using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

/// <summary>
/// A controlled in-process HTTP peer for every fixed, private, history, dashboard, hosted, OAuth and callback route.
/// Responses are source-backed fixtures from the SDK tests or the minimal records those tests use.
/// </summary>
sealed class HttpPeer(string fixtures, string profileJson) : HttpMessageHandler
{
    public const string HostedRun = "run-1";
    public const string HistoryRun = "018f5e78-7f95-7c22-8d98-3f15af20c991";
    const string Source = OecpPeer.Source;
    const string HostedStatus = """{"runId":"run-1","title":"test","source":""" + Source + ""","size":"small","atCursor":"cloud:2","status":{"phase":"queued"}}""";
    const string Checkpoint = """{"checkpointId":"opaque/a","sequence":1,"node":"worker","mapIndices":[0],"loopIterations":[],"createdAt":1700000000000}""";
    const string ConnectionSummary = """{"key":"github","scope":"user","kind":"static","fields":["GH_TOKEN"]}""";
    const string MergePlan = """{"planId":"plan-1","title":"Release","state":"queued","repository":"acme/project","branch":"main","submittedAt":"2026-09-27T00:00:00Z","expiresAt":"2026-09-28T00:00:00Z","runs":[{"name":"build","runId":"r-1","state":"blocked","needs":[],"sourceRevision":null,"readyAt":null,"queueExpiresAt":null,"terminalAt":null,"waitingReason":null,"errorCode":null}]}""";

    private readonly JsonNode history = JsonNode.Parse(File.ReadAllText(Path.Combine(fixtures, "history.json")))!;
    private readonly string bootstrap = File.ReadAllText(Path.Combine(fixtures, "dashboard-bootstrap.json"));

    public string? PreparedRunId { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = Respond(request);
        response.RequestMessage = request;
        return Task.FromResult(response);
    }

    private HttpResponseMessage Respond(HttpRequestMessage request)
    {
        var uri = request.RequestUri!;
        var route = $"{request.Method.Method} {uri.Host}{uri.AbsolutePath}";
        var head = request.Method == HttpMethod.Head;
        string History(string name) => history[name]!.ToJsonString();
        return route switch
        {
            // Fixed target routes.
            "GET target.example/.well-known/zeroshot-native-v2" => Json(File.ReadAllText(Path.Combine(fixtures, "discovery.json"))),
            "POST target.example/native-v2/oecp-session" => Json("""{"endpoint":"wss://target.example/native-v2/oecp","bearerToken":"oecp-session-token"}"""),
            "POST target.example/native-v2/run" => Json($$"""{"runId":"{{PreparedRunId}}"}"""),
            "POST target.example/native-v2/private-bootstrap" => new HttpResponseMessage(HttpStatusCode.NoContent) { Content = new ByteArrayContent([]) },
            $"GET target.example/native-v2/operator-diagnostics/{HistoryRun}" => Json("""{"diagnostics":[]}"""),
            "POST target.example/native-v2/history/definition" => Json(History("definition")),
            "POST target.example/native-v2/history/page" => Json(History("page")),

            // Direct target UI-mounted run history (GET and implicit HEAD).
            "GET target.example/native-v2/run-history" => Json(History("list")),
            $"GET target.example/native-v2/run-history/{HistoryRun}" => Json(History("definition")),
            $"GET target.example/native-v2/run-history/{HistoryRun}/page" => Json(History("page")),
            _ when head && uri.Host == "target.example" && uri.AbsolutePath.StartsWith("/native-v2/run-history") => Head("application/json"),

            // Discovered hosted capabilities from Fixtures/discovery.json.
            "GET target.example/api/runs" => Json($$"""{"runs":[{{HostedStatus}}]}"""),
            "GET target.example/api/runs/run-1" => Json(HostedStatus),
            "GET target.example/api/runs/run-1/watch" => Ndjson(
                """{"type":"event","event":{"subscriptionId":"sub-1","runId":"run-1","title":"test","source":""" + Source + ""","size":"small","cursor":"cloud:3","status":{"phase":"queued"}}}""",
                """{"type":"closed","reason":"done"}"""),
            "GET target.example/api/runs/run-1/logs" => Ndjson(
                """{"type":"event","event":{"subscriptionId":"sub-1","runId":"run-1","cursor":"cloud:4","timestamp":1,"execution":"worker:1","record":{"level":"info","target":"worker","message":"hello"}}}""",
                """{"type":"closed","reason":"done"}"""),
            "POST target.example/api/runs/run-1/force" => Json(HostedStatus, HttpStatusCode.Accepted),
            "POST target.example/api/runs/run-1/checkpoints" => Json($$"""{"runId":"run-1","checkpoints":[{{Checkpoint}}],"nextAfter":"opaque/a"}"""),
            "POST target.example/api/runs/run-1/resume" => Json("""{"runId":"run-2","resumedFrom":"run-1"}"""),
            "POST target.example/api/runs/run-1/discard" => Json("""{"runId":"run-1","discarded":true}"""),
            "GET target.example/history/runs" => Json(History("list")),
            $"GET target.example/history/runs/{HistoryRun}" => Json(History("definition")),
            $"GET target.example/history/runs/{HistoryRun}/page" => Json(History("page")),
            "POST target.example/connections/list" => Json($$"""{"connections":[{{ConnectionSummary}}]}"""),
            "POST target.example/connections/set" => Json($$"""{"connection":{{ConnectionSummary}}}""", HttpStatusCode.Created),
            "POST target.example/connections/delete" => Json("""{"deleted":true}"""),
            "POST target.example/profiles/list" => Json("""{"profiles":[{"id":"p-1","name":"review","scope":"user","isDefault":true}]}"""),
            "POST target.example/profiles/show" => Json(profileJson),
            "POST target.example/profiles/set" => Json($$"""{"profile":{{profileJson}}}"""),
            "POST target.example/profiles/delete" => Json("""{"deleted":true}"""),
            "POST target.example/profiles/default" => Json("""{"scope":"user","name":"review"}"""),
            "POST target.example/profiles/run" => Json("""{"runId":"018f5e78-7f95-7c22-8d98-3f15af20c992"}""", HttpStatusCode.Accepted),
            "POST target.example/plans/create" => Json(MergePlan),
            "GET target.example/plans/plans/plan-1" => Json(MergePlan),
            "POST target.example/plans/plans/plan-1/force" => Json(MergePlan),
            "GET target.example/.well-known/oauth-authorization-server" => Json(
                """{"issuer":"https://target.example","device_authorization_endpoint":"https://target.example/oauth/device","token_endpoint":"https://target.example/oauth/token","revocation_endpoint":"https://target.example/oauth/revoke"}"""),
            "POST target.example/oauth/device" => Json(
                """{"device_code":"device-code","user_code":"ABCD-EFGH","verification_uri":"https://login.example/device","expires_in":600,"interval":5}"""),
            "POST target.example/oauth/token" => Json(
                """{"access_token":"access","refresh_token":"refresh","token_type":"Bearer","expires_in":3600,"refresh_expires_in":2592000,"scope":"controller"}"""),
            "GET target.example/login" => Json("""{"kind":"openengine.target-session/v1","organization_id":"org-1"}"""),

            // Outbound host resolver callback.
            "POST resolver.example/host/resolve" => Json("""{"connections":{"github":{"GH_TOKEN":"resolved"}}}"""),

            // Browser dashboard mount.
            "GET 127.0.0.1/" or "HEAD 127.0.0.1/" or "GET 127.0.0.1/ui" or "HEAD 127.0.0.1/ui" => Redirect("/ui/"),
            "GET 127.0.0.1/ui/" => Content("<!doctype html>", "text/html; charset=utf-8"),
            "GET 127.0.0.1/ui/assets/app.js" => Content("export{}", "text/javascript; charset=utf-8"),
            "HEAD 127.0.0.1/ui/" => Head("text/html; charset=utf-8"),
            "HEAD 127.0.0.1/ui/assets/app.js" => Head("text/javascript; charset=utf-8"),
            "GET 127.0.0.1/ui/api/bootstrap" => Json(bootstrap),
            "POST 127.0.0.1/ui/api/validate" => Json("""{"valid":true}"""),
            "POST 127.0.0.1/ui/api/authoring" => Json(
                "{\"graph\":" + JsonNode.Parse(bootstrap)!["templates"]![0]!["graph"]!.ToJsonString() + ",\"runtime\":{\"nodes\":{}}}"),
            "POST 127.0.0.1/ui/api/data" => Json("""{"graph":{"draft":true},"runtime":null}"""),
            "GET 127.0.0.1/ui/api/profiles" => Json("""{"profiles":[{"id":"p-1","name":"review","scope":"user","isDefault":false}]}"""),
            "GET 127.0.0.1/ui/api/profiles/review" or "POST 127.0.0.1/ui/api/profiles" => Json($$"""{"profile":{{profileJson}},"revision":"rev-1"}"""),
            "GET 127.0.0.1/ui/api/runs" => Json(History("list")),
            $"GET 127.0.0.1/ui/api/runs/{HistoryRun}" => Json(History("definition")),
            $"GET 127.0.0.1/ui/api/runs/{HistoryRun}/history" => Json(History("page")),
            $"GET 127.0.0.1/ui/api/runs/{HistoryRun}/events" => Content(
                $"event: history\nid: {history["page"]!["nextCursor"]!.GetValue<string>()}\ndata: {History("page")}\n\n", "text/event-stream"),
            $"HEAD 127.0.0.1/ui/api/runs/{HistoryRun}/events" => Head("text/event-stream"),
            _ when head && uri.Host == "127.0.0.1" && uri.AbsolutePath.StartsWith("/ui/api/") => Head("application/json"),

            _ => throw new InvalidOperationException($"The controlled HTTP peer has no route for {route}.")
        };
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Content(string body, string type)
    {
        var reply = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)) };
        reply.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(type);
        return reply;
    }

    private static HttpResponseMessage Head(string type)
    {
        var reply = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        reply.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(type);
        return reply;
    }

    private static HttpResponseMessage Redirect(string location)
    {
        var reply = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect) { Content = new ByteArrayContent([]) };
        reply.Headers.TryAddWithoutValidation("Location", location);
        return reply;
    }

    private static HttpResponseMessage Ndjson(params string[] frames)
        => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(string.Join("\n", frames) + "\n")) };
}
