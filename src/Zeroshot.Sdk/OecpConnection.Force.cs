using System.Text.Json;
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

    // afterCapture lets tests place cancellation between capture and operation completion. That placement is
    // deterministic only because the cancelled WaitAsync continuation runs inline during Cancel().
    internal async Task<NativeAttempt<RunForceResult>> ForceAsync(RunId runId, OecpRequest? request,
        Action? afterCapture, CancellationToken cancellationToken)
    {
        var parameters = NativeJson.SerializeUtf8(new RunForceParams { RunId = runId });
        var correlationId = Guid.Empty;
        RunForceResult? acknowledged = null;
        Exception? failure = null;
        var sendStarted = false;
        try
        {
            await connection.CallAsync<RunForceResult>(ForceMethod, parameters, result => { if (result.RunId != runId) throw new JsonException(); },
                cancellationToken, request, control: true, onResponse: (id, result) =>
                {
                    correlationId = id;
                    Volatile.Write(ref acknowledged, result);
                    afterCapture?.Invoke();
                }).ConfigureAwait(false);
        }
        catch (NativeOecpException error) { (failure, correlationId, sendStarted) = (error, error.CorrelationId, error.Dispatch.SendStarted); }
        catch (OecpOperationCanceledException error) { (failure, correlationId, sendStarted) = (error, error.CorrelationId, error.Dispatch.SendStarted); }

        // Cancellation can end the call after validation but before the operation completes.
        var captured = Volatile.Read(ref acknowledged);
        var outcome = captured is not null ? NativeAttemptOutcome.Acknowledged
            : !sendStarted ? NativeAttemptOutcome.NotSent
            : failure is NativeOecpException { RpcError: { } rpcError } && IsForceRefusal(rpcError) ? NativeAttemptOutcome.Rejected
            : NativeAttemptOutcome.Unknown;
        return new(connection.Origin, ForceMethod, correlationId, outcome, captured, captured is null ? failure : null);
    }

    // Pinned native answers these before force_stop or request_force_stop; NOT_FOUND is the
    // ledger lookup in NativeV2CloudController::prepare_force (native_v2_cloud.rs#L648-L667).
    private static bool IsForceRefusal(JsonRpcError error) => (error.Code, error.Data?.Code) is
        (-32601, _) or
        (-32602, "SCHEMA_VIOLATION") or
        (-32600, "DUPLICATE_REQUEST_ID") or
        (-32000, "SERVER_BUSY" or "NOT_FOUND");
}
