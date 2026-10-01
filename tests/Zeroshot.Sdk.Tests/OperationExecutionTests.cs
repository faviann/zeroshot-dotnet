using System.Text;
using TUnit.Core;
using Zeroshot.Native.Execution;

namespace Zeroshot.Client.Tests;

public sealed class OperationExecutionTests
{
    private static readonly OperationDescriptor Http = new("run.status", OperationTransport.Http);
    private static readonly OperationDescriptor Oecp = new("run/status", OperationTransport.Oecp);
    private static readonly OperationDescriptor Control = new("run.force", OperationTransport.Http, isControl: true);
    private static readonly OperationLimits SmallCapacity = new() { ConcurrentRequests = 2, ReservedControlRequests = 1 };
    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task<OperationFailure> Fails(Task task, OperationFailureKind kind, OperationStage? stage = null)
    {
        try { await task; }
        catch (OperationFailure failure)
        {
            Check(failure.Kind == kind && (stage is null || failure.Stage == stage), "Unexpected failure classification: " + failure);
            return failure;
        }
        throw new InvalidOperationException("Expected operation failure.");
    }
    private static async Task Cancelled(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Expected cancellation.");
    }
    private static Task<int> Value(OperationContext _) => Task.FromResult(42);

    [Test]
    public void DefaultsAndInvalidConfiguration()
    {
        var defaults = new OperationLimits();
        Check(defaults.ConnectTimeout == TimeSpan.FromSeconds(10) && defaults.UnaryTimeout == TimeSpan.FromSeconds(30)
            && defaults.CleanupTimeout == TimeSpan.FromSeconds(5), "Timeout defaults differ.");
        Check(defaults.ConcurrentRequests == 32 && defaults.ReservedControlRequests == 4
            && defaults.OecpConnections == 18 && defaults.HttpConnectionsPerOrigin == 8, "Capacity defaults differ.");
        Check(defaults.HttpRequestBytes == 4 * 1024 * 1024 && defaults.OecpRequestBytes == 1024 * 1024
            && defaults.ResponseBytes == 8 * 1024 * 1024 && defaults.MessageBytes == 8 * 1024 * 1024
            && defaults.DiagnosticBytes == 64 * 1024, "Byte defaults differ.");
        OperationLimits[] invalid =
        [
            defaults with { ConnectTimeout = TimeSpan.Zero }, defaults with { UnaryTimeout = Timeout.InfiniteTimeSpan },
            defaults with { CleanupTimeout = TimeSpan.MaxValue }, defaults with { ConcurrentRequests = 0 },
            defaults with { ReservedControlRequests = 0 }, defaults with { ReservedControlRequests = 32 },
            defaults with { OecpConnections = -1 }, defaults with { HttpConnectionsPerOrigin = 0 },
            defaults with { HttpRequestBytes = 0 }, defaults with { OecpRequestBytes = -1 },
            defaults with { ResponseBytes = 0 }, defaults with { MessageBytes = 0 }, defaults with { DiagnosticBytes = 0 }
        ];
        foreach (var limits in invalid) Invalid(() => new OperationExecutor(limits));
        Invalid(() => new OperationDescriptor("https://user:secret@host/path", OperationTransport.Http));
        Invalid(() => new OperationDescriptor("run.status", OperationTransport.Http, responseBytes: 0));
    }

    [Test]
    public async Task FullDefaultOrdinaryCapacityLeavesFourControlSlots()
    {
        using var executor = new OperationExecutor();
        var release = Signal<int>();
        var ordinary = Enumerable.Range(0, 28).Select(_ => executor.ExecuteAsync(Http, 0, _ => release.Task)).ToArray();
        await Fails(executor.ExecuteAsync(Http, 0, Value), OperationFailureKind.Capacity, OperationStage.Admission);
        var control = Enumerable.Range(0, 4).Select(_ => executor.ExecuteAsync(Control, 0, _ => release.Task)).ToArray();
        await Fails(executor.ExecuteAsync(Control, 0, Value), OperationFailureKind.Capacity);
        release.SetResult(7);
        await Task.WhenAll(ordinary.Concat(control));
        Check(await executor.ExecuteAsync(Http, 0, Value) == 42, "Request capacity was not returned.");
    }

    [Test]
    public async Task ControlCanUseOrdinaryCapacityToo()
    {
        using var executor = new OperationExecutor(SmallCapacity);
        var release = Signal<int>();
        var first = executor.ExecuteAsync(Control, 0, _ => release.Task);
        var second = executor.ExecuteAsync(Control, 0, _ => release.Task);
        await Fails(executor.ExecuteAsync(Http, 0, Value), OperationFailureKind.Capacity);
        release.SetResult(1);
        await Task.WhenAll(first, second);
    }

    [Test]
    public async Task CancellationBeforeAdmissionDoesNotDispatchOrReserve()
    {
        using var executor = new OperationExecutor(SmallCapacity);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var called = false;
        await Cancelled(executor.ExecuteAsync(Http, 0, _ => { called = true; return Task.FromResult(0); },
            cancellationToken: cancellation.Token));
        Check(!called, "Cancelled operation dispatched.");
        Check(await executor.ExecuteAsync(Http, 0, Value) == 42, "Cancelled admission leaked capacity.");
    }

    [Test]
    public async Task ResponseBodyUsesWhatRemainsOfTheWholeUnaryBudget()
    {
        var time = new ManualTime();
        using var executor = new OperationExecutor(time: time);
        using var stream = new BlockedBody();
        var response = executor.ExecuteAsync(Http, 0, async context =>
        {
            time.Advance(TimeSpan.FromSeconds(29)); // Headers and other admission/operation work.
            return await context.ReadResponseAsync(stream);
        });
        await stream.Started.Task;
        time.Advance(TimeSpan.FromSeconds(1));
        await Fails(response, OperationFailureKind.Deadline);
        Check(await executor.ExecuteAsync(Http, 0, Value) == 42, "Deadline leaked capacity.");
    }

    [Test]
    public async Task ConnectIsLimitedByBothConnectAndEnclosingDeadlines()
    {
        foreach (var seconds in new[] { 3, 10 })
        {
            var time = new ManualTime();
            using var executor = new OperationExecutor(time: time);
            var started = Signal<bool>();
            var request = executor.ExecuteAsync(Http, 0, async context =>
            {
                await context.ConnectAsync(async token =>
                {
                    started.SetResult(true);
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                });
                return 0;
            }, enclosingBudget: TimeSpan.FromSeconds(seconds == 3 ? 3 : 30));
            await started.Task;
            time.Advance(TimeSpan.FromSeconds(seconds));
            await Fails(request, OperationFailureKind.Deadline);
        }
    }

    [Test]
    public async Task AdmissionConsumesTheBudgetFromBeforeReservation()
    {
        // This provider advances the monotonic clock while the deadline timer is installed,
        // simulating a thread paused before admission. No real time or lock contention is needed.
        var time = new AdmissionPauseTime();
        using var executor = new OperationExecutor(time: time);
        var dispatched = false;
        await Fails(executor.ExecuteAsync(Http, 0, _ => { dispatched = true; return Task.FromResult(0); }),
            OperationFailureKind.Deadline);
        Check(!dispatched, "Operation dispatched after admission had exhausted its budget.");
    }

    [Test]
    public async Task CancellationAndCleanupHoldOneReservationUntilCleanupCompletes()
    {
        using var executor = new OperationExecutor(SmallCapacity);
        using var cancellation = new CancellationTokenSource();
        var cleanupStarted = Signal<CancellationToken>();
        var cleanupFinished = Signal<bool>();
        Task<int>? execution = null;
        static async Task<int> Work(OperationContext context)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
            return 0;
        }
        var request = executor.ExecuteAsync(Http, 0, context => execution = Work(context),
            token => { cleanupStarted.SetResult(token); return cleanupFinished.Task; }, cancellation.Token);
        cancellation.Cancel();
        var cleanupToken = await cleanupStarted.Task;
        Check(!cleanupToken.IsCancellationRequested, "Caller cancellation prevented cleanup.");
        await Fails(executor.ExecuteAsync(Http, 0, Value), OperationFailureKind.Capacity);
        Check(await executor.ExecuteAsync(Control, 0, Value) == 42, "Cleanup blocked reserved control capacity.");
        cancellation.Cancel();
        await Cancelled(execution!);
        cleanupFinished.SetResult(true);
        await Cancelled(request);
        var held = Signal<int>();
        var next = executor.ExecuteAsync(Http, 0, _ => held.Task);
        await Fails(executor.ExecuteAsync(Http, 0, Value), OperationFailureKind.Capacity);
        held.SetResult(1);
        await next;
    }

    [Test]
    public async Task ValidatedSuccessSurvivesCancellationDisposalAndFailedCleanup()
    {
        using var executor = new OperationExecutor();
        using var cancellation = new CancellationTokenSource();
        var cleanupStarted = Signal<bool>();
        var cleanup = Signal<bool>();
        var request = executor.ExecuteAsync(Http, 0, Value,
            _ => { cleanupStarted.SetResult(true); return cleanup.Task; }, cancellation.Token);
        await cleanupStarted.Task;
        cancellation.Cancel();
        executor.Dispose();
        executor.Dispose();
        cleanup.SetException(new IOException("secret response body"));
        Check(await request == 42, "Later cleanup/cancellation replaced a validated result.");
    }

    [Test]
    public async Task DisposalCancelsActiveOperationAndFutureCallsUseNormalDisposedException()
    {
        var executor = new OperationExecutor();
        var active = executor.ExecuteAsync(Http, 0, async context =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
            return 0;
        });
        executor.Dispose();
        executor.Dispose();
        await Cancelled(active);
        try { await executor.ExecuteAsync(Http, 0, Value); }
        catch (ObjectDisposedException) { return; }
        throw new InvalidOperationException("Disposed executor admitted work.");
    }

    [Test]
    public async Task CleanupIsBoundedButUnsettledWorkCannotBypassCapacity()
    {
        var time = new ManualTime();
        using var executor = new OperationExecutor(SmallCapacity, time);
        var cleanupStarted = Signal<CancellationToken>();
        var cleanup = Signal<bool>();
        var request = executor.ExecuteAsync(Http, 0, Value,
            token => { cleanupStarted.SetResult(token); return cleanup.Task; });
        var cleanupToken = await cleanupStarted.Task;
        time.Advance(TimeSpan.FromSeconds(5));
        Check(await request == 42 && cleanupToken.IsCancellationRequested, "Cleanup did not stop waiting at its deadline.");
        await Fails(executor.ExecuteAsync(Http, 0, Value), OperationFailureKind.Capacity);
        cleanup.SetResult(true);
        // The lease observer is asynchronous; a control call remains usable while it finishes.
        Check(await executor.ExecuteAsync(Control, 0, Value) == 42, "Uncooperative cleanup consumed control capacity.");
    }

    [Test]
    public async Task UncooperativeExecutionRemainsCountedAfterCallerDeadline()
    {
        var time = new ManualTime();
        using var executor = new OperationExecutor(SmallCapacity, time);
        var work = Signal<int>();
        OperationContext? running = null;
        var request = executor.ExecuteAsync(Http, 0, context => { running = context; return work.Task; });
        time.Advance(TimeSpan.FromSeconds(30));
        await Fails(request, OperationFailureKind.Deadline);
        await Fails(executor.ExecuteAsync(Http, 0, Value), OperationFailureKind.Capacity);
        Check(running!.CancellationToken.IsCancellationRequested, "Late transport work lost its cancelled token.");
        work.SetException(new IOException("private transport detail"));
    }

    [Test]
    public async Task UncooperativeConnectRemainsCountedUntilItsOwnedCleanupSettles()
    {
        var time = new ManualTime();
        using var executor = new OperationExecutor(SmallCapacity, time);
        var connection = Signal<bool>();
        var cleanupStarted = Signal<bool>();
        var cleanupFinished = Signal<bool>();
        var disposed = false;
        var request = executor.ExecuteAsync(Http, 0, async context =>
        {
            await context.ConnectAsync(_ => connection.Task);
            return 0;
        }, async _ =>
        {
            cleanupStarted.SetResult(true);
            await connection.Task; // Adapter owns even a connection allocated after cancellation.
            disposed = true;
            cleanupFinished.SetResult(true);
        });
        time.Advance(TimeSpan.FromSeconds(10));
        await cleanupStarted.Task;
        time.Advance(TimeSpan.FromSeconds(5));
        await Fails(request, OperationFailureKind.Deadline, OperationStage.Connect);
        await Fails(executor.ExecuteAsync(Http, 0, Value), OperationFailureKind.Capacity);
        Check(await executor.ExecuteAsync(Control, 0, Value) == 42, "Pending connection blocked control capacity.");
        connection.SetResult(true);
        await cleanupFinished.Task;
        Check(disposed, "Late connection escaped adapter cleanup ownership.");
    }

    [Test]
    public async Task ConnectionLeasesAreBoundedByClientAndCanonicalOriginAndReleaseOnce()
    {
        using var executor = new OperationExecutor();
        var oecp = Enumerable.Range(0, 18).Select(_ => executor.RegisterOecpConnection(Oecp)).ToArray();
        await Fails(Task.Run(() => executor.RegisterOecpConnection(Oecp)), OperationFailureKind.Capacity);
        oecp[0].Dispose();
        oecp[0].Dispose();
        using var replacement = executor.RegisterOecpConnection(Oecp);
        await Fails(Task.Run(() => executor.RegisterOecpConnection(Oecp)), OperationFailureKind.Capacity);
        foreach (var lease in oecp) lease.Dispose();
        var origin = new Uri("https://example.com");
        var http = Enumerable.Range(0, 8).Select(_ => executor.RegisterHttpConnection(Http, origin)).ToArray();
        await Fails(Task.Run(() => executor.RegisterHttpConnection(Http, new Uri("https://EXAMPLE.COM:443/a?secret"))),
            OperationFailureKind.Capacity);
        using var other = executor.RegisterHttpConnection(Http, new Uri("https://example.com:444"));
        http[0].Dispose();
        http[0].Dispose();
        using var newHttp = executor.RegisterHttpConnection(Http, origin);
        await Fails(Task.Run(() => executor.RegisterHttpConnection(Http, origin)), OperationFailureKind.Capacity);
        foreach (var lease in http) lease.Dispose();
    }

    [Test]
    public async Task HttpConnectionCapacitySharesUnicodeAndPunycodeOrigins()
    {
        using var executor = new OperationExecutor();
        var unicode = new Uri("https://bücher.example");
        var ascii = new Uri("https://XN--BCHER-KVA.EXAMPLE:443/other");
        var leases = Enumerable.Range(0, 8).Select(_ => executor.RegisterHttpConnection(Http, unicode)).ToArray();
        await Fails(Task.Run(() => executor.RegisterHttpConnection(Http, ascii)), OperationFailureKind.Capacity);
        leases[0].Dispose();
        using var replacement = executor.RegisterHttpConnection(Http, ascii);
        await Fails(Task.Run(() => executor.RegisterHttpConnection(Http, unicode)), OperationFailureKind.Capacity);
        foreach (var lease in leases) lease.Dispose();
    }

    [Test]
    public async Task RequestsEnforceDefaultAndSmallerOperationCeilingsBeforeDispatch()
    {
        using var executor = new OperationExecutor();
        Check(await executor.ExecuteAsync(Http, 4 * 1024 * 1024, Value) == 42, "Exact HTTP ceiling rejected.");
        Check(await executor.ExecuteAsync(Oecp, 1024 * 1024, Value) == 42, "Exact OECP ceiling rejected.");
        var dispatched = false;
        Task<int> Unexpected(OperationContext _) { dispatched = true; return Task.FromResult(0); }
        await Fails(executor.ExecuteAsync(Http, 4 * 1024 * 1024 + 1, Unexpected), OperationFailureKind.SizeLimit, OperationStage.Request);
        await Fails(executor.ExecuteAsync(Oecp, 1024 * 1024 + 1, Unexpected), OperationFailureKind.SizeLimit, OperationStage.Request);
        var smaller = new OperationDescriptor("history.list", OperationTransport.Http, requestBytes: 5);
        await Fails(executor.ExecuteAsync(smaller, 6, Unexpected), OperationFailureKind.SizeLimit, OperationStage.Request);
        Check(!dispatched, "Oversized request dispatched.");
    }

    [Test]
    public async Task ResponseAndMessageCeilingsIncludeFragmentedAndExactBoundaryBodies()
    {
        using var executor = new OperationExecutor();
        var operation = new OperationDescriptor("history.list", OperationTransport.Http, responseBytes: 4, messageBytes: 3);
        Check((await executor.ExecuteAsync(operation, 0, context => context.ReadResponseAsync(new MemoryStream([1, 2, 3, 4])))).Length == 4,
            "Exact body ceiling rejected.");
        await Fails(executor.ExecuteAsync(operation, 0, context => context.ReadResponseAsync(new FragmentedBody([1, 2, 3, 4, 5]))),
            OperationFailureKind.SizeLimit, OperationStage.Response);
        await Fails(executor.ExecuteAsync(operation, 0, context => { context.CheckMessageSize(4); return Task.FromResult(0); }),
            OperationFailureKind.SizeLimit, OperationStage.Message);
        await Fails(executor.ExecuteAsync(Oecp, 0, context => { context.CheckMessageSize(8 * 1024 * 1024 + 1); return Task.FromResult(0); }),
            OperationFailureKind.SizeLimit, OperationStage.Message);
        Check(await executor.ExecuteAsync(Oecp, 0, context => { context.CheckMessageSize(8 * 1024 * 1024); return Task.FromResult(42); }) == 42,
            "Exact message ceiling rejected.");
        await Fails(executor.ExecuteAsync(Http, 0, context => context.ReadResponseAsync(new MemoryStream(new byte[8 * 1024 * 1024 + 1]))),
            OperationFailureKind.SizeLimit, OperationStage.Response);
    }

    [Test]
    public async Task SafeFailuresDiscardForeignExceptionsAndRawDiagnosticsAreExplicitBoundedCopies()
    {
        const string secret = "credential-secret-and-remote-payload";
        using var executor = new OperationExecutor();
        var failure = await Fails(executor.ExecuteAsync<int>(Http, 0,
            _ => throw new IOException(secret, new Exception(secret))), OperationFailureKind.Transport);
        Check(!failure.ToString().Contains(secret) && failure.InnerException is null && failure.ExportRawDiagnostic() is null,
            "Foreign exception leaked sensitive data.");
        Check(failure.Message.Contains(Http.Name) && failure.Message.Contains(failure.CorrelationId.ToString("D"))
            && failure.Message.Contains("Http"), "Failure lacks safe operation context.");
        var raw = Encoding.UTF8.GetBytes(secret);
        failure = await Fails(executor.ExecuteAsync<int>(Http, 0,
            context => throw context.Failure(OperationFailureKind.Transport, OperationStage.Response, raw)), OperationFailureKind.Transport);
        Check(failure.ExportRawDiagnostic() is null && !failure.ToString().Contains(secret), "Default diagnostic capture was enabled.");
        using var optedIn = new OperationExecutor(new OperationLimits { CaptureRawDiagnostics = true });
        failure = await Fails(optedIn.ExecuteAsync<int>(Http, 0,
            context => throw context.Failure(OperationFailureKind.Transport, OperationStage.Response, raw)), OperationFailureKind.Transport);
        raw[0] = 0;
        var exported = failure.ExportRawDiagnostic()!;
        Check(Encoding.UTF8.GetString(exported) == secret && !failure.ToString().Contains(secret), "Raw ownership or formatting failed.");
        exported[0] = 0;
        Check(Encoding.UTF8.GetString(failure.ExportRawDiagnostic()!) == secret, "Diagnostic export exposed mutable retained storage.");
        await Fails(optedIn.ExecuteAsync<int>(Http, 0, context => throw context.Failure(OperationFailureKind.Transport,
            OperationStage.Response, new byte[64 * 1024 + 1])), OperationFailureKind.SizeLimit, OperationStage.Diagnostic);
        failure = await Fails(optedIn.ExecuteAsync<int>(Http, 0, context => throw context.Failure(OperationFailureKind.Transport,
            OperationStage.Response, new byte[64 * 1024])), OperationFailureKind.Transport);
        Check(failure.ExportRawDiagnostic()!.Length == 64 * 1024, "Exact diagnostic ceiling rejected.");
    }

    private sealed class BlockedBody : MemoryStream
    {
        public TaskCompletionSource<bool> Started { get; } = Signal<bool>();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private sealed class FragmentedBody(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }

    private sealed class AdmissionPauseTime : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ticks += TimeSpan.FromSeconds(31).Ticks;
            return new ManualTime().CreateTimer(callback, state, dueTime, period);
        }
    }
}
