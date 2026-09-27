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
            _ => { }, cancellationToken, noStore: true);
    }

    internal async Task<NativeAttempt<T>> MutateConnectionAsync<T>(OperationDescriptor operation, TargetDiscoveryDocument discovery,
        TargetHttpContract request, TargetControlCredentials credentials, CancellationToken cancellationToken) where T : class
    {
        var (routes, body) = PrepareConnectionCall(discovery, request, credentials);
        var route = operation == NativeConnectionsClient.SetOperation ? routes.Set : routes.Delete;
        var (correlationId, outcome, response, failure) = await AttemptJsonAsync<T>(operation, route, body, credentials,
            IsConnectionRefusal, cancellationToken, noStore: true).ConfigureAwait(false);
        return new NativeAttempt<T>(Origin, operation.Name, correlationId, outcome, response, failure);
    }

    // Native build_connections_descriptor. Invalid use throws before any request is sent.
    private ((Uri List, Uri Set, Uri Delete) Routes, byte[] Body) PrepareConnectionCall(TargetDiscoveryDocument discovery,
        TargetHttpContract request, TargetControlCredentials credentials)
    {
        ValidateHttpUse();
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(credentials);
        var body = NativeJson.SerializeUtf8(request);
        _ = NativeJson.SerializeUtf8(discovery);
        // Native refuses direct targets; only hosted OAuth discovery can carry this capability.
        if (discovery.Kind != "zeroshot.native-v2-target/v2" || discovery.Audience != "controller" ||
            discovery.Authentication != TargetAuthentication.HostedOauth || credentials.Authentication != TargetAuthentication.HostedOauth)
            throw new ArgumentException("Connection management requires hosted OAuth discovery and matching credentials.");
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

    // Native's shared TargetHttpProblem vocabulary (contract/http_error.rs) for refusals before any effect.
    // Conflict, rate limiting, unavailability and other received failures leave the effect unknown.
    private static bool IsConnectionRefusal(NativeHttpException failure) =>
        (failure.StatusCode, failure.Problem!.Code) is
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
        => client.MutateConnectionAsync<ConnectionMutationResult>(SetOperation, discovery, request, credentials, cancellationToken);

    /// <summary>Deletes one record once. Operational failures and cancellation return evidence.</summary>
    public Task<NativeAttempt<ConnectionDeleteResult>> DeleteAsync(TargetDiscoveryDocument discovery, ConnectionDeleteRequest request,
        TargetControlCredentials credentials, CancellationToken cancellationToken = default)
        => client.MutateConnectionAsync<ConnectionDeleteResult>(DeleteOperation, discovery, request, credentials, cancellationToken);
}
