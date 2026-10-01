using Zeroshot.Native.Contracts;
using Zeroshot.Native.Execution;

namespace Zeroshot.Native;

public sealed partial class NativeClient
{
    private NativeHistoryClient? history;
    /// <summary>Discovered public run-history reads. No retention guarantee or history SSE route is implied.</summary>
    public NativeHistoryClient History => history ??= new NativeHistoryClient(this);
}

/// <summary>
/// Binds the discovered <c>run_history</c> capability (native controller_authority/history.rs). A
/// direct target serves it through its UI mount, which also answers HEAD for the same routes.
/// </summary>
public sealed class NativeHistoryClient
{
    internal const string Kind = "zeroshot.run-history/v1";
    internal const string AfterQuery = "{?after}";
    private static readonly HttpBinding<RunHistoryList> List = Bind<RunHistoryList>("history.list", 4);
    private static readonly HttpBinding<RunDefinition> Detail = Bind<RunDefinition>("history.detail", 8, RunHistoryRules.Definition);
    private static readonly HttpBinding<HistoryPage> Page = Bind<HistoryPage>("history.page", 8);
    private static readonly HttpBinding<NativeHeadResult> HeadList = Bind<NativeHeadResult>("history.list.head", 4);
    private static readonly HttpBinding<NativeHeadResult> HeadDetail = Bind<NativeHeadResult>("history.detail.head", 8);
    private static readonly HttpBinding<NativeHeadResult> HeadPage = Bind<NativeHeadResult>("history.page.head", 8);
    private readonly NativeClient client;

    internal NativeHistoryClient(NativeClient client) => this.client = client;

    // Direct history is served by the target's UI router; hosted history is a host-owned HTTP API. Native
    // TargetRunHistoryTransport sends Accept: application/json with no-store to both.
    private static HttpBinding<T> Bind<T>(string name, int responseMebibytes, Action<T, RunId>? identity = null)
        => new(Create(name, responseMebibytes, uiRouter: true), RequestHeaders.Json, identity: identity,
            hostOwned: Create(name, responseMebibytes, uiRouter: false));
    private static OperationDescriptor Create(string name, int responseMebibytes, bool uiRouter) => new(name, OperationTransport.Http,
        responseBytes: responseMebibytes * 1024 * 1024, problemBytes: 64 * 1024, uiRouter: uiRouter, historyProblems: true);

    /// <summary>Reads one list page, optionally strictly after a canonical UUIDv7 run ID.</summary>
    public Task<RunHistoryList> ListAsync(TargetDiscoveryDocument discovery, RunId? after = null,
        TargetControlCredentials? credentials = null, CancellationToken cancellationToken = default)
        => client.ReadAsync(List, () => ListUrl(discovery, after, credentials), credentials, cancellationToken,
            validate: list => RunHistoryRules.List(list, after));

    /// <summary>Reads the admitted definition. Legacy snapshots are accepted and never re-emitted.</summary>
    public Task<RunDefinition> DetailAsync(TargetDiscoveryDocument discovery, RunId runId,
        TargetControlCredentials? credentials = null, CancellationToken cancellationToken = default)
        => client.ReadAsync(Detail, () => DetailUrl(discovery, runId, credentials), credentials, cancellationToken, runId);

    /// <summary>Reads one bounded page strictly after <paramref name="after"/>, or from <c>v2:0</c>.</summary>
    public Task<HistoryPage> PageAsync(TargetDiscoveryDocument discovery, RunId runId, Cursor? after = null,
        TargetControlCredentials? credentials = null, CancellationToken cancellationToken = default)
    {
        var requested = after ?? RunHistoryRules.InitialCursor;
        return client.ReadAsync(Page, () => PageUrl(discovery, runId, requested, credentials), credentials, cancellationToken,
            validate: page => RunHistoryRules.Page(page, requested));
    }

    /// <summary>HEAD for the list route. Native serves it only on the direct target UI mount.</summary>
    public Task<NativeHeadResult> HeadListAsync(TargetDiscoveryDocument discovery, RunId? after = null,
        TargetControlCredentials? credentials = null, CancellationToken cancellationToken = default)
        => client.HeadAsync(HeadList, () => ListUrl(discovery, after, credentials), credentials, cancellationToken);

    public Task<NativeHeadResult> HeadDetailAsync(TargetDiscoveryDocument discovery, RunId runId,
        TargetControlCredentials? credentials = null, CancellationToken cancellationToken = default)
        => client.HeadAsync(HeadDetail, () => DetailUrl(discovery, runId, credentials), credentials, cancellationToken);

    public Task<NativeHeadResult> HeadPageAsync(TargetDiscoveryDocument discovery, RunId runId, Cursor? after = null,
        TargetControlCredentials? credentials = null, CancellationToken cancellationToken = default)
        => client.HeadAsync(HeadPage, () => PageUrl(discovery, runId, after ?? RunHistoryRules.InitialCursor, credentials),
            credentials, cancellationToken);

    private HttpCall ListUrl(TargetDiscoveryDocument discovery, RunId? after, TargetControlCredentials? credentials)
    {
        if (after is not null) RunHistoryRules.RequireRunId(after, nameof(after));
        return Route(discovery, credentials, d => d.List, runId: null, after?.Value, allowsAfter: true);
    }

    private HttpCall DetailUrl(TargetDiscoveryDocument discovery, RunId runId, TargetControlCredentials? credentials)
    {
        RunHistoryRules.RequireRunId(runId, nameof(runId));
        return Route(discovery, credentials, d => d.Detail, runId.Value, after: null, allowsAfter: false);
    }

    private HttpCall PageUrl(TargetDiscoveryDocument discovery, RunId runId, Cursor after, TargetControlCredentials? credentials)
    {
        RunHistoryRules.RequireRunId(runId, nameof(runId));
        RunHistoryRules.RequireCursor(after, nameof(after));
        return Route(discovery, credentials, d => d.Page, runId.Value, after.Value, allowsAfter: true);
    }

    private HttpCall Route(TargetDiscoveryDocument discovery, TargetControlCredentials? credentials,
        Func<TargetRunHistoryRoutes, string> select, string? runId, string? after, bool allowsAfter)
    {
        NativeClient.Admit(discovery, d => d.Authentication != TargetAuthentication.PrivateCapability &&
            (credentials?.Authentication ?? TargetAuthentication.None) == d.Authentication,
            "Discovery and supplied history authority are incompatible.");
        const string incompatible = "Discovery does not advertise compatible run history.";
        var capability = NativeClient.Advertised(discovery.Extensions.RunHistory, c => c.Kind, Kind, incompatible, incompatible);
        var baseUrl = NativeRoutes.CapabilityBaseUrl(client.Origin, capability.BaseUrl);
        // Native build_run_history_descriptor compiles all three routes before any is used.
        var routes = capability.RouteTemplates;
        _ = NativeRoutes.RunIdPath(routes.List, requiresRunId: false, AfterQuery);
        _ = NativeRoutes.RunIdPath(routes.Detail, requiresRunId: true, query: null);
        _ = NativeRoutes.RunIdPath(routes.Page, requiresRunId: true, AfterQuery);
        return new(NativeRoutes.RunIdRoute(baseUrl, select(routes), runId, allowsAfter ? AfterQuery : null, ("after", after)),
            HostOwned: discovery.Authentication == TargetAuthentication.HostedOauth);
    }
}
