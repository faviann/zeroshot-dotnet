using System.Net.Http.Headers;
using System.Text.Json;
using Zeroshot.Native.Contracts;
using Zeroshot.Native.Execution;

namespace Zeroshot.Native;

public sealed partial class NativeClient
{
    internal async Task<NativeAttempt<MergePlan>> CreateMergePlanAsync(TargetDiscoveryDocument discovery,
        MergePlanSubmitRequest request, TargetControlCredentials credentials, CancellationToken cancellationToken)
    {
        var body = PrepareHostedCall(discovery, request, credentials);
        var uri = MergePlanRoute(discovery, routes => routes.Create, planId: null);
        return await AttemptAsync<MergePlan>(NativeMergePlansClient.CreateOperation,
            uri, body, credentials, IsHostedRefusal, cancellationToken, configure: NoStore).ConfigureAwait(false);
    }

    internal Task<MergePlan> MergePlanStatusAsync(TargetDiscoveryDocument discovery, RunId planId,
        TargetControlCredentials credentials, CancellationToken cancellationToken)
    {
        ValidateHostedUse(discovery, credentials);
        ArgumentNullException.ThrowIfNull(planId);
        var uri = MergePlanRoute(discovery, routes => routes.Status, planId);
        return ExecuteJsonAsync<MergePlan>(NativeMergePlansClient.StatusOperation, uri, null, credentials,
            plan => RequirePlan(plan, planId), cancellationToken, configure: request =>
            {
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                NoStore(request);
            });
    }

    internal async Task<NativeAttempt<MergePlan>> ForceMergePlanAsync(TargetDiscoveryDocument discovery, RunId planId,
        TargetControlCredentials credentials, CancellationToken cancellationToken)
    {
        ValidateHostedUse(discovery, credentials);
        ArgumentNullException.ThrowIfNull(planId);
        var uri = MergePlanRoute(discovery, routes => routes.Force, planId);
        return await AttemptAsync<MergePlan>(NativeMergePlansClient.ForceOperation,
            uri, "{}"u8.ToArray(), credentials, IsHostedRefusal, cancellationToken, configure: NoStore,
            validate: plan => RequirePlan(plan, planId)).ConfigureAwait(false);
    }

    // A plan answered for another ID is foreign data, never this plan's state.
    private static void RequirePlan(MergePlan plan, RunId planId)
    {
        if (plan.PlanId != planId) throw new JsonException();
    }

    private const string PlanIdVariable = "{plan_id}";

    // Native build_merge_plans_descriptor compiles all three routes. Invalid use throws before any request is sent.
    private Uri MergePlanRoute(TargetDiscoveryDocument discovery, Func<TargetMergePlanRoutes, string> route, RunId? planId)
    {
        // Native pushes the opaque ID as one path segment; IDs it would drop or rewrite cannot address the plan.
        if (planId is not null && !NativeRoutes.IsAddressableSegment(planId.Value))
            throw new ArgumentException("The plan ID cannot address a single route segment.", nameof(planId));
        var wire = discovery.Extensions.MergePlans
            ?? throw new ArgumentException("The target does not advertise merge plans.");
        if (wire.Kind != NativeMergePlansClient.Kind) throw new ArgumentException("Merge-plan discovery is incompatible.");
        var baseUrl = NativeRoutes.CapabilityBaseUrl(Origin, wire.BaseUrl);
        var templates = wire.RouteTemplates;
        _ = NativeRoutes.RunIdPath(templates.Create, false, query: null, PlanIdVariable);
        _ = NativeRoutes.RunIdPath(templates.Status, true, query: null, PlanIdVariable);
        _ = NativeRoutes.RunIdPath(templates.Force, true, query: null, PlanIdVariable);
        return NativeRoutes.VariableRoute(baseUrl, route(templates), PlanIdVariable, planId?.Value, query: null, []);
    }
}

/// <summary>Hosted merge plans at routes advertised by explicitly supplied discovery. No polling, retry or token refresh.</summary>
public sealed class NativeMergePlansClient
{
    internal const string Kind = "zeroshot.merge-plans/v1";
    // Native MAX_MERGE_PLAN_RESPONSE_BYTES bounds every merge-plan result.
    internal static readonly OperationDescriptor CreateOperation = new("merge_plans.create", OperationTransport.Http, responseBytes: 1024 * 1024);
    internal static readonly OperationDescriptor StatusOperation = new("merge_plans.status", OperationTransport.Http, responseBytes: 1024 * 1024);
    internal static readonly OperationDescriptor ForceOperation = new("merge_plans.force", OperationTransport.Http, responseBytes: 1024 * 1024);
    private readonly NativeClient client;
    internal NativeMergePlansClient(NativeClient client) => this.client = client;

    /// <summary>Submits one plan once. Operational failures and cancellation return evidence.</summary>
    public Task<NativeAttempt<MergePlan>> CreateAsync(TargetDiscoveryDocument discovery, MergePlanSubmitRequest request,
        TargetControlCredentials credentials, CancellationToken cancellationToken = default)
        => client.CreateMergePlanAsync(discovery, request, credentials, cancellationToken);

    /// <summary>Reads the plan's current state. Failures throw NativeHttpException.</summary>
    public Task<MergePlan> StatusAsync(TargetDiscoveryDocument discovery, RunId planId,
        TargetControlCredentials credentials, CancellationToken cancellationToken = default)
        => client.MergePlanStatusAsync(discovery, planId, credentials, cancellationToken);

    /// <summary>Requests a force-stop once; the returned plan need not be terminal. Operational failures and cancellation return evidence.</summary>
    public Task<NativeAttempt<MergePlan>> ForceAsync(TargetDiscoveryDocument discovery, RunId planId,
        TargetControlCredentials credentials, CancellationToken cancellationToken = default)
        => client.ForceMergePlanAsync(discovery, planId, credentials, cancellationToken);
}
