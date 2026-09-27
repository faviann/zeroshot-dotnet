using System.Text.Json;
using Zeroshot.Native;
using Zeroshot.Native.Contracts;

if (args.Length != 2) throw new ArgumentException("Supply the controlled target origin and witness directory.");
var directory = args[1];
var request = NativeJson.DeserializeUtf8<TargetRunRequest>(File.ReadAllBytes(Path.Combine(directory, "force-request.json")));
using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(60));
var token = budget.Token;
await using var native = NativeClient.ForHttp(new() { Origin = new Uri(args[0]) });
var discovery = await native.Target.DiscoverAsync(cancellationToken: token);
var session = await native.Target.CreateOecpSessionAsync(discovery, cancellationToken: token);
await using var connection = await native.ConnectOecpAsync(session, token);
await connection.InitializeAsync(cancellationToken: token);
var accepted = await native.Target.SubmitAttemptAsync(request, cancellationToken: token);
Check(accepted.Outcome == NativeAttemptOutcome.Acknowledged && accepted.AcknowledgedRunId == request.RunId, "admission identity");

// The controlled provider stays blocked at its first gate; this consumer never releases it.
RunStatusResult active;
while (true)
{
    active = await connection.Runs.StatusAsync(request.RunId, request.Submission.Source, cancellationToken: token);
    if (active.Status is RunningRunStatus { ActiveExecutions.Length: > 0 } &&
        new FileInfo(Path.Combine(directory, "attachment-provider-ready")).Length > 0) break;
    Check(active.Status is not FinishedRunStatus, "controlled worker remains active: " + JsonSerializer.Serialize(Wire(active)));
    await Task.Delay(50, token);
}

await using var watch = await connection.Runs.WatchAsync(new() { RunId = request.RunId, FromCursor = active.AtCursor },
    request.Submission.Source, token);
var force = await connection.Runs.ForceAsync(request.RunId, cancellationToken: token);
Check(force is { Outcome: NativeAttemptOutcome.Acknowledged, Failure: null, Response: not null }, "acknowledged force: " + force.Failure?.Message);
var acknowledged = force.Response!;
Check(acknowledged.RunId == request.RunId, "exact force identity");
// Stock native usually awaits cleanup before replying; the acknowledged phase is recorded, not assumed.
Check(acknowledged.Status is StoppingRunStatus or FinishedRunStatus, "force acknowledgement records stop intent");

// Terminal evidence comes from durable history and a later status query, not from the acknowledgement.
var records = new List<RunWatchEventNotification>();
await foreach (var record in watch.ReadAllAsync(token))
{
    records.Add(record);
    if (record.Status is FinishedRunStatus) break;
}
Check(records is [.., { Status: FinishedRunStatus { TerminalResult: FailedTerminalResult { Reason.Value: "force_stopped" } } }] &&
    records.SkipLast(1).Any(record => record.Status is StoppingRunStatus), "durable stopping history before force_stopped terminal");
var terminal = await connection.Runs.StatusAsync(request.RunId, request.Submission.Source, cancellationToken: token);
Check(terminal.Status is FinishedRunStatus { TerminalResult: FailedTerminalResult { Reason.Value: "force_stopped" } }, "force_stopped terminal status");

var repeated = await connection.Runs.ForceAsync(request.RunId, cancellationToken: token);
Check(repeated is { Outcome: NativeAttemptOutcome.Acknowledged } && repeated.Response!.Status is FinishedRunStatus, "idempotent terminal force");
var unknown = await connection.Runs.ForceAsync(new("0195af77-2200-7000-8000-0000000000ff"), cancellationToken: token);
Check(unknown is { Outcome: NativeAttemptOutcome.Rejected, Failure: NativeOecpException { RpcError.Data.Code: "NOT_FOUND" } }, "unknown run refusal");
Console.WriteLine(JsonSerializer.Serialize(new
{
    active = Wire(active),
    acknowledgement = new { outcome = force.Outcome.ToString(), force.CorrelationId, response = Wire(acknowledged), phase = Phase(acknowledged.Status) },
    history = records.Select(record => new { phase = Phase(record.Status), record = Wire(record) }).ToArray(),
    terminal = Wire(terminal),
    repeated = new { outcome = repeated.Outcome.ToString(), response = Wire(repeated.Response) },
    unknown = new { outcome = unknown.Outcome.ToString(), rpcError = Wire(((NativeOecpException)unknown.Failure!).RpcError!) }
}));

static string Phase(RunStatus status) => Wire(status).GetProperty("phase").GetString()!;
static JsonElement Wire<T>(T value)
{
    using var document = JsonDocument.Parse(NativeJson.SerializeUtf8(value));
    return document.RootElement.Clone();
}
static void Check(bool condition, string evidence)
{
    if (!condition) throw new InvalidOperationException("Native force witness failed: " + evidence);
}
