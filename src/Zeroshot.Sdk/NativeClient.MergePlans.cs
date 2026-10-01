using System.Text.Json;
using Zeroshot.Native.Contracts;
using Zeroshot.Native.Execution;

namespace Zeroshot.Native;

/// <summary>Hosted merge plans at routes advertised by explicitly supplied discovery. No polling, retry or token refresh.</summary>
public sealed class NativeMergePlansClient
{
    internal const string Kind = "zeroshot.merge-plans/v1";
    private const string PlanIdVariable = "{plan_id}";
    // Native MAX_MERGE_PLAN_RESPONSE_BYTES bounds every merge-plan result.
    private const int MaxBytes = 1024 * 1024;
    private static readonly HttpBinding<MergePlan> Create = new(
        new("merge_plans.create", OperationTransport.Http, responseBytes: MaxBytes), RequestHeaders.NoStore, NativeClient.IsHostedRefusal);
    private static readonly HttpBinding<MergePlan> Status = new(
        new("merge_plans.status", OperationTransport.Http, responseBytes: MaxBytes), RequestHeaders.Json, identity: SamePlan);
    private static readonly HttpBinding<MergePlan> Force = new(
        new("merge_plans.force", OperationTransport.Http, responseBytes: MaxBytes), RequestHeaders.NoStore, NativeClient.IsHostedRefusal, SamePlan);
    private readonly NativeClient client;
    internal NativeMergePlansClient(NativeClient client) => this.client = client;

    /// <summary>Submits one plan once. Operational failures and cancellation return evidence.</summary>
    public Task<NativeAttempt<MergePlan>> CreateAsync(TargetDiscoveryDocument discovery, MergePlanSubmitRequest request,
        TargetControlCredentials credentials, CancellationToken cancellationToken = default)
        => client.MutateAsync(Create, () =>
        {
            var body = NativeClient.HostedBody(discovery, request, credentials);
            return new HttpCall(Route(discovery, r => r.Create, planId: null), body);
        }, credentials, cancellationToken);

    /// <summary>Reads the plan's current state. Failures throw NativeHttpException.</summary>
    public Task<MergePlan> StatusAsync(TargetDiscoveryDocument discovery, RunId planId,
        TargetControlCredentials credentials, CancellationToken cancellationToken = default)
        => client.ReadAsync(Status, () => Addressed(discovery, credentials, r => r.Status, planId), credentials, cancellationToken, planId);

    /// <summary>Requests a force-stop once; the returned plan need not be terminal. Operational failures and cancellation return evidence.</summary>
    public Task<NativeAttempt<MergePlan>> ForceAsync(TargetDiscoveryDocument discovery, RunId planId,
        TargetControlCredentials credentials, CancellationToken cancellationToken = default)
        => client.MutateAsync(Force, () => new HttpCall(Addressed(discovery, credentials, r => r.Force, planId), "{}"u8.ToArray()),
            credentials, cancellationToken, planId);

    // A plan answered for another ID is foreign data, never this plan's state.
    private static void SamePlan(MergePlan plan, RunId planId)
    {
        if (plan.PlanId != planId) throw new JsonException();
    }

    // An existing plan's route, after the hosted gate.
    private Uri Addressed(TargetDiscoveryDocument discovery, TargetControlCredentials credentials,
        Func<TargetMergePlanRoutes, string> select, RunId planId)
    {
        NativeClient.AdmitHosted(discovery, credentials);
        ArgumentNullException.ThrowIfNull(planId);
        return Route(discovery, select, planId);
    }

    // Native build_merge_plans_descriptor compiles all three routes.
    private Uri Route(TargetDiscoveryDocument discovery, Func<TargetMergePlanRoutes, string> select, RunId? planId)
    {
        // Native pushes the opaque ID as one path segment; IDs it would drop or rewrite cannot address the plan.
        if (planId is not null && !NativeRoutes.IsAddressableSegment(planId.Value))
            throw new ArgumentException("The plan ID cannot address a single route segment.", nameof(planId));
        var wire = NativeClient.Advertised(discovery.Extensions.MergePlans, w => w.Kind, Kind,
            "The target does not advertise merge plans.", "Merge-plan discovery is incompatible.");
        var baseUrl = NativeRoutes.CapabilityBaseUrl(client.Origin, wire.BaseUrl);
        var routes = wire.RouteTemplates;
        _ = NativeRoutes.RunIdPath(routes.Create, false, query: null, PlanIdVariable);
        _ = NativeRoutes.RunIdPath(routes.Status, true, query: null, PlanIdVariable);
        _ = NativeRoutes.RunIdPath(routes.Force, true, query: null, PlanIdVariable);
        return NativeRoutes.VariableRoute(baseUrl, select(routes), PlanIdVariable, planId?.Value, query: null, []);
    }
}
