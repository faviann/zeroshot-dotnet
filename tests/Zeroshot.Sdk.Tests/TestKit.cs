using System.Threading.Channels;
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
