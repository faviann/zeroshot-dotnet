namespace Zeroshot.Native.Observations;

// One shared owner per NativeClient, across all observation transports.
internal sealed class ObservationDelivery : IDisposable
{
    internal readonly object Gate = new();
    internal readonly TransportOptions Limits;
    internal long QueuedBytes;
    private readonly HashSet<IDisposable> queues = [];
    private bool disposed;

    internal ObservationDelivery(TransportOptions limits) => Limits = limits;

    internal ObservationQueue<T, TPosition> Open<T, TPosition>(CancellationToken cancellationToken = default)
        where TPosition : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObservationQueue<T, TPosition> queue;
        lock (Gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (queues.Count == Limits.MaxConcurrentSubscriptions)
                throw new ObservationFailure(ObservationFailureKind.Admission, Guid.NewGuid());
            queue = new(this);
            queues.Add(queue);
        }
        queue.RegisterCancellation(cancellationToken);
        return queue;
    }

    // Called only under Gate, once producer completion and queue release both occurred.
    internal void Release(IDisposable queue) => queues.Remove(queue);

    public void Dispose()
    {
        IDisposable[] active;
        lock (Gate)
        {
            if (disposed) return;
            disposed = true;
            active = queues.ToArray();
        }
        foreach (var queue in active) queue.Dispose();
    }
}

internal enum ObservationFailureKind { Admission, RecordLimit, StreamByteLimit, AggregateByteLimit }

// Cursor and record content are available from the queue, never default diagnostics.
internal sealed class ObservationFailure(ObservationFailureKind kind, Guid correlationId)
    : Exception($"Native observation ({correlationId:D}) failed: {kind}.")
{
    internal ObservationFailureKind Kind { get; } = kind;
    internal Guid CorrelationId { get; } = correlationId;
}
