using System.IO.Pipes;
using System.Net.Sockets;
using System.Text.Json;
using Zeroshot.Native;
using Zeroshot.Native.Contracts;

if (args.Length != 2) throw new ArgumentException("Supply the existing controller socket or pipe path and witness directory.");
// A Unix socket path, or on Windows the controller's \\.\pipe\ path; the run matrix is the same.
var socketPath = args[0];
var directory = args[1];
using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(60));
var token = budget.Token;
var foreignRun = new RunId("0195af77-2500-7000-8000-0000000000ff");

// Owned Unix connection: the same typed operations as WebSocket, scoped to the controller's one run.
InitializeResult initialized;
GetResult cluster;
RunListResult inventory;
RunStatusResult active;
NativeOecpException foreignStatus;
NativeAttempt<RunForceResult> foreignForce;
NativeAttempt<RunResumeResult> resume;
NativeAttempt<RunDiscardWorkspaceResult> discard;
await using (var unix = OperatingSystem.IsWindows()
    ? await OecpConnection.ConnectNamedPipeAsync(socketPath, cancellationToken: token)
    : await OecpConnection.ConnectUnixAsync(socketPath, cancellationToken: token))
{
    initialized = await unix.InitializeAsync(cancellationToken: token);
    cluster = await unix.Cluster.GetAsync(cancellationToken: token);
    Check(cluster.Status is { Phase: Phase.Empty, CurrentRunId: null }, "native empty cluster get");
    inventory = await unix.Runs.ListAsync(cancellationToken: token);
    Check(inventory.Runs.Length == 1, "controller lists only its owned run");
    var runId = inventory.Runs[0].RunId;
    // The controlled provider holds the worker at its gate until this consumer releases it.
    while (true)
    {
        active = await unix.Runs.StatusAsync(runId, cancellationToken: token);
        if (active.Status is RunningRunStatus { ActiveExecutions.Length: > 0 } && File.Exists(Path.Combine(directory, "controller-ready"))) break;
        Check(active.Status is not FinishedRunStatus, "controlled worker remains active");
        await Task.Delay(50, token);
    }
    foreignStatus = await Throws<NativeOecpException>(unix.Runs.StatusAsync(foreignRun, cancellationToken: token));
    Check(foreignStatus.RpcError?.Data?.Code == "NOT_FOUND", "foreign run status is outside the controller's scope");
    foreignForce = await unix.Runs.ForceAsync(foreignRun, cancellationToken: token);
    Check(foreignForce is { Outcome: NativeAttemptOutcome.Rejected, Failure: NativeOecpException { RpcError.Data.Code: "NOT_FOUND" } } &&
        foreignForce.Origin is { Scheme: "file", Host: "" } origin && Uri.UnescapeDataString(origin.AbsolutePath) == (OperatingSystem.IsWindows() ? "/" : "") + socketPath,
        "foreign force is a rejected attempt identified by the socket");
    // The portable controller has no recovery override, so both requests are refused before any effect.
    resume = await unix.Runs.ResumeAsync(runId, new("0195af77-2500-7000-8000-0000000000fe"), cancellationToken: token);
    discard = await unix.Runs.DiscardWorkspaceAsync(runId, cancellationToken: token);
    Check(resume is { Outcome: NativeAttemptOutcome.Rejected, Failure: NativeOecpException { RpcError.Data.Code: "INVALID_PHASE" } } &&
        discard is { Outcome: NativeAttemptOutcome.Rejected, Failure: NativeOecpException { RpcError.Data.Code: "INVALID_PHASE" } },
        "controller resume and discard are INVALID_PHASE refusals");
    // Native NDJSON has no $/cancelRequest interception, so the client refuses to send it.
    await Throws<NotSupportedException>(unix.CancelRequestAsync(new RequestId(1), token));
}

// Caller-owned socket or pipe stream: borrowed by default, so it outlives the first connection.
await using var stream = await ConnectStream(socketPath, token);
await using (var first = await OecpConnection.FromStreamsAsync(stream, stream, cancellationToken: token))
{
    await first.InitializeAsync(cancellationToken: token);
    active = await first.Runs.StatusAsync(active.RunId, active.Source, cancellationToken: token);
}
Check(stream is NamedPipeClientStream { IsConnected: true } || stream is NetworkStream { Socket.Connected: true },
    "borrowed stream remains open after connection disposal");
await using var second = await OecpConnection.FromStreamsAsync(stream, stream, cancellationToken: token);
var current = await second.Runs.StatusAsync(active.RunId, active.Source, cancellationToken: token);
Check(current.Status is RunningRunStatus, "borrowed stream reused by a new connection");
await using var watch = await second.Runs.WatchAsync(new() { RunId = active.RunId, FromCursor = current.AtCursor }, active.Source, token);
await using var logs = await second.Runs.LogsAsync(new() { RunId = active.RunId }, token);
File.WriteAllText(Path.Combine(directory, "controller-release"), "release");
var watched = Drain(watch);
var logged = Drain(logs);
var watchResult = await watched;
var logResult = await logged;
// The stock controller stops serving at terminal state, so either an authoritative close
// or an observed disconnect ends each stream; the client never invents a close body.
foreach (var (completion, failure) in new[] { (await watch.Completion, watchResult.Failure), (await logs.Completion, logResult.Failure) })
    Check(completion.Origin == NativeSubscriptionOrigin.ServerClosed ||
        (completion is { Origin: NativeSubscriptionOrigin.UnexpectedDisconnect, ServerClose: null } &&
         failure is { Kind: NativeSubscriptionFailureKind.UnexpectedDisconnect }), "stream ended by server close or observed disconnect");
Check(logResult.Records.Count > 0, "retained log replay");
var connectionEnd = await second.Completion.WaitAsync(token);
Check(connectionEnd is { Kind: NativeOecpFailureKind.Transport }, "controller exit is a transport disconnect, never completion");

Console.WriteLine(JsonSerializer.Serialize(new
{
    initialize = Wire(initialized),
    cluster = Wire(cluster),
    inventory = Wire(inventory),
    active = Wire(active),
    foreignStatus = Wire(foreignStatus.RpcError!),
    foreignForce = new { outcome = foreignForce.Outcome.ToString(), origin = foreignForce.Origin?.ToString(), rpcError = Wire(((NativeOecpException)foreignForce.Failure!).RpcError!) },
    resume = new { outcome = resume.Outcome.ToString(), rpcError = Wire(((NativeOecpException)resume.Failure!).RpcError!) },
    discard = new { outcome = discard.Outcome.ToString(), rpcError = Wire(((NativeOecpException)discard.Failure!).RpcError!) },
    borrowed = new { reusedStatus = Wire(current) },
    watch = new { establishment = Wire(watch.Establishment), records = watchResult.Records.Select(Wire).ToArray(), completion = Completion(await watch.Completion), watch.LastDeliveredCursor },
    logs = new { establishment = Wire(logs.Establishment), records = logResult.Records.Select(Wire).ToArray(), completion = Completion(await logs.Completion), logs.LastDeliveredCursor },
    connectionEnd = connectionEnd!.Kind.ToString()
}));

static async Task<Stream> ConnectStream(string path, CancellationToken token)
{
    if (OperatingSystem.IsWindows())
    {
        // The borrowed pipe is the caller's; FromStreamsAsync performs no security check of its own.
        var pipe = new NamedPipeClientStream(".", path[@"\\.\pipe\".Length..], PipeDirection.InOut, PipeOptions.Asynchronous,
            System.Security.Principal.TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(token);
        return pipe;
    }
    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), token);
    return new NetworkStream(socket, ownsSocket: true);
}
static async Task<(List<T> Records, NativeSubscriptionException? Failure)> Drain<E, T>(NativeSubscription<E, T> subscription)
{
    var records = new List<T>();
    try { await foreach (var record in subscription.ReadAllAsync()) records.Add(record); }
    catch (NativeSubscriptionException failure) { return (records, failure); }
    return (records, null);
}
static object Completion(NativeSubscriptionCompletion completion) => new
{
    origin = completion.Origin.ToString(),
    serverClose = completion.ServerClose is null ? (JsonElement?)null : Wire(completion.ServerClose),
    failure = completion.Failure?.Kind.ToString()
};
static async Task<T> Throws<T>(Task task) where T : Exception
{
    try { await task; }
    catch (T error) { return error; }
    throw new InvalidOperationException($"Native controller witness failed: expected {typeof(T).Name}.");
}
static JsonElement Wire<T>(T value)
{
    using var document = JsonDocument.Parse(NativeJson.SerializeUtf8(value));
    return document.RootElement.Clone();
}
static void Check(bool condition, string evidence)
{
    if (!condition) throw new InvalidOperationException("Native controller witness failed: " + evidence);
}
