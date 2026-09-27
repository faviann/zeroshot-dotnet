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
        ClientWebSocket? socket = null;
        HttpMessageInvoker? invoker = null;
        IDisposable? lease = null;
        var connected = false;
        try
        {
            return await executor.ExecuteAsync(ConnectOecpOperation, 0, async context =>
            {
                lease = executor.RegisterOecpConnection(ConnectOecpOperation);
                socket = new ClientWebSocket();
                socket.Options.KeepAliveInterval = transportOptions.EnableWebSocketLiveness ? transportOptions.WebSocketPingInterval : Timeout.InfiniteTimeSpan;
                socket.Options.KeepAliveTimeout = transportOptions.EnableWebSocketLiveness ? transportOptions.WebSocketPongTimeout : Timeout.InfiniteTimeSpan;
                if (session.BearerToken is not null) socket.Options.SetRequestHeader("Authorization", "Bearer " + session.BearerToken);
                invoker = new HttpMessageInvoker(CreateHttpHandler(transportOptions));
                await context.ConnectAsync(token => socket.ConnectAsync(endpoint, invoker, token)).ConfigureAwait(false);
                context.ThrowIfCancelled();
                var result = new OecpConnection(socket, invoker, lease, executor, limits, Observations, connection => oecpConnections.TryRemove(connection, out _));
                oecpConnections.TryAdd(result, 0);
                result.Start();
                if (Volatile.Read(ref disposed) != 0) { result.Dispose(); context.ThrowIfCancelled(); }
                connected = true;
                return result;
            }, cleanup: _ =>
            {
                if (!connected) { socket?.Dispose(); invoker?.Dispose(); lease?.Dispose(); }
                return Task.CompletedTask;
            }, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationFailure failure) { throw new NativeOecpException(failure, new(null, false, false, false)); }
    }
}
