using System.Net;
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
}

/// <summary>
/// Native browser routes on an existing UI origin. Requests carry the exact origin Host and no
/// Origin or Sec-Fetch-Site header. Transformations return drafts; nothing is saved or run.
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
    private const string BootstrapPath = "/ui/api/bootstrap";
    private readonly NativeClient client;
    internal NativeDashboardClient(NativeClient client) => this.client = client;

    private static OperationDescriptor Browser(string name, int? requestBytes = null)
        => new(name, OperationTransport.Http, requestBytes: requestBytes, uiRouter: true);

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

    private Uri AssetUri(string assetPath)
    {
        ArgumentNullException.ThrowIfNull(assetPath);
        // Literal relative segments only: no empty, dot, escaped or query/fragment spelling.
        return NativeRoutes.CompileLiteralRoute(client.Origin, "/ui/" + assetPath);
    }
}
