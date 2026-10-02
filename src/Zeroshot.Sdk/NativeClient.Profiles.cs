using Zeroshot.Native.Contracts;
using Zeroshot.Native.Execution;

namespace Zeroshot.Native;

/// <summary>Hosted user/org run profiles at routes advertised by explicitly supplied discovery.</summary>
public sealed class NativeProfilesClient
{
    internal const string Kind = "zeroshot.run-profiles/v1";
    // Native hosted_json reads every profile result under its 64 KiB MAX_RESPONSE_BYTES.
    private const int MaxBytes = 64 * 1024;
    private static readonly HttpBinding<RunProfileListResult> List = Read<RunProfileListResult>("run_profiles.list");
    private static readonly HttpBinding<RunProfile> Show = Read<RunProfile>("run_profiles.show");
    private static readonly HttpBinding<RunProfileMutationResult> Set = Mutate<RunProfileMutationResult>("run_profiles.set");
    private static readonly HttpBinding<RunProfileDeleteResult> Delete = Mutate<RunProfileDeleteResult>("run_profiles.delete");
    private static readonly HttpBinding<RunProfileDefaultResult> Default = Mutate<RunProfileDefaultResult>("run_profiles.default");
    private static readonly HttpBinding<TargetRunReceipt> Run = Mutate<TargetRunReceipt>("run_profiles.run");
    private readonly NativeClient client;
    internal NativeProfilesClient(NativeClient client) => this.client = client;

    private static HttpBinding<T> Read<T>(string name)
        => new(new(name, OperationTransport.Http, responseBytes: MaxBytes), RequestHeaders.NoStore);
    private static HttpBinding<T> Mutate<T>(string name)
        => new(new(name, OperationTransport.Http, responseBytes: MaxBytes), RequestHeaders.NoStore, NativeClient.IsHostedRefusal);

    /// <summary>Reads profile summaries for one scope. Failures throw NativeHttpException.</summary>
    public Task<RunProfileListResult> ListAsync(TargetDiscoveryDocument discovery, RunProfileListRequest request,
        TargetControlCredentials credentials, CancellationToken cancellationToken = default)
        => client.ReadAsync(List, () => Route(discovery, request, credentials, r => r.List), credentials, cancellationToken);

    /// <summary>Reads one complete profile. Failures throw NativeHttpException.</summary>
    public Task<RunProfile> ShowAsync(TargetDiscoveryDocument discovery, RunProfileSelector selector,
        TargetControlCredentials credentials, CancellationToken cancellationToken = default)
        => client.ReadAsync(Show, () => Route(discovery, selector, credentials, r => r.Show), credentials, cancellationToken);

    /// <summary>Stores a profile once. Operational failures and cancellation return evidence.</summary>
    public Task<NativeAttempt<RunProfileMutationResult>> SetAsync(TargetDiscoveryDocument discovery, RunProfileSetRequest request,
        TargetControlCredentials credentials, CancellationToken cancellationToken = default)
        => client.MutateAsync(Set, () => Route(discovery, request, credentials, r => r.Set), credentials, cancellationToken);

    /// <summary>Deletes one profile once. Operational failures and cancellation return evidence.</summary>
    public Task<NativeAttempt<RunProfileDeleteResult>> DeleteAsync(TargetDiscoveryDocument discovery, RunProfileSelector selector,
        TargetControlCredentials credentials, CancellationToken cancellationToken = default)
        => client.MutateAsync(Delete, () => Route(discovery, selector, credentials, r => r.Delete), credentials, cancellationToken);

    /// <summary>Selects or, with no name, clears the scope's default once. Operational failures and cancellation return evidence.</summary>
    public Task<NativeAttempt<RunProfileDefaultResult>> DefaultAsync(TargetDiscoveryDocument discovery, RunProfileDefaultRequest request,
        TargetControlCredentials credentials, CancellationToken cancellationToken = default)
        => client.MutateAsync(Default, () => Route(discovery, request, credentials, r => r.Default), credentials, cancellationToken);

    /// <summary>Submits one run from a stored profile once. The acknowledged run ID can differ from the proposed one.</summary>
    public Task<NativeAttempt<TargetRunReceipt>> RunAsync(TargetDiscoveryDocument discovery, RunProfileRunRequest request,
        TargetControlCredentials credentials, CancellationToken cancellationToken = default)
        => client.MutateAsync(Run, () => Route(discovery, request, credentials, r => r.Run), credentials, cancellationToken);

    // Native build_profiles_descriptor compiles all six routes.
    private HttpCall Route(TargetDiscoveryDocument discovery, TargetHttpContract request, TargetControlCredentials credentials,
        Func<TargetRunProfileRoutes, string> select)
    {
        var body = NativeClient.HostedBody(discovery, request, credentials);
        var wire = NativeClient.Advertised(discovery.Extensions.RunProfiles, w => w.Kind, Kind,
            "The target does not advertise profile management.", "Run-profile discovery is incompatible.");
        var baseUrl = NativeRoutes.CapabilityBaseUrl(client.Origin, wire.BaseUrl);
        var routes = wire.RouteTemplates;
        foreach (var template in new[] { routes.List, routes.Show, routes.Set, routes.Delete, routes.Default, routes.Run })
            _ = NativeRoutes.CompileLiteralRoute(baseUrl, template);
        return new(NativeRoutes.CompileLiteralRoute(baseUrl, select(routes)), body);
    }
}
