using Zeroshot.Native.Contracts;
using Zeroshot.Native.Execution;

namespace Zeroshot.Native;

public sealed partial class NativeClient
{
    private NativeHostedRecoveryClient? hostedRecovery;
    /// <summary>Hosted checkpoints and workspace recovery at routes advertised by explicitly supplied discovery.</summary>
    public NativeHostedRecoveryClient HostedRecovery => hostedRecovery ??= new NativeHostedRecoveryClient(this);

    internal Task<RunCheckpointsResult> HostedCheckpointsAsync(TargetDiscoveryDocument discovery,
        RunCheckpointsParams parameters, TargetControlCredentials credentials, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var uri = HostedRecoveryRoute(discovery, credentials, routes => routes.Checkpoints, parameters.RunId);
        return ExecuteJsonAsync<RunCheckpointsResult>(NativeHostedRecoveryClient.CheckpointsOperation, uri,
            NativeJson.SerializeUtf8(parameters), credentials, parameters.RequirePage, cancellationToken, configure: NoStore);
    }

    internal async Task<NativeAttempt<RunResumeResult>> HostedResumeAsync(TargetDiscoveryDocument discovery, RunId runId,
        RunId successorRunId, TargetControlCredentials credentials, RunResumeFrom? from, TargetRunCredentials? runCredentials,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(successorRunId);
        var uri = HostedRecoveryRoute(discovery, credentials, routes => routes.Resume, runId);
        var body = RunResumeParams.SerializeUtf8(runId, successorRunId, from, runCredentials);
        var operation = NativeHostedRecoveryClient.ResumeOperation;
        return await AttemptAsync<RunResumeResult>(operation, uri, body, credentials,
            IsHostedRefusal, cancellationToken, configure: NoStore,
            validate: result => result.Require(runId, successorRunId)).ConfigureAwait(false);
    }

    internal async Task<NativeAttempt<RunDiscardWorkspaceResult>> HostedDiscardWorkspaceAsync(TargetDiscoveryDocument discovery,
        RunId runId, TargetControlCredentials credentials, CancellationToken cancellationToken)
    {
        var uri = HostedRecoveryRoute(discovery, credentials, routes => routes.DiscardWorkspace, runId);
        var operation = NativeHostedRecoveryClient.DiscardWorkspaceOperation;
        return await AttemptAsync<RunDiscardWorkspaceResult>(operation, uri,
            NativeJson.SerializeUtf8(new RunDiscardWorkspaceParams { RunId = runId }), credentials, IsHostedRefusal,
            cancellationToken, configure: NoStore, validate: result => result.Require(runId)).ConfigureAwait(false);
    }

    // Native compiles the recovery routes within the hosted-runs descriptor (contract/hosted_runs.rs): they append
    // to its base_url, and each has one whole {run_id} segment and no query.
    // Invalid use throws before any request is sent.
    private Uri HostedRecoveryRoute(TargetDiscoveryDocument discovery, TargetControlCredentials credentials,
        Func<TargetHostedWorkspaceRecoveryRoutes, string> select, RunId runId)
    {
        ArgumentNullException.ThrowIfNull(runId);
        var baseUrl = HostedRunsBase(discovery, credentials, runId);
        var wire = discovery.Extensions.HostedWorkspaceRecovery
            ?? throw new ArgumentException("The target does not advertise hosted workspace recovery.");
        if (wire.Kind != NativeHostedRecoveryClient.Kind) throw new ArgumentException("Hosted workspace recovery discovery is incompatible.");
        var routes = wire.RouteTemplates;
        _ = NativeRoutes.RunIdPath(routes.Resume, requiresRunId: true, query: null);
        _ = NativeRoutes.RunIdPath(routes.Checkpoints, requiresRunId: true, query: null);
        _ = NativeRoutes.RunIdPath(routes.DiscardWorkspace, requiresRunId: true, query: null);
        return NativeRoutes.RunIdRoute(baseUrl, select(routes), runId.Value, query: null);
    }
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
    internal static readonly OperationDescriptor CheckpointsOperation = new("hosted_workspace_recovery.checkpoints", OperationTransport.Http, responseBytes: MaxBytes);
    internal static readonly OperationDescriptor ResumeOperation = new("hosted_workspace_recovery.resume", OperationTransport.Http, responseBytes: MaxBytes);
    internal static readonly OperationDescriptor DiscardWorkspaceOperation = new("hosted_workspace_recovery.discard_workspace", OperationTransport.Http, responseBytes: MaxBytes);
    private readonly NativeClient client;
    internal NativeHostedRecoveryClient(NativeClient client) => this.client = client;

    /// <summary>Reads one checkpoint page with the same page contract as OECP. Failures throw NativeHttpException.</summary>
    public Task<RunCheckpointsResult> CheckpointsAsync(TargetDiscoveryDocument discovery, RunCheckpointsParams parameters,
        TargetControlCredentials credentials, CancellationToken cancellationToken = default)
        => client.HostedCheckpointsAsync(discovery, parameters, credentials, cancellationToken);

    /// <summary>
    /// Sends one request to admit <paramref name="successorRunId"/> from the retained workspace, or from a checkpoint
    /// when selected. Fresh run credentials are sent only with this attempt. Operational failures and cancellation
    /// return evidence; after an unknown outcome, the source run's status reports any recorded successor.
    /// </summary>
    public Task<NativeAttempt<RunResumeResult>> ResumeAsync(TargetDiscoveryDocument discovery, RunId runId, RunId successorRunId,
        TargetControlCredentials credentials, RunResumeFrom? from = null, TargetRunCredentials? runCredentials = null,
        CancellationToken cancellationToken = default)
        => client.HostedResumeAsync(discovery, runId, successorRunId, credentials, from, runCredentials, cancellationToken);

    /// <summary>Sends one request to destroy the retained recovery workspace. The run and its history remain.</summary>
    public Task<NativeAttempt<RunDiscardWorkspaceResult>> DiscardWorkspaceAsync(TargetDiscoveryDocument discovery, RunId runId,
        TargetControlCredentials credentials, CancellationToken cancellationToken = default)
        => client.HostedDiscardWorkspaceAsync(discovery, runId, credentials, cancellationToken);
}
