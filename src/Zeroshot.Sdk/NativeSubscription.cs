using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Zeroshot.Native.Contracts;
using Zeroshot.Native.Observations;

namespace Zeroshot.Native;

public enum NativeSubscriptionOrigin { ServerClosed, Disposed, Cancelled, LocalFailure, UnexpectedDisconnect }
public enum NativeSubscriptionFailureKind
{
    Admission, RecordLimit, StreamByteLimit, AggregateByteLimit, Protocol, SizeLimit,
    UnexpectedDisconnect, SlowConsumer, SourceUnavailable
}

/// <summary>Safe observation failure metadata. Record bodies and cursors never enter exception formatting.</summary>
public sealed class NativeSubscriptionException : Exception
{
    public NativeSubscriptionFailureKind Kind { get; }
    public Guid CorrelationId { get; }
    internal NativeSubscriptionException(NativeSubscriptionFailureKind kind, Guid? correlationId = null)
        : base($"Native subscription ({correlationId ??= Guid.NewGuid():D}) failed: {kind}.")
    { Kind = kind; CorrelationId = correlationId.Value; }
    internal static NativeSubscriptionException From(ObservationFailure failure)
        => new(Enum.Parse<NativeSubscriptionFailureKind>(failure.Kind.ToString()), failure.CorrelationId);
}

/// <summary>One subscription's closure evidence, never a claim of native run success.</summary>
public sealed record NativeSubscriptionCompletion
{
    public required NativeSubscriptionOrigin Origin { get; init; }
    public SubscriptionClosedNotification? ServerClose { get; init; }
    public NativeSubscriptionException? Failure { get; init; }
    public override string ToString() => $"Native subscription completion: {Origin}.";
}

internal interface IOecpSubscription
{
    /// <summary>Cluster logs and agent attachment closes must not carry a cursor.</summary>
    bool CursorlessClose { get; }
    void Receive(JsonElement parameters, int encodedBytes);
    void Closed(SubscriptionClosedNotification notification);
    void Disconnected(NativeOecpFailureKind? failure);
}

/// <summary>One bounded native subscription. Never reopens, waits for a run, or stops native execution.</summary>
public sealed class NativeSubscription<TEstablishment, TEvent> : IAsyncDisposable, IOecpSubscription
{
    private readonly ObservationQueue<TEvent, Cursor> queue;
    private readonly Func<TEvent, Cursor?> validate;
    private readonly Func<SubscriptionId, bool, Task> detach;
    private readonly bool cursorlessClose;
    private readonly object gate = new();
    private NativeSubscriptionCompletion? outcome;
    private readonly TaskCompletionSource<NativeSubscriptionCompletion> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TEstablishment Establishment { get; }
    public Task<NativeSubscriptionCompletion> Completion => completion.Task;
    /// <summary>Last cursor handed to the caller, including records drained after closure. Null for cursorless attachment. Not a processing checkpoint.</summary>
    public Cursor? LastDeliveredCursor => queue.LastDeliveredPosition;
    internal SubscriptionId Id { get; }
    bool IOecpSubscription.CursorlessClose => cursorlessClose;

    internal NativeSubscription(TEstablishment establishment, SubscriptionId id,
        ObservationQueue<TEvent, Cursor> queue, Func<TEvent, Cursor?> validate,
        Func<SubscriptionId, bool, Task> detach, bool cursorlessClose)
    {
        Establishment = establishment; Id = id; this.queue = queue; this.validate = validate; this.detach = detach;
        this.cursorlessClose = cursorlessClose;
    }

    internal void Start() => _ = SettleAsync();

    public async IAsyncEnumerable<TEvent> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
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

    void IOecpSubscription.Receive(JsonElement parameters, int encodedBytes)
    {
        lock (gate)
        {
            if (outcome is not null) return;
            try
            {
                var record = NativeJson.DeserializeUtf8<TEvent>(Encoding.UTF8.GetBytes(parameters.GetRawText()));
                var position = validate(record);
                queue.TryEnqueue(record, encodedBytes, position);
            }
            catch (ObservationFailure failure)
            { outcome = new() { Origin = NativeSubscriptionOrigin.LocalFailure, Failure = NativeSubscriptionException.From(failure) }; }
            catch (Exception error) when (error is JsonException or ArgumentException)
            { Fail(NativeSubscriptionOrigin.LocalFailure, NativeSubscriptionFailureKind.Protocol); }
        }
    }

    void IOecpSubscription.Closed(SubscriptionClosedNotification notification)
    {
        lock (gate)
        {
            if (outcome is not null) return;
            var failure = notification.Reason switch
            {
                SubscriptionCloseReason.SlowConsumer => new NativeSubscriptionException(NativeSubscriptionFailureKind.SlowConsumer),
                SubscriptionCloseReason.SourceUnavailable => new NativeSubscriptionException(NativeSubscriptionFailureKind.SourceUnavailable),
                _ => null
            };
            outcome = new() { Origin = NativeSubscriptionOrigin.ServerClosed, ServerClose = notification, Failure = failure };
            queue.StopReceiving(failure);
        }
    }

    void IOecpSubscription.Disconnected(NativeOecpFailureKind? failure)
    {
        lock (gate)
        {
            if (outcome is not null) return;
            if (queue.StopFailure is OperationCanceledException)
                outcome = new() { Origin = NativeSubscriptionOrigin.Cancelled };
            else if (failure is null || queue.StopFailure is ObjectDisposedException)
            {
                outcome = new() { Origin = NativeSubscriptionOrigin.Disposed };
                queue.Dispose();
            }
            else Fail(NativeSubscriptionOrigin.UnexpectedDisconnect, failure switch
            {
                NativeOecpFailureKind.Protocol => NativeSubscriptionFailureKind.Protocol,
                NativeOecpFailureKind.SizeLimit => NativeSubscriptionFailureKind.SizeLimit,
                _ => NativeSubscriptionFailureKind.UnexpectedDisconnect
            });
        }
    }

    private void Fail(NativeSubscriptionOrigin origin, NativeSubscriptionFailureKind kind)
    {
        var failure = new NativeSubscriptionException(kind);
        outcome = new() { Origin = origin, Failure = failure };
        queue.StopReceiving(failure);
    }

    private async Task SettleAsync()
    {
        await queue.StopRequested.ConfigureAwait(false);
        NativeSubscriptionCompletion result;
        lock (gate)
        {
            outcome ??= new() { Origin = queue.StopFailure is OperationCanceledException
                ? NativeSubscriptionOrigin.Cancelled : NativeSubscriptionOrigin.Disposed };
            result = outcome;
        }
        try
        {
            await detach(Id, result.Origin is not (NativeSubscriptionOrigin.ServerClosed or NativeSubscriptionOrigin.UnexpectedDisconnect)).ConfigureAwait(false);
        }
        finally
        {
            queue.Complete();
            completion.TrySetResult(result);
        }
    }

    public async ValueTask DisposeAsync()
    {
        queue.Dispose();
        await Completion.ConfigureAwait(false);
    }
}
