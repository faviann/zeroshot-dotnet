using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace Zeroshot.Native.Observations;

internal sealed class ObservationQueue<T, TPosition>(ObservationDelivery owner) : IDisposable
    where TPosition : class
{
    private readonly Queue<(T Record, int Bytes, TPosition? Position)> records = [];
    private readonly TaskCompletionSource stop = Signal();
    private readonly Guid correlationId = Guid.NewGuid();
    private TaskCompletionSource changed = Signal();
    private CancellationTokenRegistration cancellation;
    private long queuedBytes;
    private bool stopped;
    private bool producerCompleted;
    private bool discarded;
    private bool readerClaimed;
    private Exception? error;
    private TPosition? receivedPosition;
    private TPosition? bufferedPosition;
    private TPosition? deliveredPosition;

    // One bounded signal, no callbacks or transport work run by the queue. A binding
    // observes it, stops its producer and calls Complete after its cleanup settles.
    internal Task StopRequested => stop.Task;
    internal TPosition? LastReceivedPosition { get { lock (owner.Gate) return receivedPosition; } }
    internal TPosition? LastBufferedPosition { get { lock (owner.Gate) return bufferedPosition; } }
    internal TPosition? LastDeliveredPosition { get { lock (owner.Gate) return deliveredPosition; } }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal void RegisterCancellation(CancellationToken token)
    {
        // Registration can invoke immediately; never register while holding Gate.
        var registration = token.UnsafeRegister(_ => Discard(new OperationCanceledException(token)), null);
        bool unregister;
        lock (owner.Gate)
        {
            unregister = discarded || producerCompleted && records.Count == 0;
            if (!unregister) cancellation = registration;
        }
        if (unregister) registration.Unregister();
    }

    // Bindings validate the whole record first and pass its retained encoded size.
    // No waiting writers: overflow ends this stream and is visible to both sides.
    // False means a concurrent stop won and the record was never accepted.
    internal bool TryEnqueue(T record, int encodedBytes, TPosition? position = null)
    {
        if (encodedBytes < 0) throw new ArgumentOutOfRangeException(nameof(encodedBytes));
        lock (owner.Gate)
        {
            if (stopped) return false;
            if (position is not null) receivedPosition = position;
            ObservationFailureKind? overflow = records.Count == owner.Limits.MaxQueuedObservationRecords
                ? ObservationFailureKind.RecordLimit
                : encodedBytes > owner.Limits.MaxQueuedObservationBytes - queuedBytes
                    ? ObservationFailureKind.StreamByteLimit
                    : encodedBytes > owner.Limits.MaxAggregateObservationBytes - owner.QueuedBytes
                        ? ObservationFailureKind.AggregateByteLimit : null;
            if (overflow is { } kind)
            {
                var failure = new ObservationFailure(kind, correlationId);
                error = failure;
                Stop();
                throw failure;
            }
            records.Enqueue((record, encodedBytes, position));
            queuedBytes += encodedBytes;
            owner.QueuedBytes += encodedBytes;
            if (position is not null) bufferedPosition = position;
            Pulse();
            return true;
        }
    }

    // End delivery independently of producer cleanup, which may still be settling.
    // Any supplied exception must already be safe, binding-owned failure metadata.
    internal void StopReceiving(Exception? failure = null)
    {
        lock (owner.Gate)
        {
            if (stopped) return;
            error = failure;
            Stop();
        }
    }

    // Producer acknowledgement: no producer work/cleanup remains. May also end
    // delivery when the binding has already finished all of its cleanup.
    internal void Complete(Exception? failure = null)
    {
        lock (owner.Gate)
        {
            if (producerCompleted) return;
            producerCompleted = true;
            if (!stopped) error = failure;
            Stop();
            ReleaseIfFinished();
        }
    }

    internal async IAsyncEnumerable<T> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        lock (owner.Gate)
        {
            if (readerClaimed) throw new InvalidOperationException("An observation has one consumer.");
            readerClaimed = true;
        }
        // Remains registered while a consumer is processing a record or abandoned.
        var registration = cancellationToken.UnsafeRegister(_ => Discard(new OperationCanceledException(cancellationToken)), null);
        try
        {
            while (true)
            {
                T? record = default;
                Task? wait = null;
                Exception? failure;
                bool delivered;
                lock (owner.Gate)
                {
                    delivered = records.TryDequeue(out var next);
                    if (delivered)
                    {
                        queuedBytes -= next.Bytes;
                        owner.QueuedBytes -= next.Bytes;
                        record = next.Record;
                        if (next.Position is not null) deliveredPosition = next.Position;
                        ReleaseIfFinished();
                    }
                    else if (!stopped) wait = changed.Task;
                    failure = error;
                }
                if (delivered) yield return record!;
                else if (wait is not null) await wait.ConfigureAwait(false);
                else
                {
                    if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
                    yield break;
                }
            }
        }
        finally
        {
            registration.Unregister();
            Dispose();
        }
    }

    public void Dispose() => Discard(new ObjectDisposedException("Native observation"));

    private void Discard(Exception failure)
    {
        lock (owner.Gate)
        {
            if (discarded) return;
            discarded = true;
            error = failure;
            owner.QueuedBytes -= queuedBytes;
            queuedBytes = 0;
            records.Clear();
            Stop();
            cancellation.Unregister(); // Nonblocking, including cancellation racing this lock.
            ReleaseIfFinished();
        }
    }

    // All helpers run under the single client gate. Signals run continuations
    // asynchronously, so no consumer/producer callbacks execute under that gate.
    private void Stop()
    {
        stopped = true;
        stop.TrySetResult();
        Pulse();
    }

    private void Pulse()
    {
        changed.TrySetResult();
        changed = Signal();
    }

    private void ReleaseIfFinished()
    {
        if (!producerCompleted || records.Count != 0) return;
        cancellation.Unregister();
        owner.Release(this);
    }
}
