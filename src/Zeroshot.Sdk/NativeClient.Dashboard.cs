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

    internal Task<DashboardRedirect> DashboardRedirectAsync(OperationDescriptor operation, HttpMethod method, string path,
        CancellationToken cancellationToken)
        => ExecuteHttpAsync(operation, method, new Uri(Origin, path), null, null, (response, context) =>
        {
            if ((int)response.StatusCode is < 300 or >= 400 || !response.Headers.TryGetValues("Location", out var values) ||
                values.ToArray() is not [var location] || location.Length == 0)
                throw context.Failure(OperationFailureKind.Protocol, OperationStage.Response, statusCode: response.StatusCode);
            return Task.FromResult(new DashboardRedirect(response.StatusCode, location));
        }, cancellationToken, redirectIsResult: true);

    internal Task<DashboardContent> DashboardContentAsync(OperationDescriptor operation, Uri requestUri, CancellationToken cancellationToken)
        => ExecuteHttpAsync(operation, HttpMethod.Get, requestUri, null, null, async (response, context) =>
        {
            var type = response.Content.Headers.ContentType;
            if (response.StatusCode != HttpStatusCode.OK || string.IsNullOrEmpty(type?.MediaType))
                throw context.Failure(OperationFailureKind.Protocol, OperationStage.Response, statusCode: response.StatusCode);
            var stream = await response.Content.ReadAsStreamAsync(context.CancellationToken).ConfigureAwait(false);
            return new DashboardContent(type.MediaType, type.CharSet, await context.ReadResponseAsync(stream).ConfigureAwait(false));
        }, cancellationToken);

    internal Task<T> DashboardJsonAsync<T>(OperationDescriptor operation, string path, DashboardContract? request,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var body = request is null ? null : NativeJson.SerializeUtf8(request);
        return ExecuteJsonAsync<T>(operation, new Uri(Origin, path), body, null, _ => { }, cancellationToken);
    }

    /// <summary>One save attempt. Only native's pre-write refusals are rejections; any other received failure leaves the effect unknown.</summary>
    internal async Task<NativeAttempt<DashboardProfile>> SaveDashboardProfileAsync(OperationDescriptor operation, string path,
        DashboardProfileSaveRequest request, string workspaceId, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var body = NativeJson.SerializeUtf8(request);
        var (correlationId, outcome, response, failure) = await AttemptAsync<DashboardProfile>(operation, new Uri(Origin, path), body,
            null, IsProfileSaveRefusal, cancellationToken,
            configure: message => message.Headers.Add("X-Zeroshot-Workspace", workspaceId)).ConfigureAwait(false);
        return new NativeAttempt<DashboardProfile>(Origin, operation.Name, correlationId, outcome, response, failure);
    }

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

    internal Task<DashboardRunEvents> OpenRunEventsAsync(OperationDescriptor operation, Uri requestUri, Cursor start,
        Cursor? lastEventId, CancellationToken cancellationToken)
        => OpenStreamAsync<DashboardRunEvent, DashboardRunEvents>(operation, requestUri, null,
            response => response.StatusCode == HttpStatusCode.OK && response.Content.Headers.ContentType?.MediaType == "text/event-stream",
            request =>
            {
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                if (lastEventId is not null) request.Headers.TryAddWithoutValidation("Last-Event-ID", lastEventId.Value);
            },
            // Native pages stay within 8 MiB; the configured message ceiling can only lower it.
            (queue, response, body) => new DashboardRunEvents(queue, response, body, start, Math.Min(limits.MessageBytes, 8 * 1024 * 1024)),
            cancellationToken);
}

/// <summary>
/// Native browser routes on an existing UI origin. Requests carry the exact origin Host and no
/// Origin or Sec-Fetch-Site header. Transformations return drafts; only a profile save stores anything, and nothing runs.
/// </summary>
public sealed class NativeDashboardClient
{
    // Native UI body limit (profile_ui.rs MAX_BODY); larger requests are refused before dispatch.
    private const int MaxDraftRequestBytes = 2 * 1024 * 1024;
    internal static readonly OperationDescriptor GetRootOperation = Browser("dashboard.getRoot");
    internal static readonly OperationDescriptor HeadRootOperation = Browser("dashboard.headRoot");
    internal static readonly OperationDescriptor GetUiOperation = Browser("dashboard.getUi");
    internal static readonly OperationDescriptor HeadUiOperation = Browser("dashboard.headUi");
    internal static readonly OperationDescriptor GetIndexOperation = Browser("dashboard.getIndex");
    internal static readonly OperationDescriptor HeadIndexOperation = Browser("dashboard.headIndex");
    internal static readonly OperationDescriptor GetAssetOperation = Browser("dashboard.getAsset");
    internal static readonly OperationDescriptor HeadAssetOperation = Browser("dashboard.headAsset");
    internal static readonly OperationDescriptor GetBootstrapOperation = Browser("dashboard.getBootstrap");
    internal static readonly OperationDescriptor HeadBootstrapOperation = Browser("dashboard.headBootstrap");
    internal static readonly OperationDescriptor ValidateOperation = Browser("dashboard.validate", MaxDraftRequestBytes);
    internal static readonly OperationDescriptor AuthoringOperation = Browser("dashboard.authoring", MaxDraftRequestBytes);
    internal static readonly OperationDescriptor DataOperation = Browser("dashboard.data", MaxDraftRequestBytes);
    internal static readonly OperationDescriptor ListProfilesOperation = Browser("dashboard.listProfiles");
    internal static readonly OperationDescriptor HeadProfilesOperation = Browser("dashboard.headProfiles");
    internal static readonly OperationDescriptor GetProfileOperation = Browser("dashboard.getProfile");
    internal static readonly OperationDescriptor HeadProfileOperation = Browser("dashboard.headProfile");
    internal static readonly OperationDescriptor SaveProfileOperation = Browser("dashboard.saveProfile", MaxDraftRequestBytes);
    // Native serves these with the run-history handlers behind the discovered direct-target routes.
    internal static readonly OperationDescriptor ListRunsOperation = History("dashboard.listRuns", 4);
    internal static readonly OperationDescriptor HeadRunsOperation = History("dashboard.headRuns", 4);
    internal static readonly OperationDescriptor GetRunOperation = History("dashboard.getRun", 8);
    internal static readonly OperationDescriptor HeadRunOperation = History("dashboard.headRun", 8);
    internal static readonly OperationDescriptor GetHistoryOperation = History("dashboard.getHistory", 8);
    internal static readonly OperationDescriptor HeadHistoryOperation = History("dashboard.headHistory", 8);
    internal static readonly OperationDescriptor RunEventsOperation = History("dashboard.runEvents", 8);
    internal static readonly OperationDescriptor HeadRunEventsOperation = History("dashboard.headRunEvents", 8);
    private const string RunsPath = "/ui/api/runs{?after}";
    private const string RunPath = "/ui/api/runs/{run_id}";
    private const string HistoryPath = "/ui/api/runs/{run_id}/history{?after}";
    private const string EventsPath = "/ui/api/runs/{run_id}/events{?after}";
    private static readonly Cursor InitialCursor = new("v2:0");
    private const string BootstrapPath = "/ui/api/bootstrap";
    private const string ProfilesPath = "/ui/api/profiles";
    private readonly NativeClient client;
    internal NativeDashboardClient(NativeClient client) => this.client = client;

    private static OperationDescriptor Browser(string name, int? requestBytes = null)
        => new(name, OperationTransport.Http, requestBytes: requestBytes, uiRouter: true);
    private static OperationDescriptor History(string name, int responseMebibytes)
        => new(name, OperationTransport.Http, responseBytes: responseMebibytes * 1024 * 1024, problemBytes: 64 * 1024,
            uiRouter: true, historyProblems: true);

    /// <summary>`GET /`: native redirects to `/ui/`.</summary>
    public Task<DashboardRedirect> GetRootAsync(CancellationToken cancellationToken = default)
        => client.DashboardRedirectAsync(GetRootOperation, HttpMethod.Get, "/", cancellationToken);
    public Task<DashboardRedirect> HeadRootAsync(CancellationToken cancellationToken = default)
        => client.DashboardRedirectAsync(HeadRootOperation, HttpMethod.Head, "/", cancellationToken);
    /// <summary>`GET /ui`: native redirects to `/ui/`.</summary>
    public Task<DashboardRedirect> GetUiAsync(CancellationToken cancellationToken = default)
        => client.DashboardRedirectAsync(GetUiOperation, HttpMethod.Get, "/ui", cancellationToken);
    public Task<DashboardRedirect> HeadUiAsync(CancellationToken cancellationToken = default)
        => client.DashboardRedirectAsync(HeadUiOperation, HttpMethod.Head, "/ui", cancellationToken);

    /// <summary>`GET /ui/`: the embedded index document.</summary>
    public Task<DashboardContent> GetIndexAsync(CancellationToken cancellationToken = default)
        => client.DashboardContentAsync(GetIndexOperation, new Uri(client.Origin, "/ui/"), cancellationToken);
    public Task<NativeHeadResult> HeadIndexAsync(CancellationToken cancellationToken = default)
        => client.ExecuteHeadAsync(HeadIndexOperation, new Uri(client.Origin, "/ui/"), null, cancellationToken);

    /// <summary>`GET /ui/{*asset}` for a relative asset path such as `assets/app.js`. A missing asset is a bare 404.</summary>
    public Task<DashboardContent> GetAssetAsync(string assetPath, CancellationToken cancellationToken = default)
        => client.DashboardContentAsync(GetAssetOperation, AssetUri(assetPath), cancellationToken);
    public Task<NativeHeadResult> HeadAssetAsync(string assetPath, CancellationToken cancellationToken = default)
        => client.ExecuteHeadAsync(HeadAssetOperation, AssetUri(assetPath), null, cancellationToken);

    /// <summary>`GET /ui/api/bootstrap`: templates, worker options, runtime schema and workspace identity.</summary>
    public Task<DashboardBootstrap> GetBootstrapAsync(CancellationToken cancellationToken = default)
        => client.DashboardJsonAsync<DashboardBootstrap>(GetBootstrapOperation, BootstrapPath, null, cancellationToken);
    public Task<NativeHeadResult> HeadBootstrapAsync(CancellationToken cancellationToken = default)
        => client.ExecuteHeadAsync(HeadBootstrapOperation, new Uri(client.Origin, BootstrapPath), null, cancellationToken);

    /// <summary>`POST /ui/api/validate`: native profile admission. An inadmissible profile is a 422 `invalid_profile` problem.</summary>
    public Task<DashboardValidation> ValidateAsync(DashboardProfileDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        return client.DashboardJsonAsync<DashboardValidation>(ValidateOperation, "/ui/api/validate", document, cancellationToken);
    }

    /// <summary>`POST /ui/api/authoring`: applies one outcome edit to a draft.</summary>
    public Task<DashboardAuthoringDraft> AuthorAsync(DashboardAuthoringRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return client.DashboardJsonAsync<DashboardAuthoringDraft>(AuthoringOperation, "/ui/api/authoring", request, cancellationToken);
    }

    /// <summary>`POST /ui/api/data`: applies one input/output edit to a draft.</summary>
    public Task<DashboardDataDraft> TransformDataAsync(DashboardDataRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return client.DashboardJsonAsync<DashboardDataDraft>(DataOperation, "/ui/api/data", request, cancellationToken);
    }

    /// <summary>`GET /ui/api/profiles`: summaries of the UI store's user-scope profiles.</summary>
    public Task<RunProfileListResult> ListProfilesAsync(CancellationToken cancellationToken = default)
        => client.DashboardJsonAsync<RunProfileListResult>(ListProfilesOperation, ProfilesPath, null, cancellationToken);
    public Task<NativeHeadResult> HeadProfilesAsync(CancellationToken cancellationToken = default)
        => client.ExecuteHeadAsync(HeadProfilesOperation, new Uri(client.Origin, ProfilesPath), null, cancellationToken);

    /// <summary>
    /// `GET /ui/api/profiles/{name}`: one user-scope profile and its current revision. Native reports a missing
    /// profile as 500 <c>profile_store_error</c>, not 404.
    /// </summary>
    public Task<DashboardProfile> GetProfileAsync(RunProfileName name, CancellationToken cancellationToken = default)
        => client.DashboardJsonAsync<DashboardProfile>(GetProfileOperation, ProfilePath(name), null, cancellationToken);
    public Task<NativeHeadResult> HeadProfileAsync(RunProfileName name, CancellationToken cancellationToken = default)
        => client.ExecuteHeadAsync(HeadProfileOperation, new Uri(client.Origin, ProfilePath(name)), null, cancellationToken);

    /// <summary>
    /// `POST /ui/api/profiles`: one compare-and-swap save, sent once with <paramref name="workspaceId"/> (from
    /// <see cref="DashboardBootstrap.Workspace"/>) verbatim as <c>X-Zeroshot-Workspace</c>. 409 <c>workspace_changed</c>
    /// and <c>profile_conflict</c>, 422 <c>invalid_profile</c> and boundary refusals are rejected attempts; any other
    /// failure after dispatch, including a lost reply or 500 <c>profile_store_error</c>, is unknown and never retried.
    /// </summary>
    public Task<NativeAttempt<DashboardProfile>> SaveProfileAsync(DashboardProfileSaveRequest request, string workspaceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(workspaceId);
        return client.SaveDashboardProfileAsync(SaveProfileOperation, ProfilesPath, request, workspaceId, cancellationToken);
    }

    /// <summary>`GET /ui/api/runs`: one run list page, optionally strictly after a canonical UUIDv7 run ID.</summary>
    public Task<RunHistoryList> ListRunsAsync(RunId? after = null, CancellationToken cancellationToken = default)
        => client.ExecuteJsonAsync<RunHistoryList>(ListRunsOperation, RunsUri(after), null, null,
            list => RunHistoryRules.List(list, after), cancellationToken);
    public Task<NativeHeadResult> HeadRunsAsync(RunId? after = null, CancellationToken cancellationToken = default)
        => client.ExecuteHeadAsync(HeadRunsOperation, RunsUri(after), null, cancellationToken);

    /// <summary>`GET /ui/api/runs/{id}`: the admitted run definition.</summary>
    public Task<RunDefinition> GetRunAsync(RunId runId, CancellationToken cancellationToken = default)
        => client.ExecuteJsonAsync<RunDefinition>(GetRunOperation, RunUri(RunPath, runId, null), null, null,
            definition => RunHistoryRules.Definition(definition, runId), cancellationToken);
    public Task<NativeHeadResult> HeadRunAsync(RunId runId, CancellationToken cancellationToken = default)
        => client.ExecuteHeadAsync(HeadRunOperation, RunUri(RunPath, runId, null), null, cancellationToken);

    /// <summary>`GET /ui/api/runs/{id}/history`: one page strictly after <paramref name="after"/>, or from <c>v2:0</c>.</summary>
    public Task<HistoryPage> GetHistoryAsync(RunId runId, Cursor? after = null, CancellationToken cancellationToken = default)
        => client.ExecuteJsonAsync<HistoryPage>(GetHistoryOperation, RunUri(HistoryPath, runId, after), null, null,
            page => RunHistoryRules.Page(page, after ?? InitialCursor), cancellationToken);
    public Task<NativeHeadResult> HeadHistoryAsync(RunId runId, Cursor? after = null, CancellationToken cancellationToken = default)
        => client.ExecuteHeadAsync(HeadHistoryOperation, RunUri(HistoryPath, runId, after), null, cancellationToken);

    /// <summary>
    /// `GET /ui/api/runs/{id}/events`: one bounded SSE observation of history pages. Both cursors are sent when
    /// supplied; native resumes after <paramref name="lastEventId"/> in preference to <paramref name="after"/>.
    /// Never reopens. A refusal before the stream starts is a <see cref="NativeHttpException"/>.
    /// </summary>
    public Task<DashboardRunEvents> OpenRunEventsAsync(RunId runId, Cursor? after = null, Cursor? lastEventId = null,
        CancellationToken cancellationToken = default)
    {
        var url = RunUri(EventsPath, runId, after);
        if (lastEventId is not null) RequireCursor(lastEventId, nameof(lastEventId));
        return client.OpenRunEventsAsync(RunEventsOperation, url, lastEventId ?? after ?? InitialCursor, lastEventId, cancellationToken);
    }
    public Task<NativeHeadResult> HeadRunEventsAsync(RunId runId, Cursor? after = null, CancellationToken cancellationToken = default)
        => client.ExecuteHeadAsync(HeadRunEventsOperation, RunUri(EventsPath, runId, after), null, cancellationToken);

    private static string ProfilePath(RunProfileName name)
    {
        ArgumentNullException.ThrowIfNull(name);
        // Profile names are unreserved path characters that cannot form a dot segment.
        return ProfilesPath + "/" + name.Value;
    }

    private Uri RunsUri(RunId? after)
    {
        if (after is not null) NativeHistoryClient.RequireRunId(after, nameof(after));
        return NativeRoutes.RunIdRoute(client.Origin, RunsPath, null, NativeHistoryClient.AfterQuery, ("after", after?.Value));
    }

    private Uri RunUri(string template, RunId runId, Cursor? after)
    {
        NativeHistoryClient.RequireRunId(runId, nameof(runId));
        if (after is not null) RequireCursor(after, nameof(after));
        return NativeRoutes.RunIdRoute(client.Origin, template, runId.Value, template != RunPath ? NativeHistoryClient.AfterQuery : null,
            ("after", after?.Value));
    }

    private static void RequireCursor(Cursor cursor, string name)
    {
        if (!RunHistoryRules.TryCanonical(cursor, out _))
            throw new ArgumentException("A history cursor must be canonical v2:<sequence>.", name);
    }

    private Uri AssetUri(string assetPath)
    {
        ArgumentNullException.ThrowIfNull(assetPath);
        // Literal relative segments only: no empty, dot, escaped or query/fragment spelling.
        return NativeRoutes.CompileLiteralRoute(client.Origin, "/ui/" + assetPath);
    }
}
