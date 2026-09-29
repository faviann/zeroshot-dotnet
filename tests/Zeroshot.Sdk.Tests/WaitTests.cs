using System.Net;
using System.Text;
using System.Text.Json;
using Zeroshot;
using Zeroshot.Client.Tests;
using Zeroshot.Native;
using Zeroshot.Native.Contracts;

namespace Zeroshot.Sdk.Tests;

// Run.WaitAsync and ZeroshotClient.RunAsync over a loopback direct target whose run/status and run/watch answers
// follow a per-call script. Wait budgets run on ManualTime, so no test depends on wall-clock timing.
public sealed class WaitTests
{
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");
    private static readonly NativeBinding Supported = NativeBinding.CallerSupplied("10.9.0", "75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa");
    private static readonly RunId RunOne = new("run-1");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private const string Running = """{"phase":"running","activeExecutions":[]}""";
    private static string Finished(string terminal) => $$"""{"phase":"finished","terminalResult":{{terminal}}}""";
    private const string NullOutput = """{"status":"succeeded","output":null}""";
    private const string RuntimeFailed = """{"status":"failed","reason":"runtime_failed"}""";

    private static string Status(string cursor, string status)
        => $$"""{"runId":"run-1","title":"test","source":{{OecpPeer.Source}},"size":"small","atCursor":"{{cursor}}","status":{{status}}}""";
    private static string Watch(string id, string cursor, string status = Running)
        => $$"""{"subscriptionId":"{{id}}","runId":"run-1","title":"test","source":{{OecpPeer.Source}},"size":"small","cursor":"{{cursor}}","status":{{status}}}""";
    private static string Closed(string id, string reason) => $$"""{"subscriptionId":"{{id}}","reason":"{{reason}}"}""";

    /// <summary>One scripted answer. A subscription left without a close or drop stays open.</summary>
    private delegate Task Call(OecpPeer.Request request, Target target);

    private static Call Reply(string body) => (request, _) => request.Reply(body);
    /// <summary>Never answers, signalling once the request is pending.</summary>
    private static Call Hold(TaskCompletionSource pending)
        => async (_, target) => { pending.TrySetResult(); await Task.Delay(Timeout.Infinite, target.Stopping); };
    private static Call Open(string id, string[] events, string? closed = null, bool drop = false)
        => async (request, _) =>
        {
            await request.Reply($$"""{"subscriptionId":"{{id}}","runId":"run-1","atCursor":{{request.Params?["fromCursor"]?.ToJsonString() ?? "\"start\""}}}""");
            foreach (var record in events) await request.Notify("event", record);
            if (closed is not null) await request.Notify("subscription/closed", closed);
            if (drop) throw new IOException("Scripted connection loss.");
        };

    /// <summary>A loopback direct target: HTTP acknowledges submissions as run-1; OECP status and watch follow scripts.</summary>
    private sealed class Target : IAsyncDisposable
    {
        private readonly CancellationTokenSource stop = new(TimeSpan.FromSeconds(60));
        private readonly HttpClient http;
        private readonly Dictionary<string, List<string?>> requests = new();
        public OecpPeer Peer { get; } = new(Fixtures);
        public ManualTime Time { get; } = new();
        public NativeClient Native { get; }
        public ZeroshotClient Sdk { get; }
        public Run Run { get; }
        public Uri Origin { get; }
        public Submissions Http { get; }
        public CancellationToken Stopping => stop.Token;
        /// <summary>The requested watch positions, in order.</summary>
        public IReadOnlyList<string?> Watches => Requested("run/watch");
        public IReadOnlyList<string?> StatusReads => Requested("run/status");

        public Target(Call[] status, Call[]? watch = null, ObservationOptions? observation = null)
        {
            var scripts = new Dictionary<string, Call[]> { ["run/status"] = status, ["run/watch"] = watch ?? [] };
            Peer.Script = async request =>
            {
                if (!scripts.TryGetValue(request.Method, out var calls)) return false;
                int call;
                lock (requests)
                {
                    if (!requests.TryGetValue(request.Method, out var seen)) requests[request.Method] = seen = [];
                    seen.Add(request.Params?["fromCursor"]?.GetValue<string>());
                    call = seen.Count - 1;
                }
                if (call >= calls.Length) throw new InvalidOperationException($"Unscripted {request.Method} call {call}.");
                await calls[call](request, this);
                return true;
            };
            var listener = OecpPeer.LoopbackListener();
            _ = Peer.ListenWebSocketsAsync(listener, stop.Token);
            Origin = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");
            Http = new Submissions(new HttpPeer(Fixtures, "{}") { PreparedRunId = RunOne.Value });
            http = new HttpClient(Http);
            // Two connections: this client's control slot and one observation, so a leaked observation is detectable.
            Native = NativeClient.ForHttp(new NativeClientOptions { Origin = Origin, Transport = new() { MaxOecpConnections = 2 } }, http);
            Sdk = new ZeroshotClient(Native, Supported, observation: observation ?? new() { ReopenDelay = TimeSpan.Zero }) { Time = Time };
            Run = Sdk.GetRun(RunOne);
        }

        private IReadOnlyList<string?> Requested(string method)
        { lock (requests) return requests.TryGetValue(method, out var seen) ? seen.ToList() : []; }

        public HistoryCheckpoint Checkpoint(string cursor) => new(Origin, RunOne, HistoryStream.Watch, null, new Cursor(cursor));

        /// <summary>The observation's connection slot is free again once another SDK client fits the limit of two.</summary>
        public async Task CheckReleasedAsync()
        {
            for (var i = 0; i < 100; i++)
            {
                try { using (new ZeroshotClient(Native, Supported)) { } return; }
                catch (InvalidOperationException) { await Task.Delay(20); }
            }
            throw new InvalidOperationException("The wait's observation was not released.");
        }

        public async ValueTask DisposeAsync()
        {
            await Sdk.DisposeAsync();
            await Native.DisposeAsync();
            stop.Cancel();
            http.Dispose();
        }
    }

    /// <summary>Counts submissions. Hooks run before the send and while the read acknowledgement is being disposed.</summary>
    private sealed class Submissions(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private int count;
        public int Count => Volatile.Read(ref count);
        public Action? BeforeSending { get; set; }
        public Action? AfterCapture { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath != "/native-v2/run") return await base.SendAsync(request, cancellationToken);
            Interlocked.Increment(ref count);
            BeforeSending?.Invoke();
            var response = await base.SendAsync(request, cancellationToken);
            if (AfterCapture is { } after) response.Content = new CleanupContent(await response.Content.ReadAsStringAsync(cancellationToken), after);
            return response;
        }
    }

    private sealed class CleanupContent(string body, Action cleanup) : StringContent(body, Encoding.UTF8, "application/json")
    {
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) cleanup();
        }
    }

    private static async Task<T> CatchAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T error) { return error; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static PreparedSubmission Retained() => PreparedSubmission.ImportUtf8(File.ReadAllBytes(Path.Combine(Fixtures, "prepared.json")));

    [Test]
    public async Task AnAlreadyTerminalRunCompletesFromStatusWithoutWatching()
    {
        foreach (var (terminal, success) in new[] { (NullOutput, true), (RuntimeFailed, false) })
        {
            await using var target = new Target([Reply(Status("s1", Finished(terminal)))]);
            var result = await target.Run.WaitAsync();
            Check(result.IsSuccess == success && result.RunId == RunOne, "The reported terminal result is returned.");
            Check(success ? result.Output is { ValueKind: JsonValueKind.Null } && result.FailureReason is null
                : result.Output is null && result.FailureReason!.Value == "runtime_failed", "Null output is success; failure is result data.");
            Check(result.Evidence is { Kind: TerminalEvidenceKind.StatusReport } && result.Evidence.Cursor.Value == "s1", "Status-report provenance.");
            Check(target.Watches.Count == 0 && target.StatusReads.Count == 1, "No observation beyond the status read.");
        }
    }

    [Test]
    public async Task AStatusOnlyRuntimeFailureAfterANormalWatchEndIsAResultWithStatusEvidence()
    {
        // Native's in-memory runtime_failed fallback: the watch ends "done" with no terminal event, status reports failure.
        await using var target = new Target(
            [Reply(Status("s1", Running)), Reply(Status("s1", Finished(RuntimeFailed)))],
            [Open("w1", [], Closed("w1", "done"))]);
        var result = await target.Run.WaitAsync();
        Check(result is { IsSuccess: false, Evidence.Kind: TerminalEvidenceKind.StatusReport } && result.FailureReason!.Value == "runtime_failed",
            "The final status decides, with status-only provenance.");
        Check(target.Watches.SequenceEqual(["s1"]) && target.StatusReads.Count == 2, "Watch from the status cursor, then read status once more.");
        Check(!target.Peer.Methods.Contains("run/force"), "Waiting never stops the run.");
        await target.CheckReleasedAsync();
    }

    [Test]
    public async Task ARetainedTerminalEventCompletesWithEventEvidenceAndDetaches()
    {
        await using var target = new Target([Reply(Status("s1", Running))],
            [Open("w1", [Watch("w1", "c2"), Watch("w1", "c3", Finished(NullOutput))])]); // the stream stays open
        var result = await target.Run.WaitAsync(TimeSpan.FromMinutes(5));
        Check(result is { IsSuccess: true, Output.ValueKind: JsonValueKind.Null, Evidence.Kind: TerminalEvidenceKind.RetainedTerminalEvent } &&
            result.Evidence.Cursor.Value == "c3", "The terminal event's result and cursor.");
        Check(target.StatusReads.Count == 1, "A terminal event needs no final status read.");
        for (var i = 0; i < 100 && !target.Peer.SawSubscriptionCancel("w1"); i++) await Task.Delay(20);
        Check(target.Peer.SawSubscriptionCancel("w1"), "The open watch is cancelled once the result is known.");
        await target.CheckReleasedAsync();
    }

    [Test]
    public async Task RepeatedInterruptionsRecoverFromTheLastDeliveredRecord()
    {
        await using var target = new Target([Reply(Status("s1", Running))],
        [
            Open("w1", [Watch("w1", "c2")], drop: true),
            Open("w2", [Watch("w2", "c3")], Closed("w2", "SLOW_CONSUMER")),
            Open("w3", [], drop: true),
            Open("w4", [Watch("w4", "c4", Finished(RuntimeFailed))]),
        ]);
        var result = await target.Run.WaitAsync();
        Check(result is { IsSuccess: false, Evidence.Kind: TerminalEvidenceKind.RetainedTerminalEvent } && result.Evidence.Cursor.Value == "c4",
            "The retained failure is the result.");
        Check(target.Watches.SequenceEqual(["s1", "c2", "c3", "c3"]), "Each reopen continues after the last delivered record.");
        Check(target.StatusReads.Count == 1, "Interruptions are recovered, not settled by status.");
    }

    [Test]
    public async Task EndOfStreamNeverBecomesSuccess()
    {
        // A normal end with a nonterminal final status is incomplete observation.
        await using (var target = new Target([Reply(Status("s1", Running)), Reply(Status("s9", Running))],
            [Open("w1", [Watch("w1", "c2")], Closed("w1", "done"))]))
        {
            var error = await CatchAsync<RunWaitException>(() => target.Run.WaitAsync());
            Check(error is { Kind: RunWaitFailureKind.Incomplete, InnerException: null } && ReferenceEquals(error.Run, target.Run), "Incomplete, on this run.");
            Check(error.Evidence.Status!.AtCursor.Value == "s9" && error.Evidence.LastEvent!.Cursor.Value == "c2" &&
                error.Evidence.ResumeAfter == target.Checkpoint("c2"), "Latest status, event and cursor.");
            await target.CheckReleasedAsync();
        }

        // EOF with recovery disabled surfaces the interruption; the status is not consulted.
        await using (var target = new Target([Reply(Status("s1", Running))], [Open("w1", [], drop: true)],
            new() { Recover = false, ReopenDelay = TimeSpan.Zero }))
        {
            var error = await CatchAsync<RunWaitException>(() => target.Run.WaitAsync());
            Check(error is { Kind: RunWaitFailureKind.Observation, InnerException: RunObservationException { Kind: RunObservationFailureKind.Interrupted } } &&
                error.Evidence.ResumeAfter == target.Checkpoint("s1") && error.Evidence.LastEvent is null, "Unrecovered EOF is an observation failure.");
            Check(target.StatusReads.Count == 1, "No final status after an interruption.");
        }
    }

    [Test]
    public async Task MalformedTerminalDataFailsTheWaitNotTheRun()
    {
        const string unknown = """{"status":"maybe","output":1}""";
        // A terminal event the SDK cannot validate is a protocol failure, never a result.
        await using (var target = new Target([Reply(Status("s1", Running))],
            [Open("w1", [Watch("w1", "c2"), Watch("w1", "c3", Finished(unknown))], Closed("w1", "done"))]))
        {
            var error = await CatchAsync<RunWaitException>(() => target.Run.WaitAsync());
            Check(error is { Kind: RunWaitFailureKind.Observation, InnerException: RunObservationException { Kind: RunObservationFailureKind.Protocol } } &&
                error.Evidence.ResumeAfter == target.Checkpoint("c2") && error.Evidence.Status!.AtCursor.Value == "s1",
                "The last valid record is kept; the malformed one is not.");
            Check(target.StatusReads.Count == 1, "Malformed data is not recovered or settled by status.");
        }

        // A finished status without a terminal result, first or final.
        foreach (var calls in new Call[][] { [Reply(Status("s1", """{"phase":"finished"}"""))],
            [Reply(Status("s1", Running)), Reply(Status("s2", Finished(unknown)))] })
        {
            await using var target = new Target(calls, [Open("w1", [], Closed("w1", "done"))]);
            var error = await CatchAsync<RunWaitException>(() => target.Run.WaitAsync());
            Check(error.Kind == RunWaitFailureKind.Status && error.InnerException is not null, "A malformed status is a status failure.");
            Check(calls.Length == 1 ? error.Evidence.Status is null : error.Evidence.Status!.AtCursor.Value == "s1",
                "Only validated status is kept as evidence.");
        }
    }

    [Test]
    public async Task QuietObservationWaitsIndefinitelyAndCancellationOnlyDetaches()
    {
        await using var target = new Target([Reply(Status("s1", Running))], [Open("w1", [])]);
        using var cancel = new CancellationTokenSource();
        var wait = target.Run.WaitAsync(cancellationToken: cancel.Token);
        for (var i = 0; i < 500 && target.Watches.Count == 0; i++) await Task.Delay(20);
        Check(target.Watches.SequenceEqual(["s1"]), "The watch is open.");
        target.Time.Advance(TimeSpan.FromDays(365));
        Check(!wait.IsCompleted, "A quiet run is not failed for silence, and the default wait has no budget.");

        cancel.Cancel();
        var error = await CatchAsync<RunWaitCanceledException>(() => wait.WaitAsync(TimeSpan.FromSeconds(10)));
        Check(error.CancellationToken == cancel.Token && ReferenceEquals(error.Run, target.Run), "Caller cancellation, on this run.");
        Check(error.Evidence.Status!.AtCursor.Value == "s1" && error.Evidence.ResumeAfter == target.Checkpoint("s1"), "Latest evidence is kept.");
        // The watch may still be establishing, so detaching is either a cancelled request or subscription/cancel.
        Check(!target.Peer.Methods.Contains("run/force"), "Cancellation never stops the run.");
        await target.CheckReleasedAsync();
    }

    [Test]
    public async Task OneBudgetCoversStatusWatchRecoveryAndDelays()
    {
        // Each case holds the wait at a different stage; advancing the clock past the budget ends it there.
        // In "reopen delay" the budget may expire while the drop is still being read or during the day-long delay;
        // either way the wait cannot outlive its budget.
        var cases = new (string Name, Func<TaskCompletionSource, (Call[] Status, Call[] Watch)> Script, TimeSpan ReopenDelay,
            string? Status, string? Resume, string? LastEvent)[]
        {
            ("initial status", held => ([Hold(held)], []), TimeSpan.Zero, null, null, null),
            ("watch setup", held => ([Reply(Status("s1", Running))], [Hold(held)]), TimeSpan.Zero, "s1", "s1", null),
            ("reopen setup", held => ([Reply(Status("s1", Running))], [Open("w1", [Watch("w1", "c2")], drop: true), Hold(held)]),
                TimeSpan.Zero, "s1", "c2", "c2"),
            ("reopen delay", held => ([Reply(Status("s1", Running))], [async (r, t) => { await Open("w1", [], drop: false)(r, t); held.TrySetResult(); throw new IOException("drop"); }]),
                TimeSpan.FromDays(1), "s1", "s1", null),
            ("final status", held => ([Reply(Status("s1", Running)), Hold(held)], [Open("w1", [], Closed("w1", "done"))]),
                TimeSpan.Zero, "s1", "s1", null),
        };
        foreach (var (name, script, delay, status, resume, lastEvent) in cases)
        {
            var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var (statusCalls, watchCalls) = script(held);
            await using var target = new Target(statusCalls, watchCalls, new() { ReopenDelay = delay });
            var wait = target.Run.WaitAsync(TimeSpan.FromSeconds(30));
            await held.Task.WaitAsync(TimeSpan.FromSeconds(10));
            target.Time.Advance(TimeSpan.FromSeconds(29));
            Check(!wait.IsCompleted, name + ": within budget.");
            target.Time.Advance(TimeSpan.FromSeconds(1));
            var error = await CatchAsync<RunWaitTimeoutException>(() => wait.WaitAsync(TimeSpan.FromSeconds(10)));
            Check(error is { Kind: RunWaitFailureKind.Timeout } && error.Timeout == TimeSpan.FromSeconds(30) && ReferenceEquals(error.Run, target.Run),
                name + ": typed timeout on this run.");
            Check(error.Evidence.Status?.AtCursor.Value == status && error.Evidence.ResumeAfter?.Cursor.Value == resume &&
                error.Evidence.LastEvent?.Cursor.Value == lastEvent, name + ": latest validated evidence.");
            Check(!target.Peer.Methods.Contains("run/force"), name + ": timeout never stops the run.");
            await target.CheckReleasedAsync();
        }
    }

    [Test]
    public async Task AZeroWaitPerformsNoObservationAndOnlyNullIsIndefinite()
    {
        await using var target = new Target([]);
        var error = await CatchAsync<RunWaitTimeoutException>(() => target.Run.WaitAsync(TimeSpan.Zero));
        Check(error.Timeout == TimeSpan.Zero && error.Evidence is { Status: null, LastEvent: null, ResumeAfter: null }, "Timed out with the evidence available: none.");
        foreach (var invalid in new[] { TimeSpan.FromTicks(-1), Timeout.InfiniteTimeSpan, TimeSpan.MaxValue })
        {
            await CatchAsync<ArgumentOutOfRangeException>(() => target.Run.WaitAsync(invalid));
            await CatchAsync<ArgumentOutOfRangeException>(() => target.Sdk.RunAsync(Retained(), timeout: invalid));
        }
        Check(target.Peer.Methods.Count == 0 && target.Http.Count == 0, "Nothing was read or sent.");
    }

    [Test]
    public async Task RunAsyncStartsItsBudgetAfterAcknowledgement()
    {
        await using var target = new Target([Reply(Status("s1", Finished(NullOutput)))]);
        // Time spent before acknowledgement is not charged to the wait.
        target.Http.BeforeSending = () => target.Time.Advance(TimeSpan.FromHours(1));
        var result = await target.Sdk.RunAsync(Retained(), timeout: TimeSpan.FromSeconds(30));
        Check(result is { IsSuccess: true, Evidence.Kind: TerminalEvidenceKind.StatusReport } && target.Http.Count == 1, "Submit once, then wait.");
    }

    [Test]
    public async Task RunAsyncKeepsTheAcknowledgementAcrossLaterTimeoutFailureAndCancellation()
    {
        static void Acknowledged(Run run)
            => Check(run.Id == RunOne && run.Submission is { Outcome: NativeAttemptOutcome.Acknowledged } attempt &&
                attempt.AcknowledgedRunId == RunOne, "The acknowledged submission is kept.");

        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var timedOut = new Target([Hold(pending)]))
        {
            var run = timedOut.Sdk.RunAsync(Retained(), timeout: TimeSpan.FromSeconds(30));
            await pending.Task.WaitAsync(TimeSpan.FromSeconds(10));
            timedOut.Time.Advance(TimeSpan.FromSeconds(30));
            var error = await CatchAsync<RunWaitTimeoutException>(() => run.WaitAsync(TimeSpan.FromSeconds(10)));
            Acknowledged(error.Run);
        }

        await using (var failed = new Target([Reply(Status("s1", Running))], [Open("w1", [], Closed("w1", "SOURCE_UNAVAILABLE"))]))
        {
            var error = await CatchAsync<RunWaitException>(() => failed.Sdk.RunAsync(Retained()));
            Check(error is { Kind: RunWaitFailureKind.Observation, InnerException: RunObservationException { Kind: RunObservationFailureKind.SourceUnavailable } },
                "Incomplete history is an observation failure.");
            Acknowledged(error.Run);
        }

        await using (var cancelled = new Target([]))
        {
            // Cancelled as the read acknowledgement is released: the submission is kept and the wait detaches.
            using var cancel = new CancellationTokenSource();
            cancelled.Http.AfterCapture = cancel.Cancel;
            var error = await CatchAsync<RunWaitCanceledException>(() => cancelled.Sdk.RunAsync(Retained(), cancellationToken: cancel.Token));
            Acknowledged(error.Run);
            Check(error.CancellationToken == cancel.Token && cancelled.Http.Count == 1 && !cancelled.Peer.Methods.Contains("run/force"),
                "One submission, no stop.");
        }
    }
}
