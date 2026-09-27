using System.Collections.Immutable;
using System.Net.WebSockets;
using System.Text.Json;
using TUnit.Core;
using Zeroshot.Native;
using Zeroshot.Native.Contracts;
using static Zeroshot.Client.Tests.OecpTests;

namespace Zeroshot.Client.Tests;

public sealed class RecoveryTests
{
    private static readonly RunId Run = new("run-1");
    private static readonly RunId Successor = new("run-2");
    private const string Resumed = """{"runId":"run-2","resumedFrom":"run-1"}""";
    private static string Checkpoint(string id, int sequence) =>
        $$"""{"checkpointId":"{{id}}","sequence":{{sequence}},"node":"worker","mapIndices":[0],"loopIterations":[],"createdAt":1700000000000}""";
    private static void Check(bool value, string message = "Recovery assertion failed.")
    { if (!value) throw new InvalidOperationException(message); }

    private static async Task<JsonElement> Expect(WebSocket socket, string method, string parameters)
    {
        var request = await Read(socket);
        Check(request.GetProperty("method").GetString() == method && request.GetProperty("params").GetRawText() == parameters,
            $"Unexpected {request.GetProperty("method")} {request.GetProperty("params").GetRawText()}");
        return request;
    }

    private static Task Error(WebSocket socket, JsonElement request, long code, string? domain)
    {
        object error = domain is null ? new { code, message = "refused" } : new { code, message = "refused", data = new { code = domain, details = new { runId = "run-9" } } };
        return Send(socket, JsonSerializer.Serialize(new { jsonrpc = "2.0", id = request.GetProperty("id"), error }));
    }

    [Test]
    public async Task CheckpointPagesUseNativeDefaultsAndOpaqueExclusiveCursor()
    {
        await using var peer = new Peer(async socket =>
        {
            await Reply(socket, await Expect(socket, "run/checkpoints", """{"runId":"run-1"}"""),
                $$"""{"runId":"run-1","checkpoints":[{{Checkpoint("opaque/a", 1)}},{{Checkpoint("opaque/b", 5)}}],"nextAfter":"opaque/b"}""");
            await Reply(socket, await Expect(socket, "run/checkpoints", """{"runId":"run-1","after":"opaque/b","limit":1}"""),
                $$"""{"runId":"run-1","checkpoints":[{{Checkpoint("opaque/c", 9)}}]}""");
        });
        using var client = peer.Client(); await using var connection = await client.ConnectOecpAsync(peer.Session);
        var first = await connection.Runs.CheckpointsAsync(new() { RunId = Run });
        Check(first.Checkpoints is [{ CheckpointId.Value: "opaque/a", Sequence.Value: 1, Node.Value: "worker", MapIndices: [{ Value: 0 }], LoopIterations.IsEmpty: true, CreatedAt.Value: 1700000000000 }, _]);
        Check(first.NextAfter?.Value == "opaque/b");
        var next = await connection.Runs.CheckpointsAsync(new() { RunId = Run, After = first.NextAfter, Limit = 1 });
        Check(next is { Checkpoints: [{ CheckpointId.Value: "opaque/c" }], NextAfter: null });
        await peer.Finished;
    }

    [Test]
    public async Task CheckpointLimitsOutsideTheNativeRangeAreNotSent()
    {
        await using var peer = new Peer(async socket =>
            await Reply(socket, await Expect(socket, "run/checkpoints", """{"runId":"run-1","limit":100}"""), """{"runId":"run-1","checkpoints":[]}"""));
        using var client = peer.Client(); await using var connection = await client.ConnectOecpAsync(peer.Session);
        foreach (var limit in new uint[] { 0, 101 })
        {
            try { await connection.Runs.CheckpointsAsync(new() { RunId = Run, Limit = limit }); throw new InvalidOperationException($"Limit {limit} was sent."); }
            catch (JsonException) { }
        }
        Check((await connection.Runs.CheckpointsAsync(new() { RunId = Run, Limit = 100 })).Checkpoints.IsEmpty);
        await peer.Finished;
    }

    [Test]
    public async Task CheckpointPagesOutsideTheNativeContractAreProtocolFailures()
    {
        (uint? Limit, string Page)[] cases =
        [
            (null, """{"runId":"run-2","checkpoints":[]}"""),
            (1, $$"""{"runId":"run-1","checkpoints":[{{Checkpoint("a", 1)}},{{Checkpoint("b", 2)}}]}"""),
            (null, $$"""{"runId":"run-1","checkpoints":[{{Checkpoint("a", 1)}},{{Checkpoint("b", 2)}}],"nextAfter":"a"}"""),
            (null, """{"runId":"run-1","checkpoints":[],"nextAfter":"a"}""")
        ];
        foreach (var (limit, page) in cases)
        {
            await using var peer = new Peer(async socket => await Reply(socket, await Read(socket), page));
            using var client = peer.Client(); await using var connection = await client.ConnectOecpAsync(peer.Session);
            try { await connection.Runs.CheckpointsAsync(new() { RunId = Run, Limit = limit }); throw new InvalidOperationException($"Accepted {page}"); }
            catch (NativeOecpException error) { Check(error.Kind == NativeOecpFailureKind.Protocol, page); }
            await peer.Finished;
        }
    }

    [Test]
    public async Task UnsupportedCheckpointCapabilityIsAVisibleNativeRefusal()
    {
        await using var peer = new Peer(async socket => await Error(socket, await Read(socket), -32000, "INVALID_PHASE"));
        using var client = peer.Client(); await using var connection = await client.ConnectOecpAsync(peer.Session);
        try { await connection.Runs.CheckpointsAsync(new() { RunId = Run }); throw new InvalidOperationException(); }
        catch (NativeOecpException error) { Check(error is { Kind: NativeOecpFailureKind.RpcError, RpcError.Data.Code: "INVALID_PHASE" }); }
        await peer.Finished;
    }

    [Test]
    public async Task ResumeSendsTheSelectionAndSeparateFreshCredentials()
    {
        const string resolver = """{"endpoint":"https://resolver.test/cb","bearerToken":"resolver-secret","keys":["gateway"]}""";
        await using var peer = new Peer(async socket =>
        {
            await Reply(socket, await Expect(socket, "run/resume", """{"runId":"run-1","successorRunId":"run-2"}"""), Resumed);
            await Reply(socket, await Expect(socket, "run/resume", """{"runId":"run-1","successorRunId":"run-2","from":{"kind":"restart"},"githubToken":"gh-secret"}"""), Resumed);
            await Reply(socket, await Expect(socket, "run/resume",
                $$$"""{"runId":"run-1","successorRunId":"run-2","from":{"kind":"checkpoint","checkpointId":"opaque/b"},"connections":{"gateway":{"API_KEY":"value-secret"}},"connectionResolver":{{{resolver}}}}"""), Resumed);
        });
        using var client = peer.Client(); await using var connection = await client.ConnectOecpAsync(peer.Session);
        var latest = await connection.Runs.ResumeAsync(Run, Successor);
        var restart = await connection.Runs.ResumeAsync(Run, Successor, new RestartResumeFrom(),
            new() { Connections = ImmutableDictionary<string, ImmutableDictionary<string, string>>.Empty, GithubToken = "gh-secret" });
        var checkpoint = await connection.Runs.ResumeAsync(Run, Successor, new CheckpointResumeFrom { CheckpointId = new("opaque/b") }, new()
        {
            Connections = ImmutableDictionary<string, ImmutableDictionary<string, string>>.Empty.Add("gateway", ImmutableDictionary<string, string>.Empty.Add("API_KEY", "value-secret")),
            ConnectionResolver = new() { Endpoint = "https://resolver.test/cb", BearerToken = "resolver-secret", Keys = [new("gateway")] }
        });
        foreach (var attempt in new[] { latest, restart, checkpoint })
            Check(attempt is { Outcome: NativeAttemptOutcome.Acknowledged, Operation: "run/resume", Failure: null, Response: { RunId.Value: "run-2", ResumedFrom.Value: "run-1" } });
        await peer.Finished;
    }

    [Test]
    public async Task OnlyPreEffectRecoveryRefusalsAreRejected()
    {
        (long Code, string? Domain, NativeAttemptOutcome Resume, NativeAttemptOutcome? Discard)[] cases =
        [
            (-32000, "INVALID_PHASE", NativeAttemptOutcome.Rejected, NativeAttemptOutcome.Rejected),
            (-32602, "SCHEMA_VIOLATION", NativeAttemptOutcome.Rejected, NativeAttemptOutcome.Rejected),
            (-32000, "NOT_FOUND", NativeAttemptOutcome.Rejected, NativeAttemptOutcome.Rejected),
            (-32000, "IDEMPOTENCY_REUSE", NativeAttemptOutcome.Rejected, null), // Resume only.
            (-32601, null, NativeAttemptOutcome.Rejected, NativeAttemptOutcome.Rejected),
            (-32600, "DUPLICATE_REQUEST_ID", NativeAttemptOutcome.Rejected, NativeAttemptOutcome.Rejected),
            (-32000, "SERVER_BUSY", NativeAttemptOutcome.Rejected, NativeAttemptOutcome.Rejected),
            // Native answers a non-recoverable or already-resumed source, a successor ID conflict and failed cleanup this way.
            (-32603, "INTERNAL_ERROR", NativeAttemptOutcome.Unknown, NativeAttemptOutcome.Unknown),
            (-32000, "FUTURE_CODE", NativeAttemptOutcome.Unknown, NativeAttemptOutcome.Unknown)
        ];
        await using var peer = new Peer(async socket =>
        {
            foreach (var (code, domain, _, discard) in cases)
                for (var i = discard is null ? 1 : 0; i < 2; i++) await Error(socket, await Read(socket), code, domain);
        });
        using var client = peer.Client(); await using var connection = await client.ConnectOecpAsync(peer.Session);
        foreach (var (code, domain, resume, discard) in cases)
        {
            var resumed = await connection.Runs.ResumeAsync(Run, Successor);
            var discarded = discard is null ? null : await connection.Runs.DiscardWorkspaceAsync(Run);
            Check(resumed.Outcome == resume && discarded?.Outcome == discard, $"{code}/{domain} was {resumed.Outcome}/{discarded?.Outcome}.");
            foreach (var failure in new[] { resumed.Failure, discarded?.Failure }.Take(discarded is null ? 1 : 2))
                Check(failure is NativeOecpException { RpcError: { } rpc } && rpc.Code == code && rpc.Data?.Code == domain);
            if (domain == "IDEMPOTENCY_REUSE")
                Check(resumed.Failure is NativeOecpException { RpcError.Data.Details: { } details } && details.GetProperty("runId").GetString() == "run-9",
                    "Conflict details were lost.");
        }
        await peer.Finished;
    }

    [Test]
    public async Task ForeignAcknowledgementsAndLostRepliesRemainUnknownWithOneSend()
    {
        foreach (var fault in new[] { "successor", "source", "discard", "lost", "disconnect" })
        {
            var sends = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var peer = new Peer(async socket =>
            {
                var request = await Read(socket);
                switch (fault)
                {
                    case "successor": await Reply(socket, request, """{"runId":"run-3","resumedFrom":"run-1"}"""); break;
                    case "source": await Reply(socket, request, """{"runId":"run-2","resumedFrom":"run-3"}"""); break;
                    case "discard": await Reply(socket, request, """{"runId":"run-2","discarded":true}"""); break;
                    case "disconnect": socket.Abort(); sends.SetResult(1); return;
                }
                var count = 1;
                try { while (true) if ((await Read(socket)).GetProperty("method").GetString() == request.GetProperty("method").GetString()) count++; }
                catch (Exception error) when (error is InvalidOperationException or WebSocketException or IOException) { sends.SetResult(count); }
            });
            using var client = peer.Client(new() { RequestTimeout = TimeSpan.FromSeconds(1) });
            var connection = await client.ConnectOecpAsync(peer.Session);
            var attempt = fault == "discard"
                ? (object)await connection.Runs.DiscardWorkspaceAsync(Run)
                : await connection.Runs.ResumeAsync(Run, Successor);
            await connection.DisposeAsync();
            var (outcome, response, failure) = attempt switch
            {
                NativeAttempt<RunResumeResult> resume => (resume.Outcome, (object?)resume.Response, resume.Failure),
                NativeAttempt<RunDiscardWorkspaceResult> discard => (discard.Outcome, discard.Response, discard.Failure),
                _ => throw new InvalidOperationException()
            };
            Check(outcome == NativeAttemptOutcome.Unknown && response is null && failure is NativeOecpException { Dispatch.SendCompleted: true }, $"{fault} was {outcome}.");
            Check(await sends.Task == 1, fault);
            await peer.Finished;
        }
    }
}
