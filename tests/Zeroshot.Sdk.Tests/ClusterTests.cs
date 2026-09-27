using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TUnit.Core;
using Zeroshot.Native;
using Zeroshot.Native.Contracts;
using static Zeroshot.Client.Tests.OecpTests;

namespace Zeroshot.Client.Tests;

// Fixtures/cluster holds verbatim pinned native 10.9.0 protocol goldens (protocol/openengine-cluster/v1).
public sealed class ClusterTests
{
    private const string Operational = """{"labels":{},"logLevel":"info","dispatchState":"active","inFlight":0}""";
    private static readonly byte[] Graph = Encoding.UTF8.GetBytes(JsonNode.Parse(File.ReadLines(Fixture("admission-lifecycle.ndjson")).ElementAt(2))!["params"]!["graph"]!.ToJsonString());
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures/cluster", name);
    private static T Parse<T>(string json) => NativeJson.DeserializeUtf8<T>(Encoding.UTF8.GetBytes(json));
    private static void Check(bool value, string message = "Cluster assertion failed.")
    { if (!value) throw new InvalidOperationException(message); }
    private static void Reject<T>(string json)
    {
        try { _ = Parse<T>(json); }
        catch (JsonException) { return; }
        throw new InvalidOperationException("Invalid cluster contract was accepted.");
    }
    private static bool Same(string expected, byte[] actual) => JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual));
    private static Task Frame(WebSocket socket, string method, string parameters)
        => Send(socket, $$"""{"jsonrpc":"2.0","method":"{{method}}","params":{{parameters}}}""");
    private static Task Error(WebSocket socket, JsonElement request, long code, string domain, string details = "null")
        => Send(socket, $$"""{"jsonrpc":"2.0","id":{{request.GetProperty("id")}},"error":{"code":{{code}},"message":"refused","data":{"code":"{{domain}}","details":""" + details + "}}}");
    private static GraphSpec TypedGraph => NativeJson.DeserializeUtf8<GraphSpec>(Graph);

    private static async Task<(object? Result, Exception? Failure)> Invoke(OecpConnection connection, string method, string parameters) => method switch
    {
        "initialize" => (await connection.InitializeAsync(Parse<InitializeParams>(parameters)), null),
        "plan" => (await connection.Cluster.PlanAsync(Parse<PlanParams>(parameters)), null),
        "get" => (await connection.Cluster.GetAsync(Parse<GetParams>(parameters)), null),
        "apply" => Settled(await connection.Cluster.ApplyAsync(Parse<ApplyParams>(parameters))),
        "update" => Settled(await connection.Cluster.UpdateAsync(Parse<UpdateParams>(parameters))),
        "stop" => Settled(await connection.Cluster.StopAsync(Parse<StopParams>(parameters))),
        "retry" => Settled(await connection.Cluster.RetryAsync(Parse<RetryParams>(parameters))),
        "resubmit" => Settled(await connection.Cluster.ResubmitAsync(Parse<ResubmitParams>(parameters))),
        "delete" => Settled(await connection.Cluster.DeleteAsync(Parse<DeleteParams>(parameters))),
        _ => throw new InvalidOperationException(method)
    };

    private static (object?, Exception?) Settled<T>(NativeAttempt<T> attempt) where T : class
    {
        Check(attempt.Outcome is NativeAttemptOutcome.Acknowledged or NativeAttemptOutcome.Rejected, $"{attempt.Operation} was {attempt.Outcome}.");
        return (attempt.Response, attempt.Failure);
    }

    [Test]
    public async Task NativeGoldenTranscriptsRoundTripEveryMethodThroughTypedBindings()
    {
        foreach (var name in new[] { "admission-lifecycle.ndjson", "admission-errors.ndjson", "lifecycle-controls.ndjson", "lifecycle-delete.ndjson", "lifecycle-resubmit.ndjson" })
        {
            var lines = File.ReadLines(Fixture(name)).Where(line => line.Length > 0).Select(line => JsonNode.Parse(line)!).ToList();
            await using var peer = new Peer(async socket =>
            {
                for (var i = 0; i < lines.Count; i += 2)
                {
                    var request = await Read(socket);
                    Check(request.GetProperty("method").GetString() == lines[i]["method"]!.GetValue<string>());
                    // The typed request is exactly the native golden, including omission versus explicit null.
                    Check(JsonNode.DeepEquals(JsonNode.Parse(request.GetProperty("params").GetRawText()), lines[i]["params"]), $"{name} request {i / 2}");
                    var response = lines[i + 1].DeepClone();
                    response["id"] = JsonNode.Parse(request.GetProperty("id").GetRawText());
                    await Send(socket, response.ToJsonString());
                }
            });
            using var client = peer.Client(); await using var connection = await client.ConnectOecpAsync(peer.Session);
            for (var i = 0; i < lines.Count; i += 2)
            {
                var method = lines[i]["method"]!.GetValue<string>();
                var (result, failure) = await Invoke(connection, method, lines[i]["params"]!.ToJsonString());
                if (lines[i + 1]["error"] is { } error)
                    Check(failure is NativeOecpException { RpcError: { } rpc } && rpc.Code == error["code"]!.GetValue<long>() && rpc.Data?.Code == error["data"]!["code"]!.GetValue<string>());
                else
                    Check(JsonNode.DeepEquals(JsonNode.Parse(NativeJson.SerializeUtf8(result!)), lines[i + 1]["result"]), $"{name} result {i / 2}");
            }
            await peer.Finished;
        }
    }

    [Test]
    public void ParametersPreserveFencesDryRunAndOmissionVersusNull()
    {
        var apply = new ApplyParams { Graph = TypedGraph, Input = JsonDocument.Parse("null").RootElement, IfGeneration = 0, IdempotencyKey = new("key") };
        Check(Same("{\"graph\":" + Encoding.UTF8.GetString(Graph) + ",\"input\":null,\"ifGeneration\":0,\"idempotencyKey\":\"key\"}",
            NativeJson.SerializeUtf8(apply)), "Explicit null input or zero generation was lost.");
        Check(Same("{\"graph\":" + Encoding.UTF8.GetString(Graph) + ",\"dryRun\":true}",
            NativeJson.SerializeUtf8(new ApplyParams { Graph = TypedGraph, DryRun = true })), "Dry run was not an omission-only request.");
        Check(!Parse<ApplyParams>("{\"graph\":" + Encoding.UTF8.GetString(Graph) + "}").Input.HasValue);
        Reject<ApplyParams>("{\"graph\":" + Encoding.UTF8.GetString(Graph) + ",\"ifGeneration\":null}");
        Reject<ApplyParams>("{\"graph\":" + Encoding.UTF8.GetString(Graph) + ",\"idempotencyKey\":null}");

        var resubmit = new ResubmitParams { IfGeneration = 3, IfRunId = new("run-1"), IdempotencyKey = new("key") };
        Check(Same("""{"ifGeneration":3,"ifRunId":"run-1","idempotencyKey":"key"}""", NativeJson.SerializeUtf8(resubmit)));
        Check(Same("""{"ifGeneration":3,"ifRunId":"run-1","idempotencyKey":"key","replacementInput":null}""",
            NativeJson.SerializeUtf8(resubmit with { ReplacementInput = JsonDocument.Parse("null").RootElement })), "Explicit null replacement input was lost.");

        try { NativeJson.SerializeUtf8(new UpdateParams { IfGeneration = 1, IdempotencyKey = new("key") }); throw new InvalidOperationException("Empty update accepted."); }
        catch (JsonException) { }
        Reject<UpdateParams>("""{"ifGeneration":1,"idempotencyKey":"key","suspended":null}""");
        Reject<StopParams>("""{"mode":"pause","ifGeneration":1,"idempotencyKey":"key"}""");
        Check(Same("""{"ifGeneration":1,"idempotencyKey":"key"}""", NativeJson.SerializeUtf8(new DeleteParams { IfGeneration = 1, IdempotencyKey = new("key") })));

        var dryRun = Parse<ApplyResult>("""{"generation":null,"runId":null,"phase":"empty","deduped":false,"diff":{"added":["worker"],"removed":[],"changed":[]}}""");
        Check(dryRun is { Generation: null, RunId: null, Diff.Added: [{ Value: "worker" }] }, "Dry-run diff was lost.");
        var deleted = Parse<DeleteResult>("""{"deleted":true,"phase":"empty","deduped":true}""");
        Check(deleted is { Generation: null, RunId: null, AtCursor: null, Deduped: true });
        Reject<DeleteResult>("""{"deleted":true,"phase":"empty","deduped":true,"extra":1}""");
    }

    [Test]
    public void CompleteWatchEventAlgebraAndFaultsAreClosed()
    {
        var session = JsonNode.Parse(File.ReadAllText(Fixture("watch-session.json")))!.AsArray();
        var events = session.Select(item => Parse<EventNotification>(item!.ToJsonString())).ToList();
        Check(events[0].Event is PhaseWatchEvent { Admission: { RunId.Value: "run-1", SeedInput.ValueKind: JsonValueKind.Null }, Status.Phase: Phase.Running });
        Check(events[1].Event is NodeBeginWatchEvent { Node: { Node.Value: "worker", Attempt.Value: 1 } });
        Check(events[2].Event is NodeEndWatchEvent { Outcome: VerifiedOutcome });
        foreach (var (record, item) in events.Zip(session)) Check(JsonNode.DeepEquals(JsonNode.Parse(NativeJson.SerializeUtf8(record)), item));

        foreach (var fault in JsonNode.Parse(File.ReadAllText(Fixture("backend-fault.json")))!.AsArray())
        {
            var watch = Parse<WatchEvent>("{\"type\":\"fault\",\"fault\":" + fault!.ToJsonString() + "}");
            Check(watch is FaultWatchEvent { Fault.ExecutionRef: null } && JsonNode.DeepEquals(JsonNode.Parse(NativeJson.SerializeUtf8(((FaultWatchEvent)watch).Fault)), fault));
        }
        Check(Parse<WatchEvent>("""{"type":"fault","fault":{"eventId":"e","code":"unknown","consequence":"no_observable_effect","retry":"indeterminate","action":"none","severity":"info","summary":"s","source":[],"executionRef":"worker:1"}}""")
            is FaultWatchEvent { Fault.ExecutionRef: "worker:1" });
        Check(Parse<WatchEvent>("""{"type":"bookmark"}""") is BookmarkWatchEvent);
        var finished = Parse<WatchEvent>("""{"type":"finished","final_status":{"phase":"finished","observedGeneration":1,"currentRunId":"run-1","atCursor":"c"},"stop_mode":"force"}""");
        Check(finished is FinishedWatchEvent { StopMode: StopMode.Force, FinalStatus.Phase: Phase.Finished });
        Check(Parse<WatchEvent>("""{"type":"finished","final_status":{"phase":"empty"}}""") is FinishedWatchEvent { StopMode: null });
        Reject<WatchEvent>("""{"type":"bookmark","cursor":"c"}""");
        Reject<WatchEvent>("""{"type":"heartbeat"}""");
        Reject<WatchEvent>("""{"type":"node_begin","node":{"node":"worker","attempt":0},"input":null}""");
        Reject<WatchEvent>("""{"type":"fault","fault":{"eventId":"e","code":"unknown","consequence":"no_observable_effect","retry":"indeterminate","action":"none","severity":"info","summary":"s","source":[{"component":"a"},{"component":"a"},{"component":"a"},{"component":"a"},{"component":"a"},{"component":"a"},{"component":"a"},{"component":"a"},{"component":"a"}]}}""");
        Reject<EventNotification>("""{"subscriptionId":"s","runId":"r","event":{"type":"bookmark"}}""");
    }

    [Test]
    public void RetryFrontierReasonIsTypedWithoutLosingUnknownValues()
    {
        foreach (var (wire, reason) in new[] { ("exhausted", NoRetryableFrontierReason.Exhausted), ("success", NoRetryableFrontierReason.Success),
            ("active", NoRetryableFrontierReason.Active), ("consumed", NoRetryableFrontierReason.Consumed) })
            Check(Parse<DomainErrorData>($$$"""{"code":"NO_RETRYABLE_FRONTIER","details":{"reason":"{{{wire}}}"}}""").NoRetryableFrontierReason == reason);
        var future = Parse<DomainErrorData>("""{"code":"NO_RETRYABLE_FRONTIER","details":{"reason":"future"}}""");
        Check(future.NoRetryableFrontierReason is null && future.Details!.Value.GetProperty("reason").GetString() == "future");
        Check(Parse<DomainErrorData>("""{"code":"INVALID_PHASE","details":{"reason":"active"}}""").NoRetryableFrontierReason is null);
        Check(Parse<DomainErrorData>("""{"code":"NO_RETRYABLE_FRONTIER"}""").NoRetryableFrontierReason is null);
        Check(!Encoding.UTF8.GetString(NativeJson.SerializeUtf8(future)).Contains("noRetryableFrontierReason", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public async Task EachMutationClassifiesOnlyItsPreEffectRefusals()
    {
        // Every row: codes that prove no effect for that operation, then codes that stay Unknown for it.
        (string Method, Func<OecpConnection, Task<(NativeAttemptOutcome, Exception?)>> Call, (long Code, string Domain)[] Rejected, (long Code, string Domain)[] Unknown)[] table =
        [
            ("apply", async c => Evidence(await c.Cluster.ApplyAsync(new() { Graph = TypedGraph, DryRun = true })),
                [(-32000, "GRAPH_INVALID"), (-32000, "GENERATION_CONFLICT"), (-32000, "RUN_CONFLICT"), (-32000, "IDEMPOTENCY_REUSE"), (-32000, "INVALID_PHASE"), (-32000, "CANCELLED")],
                [(-32000, "NO_RETRYABLE_FRONTIER"), (-32602, "GRAPH_INVALID")]),
            ("update", async c => Evidence(await c.Cluster.UpdateAsync(new() { Suspended = true, IfGeneration = 1, IdempotencyKey = new("k") })),
                [(-32000, "GENERATION_CONFLICT"), (-32000, "IDEMPOTENCY_REUSE"), (-32000, "INVALID_PHASE")],
                [(-32000, "RUN_CONFLICT"), (-32000, "CANCELLED")]),
            ("stop", async c => Evidence(await c.Cluster.StopAsync(new() { Mode = StopMode.Drain, IfGeneration = 1, IdempotencyKey = new("k") })),
                [(-32000, "GENERATION_CONFLICT"), (-32000, "IDEMPOTENCY_REUSE"), (-32000, "INVALID_PHASE")],
                [(-32000, "NOT_FOUND"), (-32000, "CANCELLED")]),
            ("retry", async c => Evidence(await c.Cluster.RetryAsync(new() { IfGeneration = 1, IdempotencyKey = new("k") })),
                [(-32000, "GENERATION_CONFLICT"), (-32000, "IDEMPOTENCY_REUSE"), (-32000, "INVALID_PHASE"), (-32000, "NO_RETRYABLE_FRONTIER")],
                [(-32000, "RUN_CONFLICT")]),
            ("resubmit", async c => Evidence(await c.Cluster.ResubmitAsync(new() { IfGeneration = 1, IfRunId = new("run-1"), IdempotencyKey = new("k") })),
                [(-32000, "GENERATION_CONFLICT"), (-32000, "RUN_CONFLICT"), (-32000, "IDEMPOTENCY_REUSE"), (-32000, "INVALID_PHASE"), (-32000, "CANCELLED")],
                [(-32000, "NO_RETRYABLE_FRONTIER")]),
            ("delete", async c => Evidence(await c.Cluster.DeleteAsync(new() { IfGeneration = 1, IdempotencyKey = new("k") })),
                [(-32000, "GENERATION_CONFLICT"), (-32000, "RUN_CONFLICT"), (-32000, "IDEMPOTENCY_REUSE"), (-32000, "INVALID_PHASE"), (-32000, "CANCELLED")],
                [(-32000, "GONE")]),
            ("run/submit", async c => Evidence(await c.Runs.SubmitAsync(Submission())),
                [(-32602, "GRAPH_INVALID"), (-32000, "IDEMPOTENCY_REUSE"), (-32000, "RUN_CONFLICT"), (-32000, "INVALID_PHASE")],
                [(-32000, "GRAPH_INVALID"), (-32000, "source_checkout_unavailable")])
        ];
        (long, string)[] dispatch = [(-32601, "METHOD_NOT_FOUND"), (-32602, "SCHEMA_VIOLATION"), (-32600, "DUPLICATE_REQUEST_ID"), (-32000, "SERVER_BUSY")];
        (long, string)[] shared = [(-32603, "INTERNAL_ERROR"), (-32000, "SOURCE_UNAVAILABLE"), (-32000, "FUTURE_CODE")];
        foreach (var (method, call, rejected, unknown) in table)
        {
            var cases = dispatch.Concat(rejected).Select(c => (c, NativeAttemptOutcome.Rejected))
                .Concat(unknown.Concat(shared).Select(c => (c, NativeAttemptOutcome.Unknown))).ToList();
            await using var peer = new Peer(async socket =>
            {
                foreach (var ((code, domain), _) in cases)
                {
                    var request = await Read(socket);
                    Check(request.GetProperty("method").GetString() == method);
                    await Error(socket, request, code, domain);
                }
            });
            using var client = peer.Client(); await using var connection = await client.ConnectOecpAsync(peer.Session);
            foreach (var ((code, domain), expected) in cases)
            {
                var (outcome, failure) = await call(connection);
                Check(outcome == expected && failure is NativeOecpException { RpcError: { } rpc } && rpc.Code == code && rpc.Data!.Code == domain,
                    $"{method} {code}/{domain} was {outcome}.");
            }
            await peer.Finished;
        }
    }

    private static (NativeAttemptOutcome, Exception?) Evidence<T>(NativeAttempt<T> attempt) where T : class
    {
        Check(attempt.Response is null && attempt.Operation.Length > 0);
        return (attempt.Outcome, attempt.Failure);
    }

    private static RunSubmitParams Submission()
    {
        var prepared = Zeroshot.PreparedSubmission.ImportUtf8(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures/prepared.json")));
        return new() { RunId = prepared.RunId, Submission = prepared.Submission };
    }

    [Test]
    public async Task AcknowledgementsKeepExactIdentityAndForeignFencesAreUnknown()
    {
        var submission = Submission();
        const string Stopped = """{"generation":2,"runId":"run-1","phase":"finished","acceptedMode":"drain","effectiveMode":"force","operational":""" + Operational + ""","atCursor":"c","deduped":true}""";
        const string Resubmitted = """{"generation":2,"priorRunId":"run-1","runId":"run-2","phase":"running","operational":""" + Operational + ""","atCursor":"c","deduped":false}""";
        await using var peer = new Peer(async socket =>
        {
            var submit = await Read(socket);
            Check(JsonNode.DeepEquals(JsonNode.Parse(submit.GetProperty("params").GetRawText()), JsonNode.Parse(NativeJson.SerializeUtf8(submission))));
            await Reply(socket, submit, """{"runId":"earlier-run"}""");
            await Reply(socket, await Read(socket), Stopped);
            await Reply(socket, await Read(socket), Resubmitted);
            await Reply(socket, await Read(socket), Stopped.Replace("\"acceptedMode\":\"drain\"", "\"acceptedMode\":\"force\""));
        });
        using var client = peer.Client(); var connection = await client.ConnectOecpAsync(peer.Session);
        // Native deduplication can acknowledge another run; the binding keeps that identity rather than rejecting it.
        var submitted = await connection.Runs.SubmitAsync(submission);
        Check(submitted is { Outcome: NativeAttemptOutcome.Acknowledged, Operation: "run/submit", Response.RunId.Value: "earlier-run" });
        var stopped = await connection.Cluster.StopAsync(new() { Mode = StopMode.Drain, IfGeneration = 1, IdempotencyKey = new("stop") });
        Check(stopped.Response is { AcceptedMode: StopMode.Drain, EffectiveMode: StopMode.Force, Deduped: true }, "Accepted and effective modes were conflated.");
        var resubmitted = await connection.Cluster.ResubmitAsync(new() { IfGeneration = 1, IfRunId = new("run-1"), IdempotencyKey = new("r") });
        Check(resubmitted.Response is { PriorRunId.Value: "run-1", RunId.Value: "run-2" });
        var foreign = await connection.Cluster.StopAsync(new() { Mode = StopMode.Drain, IfGeneration = 1, IdempotencyKey = new("stop") });
        Check(foreign is { Outcome: NativeAttemptOutcome.Unknown, Response: null, Failure: NativeOecpException { Kind: NativeOecpFailureKind.Protocol } });
        await connection.DisposeAsync();
        await peer.Finished;
    }

    [Test]
    public async Task StopUsesReservedControlCapacityUnlikeOtherMutations()
    {
        var ordinary = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var peer = new Peer(async socket =>
        {
            var list = await Read(socket); ordinary.SetResult();
            var stop = await Read(socket); stopped.SetResult();
            Check(stop.GetProperty("method").GetString() == "stop");
            await Error(socket, stop, -32000, "INVALID_PHASE");
            await Reply(socket, list, "{\"runs\":[]}");
        });
        using var client = peer.Client(new() { MaxConcurrentRequests = 2, ReservedControlRequests = 1 });
        await using var connection = await client.ConnectOecpAsync(peer.Session);
        var pending = connection.Runs.ListAsync();
        await ordinary.Task;
        var delete = await connection.Cluster.DeleteAsync(new() { IfGeneration = 1, IdempotencyKey = new("d") });
        Check(delete is { Outcome: NativeAttemptOutcome.NotSent, Failure: NativeOecpException { Kind: NativeOecpFailureKind.Capacity } });
        var stop = connection.Cluster.StopAsync(new() { Mode = StopMode.Force, IfGeneration = 1, IdempotencyKey = new("s") });
        await stopped.Task;
        Check((await stop).Outcome == NativeAttemptOutcome.Rejected && (await pending).Runs.Length == 0);
        await peer.Finished;
    }

    [Test]
    public async Task WatchFollowsRequestedOrResolvedRunAndRejectsForeignRecords()
    {
        var session = JsonNode.Parse(File.ReadAllText(Fixture("watch-session.json")))!.AsArray().Select(item => item!.ToJsonString()).ToList();
        await using var peer = new Peer(async socket =>
        {
            var watch = await Read(socket);
            Check(watch.GetProperty("method").GetString() == "watch" && watch.GetProperty("params").GetRawText() == """{"runId":"run-1","fromCursor":"cursor-0"}""");
            // WatchResult is open and atCursor is the captured tail, not an echo of fromCursor.
            await Reply(socket, watch, """{"subscriptionId":"sub-1","runId":"run-1","atCursor":"cursor-3","future":true}""");
            foreach (var item in session) await Frame(socket, "event", item);
            await Frame(socket, "subscription/closed", """{"subscriptionId":"sub-1","reason":"done","lastDeliveredCursor":"cursor-3"}""");
            var parked = await Read(socket);
            Check(parked.GetProperty("params").GetRawText() == "{}");
            await Reply(socket, parked, """{"subscriptionId":"sub-2","runId":null,"atCursor":null}""");
            await Frame(socket, "event", session[2].Replace("sub-1", "sub-2"));
            await Frame(socket, "event", session[2].Replace("sub-1", "sub-2").Replace("run-1", "run-2"));
            Check((await Read(socket)).GetProperty("method").GetString() == "subscription/cancel");
            var foreign = await Read(socket);
            await Reply(socket, foreign, """{"subscriptionId":"sub-3","runId":"run-2","atCursor":null}""");
        });
        using var client = peer.Client(); var connection = await client.ConnectOecpAsync(peer.Session);
        await using (var watch = await connection.Cluster.WatchAsync(new() { RunId = new("run-1"), FromCursor = new("cursor-0") }))
        {
            var records = new List<EventNotification>();
            await foreach (var record in watch.ReadAllAsync()) records.Add(record);
            Check(records.Count == 3 && watch.Establishment.AtCursor!.Value == "cursor-3" && watch.LastDeliveredCursor!.Value == "cursor-3");
            Check((await watch.Completion).ServerClose!.LastDeliveredCursor!.Value == "cursor-3");
        }
        await using (var parked = await connection.Cluster.WatchAsync())
        {
            Check(parked.Establishment is { RunId: null, AtCursor: null });
            var delivered = 0;
            try { await foreach (var _ in parked.ReadAllAsync()) delivered++; throw new InvalidOperationException("Foreign run delivered."); }
            catch (NativeSubscriptionException error) { Check(error.Kind == NativeSubscriptionFailureKind.Protocol && delivered == 1); }
        }
        try { await connection.Cluster.WatchAsync(new() { RunId = new("run-1") }); throw new InvalidOperationException("Foreign establishment accepted."); }
        catch (NativeOecpException error) { Check(error.Kind == NativeOecpFailureKind.Protocol); }
        await connection.DisposeAsync();
        await peer.Finished;
    }

    [Test]
    public async Task ClusterLogsAndAgentAttachmentAreFutureOnlyCursorlessAndRunless()
    {
        var logs = JsonNode.Parse(File.ReadAllText(Fixture("logs-session.json")))!.AsArray().Select(item => item!.ToJsonString()).ToList();
        var attach = JsonNode.Parse(File.ReadAllText(Fixture("agent-attach-session.json")))!.AsArray().Select(item => item!.ToJsonString()).ToList();
        await using var peer = new Peer(async socket =>
        {
            var request = await Read(socket);
            Check(request.GetProperty("method").GetString() == "logs" && request.GetProperty("params").GetRawText() == "{}");
            await Reply(socket, request, """{"subscriptionId":"sub-1"}""");
            foreach (var item in logs) await Frame(socket, "event", item);
            await Frame(socket, "subscription/closed", """{"subscriptionId":"sub-1","reason":"done"}""");
            request = await Read(socket);
            Check(request.GetProperty("method").GetString() == "agent/attach" && request.GetProperty("params").GetRawText() == """{"execution":"execution-1"}""");
            await Reply(socket, request, """{"subscriptionId":"sub-1"}""");
            foreach (var item in attach) await Frame(socket, "event", item);
            // A cluster attachment close carrying a cursor violates its cursorless contract.
            await Frame(socket, "subscription/closed", """{"subscriptionId":"sub-1","reason":"done","lastDeliveredCursor":"c"}""");
        });
        using var client = peer.Client(); var connection = await client.ConnectOecpAsync(peer.Session);
        await using (var subscription = await connection.Cluster.LogsAsync())
        {
            var records = new List<LogEventNotification>();
            await foreach (var record in subscription.ReadAllAsync()) records.Add(record);
            Check(records.Select(r => r.Record.Level).SequenceEqual([LogLevel.Info, LogLevel.Warn, LogLevel.Error]) && subscription.LastDeliveredCursor is null);
            Check(await subscription.Completion is { Origin: NativeSubscriptionOrigin.ServerClosed, ServerClose.LastDeliveredCursor: null });
        }
        await using (var subscription = await connection.Cluster.AttachAgentAsync(new() { Execution = new("execution-1") }))
        {
            var events = new List<AgentAttachEvent>();
            try { await foreach (var record in subscription.ReadAllAsync()) events.Add(record.Event); throw new InvalidOperationException("Cursor close accepted."); }
            catch (NativeSubscriptionException error) { Check(error.Kind == NativeSubscriptionFailureKind.Protocol); }
            Check(events is [WorkingAgentAttachEvent, OutputAgentAttachEvent, OutputAgentAttachEvent, SettledAgentAttachEvent] && subscription.LastDeliveredCursor is null);
            Check(await subscription.Completion is { Origin: NativeSubscriptionOrigin.UnexpectedDisconnect });
        }
        Check(await connection.Completion is { Kind: NativeOecpFailureKind.Protocol });
        await peer.Finished;
    }
}
