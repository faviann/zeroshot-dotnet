using Zeroshot.Native.Contracts;
using Zeroshot.Native.Execution;

namespace Zeroshot.Native;

public sealed partial class NativeClient
{
    private NativeHostedRecoveryClient? hostedRecovery;
    /// <summary>Hosted checkpoints and workspace recovery at routes advertised by explicitly supplied discovery.</summary>
    public NativeHostedRecoveryClient HostedRecovery => hostedRecovery ??= new NativeHostedRecoveryClient(this);
}

/// <summary>
/// The hosted <c>openengine.hosted-workspace-recovery/v1</c> capability (native hosted_runs/recovery_http.rs): the
/// OECP checkpoint and recovery contracts over the caller's hosted OAuth access bearer. Direct and private targets
/// recover over OECP instead. There is no rediscovery, token refresh, retry or reconciliation.
/// </summary>
public sealed class NativeHostedRecoveryClient
{
    internal const string Kind = "openengine.hosted-workspace-recovery/v1";
    // Native hosted_json reads every result under its 64 KiB MAX_RESPONSE_BYTES.
    private const int MaxBytes = 64 * 1024;
    private static readonly HttpBinding<RunCheckpointsResult> Checkpoints = new(
        new("hosted_workspace_recovery.checkpoints", OperationTransport.Http, responseBytes: MaxBytes), RequestHeaders.NoStore);
    private static readonly HttpBinding<RunResumeResult> Resume = new(
        new("hosted_workspace_recovery.resume", OperationTransport.Http, responseBytes: MaxBytes), RequestHeaders.NoStore, NativeClient.IsHostedRefusal);
    private static readonly HttpBinding<RunDiscardWorkspaceResult> DiscardWorkspace = new(
        new("hosted_workspace_recovery.discard_workspace", OperationTransport.Http, responseBytes: MaxBytes), RequestHeaders.NoStore,
        NativeClient.IsHostedRefusal, (result, runId) => result.Require(runId));
    private readonly NativeClient client;
    internal NativeHostedRecoveryClient(NativeClient client) => this.client = client;

    /// <summary>Reads one checkpoint page with the same page contract as OECP. Failures throw NativeHttpException.</summary>
    public Task<RunCheckpointsResult> CheckpointsAsync(TargetDiscoveryDocument discovery, RunCheckpointsParams parameters,
        TargetControlCredentials credentials, CancellationToken cancellationToken = default)
        => client.ReadAsync(Checkpoints, () =>
        {
            ArgumentNullException.ThrowIfNull(parameters);
            return new HttpCall(Route(discovery, credentials, r => r.Checkpoints, parameters.RunId), NativeJson.SerializeUtf8(parameters));
        }, credentials, cancellationToken, validate: result => parameters.RequirePage(result));

    /// <summary>
    /// Sends one request to admit <paramref name="successorRunId"/> from the retained workspace, or from a checkpoint
    /// when selected. Fresh run credentials are sent only with this attempt. Operational failures and cancellation
    /// return evidence; after an unknown outcome, the source run's status reports any recorded successor.
    /// </summary>
    public Task<NativeAttempt<RunResumeResult>> ResumeAsync(TargetDiscoveryDocument discovery, RunId runId, RunId successorRunId,
        TargetControlCredentials credentials, RunResumeFrom? from = null, TargetRunCredentials? runCredentials = null,
        CancellationToken cancellationToken = default)
        => client.MutateAsync(Resume, () =>
        {
            ArgumentNullException.ThrowIfNull(successorRunId);
            return new HttpCall(Route(discovery, credentials, r => r.Resume, runId),
                RunResumeParams.SerializeUtf8(runId, successorRunId, from, runCredentials));
        }, credentials, cancellationToken, validate: result => result.Require(runId, successorRunId));

    /// <summary>Sends one request to destroy the retained recovery workspace. The run and its history remain.</summary>
    public Task<NativeAttempt<RunDiscardWorkspaceResult>> DiscardWorkspaceAsync(TargetDiscoveryDocument discovery, RunId runId,
        TargetControlCredentials credentials, CancellationToken cancellationToken = default)
        => client.MutateAsync(DiscardWorkspace, () => new HttpCall(Route(discovery, credentials, r => r.DiscardWorkspace, runId),
            NativeJson.SerializeUtf8(new RunDiscardWorkspaceParams { RunId = runId })), credentials, cancellationToken, runId);

    // Native compiles the recovery routes within the hosted-runs descriptor (contract/hosted_runs.rs): they append
    // to its base_url, and each has one whole {run_id} segment and no query.
    private Uri Route(TargetDiscoveryDocument discovery, TargetControlCredentials credentials,
        Func<TargetHostedWorkspaceRecoveryRoutes, string> select, RunId runId)
    {
        ArgumentNullException.ThrowIfNull(runId);
        var baseUrl = NativeHostedRunsClient.Base(client.Origin, discovery, credentials, runId);
        var routes = NativeClient.Advertised(discovery.Extensions.HostedWorkspaceRecovery, w => w.Kind, Kind,
            "The target does not advertise hosted workspace recovery.", "Hosted workspace recovery discovery is incompatible.").RouteTemplates;
        _ = NativeRoutes.RunIdPath(routes.Resume, requiresRunId: true, query: null);
        _ = NativeRoutes.RunIdPath(routes.Checkpoints, requiresRunId: true, query: null);
        _ = NativeRoutes.RunIdPath(routes.DiscardWorkspace, requiresRunId: true, query: null);
        return NativeRoutes.RunIdRoute(baseUrl, select(routes), runId.Value, query: null);
    }
}
