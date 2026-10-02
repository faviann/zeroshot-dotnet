using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TUnit.Core;
using Zeroshot.Native;
using Zeroshot.Native.Contracts;

namespace Zeroshot.Client.Tests;

public sealed class HistoryTests
{
    private const string Run = "018f5e78-7f95-7c22-8d98-3f15af20c991";
    private const string Bearer = "HISTORY-CREDENTIAL-CANARY";
    private static readonly JsonNode Golden = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/history.json")))!;
    private static JsonNode Fixture(string name) => Golden[name]!.DeepClone();
    private static byte[] Bytes(JsonNode node) => Encoding.UTF8.GetBytes(node.ToJsonString());

    private static TargetDiscoveryDocument Discovery(TargetAuthentication authentication = TargetAuthentication.None,
        string baseUrl = "https://target.example/history", string list = "/runs{?after}",
        string detail = "/runs/{run_id}", string page = "/runs/{run_id}/page{?after}", string kind = NativeHistoryClient.Kind)
        => TestDiscovery.Controller(authentication) with
        {
            Extensions = new() { RunHistory = new() { Kind = kind, BaseUrl = baseUrl, RouteTemplates = new() { List = list, Detail = detail, Page = page } } }
        };

    [Test]
    public void GoldenRecordsKeepAllNineEventsMixedIdentitiesAndNativeOmissionRules()
    {
        var page = NativeJson.DeserializeUtf8<HistoryPage>(Bytes(Fixture("page")));
        Check(page.Events.Select(e => e.Event.GetType()).SequenceEqual(new[]
        {
            typeof(PriorExecutionHistoryEvent), typeof(RunStartedHistoryEvent), typeof(NodeStartedHistoryEvent),
            typeof(SafeLogHistoryEvent), typeof(TokenUsageObservedHistoryEvent), typeof(NodeCompletedHistoryEvent),
            typeof(ExecutionVoidedHistoryEvent), typeof(ForceStopRequestedHistoryEvent), typeof(TerminalHistoryEvent)
        }));
        var prior = ((PriorExecutionHistoryEvent)page.Events[0].Event).Execution;
        Check(prior.Execution == ulong.MaxValue && prior.NodeInstance == 2 &&
            prior.State is SettledExecutionState { Position: 5, Outcome: ErrorOutcome });
        var started = (NodeStartedHistoryEvent)page.Events[2].Event;
        Check(started.Reference.Execution.Value == "18446744073709551615" && started.Reference.NodeInstance.Value == "3" &&
            started.Input.ValueKind == JsonValueKind.Null);
        Check(((SafeLogHistoryEvent)page.Events[3].Event).Line.Value == "multi\nline\toutput");
        Check(((TokenUsageObservedHistoryEvent)page.Events[4].Event).Usage is { CacheReadInputTokens: null, CacheCreationInputTokens.Value: 0 });
        Check(page.Control[0].Output is { HasValue: false } && page.Control[1].Output is { HasValue: true, Value.ValueKind: JsonValueKind.Null });

        // Unknown fields of open native records are ignored and not re-emitted; the legacy snapshot is decode-only.
        var expectedPage = Fixture("page");
        expectedPage["control"]![1]!.AsObject().Remove("hostOnly");
        Check(JsonNode.DeepEquals(expectedPage, JsonNode.Parse(NativeJson.SerializeUtf8(page))));
        var definition = Fixture("definition");
        var decoded = NativeJson.DeserializeUtf8<RunDefinition>(Bytes(definition));
        definition.AsObject().Remove("snapshot");
        Check(JsonNode.DeepEquals(definition, JsonNode.Parse(NativeJson.SerializeUtf8(decoded))));
        var list = Fixture("list");
        var summaries = NativeJson.DeserializeUtf8<RunHistoryList>(Bytes(list));
        list["runs"]![0]!["runtimeFailure"]!.AsObject().Remove("hostOnly");
        // Required nullable summary fields are emitted as null rather than omitted.
        Check(JsonNode.DeepEquals(list, JsonNode.Parse(NativeJson.SerializeUtf8(summaries))));
        Check(JsonNode.Parse(NativeJson.SerializeUtf8(page.Events[8].Event))!["kind"]!.GetValue<string>() == "terminal");
    }

    public static IEnumerable<(string Section, string Case, Action<JsonNode> Change, bool Valid)> ShapeCases()
    {
        static JsonNode Event(JsonNode page, int index) => page["events"]![index]!["event"]!;
        yield return ("list", "required nullable field omitted", n => n["runs"]![1]!.AsObject().Remove("cursor"), false);
        yield return ("list", "unknown summary field", n => n["runs"]![1]!["output"] = 1, false);
        yield return ("list", "success synopsis carries output", n => n["runs"]![0]!["terminal"] = JsonNode.Parse("""{"status":"succeeded","output":1}"""), false);
        yield return ("definition", "snapshot explicitly null", n => n["snapshot"] = null, true);
        yield return ("definition", "pinned graph schema", n => n["graph"]!["root"]!["kind"] = "unknown", false);
        yield return ("definition", "unknown definition field", n => n["extra"] = 1, false);
        yield return ("page", "unknown event kind", n => Event(n, 1)["kind"] = "run_resumed", false);
        yield return ("page", "unit event ignores fields like serde", n => Event(n, 1)["extra"] = 1, true);
        yield return ("page", "strict event fields", n => Event(n, 6)["extra"] = 1, false);
        yield return ("page", "projected identity must be a string", n => Event(n, 2)["reference"]!["execution"] = 3, false);
        yield return ("page", "projected identity is canonical positive decimal", n => Event(n, 2)["reference"]!["nodeInstance"] = "03", false);
        yield return ("page", "projected identity is u64", n => Event(n, 3)["execution"] = "18446744073709551616", false);
        yield return ("page", "durable identity stays numeric", n => Event(n, 0)["execution"]!["node_instance"] = "2", false);
        yield return ("page", "durable identity is positive", n => Event(n, 0)["execution"]!["execution"] = 0, false);
        yield return ("page", "durable position fits i64", n => Event(n, 0)["execution"]!["dispatch_position"] = 9223372036854775808UL, false);
        yield return ("page", "durable record is open", n => Event(n, 0)["execution"]!["future"] = true, true);
        yield return ("page", "durable state is one variant", n => Event(n, 0)["execution"]!["state"]!["Voided"] = JsonNode.Parse("""{"position":1,"reason":"map_terminal"}"""), false);
        yield return ("page", "active durable state", n => Event(n, 0)["execution"]!["state"] = "Active", true);
        yield return ("page", "typed worker outcome", n => Event(n, 5)["completion"]!["outcome"]!["status"] = "unknown", false);
        // Only the pinned schema relates error code and reason, including inside the externally tagged durable state.
        yield return ("page", "pinned outcome schema in durable state", n => Event(n, 0)["execution"]!["state"]!["Settled"]!["outcome"]!["code"] = "timeout", false);
        yield return ("page", "pinned outcome schema in completion", n => Event(n, 5)["completion"]!["outcome"] = JsonNode.Parse("""{"status":"error","code":"timeout","reason":"policy_denied"}"""), false);
        yield return ("page", "safe log NUL", n => Event(n, 3)["line"] = "a\0b", false);
        yield return ("page", "safe log at 16 KiB", n => Event(n, 3)["line"] = new string('x', 16_384), true);
        yield return ("page", "safe log over 16 KiB", n => Event(n, 3)["line"] = new string('x', 16_385), false);
        yield return ("page", "run-wide safe log", n => Event(n, 3)["execution"] = null, true);
        yield return ("page", "usage cache count required", n => Event(n, 4)["usage"]!.AsObject().Remove("cacheReadInputTokens"), false);
        yield return ("page", "explicit null control", n => n["control"] = null, false);
        yield return ("page", "omitted control", n => n.AsObject().Remove("control"), true);
        yield return ("page", "unknown observation state", n => n["observation"]!["state"] = "stale", false);
    }

    [Test]
    [MethodDataSource(nameof(ShapeCases))]
    public void WireShapeFollowsNativeDecoding(string section, string name, Action<JsonNode> change, bool valid)
    {
        var node = Fixture(section);
        change(node);
        var bytes = Bytes(node);
        Action decode = section switch
        {
            "list" => () => NativeJson.DeserializeUtf8<RunHistoryList>(bytes),
            "definition" => () => NativeJson.DeserializeUtf8<RunDefinition>(bytes),
            _ => () => NativeJson.DeserializeUtf8<HistoryPage>(bytes)
        };
        var accepted = true;
        try { decode(); }
        catch (JsonException) { accepted = false; }
        Check(accepted == valid, name);
    }

    [Test]
    public void DuplicateKnownFieldsAreRejected()
    {
        var text = Fixture("list").ToJsonString().Replace("\"title\":\"Queued run\"", "\"title\":\"Queued run\",\"title\":\"again\"");
        try { NativeJson.DeserializeUtf8<RunHistoryList>(Encoding.UTF8.GetBytes(text)); }
        catch (JsonException) { return; }
        throw new InvalidOperationException("Expected duplicate rejection.");
    }

    [Test]
    public async Task DiscoveredRoutesUseNativeHeadersEncodingAndCloseUiRoutedConnections()
    {
        var seen = new List<HttpRequestMessage>();
        using var native = ClientFor(new Handler((request, _) =>
        {
            seen.Add(request);
            var path = request.RequestUri!.AbsolutePath;
            var body = path.EndsWith("/page") ? Fixture("page") : path.EndsWith(Run) ? Fixture("definition") : Fixture("list");
            return Task.FromResult(Reply(request, request.Method == HttpMethod.Head ? "" : body.ToJsonString()));
        }));
        var direct = Discovery();
        var list = await native.History.ListAsync(direct, new RunId("0195af77-1000-7000-8000-000000000011"));
        var definition = await native.History.DetailAsync(direct, new RunId(Run));
        var page = await native.History.PageAsync(direct, new RunId(Run));
        var later = await native.History.PageAsync(Discovery(TargetAuthentication.HostedOauth), new RunId(Run), new Cursor("v2:0"),
            new TargetControlCredentials(TargetAuthentication.HostedOauth, Bearer));
        Check(list.Runs.Length == 2 && definition.Graph.Root is SucceedNode && page.Events.Length == 9 &&
            NativeJson.SerializeUtf8(later).SequenceEqual(NativeJson.SerializeUtf8(page)));
        var heads = new[]
        {
            await native.History.HeadListAsync(direct), await native.History.HeadDetailAsync(direct, new RunId(Run)),
            await native.History.HeadPageAsync(direct, new RunId(Run), new Cursor("v2:9"))
        };
        Check(heads.All(head => head.StatusCode == HttpStatusCode.OK && head.MediaType == "application/json"));
        Check(seen.Select(r => r.Method.Method + " " + r.RequestUri!.PathAndQuery).SequenceEqual(new[]
        {
            "GET /history/runs?after=0195af77-1000-7000-8000-000000000011", "GET /history/runs/" + Run,
            "GET /history/runs/" + Run + "/page?after=v2%3A0", "GET /history/runs/" + Run + "/page?after=v2%3A0",
            "HEAD /history/runs", "HEAD /history/runs/" + Run, "HEAD /history/runs/" + Run + "/page?after=v2%3A9"
        }));
        Check(seen.All(r => r.Headers.CacheControl!.NoStore && r.Headers.Accept.Single().MediaType == "application/json" && r.Content is null));
        // The direct UI mount keeps the connection for its UI router; only the hosted request carries the bearer.
        Check(seen.Where(r => r.Headers.Authorization is null).All(r => r.Headers.ConnectionClose == true));
        Check(seen.Single(r => r.Headers.Authorization is not null).Headers.Authorization is { Scheme: "Bearer", Parameter: Bearer });
    }

    public static IEnumerable<(string Case, Func<NativeClient, Task> Call)> InvalidUses()
    {
        var run = new RunId(Run);
        yield return ("absent capability", n => n.History.ListAsync(Discovery() with { Extensions = new() }));
        yield return ("wrong capability kind", n => n.History.ListAsync(Discovery(kind: "zeroshot.run-history/v2")));
        yield return ("private authority", n => n.History.ListAsync(Discovery(TargetAuthentication.PrivateCapability), credentials: new(TargetAuthentication.PrivateCapability, Bearer)));
        yield return ("missing hosted credentials", n => n.History.ListAsync(Discovery(TargetAuthentication.HostedOauth)));
        yield return ("credentials for direct history", n => n.History.ListAsync(Discovery(), credentials: new(TargetAuthentication.HostedOauth, Bearer)));
        yield return ("cross-origin base", n => n.History.ListAsync(Discovery(baseUrl: "https://other.example/history")));
        yield return ("list declares run", n => n.History.ListAsync(Discovery(list: "/runs/{run_id}{?after}")));
        yield return ("list without after", n => n.History.ListAsync(Discovery(list: "/runs")));
        yield return ("detail with after", n => n.History.DetailAsync(Discovery(detail: "/runs/{run_id}{?after}"), run));
        yield return ("detail without run", n => n.History.DetailAsync(Discovery(detail: "/runs"), run));
        yield return ("malformed unused page template", n => n.History.ListAsync(Discovery(page: "/runs/{run_id}/page")));
        yield return ("page repeats run", n => n.History.PageAsync(Discovery(page: "/runs/{run_id}/{run_id}{?after}"), run));
        yield return ("run variable inside a segment", n => n.History.PageAsync(Discovery(page: "/runs/x{run_id}/page{?after}"), run));
        yield return ("other query variable", n => n.History.PageAsync(Discovery(page: "/runs/{run_id}/page{?after,x}"), run));
        yield return ("dot segment", n => n.History.DetailAsync(Discovery(detail: "/runs/../{run_id}"), run));
        yield return ("double slash", n => n.History.DetailAsync(Discovery(detail: "//runs/{run_id}"), run));
        yield return ("backslash", n => n.History.DetailAsync(Discovery(detail: "/runs\\{run_id}"), run));
        yield return ("non-UUIDv7 run", n => n.History.DetailAsync(Discovery(), new RunId("018f5e78-7f95-4c22-8d98-3f15af20c991")));
        yield return ("non-UUIDv7 list position", n => n.History.ListAsync(Discovery(), new RunId("not-a-run")));
        yield return ("non-canonical cursor", n => n.History.PageAsync(Discovery(), run, new Cursor("v2:01")));
        yield return ("cursor beyond i64", n => n.History.HeadPageAsync(Discovery(), run, new Cursor("v2:9223372036854775808")));
    }

    [Test]
    [MethodDataSource(nameof(InvalidUses))]
    public async Task InvalidCapabilityOrArgumentsFailBeforeDispatch(string name, Func<NativeClient, Task> call)
    {
        var handler = new Handler((request, _) => Task.FromResult(Reply(request, "{}")));
        using var native = ClientFor(handler);
        try { await call(native); }
        catch (ArgumentException error) { Check(handler.Calls == 0 && !error.ToString().Contains(Bearer), name); return; }
        throw new InvalidOperationException("Expected invalid use: " + name);
    }

    public static IEnumerable<(string Case, string Section, Action<JsonNode> Change, string? After, bool Valid)> ContractCases()
    {
        static JsonNode Run(JsonNode list, int i) => list["runs"]![i]!;
        yield return ("list over 50 entries", "list", n => { var runs = n["runs"]!.AsArray(); for (var i = 0; i < 49; i++) runs.Insert(0, Run(n, 1).DeepClone()); }, null, false);
        yield return ("list not descending", "list", n => { var runs = n["runs"]!.AsArray(); var first = runs[0]!; runs.RemoveAt(0); runs.Add(first); n["nextCursor"] = Run(n, 1)["runId"]!.DeepClone(); }, null, false);
        yield return ("list entry not after position", "list", _ => { }, "0195af77-1000-7000-8000-000000000010", false);
        yield return ("list strictly after position", "list", _ => { }, "0195af77-1000-7000-8000-000000000011", true);
        yield return ("list next is not last", "list", n => n["nextCursor"] = "0195af77-1000-7000-8000-000000000010", null, false);
        yield return ("list next without runs", "list", n => n["runs"] = new JsonArray(), null, false);
        yield return ("list final page", "list", n => n["nextCursor"] = null, null, true);
        yield return ("summary non-UUIDv7", "list", n => { Run(n, 1)["runId"] = "018f5e78-7f95-4c22-8d98-3f15af20c991"; n["nextCursor"] = null; }, null, false);
        yield return ("summary cursor without availability", "list", n => Run(n, 1)["cursor"] = "v2:0", null, false);
        yield return ("summary cursor uses native parser", "list", n => Run(n, 0)["cursor"] = "v2:+02", null, true);
        yield return ("summary cursor malformed", "list", n => Run(n, 0)["cursor"] = "v3:2", null, false);
        yield return ("unavailable summary with terminal", "list", n => { Run(n, 1)["phase"] = "unavailable"; Run(n, 1)["terminal"] = JsonNode.Parse("""{"status":"succeeded"}"""); }, null, false);
        yield return ("unavailable summary", "list", n => Run(n, 1)["phase"] = "unavailable", null, true);
        yield return ("terminal before finished", "list", n => Run(n, 0)["phase"] = "running", null, false);
        yield return ("runtime failure reason differs", "list", n => Run(n, 0)["runtimeFailure"]!["reason"] = "runtime_lost", null, false);
        yield return ("definition version", "definition", n => n["version"] = 2, null, false);
        yield return ("definition projection version", "definition", n => n["projectionVersion"] = 2, null, false);
        yield return ("definition foreign run", "definition", n => n["runId"] = "0195af77-1000-7000-8000-000000000010", null, false);
        yield return ("definition without history", "definition", n => n["historyAvailable"] = false, null, false);
        yield return ("definition initial cursor", "definition", n => n["history"]!["initialCursor"] = "v2:1", null, false);
        yield return ("definition cursors differ", "definition", n => n["history"]!["cursor"] = "v2:8", null, false);
        yield return ("definition non-canonical cursor", "definition", n => { n["cursor"] = "v2:09"; n["history"]!["cursor"] = "v2:09"; }, null, false);
        yield return ("definition active and unavailable", "definition", n => { n["phase"] = "running"; n["terminal"] = null; n["history"]!["complete"] = false; n["observation"] = JsonNode.Parse("""{"state":"incomplete","code":"runtime_unavailable"}"""); }, null, true);
        yield return ("definition status-only runtime failure", "definition", RuntimeFailed, null, true);
        yield return ("runtime failure claims complete history", "definition", n => { RuntimeFailed(n); n["history"]!["complete"] = true; }, null, false);
        yield return ("runtime failure unknown reason", "definition", n => { RuntimeFailed(n); n["runtimeFailure"]!["reason"] = "runtime_gone"; n["terminal"]!["reason"] = "runtime_gone"; }, null, false);
        yield return ("runtime failure after head", "definition", n => { RuntimeFailed(n); n["runtimeFailure"]!["atCursor"] = "v2:10"; }, null, false);
        yield return ("runtime failure without matching terminal", "definition", n => { RuntimeFailed(n); n["terminal"]!["reason"] = "worker_failed"; }, null, false);
        yield return ("page gap", "page", n => n["events"]![4]!["cursor"] = "v2:6", null, false);
        yield return ("page non-canonical event cursor", "page", n => n["events"]![1]!["cursor"] = "v2:02", null, false);
        yield return ("page next beyond head", "page", n => { n["nextCursor"] = "v2:10"; n["complete"] = false; }, null, false);
        yield return ("page next not last event", "page", n => { n["nextCursor"] = "v2:8"; n["complete"] = false; }, null, false);
        yield return ("page complete mismatch", "page", n => n["complete"] = false, null, false);
        yield return ("page before request", "page", _ => { }, "v2:9", false);
        yield return ("page partial with more", "page", n => { n["headCursor"] = "v2:12"; n["complete"] = false; n["finished"] = false; n["observation"] = JsonNode.Parse("""{"state":"active"}"""); }, null, true);
        yield return ("page empty with more", "page", n => { n["events"] = new JsonArray(); n["control"] = new JsonArray(); n["nextCursor"] = "v2:0"; n["headCursor"] = "v2:3"; n["complete"] = false; }, null, false);
        yield return ("page empty at head", "page", n => { n["events"] = new JsonArray(); n["control"] = new JsonArray(); }, "v2:9", true);
        yield return ("page over 256 events", "page", n =>
        {
            var events = new JsonArray();
            for (var i = 1; i <= 257; i++) events.Add(JsonNode.Parse($$$"""{"cursor":"v2:{{{i}}}","event":{"kind":"run_started"}}"""));
            n["events"] = events; n["control"] = new JsonArray(); n["nextCursor"] = "v2:257"; n["headCursor"] = "v2:257";
        }, null, false);
        yield return ("control outside page", "page", n => n["control"]![0]!["cursor"] = "v2:10", null, false);
        yield return ("control out of order", "page", n => { n["control"]![0]!["cursor"] = "v2:6"; n["control"]![1]!["cursor"] = "v2:3"; }, null, false);
        yield return ("expired observation of finished run", "page", n => n["observation"] = JsonNode.Parse("""{"state":"expired","code":"history_expired"}"""), null, true);
        yield return ("status-only runtime failure page", "page", n => { n["events"]!.AsArray().RemoveAt(8); n["control"] = new JsonArray(); n["nextCursor"] = "v2:8"; n["headCursor"] = "v2:8"; n["runtimeFailure"] = JsonNode.Parse("""{"atCursor":"v2:7","reason":"runtime_failed"}"""); n["observation"] = JsonNode.Parse("""{"state":"incomplete","code":"runtime_unavailable"}"""); }, null, true);
        yield return ("runtime failure before finished", "page", n => { n["runtimeFailure"] = JsonNode.Parse("""{"atCursor":"v2:7","reason":"runtime_lost"}"""); n["finished"] = false; }, null, false);
    }

    private static void RuntimeFailed(JsonNode definition)
    {
        definition["terminal"] = JsonNode.Parse("""{"status":"failed","reason":"runtime_failed"}""");
        definition["history"]!["complete"] = false;
        definition["runtimeFailure"] = JsonNode.Parse("""{"atCursor":"v2:7","reason":"runtime_failed"}""");
        definition["observation"] = JsonNode.Parse("""{"state":"active"}""");
    }

    [Test]
    [MethodDataSource(nameof(ContractCases))]
    public async Task HostRecordsMustSatisfyTheNativeHistoryContract(string name, string section, Action<JsonNode> change, string? after, bool valid)
    {
        var node = Fixture(section);
        change(node);
        using var native = ClientFor(new Handler((request, _) => Task.FromResult(Reply(request, node.ToJsonString()))));
        Task call = section switch
        {
            "list" => native.History.ListAsync(Discovery(), after is null ? null : new RunId(after)),
            "definition" => native.History.DetailAsync(Discovery(), new RunId(Run)),
            _ => native.History.PageAsync(Discovery(), new RunId(Run), after is null ? null : new Cursor(after))
        };
        try { await call; Check(valid, name); }
        catch (NativeHttpException error) { Check(!valid && error.Kind == NativeHttpFailureKind.Protocol, name); }
    }

    public static IEnumerable<(bool Hosted, string Body, HttpStatusCode Status, RunHistoryProblemCode? Expected)> Problems()
    {
        var codes = new (string, HttpStatusCode, RunHistoryProblemCode)[]
        {
            ("run_not_found", HttpStatusCode.NotFound, RunHistoryProblemCode.RunNotFound), ("not_found", HttpStatusCode.NotFound, RunHistoryProblemCode.NotFound),
            ("forbidden", HttpStatusCode.Forbidden, RunHistoryProblemCode.Forbidden), ("invalid_cursor", HttpStatusCode.BadRequest, RunHistoryProblemCode.InvalidCursor),
            ("history_gap", HttpStatusCode.Conflict, RunHistoryProblemCode.HistoryGap), ("runtime_unavailable", HttpStatusCode.ServiceUnavailable, RunHistoryProblemCode.RuntimeUnavailable),
            ("history_unavailable", HttpStatusCode.ServiceUnavailable, RunHistoryProblemCode.HistoryUnavailable), ("history_incomplete", HttpStatusCode.ServiceUnavailable, RunHistoryProblemCode.HistoryIncomplete),
            ("history_pending", HttpStatusCode.ServiceUnavailable, RunHistoryProblemCode.HistoryPending), ("history_invalid", HttpStatusCode.BadGateway, RunHistoryProblemCode.HistoryInvalid),
            ("history_expired", HttpStatusCode.Gone, RunHistoryProblemCode.HistoryExpired), ("history_incompatible", HttpStatusCode.BadGateway, RunHistoryProblemCode.HistoryIncompatible)
        };
        // The direct UI mount sends ApiError {code,message}; hosted hosts send TargetHttpProblem.
        foreach (var hosted in new[] { false, true })
            foreach (var (code, status, expected) in codes)
                yield return (hosted, $$"""{"code":"{{code}}","message":"History refused."}""", status, expected);
        // UI-router boundary problems share the shape but are not history categories.
        yield return (false, """{"code":"origin_rejected","message":"Open the UI using its configured public URL."}""", HttpStatusCode.Forbidden, null);
        // UI-router messages are not bound by TargetHttpProblem's single-line 1 KiB rule.
        yield return (false, $$"""{"code":"history_unavailable","message":"{{string.Concat(Enumerable.Repeat("line\\n", 400))}}"}""", HttpStatusCode.ServiceUnavailable, RunHistoryProblemCode.HistoryUnavailable);
        yield return (false, "not a problem", HttpStatusCode.ServiceUnavailable, null);
        yield return (true, "not a problem", HttpStatusCode.ServiceUnavailable, null);
    }

    [Test]
    [MethodDataSource(nameof(Problems))]
    public async Task RefusalsKeepStatusAndClosedHistoryCategory(bool hosted, string body, HttpStatusCode status, RunHistoryProblemCode? expected)
    {
        using var native = ClientFor(new Handler((request, _) => Task.FromResult(Reply(request, body, status))));
        try
        {
            await (hosted
                ? native.History.PageAsync(Discovery(TargetAuthentication.HostedOauth), new RunId(Run), credentials: new(TargetAuthentication.HostedOauth, Bearer))
                : native.History.PageAsync(Discovery(), new RunId(Run)));
        }
        catch (NativeHttpException error)
        {
            Check(error.Kind == NativeHttpFailureKind.HttpStatus && error.StatusCode == status && error.HistoryProblem == expected);
            Check(body.StartsWith('{') == (hosted ? error.Problem : (object?)error.UiProblem) is not null);
            Check((hosted ? (object?)error.UiProblem : error.Problem) is null);
            return;
        }
        throw new InvalidOperationException("Expected refusal.");
    }

    [Test]
    public async Task HistoryUsesItsNativeListAndProblemBounds()
    {
        var transport = new TransportOptions { MaxErrorBodyBytes = 1024 * 1024 };
        var list = Fixture("list");
        list["runs"]![1]!["title"] = new string('x', 4 * 1024 * 1024);
        using var large = ClientFor(new Handler((request, _) => Task.FromResult(Reply(request, list.ToJsonString()))), transport);
        await Expect(large.History.ListAsync(Discovery()), NativeHttpFailureKind.SizeLimit);
        var problem = $$$"""{"code":"history_gap","message":"x","details":{"pad":"{{{new string('x', 64 * 1024)}}}"}}""";
        using var refused = ClientFor(new Handler((request, _) => Task.FromResult(Reply(request, problem, HttpStatusCode.Conflict))), transport);
        var error = await Expect(refused.History.DetailAsync(Discovery(), new RunId(Run)), NativeHttpFailureKind.SizeLimit);
        Check(error.StatusCode == HttpStatusCode.Conflict && error.HistoryProblem is null);
    }

    [Test]
    public async Task HeadRefusalKeepsStatusWithoutInventingAProblem()
    {
        using var native = ClientFor(new Handler((request, _) => Task.FromResult(Reply(request, "", HttpStatusCode.NotFound))));
        var error = await Expect(native.History.HeadDetailAsync(Discovery(), new RunId(Run)), NativeHttpFailureKind.HttpStatus);
        Check(error.StatusCode == HttpStatusCode.NotFound && error.Problem is null && error.HistoryProblem is null);
    }

    private static async Task<NativeHttpException> Expect(Task task, NativeHttpFailureKind kind)
    {
        try { await task; }
        catch (NativeHttpException error) { Check(error.Kind == kind, error.ToString()); return error; }
        throw new InvalidOperationException("Expected HTTP failure.");
    }

    private static HttpResponseMessage Reply(HttpRequestMessage request, string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        var reply = new HttpResponseMessage(status) { RequestMessage = request, Content = new StringContent(body, Encoding.UTF8, "application/json") };
        reply.Content.Headers.ContentType = new("application/json");
        return reply;
    }
}
