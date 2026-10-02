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
    private static TargetDiscoveryDocument Discovery(TargetRunProfilesDiscovery profiles)
        => TestDiscovery.Controller(TargetAuthentication.HostedOauth) with { Extensions = new() { RunProfiles = profiles } };
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

    // Wire, gate, refusal and formatting rules: CapabilityConformanceTests.
    [Test]
    public async Task EachOperationDecodesItsHostOwnedResult()
    {
        foreach (var (scope, wire) in new[] { (RunProfileScope.User, "user"), (RunProfileScope.Org, "org") })
        {
            using var handler = new Handler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath switch
            {
                // Host-owned responses: any 2xx carrying a valid body.
                "/api/profiles/list" => Reply(request, $$"""{"profiles":[{"id":"p-1","name":"review","scope":"{{wire}}","isDefault":true}]}"""),
                "/api/profiles/show" => Reply(request, Profile(wire)),
                "/api/profiles/set" => Reply(request, $$"""{"profile":{{Profile(wire)}}}""", HttpStatusCode.Created),
                "/api/profiles/delete" => Reply(request, """{"deleted":true}""", HttpStatusCode.Accepted),
                "/api/profiles/default" => Reply(request, $$"""{"scope":"{{wire}}","name":"review"}"""),
                _ => Reply(request, """{"runId":"018f5e78-7f95-7c22-8d98-3f15af20c992"}""", HttpStatusCode.Accepted)
            }));
            using var client = ClientFor(handler);
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
        using var client = ClientFor(handler);
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
        using var client = ClientFor(handler);
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
    public async Task InvalidNamesAndRunConnectionsFailBeforeDispatch()
    {
        using var handler = new Handler((request, _) => Task.FromResult(Reply(request, "{}")));
        using var client = ClientFor(handler);
        foreach (var name in new[] { "", "-lead", ".lead", "a b", "é", new string('a', 65) })
            await Invalid(() => Task.FromResult(new RunProfileName(name)), Secret, Bearer);
        _ = new RunProfileName("A0" + new string('_', 31) + new string('.', 30) + "-");
        foreach (var connections in new[]
        {
            ImmutableDictionary<string, ImmutableDictionary<string, string>>.Empty.Add("github", ImmutableDictionary<string, string>.Empty),
            ImmutableDictionary<string, ImmutableDictionary<string, string>>.Empty.Add("github", ImmutableDictionary<string, string>.Empty.Add("GH_TOKEN", "")),
            ImmutableDictionary<string, ImmutableDictionary<string, string>>.Empty.Add("", ImmutableDictionary<string, string>.Empty.Add("GH_TOKEN", Secret))
        })
            await Invalid(() => client.Profiles.RunAsync(Discovery(Capability), RunRequest() with { Connections = connections }, Hosted), Secret, Bearer);
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
            using var client = ClientFor(handler);
            await Failure(client.Profiles.ShowAsync(Discovery(Capability), Selector(), Hosted), NativeHttpFailureKind.Protocol);
            var set = await client.Profiles.SetAsync(Discovery(Capability), SetRequest(), Hosted);
            Check(set.Outcome == NativeAttemptOutcome.Unknown && set.Response is null && set.Failure is NativeHttpException { Kind: NativeHttpFailureKind.Protocol });
        }
        foreach (var body in new[] { """{"profiles":[{"id":"p","name":"review","scope":"user"}]}""", """{"profiles":null}""" })
        {
            using var handler = new Handler((request, _) => Task.FromResult(Reply(request, body)));
            using var client = ClientFor(handler);
            await Failure(client.Profiles.ListAsync(Discovery(Capability), new() { Scope = RunProfileScope.User }, Hosted), NativeHttpFailureKind.Protocol);
        }
        foreach (var (path, body) in new[]
        {
            ("delete", """{"deleted":"true"}"""), ("delete", "{}"), ("default", """{"name":"review"}"""),
            ("default", """{"scope":"user","name":"-x"}"""), ("run", "{}"), ("run", """{"runId":null}"""), ("run", """{"runId":"r","extra":1}""")
        })
        {
            using var handler = new Handler((request, _) => Task.FromResult(Reply(request, body)));
            using var client = ClientFor(handler);
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
        using var client = ClientFor(handler);
        var set = await client.Profiles.SetAsync(Discovery(Capability), SetRequest(), Hosted);
        Check(set.Outcome == NativeAttemptOutcome.Unknown && set.Response is null &&
            set.Failure is NativeHttpException { Kind: NativeHttpFailureKind.SizeLimit });
    }
}
