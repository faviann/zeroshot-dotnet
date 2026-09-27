using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using TUnit.Core;
using Zeroshot.Native;
using Zeroshot.Native.Contracts;

namespace Zeroshot.Client.Tests;

public sealed class HttpConnectionTests
{
    private const string Bearer = "ACCESS-BEARER-CANARY";
    private const string Secret = "SECRET-VALUE-CANARY";
    private static readonly TargetControlCredentials Hosted = new(TargetAuthentication.HostedOauth, Bearer);
    private static readonly TargetConnectionsDiscovery Capability = new()
    {
        Kind = "zeroshot.connections/v1", BaseUrl = "https://target.example/api/",
        RouteTemplates = new() { List = "/connections/list", Set = "/connections/set", Delete = "/connections/delete", Resolve = "/connections/resolve" },
        DynamicKinds = ["github-app-installation", new string('é', 64)]
    };
    private static TargetDiscoveryDocument Discovery(TargetConnectionsDiscovery? connections = null,
        TargetAuthentication authentication = TargetAuthentication.HostedOauth) => new()
    {
        Kind = "zeroshot.native-v2-target/v2", Audience = "controller", Authentication = authentication,
        RunPath = "/native-v2/run", SessionPath = "/native-v2/oecp-session", OecpPath = "/native-v2/oecp",
        Extensions = new() { Connections = connections }
    };
    private static ConnectionSetRequest SetRequest(ImmutableDictionary<string, string>? values = null) => new()
    {
        Key = new("github"), Scope = ConnectionScope.User,
        Values = values ?? ImmutableDictionary<string, string>.Empty.Add("GH_TOKEN", Secret)
    };
    private static readonly ConnectionDeleteRequest DeleteRequest = new() { Key = new("github"), Scope = ConnectionScope.Org };
    private static readonly ConnectionListRequest ListRequest = new() { Scope = ConnectionScope.User };
    private static NativeClient Client(Handler handler) => NativeClient.ForHttp(
        new NativeClientOptions { Origin = new Uri("https://target.example/") }, new HttpClient(handler), ownsHttpClient: true);
    private static HttpResponseMessage Reply(HttpRequestMessage request, string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { RequestMessage = request, Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static string Summary(string scope = "user", string kind = "static") =>
        $$"""{"key":"github","scope":"{{scope}}","kind":"{{kind}}","fields":["GH_TOKEN"]}""";
    private static void Check(bool value, string message = "Connection assertion failed.")
    { if (!value) throw new InvalidOperationException(message); }

    [Test]
    public async Task EachOperationPostsExactWireToItsAdvertisedRouteWithOnlyTheHostedBearer()
    {
        foreach (var (scope, wire) in new[] { (ConnectionScope.User, "user"), (ConnectionScope.Org, "org") })
        {
            var seen = new List<(Uri Uri, string Body)>();
            using var handler = new Handler(async (request, token) =>
            {
                var body = await request.Content!.ReadAsStringAsync(token);
                seen.Add((request.RequestUri!, body));
                Check(request.Method == HttpMethod.Post && request.Headers.Authorization!.ToString() == "Bearer " + Bearer);
                Check(request.Headers.CacheControl!.NoStore && request.Content.Headers.ContentType!.MediaType == "application/json");
                return request.RequestUri!.AbsolutePath switch
                {
                    // Host-owned responses: any 2xx carrying a valid body, with open kind strings.
                    "/api/connections/list" => Reply(request, $$"""{"connections":[{{Summary(wire, "github-app-installation")}},{{Summary(wire, "future-kind")}}]}"""),
                    "/api/connections/set" => Reply(request, $$"""{"connection":{{Summary(wire)}}}""", HttpStatusCode.Created),
                    _ => Reply(request, """{"deleted":false}""", HttpStatusCode.Accepted)
                };
            });
            using var client = Client(handler);
            var list = await client.Connections.ListAsync(Discovery(Capability), new() { Scope = scope }, Hosted);
            var set = await client.Connections.SetAsync(Discovery(Capability), SetRequest() with { Scope = scope }, Hosted);
            var delete = await client.Connections.DeleteAsync(Discovery(Capability), DeleteRequest with { Scope = scope }, Hosted);

            Check(list.Connections.Select(c => c.Kind).SequenceEqual(new[] { "github-app-installation", "future-kind" }));
            Check(list.Connections.All(c => c.Scope == scope && c.Fields.Single().Value == "GH_TOKEN"));
            Check(set.Outcome == NativeAttemptOutcome.Acknowledged && set.Operation == "connections.set" && set.Failure is null);
            Check(set.Response!.Connection.Kind == ConnectionKinds.Static && set.Origin == client.Origin && set.CorrelationId != Guid.Empty);
            Check(delete.Outcome == NativeAttemptOutcome.Acknowledged && delete.Operation == "connections.delete" && delete.Response!.Deleted == false);
            Check(seen.Select(s => s.Uri.AbsoluteUri).SequenceEqual(new[]
            {
                "https://target.example/api/connections/list", "https://target.example/api/connections/set", "https://target.example/api/connections/delete"
            }));
            Check(seen.Select(s => s.Body).SequenceEqual(new[]
            {
                $$"""{"scope":"{{wire}}"}""",
                $$$"""{"key":"github","scope":"{{{wire}}}","values":{"GH_TOKEN":"{{{Secret}}}"}}""",
                $$"""{"key":"github","scope":"{{wire}}"}"""
            }));
        }
    }

    [Test]
    public async Task WrongTargetAbsentCapabilityAndInvalidDescriptorsFailBeforeDispatch()
    {
        var routes = Capability.RouteTemplates;
        var cases = new (TargetDiscoveryDocument Discovery, TargetControlCredentials Credentials)[]
        {
            (Discovery(Capability, TargetAuthentication.None), Hosted),
            (Discovery(Capability, TargetAuthentication.PrivateCapability), new(TargetAuthentication.PrivateCapability, Bearer)),
            (Discovery(Capability), new(TargetAuthentication.PrivateCapability, Bearer)),
            (Discovery(Capability) with { Audience = "operator" }, Hosted),
            (Discovery(), Hosted),
            (Discovery(Capability with { Kind = "zeroshot.connections/v2" }), Hosted),
            (Discovery(Capability with { DynamicKinds = ["static", "static"] }), Hosted),
            (Discovery(Capability with { DynamicKinds = [""] }), Hosted),
            (Discovery(Capability with { DynamicKinds = [new string('é', 64) + "x"] }), Hosted),
            (Discovery(Capability with { DynamicKinds = ["line\u0085break"] }), Hosted),
            (Discovery(Capability with { BaseUrl = "https://attacker.example/api/" }), Hosted),
            (Discovery(Capability with { BaseUrl = "https://target.example/api?x=1" }), Hosted),
            (Discovery(Capability with { RouteTemplates = routes with { Resolve = "/connections/{run_id}" } }), Hosted),
            (Discovery(Capability with { RouteTemplates = routes with { Resolve = "/connections/resolve?x" } }), Hosted),
            (Discovery(Capability with { RouteTemplates = routes with { List = "//attacker.example/list" } }), Hosted),
            (Discovery(Capability with { RouteTemplates = routes with { Set = "/connections/../set" } }), Hosted),
            (Discovery(Capability with { RouteTemplates = routes with { Delete = "" } }), Hosted),
        };
        using var handler = new Handler((request, _) => Task.FromResult(Reply(request, "{}")));
        using var client = Client(handler);
        foreach (var (discovery, credentials) in cases)
        {
            await Invalid(() => client.Connections.ListAsync(discovery, ListRequest, credentials));
            await Invalid(() => client.Connections.SetAsync(discovery, SetRequest(), credentials));
            await Invalid(() => client.Connections.DeleteAsync(discovery, DeleteRequest, credentials));
        }
        Check(handler.Calls == 0);
    }

    [Test]
    public async Task StaticValueBoundsFailLocallyAndMalformedResultsAreProtocolFailures()
    {
        static ImmutableDictionary<string, string> Values(int count, Func<int, string> value) =>
            Enumerable.Range(0, count).ToImmutableDictionary(i => "FIELD_" + i, value);
        var invalidValues = new[]
        {
            ImmutableDictionary<string, string>.Empty, Values(65, _ => "v"), Values(1, _ => ""), Values(1, _ => "a\0b"),
            Values(1, _ => new string('x', 64 * 1024 + 1)), Values(5, _ => new string('x', 60 * 1024)),
            ImmutableDictionary<string, string>.Empty.Add("1_INVALID", "v")
        };
        using var handler = new Handler((request, _) => Task.FromResult(Reply(request, "{}")));
        using var client = Client(handler);
        foreach (var values in invalidValues)
            await Invalid(() => client.Connections.SetAsync(Discovery(Capability), SetRequest(values), Hosted));
        await Invalid(() => client.Connections.ListAsync(Discovery(Capability), new() { Scope = (ConnectionScope)7 }, Hosted));
        Check(handler.Calls == 0);
        var boundary = await client.Connections.SetAsync(Discovery(Capability), SetRequest(Values(64, _ => "v")), Hosted);
        Check(boundary.Outcome == NativeAttemptOutcome.Unknown && handler.Calls == 1, "Valid bounds must dispatch.");

        foreach (var summary in new[]
        {
            """{"key":"github","scope":"team","kind":"static","fields":[]}""",
            """{"key":"github","scope":"user","kind":null,"fields":[]}""",
            """{"key":"github","scope":"user","fields":[]}""",
            """{"key":"github","scope":"user","kind":"static","fields":["1_INVALID"]}""",
            """{"key":"","scope":"user","kind":"static","fields":[]}""",
            """{"key":"github","scope":"user","kind":"static","fields":[],"values":{}}"""
        })
        {
            using var malformed = new Handler((request, _) => Task.FromResult(Reply(request,
                request.RequestUri!.AbsolutePath.EndsWith("list") ? $$"""{"connections":[{{summary}}]}""" : $$"""{"connection":{{summary}}}""")));
            using var peer = Client(malformed);
            await Failure(peer.Connections.ListAsync(Discovery(Capability), ListRequest, Hosted), NativeHttpFailureKind.Protocol);
            var set = await peer.Connections.SetAsync(Discovery(Capability), SetRequest(), Hosted);
            Check(set.Outcome == NativeAttemptOutcome.Unknown && set.Failure is NativeHttpException { Kind: NativeHttpFailureKind.Protocol });
        }
        foreach (var body in new[] { """{"deleted":"true"}""", """{"deleted":true,"key":"github"}""", "{}" })
        {
            using var malformed = new Handler((request, _) => Task.FromResult(Reply(request, body)));
            using var peer = Client(malformed);
            var delete = await peer.Connections.DeleteAsync(Discovery(Capability), DeleteRequest, Hosted);
            Check(delete.Outcome == NativeAttemptOutcome.Unknown && delete.Failure is NativeHttpException { Kind: NativeHttpFailureKind.Protocol });
        }
    }

    [Test]
    public async Task OnlyNativePreEffectProblemPairsRejectMutations()
    {
        foreach (var (status, code, rejected) in new[]
        {
            (400, "invalid_request", true), (401, "unauthorized", true), (403, "forbidden", true), (404, "not_found", true),
            (409, "request_conflict", false), (429, "rate_limited", false), (503, "target.unavailable", false),
            (500, "target.http_error", false), (400, "request.invalid", false), (404, "invalid_request", false)
        })
        {
            using var handler = new Handler((request, _) => Task.FromResult(Reply(request,
                JsonSerializer.Serialize(new { code, message = "refused" }), (HttpStatusCode)status)));
            using var client = Client(handler);
            var set = await client.Connections.SetAsync(Discovery(Capability), SetRequest(), Hosted);
            var delete = await client.Connections.DeleteAsync(Discovery(Capability), DeleteRequest, Hosted);
            foreach (var (outcome, failure) in new[] { (set.Outcome, set.Failure), (delete.Outcome, delete.Failure) })
            {
                Check(outcome == (rejected ? NativeAttemptOutcome.Rejected : NativeAttemptOutcome.Unknown), $"{status} {code}");
                Check(failure is NativeHttpException { Problem: { } problem } http && http.StatusCode == (HttpStatusCode)status && problem.Code == code);
            }
            var list = await Failure(client.Connections.ListAsync(Discovery(Capability), ListRequest, Hosted), NativeHttpFailureKind.HttpStatus);
            Check(list.Problem!.Code == code && handler.Calls == 3);
        }
        using var invalidProblem = new Handler((request, _) => Task.FromResult(Reply(request, "not json", HttpStatusCode.BadRequest)));
        using var peer = Client(invalidProblem);
        var unproven = await peer.Connections.DeleteAsync(Discovery(Capability), DeleteRequest, Hosted);
        Check(unproven.Outcome == NativeAttemptOutcome.Unknown && unproven.Failure is NativeHttpException { Problem: null, StatusCode: HttpStatusCode.BadRequest });
    }

    [Test]
    public async Task DefaultFormattingOmitsSecretsBearerAndRemoteTextWhileExplicitDataRemains()
    {
        const string Remote = "REMOTE-MESSAGE-CANARY";
        using var handler = new Handler((request, _) => Task.FromResult(Reply(request,
            JsonSerializer.Serialize(new { code = "invalid_request", message = Remote, details = new { echoed = Secret } }), HttpStatusCode.BadRequest)));
        using var client = Client(handler);
        var request = SetRequest();
        var attempt = await client.Connections.SetAsync(Discovery(Capability), request, Hosted);
        var list = await Failure(client.Connections.ListAsync(Discovery(Capability), ListRequest, Hosted), NativeHttpFailureKind.HttpStatus);
        Exception? refused = null;
        try { await client.Connections.SetAsync(Discovery(Capability, TargetAuthentication.None), request, Hosted); }
        catch (ArgumentException error) { refused = error; }
        foreach (var text in new[] { request.ToString(), attempt.ToString(), attempt.Failure!.ToString(), list.ToString(), refused!.ToString(), Hosted.ToString() })
            Check(!text.Contains(Secret) && !text.Contains(Bearer) && !text.Contains(Remote), text);
        Check(request.Values["GH_TOKEN"] == Secret && ((NativeHttpException)attempt.Failure).Problem!.Message == Remote);
        Check(Encoding.UTF8.GetString(NativeJson.SerializeUtf8(request)).Contains(Secret));
    }

    private static async Task Invalid(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) when (error is ArgumentException or JsonException)
        { Check(!error.ToString().Contains(Secret) && !error.ToString().Contains(Bearer)); return; }
        throw new InvalidOperationException("Expected invalid caller input.");
    }

    private static async Task<NativeHttpException> Failure(Task task, NativeHttpFailureKind kind)
    {
        try { await task; }
        catch (NativeHttpException error) { Check(error.Kind == kind, error.ToString()); return error; }
        throw new InvalidOperationException("Expected native failure.");
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return send(request, cancellationToken); }
    }
}
