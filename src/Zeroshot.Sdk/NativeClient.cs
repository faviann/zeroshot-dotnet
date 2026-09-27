using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Zeroshot.Native.Contracts;
using Zeroshot.Native.Execution;

namespace Zeroshot.Native;

/// <summary>Individual operations on an existing native target; never launches a process.</summary>
public sealed class NativeClient : IDisposable, IAsyncDisposable
{
    private static readonly HttpRequestOptionsKey<OperationContext> ContextKey = new("Zeroshot.Operation");
    private readonly HttpClient http;
    private readonly bool ownsHttpClient;
    private readonly OperationExecutor executor;
    private readonly OperationLimits limits;
    private int disposed;
    public Uri Origin { get; }
    public NativeTargetClient Target { get; }

    private NativeClient(NativeClientOptions options, HttpClient? supplied, bool ownsHttpClient)
    {
        ArgumentNullException.ThrowIfNull(options);
        Origin = ValidateOrigin(options.Origin);
        ArgumentNullException.ThrowIfNull(options.Transport);
        limits = options.Transport.Limits();
        if (supplied is not null && supplied.Timeout != Timeout.InfiniteTimeSpan && supplied.Timeout < limits.UnaryTimeout)
            throw new ArgumentException("A supplied HttpClient timeout must be infinite or at least RequestTimeout.", nameof(supplied));
        executor = new OperationExecutor(limits);
        if (supplied is null)
        {
            var handler = CreateHttpHandler(options.Transport);
            handler.ConnectCallback = ConnectAsync;
            http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            this.ownsHttpClient = true;
        }
        else { http = supplied; this.ownsHttpClient = ownsHttpClient; }
        Target = new NativeTargetClient(this);
    }

    /// <summary>Creates a safe owned HTTP transport, or borrows a caller-compliant client by default.</summary>
    public static NativeClient ForHttp(NativeClientOptions options, HttpClient? httpClient = null, bool ownsHttpClient = false)
        => new(options, httpClient, ownsHttpClient);

    /// <summary>Supported handler configuration for supplied clients. Do not loosen its TLS, redirect or pool settings.</summary>
    public static SocketsHttpHandler CreateHttpHandler(TransportOptions? options = null)
    {
        var limits = (options ?? new TransportOptions()).Limits();
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = limits.ConnectTimeout,
            MaxConnectionsPerServer = limits.HttpConnectionsPerOrigin,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None
        };
    }

    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext connection, CancellationToken token)
    {
        var operation = NativeTargetClient.DiscoveryOperation;
        var lease = executor.RegisterHttpConnection(operation, Origin);
        Socket? socket = null;
        try
        {
            socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            // HttpClient owns pooling/TLS; this lease follows the physical socket, including idle pooling.
            if (connection.InitialRequestMessage.Options.TryGetValue(ContextKey, out var context))
                await context.ConnectAsync(async ct =>
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, token);
                    await socket.ConnectAsync(connection.DnsEndPoint, linked.Token).ConfigureAwait(false);
                }).ConfigureAwait(false);
            else await socket.ConnectAsync(connection.DnsEndPoint, token).ConfigureAwait(false);
            return new LeasedNetworkStream(socket, lease);
        }
        catch { socket?.Dispose(); lease.Dispose(); throw; }
    }

    internal async Task<TargetDiscoveryDocument> DiscoverAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var responseGate = new object();
        HttpResponseMessage? ownedResponse = null;
        var cleanupStarted = false;
        try
        {
            return await executor.ExecuteAsync(NativeTargetClient.DiscoveryOperation, 0, async context =>
            {
                var requestUri = new Uri(Origin, NativeTargetClient.DiscoveryPath);
                using var request = new HttpRequestMessage(HttpMethod.Get, requestUri)
                {
                    Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact
                };
                request.Options.Set(ContextKey, context);
                var response = await SendAsync(request, context).ConfigureAwait(false);
                bool retained;
                lock (responseGate)
                {
                    retained = !cleanupStarted;
                    if (retained) ownedResponse = response;
                }
                // A supplied handler can allocate after cancellation and cleanup. Close that late result too.
                if (!retained) response.Dispose();
                context.ThrowIfCancelled();
                if (response.RequestMessage?.RequestUri != requestUri || (int)response.StatusCode is >= 300 and < 400)
                    throw context.Failure(OperationFailureKind.Redirect, OperationStage.Response, statusCode: response.StatusCode);
                var stream = await response.Content.ReadAsStreamAsync(context.CancellationToken).ConfigureAwait(false);
                var bytes = await context.ReadResponseAsync(stream, response.IsSuccessStatusCode ? null : limits.DiagnosticBytes).ConfigureAwait(false);
                if (response.StatusCode != HttpStatusCode.OK)
                    throw context.Failure(OperationFailureKind.HttpStatus, OperationStage.Response, bytes, response.StatusCode);
                TargetDiscoveryDocument discovery;
                try { discovery = NativeJson.DeserializeUtf8<TargetDiscoveryDocument>(bytes); }
                catch (JsonException) { throw context.Failure(OperationFailureKind.Protocol, OperationStage.Response); }
                if (discovery.Kind != "zeroshot.native-v2-target/v2" || discovery.Audience != "controller")
                    throw context.Failure(OperationFailureKind.Protocol, OperationStage.Response);
                return discovery;
            }, cleanup: _ => Task.Run(() =>
            {
                HttpResponseMessage? response;
                lock (responseGate)
                {
                    cleanupStarted = true;
                    response = ownedResponse;
                    ownedResponse = null;
                }
                response?.Dispose(); // Response owns its content stream. Cleanup cannot replace a valid result.
            }), cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationFailure failure) { throw new NativeHttpException(failure); }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, OperationContext context)
    {
        try
        {
            return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                context.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException)
        {
            // SocketsHttpHandler wraps callback failures and its separate DNS/TCP/TLS timeout.
            for (Exception? inner = error; inner is not null; inner = inner.InnerException)
            {
                if (inner is OperationFailure failure) throw failure;
                if (inner is TimeoutException && !context.CancellationToken.IsCancellationRequested)
                    throw context.Failure(OperationFailureKind.Deadline, OperationStage.Connect);
            }
            throw;
        }
    }

    private static Uri ValidateOrigin(Uri origin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        var raw = origin.OriginalString;
        // Inspect original text too: System.Uri normalizes dot segments, backslashes and short IP forms.
        if (!origin.IsAbsoluteUri || raw.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) || raw.Contains('\\') ||
            !string.IsNullOrEmpty(origin.UserInfo) || raw.Contains('?') || raw.Contains('#') || origin.AbsolutePath != "/" ||
            (origin.Scheme != "https" && !(origin.Scheme == "http" && origin.Host is "127.0.0.1" or "[::1]")))
            throw new ArgumentException("An HTTPS origin or numeric loopback HTTP origin without credentials, path, query or fragment is required.", nameof(origin));
        var authorityEnd = raw.IndexOf('/', raw.IndexOf("://", StringComparison.Ordinal) + 3);
        if (authorityEnd >= 0 && raw[authorityEnd..] != "/")
            throw new ArgumentException("A target origin cannot include a path.", nameof(origin));
        if (origin.Scheme == "http")
        {
            var authority = raw[(raw.IndexOf("://", StringComparison.Ordinal) + 3)..].TrimEnd('/');
            if (!(authority == "127.0.0.1" || authority.StartsWith("127.0.0.1:", StringComparison.Ordinal) ||
                authority == "[::1]" || authority.StartsWith("[::1]:", StringComparison.Ordinal)))
                throw new ArgumentException("HTTP requires the numeric loopback spelling.", nameof(origin));
        }
        return new Uri(origin.GetLeftPart(UriPartial.Authority) + "/");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        executor.Dispose();
        if (ownsHttpClient) http.Dispose();
    }
    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }

    private sealed class LeasedNetworkStream(Socket socket, IDisposable lease) : NetworkStream(socket, ownsSocket: true)
    {
        protected override void Dispose(bool disposing)
        {
            try { base.Dispose(disposing); }
            finally { if (disposing) lease.Dispose(); }
        }
    }
}

public sealed class NativeTargetClient
{
    internal const string DiscoveryPath = "/.well-known/zeroshot-native-v2";
    internal static readonly OperationDescriptor DiscoveryOperation = new("target.discover", OperationTransport.Http);
    private readonly NativeClient client;
    internal NativeTargetClient(NativeClient client) => this.client = client;
    public Task<TargetDiscoveryDocument> DiscoverAsync(CancellationToken cancellationToken = default)
        => client.DiscoverAsync(cancellationToken);
}
