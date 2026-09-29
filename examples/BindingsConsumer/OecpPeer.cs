using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

/// <summary>
/// A controlled OECP peer. Cluster methods answer with the pinned native request/response goldens; run methods,
/// subscriptions and notifications answer with the source-backed records the SDK tests use. It serves NDJSON
/// (Unix socket) and WebSocket connections with the same responder.
/// </summary>
sealed class OecpPeer
{
    public const string Source = """{"repository":"acme/project","branch":"main","revision":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""";
    public const string Status = """{"runId":"run-1","title":"test","source":""" + Source + ""","size":"small","atCursor":"cursor-1","status":{"phase":"finished","terminalResult":{"status":"failed","reason":"test"}},"workspaceRecovery":{"recoverable":true}}""";
    const string Forced = """{"runId":"run-1","title":"test","source":""" + Source + ""","size":"small","atCursor":"cursor-2","status":{"phase":"finished","terminalResult":{"status":"failed","reason":"force_stopped"}}}""";
    const string RunWatchRecord = """{"subscriptionId":"watch","runId":"run-1","title":"title","source":""" + Source + ""","size":"small","cursor":"watch-end","status":{"phase":"finished","terminalResult":{"status":"succeeded","output":{"answer":42}}}}""";
    const string RunLogRecord = """{"subscriptionId":"logs","runId":"run-1","cursor":"log-end","timestamp":1234567,"execution":"worker:1","record":{"level":"warn","target":"environment.setup","message":"retained text"}}""";
    const string Attach = """{"subscriptionId":"attach","runId":"run-1","execution":"worker:1"}""";
    const string Checkpoint = """{"checkpointId":"opaque/a","sequence":1,"node":"worker","mapIndices":[0],"loopIterations":[],"createdAt":1700000000000}""";

    private readonly string fixtures;
    private readonly Dictionary<string, (JsonNode Params, JsonNode Result)> goldens = new();
    private readonly object gate = new();
    private readonly HashSet<string> cancelledSubscriptions = new();
    private readonly List<string> cancelledRequests = new();
    private readonly List<string> methods = new();

    /// <summary>Status bodies by run ID; other runs answer with <see cref="Status"/>.</summary>
    public Dictionary<string, string> Statuses { get; } = new();
    /// <summary>Every received method, in order, across all connections.</summary>
    public IReadOnlyList<string> Methods { get { lock (gate) return methods.ToList(); } }

    /// <summary>One received request. Throwing <see cref="IOException"/> from a script drops its connection without a close.</summary>
    public sealed record Request(int Connection, string Method, JsonNode? Params, Func<string, Task> Reply, Func<string, string, Task> Notify);
    /// <summary>Answers a request before the fixed responses when it returns true.</summary>
    public Func<Request, Task<bool>>? Script { get; set; }
    private int connections;

    public OecpPeer(string fixtures)
    {
        this.fixtures = fixtures;
        // Earlier files win: admission-lifecycle supplies initialize/plan/apply/get, then the lifecycle goldens.
        foreach (var file in new[] { "admission-lifecycle.ndjson", "lifecycle-controls.ndjson", "lifecycle-delete.ndjson", "lifecycle-resubmit.ndjson" })
        {
            var lines = File.ReadLines(Path.Combine(fixtures, "cluster", file)).Where(l => l.Length > 0).Select(l => JsonNode.Parse(l)!).ToList();
            foreach (var request in lines.Where(l => l["method"] is not null))
            {
                var response = lines.Single(l => l["method"] is null && l["id"]!.ToJsonString() == request["id"]!.ToJsonString());
                goldens.TryAdd(request["method"]!.GetValue<string>(), (request["params"]!, response["result"]!));
            }
        }
    }

    /// <summary>The golden native request parameters for a cluster method.</summary>
    public byte[] GoldenParams(string method) => Encoding.UTF8.GetBytes(goldens[method].Params.ToJsonString());

    public bool SawSubscriptionCancel(string subscriptionId) { lock (gate) return cancelledSubscriptions.Contains(subscriptionId); }
    public IReadOnlyList<string> CancelledRequests { get { lock (gate) return cancelledRequests.ToList(); } }

    public async Task ServeAsync(Func<Task<string?>> read, Func<string, Task> send)
    {
        var connection = Interlocked.Increment(ref connections);
        while (await read() is { } line)
        {
            var message = JsonNode.Parse(line)!;
            var method = message["method"]!.GetValue<string>();
            var id = message["id"]?.ToJsonString();
            var parameters = message["params"];
            lock (gate) methods.Add(method);
            Task Reply(string result) => send($$"""{"jsonrpc":"2.0","id":{{id}},"result":{{result}}}""");
            Task Notify(string name, string body) => send($$"""{"jsonrpc":"2.0","method":"{{name}}","params":{{body}}}""");
            async Task Session(string establishment, IEnumerable<string> events, string? closed)
            {
                await Reply(establishment);
                foreach (var record in events) await Notify("event", record);
                if (closed is not null) await Notify("subscription/closed", closed);
            }
            IEnumerable<string> Records(string file) => JsonNode.Parse(File.ReadAllText(Path.Combine(fixtures, "cluster", file)))!.AsArray().Select(n => n!.ToJsonString());
            if (Script is { } script && await script(new(connection, method, parameters, Reply, Notify))) continue;
            // Echo the requested position; a request from the start has one of its own.
            var at = parameters?["fromCursor"]?.ToJsonString() ?? "\"start\"";

            switch (method)
            {
                case "initialize" or "plan" or "apply" or "update" or "stop" or "retry" or "resubmit" or "delete" or "get":
                    await Reply(goldens[method].Result.ToJsonString());
                    break;
                case "watch":
                    await Session("""{"subscriptionId":"sub-1","runId":"run-1","atCursor":"cursor-3"}""", Records("watch-session.json"),
                        """{"subscriptionId":"sub-1","reason":"done","lastDeliveredCursor":"cursor-3"}""");
                    break;
                case "logs":
                    await Session("""{"subscriptionId":"sub-1"}""", Records("logs-session.json"), """{"subscriptionId":"sub-1","reason":"done"}""");
                    break;
                case "agent/attach":
                    await Session("""{"subscriptionId":"sub-1"}""", Records("agent-attach-session.json"), """{"subscriptionId":"sub-1","reason":"done"}""");
                    break;
                case "run/submit":
                    await Reply($$"""{"runId":{{parameters!["runId"]!.ToJsonString()}}}""");
                    break;
                case "run/list":
                    await Reply($$"""{"runs":[{{Status}}]}""");
                    break;
                case "run/status":
                    await Reply(Statuses.GetValueOrDefault(parameters!["runId"]!.GetValue<string>(), Status));
                    break;
                case "$/cancelRequest":
                    lock (gate) cancelledRequests.Add(parameters!["id"]!.ToJsonString());
                    break;
                case "run/watch":
                    await Session($$"""{"subscriptionId":"watch","runId":"run-1","atCursor":{{at}}}""", [RunWatchRecord],
                        """{"subscriptionId":"watch","reason":"done","lastDeliveredCursor":"watch-end"}""");
                    break;
                case "run/logs":
                    // A "held" subscription stays open so that the caller's disposal sends subscription/cancel.
                    var open = parameters!["fromCursor"]?.GetValue<string>() == "held";
                    await Session($$"""{"subscriptionId":"logs","runId":"run-1","atCursor":{{at}}}""", [RunLogRecord],
                        open ? null : """{"subscriptionId":"logs","reason":"done","lastDeliveredCursor":"log-end"}""");
                    break;
                case "run/attach" when parameters!["execution"]!.GetValue<string>() == "held":
                    // A live attachment that stays open after one event, so the caller ends it.
                    var held = $$"""{"subscriptionId":"held","runId":{{parameters["runId"]!.ToJsonString()}},"execution":"held"}""";
                    await Session(held, [held[..^1] + ""","event":{"type":"working"}}"""], null);
                    break;
                case "run/attach":
                    await Session(Attach, new[] { """{"type":"working"}""", """{"type":"output","text":"visible output"}""", """{"type":"settled"}""" }
                        .Select(e => Attach[..^1] + ",\"event\":" + e + "}"), """{"subscriptionId":"attach","reason":"done"}""");
                    break;
                case "run/force":
                    await Reply(Forced);
                    break;
                case "run/checkpoints":
                    await Reply($$"""{"runId":"run-1","checkpoints":[{{Checkpoint}}],"nextAfter":"opaque/a"}""");
                    break;
                case "run/resume":
                    await Reply($$"""{"runId":{{parameters!["successorRunId"]!.ToJsonString()}},"resumedFrom":"run-1"}""");
                    break;
                case "run/discard_workspace":
                    await Reply("""{"runId":"run-1","discarded":true}""");
                    break;
                case "subscription/cancel":
                    lock (gate) cancelledSubscriptions.Add(parameters!["subscriptionId"]!.GetValue<string>());
                    break;
                default:
                    throw new InvalidOperationException($"The controlled peer has no answer for {method}.");
            }
        }
    }

    /// <summary>Accepts NDJSON connections on a Unix socket, as a native portable controller would.</summary>
    public Task ListenUnixAsync(Socket listener, CancellationToken token) => Task.Run(async () =>
    {
        while (!token.IsCancellationRequested)
        {
            Socket accepted;
            try { accepted = await listener.AcceptAsync(token); } catch (OperationCanceledException) { return; }
            _ = Task.Run(async () =>
            {
                await using var stream = new NetworkStream(accepted, ownsSocket: true);
                using var reader = new StreamReader(stream, new UTF8Encoding(false));
                var writeGate = new SemaphoreSlim(1);
                try
                {
                    await ServeAsync(() => reader.ReadLineAsync(token).AsTask(), async text =>
                    {
                        await writeGate.WaitAsync(token);
                        try { await stream.WriteAsync(Encoding.UTF8.GetBytes(text + "\n"), token); } finally { writeGate.Release(); }
                    });
                }
                catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
            }, token);
        }
    }, token);

    /// <summary>Serves every WebSocket upgrade accepted on a loopback TCP listener until cancelled.</summary>
    public Task ListenWebSocketsAsync(TcpListener listener, CancellationToken token) => Task.Run(async () =>
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient accepted;
            try { accepted = await listener.AcceptTcpClientAsync(token); } catch (OperationCanceledException) { return; }
            _ = ServeWebSocketAsync(accepted, token);
        }
    }, token);

    /// <summary>Accepts one WebSocket upgrade on a loopback TCP listener and serves OECP text messages.</summary>
    public async Task ServeWebSocketAsync(TcpListener listener, CancellationToken token)
        => await ServeWebSocketAsync(await listener.AcceptTcpClientAsync(token), token);

    private async Task ServeWebSocketAsync(TcpClient accepted, CancellationToken token)
    {
        using var client = accepted;
        var stream = client.GetStream();
        var header = new List<byte>();
        var one = new byte[1];
        while (!Encoding.ASCII.GetString(header.ToArray()).EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (await stream.ReadAsync(one, token) == 0) return;
            header.Add(one[0]);
        }
        var key = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n")
            .Single(l => l.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)).Split(':', 2)[1].Trim();
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"), token);
        using var socket = WebSocket.CreateFromStream(stream, true, null, Timeout.InfiniteTimeSpan);
        var writeGate = new SemaphoreSlim(1);
        try
        {
            await ServeAsync(async () =>
            {
                var buffer = new MemoryStream();
                var chunk = new byte[16 * 1024];
                while (true)
                {
                    var received = await socket.ReceiveAsync(chunk, token);
                    if (received.MessageType == WebSocketMessageType.Close) return null;
                    buffer.Write(chunk, 0, received.Count);
                    if (received.EndOfMessage) return Encoding.UTF8.GetString(buffer.ToArray());
                }
            }, async text =>
            {
                await writeGate.WaitAsync(token);
                try { await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, token); } finally { writeGate.Release(); }
            });
        }
        catch (Exception error) when (error is WebSocketException or IOException or OperationCanceledException) { }
    }

    public static TcpListener LoopbackListener()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return listener;
    }
}
