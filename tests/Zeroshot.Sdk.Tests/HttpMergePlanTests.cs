using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TUnit.Core;
using Zeroshot.Native;
using Zeroshot.Native.Contracts;

namespace Zeroshot.Client.Tests;

public sealed class HttpMergePlanTests
{
    private const string Bearer = "ACCESS-BEARER-CANARY";
    private const string Secret = "SECRET-VALUE-CANARY";
    private const string PlanId = "plan-1";
    private static readonly TargetControlCredentials Hosted = new(TargetAuthentication.HostedOauth, Bearer);
    private static readonly TargetMergePlansDiscovery Capability = new()
    {
        Kind = "zeroshot.merge-plans/v1", BaseUrl = "https://target.example/api/",
        RouteTemplates = new() { Create = "/plans", Status = "/plans/{plan_id}", Force = "/plans/{plan_id}/force" }
    };
    private static TargetDiscoveryDocument Discovery(TargetMergePlansDiscovery? plans = null,
        TargetAuthentication authentication = TargetAuthentication.HostedOauth)
        => TestDiscovery.Controller(authentication) with { Extensions = new() { MergePlans = plans } };
    private static MergePlanRunRequest Run(string name, params string[] needs) => new()
    {
        Name = new(name), Needs = needs.Length == 0 ? null : [.. needs.Select(need => new RunProfileName(need))],
        InitialInput = JsonDocument.Parse("""{"items":[null,1]}""").RootElement.Clone()
    };
    private static MergePlanSubmitRequest Submit() => new()
    {
        SubmissionKey = new("plan-key"), Title = new("Release"), ExpiresAt = "2026-09-28T00:00:00Z",
        Source = new() { Repository = new("acme/project"), Branch = new("main") },
        Profile = new() { Scope = RunProfileScope.Org, Name = new("software-change") },
        Runs = [Run("build"), Run("integrate", "build")]
    };
    private const string RunStatus = """{"name":"build","runId":"r-1","state":"blocked","needs":[],"sourceRevision":null,"readyAt":null,"queueExpiresAt":null,"terminalAt":null,"waitingReason":null,"errorCode":null}""";
    private static string Plan(string run = RunStatus, string state = "queued", string id = PlanId) =>
        $$"""{"planId":"{{id}}","title":"Release","state":"{{state}}","repository":"acme/project","branch":"main","submittedAt":"2026-09-27T00:00:00Z","expiresAt":"2026-09-28T00:00:00Z","runs":[{{run}}]}""";
    private static NativeClient Client(Handler handler) => NativeClient.ForHttp(
        new NativeClientOptions { Origin = new Uri("https://target.example/") }, new HttpClient(handler), ownsHttpClient: true);
    private static HttpResponseMessage Reply(HttpRequestMessage request, string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { RequestMessage = request, Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static void Check(bool value, string message = "Merge-plan assertion failed.")
    { if (!value) throw new InvalidOperationException(message); }

    [Test]
    public async Task EachOperationSendsExactWireToItsAdvertisedRouteWithOnlyTheHostedBearer()
    {
        // An opaque host-assigned ID is one percent-encoded path segment, as native's url crate pushes it.
        const string OpaqueId = "p/α b%?:@";
        const string Encoded = "p%2F%CE%B1%20b%25%3F:@";
        var seen = new List<(HttpMethod Method, string Url, string? Body)>();
        using var handler = new Handler(async (request, token) =>
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(token);
            seen.Add((request.Method, request.RequestUri!.OriginalString, body));
            Check(request.Headers.Authorization!.ToString() == "Bearer " + Bearer && request.Headers.CacheControl!.NoStore);
            Check(request.Headers.Accept.Single().MediaType == "application/json");
            // Host-owned responses: any 2xx carrying a valid plan.
            return request.Method == HttpMethod.Get ? Reply(request, Plan(id: OpaqueId))
                : request.RequestUri.OriginalString.EndsWith("/force") ? Reply(request, Plan(state: "running", id: OpaqueId), HttpStatusCode.Accepted)
                : Reply(request, Plan(), HttpStatusCode.Created);
        });
        using var client = Client(handler);
        var requests = new[]
        {
            Submit() with { Connections = ImmutableDictionary<string, ImmutableDictionary<string, string>>.Empty
                .Add("github", ImmutableDictionary<string, string>.Empty.Add("GH_TOKEN", Secret)), GithubToken = "GITHUB-TOKEN" },
            // {} selects the base environment; verbatim needs keep their order and an explicit empty list.
            Submit() with { Environment = new RuntimeEnvironment(), Runs = [Run("b"), Run("a", "c", "b"), Run("c") with { Needs = [] }] }
        };
        foreach (var request in requests)
        {
            var created = await client.MergePlans.CreateAsync(Discovery(Capability), request, Hosted);
            Check(created.Outcome == NativeAttemptOutcome.Acknowledged && created.Operation == "merge_plans.create" && created.Response!.PlanId.Value == PlanId);
        }
        var status = await client.MergePlans.StatusAsync(Discovery(Capability), new(OpaqueId), Hosted);
        var forced = await client.MergePlans.ForceAsync(Discovery(Capability), new(OpaqueId), Hosted);
        Check(status.State == MergePlanState.Queued && status.PlanId.Value == OpaqueId);
        // An acknowledged force is not terminal.
        Check(forced.Outcome == NativeAttemptOutcome.Acknowledged && forced.Operation == "merge_plans.force" && forced.Response!.State == MergePlanState.Running);

        Check(seen.Select(s => (s.Method.Method, s.Url)).SequenceEqual(new[]
        {
            ("POST", "https://target.example/api/plans"), ("POST", "https://target.example/api/plans"),
            ("GET", $"https://target.example/api/plans/{Encoded}"), ("POST", $"https://target.example/api/plans/{Encoded}/force")
        }), string.Join(" ", seen.Select(s => s.Url)));
        const string Common = """{"submissionKey":"plan-key","title":"Release","expiresAt":"2026-09-28T00:00:00Z","source":{"repository":"acme/project","branch":"main"},"profile":{"scope":"org","name":"software-change"},"runs":""";
        var expected = new[]
        {
            Common + """[{"name":"build","initialInput":{"items":[null,1]}},{"name":"integrate","needs":["build"],"initialInput":{"items":[null,1]}}],"connections":{"github":{"GH_TOKEN":"SECRET-VALUE-CANARY"}},"githubToken":"GITHUB-TOKEN"}""",
            Common + """[{"name":"b","initialInput":{"items":[null,1]}},{"name":"a","needs":["c","b"],"initialInput":{"items":[null,1]}},{"name":"c","needs":[],"initialInput":{"items":[null,1]}}],"environment":{}}""",
            null,
            "{}"
        };
        foreach (var ((_, _, actual), wire) in seen.Zip(expected))
            Check(wire is null ? actual is null : JsonNode.DeepEquals(JsonNode.Parse(actual!), JsonNode.Parse(wire)), actual ?? "(no body)");
    }

    [Test]
    public async Task EveryStateAndRequiredNullableFieldDecodesExactlyAndMalformedPlansFail()
    {
        var reply = "";
        using var handler = new Handler((request, _) => Task.FromResult(Reply(request, reply)));
        using var client = Client(handler);
        foreach (var state in new[] { "queued", "running", "succeeded", "failed", "cancelled", "expired" })
        {
            reply = Plan(state: state, run: "");
            var plan = await client.MergePlans.StatusAsync(Discovery(Capability), new(PlanId), Hosted);
            Check(plan.State.ToString().Equals(state, StringComparison.OrdinalIgnoreCase) && plan.Runs.IsEmpty, state);
        }
        foreach (var state in new[] { "blocked", "materializing", "queued", "provisioning", "running", "cancelling", "succeeded", "failed", "cancelled", "expired" })
        {
            reply = Plan(RunStatus.Replace("\"blocked\"", $"\"{state}\""));
            var run = (await client.MergePlans.StatusAsync(Discovery(Capability), new(PlanId), Hosted)).Runs.Single();
            Check(run.State.ToString().Equals(state, StringComparison.OrdinalIgnoreCase), state);
        }
        var revision = new string('a', 40);
        reply = Plan($$"""{"name":"integrate","runId":"r-2","state":"failed","needs":["build"],"sourceRevision":"{{revision}}","readyAt":"t1","queueExpiresAt":"t2","terminalAt":"t3","waitingReason":"w","errorCode":"e"}""");
        var filled = (await client.MergePlans.StatusAsync(Discovery(Capability), new(PlanId), Hosted)).Runs.Single();
        Check(filled is { ReadyAt: "t1", QueueExpiresAt: "t2", TerminalAt: "t3", WaitingReason: "w", ErrorCode: "e" } &&
            filled.SourceRevision!.Value == revision && filled.Needs.Single().Value == "build");
        reply = Plan();
        var nulls = (await client.MergePlans.StatusAsync(Discovery(Capability), new(PlanId), Hosted)).Runs.Single();
        Check(nulls is { SourceRevision: null, ReadyAt: null, QueueExpiresAt: null, TerminalAt: null, WaitingReason: null, ErrorCode: null });

        // Missing is not null, and a plan answered for another ID is foreign.
        var malformed = new[] { "sourceRevision", "readyAt", "queueExpiresAt", "terminalAt", "waitingReason", "errorCode", "needs" }
            .Select(field => Plan(RunStatus.Replace($",\"{field}\":{(field == "needs" ? "[]" : "null")}", "")))
            .Append(Plan(RunStatus.Replace("\"blocked\"", "\"paused\""))).Append(Plan(state: "paused"))
            .Append(Plan(RunStatus.Replace("\"errorCode\":null", "\"errorCode\":null,\"extra\":1")))
            .Append(Plan(id: "plan-2"));
        foreach (var body in malformed)
        {
            reply = body;
            await Failure(client.MergePlans.StatusAsync(Discovery(Capability), new(PlanId), Hosted), NativeHttpFailureKind.Protocol);
            var forced = await client.MergePlans.ForceAsync(Discovery(Capability), new(PlanId), Hosted);
            Check(forced.Outcome == NativeAttemptOutcome.Unknown && forced.Response is null &&
                forced.Failure is NativeHttpException { Kind: NativeHttpFailureKind.Protocol }, body);
        }
    }

    [Test]
    public async Task WrongTargetDescriptorsPlanIdsAndInvalidRequestsFailBeforeDispatch()
    {
        var routes = Capability.RouteTemplates;
        var cases = new (TargetDiscoveryDocument Discovery, TargetControlCredentials Credentials)[]
        {
            (Discovery(Capability, TargetAuthentication.None), Hosted),
            (Discovery(Capability, TargetAuthentication.PrivateCapability), new(TargetAuthentication.PrivateCapability, Bearer)),
            (Discovery(Capability), new(TargetAuthentication.PrivateCapability, Bearer)),
            (Discovery(), Hosted),
            (Discovery(Capability with { Kind = "zeroshot.merge-plans/v2" }), Hosted),
            (Discovery(Capability with { BaseUrl = "https://attacker.example/api/" }), Hosted),
            (Discovery(Capability with { RouteTemplates = routes with { Create = "/plans/{plan_id}" } }), Hosted),
            (Discovery(Capability with { RouteTemplates = routes with { Status = "/plans/status" } }), Hosted),
            (Discovery(Capability with { RouteTemplates = routes with { Force = "/{plan_id}/{plan_id}" } }), Hosted),
            (Discovery(Capability with { RouteTemplates = routes with { Status = "/plans/{plan_id}?view=full" } }), Hosted),
            (Discovery(Capability with { RouteTemplates = routes with { Force = "/plans/{run_id}/force" } }), Hosted),
        };
        using var handler = new Handler((request, _) => Task.FromResult(Reply(request, Plan())));
        using var client = Client(handler);
        foreach (var (discovery, credentials) in cases)
        {
            // Every operation refuses, because native compiles all three routes before any of them.
            await Invalid(() => client.MergePlans.CreateAsync(discovery, Submit(), credentials));
            await Invalid(() => client.MergePlans.StatusAsync(discovery, new(PlanId), credentials));
            await Invalid(() => client.MergePlans.ForceAsync(discovery, new(PlanId), credentials));
        }
        // Native would drop or rewrite these segments and address a different route.
        foreach (var id in new[] { ".", "..", "plan\t1", "plan\r1", "plan\n1" })
        {
            await Invalid(() => client.MergePlans.StatusAsync(Discovery(Capability), new(id), Hosted));
            await Invalid(() => client.MergePlans.ForceAsync(Discovery(Capability), new(id), Hosted));
        }
        var runs = Enumerable.Range(0, 65).Select(i => Run($"run-{i}")).ToImmutableArray();
        foreach (var request in new[]
        {
            Submit() with { Runs = [] },
            Submit() with { Runs = runs },
            Submit() with { Connections = ImmutableDictionary<string, ImmutableDictionary<string, string>>.Empty
                .Add("github", ImmutableDictionary<string, string>.Empty.Add("GH_TOKEN", "")) }
        })
            await Invalid(() => client.MergePlans.CreateAsync(Discovery(Capability), request, Hosted));
        Check(handler.Calls == 0);
        Check((await client.MergePlans.CreateAsync(Discovery(Capability), Submit() with { Runs = runs[..64] }, Hosted)).Outcome
            == NativeAttemptOutcome.Acknowledged, "64 runs is native's maximum, not beyond it.");

        // Native sends an empty ID as an empty final segment.
        using var empty = new Handler((request, _) =>
        {
            Check(request.RequestUri!.OriginalString == "https://target.example/api/plans/", request.RequestUri.OriginalString);
            return Task.FromResult(Reply(request, Plan(id: "")));
        });
        using var emptyClient = Client(empty);
        Check((await emptyClient.MergePlans.StatusAsync(Discovery(Capability), new(""), Hosted)).PlanId.Value == "" && empty.Calls == 1);
    }

    [Test]
    public async Task OnlyNativePreEffectProblemPairsRejectAndEveryOtherFailureLeavesTheEffectUnknown()
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
            var create = await client.MergePlans.CreateAsync(Discovery(Capability), Submit(), Hosted);
            var force = await client.MergePlans.ForceAsync(Discovery(Capability), new(PlanId), Hosted);
            foreach (var attempt in new[] { create, force })
            {
                Check(attempt.Outcome == (rejected ? NativeAttemptOutcome.Rejected : NativeAttemptOutcome.Unknown), $"{status} {code}");
                Check(attempt.Failure is NativeHttpException { Problem: { } problem } http && http.StatusCode == (HttpStatusCode)status && problem.Code == code);
            }
            var read = await Failure(client.MergePlans.StatusAsync(Discovery(Capability), new(PlanId), Hosted), NativeHttpFailureKind.HttpStatus);
            // Native resends once after an auth rejection; this binding never does.
            Check(read.Problem!.Code == code && handler.Calls == 3, $"{status} sent {handler.Calls} requests");
        }

        using (var lost = new Handler((_, _) => throw new HttpRequestException("connection reset")))
        using (var peer = Client(lost))
        {
            var create = await peer.MergePlans.CreateAsync(Discovery(Capability), Submit(), Hosted);
            var force = await peer.MergePlans.ForceAsync(Discovery(Capability), new(PlanId), Hosted);
            Check(create.Outcome == NativeAttemptOutcome.Unknown && force.Outcome == NativeAttemptOutcome.Unknown && lost.Calls == 2,
                "A lost exchange leaves the plan unresolved.");
        }

        // Native reads merge-plan results under 1 MiB, larger than the 64 KiB bound of other hosted results.
        var large = Plan(string.Join(",", Enumerable.Repeat(RunStatus.Replace("\"waitingReason\":null", $"\"waitingReason\":\"{new string('w', 2000)}\""), 64)));
        var oversized = large + new string(' ', 1024 * 1024);
        foreach (var (body, fits) in new[] { (large, true), (oversized, false) })
        {
            using var handler = new Handler((request, _) => Task.FromResult(Reply(request, body)));
            using var client = Client(handler);
            var create = await client.MergePlans.CreateAsync(Discovery(Capability), Submit(), Hosted);
            Check(fits ? create.Outcome == NativeAttemptOutcome.Acknowledged
                : create.Outcome == NativeAttemptOutcome.Unknown && create.Failure is NativeHttpException { Kind: NativeHttpFailureKind.SizeLimit },
                $"{body.Length} bytes");
        }
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
