using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TUnit.Core;
using Zeroshot.Native;
using Zeroshot.Native.Contracts;
using static Zeroshot.Client.Tests.OecpTests;

namespace Zeroshot.Client.Tests;

public sealed class AttachmentTests
{
    private const string Established = """{"subscriptionId":"attach","runId":"run-1","execution":"worker:1"}""";
    private const string Output = """{"type":"output","text":"visible output SECRET"}""";
    private static RunAttachParams Parameters => new() { RunId = new("run-1"), Execution = new("worker:1") };
    private static string Record(string progress) => Established[..^1] + ",\"event\":" + progress + "}";
    private static string Frame(string progress) => "{\"jsonrpc\":\"2.0\",\"method\":\"event\",\"params\":" + Record(progress) + "}";

    [Test]
    public void CompleteAttachmentContractIsClosedCursorlessAndUtf8Bounded()
    {
        foreach (var progress in new[] { "{\"type\":\"working\"}", Output, "{\"type\":\"settled\"}" })
        {
            var record = NativeJson.DeserializeUtf8<RunAttachEventNotification>(Encoding.UTF8.GetBytes(Record(progress)));
            Check(record.RunId == Parameters.RunId && record.Execution == Parameters.Execution);
            Check(NativeJson.DeserializeUtf8<RunAttachEventNotification>(NativeJson.SerializeUtf8(record)) == record);
            Check(!record.ToString().Contains("SECRET") && !record.Event.ToString().Contains("SECRET"));
        }
        _ = new BoundedAssistantOutput("");
        _ = new BoundedAssistantOutput(new string('é', 8192));
        foreach (var invalid in new[]
        {
            Output.Replace("visible output SECRET", new string('é', 8193)),
            Output.Replace("visible output SECRET", "bad\\noutput"),
            "{\"type\":\"output\"}", "{\"type\":\"output\",\"text\":null}",
            "{\"type\":\"working\",\"text\":\"hidden\"}", "{\"type\":\"settled\",\"result\":{}}",
            "{\"type\":\"future\"}", "{\"type\":\"settled\",\"type\":\"working\"}"
        })
        {
            try { NativeJson.DeserializeUtf8<RunAttachEventNotification>(Encoding.UTF8.GetBytes(Record(invalid))); }
            catch (JsonException) { continue; }
            throw new InvalidOperationException("Malformed attachment event accepted.");
        }
        foreach (var invalid in new[] { "{\"runId\":\"run-1\"}", "{\"runId\":\"run-1\",\"execution\":\"worker:1\",\"fromCursor\":\"x\"}" })
        {
            try { NativeJson.DeserializeUtf8<RunAttachParams>(Encoding.UTF8.GetBytes(invalid)); }
            catch (JsonException) { continue; }
            throw new InvalidOperationException("Malformed attachment parameters accepted.");
        }
    }

    [Test]
    public async Task ImmediateWorkingOutputAndSettlementRetainExactExecutionWithoutInventingRunResult()
    {
        var settledReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sendClose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var peer = new Peer(async socket =>
        {
            var request = await Read(socket);
            Check(request.GetProperty("method").GetString() == "run/attach");
            Check(request.GetProperty("params").GetRawText() == "{\"runId\":\"run-1\",\"execution\":\"worker:1\"}");
            await Reply(socket, request, Established);
            await Send(socket, Frame("{\"type\":\"working\"}"));
            await Send(socket, Frame(Output));
            await Send(socket, Frame("{\"type\":\"settled\"}"));
            await sendClose.Task;
            await Send(socket, """{"jsonrpc":"2.0","method":"subscription/closed","params":{"subscriptionId":"attach","reason":"done"}}""");
        });
        using var client = peer.Client(); await using var connection = await client.ConnectOecpAsync(peer.Session);
        await using var attachment = await connection.Runs.AttachAsync(Parameters);
        var records = new List<RunAttachEventNotification>();
        var reading = Task.Run(async () =>
        {
            await foreach (var record in attachment.ReadAllAsync())
            {
                records.Add(record);
                if (record.Event is SettledAgentAttachEvent) settledReceived.SetResult();
            }
        });
        await settledReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!attachment.Completion.IsCompleted && attachment.LastDeliveredCursor is null);
        sendClose.SetResult(); await reading;
        Check(records is [{ Event: WorkingAgentAttachEvent }, { Event: OutputAgentAttachEvent { Text.Value: "visible output SECRET" } }, { Event: SettledAgentAttachEvent }]);
        var completion = await attachment.Completion;
        Check(completion is { Origin: NativeSubscriptionOrigin.ServerClosed, ServerClose.Reason: SubscriptionCloseReason.Done, ServerClose.LastDeliveredCursor: null });
        Check(attachment.Establishment.Execution == Parameters.Execution && attachment.LastDeliveredCursor is null);
        await peer.Finished;
    }

    [Test]
    public async Task ForeignIdentitiesAndMalformedOutputEndOnlyTheirAttachmentAfterValidatedDataDrains()
    {
        foreach (var invalid in new[]
        {
            Frame(Output).Replace("worker:1", "foreign:2"), Frame(Output).Replace("run-1", "run-2"),
            Frame(Output.Replace("visible output SECRET", new string('é', 8193))),
            Frame("{\"type\":\"output\",\"text\":null}")
        })
        {
            await using var peer = new Peer(async socket =>
            {
                await Reply(socket, await Read(socket), Established);
                await Send(socket, Frame(Output).Replace("\"attach\"", "\"foreign\""));
                await Send(socket, Frame(Output)); await Send(socket, invalid);
                Check((await Read(socket)).GetProperty("method").GetString() == "subscription/cancel");
                await Reply(socket, await Read(socket), "{\"runs\":[]}");
            });
            using var client = peer.Client(); await using var connection = await client.ConnectOecpAsync(peer.Session);
            await using var attachment = await connection.Runs.AttachAsync(Parameters);
            var count = 0;
            var failure = await Failure(async () => { await foreach (var _ in attachment.ReadAllAsync()) count++; }, NativeSubscriptionFailureKind.Protocol);
            Check(count == 1 && attachment.LastDeliveredCursor is null && !failure.ToString().Contains("SECRET"));
            Check((await attachment.Completion).Origin == NativeSubscriptionOrigin.LocalFailure);
            Check((await connection.Runs.ListAsync()).Runs.Length == 0);
            await peer.Finished;
        }
    }

    [Test]
    public async Task EstablishmentRequiresBothIdentitiesAndRetainsNativeNotFoundVersusGone()
    {
        foreach (var result in new[] { Established.Replace("run-1", "foreign"), Established.Replace("worker:1", "foreign") })
        {
            await using var peer = new Peer(async socket => await Reply(socket, await Read(socket), result));
            using var client = peer.Client(); await using var connection = await client.ConnectOecpAsync(peer.Session);
            try { await connection.Runs.AttachAsync(Parameters); throw new InvalidOperationException("Foreign attachment accepted."); }
            catch (NativeOecpException error) { Check(error.Kind == NativeOecpFailureKind.Protocol); }
            await peer.Finished;
        }
        foreach (var code in new[] { "NOT_FOUND", "GONE" })
        {
            await using var peer = new Peer(async socket =>
            {
                var request = await Read(socket);
                await Send(socket, JsonSerializer.Serialize(new { jsonrpc = "2.0", id = request.GetProperty("id"), error = new { code = -32000, message = "refused", data = new { code } } }));
            });
            using var client = peer.Client(); await using var connection = await client.ConnectOecpAsync(peer.Session);
            try { await connection.Runs.AttachAsync(Parameters); throw new InvalidOperationException("Refusal ignored."); }
            catch (NativeOecpException error)
            { Check(error.Kind == NativeOecpFailureKind.RpcError && error.RpcError!.Data!.Code == code && error.Operation == "run/attach"); }
            await peer.Finished;
        }
    }

    [Test]
    public async Task DisposalAndCancellationDetachWithoutStoppingExecutionAndReleaseAdmission()
    {
        foreach (var cancel in new[] { false, true })
        {
            await using var peer = new Peer(async socket =>
            {
                await Reply(socket, await Read(socket), Established);
                await Send(socket, Frame(Output));
                var detached = await Read(socket);
                Check(detached.GetProperty("method").GetString() == "subscription/cancel" &&
                    detached.GetProperty("params").GetProperty("subscriptionId").GetString() == "attach");
                await Reply(socket, await Read(socket), Established.Replace("attach", "next"));
                Check((await Read(socket)).GetProperty("method").GetString() == "subscription/cancel");
            });
            using var client = peer.Client(new() { MaxConcurrentSubscriptions = 1 });
            await using var connection = await client.ConnectOecpAsync(peer.Session);
            using var cancellation = new CancellationTokenSource();
            await using var attachment = await connection.Runs.AttachAsync(Parameters, cancellation.Token);
            await using var reader = attachment.ReadAllAsync().GetAsyncEnumerator();
            Check(await reader.MoveNextAsync());
            await Failure(async () => { await connection.Runs.AttachAsync(Parameters); }, NativeSubscriptionFailureKind.Admission);
            if (cancel) cancellation.Cancel(); else await reader.DisposeAsync();
            Check((await attachment.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Origin ==
                (cancel ? NativeSubscriptionOrigin.Cancelled : NativeSubscriptionOrigin.Disposed));
            await using var next = await connection.Runs.AttachAsync(Parameters);
            await next.DisposeAsync();
            await peer.Finished;
        }
    }

    [Test]
    public async Task DisconnectDrainsOutputAndPreservesEstablishmentWithoutSettlement()
    {
        await using var peer = new Peer(async socket =>
        {
            await Reply(socket, await Read(socket), Established);
            await Send(socket, Frame(Output));
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "end", CancellationToken.None);
        });
        using var client = peer.Client(); await using var connection = await client.ConnectOecpAsync(peer.Session);
        await using var attachment = await connection.Runs.AttachAsync(Parameters);
        var completion = await attachment.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Check(completion is { Origin: NativeSubscriptionOrigin.UnexpectedDisconnect, ServerClose: null });
        var records = new List<RunAttachEventNotification>();
        await Failure(async () => { await foreach (var record in attachment.ReadAllAsync()) records.Add(record); }, NativeSubscriptionFailureKind.UnexpectedDisconnect);
        Check(records is [{ Event: OutputAgentAttachEvent }] && attachment.Establishment.RunId == Parameters.RunId &&
            attachment.Establishment.Execution == Parameters.Execution && attachment.LastDeliveredCursor is null);
        await peer.Finished;
    }

    [Test]
    public async Task AttachmentEnforcesSharedQueueAndMessageBounds()
    {
        var bytes = Encoding.UTF8.GetByteCount(Frame(Output));
        foreach (var (options, kind) in new[]
        {
            (new TransportOptions { MaxQueuedObservationRecords = 1 }, NativeSubscriptionFailureKind.RecordLimit),
            (new TransportOptions { MaxQueuedObservationBytes = bytes }, NativeSubscriptionFailureKind.StreamByteLimit),
            (new TransportOptions { MaxAggregateObservationBytes = bytes }, NativeSubscriptionFailureKind.AggregateByteLimit),
            (new TransportOptions { MaxOecpMessageBytes = bytes }, NativeSubscriptionFailureKind.SizeLimit)
        })
        {
            await using var peer = new Peer(async socket =>
            {
                await Reply(socket, await Read(socket), Established);
                await Send(socket, Frame(Output));
                await Send(socket, Frame(Output.Replace("SECRET", "SECRET-EXTRA")));
                if (kind != NativeSubscriptionFailureKind.SizeLimit)
                    Check((await Read(socket)).GetProperty("method").GetString() == "subscription/cancel");
            });
            using var client = peer.Client(options); await using var connection = await client.ConnectOecpAsync(peer.Session);
            await using var attachment = await connection.Runs.AttachAsync(Parameters);
            Check((await attachment.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Failure!.Kind == kind);
            var count = 0;
            await Failure(async () => { await foreach (var _ in attachment.ReadAllAsync()) count++; }, kind);
            Check(count == 1 && attachment.LastDeliveredCursor is null);
            await peer.Finished;
        }
    }
}
