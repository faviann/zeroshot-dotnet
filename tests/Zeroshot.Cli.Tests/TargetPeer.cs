using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Zeroshot.Cli.Tests;

/// <summary>
/// A controlled loopback direct target for the CLI process: real HTTP discovery, session and submission routes, and
/// OECP over WebSocket, each answered by a per-test script. It records every mutation it receives, so tests can
/// count sends independently of anything the CLI reports.
/// </summary>
internal sealed class TargetPeer : IAsyncDisposable
{
    public const string Source = """{"repository":"acme/project","branch":"main","revision":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""";
    public const string Running = """{"phase":"running","activeExecutions":[]}""";
    public const string Stopping = """{"phase":"stopping","activeExecutions":[]}""";
    public const string Succeeded = """{"phase":"finished","terminalResult":{"status":"succeeded","output":{"answer":42}}}""";
    public const string Failed = """{"phase":"finished","terminalResult":{"status":"failed","reason":"runtime_failed"}}""";
    public const string ForceStopped = """{"phase":"finished","terminalResult":{"status":"failed","reason":"force_stopped"}}""";

    /// <summary>A run status or force acknowledgement; the title is authored text that error records must not echo.</summary>
    public static string Status(string runId, string cursor, string status)
        => $$"""{"runId":"{{runId}}","title":"{{PrepareTests.Title}}","source":{{Source}},"size":"small","atCursor":"{{cursor}}","status":{{status}}}""";
    public static string WatchEvent(string runId, string cursor, string status)
        => $$"""{"subscriptionId":"w1","runId":"{{runId}}","title":"{{PrepareTests.Title}}","source":{{Source}},"size":"small","cursor":"{{cursor}}","status":{{status}}}""";

    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new(TimeSpan.FromSeconds(90));
    private readonly ConcurrentQueue<string> submissions = new();
    private readonly ConcurrentQueue<string> methods = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource> seen = new();

    public Uri Origin { get; }
    /// <summary>Every submission body received, in order.</summary>
    public IReadOnlyList<string> Submissions => [.. submissions];
    /// <summary>Every OECP method received, in order, across all connections.</summary>
    public IReadOnlyList<string> Methods => [.. methods];
    public int Count(string method) => Methods.Count(m => m == method);

    /// <summary>Answers discovery; replace it to hold the CLI before any mutation can be sent.</summary>
    public Func<Exchange, Task> Discovery { get; set; } = exchange => exchange.ReplyAsync(200,
        """{"kind":"zeroshot.native-v2-target/v2","authentication":"none","runPath":"/native-v2/run","sessionPath":"/native-v2/oecp-session","oecpPath":"/native-v2/oecp","audience":"controller"}""");
    /// <summary>Answers a submission; by default it acknowledges the proposed run.</summary>
    public Func<Exchange, Task> Submit { get; set; } = exchange => exchange.ReplyAsync(200, $$"""{"runId":"{{exchange.ProposedRunId}}"}""");
    /// <summary>OECP answers by method; <c>initialize</c> is built in. An unscripted method fails the connection.</summary>
    public Dictionary<string, Func<Call, Task>> Oecp { get; } = new();

    public TargetPeer()
    {
        listener.Start();
        Origin = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");
        _ = AcceptAsync();
    }

    /// <summary>Completes once a submission (<c>submit</c>), discovery (<c>discover</c>) or OECP method has been received in full.</summary>
    public Task Received(string what) => Signal(what).Task.WaitAsync(TimeSpan.FromSeconds(30));

    private TaskCompletionSource Signal(string what) => seen.GetOrAdd(what, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));

    /// <summary>Never answers; the CLI must end the exchange itself.</summary>
    public Task Hold() => Task.Delay(Timeout.Infinite, stop.Token);

    public static Func<Call, Task> Reply(Func<Call, string> result) => call => call.ReplyAsync(result(call));

    /// <summary>A run/watch that establishes from the requested cursor, sends <paramref name="events"/> and stays open.</summary>
    public static Func<Call, Task> Watch(params Func<Call, string>[] events) => async call =>
    {
        await call.ReplyAsync($$"""{"subscriptionId":"w1","runId":"{{call.RunId}}","atCursor":{{call.Params?["fromCursor"]?.ToJsonString() ?? "\"start\""}}}""");
        foreach (var record in events) await call.NotifyAsync("event", record(call));
    };

    public sealed class Exchange(string body, Func<int, string, Task> reply, Func<Task> hold)
    {
        public string Body { get; } = body;
        public string? ProposedRunId => Body.Length == 0 ? null : JsonNode.Parse(Body)?["runId"]?.GetValue<string>();
        public Task ReplyAsync(int status, string json) => reply(status, json);
        /// <summary>Closes the connection without any response: a lost reply.</summary>
        public Task Drop() => throw new IOException("Scripted lost reply.");
        public Task Hold() => hold();
    }

    public sealed class Call(JsonNode? parameters, Func<string, Task> reply, Func<string, string, Task> notify)
    {
        public JsonNode? Params { get; } = parameters;
        public string RunId => Params!["runId"]!.GetValue<string>();
        public Task ReplyAsync(string result) => reply(result);
        public Task NotifyAsync(string method, string body) => notify(method, body);
    }

    private async Task AcceptAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient accepted;
            try { accepted = await listener.AcceptTcpClientAsync(stop.Token); }
            catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or SocketException) { return; }
            _ = Task.Run(async () =>
            {
                using var client = accepted;
                try { await ServeAsync(client.GetStream()); }
                catch (Exception error) when (error is IOException or WebSocketException or OperationCanceledException or ObjectDisposedException) { }
            });
        }
    }

    private async Task ServeAsync(NetworkStream stream)
    {
        var head = new List<byte>();
        var one = new byte[1];
        while (!(head.Count >= 4 && head[^4] == '\r' && head[^3] == '\n' && head[^2] == '\r' && head[^1] == '\n'))
        {
            if (await stream.ReadAsync(one, stop.Token) == 0) return;
            head.Add(one[0]);
        }
        var lines = Encoding.ASCII.GetString(head.ToArray()).Split("\r\n");
        var route = string.Join(' ', lines[0].Split(' ')[..2]);
        string? Header(string name) => lines.FirstOrDefault(l => l.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))?.Split(':', 2)[1].Trim();

        if (Header("Sec-WebSocket-Key") is { } key)
        {
            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"), stop.Token);
            using var socket = WebSocket.CreateFromStream(stream, isServer: true, null, Timeout.InfiniteTimeSpan);
            await ServeOecpAsync(socket);
            return;
        }

        var body = new byte[int.Parse(Header("Content-Length") ?? "0")];
        await stream.ReadExactlyAsync(body, stop.Token);
        async Task Reply(int status, string json)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status} Status\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"), stop.Token);
            await stream.WriteAsync(bytes, stop.Token);
        }
        var exchange = new Exchange(Encoding.UTF8.GetString(body), Reply, Hold);
        switch (route)
        {
            case "GET /.well-known/zeroshot-native-v2":
                Signal("discover").TrySetResult();
                await Discovery(exchange);
                break;
            case "POST /native-v2/oecp-session":
                await Reply(200, $$"""{"endpoint":"ws://127.0.0.1:{{Origin.Port}}/native-v2/oecp"}""");
                break;
            case "POST /native-v2/run":
                submissions.Enqueue(exchange.Body);
                Signal("submit").TrySetResult();
                await Submit(exchange);
                break;
            default:
                await Reply(404, """{"code":"request.not_found","message":"unscripted route"}""");
                break;
        }
    }

    private async Task ServeOecpAsync(WebSocket socket)
    {
        var writeGate = new SemaphoreSlim(1);
        async Task Send(string text)
        {
            await writeGate.WaitAsync(stop.Token);
            try { await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, stop.Token); }
            finally { writeGate.Release(); }
        }
        var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var received = await socket.ReceiveAsync(chunk, stop.Token);
            if (received.MessageType == WebSocketMessageType.Close) return;
            buffer.Write(chunk, 0, received.Count);
            if (!received.EndOfMessage) continue;
            var message = JsonNode.Parse(buffer.ToArray())!;
            buffer.SetLength(0);
            var method = message["method"]!.GetValue<string>();
            var id = message["id"]?.ToJsonString();
            methods.Enqueue(method);
            Signal(method).TrySetResult();
            var call = new Call(message["params"],
                result => Send($$"""{"jsonrpc":"2.0","id":{{id}},"result":{{result}}}"""),
                (name, body) => Send($$"""{"jsonrpc":"2.0","method":"{{name}}","params":{{body}}}"""));
            if (method == "initialize")
                await call.ReplyAsync("""{"protocolVersion":"openengine.cluster/v1","capabilities":{"graphProfiles":[],"logs":false,"agentAttach":false},"status":{"phase":"empty","observedGeneration":null,"currentRunId":null,"atCursor":null}}""");
            else if (Oecp.TryGetValue(method, out var answer))
                // Answered concurrently, so a held call never blocks the connection's other requests.
                _ = Task.Run(async () =>
                {
                    try { await answer(call); }
                    catch (IOException) { socket.Abort(); } // a scripted lost reply drops the connection
                    catch (Exception error) when (error is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
                });
            else if (method is not ("subscription/cancel" or "$/cancelRequest"))
                throw new IOException($"Unscripted OECP method {method}.");
        }
    }

    public ValueTask DisposeAsync()
    {
        stop.Cancel();
        listener.Stop();
        return ValueTask.CompletedTask;
    }
}
