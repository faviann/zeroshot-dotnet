using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Zeroshot.Native.Contracts;
using Zeroshot.Native.Observations;

namespace Zeroshot.Native;

/// <summary>One native dashboard SSE event (profile_ui/runs.rs <c>events</c>), in received order.</summary>
public abstract record DashboardRunEvent
{
    // Pages carry caller JSON and errors carry remote text; neither enters default formatting.
    public sealed override string ToString() => GetType().Name;
}

/// <summary>A <c>history</c> event. Its SSE id was native's <see cref="HistoryPage.NextCursor"/>, which a reopen passes as Last-Event-ID.</summary>
public sealed record DashboardHistoryPageEvent : DashboardRunEvent
{
    public required HistoryPage Page { get; init; }
}

/// <summary>A <c>history_error</c> event with native's <c>{code,message}</c> data. Native ends the stream after it.</summary>
public sealed record DashboardHistoryErrorEvent : DashboardRunEvent
{
    public required UiProblem Problem { get; init; }
    /// <summary>The closed native history category of <see cref="UiProblem.Code"/>, or null for an unknown code.</summary>
    public RunHistoryProblemCode? Code { get; init; }
}

/// <summary>
/// One bounded dashboard SSE observation. Unlike <see cref="NativeSubscription{TEstablishment,TEvent}"/> it has no
/// establishment result, subscription ID, close notification or remote cancellation: the HTTP response is the
/// whole subscription, and disposing it closes only that response. Closure is evidence, never run success.
/// </summary>
public sealed class DashboardRunEvents : IAsyncDisposable
{
    private readonly ObservationQueue<DashboardRunEvent, Cursor> queue;
    private readonly object gate = new();
    private readonly CancellationTokenSource reading = new();
    private readonly TaskCompletionSource<NativeSubscriptionCompletion> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private NativeSubscriptionCompletion? outcome;
    public Task<NativeSubscriptionCompletion> Completion => completion.Task;
    /// <summary>Last page cursor handed to the caller, including pages drained after closure. Not a processing checkpoint.</summary>
    public Cursor? LastDeliveredCursor => queue.LastDeliveredPosition;

    internal DashboardRunEvents(ObservationQueue<DashboardRunEvent, Cursor> queue, HttpResponseMessage response, Stream body,
        Cursor start, int eventBytes)
    {
        this.queue = queue;
        _ = SettleAsync(response, ReadAsync(body, start, eventBytes));
    }

    public async IAsyncEnumerable<DashboardRunEvent> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var reader = queue.ReadAllAsync(cancellationToken).GetAsyncEnumerator();
        while (true)
        {
            bool next;
            try { next = await reader.MoveNextAsync().ConfigureAwait(false); }
            catch (ObservationFailure failure) { throw NativeSubscriptionException.From(failure); }
            if (!next) yield break;
            yield return reader.Current;
        }
    }

    private async Task ReadAsync(Stream body, Cursor after, int eventBytes)
    {
        await Task.Yield();
        NativeSubscriptionOrigin origin;
        NativeSubscriptionException? failure = null;
        try
        {
            var reader = new SseReader(body, eventBytes);
            while (await reader.NextAsync(reading.Token).ConfigureAwait(false) is { } sse)
            {
                DashboardRunEvent record;
                Cursor? position = null;
                if (sse.Type == "history")
                {
                    var page = NativeJson.DeserializeUtf8<HistoryPage>(sse.Data);
                    // The id names the page's own end, and each page continues the previous one without gaps.
                    if (sse.Id != page.NextCursor.Value) throw new JsonException();
                    RunHistoryRules.Page(page, after);
                    after = position = page.NextCursor;
                    record = new DashboardHistoryPageEvent { Page = page };
                }
                else if (sse.Type == "history_error")
                {
                    var problem = NativeJson.DeserializeUtf8<UiProblem>(sse.Data);
                    record = new DashboardHistoryErrorEvent { Problem = problem, Code = RunHistoryProblems.Parse(problem.Code) };
                }
                else throw new JsonException();
                if (!queue.TryEnqueue(record, sse.Data.Length, position)) return;
            }
            origin = NativeSubscriptionOrigin.ServerClosed;
        }
        catch (ObservationFailure overflow)
        {
            origin = NativeSubscriptionOrigin.LocalFailure;
            failure = NativeSubscriptionException.From(overflow);
        }
        catch (Exception) when (reading.IsCancellationRequested) { return; } // Stopped locally; the settle path decides.
        catch (SseSizeException)
        { (origin, failure) = (NativeSubscriptionOrigin.LocalFailure, new(NativeSubscriptionFailureKind.SizeLimit)); }
        catch (Exception error) when (error is JsonException or ArgumentException) // Includes invalid UTF-8.
        { (origin, failure) = (NativeSubscriptionOrigin.LocalFailure, new(NativeSubscriptionFailureKind.Protocol)); }
        catch (Exception)
        {
            // Read failures and a stream ending inside an event; foreign exception text is never retained.
            (origin, failure) = (NativeSubscriptionOrigin.UnexpectedDisconnect, new(NativeSubscriptionFailureKind.UnexpectedDisconnect));
        }
        lock (gate)
        {
            if (outcome is not null) return;
            outcome = new() { Origin = origin, Failure = failure };
            queue.StopReceiving(failure);
        }
    }

    private async Task SettleAsync(HttpResponseMessage response, Task read)
    {
        await queue.StopRequested.ConfigureAwait(false);
        reading.Cancel();
        response.Dispose(); // Closes only this observation's connection, also unblocking a read that ignores cancellation.
        await read.ConfigureAwait(false);
        NativeSubscriptionCompletion result;
        lock (gate)
        {
            outcome ??= new() { Origin = queue.StopFailure is OperationCanceledException
                ? NativeSubscriptionOrigin.Cancelled : NativeSubscriptionOrigin.Disposed };
            result = outcome;
        }
        reading.Dispose();
        queue.Complete();
        completion.TrySetResult(result);
    }

    public async ValueTask DisposeAsync()
    {
        queue.Dispose();
        await Completion.ConfigureAwait(false);
    }

    private sealed class SseSizeException : Exception;

    private sealed record SseEvent(string Type, string? Id, byte[] Data);

    /// <summary>
    /// The SSE framing native (axum Sse) emits: <c>event</c>, <c>id</c> and <c>data</c> fields, comment keepalives,
    /// blank-line dispatch. Also accepts LF, CR or CRLF line ends split anywhere, a leading BOM and multi-line data.
    /// The ceiling bounds one event's data, which native keeps within its 8 MiB page bound. Everything else
    /// since the last dispatch (field names, id, event type, comments, line ends) shares a small fixed allowance.
    /// </summary>
    private sealed class SseReader(Stream stream, int ceiling)
    {
        private const int FramingBytes = 1024;
        private static readonly UTF8Encoding Strict = new(false, true);
        private readonly byte[] buffer = new byte[8192];
        private readonly MemoryStream line = new();
        private readonly MemoryStream data = new();
        private int start, end;
        private long framing;
        private bool afterCr, firstLine = true, hasData;
        private string? type, id;

        public async Task<SseEvent?> NextAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                if (start == end)
                {
                    end = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    start = 0;
                    if (end == 0)
                    {
                        if (line.Length != 0 || framing != 0 || hasData) throw new EndOfStreamException(); // Truncated inside an event.
                        return null;
                    }
                }
                if (afterCr && buffer[start] == '\n') { start++; afterCr = false; continue; }
                afterCr = false;
                var span = buffer.AsSpan(start, end - start);
                var at = span.IndexOfAny((byte)'\r', (byte)'\n');
                var count = at < 0 ? span.Length : at + 1;
                // A data line is at most its field name plus one event's data; the line's own split is checked below.
                if (line.Length + count > (long)ceiling + FramingBytes) throw new SseSizeException();
                line.Write(span[..(at < 0 ? span.Length : at)]);
                start += count;
                if (at < 0) continue;
                afterCr = span[at] == '\r';
                if (Line() is { } dispatched) return dispatched;
            }
        }

        private SseEvent? Line()
        {
            var bytes = line.GetBuffer().AsSpan(0, (int)line.Length);
            if (firstLine && bytes.StartsWith("\uFEFF"u8)) bytes = bytes[3..];
            firstLine = false;
            try
            {
                if (bytes.IsEmpty) return Dispatch();
                if (bytes[0] == ':')
                {
                    framing += bytes.Length + 1;
                    if (framing > FramingBytes) throw new SseSizeException();
                    return null;
                }
                var colon = bytes.IndexOf((byte)':');
                var field = colon < 0 ? bytes : bytes[..colon];
                var value = colon < 0 ? Span<byte>.Empty : bytes[(colon + 1)..];
                if (value.StartsWith(" "u8)) value = value[1..];
                var isData = field.SequenceEqual("data"u8);
                // The line end and everything but data content is framing.
                framing += bytes.Length + 1 - (isData ? value.Length : 0);
                if (framing > FramingBytes) throw new SseSizeException();
                if (isData)
                {
                    if (hasData) data.WriteByte((byte)'\n');
                    data.Write(value);
                    hasData = true;
                    if (data.Length > ceiling) throw new SseSizeException();
                }
                else if (field.SequenceEqual("event"u8)) type = Strict.GetString(value);
                else if (field.SequenceEqual("id"u8) && value.IndexOf((byte)0) < 0) id = Strict.GetString(value);
                return null;
            }
            finally { line.SetLength(0); }
        }

        private SseEvent? Dispatch()
        {
            var result = hasData ? new SseEvent(type ?? "message", id, data.ToArray()) : null;
            (type, id, hasData, framing) = (null, null, false, 0);
            data.SetLength(0);
            return result;
        }
    }
}
