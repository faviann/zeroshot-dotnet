using System.Runtime.InteropServices;
using System.Text.Json;
using Zeroshot.Native.Contracts;
using Zeroshot.Native.Observations;

namespace Zeroshot.Native;

/// <summary>
/// One bounded hosted NDJSON watch or log stream. The HTTP response is the whole subscription: there is no
/// establishment result or remote cancellation, and disposing it closes only that response. A received
/// <c>closed</c> frame ends it as <see cref="NativeSubscriptionOrigin.ServerClosed"/>: <c>done</c> with no failure,
/// <c>SLOW_CONSUMER</c> or <c>SOURCE_UNAVAILABLE</c> with that failure kind. EOF without that frame is an
/// unexpected disconnect. Closure is evidence, never run success.
/// </summary>
public sealed class HostedRunStream<TEvent> : IAsyncDisposable
{
    private readonly ObservationLifecycle<TEvent> observation;
    public Task<NativeSubscriptionCompletion> Completion => observation.Completion;
    /// <summary>Last cursor handed to the caller, including records drained after closure. Not a processing checkpoint.</summary>
    public Cursor? LastDeliveredCursor => observation.LastDeliveredCursor;

    internal HostedRunStream(ObservationQueue<TEvent, Cursor> queue, HttpResponseMessage response, Stream body,
        int frameBytes, Func<TEvent, Cursor> validate)
    {
        observation = new(queue);
        observation.Start(response, token => ReadAsync(observation, body, frameBytes, validate, token));
    }

    public IAsyncEnumerable<TEvent> ReadAllAsync(CancellationToken cancellationToken = default)
        => observation.ReadAllAsync(cancellationToken);

    public ValueTask DisposeAsync() => observation.DisposeAsync();

    // Native HostedRunStreamFrame: strict {"type":"event","event":E} or {"type":"closed","reason":R}.
    private static async Task<NativeSubscriptionException?> ReadAsync(ObservationLifecycle<TEvent> observation, Stream body,
        int frameBytes, Func<TEvent, Cursor> validate, CancellationToken cancellationToken)
    {
        var reader = new FrameReader(body, frameBytes);
        while (await reader.NextAsync(cancellationToken).ConfigureAwait(false) is { } frame)
        {
            using var document = JsonDocument.Parse(frame, new JsonDocumentOptions { MaxDepth = 128 }); // An empty line fails here.
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2 ||
                !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
                throw new JsonException();
            // The raw value bytes go to NativeJson, which also rejects malformed UTF-8 inside them.
            if (type.ValueEquals("event") && root.TryGetProperty("event", out var wire))
            {
                var record = NativeJson.DeserializeUtf8<TEvent>(JsonMarshal.GetRawUtf8Value(wire));
                observation.Enqueue(record, frame.Length, validate(record));
            }
            else if (type.ValueEquals("closed") && root.TryGetProperty("reason", out var reason))
                return ObservationLifecycle<TEvent>.CloseFailure(
                    NativeJson.DeserializeUtf8<SubscriptionCloseReason>(JsonMarshal.GetRawUtf8Value(reason)));
            else throw new JsonException();
        }
        // Native follow loops reconnect after EOF without a closed frame; it never completes the subscription.
        throw new EndOfStreamException();
    }

    /// <summary>Native hosted framing: one frame per LF, one preceding CR removed, at most the ceiling in bytes.</summary>
    private sealed class FrameReader(Stream stream, int ceiling)
    {
        private readonly byte[] buffer = new byte[8192];
        private readonly MemoryStream line = new();
        private int start, end;

        /// <summary>Returns the next frame, or null at EOF on a frame boundary.</summary>
        public async Task<byte[]?> NextAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                if (start == end)
                {
                    end = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    start = 0;
                    if (end == 0) return line.Length == 0 ? null : throw new EndOfStreamException(); // A partial frame is a disconnect.
                }
                var span = buffer.AsSpan(start, end - start);
                var at = span.IndexOf((byte)'\n');
                var count = at < 0 ? span.Length : at;
                // Leave room for the CR of a CRLF line end.
                if (line.Length + count > (long)ceiling + 1) throw new ObservationFrameTooLarge();
                line.Write(span[..count]);
                start += at < 0 ? count : count + 1;
                if (at < 0) continue;
                var frame = line.ToArray();
                line.SetLength(0);
                if (frame.Length > 0 && frame[^1] == '\r') frame = frame[..^1];
                return frame.Length > ceiling ? throw new ObservationFrameTooLarge() : frame;
            }
        }
    }
}
