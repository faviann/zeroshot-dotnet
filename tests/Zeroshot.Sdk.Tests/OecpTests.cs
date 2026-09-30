using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TUnit.Core;
using Zeroshot.Native;
using Zeroshot.Native.Contracts;

namespace Zeroshot.Client.Tests;

public sealed class OecpTests
{
    private const string Empty = """{"spec":null,"status":{"phase":"empty","observedGeneration":null,"currentRunId":null,"atCursor":null},"atCursor":null}""";
    private const string Status = """{"runId":"run-1","title":"test","source":{"repository":"acme/project","branch":"main","revision":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"},"size":"small","atCursor":"cursor-1","status":{"phase":"finished","terminalResult":{"status":"failed","reason":"test"},"metadata":{"tokenUsage":{"inputTokens":4,"outputTokens":2,"complete":true}}},"workspaceRecovery":{"recoverable":true,"connectionRequirements":{"gateway":["GATEWAY_API_KEY"]},"successorRunId":"run-2"}}""";
    private static void Check(bool value, string message = "OECP assertion failed.")
    { if (!value) throw new InvalidOperationException(message); }
    private static async Task<NativeOecpException> Failure(Task task, NativeOecpFailureKind kind)
    {
        try { await task; }
        catch (NativeOecpException error) { Check(error.Kind == kind, error.ToString()); return error; }
        throw new InvalidOperationException("Expected OECP failure.");
    }

    [Test]
    public async Task CorrelatesConcurrentFragmentedRepliesAndPreservesFullNativeProjections()
    {
        await using var peer = new Peer(async socket =>
        {
            var first = await Read(socket); var second = await Read(socket);
            Check(first.GetProperty("method").GetString() == "get" && second.GetProperty("method").GetString() == "run/status");
            await Reply(socket, second, Status, fragmented: true);
            await Reply(socket, first, Empty);
        });
        using var client = peer.Client(); await using var connection = await client.ConnectOecpAsync(peer.Session);
        var get = connection.Cluster.GetAsync(); var status = connection.Runs.StatusAsync(new("run-1"));
        var result = await status;
        Check(result.Status is FinishedRunStatus { Metadata.TokenUsage: not null });
        Check(result.WorkspaceRecovery.Value.Recoverable && result.WorkspaceRecovery.Value.SuccessorRunId!.Value == "run-2");
        Check((await get).Spec is null);
        await peer.Finished;
    }

    [Test]
    public async Task RemoteNumericAndOpenDomainErrorsRetainDispatchFactsAndSafeFormatting()
    {
        await using var peer = new Peer(async socket =>
        {
            var request = await Read(socket);
            await Send(socket, JsonSerializer.Serialize(new { jsonrpc = "2.0", id = request.GetProperty("id"), error = new { code = -32000, message = "REMOTE-SECRET", data = new { code = "FUTURE_ERROR", details = new { secret = "RAW-SECRET" } } } }));
        });
        using var client = peer.Client(); await using var connection = await client.ConnectOecpAsync(peer.Session);
        var error = await Failure(connection.Runs.ListAsync(), NativeOecpFailureKind.RpcError);
        Check(error.RpcError!.Code == -32000 && error.RpcError.Data!.Code == "FUTURE_ERROR");
        Check(error.Dispatch.SendStarted && error.Dispatch.SendCompleted && error.Dispatch.ResponseReceived);
        Check(!error.ToString().Contains("SECRET") && error.ExportRawDiagnostic() is null);
        await peer.Finished;
    }

    [Test]
    public async Task MalformedForeignAndUncorrelatedRepliesFailTheConnection()
    {
        foreach (var response in new[]
        {
            """{"jsonrpc":"1.0","id":1,"result":{"runs":[]}}""",
            """{"jsonrpc":"2.0","id":1.0,"result":{"runs":[]}}""",
            """{"jsonrpc":"2.0","id":1e0,"result":{"runs":[]}}""",
            """{"jsonrpc":"2.0","id":9223372036854775808,"result":{"runs":[]}}""",
            """{"jsonrpc":"2.0","id":null,"result":{"runs":[]}}""",
            """{"jsonrpc":"2.0","id":"1","result":{"runs":[]}}""",
            """{"jsonrpc":"2.0","id":7,"result":{"runs":[]}}""",
            """{"jsonrpc":"2.0","id":1,"result":{"runs":[]},"error":{"code":-1,"message":"x"}}""",
            """{"jsonrpc":"2.0","id":1,"id":1,"result":{"runs":[]}}""",
            """{"jsonrpc":"2.0","id":1,"result":{"runs":[{}]}}"""
        })
        {
            await using var peer = new Peer(async socket => { await Read(socket); await Send(socket, response); });
            using var client = peer.Client(); await using var connection = await client.ConnectOecpAsync(peer.Session);
            var error = await Failure(connection.Runs.ListAsync(), NativeOecpFailureKind.Protocol);
            Check(error.Dispatch.SendStarted);
            Check((await connection.Completion)!.Kind == NativeOecpFailureKind.Protocol);
            await peer.Finished;
        }
    }

    [Test]
    public async Task ExactRunAndKnownSourceAreCheckedBeforeReturningStatus()
    {
        foreach (var sourceMismatch in new[] { false, true })
        {
            await using var peer = new Peer(async socket => await Reply(socket, await Read(socket), Status));
            using var client = peer.Client(); await using var connection = await client.ConnectOecpAsync(peer.Session);
            var expected = new ResolvedSource { Repository = new("acme/foreign"), Branch = new("main"), Revision = new(new string('a', 40)) };
            await Failure(connection.Runs.StatusAsync(new(sourceMismatch ? "run-1" : "foreign"), sourceMismatch ? expected : null), NativeOecpFailureKind.Protocol);
            await peer.Finished;
        }
    }

    [Test]
    public async Task ExplicitCancellationAndLocalCancellationAreDistinctAndLateRepliesCannotCompleteAnotherCall()
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var peer = new Peer(async socket =>
        {
            var request = await Read(socket); received.SetResult();
            var explicitCancel = await Read(socket);
            Check(explicitCancel.GetProperty("method").GetString() == "$/cancelRequest");
            Check(explicitCancel.GetProperty("params").GetProperty("id").GetInt64() == request.GetProperty("id").GetInt64());
            var localCancel = await Read(socket);
            Check(localCancel.GetProperty("method").GetString() == "$/cancelRequest");
            await Reply(socket, request, "{\"runs\":[]}");
            await Reply(socket, await Read(socket), Empty);
        });
        using var client = peer.Client(); await using var connection = await client.ConnectOecpAsync(peer.Session);
        var handle = connection.CreateRequest();
        using var cancel = new CancellationTokenSource();
        var call = connection.Runs.ListAsync(handle, cancel.Token);
        await received.Task;
        await connection.CancelRequestAsync(handle.Id);
        Check(!call.IsCompleted);
        cancel.Cancel();
        try { await call; throw new InvalidOperationException("Expected cancellation."); }
        catch (OecpOperationCanceledException error) { Check(error.Dispatch.SendCompleted); }
        Check((await connection.Cluster.GetAsync()).Spec is null);
        await peer.Finished;
    }

    [Test]
    public async Task DisconnectAndUnaryDeadlineKeepAvailableDispatchFacts()
    {
        foreach (var disconnect in new[] { true, false })
        {
            var answered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var peer = new Peer(async socket =>
            {
                await Read(socket);
                await Reply(socket, await Read(socket), "{\"runs\":[]}");
                await answered.Task;
                if (disconnect) socket.Abort();
                else { var cancel = await Read(socket); Check(cancel.GetProperty("method").GetString() == "$/cancelRequest"); }
            });
            // The deadline runs on a manual clock and expires only when advanced.
            var time = new ManualTime();
            using var client = NativeClient.ForHttp(new() { Origin = peer.Origin, Time = time });
            await using var connection = await client.ConnectOecpAsync(peer.Session);
            var list = connection.Runs.ListAsync();
            // Sends are serialized: an answered second call proves the first call's send completed.
            Check((await connection.Runs.ListAsync()).Runs.Length == 0);
            answered.SetResult();
            if (!disconnect) time.Advance(new TransportOptions().RequestTimeout);
            var error = await Failure(list, disconnect ? NativeOecpFailureKind.Transport : NativeOecpFailureKind.Deadline);
            Check(error.Dispatch.SendStarted && error.Dispatch.SendCompleted && !error.Dispatch.ResponseReceived);
            await peer.Finished;
        }
    }

    [Test]
    public async Task ResourceBoundsAndControlReservationAreSharedAcrossConnections()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var peer = new Peer(async socket =>
        {
            var request = await Read(socket); ready.SetResult();
            var cancel = await Read(socket); Check(cancel.GetProperty("method").GetString() == "$/cancelRequest");
            await Reply(socket, request, "{\"runs\":[]}");
        });
        using var client = peer.Client(new() { MaxConcurrentRequests = 2, ReservedControlRequests = 1, MaxOecpConnections = 1 });
        await using var connection = await client.ConnectOecpAsync(peer.Session);
        var handle = connection.CreateRequest(); var call = connection.Runs.ListAsync(handle);
        await ready.Task;
        var capacity = await Failure(connection.Runs.ListAsync(), NativeOecpFailureKind.Capacity);
        Check(!capacity.Dispatch.SendStarted);
        await connection.CancelRequestAsync(handle.Id);
        Check((await call).Runs.Length == 0);
        await Failure(client.ConnectOecpAsync(peer.Session), NativeOecpFailureKind.Capacity);
        await peer.Finished;
    }

    [Test]
    public async Task InitializeAndStatusUseReservedControlCapacity()
    {
        var ordinary = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var peer = new Peer(async socket =>
        {
            var list = await Read(socket); ordinary.SetResult();
            await Reply(socket, await Read(socket), """{"protocolVersion":"openengine.cluster/v1","capabilities":{"graphProfiles":[],"logs":true,"agentAttach":true},"status":{"phase":"empty"}}""");
            var status = await Read(socket);
            Check(status.GetProperty("method").GetString() == "run/status");
            await Reply(socket, status, OecpPeer.Status);
            await Reply(socket, list, "{\"runs\":[]}");
        });
        using var client = peer.Client(new() { MaxConcurrentRequests = 2, ReservedControlRequests = 1 });
        await using var connection = await client.ConnectOecpAsync(peer.Session);
        var pending = connection.Runs.ListAsync();
        await ordinary.Task;
        await Failure(connection.Runs.ListAsync(), NativeOecpFailureKind.Capacity);
        await connection.InitializeAsync();
        Check((await connection.Runs.StatusAsync(new RunId("run-1"))).RunId.Value == "run-1");
        Check((await pending).Runs.Length == 0);
        await peer.Finished;
    }

    [Test]
    public async Task OversizedAndBinaryMessagesHaveExplicitConnectionFailures()
    {
        foreach (var binary in new[] { false, true })
        {
            await using var peer = new Peer(async socket =>
            {
                await Read(socket);
                await socket.SendAsync(Encoding.UTF8.GetBytes(new string('x', 200)), binary ? WebSocketMessageType.Binary : WebSocketMessageType.Text, true, CancellationToken.None);
            });
            using var client = peer.Client(new() { MaxOecpMessageBytes = 128 });
            await using var connection = await client.ConnectOecpAsync(peer.Session);
            await Failure(connection.Runs.ListAsync(), binary ? NativeOecpFailureKind.Protocol : NativeOecpFailureKind.SizeLimit);
            await peer.Finished;
        }
    }

    [Test]
    public async Task SessionAuthorityIsRevalidatedAndBearerIsUsedOnlyOnValidatedDial()
    {
        await using var peer = new Peer(async socket => await Reply(socket, await Read(socket), "{\"runs\":[]}"));
        using var client = peer.Client();
        foreach (var endpoint in new[] { "ws://foreign.example/native-v2/oecp", peer.Session.Endpoint + "?token=x", peer.Session.Endpoint.Replace("ws:", "wss:") })
        {
            try { await client.ConnectOecpAsync(new() { Endpoint = endpoint, BearerToken = "SECRET" }); throw new InvalidOperationException(); }
            catch (ArgumentException) { }
        }
        await using var connection = await client.ConnectOecpAsync(peer.Session with { BearerToken = "session-bearer" });
        await connection.Runs.ListAsync();
        Check(peer.Headers.Contains("Authorization: Bearer session-bearer", StringComparison.OrdinalIgnoreCase));
        await peer.Finished;
    }

    [Test]
    public async Task LivenessFailureIsExposedWithoutPendingCallsAndCanBeDisabled()
    {
        foreach (var enabled in new[] { true, false })
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var peer = new Peer(_ => release.Task);
            using var client = peer.Client(new() { EnableWebSocketLiveness = enabled, WebSocketPingInterval = TimeSpan.FromMilliseconds(150), WebSocketPongTimeout = TimeSpan.FromMilliseconds(200) });
            await using var connection = await client.ConnectOecpAsync(peer.Session);
            if (enabled) Check((await connection.Completion.WaitAsync(TimeSpan.FromSeconds(3)))!.Kind == NativeOecpFailureKind.Transport);
            else { await Task.Delay(500); Check(!connection.Completion.IsCompleted); }
            release.SetResult(); await peer.Finished;
        }
    }

    [Test]
    public async Task PredispatchCancellationAndRequestLimitsDoNotClaimASend()
    {
        await using var peer = new Peer(async socket => await Reply(socket, await Read(socket), "{\"runs\":[]} "));
        using var client = peer.Client(new() { MaxOecpRequestBytes = 100 });
        await using var connection = await client.ConnectOecpAsync(peer.Session);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        try { await connection.Runs.ListAsync(cancellationToken: cancel.Token); throw new InvalidOperationException(); }
        catch (OecpOperationCanceledException error) { Check(!error.Dispatch.SendStarted && !error.Dispatch.ResponseReceived); }
        var oversized = await Failure(connection.InitializeAsync(new() { ProtocolVersion = new string('x', 200) }), NativeOecpFailureKind.SizeLimit);
        Check(!oversized.Dispatch.SendStarted);
        Check((await connection.Runs.ListAsync()).Runs.Length == 0);
        await peer.Finished;
    }

    [Test]
    public async Task ClientDisposalEndsPendingCallsAndDisposalIsIdempotent()
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var peer = new Peer(async socket => { await Read(socket); received.SetResult(); });
        using var client = peer.Client(); var connection = await client.ConnectOecpAsync(peer.Session);
        var call = connection.Runs.ListAsync(); await received.Task;
        client.Dispose();
        try { await call; throw new InvalidOperationException(); }
        catch (OperationCanceledException) { }
        Check(await connection.Completion is null);
        await connection.DisposeAsync();
        try { await connection.CancelRequestAsync(new(1)); throw new InvalidOperationException(); }
        catch (ObjectDisposedException) { }
        await peer.Finished;
    }

    internal static async Task<JsonElement> Read(WebSocket socket)
    {
        using var bytes = new MemoryStream(); var buffer = new byte[4096];
        WebSocketReceiveResult frame;
        do { frame = await socket.ReceiveAsync(buffer, CancellationToken.None); if (frame.MessageType == WebSocketMessageType.Close) throw new InvalidOperationException("Unexpected close"); bytes.Write(buffer, 0, frame.Count); } while (!frame.EndOfMessage);
        using var doc = JsonDocument.Parse(bytes.ToArray()); return doc.RootElement.Clone();
    }
    internal static Task Reply(WebSocket socket, JsonElement request, string result, bool fragmented = false)
        => Send(socket, $$"""{"jsonrpc":"2.0","id":{{request.GetProperty("id")}},"result":{{result}}} """, fragmented);
    internal static async Task Send(WebSocket socket, string value, bool fragmented = false)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (fragmented) { await socket.SendAsync(bytes.AsMemory(0, 7), WebSocketMessageType.Text, false, CancellationToken.None); await socket.SendAsync(bytes.AsMemory(7), WebSocketMessageType.Text, true, CancellationToken.None); }
        else await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    internal sealed class Peer : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private WebSocket? serverSocket;
        private TcpClient? accepted;
        public Uri Origin { get; }
        public TargetOecpSession Session => new() { Endpoint = new UriBuilder(Origin) { Scheme = "ws", Path = "/native-v2/oecp" }.Uri.AbsoluteUri };
        public string Headers { get; private set; } = "";
        public Task Finished { get; }
        public Peer(Func<WebSocket, Task> run)
        {
            listener.Start(); Origin = new($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");
            Finished = Serve(run);
        }
        public NativeClient Client(TransportOptions? options = null) => NativeClient.ForHttp(new() { Origin = Origin, Transport = options ?? new() });
        private async Task Serve(Func<WebSocket, Task> run)
        {
            accepted = await listener.AcceptTcpClientAsync(); var stream = accepted.GetStream();
            var header = new List<byte>(); var one = new byte[1];
            while (!Encoding.ASCII.GetString(header.ToArray()).EndsWith("\r\n\r\n", StringComparison.Ordinal))
            { if (await stream.ReadAsync(one) == 0) throw new InvalidOperationException(); header.Add(one[0]); }
            Headers = Encoding.ASCII.GetString(header.ToArray());
            var key = Headers.Split("\r\n").Single(line => line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)).Split(':', 2)[1].Trim();
            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"));
            serverSocket = WebSocket.CreateFromStream(stream, true, null, Timeout.InfiniteTimeSpan);
            await run(serverSocket);
        }
        public ValueTask DisposeAsync() { serverSocket?.Dispose(); accepted?.Dispose(); listener.Stop(); return ValueTask.CompletedTask; }
    }
}
