using System.Net.Sockets;
using Zeroshot.Native.Execution;
using Zeroshot.Native.Observations;

namespace Zeroshot.Native;

public sealed partial class OecpConnection
{
    private static readonly OperationDescriptor ConnectUnixOperation = new("oecp.connectUnix", OperationTransport.Oecp);
    private static readonly OperationDescriptor FromStreamsOperation = new("oecp.fromStreams", OperationTransport.Oecp);

    /// <summary>Binds native NDJSON OECP to caller-supplied streams. Nothing is launched and no handshake is sent.
    /// Borrowed streams stay open unless <paramref name="leaveOpen"/> is false. The connection has its own
    /// request and observation budgets from <paramref name="options"/>.</summary>
    public static Task<OecpConnection> FromStreamsAsync(Stream input, Stream output, bool leaveOpen = true,
        TransportOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        if (!input.CanRead) throw new ArgumentException("The input stream must be readable.", nameof(input));
        if (!output.CanWrite) throw new ArgumentException("The output stream must be writable.", nameof(output));
        cancellationToken.ThrowIfCancellationRequested();
        var (limits, executor, observations) = Standalone(options);
        var connection = new OecpConnection(null, new NdjsonTransport(input, output, leaveOpen),
            executor.RegisterOecpConnection(FromStreamsOperation), executor, limits, observations, _ => { }, processWideIds: true);
        connection.Start();
        return Task.FromResult(connection);
    }

    /// <summary>Connects to an existing native controller socket at a caller-known absolute path. The endpoint is never
    /// derived, created or reopened. The connection owns and closes its socket.</summary>
    public static async Task<OecpConnection> ConnectUnixAsync(string path, TransportOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("The controller socket path must be absolute.", nameof(path));
        var endpoint = new UnixDomainSocketEndPoint(path);
        var (limits, executor, observations) = Standalone(options);
        Socket? socket = null;
        IDisposable? lease = null;
        var connected = false;
        try
        {
            return await executor.ExecuteAsync(ConnectUnixOperation, 0, async context =>
            {
                lease = executor.RegisterOecpConnection(ConnectUnixOperation);
                socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await context.ConnectAsync(token => socket.ConnectAsync(endpoint, token).AsTask()).ConfigureAwait(false);
                context.ThrowIfCancelled();
                var stream = new NetworkStream(socket, ownsSocket: true);
                var result = new OecpConnection(FileUri(path), new NdjsonTransport(stream, stream, leaveOpen: false),
                    lease, executor, limits, observations, _ => { }, processWideIds: true);
                result.Start();
                connected = true;
                return result;
            }, cleanup: _ =>
            {
                if (!connected) { socket?.Dispose(); lease?.Dispose(); }
                return Task.CompletedTask;
            }, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationFailure failure) { throw new NativeOecpException(failure, new(null, false, false, false)); }
    }

    // Identifies exactly the supplied path: segments are escaped, extra leading slashes cannot
    // become a UNC authority and dot segments are not collapsed.
    private static Uri FileUri(string path)
    {
        var relative = path.TrimStart('/');
        var escaped = string.Concat(Enumerable.Repeat("%2F", path.Length - relative.Length - 1)) +
            string.Join('/', relative.Split('/').Select(Uri.EscapeDataString));
        return new Uri("file:///" + escaped, new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true });
    }

    private static (OperationLimits, OperationExecutor, ObservationDelivery) Standalone(TransportOptions? options)
    {
        options ??= new TransportOptions();
        var limits = options.Limits();
        return (limits, new OperationExecutor(limits), new ObservationDelivery(options));
    }
}
