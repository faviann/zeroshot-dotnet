using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Zeroshot.Native;
using Zeroshot.Native.Contracts;

if (args.Length != 2) throw new ArgumentException("Supply an existing direct target origin with its UI mount and the prepared asset directory.");
var origin = new Uri(args[0]);
await using var native = NativeClient.ForHttp(new NativeClientOptions { Origin = origin });
var dashboard = native.Dashboard;
void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
JsonNode Wire<T>(T value) where T : notnull => JsonNode.Parse(NativeJson.SerializeUtf8(value))!;

// Static routes: redirects are results, never followed; HEAD mirrors GET without a body.
var redirects = new List<object>();
foreach (var (name, redirect) in new[]
{
    ("GET /", await dashboard.GetRootAsync()), ("HEAD /", await dashboard.HeadRootAsync()),
    ("GET /ui", await dashboard.GetUiAsync()), ("HEAD /ui", await dashboard.HeadUiAsync())
})
{
    Require(redirect.StatusCode == HttpStatusCode.TemporaryRedirect && redirect.Location == "/ui/", $"Unexpected {name} redirect.");
    redirects.Add(new { route = name, status = (int)redirect.StatusCode, redirect.Location });
}
var index = await dashboard.GetIndexAsync();
var indexHead = await dashboard.HeadIndexAsync();
Require(index.MediaType == "text/html" && index.CharSet == "utf-8" && indexHead.MediaType == "text/html" && indexHead.ContentLength == index.Length,
    "Unexpected index document.");
var assetPaths = Regex.Matches(System.Text.Encoding.UTF8.GetString(index.ExportBody()), "\"\\./(assets/[^\"]+)\"").Select(m => m.Groups[1].Value).Distinct().ToArray();
Require(assetPaths.Length > 0, "The index references no embedded assets.");
var assets = new List<object>();
foreach (var path in assetPaths)
{
    var asset = await dashboard.GetAssetAsync(path);
    var head = await dashboard.HeadAssetAsync(path);
    Require(head.MediaType == asset.MediaType && head.ContentLength == asset.Length && asset.Length > 0, $"Asset {path} HEAD/GET mismatch.");
    assets.Add(new { path, asset.MediaType, asset.CharSet, asset.Length, headLength = head.ContentLength });
}
foreach (var missing in new Func<Task>[] { () => dashboard.GetAssetAsync("assets/missing-witness.js"), () => dashboard.HeadAssetAsync("assets/missing-witness.js") })
{
    try { await missing(); throw new InvalidOperationException("A missing asset was served."); }
    catch (NativeHttpException error) when (error.Kind == NativeHttpFailureKind.HttpStatus && error.StatusCode == HttpStatusCode.NotFound && error.UiProblem is null) { }
}

// Bootstrap: the complete native catalog with this target's workspace identity.
var bootstrap = await dashboard.GetBootstrapAsync();
var bootstrapHead = await dashboard.HeadBootstrapAsync();
Require(bootstrap.Workspace.Kind == DashboardWorkspaceKind.Target && bootstrapHead.MediaType == "application/json" &&
    bootstrapHead.ContentLength > 0, "Unexpected bootstrap workspace or HEAD.");
Require(bootstrap.Templates.Select(t => t.Id).SequenceEqual(["single-worker:none", "software-change:none", "software-change:push",
    "software-change:pull_request", "software-change:merge", "auto-research:none", "auto-research:push"]), "Unexpected native templates.");
Require(bootstrap.Workers.Select(w => w.Id).SequenceEqual(["agent", "git_delivery_push", "git_delivery_pr", "git_delivery_merge"]) &&
    bootstrap.Workers[0] is DashboardAgentWorker && bootstrap.Workers.Skip(1).All(w => w is DashboardGitDeliveryWorker { RuntimeBinding: GitDeliveryBinding }),
    "Unexpected native workers.");

// Native admission of the complete harness-generated asset, and of an unbound draft.
var graph = NativeJson.DeserializeUtf8<GraphSpec>(File.ReadAllBytes(Path.Combine(args[1], "graph.json")));
var runtime = NativeJson.DeserializeUtf8<RuntimePlan>(File.ReadAllBytes(Path.Combine(args[1], "runtime.json")));
var valid = await dashboard.ValidateAsync(new DashboardProfileDocument { Graph = graph, Runtime = runtime });
var single = bootstrap.Templates[0].Graph;
var unbound = NativeJson.DeserializeUtf8<RuntimePlan>("""{"harness":"codex","provider":"openai","size":"small","nodes":{}}"""u8);
var invalid = await Problem(() => dashboard.ValidateAsync(new DashboardProfileDocument { Graph = single, Runtime = unbound }), HttpStatusCode.UnprocessableEntity, "invalid_profile");

// Draft transformations return edited drafts only.
var draftRuntime = JsonDocument.Parse("""{"nodes":{"worker":{"kind":"agent","model":""}}}""").RootElement.Clone();
var authored = await dashboard.AuthorAsync(new DashboardAuthoringRequest
{
    Graph = single, Runtime = draftRuntime,
    Action = new FailureReasonAuthoringAction { Terminal = new("worker_failed"), Reason = new("worker_gave_up") }
});
Require(Wire(authored.Graph).ToJsonString().Contains("\"reason\":\"worker_gave_up\"") && authored.Runtime.GetRawText() == draftRuntime.GetRawText(),
    "Native did not apply the failure-reason edit.");
var misplaced = await Problem(() => dashboard.AuthorAsync(new DashboardAuthoringRequest
{
    Graph = single, Runtime = draftRuntime,
    Action = new FailureReasonAuthoringAction { Terminal = new("worker"), Reason = new("worker_gave_up") }
}), HttpStatusCode.UnprocessableEntity, "invalid_profile");
var singleJson = JsonDocument.Parse(NativeJson.SerializeUtf8(single)).RootElement.Clone();
var added = await dashboard.TransformDataAsync(new DashboardDataRequest
{
    Graph = singleJson, Runtime = draftRuntime,
    Action = new RunInputFieldDataAction { Name = new("title"), Type = new StringPayload(), Required = true }
});
Require(added.Graph.GetProperty("initialInput").GetProperty("fields").TryGetProperty("title", out _), "Native did not add the run input.");
var removed = await dashboard.TransformDataAsync(new DashboardDataRequest
{
    Graph = added.Graph, Runtime = added.Runtime, Action = new RemoveRunInputDataAction { Name = new("title") }
});
Require(JsonNode.DeepEquals(JsonNode.Parse(removed.Graph.GetRawText())!["initialInput"], JsonNode.Parse(singleJson.GetRawText())!["initialInput"]),
    "Removing the added run input did not restore the draft.");

// The same client still reaches the fixed target router after UI-routed exchanges.
var discovery = await native.Target.DiscoverAsync();
Require(discovery.RunPath == "/native-v2/run", "Fixed-route discovery failed after dashboard requests.");

// Run history through the browser routes: the same native handlers as the discovered history capability.
var runs = await dashboard.ListRunsAsync();
Require(Wire(runs).ToJsonString() == Wire(await native.History.ListAsync(discovery)).ToJsonString(), "Dashboard and discovered run lists differ.");
var run = runs.Runs.First(r => r is { HistoryAvailable: true, Phase: RunHistoryPhase.Finished }).RunId;
var definition = await dashboard.GetRunAsync(run);
var page = await dashboard.GetHistoryAsync(run);
Require(Wire(definition).ToJsonString() == Wire(await native.History.DetailAsync(discovery, run)).ToJsonString() &&
    Wire(page).ToJsonString() == Wire(await native.History.PageAsync(discovery, run)).ToJsonString() && page.Complete && page.Events.Length >= 2,
    "Dashboard and discovered run history differ.");
var historyHeads = new[]
{
    await dashboard.HeadRunsAsync(), await dashboard.HeadRunAsync(run), await dashboard.HeadHistoryAsync(run), await dashboard.HeadRunEventsAsync(run)
};
Require(historyHeads.All(h => h.StatusCode == HttpStatusCode.OK) && historyHeads[..3].All(h => h.MediaType == "application/json") &&
    historyHeads[3].MediaType == "text/event-stream", "Unexpected run history HEAD.");

// One SSE observation of the finished run: its retained pages, then native closes the stream.
var streamed = new List<HistoryEventRecord>();
NativeSubscriptionCompletion streamClose;
int sseEvents;
await using (var events = await dashboard.OpenRunEventsAsync(run))
{
    var received = new List<DashboardRunEvent>();
    await foreach (var entry in events.ReadAllAsync()) received.Add(entry);
    sseEvents = received.Count;
    streamed.AddRange(received.Cast<DashboardHistoryPageEvent>().SelectMany(e => e.Page.Events));
    streamClose = await events.Completion;
    Require(received[^1] is DashboardHistoryPageEvent { Page: { Complete: true, Observation.State: HistoryObservationState.Complete } } &&
        events.LastDeliveredCursor == page.NextCursor && streamClose is { Origin: NativeSubscriptionOrigin.ServerClosed, Failure: null },
        "The finished run's event stream did not end after its complete page.");
}
Require(streamed.Select(e => Wire(e).ToJsonString()).SequenceEqual(page.Events.Select(e => Wire(e).ToJsonString())),
    "Streamed history differs from the history page.");
// Native resumes after Last-Event-ID in preference to the after query.
Cursor resumedFrom;
await using (var resumed = await dashboard.OpenRunEventsAsync(run, after: new Cursor("v2:0"), lastEventId: page.Events[0].Cursor))
{
    await using var reader = resumed.ReadAllAsync().GetAsyncEnumerator();
    Require(await reader.MoveNextAsync() && reader.Current is DashboardHistoryPageEvent, "No resumed history page.");
    resumedFrom = ((DashboardHistoryPageEvent)reader.Current).Page.Events[0].Cursor;
    Require(resumedFrom == page.Events[1].Cursor, "Native did not resume after Last-Event-ID.");
}
var missingRun = await Problem(() => dashboard.OpenRunEventsAsync(new RunId("0195af77-1000-7000-8000-000000000099")), HttpStatusCode.NotFound, "run_not_found");
var aheadCursor = await Problem(() => dashboard.OpenRunEventsAsync(run, lastEventId: new Cursor("v2:999")), HttpStatusCode.BadRequest, "invalid_cursor");

// Native browser-boundary refusals, provoked by a consumer-owned handler the library does not offer.
var origin403 = await Refused(request => request.Headers.TryAddWithoutValidation("Origin", "http://evil.example"),
    d => d.GetBootstrapAsync(), HttpStatusCode.Forbidden, "origin_rejected");
var eventsOrigin403 = await Refused(request => request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site"),
    d => d.OpenRunEventsAsync(run), HttpStatusCode.Forbidden, "origin_rejected");
var media415 = await Refused(request => { if (request.Content is not null) request.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain"); },
    d => d.ValidateAsync(new DashboardProfileDocument { Graph = graph, Runtime = runtime }), HttpStatusCode.UnsupportedMediaType, "json_required");

Console.WriteLine(JsonSerializer.Serialize(new
{
    redirects, index = new { index.MediaType, index.CharSet, index.Length, headLength = indexHead.ContentLength }, assets,
    bootstrap = new
    {
        templates = bootstrap.Templates.Select(t => t.Id), workers = bootstrap.Workers.Select(w => w.Id),
        workspace = Wire(bootstrap.Workspace), headLength = bootstrapHead.ContentLength
    },
    validation = new { completeAsset = valid.Valid, unboundDraft = invalid },
    authoring = new { edited = Wire(authored.Graph)["root"], misplaced },
    data = new { added = added.Graph.GetProperty("initialInput"), removed = removed.Graph.GetProperty("initialInput") },
    discoveryAfterDashboard = discovery.Kind,
    history = new
    {
        runs = runs.Runs.Select(r => r.RunId.Value), run = run.Value, pageEvents = page.Events.Length,
        heads = historyHeads.Select(h => new { status = (int)h.StatusCode, h.MediaType, h.ContentLength }),
        stream = new { sseEvents, events = streamed.Count, close = streamClose.Origin.ToString(), resumedFrom = resumedFrom.Value },
        refusals = new { missingRun, aheadCursor }
    },
    refusals = new { origin = origin403, eventsOrigin = eventsOrigin403, mediaType = media415 }
}));

static async Task<object> Problem(Func<Task> call, HttpStatusCode status, string code)
{
    try { await call(); }
    catch (NativeHttpException error) when (error.Kind == NativeHttpFailureKind.HttpStatus && error.StatusCode == status && error.UiProblem?.Code == code)
    { return new { status = (int)status, error.UiProblem.Code, error.UiProblem.Message }; }
    throw new InvalidOperationException($"Expected native {code}.");
}

async Task<object> Refused(Action<HttpRequestMessage> tamper, Func<NativeDashboardClient, Task> call, HttpStatusCode status, string code)
{
    using var http = new HttpClient(new Tamper(tamper) { InnerHandler = NativeClient.CreateHttpHandler() }) { Timeout = Timeout.InfiniteTimeSpan };
    await using var tampered = NativeClient.ForHttp(new NativeClientOptions { Origin = origin }, http);
    return await Problem(() => call(tampered.Dashboard), status, code);
}

sealed class Tamper(Action<HttpRequestMessage> tamper) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        tamper(request);
        return base.SendAsync(request, cancellationToken);
    }
}
