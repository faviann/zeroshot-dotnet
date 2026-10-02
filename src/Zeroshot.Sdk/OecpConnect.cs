using Zeroshot.Native.Execution;
using Zeroshot.Native.Observations;

namespace Zeroshot.Native;

/// <summary>The budgets an OECP connection runs under and the owner told about its lifetime.</summary>
internal abstract class OecpHost(OperationExecutor executor, OperationLimits limits, ObservationDelivery observations, bool processWideIds)
{
    internal OperationExecutor Executor { get; } = executor;
    internal OperationLimits Limits { get; } = limits;
    internal ObservationDelivery Observations { get; } = observations;
    internal bool ProcessWideIds { get; } = processWideIds;
    /// <summary>Registers a connection before it starts. False when the owner closed meanwhile.</summary>
    internal virtual bool Attach(OecpConnection connection) => true;
    internal virtual void Released(OecpConnection connection) { }
    /// <summary>The connect failed, so no connection uses what the host created for it.</summary>
    internal virtual void Abandon() { }
}

/// <summary>Budgets created for one connection outside a <see cref="NativeClient"/>.</summary>
// Closing the connection does not dispose them: their disposal cancels in-flight calls, which would turn a
// connection failure into caller cancellation, and discards observations still buffered for subscribers.
// Neither holds an OS resource, so after close they are left to the collector.
internal sealed class StandaloneOecpHost(TransportOptions options, OperationLimits limits)
    : OecpHost(new OperationExecutor(limits), limits, new ObservationDelivery(options), processWideIds: true)
{
    internal static StandaloneOecpHost For(TransportOptions? options)
    {
        options ??= new TransportOptions();
        return new(options, options.Limits());
    }

    internal override void Abandon() { Observations.Dispose(); Executor.Dispose(); }
}

/// <summary>The one way an OECP connection is established: lease, bounded dial, start, and release of
/// everything created when the connect does not produce a connection.</summary>
internal static class OecpConnect
{
    /// <summary>Binds an already open transport; nothing is dialled.</summary>
    internal static OecpConnection Bind(OecpHost host, OperationDescriptor operation, IOecpTransport transport)
    {
        try { return Start(host, host.Executor.RegisterOecpConnection(operation), null, transport); }
        catch { host.Abandon(); throw; }
    }

    /// <summary>Runs <paramref name="dial"/> as one bounded operation. The dial connects through the context and
    /// returns the origin and transport, or null when the endpoint is refused before anything is sent; null is
    /// returned then. Resources the dial passes to <see cref="Resources.Own"/> are disposed unless a connection starts.</summary>
    internal static async Task<OecpConnection?> ConnectAsync(OecpHost host, OperationDescriptor operation,
        Func<OperationContext, Resources, Task<(Uri? Origin, IOecpTransport Transport)?>> dial, CancellationToken cancellationToken)
    {
        var resources = new Resources();
        OecpConnection? connection = null;
        try
        {
            connection = await host.Executor.ExecuteAsync(operation, 0, async context =>
            {
                var lease = resources.Own(host.Executor.RegisterOecpConnection(operation));
                var dialled = await dial(context, resources).ConfigureAwait(false);
                context.ThrowIfCancelled();
                if (dialled is not { } bound) return null;
                var result = Start(host, lease, bound.Origin, bound.Transport, context);
                resources.Keep();
                return result;
            }, cleanup: _ => { resources.Dispose(); return Task.CompletedTask; }, cancellationToken: cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch (OperationFailure failure) { throw new NativeOecpException(failure, new(null, false, false, false)); }
        finally { if (connection is null) host.Abandon(); }
    }

    private static OecpConnection Start(OecpHost host, IDisposable lease, Uri? origin, IOecpTransport transport, OperationContext? context = null)
    {
        var connection = new OecpConnection(origin, transport, lease, host);
        var attached = host.Attach(connection);
        connection.Start();
        // The owner's disposal cancels the context, unless it raced ahead of that cancellation.
        if (!attached) { connection.Dispose(); context?.ThrowIfCancelled(); }
        return connection;
    }

    /// <summary>What a dial created. A dial abandoned by its deadline can create one after cleanup ran,
    /// so a late resource is disposed at once.</summary>
    internal sealed class Resources : IDisposable
    {
        private readonly object gate = new();
        private List<IDisposable>? owned = [];

        internal T Own<T>(T resource) where T : IDisposable
        {
            lock (gate) if (owned is not null) { owned.Add(resource); return resource; }
            resource.Dispose();
            return resource;
        }

        /// <summary>The started connection took ownership.</summary>
        internal void Keep() { lock (gate) owned = []; }

        public void Dispose()
        {
            List<IDisposable>? created;
            lock (gate) (created, owned) = (owned, null);
            if (created is null) return;
            for (var i = created.Count - 1; i >= 0; i--) created[i].Dispose();
        }
    }
}
