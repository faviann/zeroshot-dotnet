using System.Net.WebSockets;
using System.Text.Json;
using TUnit.Core;
using Zeroshot.Native;
using Zeroshot.Native.Contracts;
using static Zeroshot.Client.Tests.OecpTests;

namespace Zeroshot.Client.Tests;

public sealed class ForceTests
{
    private const string Identity = """{"runId":"run-1","title":"test","source":{"repository":"acme/project","branch":"main","revision":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"},"size":"small","atCursor":"cursor-2","status":""";
    private const string Stopping = Identity + """{"phase":"stopping","activeExecutions":[{"execution":"worker:1","node":"worker"}]}}""";
    private const string Finished = Identity + """{"phase":"finished","terminalResult":{"status":"failed","reason":"force_stopped"}}}""";
    private static readonly RunId Run = new("run-1");
    private static void Check(bool value, string message = "Force assertion failed.")
    { if (!value) throw new InvalidOperationException(message); }

    // Reads every remaining frame until the client closes, so callers can prove there was no second force.
    private static async Task<List<string>> Methods(WebSocket socket, JsonElement first)
    {
        var methods = new List<string> { first.GetProperty("method").GetString()! };
        try { while (true) methods.Add((await Read(socket)).GetProperty("method").GetString()!); }
        catch (Exception error) when (error is InvalidOperationException or WebSocketException or IOException) { return methods; }
    }

    [Test]
    public async Task SendsExactRunAndPreservesStoppingSeparatelyFromTerminalAcknowledgement()
    {
        await using var peer = new Peer(async socket =>
        {
            foreach (var status in new[] { Stopping, Finished })
            {
                var request = await Read(socket);
                Check(request.GetProperty("method").GetString() == "run/force" && request.GetProperty("params").GetRawText() == """{"runId":"run-1"}""");
                await Reply(socket, request, status);
            }
        });
        using var client = peer.Client(); await using var connection = await client.ConnectOecpAsync(peer.Session);
        var stopping = await connection.Runs.ForceAsync(Run);
        Check(stopping is { Outcome: NativeAttemptOutcome.Acknowledged, Failure: null, Operation: "run/force" } && stopping.Origin == peer.Origin);
        Check(stopping.CorrelationId != Guid.Empty && stopping.Response!.Status is StoppingRunStatus { ActiveExecutions.Length: 1 });
        var finished = await connection.Runs.ForceAsync(Run);
        Check(finished.Response!.Status is FinishedRunStatus { TerminalResult: FailedTerminalResult { Reason.Value: "force_stopped" } });
        await peer.Finished;
    }

    [Test]
    public async Task OnlyPinnedPreEffectRpcErrorsAreRejections()
    {
        (long Code, string? Domain, NativeAttemptOutcome Outcome)[] cases =
        [
            (-32601, null, NativeAttemptOutcome.Rejected),
            (-32602, "SCHEMA_VIOLATION", NativeAttemptOutcome.Rejected),
            (-32600, "DUPLICATE_REQUEST_ID", NativeAttemptOutcome.Rejected),
            (-32000, "SERVER_BUSY", NativeAttemptOutcome.Rejected),
            (-32000, "NOT_FOUND", NativeAttemptOutcome.Rejected),
            (-32602, null, NativeAttemptOutcome.Unknown),
            (-32603, null, NativeAttemptOutcome.Unknown),
            (-32000, "SOURCE_UNAVAILABLE", NativeAttemptOutcome.Unknown),
            (-32000, "FUTURE_CODE", NativeAttemptOutcome.Unknown),
            (-32001, "NOT_FOUND", NativeAttemptOutcome.Unknown)
        ];
        var forces = 0;
        await using var peer = new Peer(async socket =>
        {
            foreach (var (code, domain, _) in cases)
            {
                var request = await Read(socket);
                if (request.GetProperty("method").GetString() == "run/force") forces++;
                object error = domain is null ? new { code, message = "refused" } : new { code, message = "refused", data = new { code = domain } };
                await Send(socket, JsonSerializer.Serialize(new { jsonrpc = "2.0", id = request.GetProperty("id"), error }));
            }
        });
        using var client = peer.Client(); await using var connection = await client.ConnectOecpAsync(peer.Session);
        foreach (var (code, domain, outcome) in cases)
        {
            var attempt = await connection.Runs.ForceAsync(Run);
            Check(attempt.Outcome == outcome && attempt.Response is null, $"{code}/{domain} was {attempt.Outcome}.");
            Check(attempt.Failure is NativeOecpException { RpcError: { } rpc, Dispatch.ResponseReceived: true } && rpc.Code == code && rpc.Data?.Code == domain);
        }
        await peer.Finished;
        Check(forces == cases.Length);
    }

    [Test]
    public async Task FailuresAfterPossibleDispatchRemainUnknownWithOneSend()
    {
        foreach (var fault in new[] { "lost", "disconnect", "foreign", "malformed", "oversized", "cancelled" })
        {
            using var cancel = new CancellationTokenSource();
            var methods = new TaskCompletionSource<List<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var peer = new Peer(async socket =>
            {
                var request = await Read(socket);
                received.SetResult();
                switch (fault)
                {
                    case "disconnect": socket.Abort(); break;
                    case "foreign": await Reply(socket, request, Stopping.Replace("run-1", "run-2")); break;
                    case "malformed": await Reply(socket, request, Stopping.Replace("\"stopping\"", "\"stopped\"")); break;
                    case "oversized": await Reply(socket, request, Stopping.Replace("\"test\"", "\"" + new string('t', 256) + "\"")); break;
                    case "cancelled": cancel.Cancel(); break;
                }
                methods.SetResult(fault == "disconnect" ? ["run/force"] : await Methods(socket, request));
            });
            // Deadlines run on a manual clock: connecting never races them, and the lost reply's deadline expires
            // only once the peer holds the request.
            var time = new ManualTime();
            using var client = NativeClient.ForHttp(new() { Origin = peer.Origin, Transport = new() { MaxOecpMessageBytes = Stopping.Length + 64 }, Time = time });
            var connection = await client.ConnectOecpAsync(peer.Session);
            var force = connection.Runs.ForceAsync(Run, cancellationToken: cancel.Token);
            if (fault == "lost")
            {
                await received.Task;
                time.Advance(new TransportOptions().RequestTimeout);
            }
            var attempt = await force;
            await connection.DisposeAsync();
            Check(attempt is { Outcome: NativeAttemptOutcome.Unknown, Response: null, Failure: not null }, $"{fault} was {attempt.Outcome}.");
            // A reply-driven failure follows the completed send. Cancellation and the deadline end the caller's wait as
            // soon as the peer holds the frame, which can precede the client's record of send completion; a started
            // send is what makes their effect unknown.
            Check(fault switch
            {
                "cancelled" => attempt.Failure is OecpOperationCanceledException { Dispatch.SendStarted: true },
                "lost" => attempt.Failure is NativeOecpException { Kind: NativeOecpFailureKind.Deadline, Dispatch.SendStarted: true },
                _ => attempt.Failure is NativeOecpException { Dispatch.SendCompleted: true },
            }, fault);
            Check((await methods.Task).Count(method => method == "run/force") == 1, fault);
            await peer.Finished;
        }
    }

    [Test]
    public async Task CancellationBeforeSendIsNotSent()
    {
        await using var peer = new Peer(async socket => await Reply(socket, await Read(socket), "{\"runs\":[]}"));
        using var client = peer.Client(); await using var connection = await client.ConnectOecpAsync(peer.Session);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var attempt = await connection.Runs.ForceAsync(Run, cancellationToken: cancel.Token);
        Check(attempt is { Outcome: NativeAttemptOutcome.NotSent, Failure: OecpOperationCanceledException { Dispatch.SendStarted: false } });
        Check((await connection.Runs.ListAsync()).Runs.Length == 0); // The peer's first frame was the list, not a force.
        await peer.Finished;
    }

    [Test]
    public async Task ValidatedAcknowledgementSurvivesCancellationThatLandsBeforeTheCallCompletes()
    {
        await using var peer = new Peer(async socket => await Reply(socket, await Read(socket), Stopping));
        using var client = peer.Client(); await using var connection = await client.ConnectOecpAsync(peer.Session);
        using var cancel = new CancellationTokenSource();
        // Deterministically places caller cancellation after validation and capture, before completion.
        var attempt = await connection.Runs.ForceAsync(Run, null, cancel.Cancel, cancel.Token);
        Check(cancel.IsCancellationRequested && attempt is { Outcome: NativeAttemptOutcome.Acknowledged, Failure: null }, $"Outcome {attempt.Outcome}.");
        Check(attempt.Response!.Status is StoppingRunStatus);
        await peer.Finished;
    }

    [Test]
    public async Task ForceUsesReservedControlCapacityUnderOrdinaryTraffic()
    {
        var ordinary = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var forced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var peer = new Peer(async socket =>
        {
            var list = await Read(socket); ordinary.SetResult();
            var force = await Read(socket); forced.SetResult();
            Check(force.GetProperty("method").GetString() == "run/force");
            await release.Task;
            await Reply(socket, force, Finished);
            await Reply(socket, list, "{\"runs\":[]}");
        });
        using var client = peer.Client(new() { MaxConcurrentRequests = 2, ReservedControlRequests = 1 });
        await using var connection = await client.ConnectOecpAsync(peer.Session);
        var pending = connection.Runs.ListAsync();
        await ordinary.Task;
        try { await connection.Runs.ListAsync(); throw new InvalidOperationException("Ordinary capacity was not full."); }
        catch (NativeOecpException error) { Check(error.Kind == NativeOecpFailureKind.Capacity); }
        var admitted = connection.Runs.ForceAsync(Run);
        await forced.Task;
        var exhausted = await connection.Runs.ForceAsync(Run);
        Check(exhausted is { Outcome: NativeAttemptOutcome.NotSent, Failure: NativeOecpException { Kind: NativeOecpFailureKind.Capacity } });
        release.SetResult();
        Check((await admitted).Outcome == NativeAttemptOutcome.Acknowledged && (await pending).Runs.Length == 0);
        await peer.Finished;
    }
}
