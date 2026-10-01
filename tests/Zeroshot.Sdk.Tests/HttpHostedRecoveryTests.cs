using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TUnit.Core;
using Zeroshot.Native;
using Zeroshot.Native.Contracts;

namespace Zeroshot.Client.Tests;

// Source-backed fixtures from native hosted_runs/recovery_http.rs and contract/hosted_runs.rs; no hosted service evidence.
public sealed class HttpHostedRecoveryTests
{
    private const string Bearer = "ACCESS-BEARER-CANARY";
    private const string Secret = "SECRET-VALUE-CANARY";
    private const string Run = "run/1";
    private const string Encoded = "run%2F1";
    private const string Successor = "run-2";
    private const string Entry = """{"checkpointId":"opaque/a","sequence":1,"node":"worker","mapIndices":[0],"loopIterations":[],"createdAt":1700000000000}""";
    private const string Page = """{"runId":"run/1","checkpoints":[""" + Entry + """],"nextAfter":"opaque/a"}""";
    private const string Resumed = """{"runId":"run-2","resumedFrom":"run/1"}""";
    private const string Discarded = """{"runId":"run/1","discarded":true}""";
    private static readonly TargetControlCredentials Hosted = new(TargetAuthentication.HostedOauth, Bearer);
    private static readonly TargetHostedRunsDiscovery HostedRuns = new()
    {
        Kind = "zeroshot.hosted-runs/v1", BaseUrl = "https://target.example/api/", RouteTemplates = new()
        {
            List = "/runs", Status = "/runs/{run_id}", Watch = "/runs/{run_id}/watch{?from_cursor}",
            Logs = "/runs/{run_id}/logs{?from_cursor,execution}", Force = "/runs/{run_id}/force"
        }
    };
    private static readonly TargetHostedWorkspaceRecoveryDiscovery Capability = new()
    {
        Kind = "openengine.hosted-workspace-recovery/v1", RouteTemplates = new()
        {
            Resume = "/runs/{run_id}/resume", Checkpoints = "/runs/{run_id}/checkpoints", DiscardWorkspace = "/runs/{run_id}/discard-workspace"
        }
    };
    private static TargetDiscoveryDocument Discovery(TargetHostedWorkspaceRecoveryDiscovery? recovery = null,
        TargetAuthentication authentication = TargetAuthentication.HostedOauth, TargetHostedRunsDiscovery? hostedRuns = null)
        => TestDiscovery.Controller(authentication) with { Extensions = new() { HostedRuns = hostedRuns ?? HostedRuns, HostedWorkspaceRecovery = recovery } };
    private static readonly RunCheckpointsParams Checkpoints = new() { RunId = new(Run) };
    private static NativeClient Client(Handler handler) => NativeClient.ForHttp(
        new NativeClientOptions { Origin = new Uri("https://target.example/") }, new HttpClient(handler), ownsHttpClient: true);
    private static HttpResponseMessage Reply(HttpRequestMessage request, string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { RequestMessage = request, Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static void Check(bool value, string message = "Hosted recovery assertion failed.")
    { if (!value) throw new InvalidOperationException(message); }

    [Test]
    public async Task EachOperationPostsItsContractBodyToItsRecoveryRouteUnderTheHostedRunsBase()
    {
        var seen = new List<(string Url, string Body)>();
        using var handler = new Handler(async (request, token) =>
        {
            var url = request.RequestUri!.OriginalString;
            seen.Add((url, await request.Content!.ReadAsStringAsync(token)));
            Check(request.Method == HttpMethod.Post && request.Headers.Authorization!.ToString() == "Bearer " + Bearer &&
                request.Headers.CacheControl!.NoStore && request.Headers.Accept.Single().MediaType == "application/json" &&
                request.Content.Headers.ContentType!.MediaType == "application/json");
            return Reply(request, url.EndsWith("/checkpoints") ? Page : url.EndsWith("/resume") ? Resumed : Discarded);
        });
        using var client = Client(handler);
        var page = await client.HostedRecovery.CheckpointsAsync(Discovery(Capability),
            Checkpoints with { After = new("opaque/0"), Limit = 1 }, Hosted);
        var runCredentials = new TargetRunCredentials
        {
            Connections = ImmutableDictionary<string, ImmutableDictionary<string, string>>.Empty
                .Add("gateway", ImmutableDictionary<string, string>.Empty.Add("API_KEY", Secret)),
            GithubToken = "GITHUB-TOKEN"
        };
        var resumed = await client.HostedRecovery.ResumeAsync(Discovery(Capability), new(Run), new(Successor), Hosted,
            new CheckpointResumeFrom { CheckpointId = new("opaque/a") }, runCredentials);
        var restarted = await client.HostedRecovery.ResumeAsync(Discovery(Capability), new(Run), new(Successor), Hosted);
        var discarded = await client.HostedRecovery.DiscardWorkspaceAsync(Discovery(Capability), new(Run), Hosted);

        Check(page.NextAfter!.Value == "opaque/a" && page.Checkpoints.Single().CheckpointId.Value == "opaque/a");
        Check(resumed is { Outcome: NativeAttemptOutcome.Acknowledged, Operation: "hosted_workspace_recovery.resume" } &&
            resumed.Response!.RunId.Value == Successor && restarted.Outcome == NativeAttemptOutcome.Acknowledged);
        Check(discarded is { Outcome: NativeAttemptOutcome.Acknowledged, Operation: "hosted_workspace_recovery.discard_workspace" } &&
            discarded.Response!.Discarded);
        var expected = new[]
        {
            ($"https://target.example/api/runs/{Encoded}/checkpoints", """{"runId":"run/1","after":"opaque/0","limit":1}"""),
            ($"https://target.example/api/runs/{Encoded}/resume",
                """{"runId":"run/1","successorRunId":"run-2","from":{"kind":"checkpoint","checkpointId":"opaque/a"},"connections":{"gateway":{"API_KEY":"SECRET-VALUE-CANARY"}},"githubToken":"GITHUB-TOKEN"}"""),
            ($"https://target.example/api/runs/{Encoded}/resume", """{"runId":"run/1","successorRunId":"run-2"}"""),
            ($"https://target.example/api/runs/{Encoded}/discard-workspace", """{"runId":"run/1"}""")
        };
        Check(seen.Count == expected.Length, string.Join(" ", seen.Select(s => s.Url)));
        foreach (var ((url, body), (wantUrl, wantBody)) in seen.Zip(expected))
            Check(url == wantUrl && JsonNode.DeepEquals(JsonNode.Parse(body), JsonNode.Parse(wantBody)), $"{url} {body}");
    }

    [Test]
    public async Task DirectTargetsAbsentOrMalformedCapabilitiesAndUnaddressableRunsFailBeforeDispatch()
    {
        var routes = Capability.RouteTemplates;
        var cases = new (TargetDiscoveryDocument Discovery, TargetControlCredentials Credentials)[]
        {
            // Direct recovery is OECP, even when the direct target advertises its workspace capabilities.
            (Discovery(Capability, TargetAuthentication.None) with { Extensions = new()
            {
                WorkspaceRecovery = new() { Kind = "openengine.workspace-recovery/v1" },
                WorkspaceCheckpoints = new() { Kind = "openengine.workspace-checkpoints/v1" },
                HostedRuns = HostedRuns, HostedWorkspaceRecovery = Capability
            } }, Hosted),
            (Discovery(), Hosted),
            (Discovery(Capability with { Kind = "openengine.hosted-workspace-recovery/v2" }), Hosted),
            (Discovery(Capability, hostedRuns: HostedRuns with { BaseUrl = "https://attacker.example/api/" }), Hosted),
            (Discovery(Capability with { RouteTemplates = routes with { Resume = "/runs/resume" } }), Hosted),
            (Discovery(Capability with { RouteTemplates = routes with { Checkpoints = "/runs/{run_id}/{checkpoint_id}" } }), Hosted),
            (Discovery(Capability with { RouteTemplates = routes with { Checkpoints = "/runs/{run_id}/checkpoints?limit=1" } }), Hosted),
            (Discovery(Capability with { RouteTemplates = routes with { DiscardWorkspace = "/runs/{run_id}/../discard" } }), Hosted),
            (Discovery(Capability with { RouteTemplates = routes with { DiscardWorkspace = "https://attacker.example/{run_id}" } }), Hosted),
        };
        using var handler = new Handler((request, _) => Task.FromResult(Reply(request, Discarded)));
        using var client = Client(handler);
        foreach (var (discovery, credentials) in cases)
        {
            // Every operation refuses, because native compiles all three recovery routes together.
            await Invalid(() => client.HostedRecovery.CheckpointsAsync(discovery, Checkpoints, credentials));
            await Invalid(() => client.HostedRecovery.ResumeAsync(discovery, new(Run), new(Successor), credentials));
            await Invalid(() => client.HostedRecovery.DiscardWorkspaceAsync(discovery, new(Run), credentials));
        }
        // Native's path-segment setter would drop this ID and address another route.
        await Invalid(() => client.HostedRecovery.DiscardWorkspaceAsync(Discovery(Capability), new(".."), Hosted));
        Check(handler.Calls == 0, $"{handler.Calls} requests sent");
    }

    [Test]
    public async Task RepliesMustNameTheRequestedRunsAndKeepThePageContract()
    {
        var reply = "";
        using var handler = new Handler((request, _) => Task.FromResult(Reply(request, reply)));
        using var client = Client(handler);
        foreach (var page in new[]
        {
            Page.Replace("\"runId\":\"run/1\"", "\"runId\":\"run-9\""),
            Page.Replace("\"nextAfter\":\"opaque/a\"", "\"nextAfter\":\"opaque/z\""),
            Page.Replace(Entry, Entry + "," + Entry.Replace("opaque/a", "opaque/b").Replace("\"sequence\":1", "\"sequence\":2"))
                .Replace("\"nextAfter\":\"opaque/a\"", "\"nextAfter\":\"opaque/b\"")
        })
        {
            reply = page;
            await Failure(client.HostedRecovery.CheckpointsAsync(Discovery(Capability), Checkpoints with { Limit = 1 }, Hosted),
                NativeHttpFailureKind.Protocol);
        }
        foreach (var foreign in new[] { """{"runId":"run-9","resumedFrom":"run/1"}""", """{"runId":"run-2","resumedFrom":"run-9"}""" })
        {
            reply = foreign;
            var resumed = await client.HostedRecovery.ResumeAsync(Discovery(Capability), new(Run), new(Successor), Hosted);
            Check(resumed is { Outcome: NativeAttemptOutcome.Unknown, Response: null, Failure: NativeHttpException { Kind: NativeHttpFailureKind.Protocol } }, foreign);
        }
        reply = """{"runId":"run-9","discarded":true}""";
        var discarded = await client.HostedRecovery.DiscardWorkspaceAsync(Discovery(Capability), new(Run), Hosted);
        Check(discarded is { Outcome: NativeAttemptOutcome.Unknown, Response: null });
    }

    [Test]
    public async Task OnlyHostedPreEffectProblemPairsRejectAndEveryOtherFailureLeavesTheEffectUnknown()
    {
        foreach (var (status, code, rejected) in new[]
        {
            (400, "invalid_request", true), (401, "unauthorized", true), (403, "forbidden", true), (404, "not_found", true),
            (409, "IDEMPOTENCY_REUSE", false), (500, "INTERNAL_ERROR", false)
        })
        {
            using var handler = new Handler((request, _) => Task.FromResult(Reply(request,
                JsonSerializer.Serialize(new { code, message = "refused" }), (HttpStatusCode)status)));
            using var client = Client(handler);
            var resume = await client.HostedRecovery.ResumeAsync(Discovery(Capability), new(Run), new(Successor), Hosted);
            var discard = await client.HostedRecovery.DiscardWorkspaceAsync(Discovery(Capability), new(Run), Hosted);
            Check(resume.Outcome == discard.Outcome &&
                resume.Outcome == (rejected ? NativeAttemptOutcome.Rejected : NativeAttemptOutcome.Unknown), $"{status} {code}");
            var read = await Failure(client.HostedRecovery.CheckpointsAsync(Discovery(Capability), Checkpoints, Hosted), NativeHttpFailureKind.HttpStatus);
            Check(read.Problem!.Code == code && handler.Calls == 3, $"{status} sent {handler.Calls} requests");
        }

        using (var lost = new Handler((_, _) => throw new HttpRequestException("connection reset")))
        using (var peer = Client(lost))
        {
            var resume = await peer.HostedRecovery.ResumeAsync(Discovery(Capability), new(Run), new(Successor), Hosted);
            var discard = await peer.HostedRecovery.DiscardWorkspaceAsync(Discovery(Capability), new(Run), Hosted);
            Check(resume.Outcome == NativeAttemptOutcome.Unknown && discard.Outcome == NativeAttemptOutcome.Unknown && lost.Calls == 2);
        }

        // Native reads every hosted recovery result under 64 KiB.
        using var large = new Handler((request, _) => Task.FromResult(Reply(request,
            request.RequestUri!.OriginalString.EndsWith("/checkpoints") ? Page + new string(' ', 64 * 1024) : Resumed + new string(' ', 64 * 1024))));
        using var bounded = Client(large);
        var oversized = await bounded.HostedRecovery.ResumeAsync(Discovery(Capability), new(Run), new(Successor), Hosted);
        Check(oversized is { Outcome: NativeAttemptOutcome.Unknown, Failure: NativeHttpException { Kind: NativeHttpFailureKind.SizeLimit } });
        await Failure(bounded.HostedRecovery.CheckpointsAsync(Discovery(Capability), Checkpoints, Hosted), NativeHttpFailureKind.SizeLimit);
    }

    private static async Task Invalid(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) when (error is ArgumentException or JsonException)
        { Check(!error.ToString().Contains(Bearer)); return; }
        throw new InvalidOperationException("Expected invalid caller input.");
    }

    private static async Task<NativeHttpException> Failure(Task task, NativeHttpFailureKind kind)
    {
        try { await task; }
        catch (NativeHttpException error) { Check(error.Kind == kind, error.ToString()); return error; }
        throw new InvalidOperationException("Expected native failure.");
    }
}
