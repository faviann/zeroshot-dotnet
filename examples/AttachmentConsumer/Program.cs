using System.Text.Json;
using Zeroshot.Native;
using Zeroshot.Native.Contracts;

if (args.Length != 2) throw new ArgumentException("Supply the controlled target origin and witness directory.");
var directory = args[1];
var request = NativeJson.DeserializeUtf8<TargetRunRequest>(File.ReadAllBytes(Path.Combine(directory, "attachment-request.json")));
using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(60));
var token = budget.Token;
await using var native = NativeClient.ForHttp(new() { Origin = new Uri(args[0]) });
var discovery = await native.Target.DiscoverAsync(cancellationToken: token);
var session = await native.Target.CreateOecpSessionAsync(discovery, cancellationToken: token);
await using var connection = await native.ConnectOecpAsync(session, token);
await connection.InitializeAsync(cancellationToken: token);
var accepted = await native.Target.SubmitAttemptAsync(request, cancellationToken: token);
Check(accepted.Outcome == NativeAttemptOutcome.Acknowledged && accepted.AcknowledgedRunId == request.RunId, "admission identity");

// Status supplies the exact opaque selector. The controlled provider remains
// active until this consumer explicitly releases its output and settlement gates.
RunStatusResult beforeAttach;
ExecutionRef execution;
while (true)
{
    beforeAttach = await connection.Runs.StatusAsync(request.RunId, request.Submission.Source, cancellationToken: token);
    if (beforeAttach.Status is RunningRunStatus { ActiveExecutions: [var active] } &&
        new FileInfo(Path.Combine(directory, "attachment-provider-ready")).Length > 0)
    { execution = active.Execution; break; }
    Check(beforeAttach.Status is not FinishedRunStatus, "controlled worker remains active: " + JsonSerializer.Serialize(Wire(beforeAttach)));
    await Task.Delay(50, token);
}

await using var attachment = await connection.Runs.AttachAsync(new() { RunId = request.RunId, Execution = execution }, token);
Check(attachment.Establishment.RunId == request.RunId && attachment.Establishment.Execution == execution, "exact establishment");
var events = new List<RunAttachEventNotification>();
var outputReleased = false;
var executionReleased = false;
await foreach (var record in attachment.ReadAllAsync(token))
{
    Check(record.RunId == request.RunId && record.Execution == execution, "exact event identities");
    events.Add(record);
    if (record.Event is WorkingAgentAttachEvent && !outputReleased)
    {
        await File.WriteAllTextAsync(Path.Combine(directory, "attachment-output"), "release", token);
        outputReleased = true;
    }
    if (record.Event is OutputAgentAttachEvent { Text.Value: "Codex turn started" } && !executionReleased)
    {
        Check(outputReleased, "output follows acknowledged attachment");
        await File.WriteAllTextAsync(Path.Combine(directory, "attachment-release"), "release", token);
        executionReleased = true;
    }
}
var completion = await attachment.Completion.WaitAsync(token);
Check(events.Count >= 3 && events[0].Event is WorkingAgentAttachEvent &&
    events[^1].Event is SettledAgentAttachEvent && executionReleased, "native working/live output/settled");
Check(completion is { Origin: NativeSubscriptionOrigin.ServerClosed, ServerClose.Reason: SubscriptionCloseReason.Done,
    ServerClose.LastDeliveredCursor: null } && attachment.LastDeliveredCursor is null, "cursorless native close");

// A separate status query supplies terminal run evidence. Attachment settlement
// alone deliberately makes no claim about the graph's result.
RunStatusResult afterSettlement;
do
{
    afterSettlement = await connection.Runs.StatusAsync(request.RunId, request.Submission.Source, cancellationToken: token);
    if (afterSettlement.Status is not FinishedRunStatus) await Task.Delay(50, token);
} while (afterSettlement.Status is not FinishedRunStatus);
Check(afterSettlement.Status is FinishedRunStatus { TerminalResult: SucceededTerminalResult }, "controlled graph completes successfully");
var inactive = await Refusal(connection, request.RunId, execution, "GONE", token);
var unknown = await Refusal(connection, request.RunId, new("unknown-controlled-execution"), "NOT_FOUND", token);
Console.WriteLine(JsonSerializer.Serialize(new
{
    beforeAttach = Wire(beforeAttach), establishment = Wire(attachment.Establishment),
    events = events.Select(Wire).ToArray(), close = Wire(completion.ServerClose!),
    afterSettlement = Wire(afterSettlement), inactive, unknown
}));

static async Task<object> Refusal(OecpConnection connection, RunId runId, ExecutionRef execution, string code, CancellationToken token)
{
    try
    {
        await using var unexpected = await connection.Runs.AttachAsync(new() { RunId = runId, Execution = execution }, token);
        throw new InvalidOperationException("Native unexpectedly accepted " + code + " attachment.");
    }
    catch (NativeOecpException error) when (error.Kind == NativeOecpFailureKind.RpcError)
    {
        Check(error.RpcError!.Data!.Code == code, "distinct native " + code + " refusal");
        return new { runId = runId.Value, execution = execution.Value, rpcError = Wire(error.RpcError), error.Dispatch };
    }
}
static JsonElement Wire<T>(T value)
{
    using var document = JsonDocument.Parse(NativeJson.SerializeUtf8(value));
    return document.RootElement.Clone();
}
static void Check(bool condition, string evidence)
{
    if (!condition) throw new InvalidOperationException("Native attachment witness failed: " + evidence);
}
