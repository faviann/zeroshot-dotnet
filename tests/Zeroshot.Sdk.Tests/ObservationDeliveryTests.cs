using System.Net;
using TUnit.Core;
using Zeroshot.Native;
using Zeroshot.Native.Contracts;
using Zeroshot.Native.Observations;

namespace Zeroshot.Client.Tests;

public sealed class ObservationDeliveryTests
{
    private static readonly TransportOptions Small = new()
    {
        MaxConcurrentSubscriptions = 3, MaxQueuedObservationRecords = 2,
        MaxQueuedObservationBytes = 6, MaxAggregateObservationBytes = 10
    };
    private static NativeClient Client(TransportOptions? options = null, HttpClient? http = null) => NativeClient.ForHttp(new()
    {
        Origin = new Uri("https://target.example/"), Transport = options ?? Small
    }, http);
    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition, string message = "Observation assertion failed.")
    { if (!condition) throw new InvalidOperationException(message); }
    private static TException Throws<TException>(Action action) where TException : Exception
    {
        try { action(); }
        catch (TException failure) { return failure; }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }
    private static async Task<TException> ThrowsAsync<TException>(Task action) where TException : Exception
    {
        try { await action.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TException failure) { return failure; }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }
    private static void Overflow(Action action, ObservationFailureKind kind)
        => Check(Throws<ObservationFailure>(action).Kind == kind, "Wrong local failure classification.");
    private static async Task Drain(ObservationQueue<string, string> queue, params string[] expected)
    {
        var actual = new List<string>();
        await foreach (var record in queue.ReadAllAsync()) actual.Add(record);
        Check(actual.SequenceEqual(expected), "Records were lost, reordered or invented.");
    }

    [Test]
    public void DefaultsAndConfiguration()
    {
        var defaults = new TransportOptions();
        Check(defaults.MaxConcurrentSubscriptions == 16 && defaults.MaxQueuedObservationRecords == 256
            && defaults.MaxQueuedObservationBytes == 8 * 1024 * 1024 && defaults.MaxAggregateObservationBytes == 32 * 1024 * 1024);
        foreach (var invalid in new[]
        {
            defaults with { MaxConcurrentSubscriptions = 0 }, defaults with { MaxQueuedObservationRecords = -1 },
            defaults with { MaxQueuedObservationBytes = 0 }, defaults with { MaxAggregateObservationBytes = -1 }
        }) Throws<ArgumentOutOfRangeException>(() => Client(invalid));
        // Either byte bound may be tighter; both are independent ceilings.
        using var client = Client(defaults with { MaxAggregateObservationBytes = 1 });
        using var queue = client.Observations.Open<string, string>();
        Check(queue.TryEnqueue("a", 1));
        Overflow(() => queue.TryEnqueue("b", 1), ObservationFailureKind.AggregateByteLimit);
        queue.Complete();
    }

    [Test]
    public void DefaultAdmissionIsImmediateAndClientScoped()
    {
        using var first = Client(new());
        using var second = Client(new());
        var queues = Enumerable.Range(0, 16).Select(_ => first.Observations.Open<string, string>()).ToArray();
        Overflow(() => first.Observations.Open<string, string>(), ObservationFailureKind.Admission);
        using var independent = second.Observations.Open<string, string>();
        queues[0].Complete();
        using var replacement = first.Observations.Open<string, string>();
        foreach (var queue in queues) { queue.Complete(); queue.Dispose(); }
        independent.Complete();
        replacement.Complete();
    }

    [Test]
    public void DefaultRecordAndByteCeilingsAreBothEnforced()
    {
        using var client = Client(new());
        using var records = client.Observations.Open<string, string>();
        for (var i = 0; i < 256; i++) Check(records.TryEnqueue("record", 1));
        Overflow(() => records.TryEnqueue("record", 1), ObservationFailureKind.RecordLimit);
        using var bytes = client.Observations.Open<string, string>();
        Check(bytes.TryEnqueue("encoded record", 8 * 1024 * 1024));
        Overflow(() => bytes.TryEnqueue("record", 1), ObservationFailureKind.StreamByteLimit);
        records.Complete();
        bytes.Complete();
    }

    [Test]
    public void DefaultAggregateCeilingIsSharedAcrossRecordTypes()
    {
        using var client = Client(new());
        var queues = Enumerable.Range(0, 4).Select(_ => client.Observations.Open<string, string>()).ToArray();
        foreach (var queue in queues) Check(queue.TryEnqueue("encoded record", 8 * 1024 * 1024));
        using var competing = client.Observations.Open<int, Cursor>();
        Overflow(() => competing.TryEnqueue(1, 1, new Cursor("opaque")), ObservationFailureKind.AggregateByteLimit);
        queues[0].Dispose();
        queues[0].Complete();
        using var recovered = client.Observations.Open<int, Cursor>();
        Check(recovered.TryEnqueue(2, 8 * 1024 * 1024));
        foreach (var queue in queues) { queue.Complete(); queue.Dispose(); }
        competing.Complete();
        recovered.Complete();
    }

    [Test]
    public async Task SlowConsumerDrainsBeforeOverflowWithOnlyDeliveredCursorAdvancing()
    {
        using var client = Client();
        using var queue = client.Observations.Open<string, string>();
        Check(queue.TryEnqueue("first", 2, "cursor-secret-1"));
        Check(queue.TryEnqueue("second", 2, "cursor-secret-2"));
        var overflow = Throws<ObservationFailure>(() => queue.TryEnqueue("undelivered-secret", 2, "cursor-secret-3"));
        Check(overflow.Kind == ObservationFailureKind.RecordLimit && queue.StopRequested.IsCompleted);
        Check(queue.LastReceivedPosition == "cursor-secret-3" && queue.LastBufferedPosition == "cursor-secret-2"
            && queue.LastDeliveredPosition is null);
        Check(!queue.TryEnqueue("late", 1, "late-cursor"));
        queue.Complete();
        await using var reader = queue.ReadAllAsync().GetAsyncEnumerator();
        Check(await reader.MoveNextAsync() && reader.Current == "first");
        Check(queue.LastDeliveredPosition == "cursor-secret-1");
        Check(await reader.MoveNextAsync() && reader.Current == "second");
        Check(queue.LastDeliveredPosition == "cursor-secret-2");
        var failure = await ThrowsAsync<ObservationFailure>(reader.MoveNextAsync().AsTask());
        Check(ReferenceEquals(failure, overflow));
        Check(!failure.ToString().Contains("secret"), "Default diagnostics leaked a cursor or payload.");
        Check(queue.LastDeliveredPosition == "cursor-secret-2" && queue.LastBufferedPosition == "cursor-secret-2");
    }

    [Test]
    public async Task CompetingStreamsRecoverExactBytesAfterDeliveryAndDispose()
    {
        using var client = Client();
        using var first = client.Observations.Open<string, string>();
        using var second = client.Observations.Open<string, string>();
        Check(first.TryEnqueue("a", 4, "a"));
        Check(second.TryEnqueue("b", 6, "b"));
        using (var overflow = client.Observations.Open<string, string>())
        {
            Overflow(() => overflow.TryEnqueue("c", 1, "c"), ObservationFailureKind.AggregateByteLimit);
            overflow.Complete();
        }
        await using var reader = first.ReadAllAsync().GetAsyncEnumerator();
        Check(await reader.MoveNextAsync() && reader.Current == "a");
        using var third = client.Observations.Open<string, string>();
        Check(third.TryEnqueue("d", 4)); // Exact four bytes returned on delivery.
        second.Dispose();
        second.Dispose();
        second.Complete();
        using var replacement = client.Observations.Open<string, string>();
        Check(replacement.TryEnqueue("e", 6)); // No double-release when disposed twice.
        Overflow(() => first.TryEnqueue("f", 1), ObservationFailureKind.AggregateByteLimit);
        first.Complete();
        third.Complete();
        replacement.Complete();
        await Drain(third, "d");
        await Drain(replacement, "e");
    }

    [Test]
    public async Task ConcurrentProducersCannotOverbookTheSharedBudget()
    {
        using var client = Client(Small with { MaxAggregateObservationBytes = 6 });
        using var first = client.Observations.Open<string, string>();
        using var second = client.Observations.Open<string, string>();
        var start = Signal<bool>();
        async Task<bool> Publish(ObservationQueue<string, string> queue)
        {
            await start.Task;
            try { return queue.TryEnqueue("record", 6); }
            catch (ObservationFailure failure)
            {
                Check(failure.Kind == ObservationFailureKind.AggregateByteLimit);
                return false;
            }
            finally { queue.Complete(); }
        }
        var firstWrite = Publish(first);
        var secondWrite = Publish(second);
        start.SetResult(true);
        var results = await Task.WhenAll(firstWrite, secondWrite).WaitAsync(TimeSpan.FromSeconds(5));
        Check(results.Count(accepted => accepted) == 1, "Competing reservations exceeded the aggregate bound.");
        await Drain(results[0] ? first : second, "record");
        await ThrowsAsync<ObservationFailure>(Drain(results[0] ? second : first));
        using var recovered = client.Observations.Open<string, string>();
        Check(recovered.TryEnqueue("all bytes recovered", 6));
        recovered.Complete();
        await Drain(recovered, "all bytes recovered");
    }

    [Test]
    public async Task CompletedUndrainedQueuesRetainBytesAndSlots()
    {
        using var client = Client(Small with { MaxConcurrentSubscriptions = 1 });
        using var queue = client.Observations.Open<string, string>();
        Check(queue.TryEnqueue("a", 6));
        queue.Complete();
        Overflow(() => client.Observations.Open<string, string>(), ObservationFailureKind.Admission);
        await Drain(queue, "a");
        using var recovered = client.Observations.Open<string, string>();
        Check(recovered.TryEnqueue("b", 6));
        recovered.Complete();
        await Drain(recovered, "b");
    }

    [Test]
    public async Task OversizedFirstRecordFailsWithoutRetainingBytes()
    {
        using var client = Client();
        using var queue = client.Observations.Open<string, string>();
        Overflow(() => queue.TryEnqueue("too large", 7, "received"), ObservationFailureKind.StreamByteLimit);
        Check(queue.LastReceivedPosition == "received" && queue.LastBufferedPosition is null && queue.LastDeliveredPosition is null);
        queue.Complete();
        await ThrowsAsync<ObservationFailure>(Drain(queue));
        using var first = client.Observations.Open<string, string>();
        using var second = client.Observations.Open<string, string>();
        Check(first.TryEnqueue("a", 6) && second.TryEnqueue("b", 4));
        first.Complete();
        second.Complete();
        await Drain(first, "a");
        await Drain(second, "b");
    }

    [Test]
    public async Task WaitingReaderIsWokenByEnqueueAndNormalCompletion()
    {
        using var client = Client();
        using var queue = client.Observations.Open<string, string>();
        await using var reader = queue.ReadAllAsync().GetAsyncEnumerator();
        var read = reader.MoveNextAsync().AsTask();
        Check(!read.IsCompleted);
        Check(queue.TryEnqueue("a", 1, "position"));
        Check(await read.WaitAsync(TimeSpan.FromSeconds(5)) && reader.Current == "a");
        Check(queue.LastDeliveredPosition == "position");
        var done = reader.MoveNextAsync().AsTask();
        Check(!done.IsCompleted);
        queue.Complete();
        Check(!await done.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Test]
    public async Task CancellationOfAbandonedConsumerDiscardsQueueButHoldsUnsettledProducerSlot()
    {
        using var client = Client(Small with { MaxConcurrentSubscriptions = 1 });
        using var cancellation = new CancellationTokenSource();
        using var queue = client.Observations.Open<string, string>();
        await using var reader = queue.ReadAllAsync(cancellation.Token).GetAsyncEnumerator();
        Check(queue.TryEnqueue("delivered", 1, "one"));
        Check(await reader.MoveNextAsync());
        Check(queue.TryEnqueue("abandoned", 6, "two"));
        cancellation.Cancel(); // No pending MoveNext: consumer is idle/abandoned.
        Check(queue.StopRequested.IsCompleted && !queue.TryEnqueue("late", 1));
        Overflow(() => client.Observations.Open<string, string>(), ObservationFailureKind.Admission);
        queue.Complete(); // Binding's producer and cleanup have now settled.
        using var replacement = client.Observations.Open<string, string>();
        Check(replacement.TryEnqueue("replacement", 6));
        Check(queue.LastDeliveredPosition == "one");
        var failure = await ThrowsAsync<OperationCanceledException>(reader.MoveNextAsync().AsTask());
        Check(failure.CancellationToken == cancellation.Token);
        replacement.Complete();
    }

    [Test]
    public async Task OpeningCancellationWorksBeforeEnumerationAndBeforeAdmission()
    {
        using var client = Client(Small with { MaxConcurrentSubscriptions = 1 });
        using var cancellation = new CancellationTokenSource();
        using var queue = client.Observations.Open<string, string>(cancellation.Token);
        Check(queue.TryEnqueue("never read", 6, "undelivered"));
        cancellation.Cancel();
        queue.Complete();
        Check(queue.StopRequested.IsCompleted && queue.LastDeliveredPosition is null);
        await ThrowsAsync<OperationCanceledException>(Drain(queue));
        Throws<OperationCanceledException>(() => client.Observations.Open<string, string>(cancellation.Token));
        using var recovered = client.Observations.Open<string, string>();
        Check(recovered.TryEnqueue("replacement", 6));
        recovered.Complete();
    }

    [Test]
    public async Task CancellationWakesPendingReadAndLateProducerCannotRepopulateTheQueue()
    {
        using var client = Client(Small with { MaxConcurrentSubscriptions = 1 });
        using var cancellation = new CancellationTokenSource();
        using var queue = client.Observations.Open<string, string>(cancellation.Token);
        var producerMayFinish = Signal<bool>();
        async Task Producer()
        {
            await queue.StopRequested;
            await producerMayFinish.Task;
            Check(!queue.TryEnqueue("late allocation", 6, "never-delivered"));
            queue.Complete();
        }
        var producer = Producer();
        await using var reader = queue.ReadAllAsync().GetAsyncEnumerator();
        var pending = reader.MoveNextAsync().AsTask();
        cancellation.Cancel();
        await ThrowsAsync<OperationCanceledException>(pending);
        Overflow(() => client.Observations.Open<string, string>(), ObservationFailureKind.Admission);
        producerMayFinish.SetResult(true);
        await producer.WaitAsync(TimeSpan.FromSeconds(5));
        Check(queue.LastReceivedPosition is null && queue.LastDeliveredPosition is null);
        using var recovered = client.Observations.Open<string, string>();
        Check(recovered.TryEnqueue("replacement", 6));
        recovered.Complete();
    }

    [Test]
    public async Task CancellationReturnsBytesWhileTheProducerSlotRemainsHeld()
    {
        using var client = Client(Small with { MaxConcurrentSubscriptions = 2, MaxAggregateObservationBytes = 6 });
        using var cancellation = new CancellationTokenSource();
        using var cancelled = client.Observations.Open<string, string>(cancellation.Token);
        using var active = client.Observations.Open<string, string>();
        Check(cancelled.TryEnqueue("abandoned", 6));
        cancellation.Cancel();
        Check(active.TryEnqueue("reused all bytes", 6));
        Overflow(() => client.Observations.Open<string, string>(), ObservationFailureKind.Admission);
        cancelled.Complete();
        using var replacement = client.Observations.Open<string, string>();
        active.Complete();
        await Drain(active, "reused all bytes");
        Check(replacement.TryEnqueue("reused again", 6));
        replacement.Complete();
        await Drain(replacement, "reused again");
    }

    [Test]
    public async Task CompletionAndCancellationEitherOrderReleaseEachReservationOnce()
    {
        foreach (var completeFirst in new[] { true, false })
        {
            using var client = Client(Small with { MaxConcurrentSubscriptions = 1 });
            using var cancellation = new CancellationTokenSource();
            using var queue = client.Observations.Open<string, string>(cancellation.Token);
            Check(queue.TryEnqueue("queued", 6, "never-delivered"));
            if (completeFirst) queue.Complete();
            cancellation.Cancel();
            if (!completeFirst) queue.Complete();
            queue.Complete();
            Check(queue.LastDeliveredPosition is null);
            await ThrowsAsync<OperationCanceledException>(Drain(queue));
            using var recovered = client.Observations.Open<string, string>();
            Check(recovered.TryEnqueue("replacement", 6));
            Overflow(() => recovered.TryEnqueue("extra", 1), ObservationFailureKind.StreamByteLimit);
            recovered.Complete();
        }
    }

    [Test]
    public async Task EnumeratorDisposalDetachesWithoutAdvancingUndeliveredPosition()
    {
        using var client = Client(Small with { MaxConcurrentSubscriptions = 1 });
        using var queue = client.Observations.Open<string, string>();
        Check(queue.TryEnqueue("one", 3, "one") && queue.TryEnqueue("two", 3, "two"));
        await foreach (var _ in queue.ReadAllAsync()) break;
        Check(queue.StopRequested.IsCompleted && queue.LastDeliveredPosition == "one");
        queue.Complete();
        using var recovered = client.Observations.Open<string, string>();
        Check(recovered.TryEnqueue("replacement", 6));
        recovered.Complete();
        await Drain(recovered, "replacement");
    }

    [Test]
    public async Task ClientDisposalWakesReadersAndEndsNeverReadQueues()
    {
        var client = Client();
        using var waiting = client.Observations.Open<string, string>();
        using var abandoned = client.Observations.Open<string, string>();
        Check(abandoned.TryEnqueue("never delivered", 6, "cursor"));
        await using var reader = waiting.ReadAllAsync().GetAsyncEnumerator();
        var pending = reader.MoveNextAsync().AsTask();
        client.Dispose();
        await client.DisposeAsync();
        Check(waiting.StopRequested.IsCompleted && abandoned.StopRequested.IsCompleted);
        Check(abandoned.LastDeliveredPosition is null && !abandoned.TryEnqueue("late", 1));
        await ThrowsAsync<ObjectDisposedException>(pending);
        await ThrowsAsync<ObjectDisposedException>(Drain(abandoned));
        Throws<ObjectDisposedException>(() => client.Observations.Open<string, string>());
        waiting.Complete();
        abandoned.Complete();
    }

    [Test]
    public async Task ASecondConsumerCannotStealRecordsOrDiscardTheFirstConsumersQueue()
    {
        using var client = Client();
        using var queue = client.Observations.Open<string, string>();
        await using var first = queue.ReadAllAsync().GetAsyncEnumerator();
        var pending = first.MoveNextAsync().AsTask();
        await using var second = queue.ReadAllAsync().GetAsyncEnumerator();
        await ThrowsAsync<InvalidOperationException>(second.MoveNextAsync().AsTask());
        Check(queue.TryEnqueue("a", 1));
        Check(await pending.WaitAsync(TimeSpan.FromSeconds(5)) && first.Current == "a");
        queue.Complete();
        Check(!await first.MoveNextAsync());
    }

    [Test]
    public async Task BindingFailurePreservesQueuedOrderAndDoesNotBecomeSuccessfulCompletion()
    {
        using var client = Client();
        using var queue = client.Observations.Open<string, string>();
        Check(queue.TryEnqueue("a", 2, "a") && queue.TryEnqueue("b", 2, "b"));
        var bindingFailure = new IOException("Safe binding-owned transport failure.");
        queue.Complete(bindingFailure);
        queue.Complete();
        await using var reader = queue.ReadAllAsync().GetAsyncEnumerator();
        Check(await reader.MoveNextAsync() && reader.Current == "a");
        Check(await reader.MoveNextAsync() && reader.Current == "b");
        Check(ReferenceEquals(await ThrowsAsync<IOException>(reader.MoveNextAsync().AsTask()), bindingFailure));
        Check(queue.LastDeliveredPosition == "b");
    }

    [Test]
    public async Task BindingFailureReachesTheConsumerBeforeUncooperativeCleanupSettles()
    {
        using var client = Client(Small with { MaxConcurrentSubscriptions = 1 });
        using var queue = client.Observations.Open<string, string>();
        Check(queue.TryEnqueue("a", 6, "a"));
        var cleanup = Signal<bool>();
        async Task Producer()
        {
            await queue.StopRequested;
            await cleanup.Task;
            queue.Complete();
        }
        var producer = Producer();
        queue.StopReceiving(new IOException("Safe binding-owned failure."));
        await using var reader = queue.ReadAllAsync().GetAsyncEnumerator();
        Check(await reader.MoveNextAsync() && reader.Current == "a");
        await ThrowsAsync<IOException>(reader.MoveNextAsync().AsTask());
        Check(queue.LastDeliveredPosition == "a" && !producer.IsCompleted);
        Overflow(() => client.Observations.Open<string, string>(), ObservationFailureKind.Admission);
        cleanup.SetResult(true);
        await producer.WaitAsync(TimeSpan.FromSeconds(5));
        using var recovered = client.Observations.Open<string, string>();
        Check(recovered.TryEnqueue("replacement", 6));
        recovered.Complete();
    }

    [Test]
    public async Task CongestedQueuesLeaveTheSameClientsReservedControlCapacityUsable()
    {
        using var handler = new ControlledHttpHandler();
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var client = Client(Small with { MaxConcurrentRequests = 2, ReservedControlRequests = 1 }, http);
        using var first = client.Observations.Open<string, string>();
        using var second = client.Observations.Open<string, string>();
        Check(first.TryEnqueue("a", 6) && second.TryEnqueue("b", 4));
        Overflow(() => second.TryEnqueue("overflow", 1), ObservationFailureKind.AggregateByteLimit);
        var discovery = client.Target.DiscoverAsync();
        await handler.DiscoveryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var capacity = await ThrowsAsync<NativeHttpException>(client.Target.DiscoverAsync());
        Check(capacity.Kind == NativeHttpFailureKind.Capacity);
        var descriptor = NativeJson.DeserializeUtf8<TargetDiscoveryDocument>(System.Text.Encoding.UTF8.GetBytes(ControlledHttpHandler.Discovery));
        var session = await client.Target.CreateOecpSessionAsync(descriptor).WaitAsync(TimeSpan.FromSeconds(5));
        Check(session.Endpoint == "wss://target.example/native-v2/oecp", "Congestion blocked session/control admission.");
        handler.ReleaseDiscovery.SetResult(true);
        await discovery;
        first.Complete();
        second.Complete();
    }

    private sealed class ControlledHttpHandler : HttpMessageHandler
    {
        internal const string Discovery = """{"kind":"zeroshot.native-v2-target/v2","authentication":"none","runPath":"/native-v2/run","sessionPath":"/native-v2/oecp-session","oecpPath":"/native-v2/oecp","audience":"controller"}""";
        internal readonly TaskCompletionSource<bool> DiscoveryStarted = Signal<bool>();
        internal readonly TaskCompletionSource<bool> ReleaseDiscovery = Signal<bool>();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Get)
            {
                DiscoveryStarted.TrySetResult(true);
                await ReleaseDiscovery.Task.WaitAsync(token);
                return Reply(request, Discovery);
            }
            return Reply(request, """{"endpoint":"wss://target.example/native-v2/oecp"}""");
        }
        private static HttpResponseMessage Reply(HttpRequestMessage request, string body)
            => new(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(body) };
    }
}
