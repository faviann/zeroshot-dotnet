using System.Net;
using System.Text.Json;
using Zeroshot;
using Zeroshot.Native;
using Zeroshot.Native.Contracts;

namespace Zeroshot.Sdk.Tests;

public sealed class HistoryObservationTests
{
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");
    private static readonly NativeBinding Supported = NativeBinding.CallerSupplied(NativeSchemas.NativeVersion, NativeSchemas.SourceRevision);
    private static readonly RunId RunOne = new("run-1");

    private static string Log(string id, string cursor, string run = "run-1", string message = "text", string? execution = null)
        => $$$"""{"subscriptionId":"{{{id}}}","runId":"{{{run}}}","cursor":"{{{cursor}}}","timestamp":1234567,{{{(execution is null ? "" : $"\"execution\":\"{execution}\",")}}}"record":{"level":"warn","target":"environment.setup","message":"{{{message}}}"}}""";
    private static string Watch(string id, string cursor, string repository = "acme/project")
        => $$$"""{"subscriptionId":"{{{id}}}","runId":"run-1","title":"test","source":{{{OecpPeer.Source.Replace("acme/project", repository)}}},"size":"small","cursor":"{{{cursor}}}","status":{"phase":"admitted"}}""";
    private static string Closed(string id, string reason, string? cursor = null)
        => $$"""{"subscriptionId":"{{id}}","reason":"{{reason}}"{{(cursor is null ? "" : $",\"lastDeliveredCursor\":\"{cursor}\"")}}}""";

    /// <summary>A loopback direct target whose run/watch and run/logs subscriptions follow a per-request script.</summary>
    private sealed class Target : IAsyncDisposable
    {
        private readonly CancellationTokenSource stop = new(TimeSpan.FromSeconds(60));
        private readonly HttpClient http = new(new HttpPeer(Fixtures, "{}"));
        private readonly List<(int Connection, string? From)> requests = [];
        public OecpPeer Peer { get; } = new(Fixtures);
        public NativeClient Native { get; }
        public ZeroshotClient Sdk { get; }
        public Run Run { get; }
        public Uri Origin { get; }
        public CancellationToken Stopping => stop.Token;
        /// <summary>Each subscription request's connection number and requested position, in order.</summary>
        public IReadOnlyList<(int Connection, string? From)> Requests { get { lock (requests) return requests.ToList(); } }

        public Target(string method, Func<OecpPeer.Request, int, Task>[] calls, ObservationOptions? observation = null,
            TransportOptions? transport = null)
        {
            Peer.Script = async request =>
            {
                if (request.Method != method) return false;
                int call;
                lock (requests)
                {
                    requests.Add((request.Connection, request.Params?["fromCursor"]?.GetValue<string>()));
                    call = requests.Count - 1;
                }
                await calls[call](request, call);
                return true;
            };
            var listener = OecpPeer.LoopbackListener();
            _ = Peer.ListenWebSocketsAsync(listener, stop.Token);
            Origin = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");
            // Two connections: this client's control slot and one observation, so a leaked observation is detectable.
            Native = NativeClient.ForHttp(new NativeClientOptions { Origin = Origin, Transport = (transport ?? new()) with { MaxOecpConnections = 2 } }, http);
            Sdk = new ZeroshotClient(Native, Supported, observation: observation ?? new() { ReopenDelay = TimeSpan.Zero });
            Run = Sdk.GetRun(RunOne);
        }

        public HistoryCheckpoint Checkpoint(HistoryStream stream, string cursor, ExecutionRef? execution = null)
            => new(Origin, RunOne, stream, execution, new Cursor(cursor));

        /// <summary>The observation's connection slot is free again once another SDK client fits the limit of two.</summary>
        public void CheckReleased() { using (new ZeroshotClient(Native, Supported)) { } }

        public async ValueTask DisposeAsync()
        {
            await Sdk.DisposeAsync();
            await Native.DisposeAsync();
            stop.Cancel();
            http.Dispose();
        }
    }

    private static Func<OecpPeer.Request, int, Task> Open(string id, string[] events, string? closed = null, bool drop = false)
        => async (request, _) =>
        {
            var from = request.Params?["fromCursor"]?.ToJsonString() ?? "\"start\"";
            await request.Reply($$"""{"subscriptionId":"{{id}}","runId":"run-1","atCursor":{{from}}}""");
            foreach (var record in events) await request.Notify("event", record);
            if (closed is not null) await request.Notify("subscription/closed", closed);
            if (drop) throw new IOException("Scripted connection loss.");
        };

    private static async Task<(List<HistoryRecord<T>> Records, RunObservationException? Failure)> Collect<T>(IAsyncEnumerable<HistoryRecord<T>> stream)
    {
        var records = new List<HistoryRecord<T>>();
        try { await foreach (var record in stream) records.Add(record); }
        catch (RunObservationException failure) { return (records, failure); }
        return (records, null);
    }

    [Test]
    public async Task WatchRecoversRepeatedInterruptionsExactlyAfterTheLastDeliveredRecord()
    {
        await using var target = new Target("run/watch",
        [
            // Established, two records, then the socket drops without a close.
            Open("w1", [Watch("w1", "c1"), Watch("w1", "c2")], drop: true),
            // A remote slow-consumer close reporting a server position the caller never received.
            Open("w2", [Watch("w2", "c3")], Closed("w2", "SLOW_CONSUMER", "server-ahead")),
            Open("w3", [Watch("w3", "c4")], Closed("w3", "done", "c4")),
        ]);

        var (records, failure) = await Collect(target.Run.WatchAsync(target.Checkpoint(HistoryStream.Watch, "c0")));

        Check(failure is null, "Eligible interruptions recover.");
        Check(records.Select(r => r.Event.Cursor.Value).SequenceEqual(["c1", "c2", "c3", "c4"]), "Every record exactly once, in order.");
        Check(records.All(r => r.Checkpoint == target.Checkpoint(HistoryStream.Watch, r.Event.Cursor.Value)), "Each record carries its scoped checkpoint.");
        Check(records[2].Event.SubscriptionId.Value == "w2" && records[0].Event.Source.Repository.Value == "acme/project", "Complete native identity and data.");
        var requests = target.Requests;
        Check(requests.Select(r => r.From).SequenceEqual(["c0", "c2", "c3"]), "Reopens resume after the last delivered cursor, never the server's.");
        Check(requests[1].Connection != requests[0].Connection,
            "A dropped connection is replaced.");
        Check(!target.Peer.Methods.Contains("run/force"), "Observation never stops the run.");
        target.CheckReleased();
    }

    [Test]
    public async Task ReopenedWatchStillRejectsAForeignSource()
    {
        await using var target = new Target("run/watch",
        [
            Open("w1", [Watch("w1", "c1")], drop: true),
            Open("w2", [Watch("w2", "c2", repository: "foreign/repo")]),
        ]);
        var (records, failure) = await Collect(target.Run.WatchAsync());
        Check(records.Count == 1 && failure is { Kind: RunObservationFailureKind.Protocol, Recoveries: 1 } &&
            failure.ResumeAfter!.Cursor.Value == "c1", "The source learned before the reopen still binds.");
        Check(target.Requests.Count == 2, "Foreign data is not recovered.");
    }

    [Test]
    public async Task UnrecoveredFailuresSurfaceWithTheLastDeliveredCheckpoint()
    {
        var oversized = new string('x', 2048);
        var cases = new (string Name, Func<OecpPeer.Request, int, Task> Script, ObservationOptions Options, TransportOptions Transport,
            RunObservationFailureKind Kind, NativeSubscriptionFailureKind Native)[]
        {
            ("opt-out disconnect", Open("l", [Log("l", "c1")], drop: true), new() { Recover = false }, new(),
                RunObservationFailureKind.Interrupted, NativeSubscriptionFailureKind.UnexpectedDisconnect),
            ("opt-out slow consumer", Open("l", [Log("l", "c1")], Closed("l", "SLOW_CONSUMER", "c9")), new() { Recover = false }, new(),
                RunObservationFailureKind.Interrupted, NativeSubscriptionFailureKind.SlowConsumer),
            ("history loss", Open("l", [Log("l", "c1")], Closed("l", "SOURCE_UNAVAILABLE", "c1")), new(), new(),
                RunObservationFailureKind.SourceUnavailable, NativeSubscriptionFailureKind.SourceUnavailable),
            ("foreign run", Open("l", [Log("l", "c1"), Log("l", "c2", run: "run-2")]), new(), new(),
                RunObservationFailureKind.Protocol, NativeSubscriptionFailureKind.Protocol),
            // c2 is received (and would be the queue's received position) but exceeds the stream's byte budget.
            ("local overflow", Open("l", [Log("l", "c1"), Log("l", "c2", message: oversized)]), new(), new() { MaxQueuedObservationBytes = 1024 },
                RunObservationFailureKind.ResourceLimit, NativeSubscriptionFailureKind.StreamByteLimit),
        };
        foreach (var (name, script, options, transport, kind, native) in cases)
        {
            await using var target = new Target("run/logs", [script], options with { ReopenDelay = TimeSpan.Zero }, transport);
            var (records, failure) = await Collect(target.Run.LogsAsync());
            Check(records.Count == 1 && records[0].Event.Cursor.Value == "c1", name + ": validated records drain first.");
            Check(failure is { } f && f.Kind == kind && f.InnerException is NativeSubscriptionException inner && inner.Kind == native, name + ": explicit failure.");
            Check(failure!.ResumeAfter == records[0].Checkpoint && failure.Recoveries == 0 && failure.RunId == RunOne && failure.Stream == HistoryStream.Logs,
                name + ": resume position is the last delivered record.");
            Check(!failure.Message.Contains("c1") && target.Requests.Count == 1, name + ": no silent reopen, no cursor in the message.");
            Check((await target.Run.StatusAsync()).RunId == RunOne, name + ": control stays usable.");
            target.CheckReleased();
        }
    }

    [Test]
    public async Task InitialAndReopenedEstablishmentFailuresSurface()
    {
        Task Refuse(OecpPeer.Request request, int _) => request.Reply("{\"subscriptionId\":\"l\",\"runId\":\"run-2\",\"atCursor\":\"start\"}");

        await using (var initial = new Target("run/logs", [Refuse]))
        {
            var (records, failure) = await Collect(initial.Run.LogsAsync());
            Check(records.Count == 0 && failure is { Kind: RunObservationFailureKind.Establishment, Recoveries: 0, ResumeAfter: null } &&
                failure.InnerException is NativeOecpException, "Initial establishment failure is not retried.");
            Check(initial.Requests.Count == 1, "One establishment attempt.");
            initial.CheckReleased();
        }

        await using (var reopened = new Target("run/logs", [Open("l", [Log("l", "c1")], drop: true), Refuse]))
        {
            var (records, failure) = await Collect(reopened.Run.LogsAsync());
            Check(records.Count == 1 && failure is { Kind: RunObservationFailureKind.Establishment, Recoveries: 1 } &&
                failure.ResumeAfter == records[0].Checkpoint, "Reopened establishment failure surfaces with the delivered position.");
            Check(reopened.Requests.Select(r => r.From).SequenceEqual([null, "c1"]), "One reopen from the delivered cursor.");
            reopened.CheckReleased();
        }

        // The setup budget covers establishment; a subscription that is never acknowledged fails setup.
        Target? silent = null;
        silent = new Target("run/logs", [async (_, _) => await Task.Delay(Timeout.Infinite, silent!.Stopping)],
            new() { SetupTimeout = TimeSpan.FromSeconds(1) });
        await using (silent)
        {
            // A status read first warms discovery, session and connect, so the short budget measures the subscription.
            await silent.Run.StatusAsync();
            var start = silent.Checkpoint(HistoryStream.Logs, "c0");
            var (_, failure) = await Collect(silent.Run.LogsAsync(null, start)).WaitAsync(TimeSpan.FromSeconds(10));
            Check(failure is { Kind: RunObservationFailureKind.Establishment, InnerException: TimeoutException } && failure.ResumeAfter == start,
                "The setup budget bounds establishment.");
            Check(silent.Requests.Count == 1, "The deadline was reached while the subscription was pending.");
            silent.CheckReleased();
        }
    }

    [Test]
    public async Task AnAcknowledgementAfterTheSetupDeadlineFailsSetupAndReleasesTheObservation()
    {
        // The peer holds the subscription request until the client has already failed setup, then acknowledges it.
        var setupFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var target = new Target("run/logs", [async (request, call) =>
        {
            await setupFailed.Task;
            try { await Open("late", [Log("late", "c1")])(request, call); }
            finally { acknowledged.TrySetResult(); }
        }], new() { SetupTimeout = TimeSpan.FromSeconds(1) });
        await target.Run.StatusAsync(); // warms discovery, session and connect, as above

        var (records, failure) = await Collect(target.Run.LogsAsync()).WaitAsync(TimeSpan.FromSeconds(10));
        setupFailed.TrySetResult();
        Check(records.Count == 0 && failure is { Kind: RunObservationFailureKind.Establishment, InnerException: TimeoutException, Recoveries: 0 },
            "A late acknowledgement is a setup failure, never a delivered stream.");
        await acknowledged.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Check(target.Requests.Count == 1, "Setup failure is not retried.");
        target.CheckReleased();
        Check((await target.Run.StatusAsync()).RunId == RunOne, "Control stays usable.");
    }

    [Test]
    public async Task ReopenDelayIsCancellableWhileControlStaysUsable()
    {
        await using var target = new Target("run/logs", [Open("l", [Log("l", "c1")], drop: true)],
            new() { ReopenDelay = TimeSpan.FromHours(1) });
        using var cancel = new CancellationTokenSource();
        await using var reader = target.Run.LogsAsync(cancel.Token).GetAsyncEnumerator();
        Check(await reader.MoveNextAsync() && reader.Current.Event.Cursor.Value == "c1", "Delivered before the interruption.");
        var next = reader.MoveNextAsync().AsTask();
        Check((await target.Run.StatusAsync()).RunId == RunOne && !next.IsCompleted, "Status works while the observer waits to reopen.");
        cancel.Cancel();
        try { await next.WaitAsync(TimeSpan.FromSeconds(5)); throw new InvalidOperationException("Expected cancellation."); }
        catch (OperationCanceledException) { }
        Check(target.Requests.Count == 1, "Cancelled before reopening.");
        target.CheckReleased();
    }

    [Test]
    public async Task EndingEnumerationDetachesAndReleasesTheObservation()
    {
        await using var target = new Target("run/logs", [Open("held", [Log("held", "c1", execution: "worker:1")])]);
        await foreach (var record in target.Run.LogsAsync(new ExecutionRef("worker:1"), null))
        {
            Check(record.Checkpoint.Execution!.Value == "worker:1", "The execution filter is part of the checkpoint.");
            break;
        }
        for (var i = 0; i < 100 && !target.Peer.SawSubscriptionCancel("held"); i++) await Task.Delay(20);
        Check(target.Peer.SawSubscriptionCancel("held"), "Leaving the loop cancels the native subscription.");
        target.CheckReleased();
    }

    [Test]
    public async Task CheckpointsAreScopedAndRoundTripExactly()
    {
        await using var target = new Target("run/logs", []);
        var logs = target.Checkpoint(HistoryStream.Logs, "opaque /? cursor", new ExecutionRef("worker:1"));
        var json = logs.ToJson();
        Check(json == $$"""{"schema":"zeroshot-dotnet/history-checkpoint/v1","target":"{{target.Origin.AbsoluteUri}}","runId":"run-1","stream":"logs","execution":"worker:1","cursor":"opaque /? cursor"}""", json);
        Check(HistoryCheckpoint.Parse(json) == logs && !logs.ToString().Contains("opaque"), "Exact round trip; the cursor stays out of formatting.");
        var watch = target.Checkpoint(HistoryStream.Watch, "c");
        Check(HistoryCheckpoint.Parse(watch.ToJson()) == watch, "Run-wide round trip.");
        foreach (var bad in new[] { json.Replace("/v1", "/v2"), json.Replace("\"logs\"", "\"attach\""), json.Replace("\"cursor\"", "\"extra\":1,\"cursor\""),
            json.Replace(",\"execution\":\"worker:1\"", ""), watch.ToJson().Replace("\"execution\":null", "\"execution\":\"worker:1\"") })
        {
            try { HistoryCheckpoint.Parse(bad); throw new InvalidOperationException("Accepted " + bad); }
            catch (JsonException) { }
        }

        var foreign = new[]
        {
            new HistoryCheckpoint(new Uri("https://other.example/"), RunOne, HistoryStream.Logs, new ExecutionRef("worker:1"), new Cursor("c")),
            new HistoryCheckpoint(target.Origin, new RunId("run-2"), HistoryStream.Logs, new ExecutionRef("worker:1"), new Cursor("c")),
            target.Checkpoint(HistoryStream.Watch, "c"),
            target.Checkpoint(HistoryStream.Logs, "c"),
            target.Checkpoint(HistoryStream.Logs, "c", new ExecutionRef("worker:2")),
        };
        foreach (var (checkpoint, scope) in foreign.Zip(new[] { "target", "run", "stream kind", "execution filter", "execution filter" }))
        {
            try { target.Run.LogsAsync(new ExecutionRef("worker:1"), checkpoint); throw new InvalidOperationException("Accepted " + scope); }
            catch (ArgumentException error) { Check(error.Message.Contains(scope), scope); }
        }
        try { target.Run.WatchAsync(logs); throw new InvalidOperationException("Accepted a log checkpoint for watch."); }
        catch (ArgumentException) { }
        Check(target.Peer.Methods.Count == 0, "Scope is checked before any I/O.");
    }
}
