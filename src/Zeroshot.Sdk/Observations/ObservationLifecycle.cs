using System.Runtime.CompilerServices;
using System.Text.Json;
using Zeroshot.Native.Contracts;

namespace Zeroshot.Native.Observations;

/// <summary>A received frame or event exceeded its binding's ceiling.</summary>
internal sealed class ObservationFrameTooLarge : Exception;

/// <summary>
/// One observation's closure decision above its queue, shared by every streaming binding. The first outcome wins
/// and stops delivery; a local stop with no outcome is Cancelled or Disposed. Framing stays with the binding,
/// which either pushes records (<see cref="Receive"/>) or runs a reader that pulls them (<see cref="Enqueue"/>).
/// </summary>
internal sealed class ObservationLifecycle<T>(ObservationQueue<T, Cursor> queue)
{
    private readonly object gate = new();
    private readonly TaskCompletionSource<NativeSubscriptionCompletion> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private NativeSubscriptionCompletion? outcome;
    internal Task<NativeSubscriptionCompletion> Completion => completion.Task;
    internal Cursor? LastDeliveredCursor => queue.LastDeliveredPosition;

    /// <summary>Pushed records. Once stopped, <paramref name="release"/> receives the outcome before completion.</summary>
    internal void Start(Func<NativeSubscriptionCompletion, Task> release) => _ = SettleAsync(null, release);

    /// <summary>
    /// Pulled records. <paramref name="read"/> enqueues until the server ends the subscription, returning the failure
    /// its close reports, if any; size, protocol and read failures are thrown. The response is the whole
    /// subscription: stopping cancels the read and disposes only that response.
    /// </summary>
    internal void Start(IDisposable response, Func<CancellationToken, Task<NativeSubscriptionException?>> read)
    {
        var reading = new CancellationTokenSource();
        var pump = PumpAsync(read, reading);
        _ = SettleAsync(async () =>
        {
            reading.Cancel();
            response.Dispose(); // Closes only this observation's connection, also unblocking a read that ignores cancellation.
            await pump.ConfigureAwait(false);
        }, _ => { reading.Dispose(); return Task.CompletedTask; });
    }

    internal async IAsyncEnumerable<T> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var reader = queue.ReadAllAsync(cancellationToken).GetAsyncEnumerator();
        while (true)
        {
            bool next;
            try { next = await reader.MoveNextAsync().ConfigureAwait(false); }
            catch (ObservationFailure failure) { throw NativeSubscriptionException.From(failure); }
            if (!next) yield break;
            yield return reader.Current;
        }
    }

    /// <summary>One pushed record, parsed and validated by <paramref name="produce"/>. Ignored once an outcome exists.</summary>
    internal void Receive(int encodedBytes, Func<(T Record, Cursor? Position)> produce)
    {
        lock (gate)
        {
            if (outcome is not null) return;
            try
            {
                var (record, position) = produce();
                queue.TryEnqueue(record, encodedBytes, position); // False: a concurrent stop won, the settle path decides.
            }
            catch (Exception error) when (LocalFailure(error) is { } failure) { Settle(NativeSubscriptionOrigin.LocalFailure, failure); }
        }
    }

    /// <summary>One pulled record, called only from the reader.</summary>
    internal void Enqueue(T record, int encodedBytes, Cursor? position)
    {
        if (!queue.TryEnqueue(record, encodedBytes, position)) throw new Stopped();
    }

    internal void Settle(NativeSubscriptionOrigin origin, NativeSubscriptionException? failure = null,
        SubscriptionClosedNotification? serverClose = null)
    {
        lock (gate)
        {
            if (outcome is not null) return;
            outcome = new() { Origin = origin, Failure = failure, ServerClose = serverClose };
            queue.StopReceiving(failure);
        }
    }

    /// <summary>The producer's transport ended. A local stop keeps its local outcome; an orderly close is a disposal.</summary>
    internal void Disconnect(NativeSubscriptionFailureKind? failure)
    {
        lock (gate)
        {
            if (outcome is not null) return;
            if (queue.StopFailure is OperationCanceledException) outcome = new() { Origin = NativeSubscriptionOrigin.Cancelled };
            else if (failure is null || queue.StopFailure is ObjectDisposedException)
            {
                outcome = new() { Origin = NativeSubscriptionOrigin.Disposed };
                queue.Dispose();
            }
            else Settle(NativeSubscriptionOrigin.UnexpectedDisconnect, new(failure.Value));
        }
    }

    /// <summary>The failure a server close reason carries; a normal close has none.</summary>
    internal static NativeSubscriptionException? CloseFailure(SubscriptionCloseReason reason) => reason switch
    {
        SubscriptionCloseReason.SlowConsumer => new(NativeSubscriptionFailureKind.SlowConsumer),
        SubscriptionCloseReason.SourceUnavailable => new(NativeSubscriptionFailureKind.SourceUnavailable),
        _ => null
    };

    internal async ValueTask DisposeAsync()
    {
        queue.Dispose();
        await Completion.ConfigureAwait(false);
    }

    private static NativeSubscriptionException? LocalFailure(Exception error) => error switch
    {
        ObservationFailure overflow => NativeSubscriptionException.From(overflow),
        ObservationFrameTooLarge => new(NativeSubscriptionFailureKind.SizeLimit),
        JsonException or ArgumentException => new(NativeSubscriptionFailureKind.Protocol), // Includes invalid UTF-8.
        _ => null
    };

    private async Task PumpAsync(Func<CancellationToken, Task<NativeSubscriptionException?>> read, CancellationTokenSource reading)
    {
        await Task.Yield();
        NativeSubscriptionOrigin origin;
        NativeSubscriptionException? failure;
        try
        {
            failure = await read(reading.Token).ConfigureAwait(false);
            origin = NativeSubscriptionOrigin.ServerClosed;
        }
        catch (Stopped) { return; } // A concurrent stop won; the settle path decides.
        catch (ObservationFailure overflow) { (origin, failure) = (NativeSubscriptionOrigin.LocalFailure, NativeSubscriptionException.From(overflow)); }
        catch (Exception) when (reading.IsCancellationRequested) { return; } // Stopped locally; the settle path decides.
        catch (Exception error)
        {
            // Read failures and truncated streams; foreign exception text is never retained.
            (origin, failure) = LocalFailure(error) is { } local
                ? (NativeSubscriptionOrigin.LocalFailure, local)
                : (NativeSubscriptionOrigin.UnexpectedDisconnect, new(NativeSubscriptionFailureKind.UnexpectedDisconnect));
        }
        Settle(origin, failure);
    }

    private async Task SettleAsync(Func<Task>? stopProducer, Func<NativeSubscriptionCompletion, Task> release)
    {
        await queue.StopRequested.ConfigureAwait(false);
        if (stopProducer is not null) await stopProducer().ConfigureAwait(false);
        NativeSubscriptionCompletion result;
        lock (gate)
        {
            outcome ??= new() { Origin = queue.StopFailure is OperationCanceledException
                ? NativeSubscriptionOrigin.Cancelled : NativeSubscriptionOrigin.Disposed };
            result = outcome;
        }
        try { await release(result).ConfigureAwait(false); }
        finally
        {
            queue.Complete();
            completion.TrySetResult(result);
        }
    }

    private sealed class Stopped : Exception;
}
