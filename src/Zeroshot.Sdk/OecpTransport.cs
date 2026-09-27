using System.Net.WebSockets;

namespace Zeroshot.Native;

/// <summary>Message framing beneath one OECP connection. Correlation, validation and subscriptions stay in the connection.</summary>
internal interface IOecpTransport
{
    /// <summary>Whether the native binding intercepts $/cancelRequest. NDJSON treats it as an invalid request.</summary>
    bool SupportsCancelRequest { get; }
    ValueTask SendAsync(byte[] message, CancellationToken token);
    /// <summary>Returns one complete message or throws <see cref="OecpConnection.ConnectionInterrupted"/>.</summary>
    Task<byte[]> ReceiveAsync(int ceiling, CancellationToken token);
    /// <summary>Releases owned resources and interrupts pending owned I/O.</summary>
    void Abort();
}

internal sealed class WebSocketTransport(ClientWebSocket socket, HttpMessageInvoker invoker) : IOecpTransport
{
    private byte[]? buffer;
    public bool SupportsCancelRequest => true;

    public ValueTask SendAsync(byte[] message, CancellationToken token)
        => socket.SendAsync(message.AsMemory(), WebSocketMessageType.Text, true, token);

    public async Task<byte[]> ReceiveAsync(int ceiling, CancellationToken token)
    {
        buffer ??= new byte[Math.Min(8192, ceiling)];
        using var body = new MemoryStream();
        ValueWebSocketReceiveResult frame;
        do
        {
            frame = await socket.ReceiveAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, ceiling - body.Length + 1)), token).ConfigureAwait(false);
            if (frame.MessageType == WebSocketMessageType.Close) throw new OecpConnection.ConnectionInterrupted(NativeOecpFailureKind.Transport);
            if (frame.MessageType != WebSocketMessageType.Text) throw new OecpConnection.ConnectionInterrupted(NativeOecpFailureKind.Protocol);
            if (body.Length + frame.Count > ceiling) throw new OecpConnection.ConnectionInterrupted(NativeOecpFailureKind.SizeLimit);
            body.Write(buffer, 0, frame.Count);
        } while (!frame.EndOfMessage);
        return body.ToArray();
    }

    public void Abort() { socket.Abort(); socket.Dispose(); invoker.Dispose(); }
}

/// <summary>Native NDJSON framing: one JSON-RPC message per newline-terminated line, with no heartbeat or idle deadline.</summary>
internal sealed class NdjsonTransport(Stream input, Stream output, bool leaveOpen) : IOecpTransport
{
    private readonly byte[] buffer = new byte[8192];
    private int start, end;
    public bool SupportsCancelRequest => false;

    public async ValueTask SendAsync(byte[] message, CancellationToken token)
    {
        // Serialized JSON never contains a raw newline, so the frame cannot split.
        var frame = new byte[message.Length + 1];
        message.CopyTo(frame, 0);
        frame[^1] = (byte)'\n';
        await output.WriteAsync(frame, token).ConfigureAwait(false);
        await output.FlushAsync(token).ConfigureAwait(false);
    }

    public async Task<byte[]> ReceiveAsync(int ceiling, CancellationToken token)
    {
        using var line = new MemoryStream();
        while (true)
        {
            var newline = Array.IndexOf(buffer, (byte)'\n', start, end - start);
            var count = (newline < 0 ? end : newline) - start;
            if (line.Length + count > ceiling) throw new OecpConnection.ConnectionInterrupted(NativeOecpFailureKind.SizeLimit);
            line.Write(buffer, start, count);
            if (newline >= 0) { start = newline + 1; return line.ToArray(); }
            start = 0;
            end = await input.ReadAsync(buffer, token).ConfigureAwait(false);
            // EOF is a disconnect, with or without a partial frame; it never completes a call or run.
            if (end == 0) throw new OecpConnection.ConnectionInterrupted(NativeOecpFailureKind.Transport);
        }
    }

    public void Abort()
    {
        if (leaveOpen) return;
        // A failing caller stream cannot keep the connection from completing.
        try { input.Dispose(); }
        catch (Exception) { }
        if (ReferenceEquals(input, output)) return;
        try { output.Dispose(); }
        catch (Exception) { }
    }
}
