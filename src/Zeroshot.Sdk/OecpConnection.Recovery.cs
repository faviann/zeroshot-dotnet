using Zeroshot.Native.Contracts;

namespace Zeroshot.Native;

public sealed partial class OecpRunsClient
{
    private const string ResumeMethod = "run/resume";
    private const string DiscardWorkspaceMethod = "run/discard_workspace";

    /// <summary>Reads one checkpoint page. Native refuses it with INVALID_PHASE where workspace checkpoints are unsupported.</summary>
    public Task<RunCheckpointsResult> CheckpointsAsync(RunCheckpointsParams parameters, OecpRequest? request = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return connection.CallAsync<RunCheckpointsResult>("run/checkpoints", NativeJson.SerializeUtf8(parameters),
            parameters.RequirePage, cancellationToken, request);
    }

    /// <summary>
    /// Sends one request to admit a successor from the retained workspace, or from a checkpoint when selected. Fresh
    /// credentials are sent only with this attempt. Native does not deduplicate a repeated resume; after an unknown
    /// outcome, the source run's status reports any recorded successor.
    /// </summary>
    public Task<NativeAttempt<RunResumeResult>> ResumeAsync(RunId runId, RunId successorRunId, RunResumeFrom? from = null,
        TargetRunCredentials? credentials = null, OecpRequest? request = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runId);
        ArgumentNullException.ThrowIfNull(successorRunId);
        var parameters = RunResumeParams.SerializeUtf8(runId, successorRunId, from, credentials);
        return connection.AttemptAsync<RunResumeResult>(ResumeMethod, parameters, result => result.Require(runId, successorRunId),
            IsResumeRefusal, control: false, request, cancellationToken);
    }

    /// <summary>Sends one request to destroy the retained recovery workspace. The run and its history remain.</summary>
    public Task<NativeAttempt<RunDiscardWorkspaceResult>> DiscardWorkspaceAsync(RunId runId, OecpRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runId);
        return connection.AttemptAsync<RunDiscardWorkspaceResult>(DiscardWorkspaceMethod, NativeJson.SerializeUtf8(new RunDiscardWorkspaceParams { RunId = runId }),
            result => result.Require(runId), IsDiscardRefusal, control: false, request, cancellationToken);
    }

    // The target's capability gate (INVALID_PHASE) and canonical-ID check (SCHEMA_VIOLATION) run before the controller;
    // the controller's first step is the ledger lookup (NOT_FOUND). Resume's IDEMPOTENCY_REUSE is the ledger refusing
    // to create the successor. INTERNAL_ERROR can follow successor creation or partial cleanup, so it stays unknown.
    // See native_v2_target_authority/transport.rs#L583-L654 and native_v2_cloud.rs#L390-L497.
    private static bool IsDiscardRefusal(JsonRpcError error) => OecpConnection.IsDispatchRefusal(error) ||
        (error.Code, error.Data?.Code) is (-32000, "INVALID_PHASE" or "NOT_FOUND");
    private static bool IsResumeRefusal(JsonRpcError error) => IsDiscardRefusal(error) || (error.Code, error.Data?.Code) is (-32000, "IDEMPOTENCY_REUSE");
}
