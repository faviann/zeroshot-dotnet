using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Principal;
using Zeroshot.Native.Execution;
using Zeroshot.Native.Observations;

namespace Zeroshot.Native;

public sealed partial class OecpConnection
{
    private static readonly OperationDescriptor ConnectUnixOperation = new("oecp.connectUnix", OperationTransport.Oecp);
    private static readonly OperationDescriptor FromStreamsOperation = new("oecp.fromStreams", OperationTransport.Oecp);
    private static readonly OperationDescriptor ConnectNamedPipeOperation = new("oecp.connectNamedPipe", OperationTransport.Oecp);
    private const string LocalPipePrefix = @"\\.\pipe\";

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

    /// <summary>Connects to an existing native controller pipe at a caller-known <c>\\.\pipe\&lt;name&gt;</c> path. The name is
    /// never derived and the pipe is never created or reopened. Before anything is sent, the pipe must pass native's
    /// check: owned by this process's user, with a DACL that only allows that user or SYSTEM. Otherwise, or when the
    /// pipe denies this user, <see cref="UnauthorizedAccessException"/> is thrown. The connection owns and closes its pipe.</summary>
    [SupportedOSPlatform("windows")]
    public static async Task<OecpConnection> ConnectNamedPipeAsync(string path, TransportOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native controller pipes exist only on Windows.");
        // Remote pipes are refused like native's own local-only endpoints. Windows would rewrite
        // a non-canonical name, such as one containing '/' or dot segments, into a different pipe.
        if (!path.StartsWith(LocalPipePrefix, StringComparison.OrdinalIgnoreCase) || path.Length == LocalPipePrefix.Length ||
            Path.GetFullPath(path) != path)
            throw new ArgumentException(@"The controller pipe must be an exact local \\.\pipe\<name> path.", nameof(path));
        var (limits, executor, observations) = Standalone(options);
        NamedPipeClientStream? pipe = null;
        IDisposable? lease = null;
        var connected = false;
        OecpConnection? connection;
        try
        {
            connection = await executor.ExecuteAsync(ConnectNamedPipeOperation, 0, async context =>
            {
                lease = executor.RegisterOecpConnection(ConnectNamedPipeOperation);
                // Identification-only quality of service, as native's client: the controller cannot impersonate the caller.
                pipe = new NamedPipeClientStream(".", path[LocalPipePrefix.Length..], PipeDirection.InOut,
                    PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
                var denied = false;
                await context.ConnectAsync(async token =>
                {
                    try { await pipe.ConnectAsync(token).ConfigureAwait(false); }
                    catch (UnauthorizedAccessException) { denied = true; }
                }).ConfigureAwait(false);
                context.ThrowIfCancelled();
                if (denied || !NamedPipeSecurity.IsPrivate(pipe.SafePipeHandle)) return null;
                // The whole pipe path is one escaped segment, so the origin identifies exactly that pipe.
                var origin = new Uri("file:///" + Uri.EscapeDataString(path),
                    new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true });
                var result = new OecpConnection(origin, new NdjsonTransport(pipe, pipe, leaveOpen: false),
                    lease, executor, limits, observations, _ => { }, processWideIds: true);
                result.Start();
                connected = true;
                return result;
            }, cleanup: _ =>
            {
                if (!connected) { pipe?.Dispose(); lease?.Dispose(); }
                return Task.CompletedTask;
            }, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationFailure failure) { throw new NativeOecpException(failure, new(null, false, false, false)); }
        return connection ?? throw new UnauthorizedAccessException("The controller pipe is not private to this user.");
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
