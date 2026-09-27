using System.Text.Json;
using Zeroshot.Native;
using Zeroshot.Native.Contracts;

if (args.Length != 2) throw new ArgumentException("Supply the controlled target origin and witness directory.");
var directory = args[1];
TargetRunRequest Request(string name) => NativeJson.DeserializeUtf8<TargetRunRequest>(File.ReadAllBytes(Path.Combine(directory, name)));
var request = Request("recovery-request.json");
var attached = Request("attachment-request.json");
var forced = Request("force-request.json");
var source = request.Submission.Source;
// Fresh values for the successor's declared connection; the retained submission carries none.
var credentials = new TargetRunCredentials { Connections = request.Connections };
RunId first = new("0195af77-2200-7000-8000-000000000004"), second = new("0195af77-2200-7000-8000-000000000005"),
    unused = new("0195af77-2200-7000-8000-000000000006"), unknown = new("0195af77-2200-7000-8000-0000000000ff");
using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(180));
var token = budget.Token;
await using var native = NativeClient.ForHttp(new() { Origin = new Uri(args[0]) });
var discovery = await native.Target.DiscoverAsync(cancellationToken: token);
Check(discovery.Extensions is { WorkspaceRecovery: not null, WorkspaceCheckpoints: not null }, "direct target advertises recovery and checkpoints");
var session = await native.Target.CreateOecpSessionAsync(discovery, cancellationToken: token);
await using var connection = await native.ConnectOecpAsync(session, token);
await connection.InitializeAsync(cancellationToken: token);

// The controlled worker fails after checkout; native retains that workspace for recovery.
var accepted = await native.Target.SubmitAttemptAsync(request, cancellationToken: token);
Check(accepted.Outcome == NativeAttemptOutcome.Acknowledged && accepted.AcknowledgedRunId == request.RunId, "admission identity");
var failed = await Recoverable(request.RunId);
var sourceCheckpoints = await AllCheckpoints(request.RunId);

// Refusals reachable on a stock direct target. INVALID_PHASE needs a target without the capability.
var succeeded = await ResumeRefusal(attached.RunId, "INTERNAL_ERROR", NativeAttemptOutcome.Unknown);
var forceStopped = await ResumeRefusal(forced.RunId, "INTERNAL_ERROR", NativeAttemptOutcome.Unknown);
var unknownResume = await ResumeRefusal(unknown, "NOT_FOUND", NativeAttemptOutcome.Rejected);
var nonCanonical = await connection.Runs.ResumeAsync(request.RunId, new("not-a-uuid-v7"), credentials: credentials, cancellationToken: token);
Check(nonCanonical.Outcome == NativeAttemptOutcome.Rejected && Code(nonCanonical.Failure) == "SCHEMA_VIOLATION", "noncanonical successor");
var unknownDiscard = await connection.Runs.DiscardWorkspaceAsync(unknown, cancellationToken: token);
Check(unknownDiscard.Outcome == NativeAttemptOutcome.Rejected && Code(unknownDiscard.Failure) == "NOT_FOUND", "unknown discard");
var nothingRetained = await connection.Runs.DiscardWorkspaceAsync(forced.RunId, cancellationToken: token);
Check(nothingRetained is { Outcome: NativeAttemptOutcome.Acknowledged, Response.Discarded: false }, "force_stopped workspace was not retained");
string? checkpointRefusal = null;
try { await connection.Runs.CheckpointsAsync(new() { RunId = new("not-a-uuid-v7") }, cancellationToken: token); }
catch (NativeOecpException error) { checkpointRefusal = error.RpcError?.Data?.Code; }
Check(checkpointRefusal == "SCHEMA_VIOLATION", "noncanonical checkpoint run");

// Latest-workspace restart with the selection omitted.
var resumed = await connection.Runs.ResumeAsync(request.RunId, first, credentials: credentials, cancellationToken: token);
Check(resumed is { Outcome: NativeAttemptOutcome.Acknowledged, Response: { } r } && r.RunId == first && r.ResumedFrom == request.RunId, "first successor admitted");
var firstFailed = await Recoverable(first);
Check(firstFailed.WorkspaceRecovery.Value.ResumedFrom == request.RunId, "successor records its source");
var linked = await connection.Runs.StatusAsync(request.RunId, source, cancellationToken: token);
Check(linked.WorkspaceRecovery.HasValue && linked.WorkspaceRecovery.Value is { Recoverable: false } recovery && recovery.SuccessorRunId == first, "source records its successor");

// Native does not deduplicate resume: a repeat is refused without a typed code and admits nothing.
var repeated = await connection.Runs.ResumeAsync(request.RunId, unused, credentials: credentials, cancellationToken: token);
Check(repeated.Outcome == NativeAttemptOutcome.Unknown && Code(repeated.Failure) == "INTERNAL_ERROR", "already resumed source");
string? absent = null;
try { await connection.Runs.StatusAsync(unused, cancellationToken: token); }
catch (NativeOecpException error) { absent = error.RpcError?.Data?.Code; }
Check(absent == "NOT_FOUND", "repeated resume admitted no successor");

// The successor's worker entry checkpoint restores that node with its settled prerequisites.
var firstCheckpoints = await AllCheckpoints(first);
Check(firstCheckpoints is [.., { Node.Value: "worker" }], "successor lists its worker entry checkpoint");
RunResumeFrom selection = new CheckpointResumeFrom { CheckpointId = firstCheckpoints[^1].CheckpointId };
var resumedAgain = await connection.Runs.ResumeAsync(first, second, selection, credentials, cancellationToken: token);
Check(resumedAgain is { Outcome: NativeAttemptOutcome.Acknowledged, Response: { } s } && s.RunId == second && s.ResumedFrom == first, "second successor admitted");
var secondFailed = await Recoverable(second);

var discarded = await connection.Runs.DiscardWorkspaceAsync(second, cancellationToken: token);
Check(discarded is { Outcome: NativeAttemptOutcome.Acknowledged, Response.Discarded: true }, "retained workspace discarded");
var discardedAgain = await connection.Runs.DiscardWorkspaceAsync(second, cancellationToken: token);
Check(discardedAgain is { Outcome: NativeAttemptOutcome.Acknowledged, Response.Discarded: false }, "repeated discard finds nothing");
var afterDiscard = await connection.Runs.StatusAsync(second, source, cancellationToken: token);
Check(afterDiscard is { Status: FinishedRunStatus, WorkspaceRecovery.HasValue: true } && !afterDiscard.WorkspaceRecovery.Value.Recoverable, "run and history identity remain");
var discardedResume = await ResumeRefusal(second, "INTERNAL_ERROR", NativeAttemptOutcome.Unknown);

Console.WriteLine(JsonSerializer.Serialize(new
{
    source = new { failed = Wire(failed), checkpoints = sourceCheckpoints.Select(Wire).ToArray(), afterResume = Wire(linked) },
    refusals = new
    {
        succeededSource = succeeded, forceStoppedSource = forceStopped, unknownSource = unknownResume, nonCanonicalSuccessor = Attempt(nonCanonical),
        unknownDiscard = Attempt(unknownDiscard), forceStoppedDiscard = Attempt(nothingRetained), nonCanonicalCheckpoints = checkpointRefusal,
        repeatedResume = Attempt(repeated), repeatedResumeSuccessor = absent, discardedSource = discardedResume
    },
    first = new { resume = Attempt(resumed), failed = Wire(firstFailed), checkpoints = firstCheckpoints.Select(Wire).ToArray() },
    second = new { selection = Wire(selection), resume = Attempt(resumedAgain), failed = Wire(secondFailed) },
    discard = new { first = Attempt(discarded), repeated = Attempt(discardedAgain), status = Wire(afterDiscard) }
}));

async Task<List<RunCheckpoint>> AllCheckpoints(RunId runId)
{
    // One-entry pages exercise the opaque exclusive cursor whenever more than one checkpoint exists.
    var all = new List<RunCheckpoint>();
    CheckpointId? after = null;
    do
    {
        var page = await connection.Runs.CheckpointsAsync(new() { RunId = runId, After = after, Limit = 1 }, cancellationToken: token);
        all.AddRange(page.Checkpoints);
        after = page.NextAfter;
    } while (after is not null);
    var whole = await connection.Runs.CheckpointsAsync(new() { RunId = runId }, cancellationToken: token);
    Check(whole.NextAfter is null && whole.Checkpoints.Select(c => c.CheckpointId).SequenceEqual(all.Select(c => c.CheckpointId)), "paged checkpoints match the default page");
    return all;
}

async Task<RunStatusResult> Recoverable(RunId runId)
{
    while (true)
    {
        var status = await connection.Runs.StatusAsync(runId, source, cancellationToken: token);
        if (status.Status is FinishedRunStatus finished)
        {
            // Native also retains preparation failures; only the worker's own failure proves a completed restore.
            Check(finished.TerminalResult is FailedTerminalResult { Reason.Value: "worker_failed" } && status.WorkspaceRecovery.HasValue &&
                status.WorkspaceRecovery.Value is { Recoverable: true } recovery && recovery.ConnectionRequirements.ContainsKey(new("openai")),
                "worker_failed run retains a recoverable workspace with its connection requirement: " + JsonSerializer.Serialize(Wire(status)));
            return status;
        }
        await Task.Delay(50, token);
    }
}

async Task<object> ResumeRefusal(RunId runId, string code, NativeAttemptOutcome outcome)
{
    var attempt = await connection.Runs.ResumeAsync(runId, unused, credentials: credentials, cancellationToken: token);
    Check(attempt.Outcome == outcome && Code(attempt.Failure) == code, $"resume refusal {code}");
    return Attempt(attempt);
}

static string? Code(Exception? failure) => (failure as NativeOecpException)?.RpcError?.Data?.Code;
static object Attempt<T>(NativeAttempt<T> attempt) where T : class => new
{
    outcome = attempt.Outcome.ToString(), attempt.CorrelationId,
    response = attempt.Response is null ? (JsonElement?)null : Wire(attempt.Response),
    rpcError = (attempt.Failure as NativeOecpException)?.RpcError is { } error ? Wire(error) : (JsonElement?)null
};
static JsonElement Wire<T>(T value)
{
    using var document = JsonDocument.Parse(NativeJson.SerializeUtf8(value));
    return document.RootElement.Clone();
}
static void Check(bool condition, string evidence)
{
    if (!condition) throw new InvalidOperationException("Native recovery witness failed: " + evidence);
}
