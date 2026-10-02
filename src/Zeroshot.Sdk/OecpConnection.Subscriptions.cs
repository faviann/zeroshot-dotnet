using System.Text;
using System.Text.Json;
using Zeroshot.Native.Contracts;
using Zeroshot.Native.Execution;
using Zeroshot.Native.Observations;

namespace Zeroshot.Native;

public sealed partial class OecpConnection
{
    internal async Task<NativeSubscription<TEstablishment, TEvent>> SubscribeAsync<TEstablishment, TEvent>(
        string method, byte[] parameters, Func<TEstablishment, SubscriptionId> validateEstablishment,
        Func<SubscriptionId, TEvent, Cursor?> validateEvent, CancellationToken cancellationToken, bool cursorlessClose = false)
    {
        ObservationQueue<TEvent, Cursor> queue;
        try { queue = observations.Open<TEvent, Cursor>(cancellationToken); }
        catch (ObservationFailure failure) { throw NativeSubscriptionException.From(failure); }
        NativeSubscription<TEstablishment, TEvent>? subscription = null;
        try
        {
            await CallAsync<TEstablishment>(method, parameters, null, cancellationToken, register: result =>
            {
                var id = validateEstablishment(result);
                if (subscriptions.ContainsKey(id)) throw new JsonException();
                subscription = new(result, id, queue, record => validateEvent(id, record), DetachAsync, cursorlessClose);
                subscriptions.Add(id, subscription);
                subscription.Start();
            }).ConfigureAwait(false);
            return subscription!;
        }
        catch
        {
            if (subscription is not null) await subscription.DisposeAsync().ConfigureAwait(false);
            else { queue.Dispose(); queue.Complete(); }
            throw;
        }
    }

    private void ReceiveNotification(JsonElement root, int encodedBytes)
    {
        if (root.TryGetProperty("id", out _) || root.TryGetProperty("result", out _) || root.TryGetProperty("error", out _) ||
            root.GetProperty("method").ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("params", out var parameters) || parameters.ValueKind != JsonValueKind.Object ||
            parameters.EnumerateObject().Select(p => p.Name).Distinct().Count() != parameters.EnumerateObject().Count() ||
            !parameters.TryGetProperty("subscriptionId", out var wireId) || wireId.ValueKind != JsonValueKind.String)
            throw new JsonException();
        var method = root.GetProperty("method").GetString();
        if (method is not ("event" or "subscription/closed")) throw new JsonException();
        var id = new SubscriptionId(wireId.GetString()!);
        lock (gate)
        {
            // Cancellation has no acknowledgement. Late frames for a detached ID and
            // foreign IDs are never delivered; no unbounded retired-ID table is retained.
            if (!subscriptions.TryGetValue(id, out var subscription)) return;
            if (method == "event") subscription.Receive(parameters, encodedBytes);
            else
            {
                var closed = NativeJson.DeserializeUtf8<SubscriptionClosedNotification>(Encoding.UTF8.GetBytes(parameters.GetRawText()));
                if (subscription.CursorlessClose && closed.LastDeliveredCursor is not null) throw new JsonException();
                subscriptions.Remove(id);
                subscription.Closed(closed);
            }
        }
    }

    private async Task DetachAsync(SubscriptionId id, bool cancelRemote)
    {
        lock (gate)
        {
            subscriptions.Remove(id);
            if (closed || !cancelRemote) return;
        }
        var bytes = NotificationBytes("subscription/cancel", NativeJson.SerializeUtf8(new SubscriptionCancelParams { SubscriptionId = id }));
        try
        {
            await executor.ExecuteAsync(new("subscription/cancel", OperationTransport.Oecp, isControl: true), bytes.Length,
                async context => { await SendAsync(bytes, new Pending(null), context.CancellationToken).ConfigureAwait(false); return true; },
                enclosingBudget: limits.CleanupTimeout).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A failed cancel cannot leave unbounded remote streams on a reusable socket.
            Close(NativeOecpFailureKind.Transport);
        }
    }

    private static byte[] NotificationBytes(string method, byte[] parameters)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject(); writer.WriteString("jsonrpc", "2.0"); writer.WriteString("method", method);
            writer.WritePropertyName("params"); writer.WriteRawValue(parameters); writer.WriteEndObject();
        }
        return output.ToArray();
    }
}

public sealed partial class OecpRunsClient
{
    /// <summary>One live read-only attachment to an exact active execution. No cursor, replay, input or automatic reopen.</summary>
    public Task<NativeSubscription<RunAttachResult, RunAttachEventNotification>> AttachAsync(RunAttachParams parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return connection.SubscribeAsync<RunAttachResult, RunAttachEventNotification>("run/attach", NativeJson.SerializeUtf8(parameters), result =>
        {
            parameters.Require(result);
            return result.SubscriptionId;
        }, (id, record) =>
        {
            parameters.Require(record, id);
            return null;
        }, cancellationToken);
    }

    /// <summary>One durable watch, replayed exclusively after FromCursor then followed live. No automatic reopen.</summary>
    public Task<NativeSubscription<RunWatchResult, RunWatchEventNotification>> WatchAsync(RunWatchParams parameters,
        ResolvedSource? expectedSource = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (expectedSource is not null) _ = NativeJson.SerializeUtf8(expectedSource);
        var source = expectedSource;
        return connection.SubscribeAsync<RunWatchResult, RunWatchEventNotification>("run/watch", NativeJson.SerializeUtf8(parameters), result =>
        {
            parameters.Require(result);
            return result.SubscriptionId;
        }, (id, record) =>
        {
            source ??= record.Source;
            parameters.Require(record, id, source);
            return record.Cursor;
        }, cancellationToken);
    }

    /// <summary>One retained-log replay followed live, optionally restricted to one exact execution.</summary>
    public Task<NativeSubscription<RunLogsResult, RunLogEventNotification>> LogsAsync(RunLogsParams parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return connection.SubscribeAsync<RunLogsResult, RunLogEventNotification>("run/logs", NativeJson.SerializeUtf8(parameters), result =>
        {
            parameters.Require(result);
            return result.SubscriptionId;
        }, (id, record) =>
        {
            parameters.Require(record, id);
            return record.Cursor;
        }, cancellationToken);
    }
}
