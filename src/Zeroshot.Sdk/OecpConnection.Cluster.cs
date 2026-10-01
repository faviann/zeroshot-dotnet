using System.Text.Json;
using Zeroshot.Native.Contracts;

namespace Zeroshot.Native;

/// <summary>
/// Shared cluster contract bindings. Backend support varies: stock DirectTarget answers INVALID_PHASE for
/// everything except get, whatever initialize advertises. Nothing here converts cluster calls into run/* calls.
/// </summary>
public sealed class OecpClusterClient
{
    private readonly OecpConnection connection;
    internal OecpClusterClient(OecpConnection connection) => this.connection = connection;

    public Task<GetResult> GetAsync(GetParams? parameters = null, OecpRequest? request = null, CancellationToken cancellationToken = default)
        => connection.CallAsync<GetResult>("get", NativeJson.SerializeUtf8(parameters ?? new()), null, cancellationToken, request);

    /// <summary>Verifies a graph without admitting it.</summary>
    public Task<PlanResult> PlanAsync(PlanParams parameters, OecpRequest? request = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return connection.CallAsync<PlanResult>("plan", NativeJson.SerializeUtf8(parameters), null, cancellationToken, request);
    }

    /// <summary>One apply attempt, committed or dry run. There is no retry.</summary>
    public Task<NativeAttempt<ApplyResult>> ApplyAsync(ApplyParams parameters, OecpRequest? request = null, CancellationToken cancellationToken = default)
        => Attempt<ApplyResult>("apply", parameters, null, error => Refused(error, "GRAPH_INVALID", "GENERATION_CONFLICT",
            "IDEMPOTENCY_REUSE", "INVALID_PHASE", "CANCELLED"), request, cancellationToken);

    public Task<NativeAttempt<UpdateResult>> UpdateAsync(UpdateParams parameters, OecpRequest? request = null, CancellationToken cancellationToken = default)
        => Attempt<UpdateResult>("update", parameters, null, error => Refused(error, "GENERATION_CONFLICT",
            "IDEMPOTENCY_REUSE", "INVALID_PHASE"), request, cancellationToken);

    /// <summary>One cluster stop attempt as a control call. The effective mode can differ from the accepted mode.</summary>
    public Task<NativeAttempt<StopResult>> StopAsync(StopParams parameters, OecpRequest? request = null, CancellationToken cancellationToken = default)
        => Attempt<StopResult>("stop", parameters, parameters.Require,
            error => Refused(error, "GENERATION_CONFLICT", "IDEMPOTENCY_REUSE", "INVALID_PHASE"), request, cancellationToken, control: true);

    /// <summary>A NO_RETRYABLE_FRONTIER refusal exposes its reason through DomainErrorData.NoRetryableFrontierReason.</summary>
    public Task<NativeAttempt<RetryResult>> RetryAsync(RetryParams parameters, OecpRequest? request = null, CancellationToken cancellationToken = default)
        => Attempt<RetryResult>("retry", parameters, null, error => Refused(error, "GENERATION_CONFLICT",
            "IDEMPOTENCY_REUSE", "INVALID_PHASE", "NO_RETRYABLE_FRONTIER"), request, cancellationToken);

    public Task<NativeAttempt<ResubmitResult>> ResubmitAsync(ResubmitParams parameters, OecpRequest? request = null, CancellationToken cancellationToken = default)
        => Attempt<ResubmitResult>("resubmit", parameters, parameters.Require,
            error => Refused(error, "GENERATION_CONFLICT", "RUN_CONFLICT", "IDEMPOTENCY_REUSE", "INVALID_PHASE", "CANCELLED"),
            request, cancellationToken);

    public Task<NativeAttempt<DeleteResult>> DeleteAsync(DeleteParams parameters, OecpRequest? request = null, CancellationToken cancellationToken = default)
        => Attempt<DeleteResult>("delete", parameters, null, error => Refused(error, "GENERATION_CONFLICT",
            "RUN_CONFLICT", "IDEMPOTENCY_REUSE", "INVALID_PHASE", "CANCELLED"), request, cancellationToken);

    /// <summary>One durable cluster watch. Without a RunId it follows the run native resolves, including after parking.</summary>
    public Task<NativeSubscription<WatchResult, EventNotification>> WatchAsync(WatchParams? parameters = null,
        CancellationToken cancellationToken = default)
    {
        parameters ??= new();
        RunId? run = parameters.RunId;
        return connection.SubscribeAsync<WatchResult, EventNotification>("watch", NativeJson.SerializeUtf8(parameters), result =>
        {
            if (parameters.RunId is not null && result.RunId != parameters.RunId) throw new JsonException();
            run = result.RunId;
            return result.SubscriptionId;
        }, (id, record) =>
        {
            // A parked subscription learns its run from the first delivered record.
            run ??= record.RunId;
            if (record.SubscriptionId != id || record.RunId != run) throw new JsonException();
            return record.Cursor;
        }, cancellationToken);
    }

    /// <summary>Future-only, cluster-wide logs with no run, cursor or replay.</summary>
    public Task<NativeSubscription<LogsResult, LogEventNotification>> LogsAsync(CancellationToken cancellationToken = default)
        => connection.SubscribeAsync<LogsResult, LogEventNotification>("logs", NativeJson.SerializeUtf8(new LogsParams()),
            result => result.SubscriptionId, (id, record) => record.SubscriptionId == id ? null : throw new JsonException(),
            cancellationToken, cursorlessClose: true);

    /// <summary>Future-only, read-only attachment to one execution. There is no run selector, cursor or input.</summary>
    public Task<NativeSubscription<AgentAttachResult, AgentAttachEventNotification>> AttachAgentAsync(AgentAttachParams parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return connection.SubscribeAsync<AgentAttachResult, AgentAttachEventNotification>("agent/attach", NativeJson.SerializeUtf8(parameters),
            result => result.SubscriptionId, (id, record) => record.SubscriptionId == id ? null : throw new JsonException(),
            cancellationToken, cursorlessClose: true);
    }

    private Task<NativeAttempt<T>> Attempt<T>(string method, NativeContract parameters, Action<T>? validate,
        Func<JsonRpcError, bool> isRefusal, OecpRequest? request, CancellationToken cancellationToken, bool control = false) where T : class
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return connection.AttemptAsync(method, NativeJson.SerializeUtf8(parameters), validate, isRefusal, control, request, cancellationToken);
    }

    // The shared admission server maps these store preconditions before any commit
    // (openengine-cluster-server admission.rs, admission/core.rs and admission/errors.rs).
    // Each operation lists only the codes its path can return; any other code stays Unknown.
    private static bool Refused(JsonRpcError error, params string[] codes)
        => OecpConnection.IsDispatchRefusal(error) || (error.Code == -32000 && error.Data?.Code is { } code && codes.Contains(code));
}

public sealed partial class OecpRunsClient
{
    /// <summary>One trusted-controller run/submit attempt. Stock targets refuse it with RUN_CONFLICT; use HTTP submission there.</summary>
    public Task<NativeAttempt<RunSubmitResult>> SubmitAsync(RunSubmitParams parameters, OecpRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        // Pinned native refusals: graph admission is -32602 GRAPH_INVALID and a submission-key conflict is
        // IDEMPOTENCY_REUSE (native_v2_cloud/backend.rs); the target adapter answers RUN_CONFLICT
        // (native_v2_target_authority/transport.rs), as does the portable controller; backends without
        // run/submit inherit INVALID_PHASE (openengine-cluster-server lib.rs).
        return connection.AttemptAsync<RunSubmitResult>("run/submit", NativeJson.SerializeUtf8(parameters), null, error =>
            OecpConnection.IsDispatchRefusal(error) || (error.Code, error.Data?.Code) is
                (-32602, "GRAPH_INVALID") or (-32000, "IDEMPOTENCY_REUSE" or "RUN_CONFLICT" or "INVALID_PHASE"),
            control: false, request, cancellationToken);
    }
}
