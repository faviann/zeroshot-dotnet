namespace Zeroshot.Native.Execution;

// One instance per native client. No semaphore waiters or admission queue.
internal sealed class OperationCapacity(OperationLimits limits)
{
    private readonly object gate = new();
    private readonly Dictionary<(string Scheme, string Host, int Port), int> http = [];
    private int requests;
    private int ordinaryRequests;
    private int oecp;

    public IDisposable? TryRequest(bool control)
    {
        lock (gate)
        {
            if (requests == limits.ConcurrentRequests ||
                (!control && ordinaryRequests == limits.ConcurrentRequests - limits.ReservedControlRequests))
                return null;
            requests++;
            if (!control) ordinaryRequests++;
            return new Reservation(() =>
            {
                lock (gate)
                {
                    requests--;
                    if (!control) ordinaryRequests--;
                }
            });
        }
    }

    public IDisposable? TryOecpConnection()
    {
        lock (gate)
        {
            if (oecp == limits.OecpConnections) return null;
            oecp++;
            return new Reservation(() => { lock (gate) oecp--; });
        }
    }

    public IDisposable? TryHttpConnection(Uri origin)
    {
        if (!origin.IsAbsoluteUri || (origin.Scheme != "http" && origin.Scheme != "https"))
            throw new ArgumentException("An absolute HTTP origin is required.", nameof(origin));
        var key = (origin.Scheme, origin.IdnHost.ToLowerInvariant(), origin.Port);
        lock (gate)
        {
            var count = http.GetValueOrDefault(key);
            if (count == limits.HttpConnectionsPerOrigin) return null;
            http[key] = count + 1;
            return new Reservation(() =>
            {
                lock (gate)
                {
                    if (http[key] == 1) http.Remove(key);
                    else http[key]--;
                }
            });
        }
    }

    private sealed class Reservation(Action release) : IDisposable
    {
        private Action? release = release;
        public void Dispose() => Interlocked.Exchange(ref release, null)?.Invoke();
    }
}
