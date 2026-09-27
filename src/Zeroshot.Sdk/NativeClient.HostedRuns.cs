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

    internal Uri HostedRunRoute(TargetDiscoveryDocument discovery, TargetControlCredentials credentials,
        Func<TargetHostedRunRoutes, string> select, RunId? runId, string? query, params (string, string?)[] values)
        => NativeRoutes.RunIdRoute(HostedRunsBase(discovery, credentials, runId),
            select(discovery.Extensions.HostedRuns!.RouteTemplates), runId?.Value, query, values);

    // Native build_hosted_runs_descriptor compiles all five routes before any is used and returns the
    // capability base, which hosted workspace recovery shares. Invalid use throws before any request is sent.
    private Uri HostedRunsBase(TargetDiscoveryDocument discovery, TargetControlCredentials credentials, RunId? runId)
    {
        ValidateHostedUse(discovery, credentials);
        if (runId is not null && !NativeRoutes.IsAddressableSegment(runId.Value))
            throw new ArgumentException("Native cannot address this run ID as one route segment.", nameof(runId));
        var wire = discovery.Extensions.HostedRuns
            ?? throw new ArgumentException("The target does not advertise hosted runs.");
        if (wire.Kind != NativeHostedRunsClient.Kind) throw new ArgumentException("Hosted run discovery is incompatible.");
        var baseUrl = NativeRoutes.CapabilityBaseUrl(Origin, wire.BaseUrl);
        var routes = wire.RouteTemplates;
        _ = NativeRoutes.RunIdPath(routes.List, requiresRunId: false, query: null);
        _ = NativeRoutes.RunIdPath(routes.Status, requiresRunId: true, query: null);
        _ = NativeRoutes.RunIdPath(routes.Watch, requiresRunId: true, NativeHostedRunsClient.WatchQuery);
        _ = NativeRoutes.RunIdPath(routes.Logs, requiresRunId: true, NativeHostedRunsClient.LogsQuery);
        _ = NativeRoutes.RunIdPath(routes.Force, requiresRunId: true, query: null);
        return baseUrl;
    }

    // Native hosted_stream admits any successful status without checking Content-Type.
    internal Task<HostedRunStream<TEvent>> OpenHostedRunStreamAsync<TEvent>(OperationDescriptor operation, Uri route,
        TargetControlCredentials credentials, Func<TEvent, Cursor> validate, CancellationToken cancellationToken)
        => OpenStreamAsync<TEvent, HostedRunStream<TEvent>>(operation, route, credentials, _ => true, request =>
            {
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/x-ndjson"));
                NoStore(request);
            }, (queue, response, body) => new HostedRunStream<TEvent>(queue, response, body,
                Math.Min(limits.MessageBytes, NativeHostedRunsClient.MaxBytes), validate), cancellationToken);

    internal async Task<NativeAttempt<HostedRunStatusResult>> ForceHostedRunAsync(Uri route, RunId runId,
        TargetControlCredentials credentials, CancellationToken cancellationToken)
    {
        var operation = NativeHostedRunsClient.ForceOperation;
        var (correlationId, outcome, response, failure) = await AttemptAsync<HostedRunStatusResult>(operation, route, "{}"u8.ToArray(),
            credentials, IsHostedRefusal, cancellationToken,
            validate: result => { if (result.RunId != runId) throw new JsonException(); }).ConfigureAwait(false);
        return new NativeAttempt<HostedRunStatusResult>(Origin, operation.Name, correlationId, outcome, response, failure);
    }
}

/// <summary>
/// The hosted <c>zeroshot.hosted-runs/v1</c> capability (native controller_authority/hosted_runs.rs): individual
/// calls and subscriptions with the caller's current hosted OAuth access bearer. Direct targets do not serve it.
/// There is no rediscovery, token refresh, reopen, waiting or retry.
/// </summary>
public sealed class NativeHostedRunsClient
{
    internal const string Kind = "zeroshot.hosted-runs/v1";
    internal const string WatchQuery = "{?from_cursor}";
    internal const string LogsQuery = "{?from_cursor,execution}";
    // Native hosted_json reads results under its 64 KiB MAX_RESPONSE_BYTES; stream frames have the same bound.
    internal const int MaxBytes = 64 * 1024;
    internal static readonly OperationDescriptor ListOperation = new("hosted_runs.list", OperationTransport.Http, responseBytes: MaxBytes);
    internal static readonly OperationDescriptor StatusOperation = new("hosted_runs.status", OperationTransport.Http, responseBytes: MaxBytes);
    internal static readonly OperationDescriptor WatchOperation = new("hosted_runs.watch", OperationTransport.Http);
    internal static readonly OperationDescriptor LogsOperation = new("hosted_runs.logs", OperationTransport.Http);
    internal static readonly OperationDescriptor ForceOperation = new("hosted_runs.force", OperationTransport.Http, responseBytes: MaxBytes);
    private readonly NativeClient client;
    internal NativeHostedRunsClient(NativeClient client) => this.client = client;

    /// <summary>Reads every run the host exposes. Failures throw NativeHttpException.</summary>
    public Task<HostedRunListResult> ListAsync(TargetDiscoveryDocument discovery, TargetControlCredentials credentials,
        CancellationToken cancellationToken = default)
        => client.ExecuteJsonAsync<HostedRunListResult>(ListOperation, client.HostedRunRoute(discovery, credentials, r => r.List, null, null),
            null, credentials, _ => { }, cancellationToken, configure: Json);

    /// <summary>Reads the exact run, which may still be queued by the host. Failures throw NativeHttpException.</summary>
    public Task<HostedRunStatusResult> StatusAsync(TargetDiscoveryDocument discovery, RunId runId,
        TargetControlCredentials credentials, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runId);
        return client.ExecuteJsonAsync<HostedRunStatusResult>(StatusOperation,
            client.HostedRunRoute(discovery, credentials, r => r.Status, runId, null), null, credentials,
            result => { if (result.RunId != runId) throw new JsonException(); }, cancellationToken, configure: Json);
    }

    /// <summary>
    /// One durable watch stream, replayed exclusively after <see cref="RunWatchParams.FromCursor"/> then followed live.
    /// Records must keep the run, and the first record's subscription ID and source. Never reopens.
    /// </summary>
    public Task<HostedRunStream<HostedRunWatchEventNotification>> WatchAsync(TargetDiscoveryDocument discovery,
        RunWatchParams parameters, TargetControlCredentials credentials, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        _ = NativeJson.SerializeUtf8(parameters);
        var route = client.HostedRunRoute(discovery, credentials, r => r.Watch, parameters.RunId, WatchQuery,
            ("from_cursor", parameters.FromCursor?.Value));
        SubscriptionId? subscription = null;
        ResolvedSource? source = null;
        return client.OpenHostedRunStreamAsync<HostedRunWatchEventNotification>(WatchOperation, route, credentials, record =>
        {
            subscription ??= record.SubscriptionId;
            source ??= record.Source;
            if (record.SubscriptionId != subscription || record.RunId != parameters.RunId || record.Source != source)
                throw new JsonException();
            return record.Cursor;
        }, cancellationToken);
    }

    /// <summary>One retained-log replay followed live, optionally restricted to one exact execution. Never reopens.</summary>
    public Task<HostedRunStream<RunLogEventNotification>> LogsAsync(TargetDiscoveryDocument discovery,
        RunLogsParams parameters, TargetControlCredentials credentials, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        _ = NativeJson.SerializeUtf8(parameters);
        var route = client.HostedRunRoute(discovery, credentials, r => r.Logs, parameters.RunId, LogsQuery,
            ("from_cursor", parameters.FromCursor?.Value), ("execution", parameters.Execution?.Value));
        SubscriptionId? subscription = null;
        return client.OpenHostedRunStreamAsync<RunLogEventNotification>(LogsOperation, route, credentials, record =>
        {
            subscription ??= record.SubscriptionId;
            if (record.SubscriptionId != subscription || record.RunId != parameters.RunId ||
                (parameters.Execution is not null && record.Execution != parameters.Execution))
                throw new JsonException();
            return record.Cursor;
        }, cancellationToken);
    }

    /// <summary>
    /// Sends one native force request. An acknowledged status can be queued or stopping, not terminal. Operational
    /// failures and cancellation return evidence; there is no retry or wait.
    /// </summary>
    public Task<NativeAttempt<HostedRunStatusResult>> ForceAsync(TargetDiscoveryDocument discovery, RunId runId,
        TargetControlCredentials credentials, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runId);
        return client.ForceHostedRunAsync(client.HostedRunRoute(discovery, credentials, r => r.Force, runId, null),
            runId, credentials, cancellationToken);
    }

    // Native hosted_get_json sends exactly these headers.
    private static void Json(HttpRequestMessage request)
    {
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        NativeClient.NoStore(request);
    }
}
