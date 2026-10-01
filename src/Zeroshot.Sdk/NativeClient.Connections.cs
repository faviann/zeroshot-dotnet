using System.Net;
using System.Text;
using Zeroshot.Native.Contracts;
using Zeroshot.Native.Execution;

namespace Zeroshot.Native;

public sealed partial class NativeClient
{
    internal Task<ConnectionListResult> ListConnectionsAsync(TargetDiscoveryDocument discovery,
        ConnectionListRequest request, TargetControlCredentials credentials, CancellationToken cancellationToken)
    {
        var (routes, body) = PrepareConnectionCall(discovery, request, credentials);
        return ExecuteJsonAsync<ConnectionListResult>(NativeConnectionsClient.ListOperation, routes.List, body, credentials,
            _ => { }, cancellationToken, configure: NoStore);
    }

    internal async Task<NativeAttempt<T>> MutateConnectionAsync<T>(OperationDescriptor operation,
        Func<(Uri List, Uri Set, Uri Delete), Uri> route, TargetDiscoveryDocument discovery,
        TargetHttpContract request, TargetControlCredentials credentials, CancellationToken cancellationToken) where T : class
    {
        var (routes, body) = PrepareConnectionCall(discovery, request, credentials);
        return await AttemptAsync<T>(operation, route(routes), body, credentials,
            IsHostedRefusal, cancellationToken, configure: NoStore).ConfigureAwait(false);
    }

    // Native build_connections_descriptor. Invalid use throws before any request is sent.
    private ((Uri List, Uri Set, Uri Delete) Routes, byte[] Body) PrepareConnectionCall(TargetDiscoveryDocument discovery,
        TargetHttpContract request, TargetControlCredentials credentials)
    {
        var body = PrepareHostedCall(discovery, request, credentials);
        var wire = discovery.Extensions.Connections
            ?? throw new ArgumentException("The target does not advertise connection management.");
        var kinds = wire.DynamicKinds;
        if (wire.Kind != NativeConnectionsClient.Kind || kinds.Distinct(StringComparer.Ordinal).Count() != kinds.Length ||
            kinds.Any(kind => kind.Length == 0 || Encoding.UTF8.GetByteCount(kind) > 128 || kind.Any(char.IsControl)))
            throw new ArgumentException("Connection discovery is incompatible.");
        var baseUrl = NativeRoutes.CapabilityBaseUrl(Origin, wire.BaseUrl);
        // The run-scoped resolver is a host callback, not a caller operation; its declaration must still be valid.
        _ = NativeRoutes.CompileLiteralRoute(baseUrl, wire.RouteTemplates.Resolve);
        return ((NativeRoutes.CompileLiteralRoute(baseUrl, wire.RouteTemplates.List),
            NativeRoutes.CompileLiteralRoute(baseUrl, wire.RouteTemplates.Set),
            NativeRoutes.CompileLiteralRoute(baseUrl, wire.RouteTemplates.Delete)), body);
    }

    // Validates a hosted management call and returns its body. Invalid use throws before any request is sent.
    private byte[] PrepareHostedCall(TargetDiscoveryDocument discovery, TargetHttpContract request, TargetControlCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateHostedUse(discovery, credentials);
        return NativeJson.SerializeUtf8(request);
    }

    // The hosted gate shared by every host-owned operation. Invalid use throws before any request is sent.
    private void ValidateHostedUse(TargetDiscoveryDocument discovery, TargetControlCredentials credentials)
    {
        ValidateHttpUse();
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(credentials);
        // No remote descriptor may influence credential-bearing dispatch until validated.
        _ = NativeJson.SerializeUtf8(discovery);
        // Native refuses direct targets; only hosted OAuth discovery can carry these capabilities.
        if (discovery.Kind != "zeroshot.native-v2-target/v2" || discovery.Audience != "controller" ||
            discovery.Authentication != TargetAuthentication.HostedOauth || credentials.Authentication != TargetAuthentication.HostedOauth)
            throw new ArgumentException("Hosted operations require hosted OAuth discovery and matching credentials.");
    }

    // Native's status-derived default codes (default_http_error_code in contract/http_error.rs), which native
    // itself uses only when a response has no parseable problem. A hosted server's own codes are not pinned
    // and native serves no connection, profile or OAuth routes; every other received failure leaves the effect unknown.
    private static bool IsHostedRefusal(HttpStatusCode? status, string code) =>
        (status, code) is
            (HttpStatusCode.BadRequest, "invalid_request") or
            (HttpStatusCode.Unauthorized, "unauthorized") or
            (HttpStatusCode.Forbidden, "forbidden") or
            (HttpStatusCode.NotFound, "not_found");
}

/// <summary>Hosted user/org connection records at routes advertised by explicitly supplied discovery.</summary>
public sealed class NativeConnectionsClient
{
    internal const string Kind = "zeroshot.connections/v1";
    internal static readonly OperationDescriptor ListOperation = new("connections.list", OperationTransport.Http, responseBytes: 64 * 1024);
    internal static readonly OperationDescriptor SetOperation = new("connections.set", OperationTransport.Http, responseBytes: 64 * 1024);
    internal static readonly OperationDescriptor DeleteOperation = new("connections.delete", OperationTransport.Http, responseBytes: 64 * 1024);
    private readonly NativeClient client;
    internal NativeConnectionsClient(NativeClient client) => this.client = client;

    /// <summary>Reads secret-free summaries. Failures throw NativeHttpException.</summary>
    public Task<ConnectionListResult> ListAsync(TargetDiscoveryDocument discovery, ConnectionListRequest request,
        TargetControlCredentials credentials, CancellationToken cancellationToken = default)
        => client.ListConnectionsAsync(discovery, request, credentials, cancellationToken);

    /// <summary>Stores static values once. Operational failures and cancellation return evidence.</summary>
    public Task<NativeAttempt<ConnectionMutationResult>> SetAsync(TargetDiscoveryDocument discovery, ConnectionSetRequest request,
        TargetControlCredentials credentials, CancellationToken cancellationToken = default)
        => client.MutateConnectionAsync<ConnectionMutationResult>(SetOperation, routes => routes.Set, discovery, request, credentials, cancellationToken);

    /// <summary>Deletes one record once. Operational failures and cancellation return evidence.</summary>
    public Task<NativeAttempt<ConnectionDeleteResult>> DeleteAsync(TargetDiscoveryDocument discovery, ConnectionDeleteRequest request,
        TargetControlCredentials credentials, CancellationToken cancellationToken = default)
        => client.MutateConnectionAsync<ConnectionDeleteResult>(DeleteOperation, routes => routes.Delete, discovery, request, credentials, cancellationToken);
}
