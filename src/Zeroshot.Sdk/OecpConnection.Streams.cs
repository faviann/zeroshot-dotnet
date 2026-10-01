using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Principal;
using Zeroshot.Native.Execution;

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
        return Task.FromResult(OecpConnect.Bind(StandaloneOecpHost.For(options), FromStreamsOperation,
            new NdjsonTransport(input, output, leaveOpen)));
    }

    /// <summary>Connects to an existing native controller socket at a caller-known absolute path. The endpoint is never
    /// derived, created or reopened. The connection owns and closes its socket.</summary>
    public static async Task<OecpConnection> ConnectUnixAsync(string path, TransportOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("The controller socket path must be absolute.", nameof(path));
        var endpoint = new UnixDomainSocketEndPoint(path);
        return (await OecpConnect.ConnectAsync(StandaloneOecpHost.For(options), ConnectUnixOperation, async (context, resources) =>
        {
            var socket = resources.Own(new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified));
            await context.ConnectAsync(token => socket.ConnectAsync(endpoint, token).AsTask()).ConfigureAwait(false);
            var stream = new NetworkStream(socket, ownsSocket: true);
            return (FileUri(path), new NdjsonTransport(stream, stream, leaveOpen: false));
        }, cancellationToken).ConfigureAwait(false))!;
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
        var connection = await OecpConnect.ConnectAsync(StandaloneOecpHost.For(options), ConnectNamedPipeOperation, async (context, resources) =>
        {
            // Identification-only quality of service, as native's client: the controller cannot impersonate the caller.
            var pipe = resources.Own(new NamedPipeClientStream(".", path[LocalPipePrefix.Length..], PipeDirection.InOut,
                PipeOptions.Asynchronous, TokenImpersonationLevel.Identification));
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
            return (origin, new NdjsonTransport(pipe, pipe, leaveOpen: false));
        }, cancellationToken).ConfigureAwait(false);
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
}
