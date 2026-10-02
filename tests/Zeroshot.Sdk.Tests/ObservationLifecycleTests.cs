using System.Text.Json;
using TUnit.Core;
using Zeroshot.Native;
using Zeroshot.Native.Contracts;
using Zeroshot.Native.Observations;

namespace Zeroshot.Client.Tests;

public sealed class ObservationLifecycleTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private static void Check(bool condition, string message = "Lifecycle assertion failed.")
    { if (!condition) throw new InvalidOperationException(message); }
    private static (ObservationLifecycle<string> Lifecycle, ObservationQueue<string, Cursor> Queue) Open(CancellationToken token = default)
    {
        var queue = new ObservationDelivery(new TransportOptions { MaxQueuedObservationRecords = 1 }).Open<string, Cursor>(token);
        return (new ObservationLifecycle<string>(queue), queue);
    }
    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Test]
    public async Task TransportLossAfterALocalStopKeepsTheLocalOutcome()
    {
        using var cancel = new CancellationTokenSource();
        var (cancelled, _) = Open(cancel.Token);
        var (disposed, disposedQueue) = Open();
        var released = new List<NativeSubscriptionOrigin>();
        Task Release(NativeSubscriptionCompletion result) { lock (released) released.Add(result.Origin); return Task.CompletedTask; }
        // Stop locally, then lose the transport before the settle path runs.
        cancel.Cancel();
        disposedQueue.Dispose();
        cancelled.Disconnect(NativeSubscriptionFailureKind.UnexpectedDisconnect);
        disposed.Disconnect(NativeSubscriptionFailureKind.Protocol);
        cancelled.Start(Release);
        disposed.Start(Release);
        Check((await cancelled.Completion.WaitAsync(Wait)) is { Origin: NativeSubscriptionOrigin.Cancelled, Failure: null });
        Check((await disposed.Completion.WaitAsync(Wait)) is { Origin: NativeSubscriptionOrigin.Disposed, Failure: null });
        Check(released.Order().SequenceEqual([NativeSubscriptionOrigin.Disposed, NativeSubscriptionOrigin.Cancelled]));
    }

    [Test]
    public async Task AnOrderlyTransportCloseDiscardsAndAFaultedOneFails()
    {
        var (orderly, orderlyQueue) = Open();
        var (faulted, _) = Open();
        orderly.Receive(1, () => ("buffered", null));
        orderly.Disconnect(null);
        faulted.Disconnect(NativeSubscriptionFailureKind.SizeLimit);
        orderly.Start(_ => Task.CompletedTask);
        faulted.Start(_ => Task.CompletedTask);
        Check((await orderly.Completion.WaitAsync(Wait)).Origin == NativeSubscriptionOrigin.Disposed);
        Check(orderlyQueue.StopFailure is ObjectDisposedException, "An orderly close must discard buffered records.");
        var failure = await faulted.Completion.WaitAsync(Wait);
        Check(failure is { Origin: NativeSubscriptionOrigin.UnexpectedDisconnect, Failure.Kind: NativeSubscriptionFailureKind.SizeLimit });
        var thrown = false;
        try { await foreach (var _ in faulted.ReadAllAsync()) { } }
        catch (NativeSubscriptionException error) { thrown = error.Kind == NativeSubscriptionFailureKind.SizeLimit; }
        Check(thrown, "The reader must see the transport failure.");
    }

    [Test]
    public async Task ThePushedOutcomeIsFirstWinsAndReleaseCannotStrandCompletion()
    {
        var (lifecycle, _) = Open();
        lifecycle.Receive(1, () => throw new JsonException());
        lifecycle.Settle(NativeSubscriptionOrigin.ServerClosed, ObservationLifecycle<string>.CloseFailure(SubscriptionCloseReason.SlowConsumer));
        lifecycle.Receive(1, () => throw new InvalidOperationException("Ignored once settled."));
        lifecycle.Start(_ => throw new IOException());
        var result = await lifecycle.Completion.WaitAsync(Wait);
        Check(result is { Origin: NativeSubscriptionOrigin.LocalFailure, Failure.Kind: NativeSubscriptionFailureKind.Protocol, ServerClose: null });
    }

    [Test]
    public async Task APushedOverflowIsALocalFailure()
    {
        var (lifecycle, _) = Open();
        lifecycle.Receive(1, () => ("first", new Cursor("c1")));
        lifecycle.Receive(1, () => ("second", new Cursor("c2")));
        lifecycle.Start(_ => Task.CompletedTask);
        Check((await lifecycle.Completion.WaitAsync(Wait)) is
            { Origin: NativeSubscriptionOrigin.LocalFailure, Failure.Kind: NativeSubscriptionFailureKind.RecordLimit });
    }

    [Test]
    public async Task APulledReadDecidesBeforeTheLocalDefault()
    {
        // The reader completes its server close only once its response is disposed, after the local stop.
        var (closed, closedQueue) = Open();
        var response = new Response();
        closed.Start(response, async _ => { await response.Disposed.Task; return null; });
        closedQueue.Dispose();
        Check((await closed.Completion.WaitAsync(Wait)).Origin == NativeSubscriptionOrigin.ServerClosed);

        // A reader that fails because it was stopped leaves the local outcome.
        var (stopped, stoppedQueue) = Open();
        var other = new Response();
        stopped.Start(other, async token => { await Task.Delay(Timeout.Infinite, token); return null; });
        stoppedQueue.Dispose();
        Check((await stopped.Completion.WaitAsync(Wait)).Origin == NativeSubscriptionOrigin.Disposed && other.Disposed.Task.IsCompleted);
    }

    [Test]
    public async Task PulledReadFailuresAreClassifiedWithoutForeignText()
    {
        foreach (var (error, origin, kind) in new (Exception, NativeSubscriptionOrigin, NativeSubscriptionFailureKind)[]
        {
            (new ObservationFrameTooLarge(), NativeSubscriptionOrigin.LocalFailure, NativeSubscriptionFailureKind.SizeLimit),
            (new JsonException("secret"), NativeSubscriptionOrigin.LocalFailure, NativeSubscriptionFailureKind.Protocol),
            (new ArgumentException("secret"), NativeSubscriptionOrigin.LocalFailure, NativeSubscriptionFailureKind.Protocol),
            (new IOException("secret"), NativeSubscriptionOrigin.UnexpectedDisconnect, NativeSubscriptionFailureKind.UnexpectedDisconnect)
        })
        {
            var (lifecycle, _) = Open();
            var response = new Response();
            lifecycle.Start(response, _ => Task.FromException<NativeSubscriptionException?>(error));
            var result = await lifecycle.Completion.WaitAsync(Wait);
            Check(result.Origin == origin && result.Failure?.Kind == kind && !result.Failure.Message.Contains("secret") &&
                response.Disposed.Task.IsCompleted, $"Wrong classification for {error.GetType().Name}.");
        }
        var (overflow, _) = Open();
        overflow.Start(new Response(), _ =>
        {
            overflow.Enqueue("first", 1, null);
            overflow.Enqueue("second", 1, null);
            return Task.FromResult<NativeSubscriptionException?>(null);
        });
        Check((await overflow.Completion.WaitAsync(Wait)) is
            { Origin: NativeSubscriptionOrigin.LocalFailure, Failure.Kind: NativeSubscriptionFailureKind.RecordLimit });
    }

    private sealed class Response : IDisposable
    {
        public TaskCompletionSource<bool> Disposed { get; } = Signal<bool>();
        public void Dispose() => Disposed.TrySetResult(true);
    }
}
