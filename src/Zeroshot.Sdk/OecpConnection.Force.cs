using Zeroshot.Native.Contracts;

namespace Zeroshot.Native;

public sealed partial class OecpRunsClient
{
    private const string ForceMethod = "run/force";

    /// <summary>Sends one native force request as a control call. An acknowledged status can still be stopping; there is no retry or wait.</summary>
    public Task<NativeAttempt<RunForceResult>> ForceAsync(RunId runId, OecpRequest? request = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runId);
        return ForceAsync(runId, request, null, cancellationToken);
    }

    internal Task<NativeAttempt<RunForceResult>> ForceAsync(RunId runId, OecpRequest? request,
        Action? afterCapture, CancellationToken cancellationToken)
        => connection.AttemptAsync<RunForceResult>(ForceMethod, NativeJson.SerializeUtf8(new RunForceParams { RunId = runId }),
            result => result.Require(runId), IsForceRefusal, control: true,
            request, cancellationToken, afterCapture);

    // Pinned native answers these before force_stop or request_force_stop; NOT_FOUND is the
    // ledger lookup in NativeV2CloudController::prepare_force (native_v2_cloud.rs#L648-L667).
    private static bool IsForceRefusal(JsonRpcError error) =>
        OecpConnection.IsDispatchRefusal(error) || error is { Code: -32000, Data.Code: "NOT_FOUND" };
}
