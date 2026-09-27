using System.Runtime.CompilerServices;
using System.Text.Json;
using Zeroshot.Native.Contracts;
using Zeroshot.Native.Observations;

namespace Zeroshot.Native;

/// <summary>A received frame or event exceeded its binding's ceiling.</summary>
internal sealed class ObservationFrameTooLarge : Exception;

/// <summary>
/// Lifecycle shared by HTTP streaming observations. The response is the whole subscription: there is no
/// establishment, subscription cancellation or reopen, and stopping closes only that response.
/// </summary>
internal sealed class HttpObservation<T>
{
    private readonly ObservationQueue<T, Cursor> queue;
    private readonly object gate = new();
    private readonly CancellationTokenSource reading = new();
    private readonly TaskCompletionSource<NativeSubscriptionCompletion> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private NativeSubscriptionCompletion? outcome;
    internal Task<NativeSubscriptionCompletion> Completion => completion.Task;
    internal Cursor? LastDeliveredCursor => queue.LastDeliveredPosition;

    /// <param name="read">Reads and enqueues records until the server ends the subscription, returning the
    /// failure its close reports, if any. Size, protocol and read failures are thrown.</param>
    internal HttpObservation(ObservationQueue<T, Cursor> queue, HttpResponseMessage response,
        Func<HttpObservation<T>, CancellationToken, Task<NativeSubscriptionException?>> read)
    {
        this.queue = queue;
        _ = SettleAsync(response, ReadAsync(read));
    }

    internal void Enqueue(T record, int encodedBytes, Cursor? position)
    {
        if (!queue.TryEnqueue(record, encodedBytes, position)) throw new Stopped();
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

    private async Task ReadAsync(Func<HttpObservation<T>, CancellationToken, Task<NativeSubscriptionException?>> read)
    {
        await Task.Yield();
        NativeSubscriptionOrigin origin;
        NativeSubscriptionException? failure;
        try
        {
            failure = await read(this, reading.Token).ConfigureAwait(false);
            origin = NativeSubscriptionOrigin.ServerClosed;
        }
        catch (Stopped) { return; } // A concurrent stop won; the settle path decides.
        catch (ObservationFailure overflow)
        {
            origin = NativeSubscriptionOrigin.LocalFailure;
            failure = NativeSubscriptionException.From(overflow);
        }
        catch (Exception) when (reading.IsCancellationRequested) { return; } // Stopped locally; the settle path decides.
        catch (ObservationFrameTooLarge)
        { (origin, failure) = (NativeSubscriptionOrigin.LocalFailure, new(NativeSubscriptionFailureKind.SizeLimit)); }
        catch (Exception error) when (error is JsonException or ArgumentException) // Includes invalid UTF-8.
        { (origin, failure) = (NativeSubscriptionOrigin.LocalFailure, new(NativeSubscriptionFailureKind.Protocol)); }
        catch (Exception)
        {
            // Read failures and truncated streams; foreign exception text is never retained.
            (origin, failure) = (NativeSubscriptionOrigin.UnexpectedDisconnect, new(NativeSubscriptionFailureKind.UnexpectedDisconnect));
        }
        lock (gate)
        {
            if (outcome is not null) return;
            outcome = new() { Origin = origin, Failure = failure };
            queue.StopReceiving(failure);
        }
    }

    private async Task SettleAsync(HttpResponseMessage response, Task read)
    {
        await queue.StopRequested.ConfigureAwait(false);
        reading.Cancel();
        response.Dispose(); // Closes only this observation's connection, also unblocking a read that ignores cancellation.
        await read.ConfigureAwait(false);
        NativeSubscriptionCompletion result;
        lock (gate)
        {
            outcome ??= new() { Origin = queue.StopFailure is OperationCanceledException
                ? NativeSubscriptionOrigin.Cancelled : NativeSubscriptionOrigin.Disposed };
            result = outcome;
        }
        reading.Dispose();
        queue.Complete();
        completion.TrySetResult(result);
    }

    internal async ValueTask DisposeAsync()
    {
        queue.Dispose();
        await Completion.ConfigureAwait(false);
    }

    private sealed class Stopped : Exception;
}
