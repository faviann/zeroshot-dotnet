using Zeroshot.Native.Contracts;
using Zeroshot.Native.Execution;

namespace Zeroshot.Native;

public sealed partial class NativeClient
{
    internal Task<T> ReadProfileAsync<T>(OperationDescriptor operation, Func<TargetRunProfileRoutes, string> route,
        TargetDiscoveryDocument discovery, TargetHttpContract request, TargetControlCredentials credentials,
        CancellationToken cancellationToken)
    {
        var (uri, body) = PrepareProfileCall(route, discovery, request, credentials);
        return ExecuteJsonAsync<T>(operation, uri, body, credentials, _ => { }, cancellationToken, configure: NoStore);
    }

    internal async Task<NativeAttempt<T>> MutateProfileAsync<T>(OperationDescriptor operation,
        Func<TargetRunProfileRoutes, string> route, TargetDiscoveryDocument discovery, TargetHttpContract request,
        TargetControlCredentials credentials, CancellationToken cancellationToken) where T : class
    {
        var (uri, body) = PrepareProfileCall(route, discovery, request, credentials);
        var (correlationId, outcome, response, failure) = await AttemptAsync<T>(operation, uri, body, credentials,
            IsHostedRefusal, cancellationToken, configure: NoStore).ConfigureAwait(false);
        return new NativeAttempt<T>(Origin, operation.Name, correlationId, outcome, response, failure);
    }

    // Native build_profiles_descriptor compiles all six routes. Invalid use throws before any request is sent.
    private (Uri Route, byte[] Body) PrepareProfileCall(Func<TargetRunProfileRoutes, string> route,
        TargetDiscoveryDocument discovery, TargetHttpContract request, TargetControlCredentials credentials)
    {
        var body = PrepareHostedCall(discovery, request, credentials);
        var wire = discovery.Extensions.RunProfiles
            ?? throw new ArgumentException("The target does not advertise profile management.");
        if (wire.Kind != NativeProfilesClient.Kind) throw new ArgumentException("Run-profile discovery is incompatible.");
        var baseUrl = NativeRoutes.CapabilityBaseUrl(Origin, wire.BaseUrl);
        var templates = wire.RouteTemplates;
        foreach (var template in new[] { templates.List, templates.Show, templates.Set, templates.Delete, templates.Default, templates.Run })
            _ = NativeRoutes.CompileLiteralRoute(baseUrl, template);
        return (NativeRoutes.CompileLiteralRoute(baseUrl, route(templates)), body);
    }
}

/// <summary>Hosted user/org run profiles at routes advertised by explicitly supplied discovery.</summary>
public sealed class NativeProfilesClient
{
    internal const string Kind = "zeroshot.run-profiles/v1";
    // List, show and set results carry profile graphs and runtimes, so they keep the transport response ceiling.
    internal static readonly OperationDescriptor ListOperation = new("run_profiles.list", OperationTransport.Http);
    internal static readonly OperationDescriptor ShowOperation = new("run_profiles.show", OperationTransport.Http);
    internal static readonly OperationDescriptor SetOperation = new("run_profiles.set", OperationTransport.Http);
    internal static readonly OperationDescriptor DeleteOperation = new("run_profiles.delete", OperationTransport.Http, responseBytes: 64 * 1024);
    internal static readonly OperationDescriptor DefaultOperation = new("run_profiles.default", OperationTransport.Http, responseBytes: 64 * 1024);
    internal static readonly OperationDescriptor RunOperation = new("run_profiles.run", OperationTransport.Http, responseBytes: 64 * 1024);
    private readonly NativeClient client;
    internal NativeProfilesClient(NativeClient client) => this.client = client;

    /// <summary>Reads profile summaries for one scope. Failures throw NativeHttpException.</summary>
    public Task<RunProfileListResult> ListAsync(TargetDiscoveryDocument discovery, RunProfileListRequest request,
        TargetControlCredentials credentials, CancellationToken cancellationToken = default)
        => client.ReadProfileAsync<RunProfileListResult>(ListOperation, routes => routes.List, discovery, request, credentials, cancellationToken);

    /// <summary>Reads one complete profile. Failures throw NativeHttpException.</summary>
    public Task<RunProfile> ShowAsync(TargetDiscoveryDocument discovery, RunProfileSelector selector,
        TargetControlCredentials credentials, CancellationToken cancellationToken = default)
        => client.ReadProfileAsync<RunProfile>(ShowOperation, routes => routes.Show, discovery, selector, credentials, cancellationToken);

    /// <summary>Stores a profile once. Operational failures and cancellation return evidence.</summary>
    public Task<NativeAttempt<RunProfileMutationResult>> SetAsync(TargetDiscoveryDocument discovery, RunProfileSetRequest request,
        TargetControlCredentials credentials, CancellationToken cancellationToken = default)
        => client.MutateProfileAsync<RunProfileMutationResult>(SetOperation, routes => routes.Set, discovery, request, credentials, cancellationToken);

    /// <summary>Deletes one profile once. Operational failures and cancellation return evidence.</summary>
    public Task<NativeAttempt<RunProfileDeleteResult>> DeleteAsync(TargetDiscoveryDocument discovery, RunProfileSelector selector,
        TargetControlCredentials credentials, CancellationToken cancellationToken = default)
        => client.MutateProfileAsync<RunProfileDeleteResult>(DeleteOperation, routes => routes.Delete, discovery, selector, credentials, cancellationToken);

    /// <summary>Selects or, with no name, clears the scope's default once. Operational failures and cancellation return evidence.</summary>
    public Task<NativeAttempt<RunProfileDefaultResult>> DefaultAsync(TargetDiscoveryDocument discovery, RunProfileDefaultRequest request,
        TargetControlCredentials credentials, CancellationToken cancellationToken = default)
        => client.MutateProfileAsync<RunProfileDefaultResult>(DefaultOperation, routes => routes.Default, discovery, request, credentials, cancellationToken);

    /// <summary>Submits one run from a stored profile once. The acknowledged run ID can differ from the proposed one.</summary>
    public Task<NativeAttempt<TargetRunReceipt>> RunAsync(TargetDiscoveryDocument discovery, RunProfileRunRequest request,
        TargetControlCredentials credentials, CancellationToken cancellationToken = default)
        => client.MutateProfileAsync<TargetRunReceipt>(RunOperation, routes => routes.Run, discovery, request, credentials, cancellationToken);
}
