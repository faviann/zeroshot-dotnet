using System.Net;
using System.Net.Sockets;
using System.Net.Http.Headers;
using System.Text.Json;
using Zeroshot.Native.Contracts;
using Zeroshot.Native.Execution;
using Zeroshot.Native.Observations;

namespace Zeroshot.Native;

/// <summary>Individual operations on an existing native target; never launches a process.</summary>
public sealed partial class NativeClient : IDisposable, IAsyncDisposable
{
    private static readonly HttpRequestOptionsKey<OperationContext> ContextKey = new("Zeroshot.Operation");
    private readonly HttpClient http;
    private readonly bool ownsHttpClient;
    private readonly OperationExecutor executor;
    private readonly OperationLimits limits;
    private int disposed;
    private readonly TransportOptions transportOptions;
    internal ObservationDelivery Observations { get; }
    public Uri Origin { get; }
    public NativeTargetClient Target { get; }
    public NativeConnectionsClient Connections { get; }

    private NativeClient(NativeClientOptions options, HttpClient? supplied, bool ownsHttpClient)
    {
        ArgumentNullException.ThrowIfNull(options);
        Origin = ValidateOrigin(options.Origin);
        if (supplied?.DefaultRequestHeaders.Authorization is not null)
            throw new ArgumentException("Supply credentials per operation, not as HTTP default headers.", nameof(supplied));
        ArgumentNullException.ThrowIfNull(options.Transport);
        limits = options.Transport.Limits();
        transportOptions = options.Transport;
        if (supplied is not null && supplied.Timeout != Timeout.InfiniteTimeSpan && supplied.Timeout < limits.UnaryTimeout)
            throw new ArgumentException("A supplied HttpClient timeout must be infinite or at least RequestTimeout.", nameof(supplied));
        executor = new OperationExecutor(limits);
        Observations = new ObservationDelivery(options.Transport);
        if (supplied is null)
        {
            var handler = CreateHttpHandler(options.Transport);
            handler.ConnectCallback = ConnectAsync;
            http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            this.ownsHttpClient = true;
        }
        else { http = supplied; this.ownsHttpClient = ownsHttpClient; }
        Target = new NativeTargetClient(this);
        Connections = new NativeConnectionsClient(this);
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
        connection.InitialRequestMessage.Options.TryGetValue(ContextKey, out var context);
        var operation = context?.Operation ?? NativeTargetClient.DiscoveryOperation;
        var lease = executor.RegisterHttpConnection(operation, Origin);
        Socket? socket = null;
        try
        {
            socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            // HttpClient owns pooling/TLS; this lease follows the physical socket, including idle pooling.
            if (context is not null)
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

    internal Task<TargetDiscoveryDocument> DiscoverAsync(CancellationToken cancellationToken)
        => ExecuteJsonAsync<TargetDiscoveryDocument>(NativeTargetClient.DiscoveryOperation, new Uri(Origin, NativeTargetClient.DiscoveryPath),
            null, null, discovery =>
            {
                if (discovery.Kind != "zeroshot.native-v2-target/v2" || discovery.Audience != "controller")
                    throw new JsonException();
            }, cancellationToken);

    internal Task<TargetOecpSession> CreateOecpSessionAsync(TargetDiscoveryDocument discovery,
        TargetOecpSessionRequest request, TargetControlCredentials? credentials, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(request);
        // No remote descriptor may influence credential-bearing dispatch until validated.
        _ = NativeJson.SerializeUtf8(discovery);
        if (discovery.Kind != "zeroshot.native-v2-target/v2" || discovery.Audience != "controller" ||
            (credentials?.Authentication ?? TargetAuthentication.None) != discovery.Authentication)
            throw new ArgumentException("Discovery and supplied control authority are incompatible.");
        _ = NativeRoutes.SameOriginPath(Origin, discovery.RunPath);
        _ = NativeRoutes.SameOriginPath(Origin, discovery.OecpPath);
        var endpoint = NativeRoutes.SameOriginPath(Origin, discovery.SessionPath);
        if (request.RunId is { Value: var id } && !TargetRunRequest.IsCanonicalRunId(id))
            throw new ArgumentException("A session run selector must be a canonical UUIDv7.", nameof(request));
        var bytes = NativeJson.SerializeUtf8(request);
        return ExecuteJsonAsync(NativeTargetClient.SessionOperation, endpoint, bytes, credentials,
            (TargetOecpSession session) =>
            {
                _ = NativeRoutes.SessionEndpoint(Origin, session.Endpoint);
                if (discovery.Authentication == TargetAuthentication.None)
                {
                    if (session.BearerToken is not null) throw new JsonException();
                }
                else TargetControlCredentials.ValidateBearer(session.BearerToken!);
            }, cancellationToken);
    }

    internal Task<T> ExecuteJsonAsync<T>(OperationDescriptor operation, Uri requestUri, byte[]? body,
        TargetControlCredentials? credentials, Action<T> validate, CancellationToken cancellationToken,
        Action<Guid>? onDispatch = null, Action<T>? onResponse = null, Action<HttpRequestMessage>? configure = null)
        => ExecuteHttpAsync(operation, body is null ? HttpMethod.Get : HttpMethod.Post, requestUri, body, credentials,
            async (response, context) =>
            {
                var stream = await response.Content.ReadAsStreamAsync(context.CancellationToken).ConfigureAwait(false);
                var bytes = await context.ReadResponseAsync(stream).ConfigureAwait(false);
                try
                {
                    var result = NativeJson.DeserializeUtf8<T>(bytes);
                    validate(result);
                    onResponse?.Invoke(result);
                    return result;
                }
                catch (Exception error) when (error is JsonException or ArgumentException)
                { throw context.Failure(OperationFailureKind.Protocol, OperationStage.Response, statusCode: response.StatusCode); }
            }, cancellationToken, onDispatch, configure);

    /// <summary>A body-less HEAD binding; refusals keep their status like any other HTTP operation.</summary>
    internal Task<NativeHeadResult> ExecuteHeadAsync(OperationDescriptor operation, Uri requestUri,
        TargetControlCredentials? credentials, CancellationToken cancellationToken, Action<HttpRequestMessage>? configure = null)
        => ExecuteHttpAsync(operation, HttpMethod.Head, requestUri, null, credentials,
            (response, _) => Task.FromResult(new NativeHeadResult(response.StatusCode,
                response.Content.Headers.ContentLength, response.Content.Headers.ContentType?.MediaType)),
            cancellationToken, configure: configure);

    /// <summary>Per-request header hook for operations whose native caller sends Cache-Control: no-store.</summary>
    internal static void NoStore(HttpRequestMessage request) => request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };

    private async Task<T> ExecuteHttpAsync<T>(OperationDescriptor operation, HttpMethod method, Uri requestUri, byte[]? body,
        TargetControlCredentials? credentials, Func<HttpResponseMessage, OperationContext, Task<T>> readSuccess,
        CancellationToken cancellationToken, Action<Guid>? onDispatch = null, Action<HttpRequestMessage>? configure = null,
        bool redirectIsResult = false)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var responseGate = new object();
        HttpResponseMessage? ownedResponse = null;
        TargetHttpProblem? problem = null;
        UiProblem? uiProblem = null;
        HttpStatusCode? receivedStatus = null;
        var cleanupStarted = false;
        try
        {
            return await executor.ExecuteAsync(operation, body?.Length ?? 0, async context =>
            {
                using var request = new HttpRequestMessage(method, requestUri)
                {
                    Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact
                };
                if (body is not null)
                {
                    request.Content = new ByteArrayContent(body);
                    request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                }
                // A direct target hands every later request on a UI-routed connection to its UI
                // router (native transport.rs serve_connection), so pooled reuse would send control
                // requests there. Close it after this exchange instead.
                if (operation.UiRouter) request.Headers.ConnectionClose = true;
                configure?.Invoke(request);
                if (credentials is not null)
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.BearerToken);
                request.Options.Set(ContextKey, context);
                context.ThrowIfCancelled();
                onDispatch?.Invoke(context.CorrelationId);
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
                receivedStatus = response.StatusCode;
                // Some browser routes answer with a redirect as their result; it is reported, never followed.
                var redirect = (int)response.StatusCode is >= 300 and < 400;
                if (response.RequestMessage?.RequestUri != requestUri || (redirect && !redirectIsResult))
                    throw context.Failure(OperationFailureKind.Redirect, OperationStage.Response, statusCode: response.StatusCode);
                if (!response.IsSuccessStatusCode && !redirect)
                {
                    var stream = await response.Content.ReadAsStreamAsync(context.CancellationToken).ConfigureAwait(false);
                    var bytes = await context.ReadResponseAsync(stream, Math.Min(limits.DiagnosticBytes,
                        operation.ProblemBytes ?? int.MaxValue)).ConfigureAwait(false);
                    // Native's UI router answers with its own {code,message} problems, not TargetHttpProblem.
                    try
                    {
                        if (operation.UiRouter) uiProblem = NativeJson.DeserializeUtf8<UiProblem>(bytes);
                        else problem = NativeJson.DeserializeUtf8<TargetHttpProblem>(bytes);
                    }
                    catch (JsonException) { } // Status remains an observed refusal even without a valid problem.
                    throw context.Failure(OperationFailureKind.HttpStatus, OperationStage.Response, bytes, response.StatusCode);
                }
                if ((operation == NativeTargetClient.DiscoveryOperation || operation == NativeTargetClient.SubmitOperation) &&
                    response.StatusCode != HttpStatusCode.OK)
                    throw context.Failure(OperationFailureKind.HttpStatus, OperationStage.Response, statusCode: response.StatusCode);
                return await readSuccess(response, context).ConfigureAwait(false);
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
        catch (OperationFailure failure) { throw new NativeHttpException(failure, problem, receivedStatus, uiProblem,
            // The direct UI mount and hosted hosts send the same closed code in their own problem shapes.
            operation.HistoryProblems ? RunHistoryProblems.Parse(uiProblem?.Code ?? problem?.Code) : null); }
    }

    /// <summary>
    /// Sends one mutation and classifies its evidence without retrying. The response is JSON unless
    /// <paramref name="readSuccess"/> reads the operation's own success shape.
    /// </summary>
    private async Task<(Guid CorrelationId, NativeAttemptOutcome Outcome, T? Response, Exception? Failure)> AttemptAsync<T>(
        OperationDescriptor operation, Uri requestUri, byte[] body, TargetControlCredentials? credentials,
        Func<HttpStatusCode?, string, bool> isRefusal, CancellationToken cancellationToken,
        Action<HttpRequestMessage>? configure = null,
        Func<HttpResponseMessage, OperationContext, Task<T>>? readSuccess = null) where T : class
    {
        var dispatched = 0;
        var correlationId = Guid.Empty;
        T? response = null;
        Exception? failure = null;
        void OnDispatch(Guid id) { correlationId = id; Interlocked.Exchange(ref dispatched, 1); }
        void Capture(T value) => Volatile.Write(ref response, value);
        try
        {
            await (readSuccess is null
                ? ExecuteJsonAsync<T>(operation, requestUri, body, credentials, _ => { }, cancellationToken,
                    onDispatch: OnDispatch, onResponse: Capture, configure: configure)
                : ExecuteHttpAsync(operation, HttpMethod.Post, requestUri, body, credentials, async (message, context) =>
                {
                    var value = await readSuccess(message, context).ConfigureAwait(false);
                    Capture(value);
                    return value;
                }, cancellationToken, OnDispatch, configure)).ConfigureAwait(false);
        }
        catch (Exception error) when (error is NativeHttpException or OperationCanceledException)
        {
            failure = error;
            correlationId = error switch
            {
                NativeHttpException httpFailure => httpFailure.CorrelationId,
                OperationCancelled cancelled => cancelled.CorrelationId,
                _ => correlationId
            };
        }

        // The request has finished its bounded cleanup. A response already validated
        // by the adapter wins a cancellation race, including cancellation during cleanup.
        var captured = Volatile.Read(ref response);
        var outcome = captured is not null ? NativeAttemptOutcome.Acknowledged
            : Volatile.Read(ref dispatched) == 0 ? NativeAttemptOutcome.NotSent
            : failure is NativeHttpException { Kind: NativeHttpFailureKind.HttpStatus, Problem: { } problem } refused &&
                isRefusal(refused.StatusCode, problem.Code)
                ? NativeAttemptOutcome.Rejected : NativeAttemptOutcome.Unknown;
        return (correlationId, outcome, captured, captured is null ? failure : null);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, OperationContext context)
    {
        if (http.DefaultRequestHeaders.Authorization is not null)
            throw new ArgumentException("Supply credentials per operation, not as HTTP default headers.");
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
            string.IsNullOrEmpty(origin.Host) || raw.Contains('@') || !string.IsNullOrEmpty(origin.UserInfo) || raw.Contains('?') || raw.Contains('#') || origin.AbsolutePath != "/" ||
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
        Observations.Dispose();
        executor.Dispose();
        foreach (var connection in oecpConnections.Keys) connection.Dispose();
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

public sealed partial class NativeTargetClient
{
    internal const string DiscoveryPath = "/.well-known/zeroshot-native-v2";
    internal static readonly OperationDescriptor DiscoveryOperation = new("target.discover", OperationTransport.Http);
    internal static readonly OperationDescriptor SessionOperation = new("target.createOecpSession", OperationTransport.Http, isControl: true,
        requestBytes: 4 * 1024 * 1024, responseBytes: 64 * 1024);
    private readonly NativeClient client;
    internal NativeTargetClient(NativeClient client) => this.client = client;
    public Task<TargetDiscoveryDocument> DiscoverAsync(CancellationToken cancellationToken = default)
        => client.DiscoverAsync(cancellationToken);

    /// <summary>Obtains one session with explicitly supplied discovery and current control authority; never refreshes credentials.</summary>
    public Task<TargetOecpSession> CreateOecpSessionAsync(TargetDiscoveryDocument discovery,
        TargetOecpSessionRequest? request = null, TargetControlCredentials? credentials = null,
        CancellationToken cancellationToken = default)
        => client.CreateOecpSessionAsync(discovery, request ?? new(), credentials, cancellationToken);
}
