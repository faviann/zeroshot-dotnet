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
        => new(KindOf(failure.Kind), failure.CorrelationId);
    internal static NativeSubscriptionFailureKind KindOf(ObservationFailureKind kind) => kind switch
    {
        ObservationFailureKind.Admission => NativeSubscriptionFailureKind.Admission,
        ObservationFailureKind.RecordLimit => NativeSubscriptionFailureKind.RecordLimit,
        ObservationFailureKind.StreamByteLimit => NativeSubscriptionFailureKind.StreamByteLimit,
        ObservationFailureKind.AggregateByteLimit => NativeSubscriptionFailureKind.AggregateByteLimit
    };
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
    private readonly ObservationLifecycle<TEvent> observation;
    private readonly Func<TEvent, Cursor?> validate;
    private readonly Func<SubscriptionId, bool, Task> detach;
    private readonly bool cursorlessClose;
    public TEstablishment Establishment { get; }
    public Task<NativeSubscriptionCompletion> Completion => observation.Completion;
    /// <summary>Last cursor handed to the caller, including records drained after closure. Null for cursorless attachment. Not a processing checkpoint.</summary>
    public Cursor? LastDeliveredCursor => observation.LastDeliveredCursor;
    internal SubscriptionId Id { get; }
    bool IOecpSubscription.CursorlessClose => cursorlessClose;

    internal NativeSubscription(TEstablishment establishment, SubscriptionId id,
        ObservationQueue<TEvent, Cursor> queue, Func<TEvent, Cursor?> validate,
        Func<SubscriptionId, bool, Task> detach, bool cursorlessClose)
    {
        Establishment = establishment; Id = id; observation = new(queue); this.validate = validate; this.detach = detach;
        this.cursorlessClose = cursorlessClose;
    }

    // A server or transport close already ended the remote stream; only a local outcome cancels it.
    internal void Start() => observation.Start(result =>
        detach(Id, result.Origin is not (NativeSubscriptionOrigin.ServerClosed or NativeSubscriptionOrigin.UnexpectedDisconnect)));

    public IAsyncEnumerable<TEvent> ReadAllAsync(CancellationToken cancellationToken = default)
        => observation.ReadAllAsync(cancellationToken);

    void IOecpSubscription.Receive(JsonElement parameters, int encodedBytes) => observation.Receive(encodedBytes, () =>
    {
        var record = NativeJson.DeserializeUtf8<TEvent>(Encoding.UTF8.GetBytes(parameters.GetRawText()));
        return (record, validate(record));
    });

    void IOecpSubscription.Closed(SubscriptionClosedNotification notification)
        => observation.Settle(NativeSubscriptionOrigin.ServerClosed, ObservationLifecycle<TEvent>.CloseFailure(notification.Reason), notification);

    void IOecpSubscription.Disconnected(NativeOecpFailureKind? failure) => observation.Disconnect(failure switch
    {
        null => null,
        NativeOecpFailureKind.Protocol => NativeSubscriptionFailureKind.Protocol,
        NativeOecpFailureKind.SizeLimit => NativeSubscriptionFailureKind.SizeLimit,
        _ => NativeSubscriptionFailureKind.UnexpectedDisconnect
    });

    public ValueTask DisposeAsync() => observation.DisposeAsync();
}
