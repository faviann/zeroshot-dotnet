using System.Net.Http.Headers;
using System.Text.Json;
using Zeroshot.Native.Contracts;
using Zeroshot.Native.Execution;

namespace Zeroshot.Native;

public sealed partial class NativeClient
{
    private NativeHostedRunsClient? hostedRuns;
    /// <summary>Hosted run lifecycle at routes advertised by explicitly supplied discovery.</summary>
    public NativeHostedRunsClient HostedRuns => hostedRuns ??= new NativeHostedRunsClient(this);
}

/// <summary>
/// The hosted <c>zeroshot.hosted-runs/v1</c> capability (native controller_authority/hosted_runs.rs): individual
/// calls and subscriptions with the caller's current hosted OAuth access bearer. Direct targets do not serve it.
/// There is no rediscovery, token refresh, reopen, waiting or retry.
/// </summary>
public sealed class NativeHostedRunsClient
{
    internal const string Kind = "zeroshot.hosted-runs/v1";
    private const string WatchQuery = "{?from_cursor}";
    private const string LogsQuery = "{?from_cursor,execution}";
    // Native hosted_json reads results under its 64 KiB MAX_RESPONSE_BYTES; stream frames have the same bound.
    private const int MaxBytes = 64 * 1024;
    // Native hosted_get_json sends Accept: application/json with no-store.
    private static readonly HttpBinding<HostedRunListResult> List = new(
        new("hosted_runs.list", OperationTransport.Http, responseBytes: MaxBytes), RequestHeaders.Json);
    private static readonly HttpBinding<HostedRunStatusResult> Status = new(
        new("hosted_runs.status", OperationTransport.Http, responseBytes: MaxBytes), RequestHeaders.Json, identity: SameRun);
    private static readonly HttpBinding<HostedRunStream<HostedRunWatchEventNotification>> Watch = new(
        new("hosted_runs.watch", OperationTransport.Http), RequestHeaders.NoStore);
    private static readonly HttpBinding<HostedRunStream<RunLogEventNotification>> Logs = new(
        new("hosted_runs.logs", OperationTransport.Http), RequestHeaders.NoStore);
    private static readonly HttpBinding<HostedRunStatusResult> Force = new(
        new("hosted_runs.force", OperationTransport.Http, responseBytes: MaxBytes), refusals: NativeClient.IsHostedRefusal, identity: SameRun);
    private readonly NativeClient client;
    internal NativeHostedRunsClient(NativeClient client) => this.client = client;

    /// <summary>Reads every run the host exposes. Failures throw NativeHttpException.</summary>
    public Task<HostedRunListResult> ListAsync(TargetDiscoveryDocument discovery, TargetControlCredentials credentials,
        CancellationToken cancellationToken = default)
        => client.ReadAsync(List, () => Route(discovery, credentials, r => r.List, null, null), credentials, cancellationToken);

    /// <summary>Reads the exact run, which may still be queued by the host. Failures throw NativeHttpException.</summary>
    public Task<HostedRunStatusResult> StatusAsync(TargetDiscoveryDocument discovery, RunId runId,
        TargetControlCredentials credentials, CancellationToken cancellationToken = default)
        => client.ReadAsync(Status, () => Route(discovery, credentials, r => r.Status, Required(runId), null), credentials,
            cancellationToken, runId);

    /// <summary>
    /// One durable watch stream, replayed exclusively after <see cref="RunWatchParams.FromCursor"/> then followed live.
    /// Records must keep the run, and the first record's subscription ID and source. Never reopens.
    /// </summary>
    public Task<HostedRunStream<HostedRunWatchEventNotification>> WatchAsync(TargetDiscoveryDocument discovery,
        RunWatchParams parameters, TargetControlCredentials credentials, CancellationToken cancellationToken = default)
    {
        SubscriptionId? subscription = null;
        ResolvedSource? source = null;
        return OpenAsync<HostedRunWatchEventNotification>(Watch, () => Route(discovery, credentials, r => r.Watch,
            Valid(parameters).RunId, WatchQuery, ("from_cursor", parameters.FromCursor?.Value)), credentials, record =>
        {
            subscription ??= record.SubscriptionId;
            source ??= record.Source;
            parameters.Require(record, subscription, source);
            return record.Cursor;
        }, cancellationToken);
    }

    /// <summary>One retained-log replay followed live, optionally restricted to one exact execution. Never reopens.</summary>
    public Task<HostedRunStream<RunLogEventNotification>> LogsAsync(TargetDiscoveryDocument discovery,
        RunLogsParams parameters, TargetControlCredentials credentials, CancellationToken cancellationToken = default)
    {
        SubscriptionId? subscription = null;
        return OpenAsync<RunLogEventNotification>(Logs, () => Route(discovery, credentials, r => r.Logs, Valid(parameters).RunId,
            LogsQuery, ("from_cursor", parameters.FromCursor?.Value), ("execution", parameters.Execution?.Value)), credentials, record =>
        {
            subscription ??= record.SubscriptionId;
            parameters.Require(record, subscription);
            return record.Cursor;
        }, cancellationToken);
    }

    /// <summary>
    /// Sends one native force request. An acknowledged status can be queued or stopping, not terminal. Operational
    /// failures and cancellation return evidence; there is no retry or wait.
    /// </summary>
    public Task<NativeAttempt<HostedRunStatusResult>> ForceAsync(TargetDiscoveryDocument discovery, RunId runId,
        TargetControlCredentials credentials, CancellationToken cancellationToken = default)
        => client.MutateAsync(Force, () => new HttpCall(Route(discovery, credentials, r => r.Force, Required(runId), null), "{}"u8.ToArray()),
            credentials, cancellationToken, runId);

    private static void SameRun(HostedRunStatusResult result, RunId runId)
    {
        if (result.RunId != runId) throw new JsonException();
    }

    private static RunId Required(RunId runId)
    {
        ArgumentNullException.ThrowIfNull(runId);
        return runId;
    }

    private static T Valid<T>(T parameters) where T : NativeContract
    {
        ArgumentNullException.ThrowIfNull(parameters);
        _ = NativeJson.SerializeUtf8(parameters);
        return parameters;
    }

    // Native hosted_stream admits any successful status without checking Content-Type.
    private Task<HostedRunStream<TEvent>> OpenAsync<TEvent>(HttpBinding<HostedRunStream<TEvent>> binding, Func<HttpCall> route,
        TargetControlCredentials credentials, Func<TEvent, Cursor> validate, CancellationToken cancellationToken)
        => client.OpenStreamAsync<TEvent, HostedRunStream<TEvent>>(binding, route, credentials, _ => true,
            request => request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/x-ndjson")), MaxBytes,
            (queue, response, body, frameBytes) => new HostedRunStream<TEvent>(queue, response, body, frameBytes, validate), cancellationToken);

    private Uri Route(TargetDiscoveryDocument discovery, TargetControlCredentials credentials,
        Func<TargetHostedRunRoutes, string> select, RunId? runId, string? query, params (string, string?)[] values)
        => NativeRoutes.RunIdRoute(Base(client.Origin, discovery, credentials, runId),
            select(discovery.Extensions.HostedRuns!.RouteTemplates), runId?.Value, query, values);

    // Native build_hosted_runs_descriptor compiles all five routes before any is used and returns the
    // capability base, which hosted workspace recovery shares.
    internal static Uri Base(Uri origin, TargetDiscoveryDocument discovery, TargetControlCredentials credentials, RunId? runId)
    {
        NativeClient.AdmitHosted(discovery, credentials);
        if (runId is not null && !NativeRoutes.IsAddressableSegment(runId.Value))
            throw new ArgumentException("Native cannot address this run ID as one route segment.", nameof(runId));
        var wire = NativeClient.Advertised(discovery.Extensions.HostedRuns, w => w.Kind, Kind,
            "The target does not advertise hosted runs.", "Hosted run discovery is incompatible.");
        var baseUrl = NativeRoutes.CapabilityBaseUrl(origin, wire.BaseUrl);
        var routes = wire.RouteTemplates;
        _ = NativeRoutes.RunIdPath(routes.List, requiresRunId: false, query: null);
        _ = NativeRoutes.RunIdPath(routes.Status, requiresRunId: true, query: null);
        _ = NativeRoutes.RunIdPath(routes.Watch, requiresRunId: true, WatchQuery);
        _ = NativeRoutes.RunIdPath(routes.Logs, requiresRunId: true, LogsQuery);
        _ = NativeRoutes.RunIdPath(routes.Force, requiresRunId: true, query: null);
        return baseUrl;
    }
}
