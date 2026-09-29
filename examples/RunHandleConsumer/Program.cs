using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Zeroshot;
using Zeroshot.Native;
using Zeroshot.Native.Contracts;

// Exercises the SDK run handle through the packed Zeroshot.Client package against controlled peers:
// exact reconnection, binding refusals, generic and null output, failed results, attachment cancellation,
// checkpointed watch/log observation, and ordinary and explicit submission through the same client.
// This is deterministic consumer evidence, not live-native conformance.
var fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");
using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(60));
var token = budget.Token;
void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
async Task<T> Refused<T>(Func<Task> call) where T : Exception
{
    try { await call(); }
    catch (T error) { return error; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

const string Revision = "75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa";
const string NullOutput = "0195af77-1000-7000-8000-000000000001";
const string ObjectOutput = "0195af77-1000-7000-8000-000000000002";
const string FailedRun = "0195af77-1000-7000-8000-000000000003";
string Finished(string runId, string cursor, string terminal) => $$$"""{"runId":"{{{runId}}}","title":"test","source":{{{OecpPeer.Source}}},"size":"small","atCursor":"{{{cursor}}}","status":{"phase":"finished","terminalResult":{{{terminal}}}}}""";

var oecp = new OecpPeer(fixtures);
oecp.Statuses[NullOutput] = Finished(NullOutput, "c-null", """{"status":"succeeded","output":null}""");
oecp.Statuses[ObjectOutput] = Finished(ObjectOutput, "c-object", """{"status":"succeeded","output":{"answer":42}}""");
oecp.Statuses[FailedRun] = Finished(FailedRun, "c-failed", """{"status":"failed","reason":"provider_error"}""");
var listener = OecpPeer.LoopbackListener();
_ = oecp.ListenWebSocketsAsync(listener, token);
var origin = new Uri($"http://127.0.0.1:{((System.Net.IPEndPoint)listener.LocalEndpoint).Port}/");
var peer = new HttpPeer(fixtures, "{}");
var http = new CountingHandler(peer);
using var httpClient = new HttpClient(http) { Timeout = Timeout.InfiniteTimeSpan };
await using var native = NativeClient.ForHttp(new NativeClientOptions { Origin = origin }, httpClient);
var binding = NativeBinding.CallerSupplied("10.9.0", Revision);
Check(binding.Provenance == "caller-supplied" && binding.ToString().StartsWith("caller-supplied", StringComparison.Ordinal), "Binding is labelled caller-supplied.");

// Construction, preparation and reopening perform no network I/O.
await using var sdk = new ZeroshotClient(native, binding);
var prepared = JsonNode.Parse(File.ReadAllText(Path.Combine(fixtures, "prepared.json")))!["submission"]!.AsObject();
prepared.Remove("submissionKey");
var request = RunRequest.ParseUtf8(Encoding.UTF8.GetBytes(prepared.ToJsonString()));
var first = sdk.Prepare(request);
var second = request.Prepare();
Check(first.RunId != second.RunId && first.Submission.SubmissionKey.Value.StartsWith("dotnet-", StringComparison.Ordinal), "Each preparation fixes new identity.");
var exact = PreparedSubmission.ImportUtf8(first.ExportUtf8());
Check(exact.RunId == first.RunId && exact.Submission.SubmissionKey == first.Submission.SubmissionKey, "Prepared identity survives export/import.");
var run = sdk.GetRun(new RunId(NullOutput));
var reopened = sdk.GetRun(RunReference.Parse(run.Reference.ToJson()));
Check(reopened.Id == run.Id && reopened.Reference == run.Reference, "Reference round-trips exactly.");
Check(oecp.Methods.Count == 0 && http.Calls == 0, "No network I/O before a run operation.");
Catch<ArgumentException>(() => sdk.GetRun(new RunReference(new Uri("https://other.example/"), run.Id, binding)));

// Missing and mismatched bindings refuse before dispatch; the lower client stays usable.
await using (var missing = new ZeroshotClient(new ZeroshotClientOptions { Target = origin }))
{
    var error = await Refused<NativeBindingException>(() => missing.GetRun(run.Id).StatusAsync(token));
    Check(error is { Reason: NativeBindingProblem.Missing, Declared: null }, "Missing binding reason.");
    Check(Catch<NativeBindingException>(() => _ = missing.GetRun(run.Id).Reference).Reason == NativeBindingProblem.Missing, "No reference without a binding.");
    var unsent = await Refused<SubmissionException>(() => missing.SubmitAsync(request, cancellationToken: token));
    Check(unsent is { Attempt.Outcome: NativeAttemptOutcome.NotSent, InnerException: NativeBindingException { Reason: NativeBindingProblem.Missing } },
        "No submission without a binding.");
}
await using (var older = new ZeroshotClient(new ZeroshotClientOptions { Target = origin, NativeBinding = NativeBinding.CallerSupplied("10.8.0", Revision) }))
{
    var error = await Refused<NativeBindingException>(() => older.GetRun(run.Id).StatusAsync(token));
    Check(error is { Reason: NativeBindingProblem.Mismatched, Declared.Release: "10.8.0", Required.Release: "10.9.0" }, "Unsupported binding reason.");
}
var foreign = sdk.GetRun(new RunReference(origin, run.Id, NativeBinding.CallerSupplied("10.9.0", new string('b', 40))));
var foreignError = await Refused<NativeBindingException>(() => foreign.StatusAsync(token));
Check(foreignError.Reason == NativeBindingProblem.Mismatched && foreignError.Required == binding, "Reference binding mismatch reason.");
Catch<NativeBindingException>(() => foreign.AttachAsync(new ExecutionRef("held"), token));
Check(oecp.Methods.Count == 0 && http.Calls == 0, "Refusals dispatch nothing.");
var discovery = await native.Target.DiscoverAsync(token);
Check(discovery.Authentication == TargetAuthentication.None && http.Calls == 1, "Discovery needs no SDK binding.");

// Status: complete native data plus generic results with status-report evidence.
var nullStatus = await run.StatusAsync(token);
var nullResult = RunResult.FromStatus(nullStatus)!;
Check(nullStatus.RunId.Value == NullOutput && nullResult is { IsSuccess: true, Output.ValueKind: JsonValueKind.Null, FailureReason: null }, "Null output is success.");
Check(nullResult.Evidence is { Kind: TerminalEvidenceKind.StatusReport } && nullResult.Evidence.Cursor.Value == "c-null", "Status-report evidence.");
nullResult.EnsureSuccess();
Check((await reopened.StatusAsync(token)).RunId == run.Id, "The reopened reference reads the same run.");
var objectResult = RunResult.FromStatus(await sdk.GetRun(new RunId(ObjectOutput)).StatusAsync(token))!;
Check(objectResult.IsSuccess && objectResult.Output!.Value.GetProperty("answer").GetInt32() == 42, "Generic JSON output.");
var failed = RunResult.FromStatus(await sdk.GetRun(new RunId(FailedRun)).StatusAsync(token))!;
Check(failed is { IsSuccess: false, Output: null } && failed.FailureReason!.Value == "provider_error", "Failed run is result data.");
var raised = Catch<RunFailedException>(failed.EnsureSuccess);
Check(oecp.Methods.Count(m => m == "initialize") == 1 && oecp.Methods.Count(m => m == "run/status") == 4, "One shared control connection.");

// Attachment: each enumeration is its own live view; cancelling detaches without stop or replay.
using (var stop = CancellationTokenSource.CreateLinkedTokenSource(token))
{
    try
    {
        await foreach (var record in run.AttachAsync(new ExecutionRef("held"), stop.Token))
        {
            Check(record.Event is WorkingAgentAttachEvent && record.RunId == run.Id, "Live attachment event.");
            stop.Cancel();
        }
        throw new InvalidOperationException("A held attachment ended by itself.");
    }
    catch (OperationCanceledException) when (stop.IsCancellationRequested && !token.IsCancellationRequested) { }
}
while (!oecp.SawSubscriptionCancel("held")) await Task.Delay(10, token);
var events = new List<RunAttachEventNotification>();
await foreach (var record in sdk.GetRun(new RunId("run-1")).AttachAsync(new ExecutionRef("worker:1"), token)) events.Add(record);
Check(events is [{ Event: WorkingAgentAttachEvent }, _, { Event: SettledAgentAttachEvent }], "A new enumeration is a new live attachment.");
Check(oecp.Methods.Count(m => m == "run/attach") == 2 && !oecp.Methods.Contains("run/force"), "No reopen, replay or stop.");

// Durable watch/logs: complete native records with scoped checkpoints the caller retains and resumes from.
var history = sdk.GetRun(new RunId("run-1"));
var watched = new List<HistoryRecord<RunWatchEventNotification>>();
await foreach (var record in history.WatchAsync(token)) watched.Add(record);
Check(watched is [{ Event.Status: FinishedRunStatus, Checkpoint.Stream: HistoryStream.Watch }] && watched[0].Checkpoint.Cursor == watched[0].Event.Cursor, "Watch record and checkpoint.");
var retained = HistoryCheckpoint.Parse(watched[0].Checkpoint.ToJson());
await foreach (var _ in history.WatchAsync(retained, token)) { }
var logged = new List<HistoryRecord<RunLogEventNotification>>();
await foreach (var record in history.LogsAsync(new ExecutionRef("worker:1"), null, token)) logged.Add(record);
Check(logged is [{ Checkpoint.Execution.Value: "worker:1" }], "Execution-filtered logs carry their filter.");
Catch<ArgumentException>(() => history.LogsAsync(null, logged[0].Checkpoint, token));
Catch<ArgumentException>(() => history.LogsAsync(new ExecutionRef("worker:1"), retained, token));
Check(oecp.Methods.Count(m => m == "run/watch") == 2 && oecp.Methods.Count(m => m == "run/logs") == 1, "Mismatched checkpoints dispatch nothing.");
Check(RunResult.FromStatus(await run.StatusAsync(token))!.IsSuccess, "Control remains usable after observation.");
Check(raised.Result == failed, "EnsureSuccess carries its result.");

// Ordinary submission: preparation happens inside the call and nothing is persisted. The peer acknowledges an
// existing run, so the handle follows that ID while both IDs and the mismatch stay available for caller policy.
var callsBeforeSubmission = http.Calls;
peer.PreparedRunId = ObjectOutput;
var submitted = await sdk.SubmitAsync(request, cancellationToken: token);
Check(submitted.Id.Value == ObjectOutput && submitted.Submission is { RunIdsMatch: false } accepted &&
    accepted.ProposedRunId != submitted.Id && accepted.AcknowledgedRunId == submitted.Id, "The handle follows the acknowledged run.");
Check(RunResult.FromStatus(await submitted.StatusAsync(token))!.IsSuccess, "The submitted handle reads its acknowledged run.");

// Explicit attempt: prepare, retain before sending, import the exact bytes and send once. The same retained request
// can be replayed later with fresh credentials; nothing is regenerated or resent automatically.
var retainedPath = Path.Combine(Path.GetTempPath(), $"zeroshot-prepared-{Guid.NewGuid():N}.json");
try
{
    File.WriteAllBytes(retainedPath, sdk.Prepare(request).ExportUtf8());
    var imported = PreparedSubmission.ImportUtf8(File.ReadAllBytes(retainedPath));
    peer.PreparedRunId = imported.RunId.Value;
    var attempt = await sdk.SubmitAttemptAsync(imported, cancellationToken: token);
    Check(attempt is { Outcome: NativeAttemptOutcome.Acknowledged, RunIdsMatch: true } &&
        attempt.Prepared.ExportUtf8().AsSpan().SequenceEqual(File.ReadAllBytes(retainedPath)), "Explicit attempt sends the retained request.");

    // Cancelled before dispatch: the explicit attempt returns NotSent; the ordinary call throws an
    // OperationCanceledException subtype carrying the same kind of attempt.
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    Check((await sdk.SubmitAttemptAsync(imported, cancellationToken: cancelled.Token)).Outcome == NativeAttemptOutcome.NotSent, "Explicit not sent.");
    var cancelledSubmit = await Refused<SubmissionCanceledException>(() => sdk.SubmitAsync(imported, cancellationToken: cancelled.Token));
    Check(cancelledSubmit.Attempt.Outcome == NativeAttemptOutcome.NotSent && ReferenceEquals(cancelledSubmit.Attempt.Prepared, imported), "Ordinary not sent.");
}
finally { File.Delete(retainedPath); }
Check(http.Calls - callsBeforeSubmission == 2, "One HTTP send per dispatched submission.");
Console.WriteLine("RunHandleConsumer passed.");

static T Catch<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T error) { return error; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

sealed class CountingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private int calls;
    public int Calls => Volatile.Read(ref calls);
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref calls);
        return base.SendAsync(request, cancellationToken);
    }
}
