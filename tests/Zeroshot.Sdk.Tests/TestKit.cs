global using static Zeroshot.Client.Tests.TestKit;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Zeroshot.Native;
using Zeroshot.Native.Contracts;

namespace Zeroshot.Client.Tests;

// Shared HTTP test doubles. Calls counts every request the client hands to the handler.
internal sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    private int calls;
    public int Calls => Volatile.Read(ref calls);
    public bool Disposed { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    { Interlocked.Increment(ref calls); return send(request, cancellationToken); }
    protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
}

// A response body fed chunk by chunk, like a socket: reads wait for data, honor cancellation and end on disposal.
internal sealed class FeedStream : Stream
{
    private readonly Channel<byte[]?> chunks = Channel.CreateUnbounded<byte[]?>();
    private readonly CancellationTokenSource closed = new();
    private byte[] current = [];
    private int offset;
    public bool Disposed => closed.IsCancellationRequested;
    public void Write(byte[] chunk) => chunks.Writer.TryWrite(chunk);
    public void End() => chunks.Writer.TryComplete();
    public void Fail() => chunks.Writer.TryComplete(new IOException("reset"));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, closed.Token);
        while (offset == current.Length)
        {
            if (!await chunks.Reader.WaitToReadAsync(linked.Token)) return 0;
            chunks.Reader.TryRead(out var next);
            (current, offset) = (next ?? [], 0);
        }
        var count = Math.Min(buffer.Length, current.Length - offset);
        current.AsMemory(offset, count).CopyTo(buffer);
        offset += count;
        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    protected override void Dispose(bool disposing) { closed.Cancel(); base.Dispose(disposing); }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

internal static class TestDiscovery
{
    /// <summary>Valid native-v2 controller discovery with the fixed target paths and no capabilities.</summary>
    public static TargetDiscoveryDocument Controller(TargetAuthentication authentication) => new()
    {
        Kind = "zeroshot.native-v2-target/v2", Audience = "controller", Authentication = authentication,
        RunPath = "/native-v2/run", SessionPath = "/native-v2/oecp-session", OecpPath = "/native-v2/oecp"
    };
}

// Assertions and fixtures shared by every test file (imported by the global using above).
internal static class TestKit
{
    /// <summary>Fails with the asserted expression so a bare check still says what broke.</summary>
    public static void Check(bool condition, string? message = null, [CallerArgumentExpression(nameof(condition))] string? expression = null)
    { if (!condition) throw new InvalidOperationException(message is null ? $"Check failed: {expression}" : $"{message} (check: {expression})"); }

    // Not "Client": inside namespace Zeroshot.Client.Tests that name binds to the Zeroshot.Client namespace first.
    public static NativeClient ClientFor(Handler handler, TransportOptions? transport = null) => NativeClient.ForHttp(
        new NativeClientOptions { Origin = new Uri("https://target.example/"), Transport = transport ?? new() },
        new HttpClient(handler), ownsHttpClient: true);

    /// <summary>A JSON response ("application/json; charset=utf-8").</summary>
    public static HttpResponseMessage Reply(HttpRequestMessage request, string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { RequestMessage = request, Content = new StringContent(body, Encoding.UTF8, "application/json") };
    /// <summary>The same body labelled "text/plain; charset=utf-8", for routes whose parsing must not depend on Content-Type.</summary>
    public static HttpResponseMessage TextReply(HttpRequestMessage request, string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { RequestMessage = request, Content = new StringContent(body) };

    public static async Task<TException> Failure<TException>(Func<Task> action, Func<TException, bool> expected) where TException : Exception
    {
        try { await action(); }
        catch (TException error) { Check(expected(error), error.ToString()); return error; }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }
    public static Task<NativeHttpException> Failure(Task task, NativeHttpFailureKind kind) => Failure<NativeHttpException>(() => task, error => error.Kind == kind);
    public static Task<NativeOecpException> Failure(Task task, NativeOecpFailureKind kind) => Failure<NativeOecpException>(() => task, error => error.Kind == kind);
    public static Task<NativeSubscriptionException> Failure(Func<Task> action, NativeSubscriptionFailureKind kind)
        => Failure<NativeSubscriptionException>(action, error => error.Kind == kind);

    /// <summary>Caller input refused before anything is sent, without echoing any of <paramref name="secrets"/>.</summary>
    public static async Task Invalid(Func<Task> action, params string[] secrets)
    {
        try { await action(); }
        catch (Exception error) when (error is ArgumentException or JsonException)
        { var text = error.ToString(); Check(!secrets.Any(text.Contains), $"Invalid-input error echoed a secret: {text}"); return; }
        throw new InvalidOperationException("Expected invalid caller input.");
    }
    public static void Invalid(Action action)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("Expected ArgumentException.");
    }

    public static async Task<T> Throws<T>(Task task, int seconds = 5) where T : Exception
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(seconds)); }
        catch (T error) { return error; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}
