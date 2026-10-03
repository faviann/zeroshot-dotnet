using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Zeroshot;
using Zeroshot.Native;
using Zeroshot.Native.Contracts;

// Invokes every implemented coverage.json binding once, through the packed Zeroshot.Client package only, against
// controlled peers answering with source-backed fixtures. It then requires the invoked set to equal the manifest's
// implemented rows. This is binding-reachability evidence, not live-native conformance, and it applies no SDK
// build assertion, waiting or recovery policy.
if (args.Length != 1) throw new ArgumentException("Supply the path to docs/contracts/coverage.json.");
var fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");
using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(60));
var token = cancel.Token;
var invoked = new SortedSet<string>(StringComparer.Ordinal);
void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
async Task<T> Call<T>(string id, Task<T> call) { var result = await call; invoked.Add(id); return result; }
async Task CallVoid(string id, Task call) { await call; invoked.Add(id); }
T Parse<T>(string json) => NativeJson.DeserializeUtf8<T>(Encoding.UTF8.GetBytes(json));
JsonElement Element(string json) => JsonDocument.Parse(json).RootElement.Clone();
void Acknowledged<T>(NativeAttempt<T> attempt, string id) where T : class
    => Require(attempt is { Outcome: NativeAttemptOutcome.Acknowledged, Response: not null }, $"{id} was {attempt.Outcome}.");

var graph = Parse<GraphSpec>(JsonNode.Parse(File.ReadAllText(Path.Combine(fixtures, "graphs.json")))![0]!.ToJsonString());
var runtime = Parse<RuntimePlan>(JsonNode.Parse(File.ReadAllText(Path.Combine(fixtures, "runtimes.json")))![0]!.ToJsonString());
var profileJson = $$"""{"id":"p-1","name":"review","scope":"user","graph":{{Encoding.UTF8.GetString(NativeJson.SerializeUtf8(graph))}},"runtime":{{Encoding.UTF8.GetString(NativeJson.SerializeUtf8(runtime))}},"isDefault":true}""";
var peer = new HttpPeer(fixtures, profileJson);
using var http = new HttpClient(peer) { Timeout = Timeout.InfiniteTimeSpan };

// Fixed target routes, discovered hosted capabilities, OAuth and private routes share one https origin.
await using var native = NativeClient.ForHttp(new NativeClientOptions { Origin = new Uri("https://target.example/") }, http);
var hosted = new TargetControlCredentials(TargetAuthentication.HostedOauth, "hosted-access");
var discovery = await Call("http:GET /.well-known/zeroshot-native-v2", native.Target.DiscoverAsync(token));
Require(discovery.Extensions?.HostedRuns is not null && discovery.Oauth is not null, "Hosted discovery fixture lost its capabilities.");
var session = await Call("http:POST /native-v2/oecp-session", native.Target.CreateOecpSessionAsync(discovery, credentials: hosted, cancellationToken: token));
Require(session.BearerToken == "oecp-session-token", "Unexpected OECP session.");
var prepared = PreparedSubmission.ImportUtf8(File.ReadAllBytes(Path.Combine(fixtures, "prepared.json")));
peer.PreparedRunId = prepared.RunId.Value;
var noConnections = ImmutableDictionary<string, ImmutableDictionary<string, string>>.Empty;
var submitted = await Call("http:POST /native-v2/run",
    native.Target.SubmitAttemptAsync(prepared, new TargetRunCredentials { Connections = noConnections }, hosted, token));
Require(submitted.Outcome == NativeAttemptOutcome.Acknowledged, "Submission was not acknowledged.");

var privateDiscovery = new TargetDiscoveryDocument
{
    Kind = discovery.Kind, Audience = discovery.Audience, Authentication = TargetAuthentication.PrivateCapability,
    RunPath = discovery.RunPath, SessionPath = discovery.SessionPath, OecpPath = discovery.OecpPath,
    PrivateBootstrapPath = "/native-v2/private-bootstrap"
};
Acknowledged(await Call("http:POST /native-v2/private-bootstrap", native.Private.BootstrapAsync(privateDiscovery,
    new TargetPrivateBootstrapRequest { Nonce = new string('a', 24), Ciphertext = string.Concat(Enumerable.Repeat("0123456789abcdef", 10)) }, token)), "bootstrap");
var capability = new TargetControlCredentials(TargetAuthentication.PrivateCapability, new string('c', 64));
var historyRun = new RunId(HttpPeer.HistoryRun);
Require((await Call("http:GET /native-v2/operator-diagnostics/{runId}", native.Private.GetOperatorDiagnosticsAsync(historyRun, capability, token))).Diagnostics.IsEmpty,
    "Unexpected diagnostics.");
Require((await Call("http:POST /native-v2/history/definition", native.Private.GetHistoryDefinitionAsync(historyRun, capability, token))).RunId == historyRun,
    "Unexpected private definition.");
Require((await Call("http:POST /native-v2/history/page", native.Private.GetHistoryPageAsync(historyRun, capability, cancellationToken: token))).Events.Length > 0,
    "Unexpected private page.");

// Discovered run history, and the same binding over a direct target's UI mount including implicit HEAD.
Require((await Call("discovered:GET run_history.list", native.History.ListAsync(discovery, credentials: hosted, cancellationToken: token))).Runs.Length > 0, "Empty history list.");
Require((await Call("discovered:GET run_history.detail", native.History.DetailAsync(discovery, historyRun, hosted, token))).RunId == historyRun, "Wrong history detail.");
Require((await Call("discovered:GET run_history.page", native.History.PageAsync(discovery, historyRun, credentials: hosted, cancellationToken: token))).Events.Length > 0, "Empty history page.");
var direct = new TargetDiscoveryDocument
{
    Kind = discovery.Kind, Audience = discovery.Audience, Authentication = TargetAuthentication.None,
    RunPath = discovery.RunPath, SessionPath = discovery.SessionPath, OecpPath = discovery.OecpPath,
    Extensions = new()
    {
        RunHistory = new()
        {
            Kind = "zeroshot.run-history/v1", BaseUrl = "https://target.example/native-v2",
            RouteTemplates = new() { List = "/run-history{?after}", Detail = "/run-history/{run_id}", Page = "/run-history/{run_id}/page{?after}" }
        }
    }
};
Require((await Call("http:GET /native-v2/run-history", native.History.ListAsync(direct, cancellationToken: token))).Runs.Length > 0, "Empty direct list.");
Require((await Call("http:GET /native-v2/run-history/{id}", native.History.DetailAsync(direct, historyRun, cancellationToken: token))).RunId == historyRun, "Wrong direct detail.");
Require((await Call("http:GET /native-v2/run-history/{id}/page", native.History.PageAsync(direct, historyRun, cancellationToken: token))).Events.Length > 0, "Empty direct page.");
foreach (var (id, head) in new (string, Task<NativeHeadResult>)[]
{
    ("http:HEAD /native-v2/run-history", native.History.HeadListAsync(direct, cancellationToken: token)),
    ("http:HEAD /native-v2/run-history/{id}", native.History.HeadDetailAsync(direct, historyRun, cancellationToken: token)),
    ("http:HEAD /native-v2/run-history/{id}/page", native.History.HeadPageAsync(direct, historyRun, cancellationToken: token))
})
    Require((await Call(id, head)).StatusCode == HttpStatusCode.OK, $"{id} failed.");

// Hosted run lifecycle, workspace recovery, connections, profiles and merge plans.
var hostedRun = new RunId(HttpPeer.HostedRun);
Require((await Call("discovered:GET hosted_runs.list", native.HostedRuns.ListAsync(discovery, hosted, token))).Runs.Length == 1, "Hosted list.");
Require((await Call("discovered:GET hosted_runs.status", native.HostedRuns.StatusAsync(discovery, hostedRun, hosted, token))).RunId == hostedRun, "Hosted status.");
await using (var watch = await Call("discovered:GET hosted_runs.watch", native.HostedRuns.WatchAsync(discovery, new RunWatchParams { RunId = hostedRun }, hosted, token)))
{
    var records = 0;
    await foreach (var record in watch.ReadAllAsync(token)) records += record.RunId == hostedRun ? 1 : 0;
    Require(records == 1 && (await watch.Completion).Origin == NativeSubscriptionOrigin.ServerClosed, "Hosted watch.");
}
await using (var logs = await Call("discovered:GET hosted_runs.logs", native.HostedRuns.LogsAsync(discovery, new RunLogsParams { RunId = hostedRun }, hosted, token)))
{
    var records = 0;
    await foreach (var _ in logs.ReadAllAsync(token)) records++;
    Require(records == 1 && (await logs.Completion).Origin == NativeSubscriptionOrigin.ServerClosed, "Hosted logs.");
}
Acknowledged(await Call("discovered:POST hosted_runs.force", native.HostedRuns.ForceAsync(discovery, hostedRun, hosted, token)), "hosted force");
Require((await Call("discovered:POST hosted_workspace_recovery.checkpoints",
    native.HostedRecovery.CheckpointsAsync(discovery, new RunCheckpointsParams { RunId = hostedRun }, hosted, token))).Checkpoints.Length == 1, "Hosted checkpoints.");
Acknowledged(await Call("discovered:POST hosted_workspace_recovery.resume",
    native.HostedRecovery.ResumeAsync(discovery, hostedRun, new RunId("run-2"), hosted, cancellationToken: token)), "hosted resume");
Acknowledged(await Call("discovered:POST hosted_workspace_recovery.discard_workspace",
    native.HostedRecovery.DiscardWorkspaceAsync(discovery, hostedRun, hosted, token)), "hosted discard");

Require((await Call("discovered:POST connections.list",
    native.Connections.ListAsync(discovery, new ConnectionListRequest { Scope = ConnectionScope.User }, hosted, token))).Connections.Length == 1, "Connection list.");
Acknowledged(await Call("discovered:POST connections.set", native.Connections.SetAsync(discovery, new ConnectionSetRequest
{
    Key = new("github"), Scope = ConnectionScope.User, Values = ImmutableDictionary<string, string>.Empty.Add("GH_TOKEN", "value")
}, hosted, token)), "connection set");
Acknowledged(await Call("discovered:POST connections.delete",
    native.Connections.DeleteAsync(discovery, new ConnectionDeleteRequest { Key = new("github"), Scope = ConnectionScope.User }, hosted, token)), "connection delete");

var selector = new RunProfileSelector { Scope = RunProfileScope.User, Name = new("review") };
Require((await Call("discovered:POST run_profiles.list",
    native.Profiles.ListAsync(discovery, new RunProfileListRequest { Scope = RunProfileScope.User }, hosted, token))).Profiles.Length == 1, "Profile list.");
Require((await Call("discovered:POST run_profiles.show", native.Profiles.ShowAsync(discovery, selector, hosted, token))).Name == selector.Name, "Profile show.");
Acknowledged(await Call("discovered:POST run_profiles.set", native.Profiles.SetAsync(discovery,
    new RunProfileSetRequest { Name = new("review"), Scope = RunProfileScope.User, Graph = graph, Runtime = runtime }, hosted, token)), "profile set");
Acknowledged(await Call("discovered:POST run_profiles.delete", native.Profiles.DeleteAsync(discovery, selector, hosted, token)), "profile delete");
Acknowledged(await Call("discovered:POST run_profiles.default",
    native.Profiles.DefaultAsync(discovery, new RunProfileDefaultRequest { Scope = RunProfileScope.User, Name = new("review") }, hosted, token)), "profile default");
Acknowledged(await Call("discovered:POST run_profiles.run", native.Profiles.RunAsync(discovery, new RunProfileRunRequest
{
    RunId = new("018f5e78-7f95-7c22-8d98-3f15af20c991"), Profile = selector, Title = new("Profile run"), InitialInput = Element("""{"items":[null,1]}"""),
    Source = new() { Repository = new("acme/project"), Branch = new("main"), Revision = new(new string('a', 40)) },
    SubmissionKey = new("profile-key"), Connections = noConnections
}, hosted, token)), "profile run");

var plan = new RunId("plan-1");
Acknowledged(await Call("discovered:POST merge_plans.create", native.MergePlans.CreateAsync(discovery, new MergePlanSubmitRequest
{
    SubmissionKey = new("plan-key"), Title = new("Release"), ExpiresAt = "2026-09-28T00:00:00Z",
    Source = new() { Repository = new("acme/project"), Branch = new("main") },
    Profile = new() { Scope = RunProfileScope.Org, Name = new("software-change") },
    Runs = [new MergePlanRunRequest { Name = new("build"), InitialInput = Element("null") }]
}, hosted, token)), "merge create");
Require((await Call("discovered:GET merge_plans.status", native.MergePlans.StatusAsync(discovery, plan, hosted, token))).PlanId == plan, "Merge status.");
Acknowledged(await Call("discovered:POST merge_plans.force", native.MergePlans.ForceAsync(discovery, plan, hosted, token)), "merge force");

// Hosted OAuth: five individual native calls against the discovered authority.
Require((await Call("discovered:GET oauth.metadata", native.OAuth.MetadataAsync(discovery, token))).TokenEndpoint == discovery.Oauth!.TokenEndpoint, "OAuth metadata.");
var device = await Call("discovered:POST oauth.begin_device_authorization", native.OAuth.BeginDeviceAuthorizationAsync(discovery, token));
Acknowledged(await Call("discovered:POST oauth.poll_device_token", native.OAuth.ExchangeDeviceTokenAsync(discovery, device, Guid.NewGuid(), token)), "device token");
Acknowledged(await Call("discovered:POST oauth.refresh_access", native.OAuth.RefreshAsync(discovery, "refresh", token)), "refresh");
Require((await Call("discovered:GET oauth.verify_login_session", native.OAuth.VerifySessionAsync(discovery, hosted, token))).OrganizationId == "org-1", "Login session.");

// The outbound host resolver callback is a standalone client, independent of any target.
await using (var resolver = ConnectionResolverClient.ForHttp(new TargetConnectionResolver
{
    Endpoint = "https://resolver.example/host/resolve", BearerToken = "resolver-bearer", Keys = [new("github")]
}, httpClient: http))
{
    var resolved = await Call("callback:POST connectionResolver.endpoint", resolver.ResolveAsync(new ConnectionResolveRequest
    {
        RunId = new("0195af77-1000-7000-8000-000000000001"),
        Connections = ImmutableDictionary<string, ImmutableArray<EnvironmentVariableName>>.Empty.Add("github", [new("GH_TOKEN")])
    }, token));
    Require(resolved.Connections["github"]["GH_TOKEN"] == "resolved", "Resolver result.");
}

// Browser dashboard mount on a loopback UI origin.
await using (var ui = NativeClient.ForHttp(new NativeClientOptions { Origin = new Uri("http://127.0.0.1:4173/") }, http))
{
    var d = ui.Dashboard;
    foreach (var (id, redirect) in new (string, Task<DashboardRedirect>)[]
    {
        ("http:GET /", d.GetRootAsync(token)), ("http:HEAD /", d.HeadRootAsync(token)),
        ("http:GET /ui", d.GetUiAsync(token)), ("http:HEAD /ui", d.HeadUiAsync(token))
    })
        Require((await Call(id, redirect)).Location == "/ui/", $"{id} redirect.");
    Require((await Call("http:GET /ui/", d.GetIndexAsync(token))).MediaType == "text/html", "Index.");
    Require((await Call("http:GET /ui/{*asset}", d.GetAssetAsync("assets/app.js", token))).MediaType == "text/javascript", "Asset.");
    var bootstrap = await Call("http:GET /ui/api/bootstrap", d.GetBootstrapAsync(token));
    Require(bootstrap.Version == 1, "Bootstrap.");
    var uiGraph = bootstrap.Templates[0].Graph;
    var uiRuntime = Parse<RuntimePlan>("""{"harness":"codex","provider":"openai","size":"small","nodes":{"work":{"kind":"agent","model":"gpt-5.6-sol"}}}""");
    var draftRuntime = Element("""{"nodes":{"work":{"kind":"agent","model":""}}}""");
    Require((await Call("http:POST /ui/api/validate", d.ValidateAsync(new DashboardProfileDocument { Graph = uiGraph, Runtime = uiRuntime }, token))).Valid, "Validate.");
    Require((await Call("http:POST /ui/api/authoring", d.AuthorAsync(new DashboardAuthoringRequest
    {
        Graph = uiGraph, Runtime = draftRuntime, Action = new ProtectAuthoringAction { Node = new("work") }
    }, token))).Graph is not null, "Authoring.");
    Require((await Call("http:POST /ui/api/data", d.TransformDataAsync(new DashboardDataRequest
    {
        Graph = Element(Encoding.UTF8.GetString(NativeJson.SerializeUtf8(uiGraph))), Runtime = draftRuntime,
        Action = new RunInputFieldDataAction { Name = new("title"), Type = new StringPayload(), Required = true }
    }, token))).Graph.ValueKind == JsonValueKind.Object, "Data.");
    Require((await Call("http:GET /ui/api/profiles", d.ListProfilesAsync(token))).Profiles.Length == 1, "Dashboard profiles.");
    Require((await Call("http:GET /ui/api/profiles/{name}", d.GetProfileAsync(new("review"), token))).Revision == "rev-1", "Dashboard profile.");
    Acknowledged(await Call("http:POST /ui/api/profiles", d.SaveProfileAsync(new DashboardProfileSaveRequest
    {
        Name = new("review"), Graph = uiGraph, Runtime = uiRuntime
    }, "0199aa00-0000-7000-8000-000000000001", token)), "dashboard save");
    Require((await Call("http:GET /ui/api/runs", d.ListRunsAsync(cancellationToken: token))).Runs.Length > 0, "Dashboard runs.");
    Require((await Call("http:GET /ui/api/runs/{id}", d.GetRunAsync(historyRun, token))).RunId == historyRun, "Dashboard run.");
    Require((await Call("http:GET /ui/api/runs/{id}/history", d.GetHistoryAsync(historyRun, cancellationToken: token))).Events.Length > 0, "Dashboard history.");
    await using (var events = await Call("http:GET /ui/api/runs/{id}/events", d.OpenRunEventsAsync(historyRun, cancellationToken: token)))
    {
        var pages = 0;
        await foreach (var _ in events.ReadAllAsync(token)) pages++;
        Require(pages == 1 && (await events.Completion).Origin == NativeSubscriptionOrigin.ServerClosed, "Dashboard events.");
    }
    foreach (var (id, head) in new (string, Task<NativeHeadResult>)[]
    {
        ("http:HEAD /ui/", d.HeadIndexAsync(token)), ("http:HEAD /ui/{*asset}", d.HeadAssetAsync("assets/app.js", token)),
        ("http:HEAD /ui/api/bootstrap", d.HeadBootstrapAsync(token)), ("http:HEAD /ui/api/profiles", d.HeadProfilesAsync(token)),
        ("http:HEAD /ui/api/profiles/{name}", d.HeadProfileAsync(new("review"), token)), ("http:HEAD /ui/api/runs", d.HeadRunsAsync(cancellationToken: token)),
        ("http:HEAD /ui/api/runs/{id}", d.HeadRunAsync(historyRun, token)), ("http:HEAD /ui/api/runs/{id}/history", d.HeadHistoryAsync(historyRun, cancellationToken: token)),
        ("http:HEAD /ui/api/runs/{id}/events", d.HeadRunEventsAsync(historyRun, cancellationToken: token))
    })
        Require((await Call(id, head)).StatusCode == HttpStatusCode.OK, $"{id} failed.");
}

// OECP: all 22 methods and the subscription notifications over a borrowed NDJSON stream.
var oecp = new OecpPeer(fixtures);
// Short enough for macOS, whose socket paths hold at most 104 bytes and whose temporary directory is already long.
var socketPath = Path.Combine(Path.GetTempPath(), $"zsb-{Guid.NewGuid().ToString("N")[..16]}.sock");
using var unixListener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
unixListener.Bind(new UnixDomainSocketEndPoint(socketPath));
unixListener.Listen();
using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
var unixServer = oecp.ListenUnixAsync(unixListener, stop.Token);
try
{
    using var borrowed = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    await borrowed.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), token);
    await using var stream = new NetworkStream(borrowed, ownsSocket: false);
    await using (var connection = await Call("binding:ndjson-streams", OecpConnection.FromStreamsAsync(stream, stream, cancellationToken: token)))
        await ExerciseOecp(connection);

    await using (var unix = await Call("binding:unix-controller", OecpConnection.ConnectUnixAsync(socketPath, cancellationToken: token)))
    {
        Require((await unix.InitializeAsync(cancellationToken: token)).ProtocolVersion == OecpConnection.ProtocolVersion, "Unix initialize.");
        Require((await unix.Runs.StatusAsync(new RunId("run-1"), cancellationToken: token)).RunId.Value == "run-1", "Unix status.");
    }
}
finally
{
    stop.Cancel();
    File.Delete(socketPath);
}

// WebSocket: the upgrade route, the session binding and WebSocket-only $/cancelRequest.
var tcp = OecpPeer.LoopbackListener();
try
{
    var origin = new Uri($"http://127.0.0.1:{((IPEndPoint)tcp.LocalEndpoint).Port}/");
    var served = oecp.ServeWebSocketAsync(tcp, token);
    await using var loopback = NativeClient.ForHttp(new NativeClientOptions { Origin = origin });
    var wsSession = new TargetOecpSession { Endpoint = new UriBuilder(origin) { Scheme = "ws", Path = "/native-v2/oecp" }.Uri.AbsoluteUri };
    var ws = await Call("binding:websocket", loopback.ConnectOecpAsync(wsSession, token));
    invoked.Add("http:GET /native-v2/oecp");
    await using (ws)
    {
        Require((await ws.InitializeAsync(cancellationToken: token)).ProtocolVersion == OecpConnection.ProtocolVersion, "WebSocket initialize.");
        await CallVoid("oecp-notification:$/cancelRequest", ws.CancelRequestAsync(new RequestId(7), token));
        // Native sends no reply; a following call proves the peer processed the frame first.
        await ws.Runs.StatusAsync(new RunId("run-1"), cancellationToken: token);
        Require(oecp.CancelledRequests.SequenceEqual(["7"]), "The peer did not receive $/cancelRequest.");
    }
    await served;
}
finally { tcp.Stop(); }

async Task ExerciseOecp(OecpConnection c)
{
    TParams Golden<TParams>(string method) => NativeJson.DeserializeUtf8<TParams>(oecp.GoldenParams(method));
    var run = new RunId("run-1");
    Require((await Call("oecp:initialize", c.InitializeAsync(cancellationToken: token))).ProtocolVersion
        == OecpConnection.ProtocolVersion, "Initialize.");
    Require((await Call("oecp:plan", c.Cluster.PlanAsync(Golden<PlanParams>("plan"), cancellationToken: token))).Ok, "Plan.");
    Acknowledged(await Call("oecp:apply", c.Cluster.ApplyAsync(Golden<ApplyParams>("apply"), cancellationToken: token)), "apply");
    Acknowledged(await Call("oecp:update", c.Cluster.UpdateAsync(Golden<UpdateParams>("update"), cancellationToken: token)), "update");
    Acknowledged(await Call("oecp:stop", c.Cluster.StopAsync(Golden<StopParams>("stop"), cancellationToken: token)), "stop");
    Acknowledged(await Call("oecp:retry", c.Cluster.RetryAsync(Golden<RetryParams>("retry"), cancellationToken: token)), "retry");
    Acknowledged(await Call("oecp:resubmit", c.Cluster.ResubmitAsync(Golden<ResubmitParams>("resubmit"), cancellationToken: token)), "resubmit");
    Acknowledged(await Call("oecp:delete", c.Cluster.DeleteAsync(Golden<DeleteParams>("delete"), cancellationToken: token)), "delete");
    Require((await Call("oecp:get", c.Cluster.GetAsync(Golden<GetParams>("get"), cancellationToken: token))).Status is not null, "Get.");

    await Drain("oecp:watch", c.Cluster.WatchAsync(new WatchParams { RunId = run, FromCursor = new("cursor-0") }, token), 3);
    await Drain("oecp:logs", c.Cluster.LogsAsync(token), 3);
    await Drain("oecp:agent/attach", c.Cluster.AttachAgentAsync(new AgentAttachParams { Execution = new("execution-1") }, token), 4);

    var prepared = PreparedSubmission.ImportUtf8(File.ReadAllBytes(Path.Combine(fixtures, "prepared.json")));
    Acknowledged(await Call("oecp:run/submit", c.Runs.SubmitAsync(new RunSubmitParams { RunId = prepared.RunId, Submission = prepared.Submission }, cancellationToken: token)), "run/submit");
    Require((await Call("oecp:run/list", c.Runs.ListAsync(cancellationToken: token))).Runs.Length == 1, "Run list.");
    Require((await Call("oecp:run/status", c.Runs.StatusAsync(run, cancellationToken: token))).RunId == run, "Run status.");
    await Drain("oecp:run/watch", c.Runs.WatchAsync(new RunWatchParams { RunId = run, FromCursor = new("start") }, cancellationToken: token), 1);
    await Drain("oecp:run/logs", c.Runs.LogsAsync(new RunLogsParams { RunId = run, FromCursor = new("start") }, token), 1);
    await Drain("oecp:run/attach", c.Runs.AttachAsync(new RunAttachParams { RunId = run, Execution = new("worker:1") }, token), 3);
    Acknowledged(await Call("oecp:run/force", c.Runs.ForceAsync(run, cancellationToken: token)), "run/force");
    Require((await Call("oecp:run/checkpoints", c.Runs.CheckpointsAsync(new RunCheckpointsParams { RunId = run }, cancellationToken: token))).Checkpoints.Length == 1,
        "Checkpoints.");
    Acknowledged(await Call("oecp:run/resume", c.Runs.ResumeAsync(run, new RunId("run-2"), cancellationToken: token)), "run/resume");
    Acknowledged(await Call("oecp:run/discard_workspace", c.Runs.DiscardWorkspaceAsync(run, cancellationToken: token)), "run/discard_workspace");

    // Disposing an open subscription sends subscription/cancel; the next reply proves the peer has processed it.
    await using (var open = await c.Runs.LogsAsync(new RunLogsParams { RunId = run, FromCursor = new("held") }, token))
    {
        await using var records = open.ReadAllAsync(token).GetAsyncEnumerator(token);
        Require(await records.MoveNextAsync(), "The held subscription delivered nothing.");
    }
    await c.Runs.ListAsync(cancellationToken: token);
    Require(oecp.SawSubscriptionCancel("logs"), "The peer did not receive subscription/cancel.");
    invoked.Add("oecp-notification:subscription/cancel");
}

async Task Drain<TEstablishment, TEvent>(string id, Task<NativeSubscription<TEstablishment, TEvent>> establish, int expected)
{
    await using var subscription = await Call(id, establish);
    var records = 0;
    await foreach (var _ in subscription.ReadAllAsync(token)) records++;
    var completion = await subscription.Completion;
    Require(records == expected && completion.Origin == NativeSubscriptionOrigin.ServerClosed, $"{id} delivered {records} records, {completion.Origin}.");
    invoked.Add("oecp-notification:event");
    invoked.Add("oecp-notification:subscription/closed");
}

// Reconcile the invoked set with the single manifest.
using var manifest = JsonDocument.Parse(File.ReadAllBytes(args[0]));
var rows = manifest.RootElement.GetProperty("operations").EnumerateArray().ToList();
var ids = rows.Select(r => r.GetProperty("id").GetString()!).ToList();
Require(ids.Distinct().Count() == ids.Count, "coverage.json has duplicate IDs: " + string.Join(", ", ids.GroupBy(i => i).Where(g => g.Count() > 1).Select(g => g.Key)));
var classes = new Dictionary<string, string>
{
    ["live-native"] = "implemented", ["live-refusal-only"] = "implemented", ["fixture-only"] = "implemented",
    ["advertised-only"] = "advertised-only", ["schema-data"] = "schema-data"
};
foreach (var row in rows)
{
    var id = row.GetProperty("id").GetString();
    Require(row.TryGetProperty("bindingOwnerIssue", out var owner) && owner.ValueKind == JsonValueKind.Number && owner.GetInt32() > 0, $"{id} has no owner issue.");
    var status = row.GetProperty("status").GetString();
    var evidenceClass = row.TryGetProperty("evidenceClass", out var value) ? value.GetString() : null;
    Require(evidenceClass is not null && classes.TryGetValue(evidenceClass, out var expectedStatus) && expectedStatus == status,
        $"{id} has status {status} and evidence class {evidenceClass ?? "(none)"}.");
}
var implemented = rows.Where(r => r.GetProperty("status").GetString() == "implemented").Select(r => r.GetProperty("id").GetString()!).ToHashSet();
// Exercised by the Windows pipe tests and windows-native-witness workflow, not by this consumer.
var notApplicable = new HashSet<string> { "binding:windows-controller" };
var missing = implemented.Except(invoked).Except(notApplicable).Order().ToList();
var unlisted = invoked.Except(implemented).Order().ToList();
Require(missing.Count == 0 && unlisted.Count == 0,
    $"Invoked bindings differ from implemented coverage rows. Not invoked: [{string.Join(", ", missing)}]. Not in the manifest: [{string.Join(", ", unlisted)}].");
Console.WriteLine(JsonSerializer.Serialize(new
{
    manifestRows = rows.Count, implemented = implemented.Count, invoked = invoked.Count,
    exempt = notApplicable.Order(), evidence = "controlled source-backed peers; not live-native conformance"
}));
