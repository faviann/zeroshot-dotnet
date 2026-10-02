using System.Text;
using Zeroshot.Native.Contracts;
using Zeroshot.Native.Execution;

namespace Zeroshot.Native;

/// <summary>Hosted user/org connection records at routes advertised by explicitly supplied discovery.</summary>
public sealed class NativeConnectionsClient
{
    internal const string Kind = "zeroshot.connections/v1";
    private const int MaxBytes = 64 * 1024;
    private static readonly HttpBinding<ConnectionListResult> List = new(
        new("connections.list", OperationTransport.Http, responseBytes: MaxBytes), RequestHeaders.NoStore);
    private static readonly HttpBinding<ConnectionMutationResult> Set = new(
        new("connections.set", OperationTransport.Http, responseBytes: MaxBytes), RequestHeaders.NoStore, NativeClient.IsHostedRefusal);
    private static readonly HttpBinding<ConnectionDeleteResult> Delete = new(
        new("connections.delete", OperationTransport.Http, responseBytes: MaxBytes), RequestHeaders.NoStore, NativeClient.IsHostedRefusal);
    private readonly NativeClient client;
    internal NativeConnectionsClient(NativeClient client) => this.client = client;

    /// <summary>Reads secret-free summaries. Failures throw NativeHttpException.</summary>
    public Task<ConnectionListResult> ListAsync(TargetDiscoveryDocument discovery, ConnectionListRequest request,
        TargetControlCredentials credentials, CancellationToken cancellationToken = default)
        => client.ReadAsync(List, () => Route(discovery, request, credentials, r => r.List), credentials, cancellationToken);

    /// <summary>Stores static values once. Operational failures and cancellation return evidence.</summary>
    public Task<NativeAttempt<ConnectionMutationResult>> SetAsync(TargetDiscoveryDocument discovery, ConnectionSetRequest request,
        TargetControlCredentials credentials, CancellationToken cancellationToken = default)
        => client.MutateAsync(Set, () => Route(discovery, request, credentials, r => r.Set), credentials, cancellationToken);

    /// <summary>Deletes one record once. Operational failures and cancellation return evidence.</summary>
    public Task<NativeAttempt<ConnectionDeleteResult>> DeleteAsync(TargetDiscoveryDocument discovery, ConnectionDeleteRequest request,
        TargetControlCredentials credentials, CancellationToken cancellationToken = default)
        => client.MutateAsync(Delete, () => Route(discovery, request, credentials, r => r.Delete), credentials, cancellationToken);

    // Native build_connections_descriptor.
    private HttpCall Route(TargetDiscoveryDocument discovery, TargetHttpContract request, TargetControlCredentials credentials,
        Func<TargetConnectionRoutes, string> select)
    {
        var body = NativeClient.HostedBody(discovery, request, credentials);
        const string incompatible = "Connection discovery is incompatible.";
        var wire = NativeClient.Advertised(discovery.Extensions.Connections, w => w.Kind, Kind,
            "The target does not advertise connection management.", incompatible);
        var kinds = wire.DynamicKinds;
        if (kinds.Distinct(StringComparer.Ordinal).Count() != kinds.Length ||
            kinds.Any(kind => kind.Length == 0 || Encoding.UTF8.GetByteCount(kind) > 128 || kind.Any(char.IsControl)))
            throw new ArgumentException(incompatible);
        var baseUrl = NativeRoutes.CapabilityBaseUrl(client.Origin, wire.BaseUrl);
        var routes = wire.RouteTemplates;
        // The run-scoped resolver is a host callback, not a caller operation; its declaration must still be valid.
        foreach (var template in new[] { routes.Resolve, routes.List, routes.Set, routes.Delete })
            _ = NativeRoutes.CompileLiteralRoute(baseUrl, template);
        return new(NativeRoutes.CompileLiteralRoute(baseUrl, select(routes)), body);
    }
}
