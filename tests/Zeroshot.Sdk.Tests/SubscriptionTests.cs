using System.Net.WebSockets;
using System.Text.Json;
using TUnit.Core;
using Zeroshot.Native;
using Zeroshot.Native.Contracts;
using static Zeroshot.Client.Tests.OecpTests;
using static Zeroshot.Client.Tests.SubscriptionContractTests;

namespace Zeroshot.Client.Tests;

public sealed class SubscriptionTests
{
    private static string Establishment(string id, string cursor = "start") => $$"""{"subscriptionId":"{{id}}","runId":"run-1","atCursor":"{{cursor}}"}""";
    private static Task Event(WebSocket socket, string parameters) => Send(socket, $$"""{"jsonrpc":"2.0","method":"event","params":{{parameters}}}""");
    private static Task Closed(WebSocket socket, string id, string reason = "done", string cursor = "server-end")
        => Send(socket, $$$"""{"jsonrpc":"2.0","method":"subscription/closed","params":{"subscriptionId":"{{{id}}}","reason":"{{{reason}}}","lastDeliveredCursor":"{{{cursor}}}"}}""");
    private static async Task<NativeSubscriptionException> Failure(Func<Task> action, NativeSubscriptionFailureKind kind)
    {
        try { await action(); }
        catch (NativeSubscriptionException failure) { Check(failure.Kind == kind); return failure; }
        throw new InvalidOperationException("Expected subscription failure.");
    }
    private static async Task<List<T>> Drain<E, T>(NativeSubscription<E, T> subscription)
    {
        var records = new List<T>();
        await foreach (var item in subscription.ReadAllAsync()) records.Add(item);
        return records;
    }

    [Test]
    public async Task ImmediateEventsAreRegisteredBeforeCallerResumesAndCloseCursorStaysSeparate()
    {
        await using var peer = new Peer(async socket =>
        {
            var watch = await Read(socket);
            Check(watch.GetProperty("method").GetString() == "run/watch");
            Check(watch.GetProperty("params").GetProperty("fromCursor").GetString() == "opaque /? start");
            await Reply(socket, watch, Establishment("watch", "opaque /? start"));
            await Event(socket, WatchEvent);
            await Event(socket, WatchEvent.Replace("opaque cursor /? SECRET", "next cursor"));
            await Closed(socket, "watch");
            var logs = await Read(socket);
            Check(logs.GetProperty("method").GetString() == "run/logs");
            await Reply(socket, logs, Establishment("logs"));
            await Event(socket, LogEvent);
            await Closed(socket, "logs");
        });
        using var client = peer.Client(); await using var connection = await client.ConnectOecpAsync(peer.Session);
        await using var watch = await connection.Runs.WatchAsync(new() { RunId = new("run-1"), FromCursor = new("opaque /? start") });
        var watchClose = await watch.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Check(watchClose is { Origin: NativeSubscriptionOrigin.ServerClosed, ServerClose.Reason: SubscriptionCloseReason.Done });
        Check(watch.LastDeliveredCursor is null && watchClose.ServerClose!.LastDeliveredCursor!.Value == "server-end");
        var events = await Drain(watch);
        Check(events.Count == 2 && watch.LastDeliveredCursor!.Value == "next cursor");
        Check(watchClose.ServerClose!.LastDeliveredCursor!.Value == "server-end");
        Check(((FinishedRunStatus)events[0].Status).Metadata.TokenUsage!.InputTokens.Value == 7);
        await using var logs = await connection.Runs.LogsAsync(new() { RunId = new("run-1") });
        var records = await Drain(logs);
        Check(records.Count == 1 && records[0].Timestamp.Value == 1234567 && records[0].Execution!.Value == "worker:1");
        Check(logs.LastDeliveredCursor!.Value == "log boundary /?");
        await peer.Finished;
    }

    [Test]
    public async Task ForeignRunSourceExecutionAndMalformedActiveRecordsAreNeverDelivered()
    {
        foreach (var invalid in new[]
        {
            LogEvent.Replace("worker:1", "worker:2"), LogEvent.Replace("\"execution\":\"worker:1\",", ""),
            LogEvent.Replace("run-1", "run-2"), LogEvent.Replace("1234567", "0")
        })
        {
            await using var peer = new Peer(async socket =>
            {
                var request = await Read(socket);
                Check(request.GetProperty("params").GetProperty("execution").GetString() == "worker:1");
                await Reply(socket, request, Establishment("logs"));
                await Event(socket, LogEvent); await Event(socket, invalid);
                Check((await Read(socket)).GetProperty("method").GetString() == "subscription/cancel");
            });
            using var client = peer.Client(); await using var connection = await client.ConnectOecpAsync(peer.Session);
            await using var logs = await connection.Runs.LogsAsync(new() { RunId = new("run-1"), Execution = new("worker:1") });
            var delivered = 0;
            await Failure(async () => { await foreach (var _ in logs.ReadAllAsync()) delivered++; }, NativeSubscriptionFailureKind.Protocol);
            Check(delivered == 1 && (await logs.Completion).Origin == NativeSubscriptionOrigin.LocalFailure);
            await peer.Finished;
        }
        await using var sourcePeer = new Peer(async socket =>
        {
            await Reply(socket, await Read(socket), Establishment("watch"));
            await Event(socket, WatchEvent);
            await Event(socket, WatchEvent.Replace("owner/repo", "foreign/repo"));
            await Read(socket);
        });
        using var sourceClient = sourcePeer.Client(); await using var sourceConnection = await sourceClient.ConnectOecpAsync(sourcePeer.Session);
        await using var watch = await sourceConnection.Runs.WatchAsync(new() { RunId = new("run-1") });
        var count = 0;
        await Failure(async () => { await foreach (var _ in watch.ReadAllAsync()) count++; }, NativeSubscriptionFailureKind.Protocol);
        Check(count == 1);
        await sourcePeer.Finished;
    }

    [Test]
    public async Task ForeignAndDetachedIdsCannotContaminateStreamsOrBreakControl()
    {
        await using var peer = new Peer(async socket =>
        {
            await Reply(socket, await Read(socket), Establishment("logs"));
            await Event(socket, LogEvent.Replace("\"logs\"", "\"foreign\""));
            await Event(socket, LogEvent);
            var cancel = await Read(socket);
            Check(cancel.GetProperty("method").GetString() == "subscription/cancel");
            Check(cancel.GetProperty("params").GetProperty("subscriptionId").GetString() == "logs");
            await Event(socket, LogEvent);
            await Reply(socket, await Read(socket), "{\"runs\":[]}");
        });
        using var client = peer.Client(); await using var connection = await client.ConnectOecpAsync(peer.Session);
        var logs = await connection.Runs.LogsAsync(new() { RunId = new("run-1") });
        await using (var reader = logs.ReadAllAsync().GetAsyncEnumerator())
        {
            Check(await reader.MoveNextAsync() && reader.Current.SubscriptionId.Value == "logs");
            await logs.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
        Check((await logs.Completion).Origin == NativeSubscriptionOrigin.Disposed);
        Check((await connection.Runs.ListAsync()).Runs.Length == 0);
        await peer.Finished;
    }

    [Test]
    public async Task RemoteIncompleteClosuresDrainValidatedRecordsThenExposeTheirReason()
    {
        foreach (var (reason, kind) in new[] { ("SLOW_CONSUMER", NativeSubscriptionFailureKind.SlowConsumer), ("SOURCE_UNAVAILABLE", NativeSubscriptionFailureKind.SourceUnavailable) })
        {
            await using var peer = new Peer(async socket =>
            {
                await Reply(socket, await Read(socket), Establishment("logs"));
                await Event(socket, LogEvent); await Closed(socket, "logs", reason);
            });
            using var client = peer.Client(); await using var connection = await client.ConnectOecpAsync(peer.Session);
            await using var logs = await connection.Runs.LogsAsync(new() { RunId = new("run-1") });
            var outcome = await logs.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Check(outcome.Origin == NativeSubscriptionOrigin.ServerClosed && outcome.Failure!.Kind == kind);
            Check(logs.LastDeliveredCursor is null);
            var count = 0;
            await Failure(async () => { await foreach (var _ in logs.ReadAllAsync()) count++; }, kind);
            Check(count == 1 && logs.LastDeliveredCursor!.Value == "log boundary /?");
            await peer.Finished;
        }
    }

    [Test]
    public async Task DisconnectKeepsBufferedEvidenceWithoutInventingServerClose()
    {
        await using var peer = new Peer(async socket =>
        {
            await Reply(socket, await Read(socket), Establishment("logs"));
            await Event(socket, LogEvent);
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "peer done", CancellationToken.None);
        });
        using var client = peer.Client(); await using var connection = await client.ConnectOecpAsync(peer.Session);
        await using var logs = await connection.Runs.LogsAsync(new() { RunId = new("run-1") });
        var outcome = await logs.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Check(outcome.Origin == NativeSubscriptionOrigin.UnexpectedDisconnect && outcome.ServerClose is null);
        var count = 0;
        await Failure(async () => { await foreach (var _ in logs.ReadAllAsync()) count++; }, NativeSubscriptionFailureKind.UnexpectedDisconnect);
        Check(count == 1);
        await peer.Finished;
    }

    [Test]
    public async Task QueueOverflowUsesReservedControlCapacityAndOnlyDeliveredRecordsAdvance()
    {
        var ordinaryReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var peer = new Peer(async socket =>
        {
            await Reply(socket, await Read(socket), Establishment("logs"));
            var ordinary = await Read(socket); ordinaryReceived.SetResult();
            await Event(socket, LogEvent);
            await Event(socket, LogEvent.Replace("log boundary /?", "overflow cursor"));
            Check((await Read(socket)).GetProperty("method").GetString() == "subscription/cancel");
            await Reply(socket, ordinary, "{\"runs\":[]}");
        });
        using var client = peer.Client(new() { MaxQueuedObservationRecords = 1, MaxConcurrentRequests = 2, ReservedControlRequests = 1 });
        await using var connection = await client.ConnectOecpAsync(peer.Session);
        await using var logs = await connection.Runs.LogsAsync(new() { RunId = new("run-1") });
        var ordinary = connection.Runs.ListAsync();
        await ordinaryReceived.Task;
        var outcome = await logs.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Check(outcome is { Origin: NativeSubscriptionOrigin.LocalFailure, Failure.Kind: NativeSubscriptionFailureKind.RecordLimit });
        Check(logs.LastDeliveredCursor is null && (await ordinary).Runs.Length == 0);
        var count = 0;
        var error = await Failure(async () => { await foreach (var _ in logs.ReadAllAsync()) count++; }, NativeSubscriptionFailureKind.RecordLimit);
        Check(count == 1 && logs.LastDeliveredCursor!.Value == "log boundary /?");
        Check(!error.ToString().Contains("boundary"));
        await peer.Finished;
    }

    [Test]
    public async Task WrongEstablishmentRunOrExclusiveStartFailsBeforeReturningSubscription()
    {
        foreach (var result in new[] { Establishment("logs").Replace("run-1", "foreign-run"), Establishment("logs", "wrong cursor") })
        {
            await using var peer = new Peer(async socket => await Reply(socket, await Read(socket), result));
            using var client = peer.Client(); await using var connection = await client.ConnectOecpAsync(peer.Session);
            try
            {
                await connection.Runs.LogsAsync(new() { RunId = new("run-1"), FromCursor = new("start") });
                throw new InvalidOperationException("Foreign establishment was accepted.");
            }
            catch (NativeOecpException failure) { Check(failure.Kind == NativeOecpFailureKind.Protocol); }
            await peer.Finished;
        }
    }

    [Test]
    public async Task CancellationAfterEstablishmentDetachesWhileIdleAndReleasesAdmission()
    {
        await using var peer = new Peer(async socket =>
        {
            await Reply(socket, await Read(socket), Establishment("logs"));
            Check((await Read(socket)).GetProperty("method").GetString() == "subscription/cancel");
            await Reply(socket, await Read(socket), Establishment("next"));
            await Closed(socket, "next");
        });
        using var client = peer.Client(new() { MaxConcurrentSubscriptions = 1 });
        await using var connection = await client.ConnectOecpAsync(peer.Session);
        using var cancellation = new CancellationTokenSource();
        await using var logs = await connection.Runs.LogsAsync(new() { RunId = new("run-1") }, cancellation.Token);
        await Failure(async () => { await connection.Runs.WatchAsync(new() { RunId = new("run-1") }); }, NativeSubscriptionFailureKind.Admission);
        cancellation.Cancel();
        Check((await logs.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Origin == NativeSubscriptionOrigin.Cancelled);
        await using var next = await connection.Runs.LogsAsync(new() { RunId = new("run-1") });
        Check((await next.Completion).Origin == NativeSubscriptionOrigin.ServerClosed);
        await peer.Finished;
    }

    [Test]
    public async Task EncodedByteBudgetIsSharedByWatchAndLogBindings()
    {
        var watchFrame = $$$"""{"jsonrpc":"2.0","method":"event","params":{{{WatchEvent}}}}""";
        var logFrame = $$$"""{"jsonrpc":"2.0","method":"event","params":{{{LogEvent}}}}""";
        var budget = System.Text.Encoding.UTF8.GetByteCount(watchFrame + logFrame) - 1;
        await using var peer = new Peer(async socket =>
        {
            await Reply(socket, await Read(socket), Establishment("watch"));
            await Send(socket, watchFrame);
            await Reply(socket, await Read(socket), Establishment("logs"));
            await Send(socket, logFrame);
            Check((await Read(socket)).GetProperty("params").GetProperty("subscriptionId").GetString() == "logs");
            Check((await Read(socket)).GetProperty("params").GetProperty("subscriptionId").GetString() == "watch");
        });
        using var client = peer.Client(new() { MaxAggregateObservationBytes = budget });
        await using var connection = await client.ConnectOecpAsync(peer.Session);
        await using var watch = await connection.Runs.WatchAsync(new() { RunId = new("run-1") });
        await using var logs = await connection.Runs.LogsAsync(new() { RunId = new("run-1") });
        Check((await logs.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Failure!.Kind == NativeSubscriptionFailureKind.AggregateByteLimit);
        await Failure(async () => { await Drain(logs); }, NativeSubscriptionFailureKind.AggregateByteLimit);
        Check(logs.LastDeliveredCursor is null);
        await using (var reader = watch.ReadAllAsync().GetAsyncEnumerator()) Check(await reader.MoveNextAsync());
        await watch.DisposeAsync();
        await peer.Finished;
    }

    [Test]
    public async Task CancellationDuringEstablishmentDoesNotLeaveAnAdmittedStreamBehind()
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var peer = new Peer(async socket =>
        {
            var request = await Read(socket); received.SetResult(); await reply.Task;
            try { await Reply(socket, request, Establishment("logs")); await Event(socket, LogEvent); }
            catch (WebSocketException) { }
        });
        using var client = peer.Client(new() { MaxConcurrentSubscriptions = 1 });
        await using var connection = await client.ConnectOecpAsync(peer.Session);
        using var cancellation = new CancellationTokenSource();
        var opening = connection.Runs.LogsAsync(new() { RunId = new("run-1") }, cancellation.Token);
        await received.Task; cancellation.Cancel(); reply.SetResult();
        try
        {
            await using var subscription = await opening;
            Check((await subscription.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Origin == NativeSubscriptionOrigin.Cancelled);
        }
        catch (OecpOperationCanceledException) { }
        // The cancelled producer must release the same client's subscription admission.
        var queue = client.Observations.Open<string, Cursor>(); queue.Complete(); queue.Dispose();
        await peer.Finished;
    }
}
