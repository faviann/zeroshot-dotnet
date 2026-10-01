using System.Collections.Concurrent;
using System.Net.WebSockets;
using Zeroshot.Native.Contracts;
using Zeroshot.Native.Execution;

namespace Zeroshot.Native;

public sealed partial class NativeClient
{
    private static readonly OperationDescriptor ConnectOecpOperation = new("oecp.connect", OperationTransport.Oecp);
    private readonly ConcurrentDictionary<OecpConnection, byte> oecpConnections = new();

    /// <summary>Dials an acquired session; revalidates its same-origin endpoint and session bearer before dispatch.</summary>
    public async Task<OecpConnection> ConnectOecpAsync(TargetOecpSession session, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(session);
        var endpoint = NativeRoutes.SessionEndpoint(Origin, session.Endpoint);
        if (session.BearerToken is not null) TargetControlCredentials.ValidateBearer(session.BearerToken);
        return (await OecpConnect.ConnectAsync(new Host(this), ConnectOecpOperation, async (context, resources) =>
        {
            var socket = resources.Own(new ClientWebSocket());
            socket.Options.KeepAliveInterval = transportOptions.EnableWebSocketLiveness ? transportOptions.WebSocketPingInterval : Timeout.InfiniteTimeSpan;
            socket.Options.KeepAliveTimeout = transportOptions.EnableWebSocketLiveness ? transportOptions.WebSocketPongTimeout : Timeout.InfiniteTimeSpan;
            if (session.BearerToken is not null) socket.Options.SetRequestHeader("Authorization", "Bearer " + session.BearerToken);
            var invoker = resources.Own(new HttpMessageInvoker(CreateHttpHandler(transportOptions)));
            await context.ConnectAsync(token => socket.ConnectAsync(endpoint, invoker, token)).ConfigureAwait(false);
            return (Origin, new WebSocketTransport(socket, invoker));
        }, cancellationToken).ConfigureAwait(false))!;
    }

    // Shares the client's budgets. The client disposes them, and its connections, when it is disposed.
    private sealed class Host(NativeClient client) : OecpHost(client.executor, client.limits, client.Observations, processWideIds: false)
    {
        internal override bool Attach(OecpConnection connection)
        {
            client.oecpConnections.TryAdd(connection, 0);
            return Volatile.Read(ref client.disposed) == 0;
        }

        internal override void Released(OecpConnection connection) => client.oecpConnections.TryRemove(connection, out _);
    }
}
