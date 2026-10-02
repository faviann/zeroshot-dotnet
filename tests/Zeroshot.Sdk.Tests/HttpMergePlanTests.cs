using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
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
    private static TargetDiscoveryDocument Discovery(TargetMergePlansDiscovery plans)
        => TestDiscovery.Controller(TargetAuthentication.HostedOauth) with { Extensions = new() { MergePlans = plans } };
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

    // Wire, gate, refusal and formatting rules: CapabilityConformanceTests.
    [Test]
    public async Task EachOperationDecodesItsHostOwnedPlan()
    {
        const string OpaqueId = "p/α b%?:@";
        using var handler = new Handler((request, _) => Task.FromResult(
            // Host-owned responses: any 2xx carrying a valid plan.
            request.Method == HttpMethod.Get ? Reply(request, Plan(id: OpaqueId))
                : request.RequestUri!.OriginalString.EndsWith("/force") ? Reply(request, Plan(state: "running", id: OpaqueId), HttpStatusCode.Accepted)
                : Reply(request, Plan(), HttpStatusCode.Created)));
        using var client = ClientFor(handler);
        var created = await client.MergePlans.CreateAsync(Discovery(Capability), Submit(), Hosted);
        Check(created.Outcome == NativeAttemptOutcome.Acknowledged && created.Operation == "merge_plans.create" && created.Response!.PlanId.Value == PlanId);
        var status = await client.MergePlans.StatusAsync(Discovery(Capability), new(OpaqueId), Hosted);
        var forced = await client.MergePlans.ForceAsync(Discovery(Capability), new(OpaqueId), Hosted);
        Check(status.State == MergePlanState.Queued && status.PlanId.Value == OpaqueId);
        // An acknowledged force is not terminal.
        Check(forced.Outcome == NativeAttemptOutcome.Acknowledged && forced.Operation == "merge_plans.force" && forced.Response!.State == MergePlanState.Running);
    }

    [Test]
    public async Task EveryStateAndRequiredNullableFieldDecodesExactlyAndMalformedPlansFail()
    {
        var reply = "";
        using var handler = new Handler((request, _) => Task.FromResult(Reply(request, reply)));
        using var client = ClientFor(handler);
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
    public async Task UnaddressablePlanIdsAndInvalidRequestsFailBeforeDispatch()
    {
        using var handler = new Handler((request, _) => Task.FromResult(Reply(request, Plan())));
        using var client = ClientFor(handler);
        // Native would drop or rewrite these segments and address a different route.
        foreach (var id in new[] { ".", "..", "plan\t1", "plan\r1", "plan\n1" })
        {
            await Invalid(() => client.MergePlans.StatusAsync(Discovery(Capability), new(id), Hosted), Secret, Bearer);
            await Invalid(() => client.MergePlans.ForceAsync(Discovery(Capability), new(id), Hosted), Secret, Bearer);
        }
        var runs = Enumerable.Range(0, 65).Select(i => Run($"run-{i}")).ToImmutableArray();
        foreach (var request in new[]
        {
            Submit() with { Runs = [] },
            Submit() with { Runs = runs },
            Submit() with { Connections = ImmutableDictionary<string, ImmutableDictionary<string, string>>.Empty
                .Add("github", ImmutableDictionary<string, string>.Empty.Add("GH_TOKEN", "")) }
        })
            await Invalid(() => client.MergePlans.CreateAsync(Discovery(Capability), request, Hosted), Secret, Bearer);
        Check(handler.Calls == 0);
        Check((await client.MergePlans.CreateAsync(Discovery(Capability), Submit() with { Runs = runs[..64] }, Hosted)).Outcome
            == NativeAttemptOutcome.Acknowledged, "64 runs is native's maximum, not beyond it.");

        // Native sends an empty ID as an empty final segment.
        using var empty = new Handler((request, _) =>
        {
            Check(request.RequestUri!.OriginalString == "https://target.example/api/plans/", request.RequestUri.OriginalString);
            return Task.FromResult(Reply(request, Plan(id: "")));
        });
        using var emptyClient = ClientFor(empty);
        Check((await emptyClient.MergePlans.StatusAsync(Discovery(Capability), new(""), Hosted)).PlanId.Value == "" && empty.Calls == 1);
    }

    [Test]
    public async Task PlansUpToTheNativeMebibyteBoundAreRead()
    {
        // Native reads merge-plan results under 1 MiB, larger than the 64 KiB bound of other hosted results.
        var large = Plan(string.Join(",", Enumerable.Repeat(RunStatus.Replace("\"waitingReason\":null", $"\"waitingReason\":\"{new string('w', 2000)}\""), 64)));
        var oversized = large + new string(' ', 1024 * 1024);
        foreach (var (body, fits) in new[] { (large, true), (oversized, false) })
        {
            using var handler = new Handler((request, _) => Task.FromResult(Reply(request, body)));
            using var client = ClientFor(handler);
            var create = await client.MergePlans.CreateAsync(Discovery(Capability), Submit(), Hosted);
            Check(fits ? create.Outcome == NativeAttemptOutcome.Acknowledged
                : create.Outcome == NativeAttemptOutcome.Unknown && create.Failure is NativeHttpException { Kind: NativeHttpFailureKind.SizeLimit },
                $"{body.Length} bytes");
        }
    }

}
