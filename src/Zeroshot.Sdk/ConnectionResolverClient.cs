using System.Net;
using System.Net.Http.Headers;
using Zeroshot.Native.Contracts;
using Zeroshot.Native.Execution;

namespace Zeroshot.Native;

/// <summary>
/// Calls one host-owned connection-resolver callback as native hosting does (native 10.9.0
/// <c>native_v2_hosting/connections.rs</c>). The endpoint is the host's own HTTPS authority, never a
/// target capability route, and the resolver bearer is sent only to it. There is no retry.
/// </summary>
public sealed class ConnectionResolverClient : IDisposable, IAsyncDisposable
{
    internal static readonly OperationDescriptor ResolveOperation = new("connectionResolver.resolve", OperationTransport.Http,
        responseBytes: 300 * 1024);
    private static readonly TimeSpan NativeTimeout = TimeSpan.FromSeconds(30);
    private readonly NativeClient http;
    private readonly string bearerToken;
    private readonly HashSet<string> keys;

    public Uri Endpoint { get; }

    private ConnectionResolverClient(TargetConnectionResolver resolver, TransportOptions? transport, HttpClient? httpClient)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        Endpoint = ValidateEndpoint(resolver.Endpoint);
        TargetControlCredentials.ValidateBearer(resolver.BearerToken);
        // Native validated_dynamic_keys.
        var declared = resolver.Keys.IsDefault ? [] : resolver.Keys;
        keys = declared.Select(key => key.Value).ToHashSet(StringComparer.Ordinal);
        if (keys.Count == 0 || keys.Count != declared.Length ||
            (resolver.SourceConnection is { } source && !keys.Contains(source.Value)))
            throw new ArgumentException("Resolver keys must be distinct and nonempty, and include any source connection.", nameof(resolver));
        bearerToken = resolver.BearerToken;
        transport ??= new TransportOptions();
        // Native's fixed 30-second resolution timeout; a smaller configured deadline still applies.
        if (transport.RequestTimeout > NativeTimeout) transport = transport with { RequestTimeout = NativeTimeout };
        http = NativeClient.ForHttp(new NativeClientOptions
        {
            Origin = new Uri(Endpoint.GetLeftPart(UriPartial.Authority) + "/"), Transport = transport
        }, httpClient);
    }

    /// <summary>
    /// Binds one resolver authority. A supplied HttpClient is borrowed and must meet the
    /// <see cref="NativeClient.ForHttp"/> caller contract; in particular it must disable automatic redirects, or
    /// the request body can reach a redirect's Location before the redirect is refused. Invalid descriptors
    /// throw <see cref="ArgumentException"/>.
    /// </summary>
    public static ConnectionResolverClient ForHttp(TargetConnectionResolver resolver, TransportOptions? transport = null,
        HttpClient? httpClient = null) => new(resolver, transport, httpClient);

    /// <summary>
    /// Sends one resolution request for declared keys. Failures throw <see cref="ConnectionResolutionException"/>;
    /// the returned values are not matched against the requested fields.
    /// </summary>
    public async Task<ConnectionResolveResult> ResolveAsync(ConnectionResolveRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var body = NativeJson.SerializeUtf8(request);
        if (request.Connections.Keys.FirstOrDefault(key => !keys.Contains(key)) is not null)
            throw new ArgumentException("The request names a connection key outside the resolver's declared keys.", nameof(request));
        try
        {
            return await http.ExecuteJsonAsync<ConnectionResolveResult>(ResolveOperation, Endpoint, body, null, _ => { },
                cancellationToken, configure: message =>
                    message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken)).ConfigureAwait(false);
        }
        catch (NativeHttpException failure) { throw new ConnectionResolutionException(failure); }
    }

    // Native validated_endpoint: HTTPS with a host and no userinfo, query or fragment. The canonical
    // spelling rules shared with target routes are narrower, so System.Uri never dials a different URL.
    private static Uri ValidateEndpoint(string endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        try
        {
            if (!endpoint.StartsWith("https://", StringComparison.Ordinal) || !Uri.TryCreate(endpoint, UriKind.Absolute, out var parsed))
                throw new ArgumentException();
            return NativeRoutes.SameOriginUrl(new Uri(parsed.GetLeftPart(UriPartial.Authority) + "/"), endpoint);
        }
        catch (Exception error) when (error is ArgumentException or UriFormatException)
        {
            throw new ArgumentException("A canonical HTTPS resolver endpoint with a path and no userinfo, query or fragment is required.",
                nameof(endpoint));
        }
    }

    public override string ToString() => nameof(ConnectionResolverClient);
    public void Dispose() => http.Dispose();
    public ValueTask DisposeAsync() => http.DisposeAsync();
}

/// <summary>Native <c>ConnectionResolutionError</c>.</summary>
public enum ConnectionResolutionError { Unavailable, Refused, InvalidResponse }

/// <summary>
/// A failed resolution with native's category. <see cref="Exception.InnerException"/> is the safe
/// <see cref="NativeHttpException"/> carrying the failure kind, status and opt-in raw diagnostics.
/// </summary>
public sealed class ConnectionResolutionException : Exception
{
    public ConnectionResolutionError Error { get; }
    public HttpStatusCode? StatusCode => ((NativeHttpException)InnerException!).StatusCode;

    internal ConnectionResolutionException(NativeHttpException failure)
        : base($"Connection resolution failed: {Classify(failure)}.", failure) => Error = Classify(failure);

    // Native require_resolution_status decides from the status alone; problem bodies are ignored. A redirect
    // is native's 3xx InvalidResponse even when a redirect-following client reports its Location's status.
    private static ConnectionResolutionError Classify(NativeHttpException failure) => failure.StatusCode switch
    {
        _ when failure.Kind == NativeHttpFailureKind.Redirect => ConnectionResolutionError.InvalidResponse,
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ConnectionResolutionError.Refused,
        HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests => ConnectionResolutionError.Unavailable,
        { } status when (int)status is >= 500 and < 600 => ConnectionResolutionError.Unavailable,
        { } status when (int)status is < 200 or >= 300 => ConnectionResolutionError.InvalidResponse,
        // A 2xx or no status: native maps send/read failures to Unavailable, bad bodies and redirects to InvalidResponse.
        _ => failure.Kind is NativeHttpFailureKind.Transport or NativeHttpFailureKind.Deadline or NativeHttpFailureKind.Capacity
            ? ConnectionResolutionError.Unavailable : ConnectionResolutionError.InvalidResponse
    };
}
