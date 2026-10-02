using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TUnit.Core;
using Zeroshot.Native;
using Zeroshot.Native.Contracts;

namespace Zeroshot.Client.Tests;

public sealed class HttpProfileTests
{
    private const string Bearer = "ACCESS-BEARER-CANARY";
    private const string Secret = "SECRET-VALUE-CANARY";
    private const string GithubToken = "GITHUB-TOKEN-CANARY";
    private const string RunId = "018f5e78-7f95-7c22-8d98-3f15af20c991";
    private static readonly TargetControlCredentials Hosted = new(TargetAuthentication.HostedOauth, Bearer);
    private static readonly TargetRunProfilesDiscovery Capability = new()
    {
        Kind = "zeroshot.run-profiles/v1", BaseUrl = "https://target.example/api/",
        RouteTemplates = new()
        {
            List = "/profiles/list", Show = "/profiles/show", Set = "/profiles/set",
            Delete = "/profiles/delete", Default = "/profiles/default", Run = "/profiles/run"
        }
    };
    private static TargetDiscoveryDocument Discovery(TargetRunProfilesDiscovery? profiles = null,
        TargetAuthentication authentication = TargetAuthentication.HostedOauth)
        => TestDiscovery.Controller(authentication) with { Extensions = new() { RunProfiles = profiles } };
    private static JsonArray Fixture(string name) =>
        JsonNode.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)))!.AsArray();
    private static T Parse<T>(JsonNode node) => NativeJson.DeserializeUtf8<T>(Encoding.UTF8.GetBytes(node.ToJsonString()));
    private static JsonNode Canonical<T>(T value) where T : notnull => JsonNode.Parse(NativeJson.SerializeUtf8(value))!;
    private static readonly GraphSpec Graph = Parse<GraphSpec>(Fixture("graphs.json")[0]!);
    private static readonly RuntimePlan Runtime = Parse<RuntimePlan>(Fixture("runtimes.json")[0]!);
    private static RunProfileSelector Selector(RunProfileScope scope = RunProfileScope.User) => new() { Scope = scope, Name = new("review") };
    private static RunProfileSetRequest SetRequest(RunProfileScope scope = RunProfileScope.User) =>
        new() { Name = new("review"), Scope = scope, Graph = Graph, Runtime = Runtime };
    private static RunProfileRunRequest RunRequest(RuntimeEnvironment? environment = null) => new()
    {
        RunId = new(RunId), Profile = Selector(RunProfileScope.Org), Title = new("Profile run"),
        InitialInput = JsonDocument.Parse("""{"items":[null,1]}""").RootElement.Clone(),
        Source = new() { Repository = new("acme/project"), Branch = new("main"), Revision = new(new string('a', 40)) },
        SubmissionKey = new("profile-key"), Environment = environment,
        Connections = ImmutableDictionary<string, ImmutableDictionary<string, string>>.Empty
            .Add("github", ImmutableDictionary<string, string>.Empty.Add("GH_TOKEN", Secret)),
        GithubToken = GithubToken
    };
    private static string Profile(string scope = "user", string graph = "", string runtime = "") =>
        $$"""{"id":"p-1","name":"review","scope":"{{scope}}","graph":{{(graph == "" ? Canonical(Graph).ToJsonString() : graph)}},"runtime":{{(runtime == "" ? Canonical(Runtime).ToJsonString() : runtime)}},"isDefault":true}""";
    private static NativeClient Client(Handler handler) => NativeClient.ForHttp(
        new NativeClientOptions { Origin = new Uri("https://target.example/") }, new HttpClient(handler), ownsHttpClient: true);
    private static HttpResponseMessage Reply(HttpRequestMessage request, string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { RequestMessage = request, Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static void Check(bool value, string message = "Profile assertion failed.")
    { if (!value) throw new InvalidOperationException(message); }

    [Test]
    public async Task EachOperationPostsExactWireToItsAdvertisedRouteWithOnlyTheHostedBearer()
    {
        foreach (var (scope, wire) in new[] { (RunProfileScope.User, "user"), (RunProfileScope.Org, "org") })
        {
            var seen = new List<(string Path, JsonNode Body)>();
            using var handler = new Handler(async (request, token) =>
            {
                var body = await request.Content!.ReadAsStringAsync(token);
                seen.Add((request.RequestUri!.AbsolutePath, JsonNode.Parse(body)!));
                Check(request.Method == HttpMethod.Post && request.Headers.Authorization!.ToString() == "Bearer " + Bearer);
                Check(request.Headers.CacheControl!.NoStore && request.Content.Headers.ContentType!.MediaType == "application/json");
                return request.RequestUri!.AbsolutePath switch
                {
                    // Host-owned responses: any 2xx carrying a valid body.
                    "/api/profiles/list" => Reply(request, $$"""{"profiles":[{"id":"p-1","name":"review","scope":"{{wire}}","isDefault":true}]}"""),
                    "/api/profiles/show" => Reply(request, Profile(wire)),
                    "/api/profiles/set" => Reply(request, $$"""{"profile":{{Profile(wire)}}}""", HttpStatusCode.Created),
                    "/api/profiles/delete" => Reply(request, """{"deleted":true}""", HttpStatusCode.Accepted),
                    "/api/profiles/default" => Reply(request, $$"""{"scope":"{{wire}}","name":"review"}"""),
                    _ => Reply(request, """{"runId":"018f5e78-7f95-7c22-8d98-3f15af20c992"}""", HttpStatusCode.Accepted)
                };
            });
            using var client = Client(handler);
            var list = await client.Profiles.ListAsync(Discovery(Capability), new() { Scope = scope }, Hosted);
            var show = await client.Profiles.ShowAsync(Discovery(Capability), Selector(scope), Hosted);
            var set = await client.Profiles.SetAsync(Discovery(Capability), SetRequest(scope) with { SetDefault = true }, Hosted);
            var delete = await client.Profiles.DeleteAsync(Discovery(Capability), Selector(scope), Hosted);
            var selectDefault = await client.Profiles.DefaultAsync(Discovery(Capability), new() { Scope = scope, Name = new("review") }, Hosted);
            var run = await client.Profiles.RunAsync(Discovery(Capability), RunRequest(), Hosted);

            Check(list.Profiles.Single() is { Id: "p-1", IsDefault: true } summary && summary.Scope == scope && summary.Name.Value == "review");
            Check(show.Scope == scope && JsonNode.DeepEquals(Canonical(show.Graph), Canonical(Graph)));
            Check(set.Outcome == NativeAttemptOutcome.Acknowledged && set.Operation == "run_profiles.set" && set.Response!.Profile.IsDefault);
            Check(delete.Outcome == NativeAttemptOutcome.Acknowledged && delete.Response!.Deleted);
            Check(selectDefault.Outcome == NativeAttemptOutcome.Acknowledged && selectDefault.Response!.Name!.Value == "review");
            // Native deduplication can acknowledge a different run; the caller compares it with its proposal.
            Check(run.Outcome == NativeAttemptOutcome.Acknowledged && run.Operation == "run_profiles.run" &&
                run.Response!.RunId.Value == "018f5e78-7f95-7c22-8d98-3f15af20c992" && run.Origin == client.Origin);
            Check(seen.Select(s => s.Path).SequenceEqual(new[]
            {
                "/api/profiles/list", "/api/profiles/show", "/api/profiles/set", "/api/profiles/delete", "/api/profiles/default", "/api/profiles/run"
            }));
            var expected = new[]
            {
                $$"""{"scope":"{{wire}}"}""",
                $$"""{"scope":"{{wire}}","name":"review"}""",
                $$"""{"name":"review","scope":"{{wire}}","graph":{{Canonical(Graph).ToJsonString()}},"runtime":{{Canonical(Runtime).ToJsonString()}},"setDefault":true}""",
                $$"""{"scope":"{{wire}}","name":"review"}""",
                $$"""{"scope":"{{wire}}","name":"review"}""",
                $$$"""{"runId":"{{{RunId}}}","profile":{"scope":"org","name":"review"},"title":"Profile run","initialInput":{"items":[null,1]},"source":{"repository":"acme/project","branch":"main","revision":"{{{new string('a', 40)}}}"},"submissionKey":"profile-key","connections":{"github":{"GH_TOKEN":"{{{Secret}}}"}},"githubToken":"{{{GithubToken}}}"}"""
            };
            foreach (var (actual, wireBody) in seen.Select(s => s.Body).Zip(expected))
                Check(JsonNode.DeepEquals(actual, JsonNode.Parse(wireBody)), actual.ToJsonString());
        }
    }

    [Test]
    public async Task CompleteGraphAndRuntimeDefinitionsRoundTripThroughSetAndShow()
    {
        var graphs = Fixture("graphs.json").Select(g => Parse<GraphSpec>(g!)).ToArray();
        var runtimes = Fixture("runtimes.json").Select(r => Parse<RuntimePlan>(r!)).ToArray();
        using var handler = new Handler(async (request, token) =>
        {
            // The peer stores what it received and returns it as the full profile.
            if (request.RequestUri!.AbsolutePath.EndsWith("/show")) return Reply(request, Profile());
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
            return Reply(request, $$"""{"profile":{{Profile("user", body["graph"]!.ToJsonString(), body["runtime"]!.ToJsonString())}}}""");
        });
        using var client = Client(handler);
        for (var i = 0; i < Math.Max(graphs.Length, runtimes.Length); i++)
        {
            var request = SetRequest() with { Graph = graphs[i % graphs.Length], Runtime = runtimes[i % runtimes.Length] };
            var set = await client.Profiles.SetAsync(Discovery(Capability), request, Hosted);
            Check(set.Outcome == NativeAttemptOutcome.Acknowledged);
            Check(JsonNode.DeepEquals(Canonical(set.Response!.Profile.Graph), Canonical(request.Graph)) &&
                JsonNode.DeepEquals(Canonical(set.Response.Profile.Runtime), Canonical(request.Runtime)), $"set {i}");
        }
        var shown = await client.Profiles.ShowAsync(Discovery(Capability), Selector(), Hosted);
        Check(JsonNode.DeepEquals(Canonical(shown.Graph), Canonical(Graph)) && JsonNode.DeepEquals(Canonical(shown.Runtime), Canonical(Runtime)));
    }

    [Test]
    public async Task OptionalFieldsPreserveOmittedEmptyAndNullDistinctions()
    {
        var bodies = new List<JsonObject>();
        var defaultReply = """{"scope":"org","name":null}""";
        using var handler = new Handler(async (request, token) =>
        {
            bodies.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!.AsObject());
            return request.RequestUri!.AbsolutePath.EndsWith("/default")
                ? Reply(request, defaultReply) : Reply(request, $$"""{"runId":"{{RunId}}"}""");
        });
        using var client = Client(handler);
        var runs = new[]
        {
            RunRequest() with { GithubToken = null, Connections = ImmutableDictionary<string, ImmutableDictionary<string, string>>.Empty },
            RunRequest(new RuntimeEnvironment()),
            RunRequest(new RuntimeEnvironment { Setup = "make setup" })
        };
        foreach (var run in runs)
            Check((await client.Profiles.RunAsync(Discovery(Capability), run, Hosted)).Outcome == NativeAttemptOutcome.Acknowledged);
        // Omission leaves the environment to the host; {} explicitly selects the base environment.
        Check(!bodies[0].ContainsKey("environment") && !bodies[0].ContainsKey("githubToken"));
        Check(bodies[0]["connections"]!.AsObject().Count == 0, "Connections are required even when empty.");
        Check(bodies[1]["environment"]!.AsObject().Count == 0 && bodies[2]["environment"]!["setup"]!.GetValue<string>() == "make setup");

        var cleared = await client.Profiles.DefaultAsync(Discovery(Capability), new() { Scope = RunProfileScope.Org }, Hosted);
        Check(JsonNode.DeepEquals(bodies[3], JsonNode.Parse("""{"scope":"org"}""")), "An omitted name clears the default.");
        Check(cleared.Outcome == NativeAttemptOutcome.Acknowledged && cleared.Response is { Scope: RunProfileScope.Org, Name: null });
        defaultReply = """{"scope":"org"}""";
        var missing = await client.Profiles.DefaultAsync(Discovery(Capability), new() { Scope = RunProfileScope.Org }, Hosted);
        Check(missing.Outcome == NativeAttemptOutcome.Acknowledged && missing.Response!.Name is null, "Native reads a missing name as None.");
    }

    [Test]
    public async Task WrongTargetAbsentCapabilityInvalidDescriptorsAndInvalidRequestsFailBeforeDispatch()
    {
        var routes = Capability.RouteTemplates;
        var cases = new (TargetDiscoveryDocument Discovery, TargetControlCredentials Credentials)[]
        {
            (Discovery(Capability, TargetAuthentication.None), Hosted),
            (Discovery(Capability, TargetAuthentication.PrivateCapability), new(TargetAuthentication.PrivateCapability, Bearer)),
            (Discovery(Capability), new(TargetAuthentication.PrivateCapability, Bearer)),
            (Discovery(Capability) with { Audience = "operator" }, Hosted),
            (Discovery(), Hosted),
            (Discovery(Capability with { Kind = "zeroshot.run-profiles/v2" }), Hosted),
            (Discovery(Capability with { BaseUrl = "https://attacker.example/api/" }), Hosted),
            (Discovery(Capability with { RouteTemplates = routes with { Run = "/profiles/{run_id}" } }), Hosted),
        };
        using var handler = new Handler((request, _) => Task.FromResult(Reply(request, "{}")));
        using var client = Client(handler);
        foreach (var (discovery, credentials) in cases)
        {
            // Every operation refuses, because native compiles all six routes before any of them.
            await Invalid(() => client.Profiles.ListAsync(discovery, new() { Scope = RunProfileScope.User }, credentials));
            await Invalid(() => client.Profiles.ShowAsync(discovery, Selector(), credentials));
            await Invalid(() => client.Profiles.SetAsync(discovery, SetRequest(), credentials));
            await Invalid(() => client.Profiles.DeleteAsync(discovery, Selector(), credentials));
            await Invalid(() => client.Profiles.DefaultAsync(discovery, new() { Scope = RunProfileScope.User }, credentials));
            await Invalid(() => client.Profiles.RunAsync(discovery, RunRequest(), credentials));
        }

        foreach (var name in new[] { "", "-lead", ".lead", "a b", "é", new string('a', 65) })
            await Invalid(() => Task.FromResult(new RunProfileName(name)));
        _ = new RunProfileName("A0" + new string('_', 31) + new string('.', 30) + "-");
        foreach (var connections in new[]
        {
            ImmutableDictionary<string, ImmutableDictionary<string, string>>.Empty.Add("github", ImmutableDictionary<string, string>.Empty),
            ImmutableDictionary<string, ImmutableDictionary<string, string>>.Empty.Add("github", ImmutableDictionary<string, string>.Empty.Add("GH_TOKEN", "")),
            ImmutableDictionary<string, ImmutableDictionary<string, string>>.Empty.Add("", ImmutableDictionary<string, string>.Empty.Add("GH_TOKEN", Secret))
        })
            await Invalid(() => client.Profiles.RunAsync(Discovery(Capability), RunRequest() with { Connections = connections }, Hosted));
        Check(handler.Calls == 0);
    }

    [Test]
    public async Task MalformedResultsFailReadsAndLeaveMutationEffectsUnknown()
    {
        var badProfiles = new[]
        {
            Profile("team"), Profile().Replace("\"review\"", "\"-review\""), Profile().Replace(",\"isDefault\":true", ""),
            Profile().Replace("\"isDefault\":true", "\"isDefault\":true,\"revision\":\"1\""),
            Profile(graph: """{"profile":"openengine.graph.full/v1"}"""), Profile(runtime: "{}")
        };
        foreach (var profile in badProfiles)
        {
            using var handler = new Handler((request, _) => Task.FromResult(Reply(request,
                request.RequestUri!.AbsolutePath.EndsWith("/show") ? profile : $$"""{"profile":{{profile}}}""")));
            using var client = Client(handler);
            await Failure(client.Profiles.ShowAsync(Discovery(Capability), Selector(), Hosted), NativeHttpFailureKind.Protocol);
            var set = await client.Profiles.SetAsync(Discovery(Capability), SetRequest(), Hosted);
            Check(set.Outcome == NativeAttemptOutcome.Unknown && set.Response is null && set.Failure is NativeHttpException { Kind: NativeHttpFailureKind.Protocol });
        }
        foreach (var body in new[] { """{"profiles":[{"id":"p","name":"review","scope":"user"}]}""", """{"profiles":null}""" })
        {
            using var handler = new Handler((request, _) => Task.FromResult(Reply(request, body)));
            using var client = Client(handler);
            await Failure(client.Profiles.ListAsync(Discovery(Capability), new() { Scope = RunProfileScope.User }, Hosted), NativeHttpFailureKind.Protocol);
        }
        foreach (var (path, body) in new[]
        {
            ("delete", """{"deleted":"true"}"""), ("delete", "{}"), ("default", """{"name":"review"}"""),
            ("default", """{"scope":"user","name":"-x"}"""), ("run", "{}"), ("run", """{"runId":null}"""), ("run", """{"runId":"r","extra":1}""")
        })
        {
            using var handler = new Handler((request, _) => Task.FromResult(Reply(request, body)));
            using var client = Client(handler);
            var attempt = path switch
            {
                "delete" => (await client.Profiles.DeleteAsync(Discovery(Capability), Selector(), Hosted)).Outcome,
                "default" => (await client.Profiles.DefaultAsync(Discovery(Capability), new() { Scope = RunProfileScope.User }, Hosted)).Outcome,
                _ => (await client.Profiles.RunAsync(Discovery(Capability), RunRequest(), Hosted)).Outcome
            };
            Check(attempt == NativeAttemptOutcome.Unknown && handler.Calls == 1, $"{path} {body}");
        }
    }

    [Test]
    public async Task ProfileResultsOverNativeHostedBoundLeaveSetUnknown()
    {
        // Otherwise valid, but over native's 64 KiB hosted response bound: native treats the exchange as failed.
        var oversized = $$"""{"profile":{{Profile().Replace("\"p-1\"", "\"" + new string('p', 64 * 1024) + "\"")}}}""";
        using var handler = new Handler((request, _) => Task.FromResult(Reply(request, oversized)));
        using var client = Client(handler);
        var set = await client.Profiles.SetAsync(Discovery(Capability), SetRequest(), Hosted);
        Check(set.Outcome == NativeAttemptOutcome.Unknown && set.Response is null &&
            set.Failure is NativeHttpException { Kind: NativeHttpFailureKind.SizeLimit });
    }

    [Test]
    public async Task OnlyNativePreEffectProblemPairsRejectMutations()
    {
        foreach (var (status, code, rejected) in new[]
        {
            (400, "invalid_request", true), (401, "unauthorized", true), (403, "forbidden", true), (404, "not_found", true),
            (409, "request_conflict", false), (429, "rate_limited", false), (503, "target.unavailable", false), (404, "invalid_request", false)
        })
        {
            using var handler = new Handler((request, _) => Task.FromResult(Reply(request,
                JsonSerializer.Serialize(new { code, message = "refused" }), (HttpStatusCode)status)));
            using var client = Client(handler);
            var attempts = new (NativeAttemptOutcome Outcome, Exception? Failure)[]
            {
                await Evidence(client.Profiles.SetAsync(Discovery(Capability), SetRequest(), Hosted)),
                await Evidence(client.Profiles.DeleteAsync(Discovery(Capability), Selector(), Hosted)),
                await Evidence(client.Profiles.DefaultAsync(Discovery(Capability), new() { Scope = RunProfileScope.User }, Hosted)),
                await Evidence(client.Profiles.RunAsync(Discovery(Capability), RunRequest(), Hosted))
            };
            foreach (var (outcome, failure) in attempts)
            {
                Check(outcome == (rejected ? NativeAttemptOutcome.Rejected : NativeAttemptOutcome.Unknown), $"{status} {code}");
                Check(failure is NativeHttpException { Problem: { } problem } http && http.StatusCode == (HttpStatusCode)status && problem.Code == code);
            }
            var show = await Failure(client.Profiles.ShowAsync(Discovery(Capability), Selector(), Hosted), NativeHttpFailureKind.HttpStatus);
            Check(show.Problem!.Code == code);
        }
        using var lost = new Handler((_, _) => throw new HttpRequestException("connection reset"));
        using var peer = Client(lost);
        var run = await peer.Profiles.RunAsync(Discovery(Capability), RunRequest(), Hosted);
        Check(run.Outcome == NativeAttemptOutcome.Unknown && run.Failure is NativeHttpException, "A lost exchange leaves the run unresolved.");
    }

    [Test]
    public async Task DefaultFormattingOmitsSecretsBearerAndRemoteTextWhileExplicitDataRemains()
    {
        const string Remote = "REMOTE-MESSAGE-CANARY";
        using var handler = new Handler((request, _) => Task.FromResult(Reply(request,
            JsonSerializer.Serialize(new { code = "invalid_request", message = Remote, details = new { echoed = Secret } }), HttpStatusCode.BadRequest)));
        using var client = Client(handler);
        var request = RunRequest();
        var attempt = await client.Profiles.RunAsync(Discovery(Capability), request, Hosted);
        Exception? refused = null;
        try { await client.Profiles.RunAsync(Discovery(Capability, TargetAuthentication.None), request, Hosted); }
        catch (ArgumentException error) { refused = error; }
        foreach (var text in new[] { request.ToString(), attempt.ToString(), attempt.Failure!.ToString(), refused!.ToString() })
            Check(!text.Contains(Secret) && !text.Contains(GithubToken) && !text.Contains(Bearer) && !text.Contains(Remote), text);
        var wire = Encoding.UTF8.GetString(NativeJson.SerializeUtf8(request));
        Check(wire.Contains(Secret) && wire.Contains(GithubToken) && ((NativeHttpException)attempt.Failure).Problem!.Message == Remote);
    }

    private static async Task<(NativeAttemptOutcome, Exception?)> Evidence<T>(Task<NativeAttempt<T>> task) where T : class
    {
        var attempt = await task;
        return (attempt.Outcome, attempt.Failure);
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
}
