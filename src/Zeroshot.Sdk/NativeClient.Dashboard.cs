using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Zeroshot.Native.Contracts;
using Zeroshot.Native.Execution;

namespace Zeroshot.Native;

/// <summary>A redirect returned by a browser route. It is reported, never followed.</summary>
public sealed class DashboardRedirect
{
    public HttpStatusCode StatusCode { get; }
    /// <summary>The exact received Location value, which native sends as a path.</summary>
    public string Location { get; }
    internal DashboardRedirect(HttpStatusCode statusCode, string location) => (StatusCode, Location) = (statusCode, location);
}

/// <summary>A bounded static browser asset. It is not JSON and is never interpreted.</summary>
public sealed class DashboardContent
{
    private readonly byte[] body;
    public string MediaType { get; }
    public string? CharSet { get; }
    public int Length => body.Length;
    internal DashboardContent(string mediaType, string? charSet, byte[] body) => (MediaType, CharSet, this.body) = (mediaType, charSet, body);
    /// <summary>A copy of the complete received body.</summary>
    public byte[] ExportBody() => body.ToArray();
}

public sealed partial class NativeClient
{
    private NativeDashboardClient? dashboard;
    /// <summary>Browser dashboard routes of native's UI router: a local UI or a direct target's UI mount.</summary>
    public NativeDashboardClient Dashboard => dashboard ??= new NativeDashboardClient(this);
}

/// <summary>
/// Native browser routes on an existing UI origin. Requests carry the exact origin Host and no
/// Origin or Sec-Fetch-Site header. Transformations return drafts; only a profile save stores anything, and nothing runs.
/// </summary>
public sealed class NativeDashboardClient
{
    // Native UI body limit (profile_ui.rs MAX_BODY); larger requests are refused before dispatch.
    private const int MaxDraftRequestBytes = 2 * 1024 * 1024;
    private static readonly HttpBinding<DashboardRedirect> GetRoot = Browser<DashboardRedirect>("dashboard.getRoot");
    private static readonly HttpBinding<DashboardRedirect> HeadRoot = Browser<DashboardRedirect>("dashboard.headRoot");
    private static readonly HttpBinding<DashboardRedirect> GetUi = Browser<DashboardRedirect>("dashboard.getUi");
    private static readonly HttpBinding<DashboardRedirect> HeadUi = Browser<DashboardRedirect>("dashboard.headUi");
    private static readonly HttpBinding<DashboardContent> GetIndex = Browser<DashboardContent>("dashboard.getIndex");
    private static readonly HttpBinding<NativeHeadResult> HeadIndex = Browser<NativeHeadResult>("dashboard.headIndex");
    private static readonly HttpBinding<DashboardContent> GetAsset = Browser<DashboardContent>("dashboard.getAsset");
    private static readonly HttpBinding<NativeHeadResult> HeadAsset = Browser<NativeHeadResult>("dashboard.headAsset");
    private static readonly HttpBinding<DashboardBootstrap> GetBootstrap = Browser<DashboardBootstrap>("dashboard.getBootstrap");
    private static readonly HttpBinding<NativeHeadResult> HeadBootstrap = Browser<NativeHeadResult>("dashboard.headBootstrap");
    private static readonly HttpBinding<DashboardValidation> Validate = Browser<DashboardValidation>("dashboard.validate", MaxDraftRequestBytes);
    private static readonly HttpBinding<DashboardAuthoringDraft> Authoring = Browser<DashboardAuthoringDraft>("dashboard.authoring", MaxDraftRequestBytes);
    private static readonly HttpBinding<DashboardDataDraft> Data = Browser<DashboardDataDraft>("dashboard.data", MaxDraftRequestBytes);
    private static readonly HttpBinding<RunProfileListResult> ListProfiles = Browser<RunProfileListResult>("dashboard.listProfiles");
    private static readonly HttpBinding<NativeHeadResult> HeadProfiles = Browser<NativeHeadResult>("dashboard.headProfiles");
    private static readonly HttpBinding<DashboardProfile> GetProfile = Browser<DashboardProfile>("dashboard.getProfile");
    private static readonly HttpBinding<NativeHeadResult> HeadProfile = Browser<NativeHeadResult>("dashboard.headProfile");
    private static readonly HttpBinding<DashboardProfile> SaveProfile = new(
        new("dashboard.saveProfile", OperationTransport.Http, requestBytes: MaxDraftRequestBytes), refusals: IsProfileSaveRefusal,
        response: HttpResponsePolicy.UiRouter);
    // Native serves these with the run-history handlers behind the discovered direct-target routes.
    private static readonly HttpBinding<RunHistoryList> ListRuns = History<RunHistoryList>("dashboard.listRuns", 4);
    private static readonly HttpBinding<NativeHeadResult> HeadRuns = History<NativeHeadResult>("dashboard.headRuns", 4);
    private static readonly HttpBinding<RunDefinition> GetRun = History<RunDefinition>("dashboard.getRun", 8, RunHistoryRules.Definition);
    private static readonly HttpBinding<NativeHeadResult> HeadRun = History<NativeHeadResult>("dashboard.headRun", 8);
    private static readonly HttpBinding<HistoryPage> GetHistory = History<HistoryPage>("dashboard.getHistory", 8);
    private static readonly HttpBinding<NativeHeadResult> HeadHistory = History<NativeHeadResult>("dashboard.headHistory", 8);
    private static readonly HttpBinding<DashboardRunEvents> RunEvents = History<DashboardRunEvents>("dashboard.runEvents", 8);
    private static readonly HttpBinding<NativeHeadResult> HeadRunEvents = History<NativeHeadResult>("dashboard.headRunEvents", 8);
    private const string RunsPath = "/ui/api/runs{?after}";
    private const string RunPath = "/ui/api/runs/{run_id}";
    private const string HistoryPath = "/ui/api/runs/{run_id}/history{?after}";
    private const string EventsPath = "/ui/api/runs/{run_id}/events{?after}";
    private const string CursorMessage = "A history cursor must be canonical v2:<sequence>.";
    private const string BootstrapPath = "/ui/api/bootstrap";
    private const string ProfilesPath = "/ui/api/profiles";
    private readonly NativeClient client;
    internal NativeDashboardClient(NativeClient client) => this.client = client;

    private static HttpBinding<T> Browser<T>(string name, int? requestBytes = null)
        => new(new(name, OperationTransport.Http, requestBytes: requestBytes), response: HttpResponsePolicy.UiRouter);
    private static HttpBinding<T> History<T>(string name, int responseMebibytes, Action<T, RunId>? identity = null)
        => new(new(name, OperationTransport.Http, responseBytes: responseMebibytes * 1024 * 1024), identity: identity,
            response: HttpResponsePolicy.History(HttpProblemDialect.UiRouter));

    /// <summary>`GET /`: native redirects to `/ui/`.</summary>
    public Task<DashboardRedirect> GetRootAsync(CancellationToken cancellationToken = default)
        => RedirectAsync(GetRoot, HttpMethod.Get, "/", cancellationToken);
    public Task<DashboardRedirect> HeadRootAsync(CancellationToken cancellationToken = default)
        => RedirectAsync(HeadRoot, HttpMethod.Head, "/", cancellationToken);
    /// <summary>`GET /ui`: native redirects to `/ui/`.</summary>
    public Task<DashboardRedirect> GetUiAsync(CancellationToken cancellationToken = default)
        => RedirectAsync(GetUi, HttpMethod.Get, "/ui", cancellationToken);
    public Task<DashboardRedirect> HeadUiAsync(CancellationToken cancellationToken = default)
        => RedirectAsync(HeadUi, HttpMethod.Head, "/ui", cancellationToken);

    /// <summary>`GET /ui/`: the embedded index document.</summary>
    public Task<DashboardContent> GetIndexAsync(CancellationToken cancellationToken = default)
        => ContentAsync(GetIndex, () => Path("/ui/"), cancellationToken);
    public Task<NativeHeadResult> HeadIndexAsync(CancellationToken cancellationToken = default)
        => client.HeadAsync(HeadIndex, () => Path("/ui/"), null, cancellationToken);

    /// <summary>`GET /ui/{*asset}` for a relative asset path such as `assets/app.js`. A missing asset is a bare 404.</summary>
    public Task<DashboardContent> GetAssetAsync(string assetPath, CancellationToken cancellationToken = default)
        => ContentAsync(GetAsset, () => AssetUri(assetPath), cancellationToken);
    public Task<NativeHeadResult> HeadAssetAsync(string assetPath, CancellationToken cancellationToken = default)
        => client.HeadAsync(HeadAsset, () => AssetUri(assetPath), null, cancellationToken);

    /// <summary>`GET /ui/api/bootstrap`: templates, worker options, runtime schema and workspace identity.</summary>
    public Task<DashboardBootstrap> GetBootstrapAsync(CancellationToken cancellationToken = default)
        => client.ReadAsync(GetBootstrap, () => Path(BootstrapPath), null, cancellationToken);
    public Task<NativeHeadResult> HeadBootstrapAsync(CancellationToken cancellationToken = default)
        => client.HeadAsync(HeadBootstrap, () => Path(BootstrapPath), null, cancellationToken);

    /// <summary>`POST /ui/api/validate`: native profile admission. An inadmissible profile is a 422 `invalid_profile` problem.</summary>
    public Task<DashboardValidation> ValidateAsync(DashboardProfileDocument document, CancellationToken cancellationToken = default)
        => client.ReadAsync(Validate, () => Post("/ui/api/validate", document), null, cancellationToken);

    /// <summary>`POST /ui/api/authoring`: applies one outcome edit to a draft.</summary>
    public Task<DashboardAuthoringDraft> AuthorAsync(DashboardAuthoringRequest request, CancellationToken cancellationToken = default)
        => client.ReadAsync(Authoring, () => Post("/ui/api/authoring", request), null, cancellationToken);

    /// <summary>`POST /ui/api/data`: applies one input/output edit to a draft.</summary>
    public Task<DashboardDataDraft> TransformDataAsync(DashboardDataRequest request, CancellationToken cancellationToken = default)
        => client.ReadAsync(Data, () => Post("/ui/api/data", request), null, cancellationToken);

    /// <summary>`GET /ui/api/profiles`: summaries of the UI store's user-scope profiles.</summary>
    public Task<RunProfileListResult> ListProfilesAsync(CancellationToken cancellationToken = default)
        => client.ReadAsync(ListProfiles, () => Path(ProfilesPath), null, cancellationToken);
    public Task<NativeHeadResult> HeadProfilesAsync(CancellationToken cancellationToken = default)
        => client.HeadAsync(HeadProfiles, () => Path(ProfilesPath), null, cancellationToken);

    /// <summary>
    /// `GET /ui/api/profiles/{name}`: one user-scope profile and its current revision. Native reports a missing
    /// profile as 500 <c>profile_store_error</c>, not 404.
    /// </summary>
    public Task<DashboardProfile> GetProfileAsync(RunProfileName name, CancellationToken cancellationToken = default)
        => client.ReadAsync(GetProfile, () => Path(ProfilePath(name)), null, cancellationToken);
    public Task<NativeHeadResult> HeadProfileAsync(RunProfileName name, CancellationToken cancellationToken = default)
        => client.HeadAsync(HeadProfile, () => Path(ProfilePath(name)), null, cancellationToken);

    /// <summary>
    /// `POST /ui/api/profiles`: one compare-and-swap save, sent once with <paramref name="workspaceId"/> (from
    /// <see cref="DashboardBootstrap.Workspace"/>) verbatim as <c>X-Zeroshot-Workspace</c>. 409 <c>workspace_changed</c>
    /// and <c>profile_conflict</c>, 422 <c>invalid_profile</c> and boundary refusals are rejected attempts; any other
    /// failure after dispatch, including a lost reply or 500 <c>profile_store_error</c>, is unknown and never retried.
    /// </summary>
    public Task<NativeAttempt<DashboardProfile>> SaveProfileAsync(DashboardProfileSaveRequest request, string workspaceId,
        CancellationToken cancellationToken = default)
        => client.MutateAsync(SaveProfile, () =>
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(workspaceId);
            return Post(ProfilesPath, request);
        }, null, cancellationToken, configure: message => message.Headers.Add("X-Zeroshot-Workspace", workspaceId));

    // The browser boundary, the workspace check, decoding/admission and the revision check all answer before
    // native writes (profile_ui.rs save, server.rs browser_boundary). A 500 profile_store_error can follow the write.
    private static bool IsProfileSaveRefusal(HttpStatusCode? status, string code) =>
        (status, code) is
            (HttpStatusCode.Conflict, "workspace_changed") or
            (HttpStatusCode.Conflict, "profile_conflict") or
            (HttpStatusCode.UnprocessableEntity, "invalid_profile") or
            (HttpStatusCode.Forbidden, "origin_rejected") or
            (HttpStatusCode.UnsupportedMediaType, "json_required") or
            (HttpStatusCode.ServiceUnavailable, "server_stopping");

    /// <summary>`GET /ui/api/runs`: one run list page, optionally strictly after a canonical UUIDv7 run ID.</summary>
    public Task<RunHistoryList> ListRunsAsync(RunId? after = null, CancellationToken cancellationToken = default)
        => client.ReadAsync(ListRuns, () => RunsUri(after), null, cancellationToken, validate: list => RunHistoryRules.List(list, after));
    public Task<NativeHeadResult> HeadRunsAsync(RunId? after = null, CancellationToken cancellationToken = default)
        => client.HeadAsync(HeadRuns, () => RunsUri(after), null, cancellationToken);

    /// <summary>`GET /ui/api/runs/{id}`: the admitted run definition.</summary>
    public Task<RunDefinition> GetRunAsync(RunId runId, CancellationToken cancellationToken = default)
        => client.ReadAsync(GetRun, () => RunUri(RunPath, runId, null), null, cancellationToken, runId);
    public Task<NativeHeadResult> HeadRunAsync(RunId runId, CancellationToken cancellationToken = default)
        => client.HeadAsync(HeadRun, () => RunUri(RunPath, runId, null), null, cancellationToken);

    /// <summary>`GET /ui/api/runs/{id}/history`: one page strictly after <paramref name="after"/>, or from <c>v2:0</c>.</summary>
    public Task<HistoryPage> GetHistoryAsync(RunId runId, Cursor? after = null, CancellationToken cancellationToken = default)
        => client.ReadAsync(GetHistory, () => RunUri(HistoryPath, runId, after), null, cancellationToken,
            validate: page => RunHistoryRules.Page(page, after ?? RunHistoryRules.InitialCursor));
    public Task<NativeHeadResult> HeadHistoryAsync(RunId runId, Cursor? after = null, CancellationToken cancellationToken = default)
        => client.HeadAsync(HeadHistory, () => RunUri(HistoryPath, runId, after), null, cancellationToken);

    /// <summary>
    /// `GET /ui/api/runs/{id}/events`: one bounded SSE observation of history pages. Both cursors are sent when
    /// supplied; native resumes after <paramref name="lastEventId"/> in preference to <paramref name="after"/>.
    /// Never reopens. A refusal before the stream starts is a <see cref="NativeHttpException"/>.
    /// </summary>
    public Task<DashboardRunEvents> OpenRunEventsAsync(RunId runId, Cursor? after = null, Cursor? lastEventId = null,
        CancellationToken cancellationToken = default)
        => client.OpenStreamAsync<DashboardRunEvent, DashboardRunEvents>(RunEvents, () =>
            {
                var url = RunUri(EventsPath, runId, after);
                if (lastEventId is not null) RunHistoryRules.RequireCursor(lastEventId, nameof(lastEventId), CursorMessage);
                return url;
            }, null,
            response => response.StatusCode == HttpStatusCode.OK && response.Content.Headers.ContentType?.MediaType == "text/event-stream",
            request =>
            {
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                if (lastEventId is not null) request.Headers.TryAddWithoutValidation("Last-Event-ID", lastEventId.Value);
            },
            // Native pages stay within 8 MiB.
            8 * 1024 * 1024,
            (queue, response, body, frameBytes) => new DashboardRunEvents(queue, response, body,
                lastEventId ?? after ?? RunHistoryRules.InitialCursor, frameBytes),
            cancellationToken);
    public Task<NativeHeadResult> HeadRunEventsAsync(RunId runId, Cursor? after = null, CancellationToken cancellationToken = default)
        => client.HeadAsync(HeadRunEvents, () => RunUri(EventsPath, runId, after), null, cancellationToken);

    private Task<DashboardRedirect> RedirectAsync(HttpBinding<DashboardRedirect> binding, HttpMethod method, string path,
        CancellationToken cancellationToken)
        => client.ExchangeAsync(binding, method, () => Path(path), null, (response, context) =>
        {
            if ((int)response.StatusCode is < 300 or >= 400 || !response.Headers.TryGetValues("Location", out var values) ||
                values.ToArray() is not [var location] || location.Length == 0)
                throw context.Failure(OperationFailureKind.Protocol, OperationStage.Response, statusCode: response.StatusCode);
            return Task.FromResult(new DashboardRedirect(response.StatusCode, location));
        }, cancellationToken, redirectIsResult: true);

    private Task<DashboardContent> ContentAsync(HttpBinding<DashboardContent> binding, Func<HttpCall> route, CancellationToken cancellationToken)
        => client.ExchangeAsync(binding, HttpMethod.Get, route, null, async (response, context) =>
        {
            var type = response.Content.Headers.ContentType;
            if (response.StatusCode != HttpStatusCode.OK || string.IsNullOrEmpty(type?.MediaType))
                throw context.Failure(OperationFailureKind.Protocol, OperationStage.Response, statusCode: response.StatusCode);
            var stream = await response.Content.ReadAsStreamAsync(context.CancellationToken).ConfigureAwait(false);
            return new DashboardContent(type.MediaType, type.CharSet, await context.ReadResponseAsync(stream).ConfigureAwait(false));
        }, cancellationToken);

    private HttpCall Path(string path) => new Uri(client.Origin, path);

    private HttpCall Post(string path, DashboardContract request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new(new Uri(client.Origin, path), NativeJson.SerializeUtf8(request));
    }

    private static string ProfilePath(RunProfileName name)
    {
        ArgumentNullException.ThrowIfNull(name);
        // Profile names are unreserved path characters that cannot form a dot segment.
        return ProfilesPath + "/" + name.Value;
    }

    private HttpCall RunsUri(RunId? after)
    {
        if (after is not null) RunHistoryRules.RequireRunId(after, nameof(after));
        return NativeRoutes.RunIdRoute(client.Origin, RunsPath, null, NativeHistoryClient.AfterQuery, ("after", after?.Value));
    }

    private HttpCall RunUri(string template, RunId runId, Cursor? after)
    {
        RunHistoryRules.RequireRunId(runId, nameof(runId));
        if (after is not null) RunHistoryRules.RequireCursor(after, nameof(after), CursorMessage);
        return NativeRoutes.RunIdRoute(client.Origin, template, runId.Value, template != RunPath ? NativeHistoryClient.AfterQuery : null,
            ("after", after?.Value));
    }

    private HttpCall AssetUri(string assetPath)
    {
        ArgumentNullException.ThrowIfNull(assetPath);
        // Literal relative segments only: no empty, dot, escaped or query/fragment spelling.
        return NativeRoutes.CompileLiteralRoute(client.Origin, "/ui/" + assetPath);
    }
}
