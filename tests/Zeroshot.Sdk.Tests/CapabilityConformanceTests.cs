using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TUnit.Core;
using Zeroshot.Native;
using Zeroshot.Native.Contracts;

namespace Zeroshot.Client.Tests;

// The rules every HTTP capability operation keeps, checked from one table: each capability contributes its discovery,
// credentials, gate cases and refusal pairs; each operation its calls with their exact wire and the secrets they carry.
// Capability files keep what is theirs alone: decoding, identity, page contracts, streams and size limits.
public sealed class CapabilityConformanceTests
{
    public enum Kind { Read, Mutation }
    /// <summary>Where a refusal's problem lands: <see cref="NativeHttpException.Problem"/> or <see cref="NativeHttpException.UiProblem"/>.</summary>
    public enum Dialect { Target, UiRouter }

    /// <summary>A request as sent: method, URL, every header as "Name: value" in ordinal order, and the body text.</summary>
    public sealed record Request(string Method, string Url, string Headers, string? Body)
    {
        public override string ToString() => $"{Method} {Url} [{Headers}] {Body ?? "(no body)"}";
    }

    /// <summary>What a call produced: an attempt's outcome (null for a read), its failure, and its default formatting.</summary>
    public sealed record Result(NativeAttemptOutcome? Outcome, Exception? Failure, bool Responded, string Text);
    public delegate Task<Result> Invoke(NativeClient client, TargetDiscoveryDocument discovery, TargetControlCredentials credentials);

    /// <summary>One way to call an operation. <paramref name="Secrets"/> are carried by the request: on the wire, never in default formatting.</summary>
    public sealed record Call(string Variant, Invoke Invoke, Request? Wire, object? Input = null, ImmutableArray<string> Secrets = default)
    {
        public ImmutableArray<string> Carried => Secrets.IsDefault ? [] : Secrets;
    }

    public sealed record Gate(string Case, TargetDiscoveryDocument Discovery, TargetControlCredentials Credentials);

    /// <summary>A refusal body under a status; <paramref name="Code"/> is the problem the caller keeps (null when the body is no problem).</summary>
    public sealed record Refusal(int Status, string Body, string? Code, bool Rejected)
    {
        public override string ToString() => $"{Status} {Code ?? Body}";
    }

    public sealed record Capability(string Name, TargetDiscoveryDocument Discovery, TargetAuthentication Authentication, string Token,
        Dialect Dialect, ImmutableArray<Gate> Gates, ImmutableArray<Refusal> Refusals)
    {
        // A capability without control credentials (the browser UI) passes none.
        public TargetControlCredentials Credentials => Token.Length == 0 ? null! : new(Authentication, Token);
    }

    /// <summary>
    /// One operation. Its first call is the one gate, refusal and formatting checks use. <paramref name="Refuses"/> is false for
    /// an operation whose refusals speak another dialect, which its own file pins.
    /// </summary>
    public sealed record Op(Capability Capability, string Name, Kind Kind, ImmutableArray<Call> Calls, bool Refuses = true)
    {
        public override string ToString() => $"{Capability.Name}/{Name}";
    }

    private const string Bearer = "ACCESS-BEARER-CANARY";
    private const string Secret = "SECRET-VALUE-CANARY";
    private const string GithubToken = "GITHUB-TOKEN-CANARY";
    private const string Remote = "REMOTE-MESSAGE-CANARY";
    private const string Json = "Content-Type: application/json";
    private const string NoStore = "Cache-Control: no-store";
    private const string AcceptJson = "Accept: application/json";
    private static readonly string Auth = "Authorization: Bearer " + Bearer;

    private static string Headers(params string[] headers) => string.Join(" | ", headers.Order(StringComparer.Ordinal));
    private static Request Post(string url, string body, params string[] headers) => new("POST", url, Headers(headers), body);
    private static Request Get(string url, params string[] headers) => new("GET", url, Headers(headers), null);
    private static Refusal Problem(int status, string code, bool rejected) =>
        new(status, JsonSerializer.Serialize(new { code, message = "refused" }), code, rejected);
    private static Op Define(Capability capability, string name, Kind kind, params Call[] calls) => new(capability, name, kind, [.. calls]);
    private static Call Via(string variant, Invoke invoke, Request? wire, object? input = null, params string[] secrets) =>
        new(variant, invoke, wire, input, [.. secrets]);

    private static async Task<Result> Read<T>(Func<Task<T>> read)
    {
        try
        {
            var value = await read();
            if (value is IAsyncDisposable stream) await stream.DisposeAsync();
            return new(null, null, true, value?.ToString() ?? "");
        }
        catch (NativeHttpException error) { return new(null, error, false, error.ToString()); }
    }

    private static async Task<Result> Attempt<T>(Task<NativeAttempt<T>> attempt) where T : class
    {
        var value = await attempt;
        return new(value.Outcome, value.Failure, value.Response is not null, value.ToString());
    }

    // Hosted capabilities share native's status-derived pre-effect codes; every other pair leaves the effect unknown.
    private static readonly ImmutableArray<Refusal> HostedRefusals =
    [
        Problem(400, "invalid_request", true), Problem(401, "unauthorized", true), Problem(403, "forbidden", true), Problem(404, "not_found", true),
        Problem(409, "request_conflict", false), Problem(429, "rate_limited", false), Problem(503, "target.unavailable", false),
        Problem(500, "target.http_error", false), Problem(400, "request.invalid", false), Problem(404, "invalid_request", false),
        Problem(409, "IDEMPOTENCY_REUSE", false), Problem(500, "INTERNAL_ERROR", false), Problem(409, "request.conflict", false)
    ];
    // Every dialect: a body that is no problem proves nothing.
    private static readonly ImmutableArray<Refusal> Unproven =
    [
        new(400, "not json", null, false), new(400, """{"error":"invalid_grant"}""", null, false), new(404, "not json", null, false)
    ];

    private static TargetDiscoveryDocument Controller(TargetAuthentication authentication = TargetAuthentication.HostedOauth)
        => TestDiscovery.Controller(authentication);
    private static readonly TargetControlCredentials Hosted = new(TargetAuthentication.HostedOauth, Bearer);
    private static readonly TargetControlCredentials PrivateBearer = new(TargetAuthentication.PrivateCapability, Bearer);

    // Wrong authority and credentials, which every hosted capability refuses first.
    private static IEnumerable<Gate> HostedAuthority(Func<TargetAuthentication, TargetDiscoveryDocument> discovery) =>
    [
        new("direct target", discovery(TargetAuthentication.None), Hosted),
        new("private target", discovery(TargetAuthentication.PrivateCapability), PrivateBearer),
        new("private credentials", discovery(TargetAuthentication.HostedOauth), PrivateBearer),
    ];

    // ---- connections ----
    private static readonly TargetConnectionsDiscovery ConnectionsWire = new()
    {
        Kind = "zeroshot.connections/v1", BaseUrl = "https://target.example/api/",
        RouteTemplates = new() { List = "/connections/list", Set = "/connections/set", Delete = "/connections/delete", Resolve = "/connections/resolve" },
        DynamicKinds = ["github-app-installation", new string('é', 64)]
    };
    private static TargetDiscoveryDocument Connections(TargetConnectionsDiscovery? wire, TargetAuthentication authentication = TargetAuthentication.HostedOauth)
        => Controller(authentication) with { Extensions = new() { Connections = wire } };

    private static IEnumerable<Op> ConnectionOps()
    {
        var routes = ConnectionsWire.RouteTemplates;
        var capability = new Capability("connections", Connections(ConnectionsWire), TargetAuthentication.HostedOauth, Bearer, Dialect.Target,
        [
            .. HostedAuthority(a => Connections(ConnectionsWire, a)),
            new("operator audience", Connections(ConnectionsWire) with { Audience = "operator" }, Hosted),
            new("absent capability", Connections(null), Hosted),
            new("kind v2", Connections(ConnectionsWire with { Kind = "zeroshot.connections/v2" }), Hosted),
            new("duplicate dynamic kinds", Connections(ConnectionsWire with { DynamicKinds = ["static", "static"] }), Hosted),
            new("empty dynamic kind", Connections(ConnectionsWire with { DynamicKinds = [""] }), Hosted),
            new("dynamic kind over 128 bytes", Connections(ConnectionsWire with { DynamicKinds = [new string('é', 64) + "x"] }), Hosted),
            new("control character in dynamic kind", Connections(ConnectionsWire with { DynamicKinds = ["line\u0085break"] }), Hosted),
            new("cross-origin base", Connections(ConnectionsWire with { BaseUrl = "https://attacker.example/api/" }), Hosted),
            new("base with query", Connections(ConnectionsWire with { BaseUrl = "https://target.example/api?x=1" }), Hosted),
            new("resolve declares run", Connections(ConnectionsWire with { RouteTemplates = routes with { Resolve = "/connections/{run_id}" } }), Hosted),
            new("resolve with query", Connections(ConnectionsWire with { RouteTemplates = routes with { Resolve = "/connections/resolve?x" } }), Hosted),
            new("network-path list", Connections(ConnectionsWire with { RouteTemplates = routes with { List = "//attacker.example/list" } }), Hosted),
            new("dot-segment set", Connections(ConnectionsWire with { RouteTemplates = routes with { Set = "/connections/../set" } }), Hosted),
            new("empty delete", Connections(ConnectionsWire with { RouteTemplates = routes with { Delete = "" } }), Hosted),
        ], HostedRefusals);
        const string Base = "https://target.example/api/connections/";
        ConnectionSetRequest Set(ConnectionScope scope) => new()
        { Key = new("github"), Scope = scope, Values = ImmutableDictionary<string, string>.Empty.Add("GH_TOKEN", Secret) };
        var scopes = new[] { (ConnectionScope.User, "user"), (ConnectionScope.Org, "org") };
        yield return Define(capability, "connections.list", Kind.Read, [.. scopes.Select(s => Via(s.Item2,
            (c, d, k) => Read(() => c.Connections.ListAsync(d, new() { Scope = s.Item1 }, k)),
            Post(Base + "list", $$"""{"scope":"{{s.Item2}}"}""", Auth, AcceptJson, NoStore, Json)))]);
        yield return Define(capability, "connections.set", Kind.Mutation, [.. scopes.Select(s => Via(s.Item2,
            (c, d, k) => Attempt(c.Connections.SetAsync(d, Set(s.Item1), k)),
            Post(Base + "set", $$$"""{"key":"github","scope":"{{{s.Item2}}}","values":{"GH_TOKEN":"{{{Secret}}}"}}""", Auth, AcceptJson, NoStore, Json),
            Set(s.Item1), Secret))]);
        yield return Define(capability, "connections.delete", Kind.Mutation, [.. scopes.Select(s => Via(s.Item2,
            (c, d, k) => Attempt(c.Connections.DeleteAsync(d, new() { Key = new("github"), Scope = s.Item1 }, k)),
            Post(Base + "delete", $$"""{"key":"github","scope":"{{s.Item2}}"}""", Auth, AcceptJson, NoStore, Json)))]);
    }

    // ---- run profiles ----
    private static readonly TargetRunProfilesDiscovery ProfilesWire = new()
    {
        Kind = "zeroshot.run-profiles/v1", BaseUrl = "https://target.example/api/",
        RouteTemplates = new()
        {
            List = "/profiles/list", Show = "/profiles/show", Set = "/profiles/set",
            Delete = "/profiles/delete", Default = "/profiles/default", Run = "/profiles/run"
        }
    };
    private static TargetDiscoveryDocument Profiles(TargetRunProfilesDiscovery? wire, TargetAuthentication authentication = TargetAuthentication.HostedOauth)
        => Controller(authentication) with { Extensions = new() { RunProfiles = wire } };
    private static T Fixture<T>(string name) => NativeJson.DeserializeUtf8<T>(Encoding.UTF8.GetBytes(
        JsonNode.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)))!.AsArray()[0]!.ToJsonString()));
    private static string Canonical<T>(T value) where T : notnull => Encoding.UTF8.GetString(NativeJson.SerializeUtf8(value));

    private static IEnumerable<Op> ProfileOps()
    {
        var graph = Fixture<GraphSpec>("graphs.json");
        var runtime = Fixture<RuntimePlan>("runtimes.json");
        var capability = new Capability("run-profiles", Profiles(ProfilesWire), TargetAuthentication.HostedOauth, Bearer, Dialect.Target,
        [
            .. HostedAuthority(a => Profiles(ProfilesWire, a)),
            new("operator audience", Profiles(ProfilesWire) with { Audience = "operator" }, Hosted),
            new("absent capability", Profiles(null), Hosted),
            new("kind v2", Profiles(ProfilesWire with { Kind = "zeroshot.run-profiles/v2" }), Hosted),
            new("cross-origin base", Profiles(ProfilesWire with { BaseUrl = "https://attacker.example/api/" }), Hosted),
            // Every operation refuses, because native compiles all six routes before any of them.
            new("run declares run", Profiles(ProfilesWire with { RouteTemplates = ProfilesWire.RouteTemplates with { Run = "/profiles/{run_id}" } }), Hosted),
        ], HostedRefusals);
        const string Base = "https://target.example/api/profiles/";
        var scopes = new[] { (Scope: RunProfileScope.User, Wire: "user"), (Scope: RunProfileScope.Org, Wire: "org") };
        RunProfileSelector Selector(RunProfileScope scope) => new() { Scope = scope, Name = new("review") };
        Call Scoped(string route, (RunProfileScope Scope, string Wire) s, Invoke invoke, bool named = true) => Via(s.Wire, invoke,
            Post(Base + route, named ? $$"""{"scope":"{{s.Wire}}","name":"review"}""" : $$"""{"scope":"{{s.Wire}}"}""", Auth, AcceptJson, NoStore, Json));
        yield return Define(capability, "run_profiles.list", Kind.Read, [.. scopes.Select(s => Scoped("list", s,
            (c, d, k) => Read(() => c.Profiles.ListAsync(d, new() { Scope = s.Scope }, k)), named: false))]);
        yield return Define(capability, "run_profiles.show", Kind.Read, [.. scopes.Select(s => Scoped("show", s,
            (c, d, k) => Read(() => c.Profiles.ShowAsync(d, Selector(s.Scope), k))))]);
        yield return Define(capability, "run_profiles.set", Kind.Mutation, [.. scopes.Select(s => Via(s.Item2,
            (c, d, k) => Attempt(c.Profiles.SetAsync(d, new() { Name = new("review"), Scope = s.Item1, Graph = graph, Runtime = runtime, SetDefault = true }, k)),
            Post(Base + "set", $$"""{"name":"review","scope":"{{s.Item2}}","graph":{{Canonical(graph)}},"runtime":{{Canonical(runtime)}},"setDefault":true}""",
                Auth, AcceptJson, NoStore, Json)))]);
        yield return Define(capability, "run_profiles.delete", Kind.Mutation, [.. scopes.Select(s => Scoped("delete", s,
            (c, d, k) => Attempt(c.Profiles.DeleteAsync(d, Selector(s.Scope), k))))]);
        yield return Define(capability, "run_profiles.default", Kind.Mutation, [.. scopes.Select(s => Scoped("default", s,
            (c, d, k) => Attempt(c.Profiles.DefaultAsync(d, new() { Scope = s.Scope, Name = new("review") }, k))))]);
        var run = new RunProfileRunRequest
        {
            RunId = new("018f5e78-7f95-7c22-8d98-3f15af20c991"), Profile = Selector(RunProfileScope.Org), Title = new("Profile run"),
            InitialInput = JsonDocument.Parse("""{"items":[null,1]}""").RootElement.Clone(),
            Source = new() { Repository = new("acme/project"), Branch = new("main"), Revision = new(new string('a', 40)) },
            SubmissionKey = new("profile-key"),
            Connections = ImmutableDictionary<string, ImmutableDictionary<string, string>>.Empty
                .Add("github", ImmutableDictionary<string, string>.Empty.Add("GH_TOKEN", Secret)),
            GithubToken = GithubToken
        };
        yield return Define(capability, "run_profiles.run", Kind.Mutation, Via("org", (c, d, k) => Attempt(c.Profiles.RunAsync(d, run, k)),
            Post(Base + "run", $$$"""{"runId":"018f5e78-7f95-7c22-8d98-3f15af20c991","profile":{"scope":"org","name":"review"},"title":"Profile run","initialInput":{"items":[null,1]},"source":{"branch":"main","repository":"acme/project","revision":"{{{new string('a', 40)}}}"},"submissionKey":"profile-key","connections":{"github":{"GH_TOKEN":"{{{Secret}}}"}},"githubToken":"{{{GithubToken}}}"}""",
                Auth, AcceptJson, NoStore, Json), run, Secret, GithubToken));
    }

    // ---- hosted runs ----
    private static readonly TargetHostedRunRoutes RunRoutes = new()
    {
        List = "/native-v2/runs", Status = "/native-v2/runs/{run_id}", Watch = "/native-v2/runs/{run_id}/watch{?from_cursor}",
        Logs = "/native-v2/runs/{run_id}/logs{?from_cursor,execution}", Force = "/native-v2/runs/{run_id}/force"
    };
    private static readonly TargetHostedRunsDiscovery RunsWire = new() { Kind = "zeroshot.hosted-runs/v1", BaseUrl = "https://target.example/api/", RouteTemplates = RunRoutes };
    private static TargetDiscoveryDocument HostedRuns(TargetHostedRunsDiscovery? wire, TargetAuthentication authentication = TargetAuthentication.HostedOauth)
        => Controller(authentication) with { Extensions = new() { HostedRuns = wire } };

    private static IEnumerable<Op> HostedRunOps()
    {
        static TargetDiscoveryDocument With(TargetHostedRunRoutes routes) => HostedRuns(RunsWire with { RouteTemplates = routes });
        var capability = new Capability("hosted-runs", HostedRuns(RunsWire), TargetAuthentication.HostedOauth, Bearer, Dialect.Target,
        [
            .. HostedAuthority(a => HostedRuns(RunsWire, a)),
            new("absent capability", HostedRuns(null), Hosted),
            new("kind v2", HostedRuns(RunsWire with { Kind = "zeroshot.hosted-runs/v2" }), Hosted),
            new("cross-origin base", HostedRuns(RunsWire with { BaseUrl = "https://attacker.example/api" }), Hosted),
            new("status without run", With(RunRoutes with { Status = "/native-v2/runs" }), Hosted),
            new("list declares run", With(RunRoutes with { List = "/native-v2/runs/{run_id}" }), Hosted),
            new("watch without cursor", With(RunRoutes with { Watch = "/native-v2/runs/{run_id}/watch" }), Hosted),
            new("logs without cursor", With(RunRoutes with { Logs = "/native-v2/runs/{run_id}/logs{?execution}" }), Hosted),
            new("dot-segment force", With(RunRoutes with { Force = "/native-v2/../runs/{run_id}/force" }), Hosted),
            new("force repeats run", With(RunRoutes with { Force = "/runs/{run_id}/{run_id}" }), Hosted),
        ], HostedRefusals);
        // Native pushes the run ID as one percent-encoded path segment.
        var run = new RunId("run/1 ?%é");
        const string Base = "https://target.example/api/native-v2/runs";
        const string Encoded = Base + "/run%2F1%20%3F%25%C3%A9";
        const string Ndjson = "Accept: application/x-ndjson";
        yield return Define(capability, "hosted_runs.list", Kind.Read, Via("", (c, d, k) => Read(() => c.HostedRuns.ListAsync(d, k)),
            Get(Base, Auth, AcceptJson, NoStore)));
        yield return Define(capability, "hosted_runs.status", Kind.Read, Via("", (c, d, k) => Read(() => c.HostedRuns.StatusAsync(d, run, k)),
            Get(Encoded, Auth, AcceptJson, NoStore)));
        yield return Define(capability, "hosted_runs.force", Kind.Mutation, Via("", (c, d, k) => Attempt(c.HostedRuns.ForceAsync(d, run, k)),
            Post(Encoded + "/force", "{}", Auth, AcceptJson, Json)));
        yield return Define(capability, "hosted_runs.watch", Kind.Read,
            Via("", (c, d, k) => Read(() => c.HostedRuns.WatchAsync(d, new() { RunId = run }, k)), Get(Encoded + "/watch", Auth, Ndjson, NoStore)),
            Via("from cursor", (c, d, k) => Read(() => c.HostedRuns.WatchAsync(d, new() { RunId = run, FromCursor = new("cloud:7 &") }, k)),
                Get(Encoded + "/watch?from_cursor=cloud%3A7+%26", Auth, Ndjson, NoStore)));
        yield return Define(capability, "hosted_runs.logs", Kind.Read,
            Via("", (c, d, k) => Read(() => c.HostedRuns.LogsAsync(d, new() { RunId = run }, k)), Get(Encoded + "/logs", Auth, Ndjson, NoStore)),
            Via("from cursor of execution", (c, d, k) => Read(() => c.HostedRuns.LogsAsync(d, new() { RunId = run, FromCursor = new("cloud:8"), Execution = new("worker/1") }, k)),
                Get(Encoded + "/logs?from_cursor=cloud%3A8&execution=worker%2F1", Auth, Ndjson, NoStore)));
    }

    // ---- hosted workspace recovery ----
    private static readonly TargetHostedRunsDiscovery RecoveryRuns = new()
    {
        Kind = "zeroshot.hosted-runs/v1", BaseUrl = "https://target.example/api/", RouteTemplates = new()
        {
            List = "/runs", Status = "/runs/{run_id}", Watch = "/runs/{run_id}/watch{?from_cursor}",
            Logs = "/runs/{run_id}/logs{?from_cursor,execution}", Force = "/runs/{run_id}/force"
        }
    };
    private static readonly TargetHostedWorkspaceRecoveryDiscovery RecoveryWire = new()
    {
        Kind = "openengine.hosted-workspace-recovery/v1", RouteTemplates = new()
        {
            Resume = "/runs/{run_id}/resume", Checkpoints = "/runs/{run_id}/checkpoints", DiscardWorkspace = "/runs/{run_id}/discard-workspace"
        }
    };
    private static TargetDiscoveryDocument Recovery(TargetHostedWorkspaceRecoveryDiscovery? wire,
        TargetAuthentication authentication = TargetAuthentication.HostedOauth, TargetHostedRunsDiscovery? runs = null)
        => Controller(authentication) with { Extensions = new() { HostedRuns = runs ?? RecoveryRuns, HostedWorkspaceRecovery = wire } };

    private static IEnumerable<Op> RecoveryOps()
    {
        var routes = RecoveryWire.RouteTemplates;
        var capability = new Capability("hosted-recovery", Recovery(RecoveryWire), TargetAuthentication.HostedOauth, Bearer, Dialect.Target,
        [
            .. HostedAuthority(a => Recovery(RecoveryWire, a)),
            // Direct recovery is OECP, even when the direct target advertises its workspace capabilities.
            new("direct target advertising recovery", Recovery(RecoveryWire, TargetAuthentication.None) with { Extensions = new()
            {
                WorkspaceRecovery = new() { Kind = "openengine.workspace-recovery/v1" },
                WorkspaceCheckpoints = new() { Kind = "openengine.workspace-checkpoints/v1" },
                HostedRuns = RecoveryRuns, HostedWorkspaceRecovery = RecoveryWire
            } }, Hosted),
            new("absent capability", Recovery(null), Hosted),
            new("kind v2", Recovery(RecoveryWire with { Kind = "openengine.hosted-workspace-recovery/v2" }), Hosted),
            new("cross-origin hosted-runs base", Recovery(RecoveryWire, runs: RecoveryRuns with { BaseUrl = "https://attacker.example/api/" }), Hosted),
            new("resume without run", Recovery(RecoveryWire with { RouteTemplates = routes with { Resume = "/runs/resume" } }), Hosted),
            new("checkpoints with other variable", Recovery(RecoveryWire with { RouteTemplates = routes with { Checkpoints = "/runs/{run_id}/{checkpoint_id}" } }), Hosted),
            new("checkpoints with query", Recovery(RecoveryWire with { RouteTemplates = routes with { Checkpoints = "/runs/{run_id}/checkpoints?limit=1" } }), Hosted),
            new("dot-segment discard", Recovery(RecoveryWire with { RouteTemplates = routes with { DiscardWorkspace = "/runs/{run_id}/../discard" } }), Hosted),
            new("absolute discard", Recovery(RecoveryWire with { RouteTemplates = routes with { DiscardWorkspace = "https://attacker.example/{run_id}" } }), Hosted),
        ], HostedRefusals);
        const string Base = "https://target.example/api/runs/run%2F1/";
        var run = new RunId("run/1");
        var credentials = new TargetRunCredentials
        {
            Connections = ImmutableDictionary<string, ImmutableDictionary<string, string>>.Empty
                .Add("gateway", ImmutableDictionary<string, string>.Empty.Add("API_KEY", Secret)),
            GithubToken = GithubToken
        };
        yield return Define(capability, "hosted_workspace_recovery.checkpoints", Kind.Read, Via("after",
            (c, d, k) => Read(() => c.HostedRecovery.CheckpointsAsync(d, new() { RunId = run, After = new("opaque/0"), Limit = 1 }, k)),
            Post(Base + "checkpoints", """{"runId":"run/1","after":"opaque/0","limit":1}""", Auth, AcceptJson, NoStore, Json)));
        yield return Define(capability, "hosted_workspace_recovery.resume", Kind.Mutation,
            Via("from checkpoint with run credentials", (c, d, k) => Attempt(c.HostedRecovery.ResumeAsync(d, run, new("run-2"), k,
                new CheckpointResumeFrom { CheckpointId = new("opaque/a") }, credentials)),
                Post(Base + "resume", $$$"""{"runId":"run/1","successorRunId":"run-2","from":{"kind":"checkpoint","checkpointId":"opaque/a"},"connections":{"gateway":{"API_KEY":"{{{Secret}}}"}},"githubToken":"{{{GithubToken}}}"}""",
                    Auth, AcceptJson, NoStore, Json), credentials, Secret, GithubToken),
            Via("restart", (c, d, k) => Attempt(c.HostedRecovery.ResumeAsync(d, run, new("run-2"), k)),
                Post(Base + "resume", """{"runId":"run/1","successorRunId":"run-2"}""", Auth, AcceptJson, NoStore, Json)));
        yield return Define(capability, "hosted_workspace_recovery.discard_workspace", Kind.Mutation, Via("",
            (c, d, k) => Attempt(c.HostedRecovery.DiscardWorkspaceAsync(d, run, k)),
            Post(Base + "discard-workspace", """{"runId":"run/1"}""", Auth, AcceptJson, NoStore, Json)));
    }

    // ---- merge plans ----
    private static readonly TargetMergePlansDiscovery PlansWire = new()
    {
        Kind = "zeroshot.merge-plans/v1", BaseUrl = "https://target.example/api/",
        RouteTemplates = new() { Create = "/plans", Status = "/plans/{plan_id}", Force = "/plans/{plan_id}/force" }
    };
    private static TargetDiscoveryDocument Plans(TargetMergePlansDiscovery? wire, TargetAuthentication authentication = TargetAuthentication.HostedOauth)
        => Controller(authentication) with { Extensions = new() { MergePlans = wire } };

    private static IEnumerable<Op> MergePlanOps()
    {
        var routes = PlansWire.RouteTemplates;
        var capability = new Capability("merge-plans", Plans(PlansWire), TargetAuthentication.HostedOauth, Bearer, Dialect.Target,
        [
            .. HostedAuthority(a => Plans(PlansWire, a)),
            new("absent capability", Plans(null), Hosted),
            new("kind v2", Plans(PlansWire with { Kind = "zeroshot.merge-plans/v2" }), Hosted),
            new("cross-origin base", Plans(PlansWire with { BaseUrl = "https://attacker.example/api/" }), Hosted),
            new("create declares plan", Plans(PlansWire with { RouteTemplates = routes with { Create = "/plans/{plan_id}" } }), Hosted),
            new("status without plan", Plans(PlansWire with { RouteTemplates = routes with { Status = "/plans/status" } }), Hosted),
            new("force repeats plan", Plans(PlansWire with { RouteTemplates = routes with { Force = "/{plan_id}/{plan_id}" } }), Hosted),
            new("status with query", Plans(PlansWire with { RouteTemplates = routes with { Status = "/plans/{plan_id}?view=full" } }), Hosted),
            new("force with run variable", Plans(PlansWire with { RouteTemplates = routes with { Force = "/plans/{run_id}/force" } }), Hosted),
        ], HostedRefusals);
        static MergePlanRunRequest Run(string name, params string[] needs) => new()
        {
            Name = new(name), Needs = needs.Length == 0 ? null : [.. needs.Select(need => new RunProfileName(need))],
            InitialInput = JsonDocument.Parse("""{"items":[null,1]}""").RootElement.Clone()
        };
        var submit = new MergePlanSubmitRequest
        {
            SubmissionKey = new("plan-key"), Title = new("Release"), ExpiresAt = "2026-09-28T00:00:00Z",
            Source = new() { Repository = new("acme/project"), Branch = new("main") },
            Profile = new() { Scope = RunProfileScope.Org, Name = new("software-change") },
            Runs = [Run("build"), Run("integrate", "build")]
        };
        var secret = submit with
        {
            Connections = ImmutableDictionary<string, ImmutableDictionary<string, string>>.Empty
                .Add("github", ImmutableDictionary<string, string>.Empty.Add("GH_TOKEN", Secret)), GithubToken = GithubToken
        };
        // {} selects the base environment; verbatim needs keep their order and an explicit empty list.
        var environment = submit with { Environment = new RuntimeEnvironment(), Runs = [Run("b"), Run("a", "c", "b"), Run("c") with { Needs = [] }] };
        const string Common = """{"submissionKey":"plan-key","title":"Release","expiresAt":"2026-09-28T00:00:00Z","source":{"repository":"acme/project","branch":"main"},"profile":{"scope":"org","name":"software-change"},"runs":""";
        const string Base = "https://target.example/api/plans";
        // An opaque host-assigned ID is one percent-encoded path segment, as native's url crate pushes it.
        var plan = new RunId("p/α b%?:@");
        const string Encoded = Base + "/p%2F%CE%B1%20b%25%3F:@";
        yield return Define(capability, "merge_plans.create", Kind.Mutation,
            Via("with run credentials", (c, d, k) => Attempt(c.MergePlans.CreateAsync(d, secret, k)),
                Post(Base, Common + $$$"""[{"name":"build","initialInput":{"items":[null,1]}},{"name":"integrate","needs":["build"],"initialInput":{"items":[null,1]}}],"connections":{"github":{"GH_TOKEN":"{{{Secret}}}"}},"githubToken":"{{{GithubToken}}}"}""",
                    Auth, AcceptJson, NoStore, Json), secret, Secret, GithubToken),
            Via("base environment", (c, d, k) => Attempt(c.MergePlans.CreateAsync(d, environment, k)),
                Post(Base, Common + """[{"name":"b","initialInput":{"items":[null,1]}},{"name":"a","needs":["c","b"],"initialInput":{"items":[null,1]}},{"name":"c","needs":[],"initialInput":{"items":[null,1]}}],"environment":{}}""",
                    Auth, AcceptJson, NoStore, Json)));
        yield return Define(capability, "merge_plans.status", Kind.Read, Via("opaque ID",
            (c, d, k) => Read(() => c.MergePlans.StatusAsync(d, plan, k)), Get(Encoded, Auth, AcceptJson, NoStore)));
        yield return Define(capability, "merge_plans.force", Kind.Mutation, Via("opaque ID",
            (c, d, k) => Attempt(c.MergePlans.ForceAsync(d, plan, k)), Post(Encoded + "/force", "{}", Auth, AcceptJson, NoStore, Json)));
    }

    // ---- OAuth ----
    private const string Access = "ACCESS-TOKEN-CANARY";
    private const string RefreshToken = "REFRESH-TOKEN-CANARY";
    private const string DeviceCode = "DEVICE-CODE-CANARY";
    private static readonly TargetOAuthDiscovery OAuthWire = new()
    {
        MetadataUrl = "https://target.example/.well-known/oauth-authorization-server",
        DeviceAuthorizationEndpoint = "https://target.example/oauth/device",
        TokenEndpoint = "https://target.example/oauth/token",
        RevocationEndpoint = "https://target.example/oauth/revoke",
        ClientId = "zeroshot cli~1", DeviceGrantType = "urn:ietf:params:oauth:grant-type:device_code",
        DeviceExchangeFields = ["device_label", "device_token"]
    };
    private static readonly TargetLoginSessionDiscovery LoginWire = new() { RouteTemplate = "/login/session", Method = "GET", CachePolicy = "no-store" };
    private static TargetDiscoveryDocument OAuth(TargetOAuthDiscovery? oauth = null, TargetLoginSessionDiscovery? login = null)
        => Controller() with { Oauth = oauth ?? OAuthWire, LoginSession = login ?? LoginWire };

    private static IEnumerable<Op> OAuthOps()
    {
        var access = new TargetControlCredentials(TargetAuthentication.HostedOauth, Access);
        Gate Refused(string name, TargetDiscoveryDocument discovery) => new(name, discovery, access);
        var capability = new Capability("oauth", OAuth(), TargetAuthentication.HostedOauth, Access, Dialect.Target,
        [
            Refused("direct target", OAuth() with { Authentication = TargetAuthentication.None }),
            Refused("private target", OAuth() with { Authentication = TargetAuthentication.PrivateCapability }),
            Refused("operator audience", OAuth() with { Audience = "operator" }),
            Refused("absent oauth", OAuth() with { Oauth = null }),
            Refused("absent login session", OAuth() with { LoginSession = null }),
            Refused("short device grant", OAuth(OAuthWire with { DeviceGrantType = "device_code" })),
            Refused("missing device label field", OAuth(OAuthWire with { DeviceExchangeFields = ["device_token"] })),
            Refused("duplicate exchange field", OAuth(OAuthWire with { DeviceExchangeFields = ["device_token", "device_token"] })),
            Refused("extra exchange field", OAuth(OAuthWire with { DeviceExchangeFields = ["device_token", "device_label", "device_name"] })),
            Refused("empty client", OAuth(OAuthWire with { ClientId = "" })),
            Refused("client over 256 bytes", OAuth(OAuthWire with { ClientId = new string('é', 128) + "x" })),
            Refused("control character in client", OAuth(OAuthWire with { ClientId = "client\u007f" })),
            Refused("cross-origin metadata", OAuth(OAuthWire with { MetadataUrl = "https://attacker.example/metadata" })),
            Refused("cross-origin device endpoint", OAuth(OAuthWire with { DeviceAuthorizationEndpoint = "https://attacker.example/oauth/device" })),
            Refused("cross-origin token endpoint", OAuth(OAuthWire with { TokenEndpoint = "https://attacker.example/oauth/token" })),
            Refused("cross-origin revocation endpoint", OAuth(OAuthWire with { RevocationEndpoint = "https://attacker.example/oauth/revoke" })),
            Refused("login session by POST", OAuth(login: LoginWire with { Method = "POST" })),
            Refused("login session cacheable", OAuth(login: LoginWire with { CachePolicy = "no-cache" })),
            Refused("network-path login session", OAuth(login: LoginWire with { RouteTemplate = "//attacker.example/session" })),
            Refused("login session with query", OAuth(login: LoginWire with { RouteTemplate = "/login/session?device_label=x" })),
        ], HostedRefusals);
        const string Form = "Content-Type: application/x-www-form-urlencoded";
        const string Any = "Accept: */*";
        var deviceToken = Guid.Parse("0f3c2a9e-58b1-4d7e-9a61-2b4c8d0e6f17");
        var authorization = new DeviceAuthorization
        {
            DeviceCode = DeviceCode, UserCode = "ABCD-EFGH", VerificationUri = "https://login.example/device",
            VerificationUriComplete = "https://login.example/device?user_code=ABCD-EFGH", ExpiresIn = 600, Interval = 5
        };
        yield return Define(capability, "oauth.metadata", Kind.Read, Via("", (c, d, _) => Read(() => c.OAuth.MetadataAsync(d)),
            Get(OAuthWire.MetadataUrl, AcceptJson)));
        yield return Define(capability, "oauth.beginDeviceAuthorization", Kind.Read, Via("", (c, d, _) => Read(() => c.OAuth.BeginDeviceAuthorizationAsync(d)),
            Post(OAuthWire.DeviceAuthorizationEndpoint, "client_id=zeroshot+cli%7E1", Any, Form)));
        // A recognized OAuth error is this exchange's only refusal; HttpOAuthTests pins that dialect.
        yield return Define(capability, "oauth.exchangeDeviceToken", Kind.Mutation, Via("", (c, d, _) => Attempt(c.OAuth.ExchangeDeviceTokenAsync(d, authorization, deviceToken)),
            Post(OAuthWire.TokenEndpoint, "grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Adevice_code&device_code=DEVICE-CODE-CANARY&client_id=zeroshot+cli%7E1" +
                "&device_token=0f3c2a9e-58b1-4d7e-9a61-2b4c8d0e6f17&device_label=zeroshot-cli&audience=controller", Any, Form),
            authorization, DeviceCode, deviceToken.ToString())) with { Refuses = false };
        yield return Define(capability, "oauth.refresh", Kind.Mutation,
            Via("", (c, d, _) => Attempt(c.OAuth.RefreshAsync(d, RefreshToken)),
                Post(OAuthWire.TokenEndpoint, "grant_type=refresh_token&client_id=zeroshot+cli%7E1&refresh_token=REFRESH-TOKEN-CANARY&audience=controller", Any, Form),
                null, RefreshToken),
            Via("encoded token", (c, d, _) => Attempt(c.OAuth.RefreshAsync(d, "rotating refresh/1")),
                Post(OAuthWire.TokenEndpoint, "grant_type=refresh_token&client_id=zeroshot+cli%7E1&refresh_token=rotating+refresh%2F1&audience=controller", Any, Form)));
        yield return Define(capability, "oauth.verifySession", Kind.Read, Via("", (c, d, k) => Read(() => c.OAuth.VerifySessionAsync(d, k)),
            Get("https://target.example/login/session", "Authorization: Bearer " + Access, AcceptJson, NoStore)));
    }

    // ---- private targets ----
    private const string PrivateCapability = "PRIVATE-CAPABILITY-CANARY";

    private static IEnumerable<Op> PrivateOps()
    {
        static TargetDiscoveryDocument Bootstrap(TargetAuthentication authentication = TargetAuthentication.PrivateCapability,
            string? path = "/native-v2/private-bootstrap") => Controller(authentication) with { PrivateBootstrapPath = path };
        // The route is unauthenticated: the credential column is unused and never sent.
        var bootstrap = new Capability("private-bootstrap", Bootstrap(), TargetAuthentication.PrivateCapability, PrivateCapability, Dialect.Target,
        [
            new("direct target", Bootstrap(TargetAuthentication.None, null), Hosted),
            new("hosted target", Bootstrap(TargetAuthentication.HostedOauth, null), Hosted),
            new("no bootstrap path", Bootstrap(path: null), Hosted),
            new("cross-origin bootstrap path", Bootstrap(path: "https://other.example/native-v2/private-bootstrap"), Hosted),
        ],
        [
            Problem(400, "request.invalid", true), Problem(404, "request.not_found", true), Problem(408, "request.timeout", true),
            Problem(401, "request.unauthorized", false), Problem(503, "target.unavailable", false), Problem(400, "run.rejected", false),
            Problem(404, "request.invalid", false), Problem(500, "target.http_error", false)
        ]);
        var nonce = new string('a', 24);
        var ciphertext = string.Concat(Enumerable.Repeat("0123456789abcdef", 10));
        yield return Define(bootstrap, "private.bootstrap", Kind.Mutation, Via("",
            (c, d, _) => Attempt(c.Private.BootstrapAsync(d, new() { Nonce = nonce, Ciphertext = ciphertext })),
            Post("https://target.example/native-v2/private-bootstrap", $$"""{"nonce":"{{nonce}}","ciphertext":"{{ciphertext}}"}""", AcceptJson, Json)));

        // The exports take no discovery: the private capability is their only authority.
        var exports = new Capability("private-exports", Controller(TargetAuthentication.PrivateCapability), TargetAuthentication.PrivateCapability,
            PrivateCapability, Dialect.Target, [],
            [Problem(401, "request.unauthorized", false), Problem(404, "request.not_found", false), Problem(404, "run_not_found", false), Problem(400, "invalid_cursor", false)]);
        const string Run = "018f5e78-7f95-7c22-8d98-3f15af20c991";
        var run = new RunId(Run);
        var auth = "Authorization: Bearer " + PrivateCapability;
        yield return Define(exports, "private.operatorDiagnostics", Kind.Read, Via("", (c, _, k) => Read(() => c.Private.GetOperatorDiagnosticsAsync(run, k)),
            Get("https://target.example/native-v2/operator-diagnostics/" + Run, auth)));
        yield return Define(exports, "private.history.definition", Kind.Read, Via("", (c, _, k) => Read(() => c.Private.GetHistoryDefinitionAsync(run, k)),
            Post("https://target.example/native-v2/history/definition", $$"""{"runId":"{{Run}}"}""", auth, AcceptJson, Json)));
        yield return Define(exports, "private.history.page", Kind.Read,
            Via("", (c, _, k) => Read(() => c.Private.GetHistoryPageAsync(run, k)),
                Post("https://target.example/native-v2/history/page", $$"""{"runId":"{{Run}}"}""", auth, AcceptJson, Json)),
            Via("after", (c, _, k) => Read(() => c.Private.GetHistoryPageAsync(run, k, new Cursor("v2:4"))),
                Post("https://target.example/native-v2/history/page", $$"""{"runId":"{{Run}}","after":"v2:4"}""", auth, AcceptJson, Json)));
    }

    // ---- dashboard profile save ----
    private static IEnumerable<Op> DashboardOps()
    {
        // The UI router answers with its own {code,message}; its exact browser wire is pinned per route in DashboardProfileTests.
        var dashboard = new Capability("dashboard-profiles", Controller(TargetAuthentication.None), TargetAuthentication.None, "", Dialect.UiRouter, [],
        [
            // workspace_changed is also native's answer to a matching header after the store was replaced.
            Problem(409, "workspace_changed", true), Problem(409, "profile_conflict", true), Problem(422, "invalid_profile", true),
            Problem(403, "origin_rejected", true), Problem(415, "json_required", true), Problem(503, "server_stopping", true),
            // A store error can follow the write; a code under another status or a non-UI refusal proves nothing.
            Problem(500, "profile_store_error", false), Problem(409, "invalid_profile", false), Problem(404, "request.not_found", false)
        ]);
        var save = new DashboardProfileSaveRequest
        {
            Name = new("review"), Graph = Fixture<GraphSpec>("graphs.json"), Runtime = Fixture<RuntimePlan>("runtimes.json"), ExpectedRevision = "rev-1"
        };
        yield return Define(dashboard, "dashboard.saveProfile", Kind.Mutation, Via("",
            (c, _, _) => Attempt(c.Dashboard.SaveProfileAsync(save, "0199aa00-0000-7000-8000-000000000001")), null, save));
    }

    public static IEnumerable<Op> Operations() =>
        [.. ConnectionOps(), .. ProfileOps(), .. HostedRunOps(), .. RecoveryOps(), .. MergePlanOps(), .. OAuthOps(), .. PrivateOps(), .. DashboardOps()];
    public static IEnumerable<Func<Op>> WireOperations() => Operations().Where(op => op.Calls.Any(c => c.Wire is not null)).Select(op => (Func<Op>)(() => op));
    public static IEnumerable<Func<Op>> GatedOperations() => Operations().Where(op => !op.Capability.Gates.IsEmpty).Select(op => (Func<Op>)(() => op));
    public static IEnumerable<Func<Op>> RefusingOperations() => Operations().Where(op => op.Refuses).Select(op => (Func<Op>)(() => op));
    public static IEnumerable<Func<Op>> AllOperations() => Operations().Select(op => (Func<Op>)(() => op));

    private static async Task<Request> Capture(HttpRequestMessage request, CancellationToken token)
    {
        var headers = request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
            .Where(h => h.Key != "Content-Length")
            .Select(h => $"{h.Key}: {string.Join(", ", h.Value)}").ToArray();
        return new(request.Method.Method, request.RequestUri!.OriginalString, Headers(headers),
            request.Content is null ? null : await request.Content.ReadAsStringAsync(token));
    }

    private static string? Code(NativeHttpException error, Dialect dialect) => dialect == Dialect.Target ? error.Problem?.Code : error.UiProblem?.Code;
    private static string? Message(NativeHttpException error, Dialect dialect) => dialect == Dialect.Target ? error.Problem?.Message : error.UiProblem?.Message;
    private static object? Foreign(NativeHttpException error, Dialect dialect) => dialect == Dialect.Target ? error.UiProblem : error.Problem;

    [Test]
    [MethodDataSource(nameof(WireOperations))]
    public async Task EachCallSendsExactlyItsDeclaredRequestOnce(Op op)
    {
        foreach (var call in op.Calls.Where(c => c.Wire is not null))
        {
            var seen = new List<Request>();
            using var handler = new Handler(async (request, token) =>
            {
                seen.Add(await Capture(request, token));
                return Reply(request, """{"code":"invalid_request","message":"refused"}""", HttpStatusCode.BadRequest);
            });
            using var client = ClientFor(handler);
            await call.Invoke(client, op.Capability.Discovery, op.Capability.Credentials);
            Check(seen.Count == 1 && seen[0] == call.Wire,
                $"{op} [{call.Variant}] sent\n  {string.Join("\n  ", seen)}\nexpected\n  {call.Wire}");
        }
    }

    [Test]
    [MethodDataSource(nameof(GatedOperations))]
    public async Task WrongTargetsAbsentCapabilitiesAndInvalidDescriptorsFailBeforeDispatch(Op op)
    {
        var call = op.Calls[0];
        var secrets = Forbidden(op, call);
        foreach (var gate in op.Capability.Gates)
        {
            using var handler = new Handler((_, _) => throw new InvalidOperationException("Dispatched."));
            using var client = ClientFor(handler);
            Exception? error = null;
            try { await call.Invoke(client, gate.Discovery, gate.Credentials); }
            catch (Exception thrown) { error = thrown; }
            Check(error?.GetType() == typeof(ArgumentException) && handler.Calls == 0,
                $"{op} admitted '{gate.Case}': {error?.GetType().Name ?? "no exception"}, {handler.Calls} requests");
            Check(!secrets.Any(error!.ToString().Contains), $"{op} refusal of '{gate.Case}' echoed a secret: {error}");
        }
    }

    [Test]
    [MethodDataSource(nameof(RefusingOperations))]
    public async Task OnlyNativePreEffectPairsRejectAndEveryOtherRefusalLeavesTheEffectUnknown(Op op)
    {
        var (capability, call) = (op.Capability, op.Calls[0]);
        foreach (var row in capability.Refusals.Concat(Unproven))
        {
            using var handler = new Handler((request, _) => Task.FromResult(Reply(request, row.Body, (HttpStatusCode)row.Status)));
            using var client = ClientFor(handler);
            var result = await call.Invoke(client, capability.Discovery, capability.Credentials);
            // Reads throw the refusal; mutations return it as evidence.
            NativeAttemptOutcome? expected = op.Kind == Kind.Read ? null : row.Rejected ? NativeAttemptOutcome.Rejected : NativeAttemptOutcome.Unknown;
            Check(result.Outcome == expected, $"{op} {row}: {result.Outcome?.ToString() ?? "read"} instead of {expected?.ToString() ?? "read"}");
            Check(result is { Responded: false, Failure: NativeHttpException { Kind: NativeHttpFailureKind.HttpStatus, DeviceTokenError: null } http } &&
                http.StatusCode == (HttpStatusCode)row.Status && Code(http, capability.Dialect) == row.Code && Foreign(http, capability.Dialect) is null,
                $"{op} {row} did not keep its status and problem: {result.Failure}");
            Check(handler.Calls == 1, $"{op} {row} sent {handler.Calls} requests");
        }
        using var lost = new Handler((_, _) => throw new HttpRequestException("connection reset"));
        using var peer = ClientFor(lost);
        var reset = await call.Invoke(peer, capability.Discovery, capability.Credentials);
        Check(reset.Outcome == (op.Kind == Kind.Read ? null : NativeAttemptOutcome.Unknown) &&
            reset.Failure is NativeHttpException { Kind: NativeHttpFailureKind.Transport } && lost.Calls == 1,
            $"{op}: a lost exchange gave {reset.Outcome} {reset.Failure?.GetType().Name} after {lost.Calls} requests");
    }

    // The bearer, request-carried secrets and the remote problem text may reach the wire and explicit accessors only.
    private static string[] Forbidden(Op op, Call call) =>
        [.. new[] { Secret, Remote, op.Capability.Token }.Where(s => s.Length > 0), .. call.Carried];

    [Test]
    [MethodDataSource(nameof(AllOperations))]
    public async Task DefaultFormattingOmitsCredentialsSecretsAndRemoteText(Op op)
    {
        var (capability, call) = (op.Capability, op.Calls[0]);
        var problem = capability.Dialect == Dialect.Target
            ? JsonSerializer.Serialize(new { code = "invalid_request", message = Remote, details = new { echoed = Secret } })
            : JsonSerializer.Serialize(new { code = "invalid_profile", message = Remote + " " + Secret });
        string? sent = null;
        using var handler = new Handler(async (request, token) =>
        {
            sent = request.Content is null ? null : await request.Content.ReadAsStringAsync(token);
            return Reply(request, problem, (HttpStatusCode)422);
        });
        using var client = ClientFor(handler);
        var result = await call.Invoke(client, capability.Discovery, capability.Credentials);
        var texts = new List<string> { result.Text, result.Failure!.ToString(), capability.Credentials?.ToString() ?? "" };
        if (call.Input is not null) texts.Add(call.Input.ToString()!);
        if (!capability.Gates.IsEmpty)
        {
            try { await call.Invoke(client, capability.Gates[0].Discovery, capability.Gates[0].Credentials); }
            catch (ArgumentException error) { texts.Add(error.ToString()); }
        }
        var forbidden = Forbidden(op, call);
        foreach (var text in texts)
            Check(!forbidden.Any(text.Contains), $"{op} default formatting leaked: {text}");
        // Explicit data remains: the remote problem through its accessor, every carried secret on the wire.
        Check(!op.Refuses || result.Failure is NativeHttpException http && Message(http, capability.Dialect)!.Contains(Remote), $"{op} lost the remote problem text");
        Check(call.Carried.All(secret => sent!.Contains(secret)), $"{op} did not send its carried secrets");
    }
}
