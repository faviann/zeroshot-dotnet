using System.IO.Pipelines;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using TUnit.Core;
using Zeroshot.Native;
using Zeroshot.Native.Contracts;
using static Zeroshot.Client.Tests.SubscriptionContractTests;

namespace Zeroshot.Client.Tests;

public sealed class StreamConnectionTests
{
    private const string EmptyList = """{"runs":[]}""";

    private static long Id(string line) => JsonDocument.Parse(line).RootElement.GetProperty("id").GetInt64();
    private static string Reply(long id, string result) => $$"""{"jsonrpc":"2.0","id":{{id}},"result":{{result}}}""" + "\n";

    private static string WatchReply(JsonElement request)
        => $$$"""{"jsonrpc":"2.0","id":{{{request.GetProperty("id")}}},"result":{"subscriptionId":"watch","runId":"run-1","atCursor":"start"}}""" + "\n";

    private static async Task<T> Throws<T>(Task task) where T : Exception
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (T error) { return error; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    [Test]
    public async Task FramesEachRequestAsOneLineAndReassemblesSplitAndCoalescedReplies()
    {
        await using var peer = new NdjsonPeer();
        await using var connection = await OecpConnection.FromStreamsAsync(peer.Input, peer.Output);
        var first = connection.Runs.ListAsync();
        var second = connection.Runs.ListAsync();
        var lines = new[] { await peer.ReadLine(), await peer.ReadLine() }.OrderBy(Id).ToArray();
        var (one, two) = (Id(lines[0]), Id(lines[1]));
        Check(lines[0] == $$$"""{"jsonrpc":"2.0","id":{{{one}}},"method":"run/list","params":{}}""" && two > one);

        await peer.Write($$"""{"jsonrpc":"2.0","id":{{two}},"res""");
        await Task.Delay(100);
        Check(!second.IsCompleted, "A partial line must not complete a call.");
        // The rest of the split reply and the complete first reply arrive in one write.
        await peer.Write($$"""ult":{{EmptyList}}}""" + "\n" + Reply(one, EmptyList));
        Check((await first).Runs.Length == 0 && (await second).Runs.Length == 0);
    }

    [Test]
    public async Task OversizedLineFailsTheConnectionWithSizeLimit()
    {
        await using var peer = new NdjsonPeer();
        await using var connection = await OecpConnection.FromStreamsAsync(peer.Input, peer.Output,
            options: new() { MaxOecpMessageBytes = 64 });
        var pending = connection.Runs.ListAsync();
        await peer.ReadLine();
        await peer.Write(new string(' ', 65));
        Check((await Throws<NativeOecpException>(pending)).Kind == NativeOecpFailureKind.SizeLimit);
        Check((await connection.Completion)!.Kind == NativeOecpFailureKind.SizeLimit);
    }

    [Test]
    public async Task EofAfterPartialFrameIsADisconnectWithUnknownForceAndNoInventedClose()
    {
        await using var peer = new NdjsonPeer();
        await using var connection = await OecpConnection.FromStreamsAsync(peer.Input, peer.Output);
        var opening = connection.Runs.WatchAsync(new() { RunId = new("run-1") });
        var watch = JsonDocument.Parse(await peer.ReadLine()).RootElement;
        await peer.Write(WatchReply(watch) +
            $$"""{"jsonrpc":"2.0","method":"event","params":{{RunWatchRecord}}}""" + "\n");
        await using var subscription = await opening;
        var force = connection.Runs.ForceAsync(new("run-1"));
        Check((await peer.ReadLine()).Contains("\"method\":\"run/force\""));

        await peer.Write("""{"jsonrpc":"2.0","id":""");
        peer.End();
        var attempt = await force;
        Check(attempt is { Outcome: NativeAttemptOutcome.Unknown, Origin: null, Failure: NativeOecpException { Kind: NativeOecpFailureKind.Transport } });
        var delivered = 0;
        var failure = await Throws<NativeSubscriptionException>(Task.Run(async () =>
        {
            await foreach (var _ in subscription.ReadAllAsync()) delivered++;
        }));
        var completion = await subscription.Completion;
        Check(delivered == 1 && failure.Kind == NativeSubscriptionFailureKind.UnexpectedDisconnect &&
            completion is { Origin: NativeSubscriptionOrigin.UnexpectedDisconnect, ServerClose: null });
        Check((await connection.Completion)!.Kind == NativeOecpFailureKind.Transport);
    }

    [Test]
    public async Task UnaryCancellationOnlyDetachesAndSubscriptionCancellationIsSent()
    {
        await using var peer = new NdjsonPeer();
        await using var connection = await OecpConnection.FromStreamsAsync(peer.Input, peer.Output);
        await Throws<NotSupportedException>(connection.CancelRequestAsync(new RequestId(1)));

        using var cancel = new CancellationTokenSource();
        var abandoned = connection.Runs.ListAsync(cancellationToken: cancel.Token);
        var abandonedId = Id(await peer.ReadLine());
        cancel.Cancel();
        var cancelled = await Throws<OecpOperationCanceledException>(abandoned);
        Check(cancelled.Dispatch is { SendCompleted: true, ResponseReceived: false });
        // The late reply keeps its retired ID and is ignored; the next line is the next call, not $/cancelRequest.
        await peer.Write(Reply(abandonedId, EmptyList));
        var opening = connection.Runs.WatchAsync(new() { RunId = new("run-1") });
        var watch = JsonDocument.Parse(await peer.ReadLine()).RootElement;
        Check(watch.GetProperty("method").GetString() == "run/watch");
        await peer.Write(WatchReply(watch));
        await (await opening).DisposeAsync();
        Check(await peer.ReadLine() == """{"jsonrpc":"2.0","method":"subscription/cancel","params":{"subscriptionId":"watch"}}""");
        Check(!connection.Completion.IsCompleted);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task BorrowedStreamsStayOpenUnlessOwnershipIsTransferred(bool leaveOpen)
    {
        await using var peer = new NdjsonPeer();
        var input = new Probe(peer.Input, peer.Output);
        var output = new Probe(peer.Input, peer.Output);
        var connection = await OecpConnection.FromStreamsAsync(input, output, leaveOpen);
        await connection.DisposeAsync();
        Check(input.Disposed == !leaveOpen && output.Disposed == !leaveOpen && await connection.Completion is null);

        var duplex = new Probe(peer.Input, peer.Output);
        await (await OecpConnection.FromStreamsAsync(duplex, duplex, leaveOpen: false)).DisposeAsync();
        Check(duplex.DisposeCount == 1, "A shared duplex stream is disposed once.");

        var failing = new Probe(peer.Input, peer.Output) { ThrowOnDispose = true };
        var owning = await OecpConnection.FromStreamsAsync(failing, failing, leaveOpen: false);
        owning.Dispose();
        Check(owning.Completion.IsCompletedSuccessfully && failing.Disposed, "A failing owned stream cannot block completion.");
    }

    [Test]
    public async Task LateReplyOnReusedBorrowedStreamCannotCompleteALaterConnectionsCall()
    {
        await using var peer = new NdjsonPeer();
        var first = await OecpConnection.FromStreamsAsync(peer.Input, peer.Output);
        var abandoned = first.Runs.ListAsync();
        var staleId = Id(await peer.ReadLine());
        await first.DisposeAsync();
        await Throws<NativeOecpException>(abandoned);

        await using var second = await OecpConnection.FromStreamsAsync(peer.Input, peer.Output);
        var force = second.Runs.ForceAsync(new("run-1"));
        var forceId = Id(await peer.ReadLine());
        Check(forceId != staleId, "Request IDs are never reused on a borrowed stream.");
        var status = """{"runId":"run-1","title":"t","source":{"repository":"a/b","branch":"main","revision":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"},"size":"small","atCursor":"c","status":{"phase":"stopping","activeExecutions":[]}}""";
        // Native's late reply to the first connection has the same shape as a force acknowledgement.
        await peer.Write(Reply(staleId, status));
        await peer.Write($$$$"""{"jsonrpc":"2.0","id":{{{{forceId}}}},"error":{"code":-32000,"message":"run was not found","data":{"code":"NOT_FOUND"}}}""" + "\n");
        var attempt = await force;
        Check(attempt is { Outcome: NativeAttemptOutcome.Rejected, Response: null } && !second.Completion.IsCompleted);
    }

    [Test]
    public async Task UnixControllerSocketIsOwnedAndReleasedOnDisposal()
    {
        // An escape-like name and an extra leading slash must survive exactly in the attempt origin.
        var directory = Directory.CreateTempSubdirectory("zs %41-");
        var path = Path.Combine(directory.FullName, "controller.sock");
        try
        {
            await Throws<ArgumentException>(OecpConnection.ConnectUnixAsync("controller.sock"));
            Check((await Throws<NativeOecpException>(OecpConnection.ConnectUnixAsync(path))).Kind == NativeOecpFailureKind.Transport);
            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen();
            var connection = await OecpConnection.ConnectUnixAsync("/" + path);
            using var server = new NetworkStream(await listener.AcceptAsync(), ownsSocket: true);
            using var reader = new StreamReader(server);

            var force = connection.Runs.ForceAsync(new("run-1"));
            var forceLine = (await reader.ReadLineAsync())!;
            Check(forceLine.Contains("\"method\":\"run/force\""));
            var status = """{"runId":"run-1","title":"t","source":{"repository":"a/b","branch":"main","revision":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"},"size":"small","atCursor":"c","status":{"phase":"stopping","activeExecutions":[]}}""";
            await server.WriteAsync(Encoding.UTF8.GetBytes(Reply(Id(forceLine), status)));
            var attempt = await force;
            Check(attempt is { Outcome: NativeAttemptOutcome.Acknowledged, Origin: { Scheme: "file", Host: "" } origin } &&
                Uri.UnescapeDataString(origin.AbsolutePath) == "/" + path, "The origin identifies exactly the supplied path.");

            var pending = connection.Runs.ListAsync();
            await reader.ReadLineAsync();
            await connection.DisposeAsync();
            Check((await Throws<NativeOecpException>(pending)).Kind == NativeOecpFailureKind.Transport);
            Check(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) is null, "The owned socket is closed.");
        }
        finally { directory.Delete(true); }
    }

    /// <summary>Two in-memory pipes: the client reads <see cref="Input"/> and writes <see cref="Output"/>.</summary>
    private sealed class NdjsonPeer : IAsyncDisposable
    {
        private readonly Pipe toClient = new();
        private readonly Pipe fromClient = new();
        private readonly StreamReader reader;
        public Stream Input { get; }
        public Stream Output { get; }
        public NdjsonPeer()
        {
            Input = toClient.Reader.AsStream();
            Output = fromClient.Writer.AsStream();
            reader = new StreamReader(fromClient.Reader.AsStream());
        }
        public async Task<string> ReadLine() => await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5))
            ?? throw new InvalidOperationException("Client output ended.");
        public async Task Write(string text) => await toClient.Writer.WriteAsync(Encoding.UTF8.GetBytes(text));
        public void End() => toClient.Writer.Complete();
        public ValueTask DisposeAsync() { toClient.Writer.Complete(); reader.Dispose(); return ValueTask.CompletedTask; }
    }

    private sealed class Probe(Stream input, Stream output) : Stream
    {
        public int DisposeCount;
        public bool ThrowOnDispose;
        public bool Disposed => DisposeCount > 0;
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => input.ReadAsync(buffer, token);
        public override void Write(byte[] buffer, int offset, int count) => output.Write(buffer, offset, count);
        public override void Flush() => output.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) DisposeCount++;
            base.Dispose(disposing);
            if (ThrowOnDispose) throw new IOException("Caller stream failed to close.");
        }
    }
}
