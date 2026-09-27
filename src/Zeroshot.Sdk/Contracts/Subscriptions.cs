using System.Text.Json.Serialization;

namespace Zeroshot.Native.Contracts;

[WireContract("RunWatchParams")]
public sealed record RunWatchParams : NativeContract
{
    [JsonPropertyName("runId")]
    public required RunId RunId { get; init; }
    /// <summary>Opaque exclusive history position; the boundary record is not replayed.</summary>
    [JsonPropertyName("fromCursor"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Cursor? FromCursor { get; init; }
}

[WireContract("RunLogsParams")]
public sealed record RunLogsParams : NativeContract
{
    [JsonPropertyName("runId")]
    public required RunId RunId { get; init; }
    [JsonPropertyName("fromCursor"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Cursor? FromCursor { get; init; }
    /// <summary>Exact opaque active or settled execution selector.</summary>
    [JsonPropertyName("execution"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExecutionRef? Execution { get; init; }
}

[WireContract("RunWatchResult")]
public sealed record RunWatchResult : NativeContract
{
    [JsonPropertyName("subscriptionId")]
    public required SubscriptionId SubscriptionId { get; init; }
    [JsonPropertyName("runId")]
    public required RunId RunId { get; init; }
    [JsonPropertyName("atCursor")]
    public required Cursor AtCursor { get; init; }
}

[WireContract("RunLogsResult")]
public sealed record RunLogsResult : NativeContract
{
    [JsonPropertyName("subscriptionId")]
    public required SubscriptionId SubscriptionId { get; init; }
    [JsonPropertyName("runId")]
    public required RunId RunId { get; init; }
    [JsonPropertyName("atCursor")]
    public required Cursor AtCursor { get; init; }
}

[WireContract("RunWatchEventNotification")]
public sealed record RunWatchEventNotification : NativeContract
{
    [JsonPropertyName("subscriptionId")]
    public required SubscriptionId SubscriptionId { get; init; }
    [JsonPropertyName("runId")]
    public required RunId RunId { get; init; }
    [JsonPropertyName("title")]
    public required RunTitle Title { get; init; }
    [JsonPropertyName("source")]
    public required ResolvedSource Source { get; init; }
    [JsonPropertyName("size")]
    public required RunSize Size { get; init; }
    [JsonPropertyName("cursor")]
    public required Cursor Cursor { get; init; }
    [JsonPropertyName("status")]
    public required RunStatus Status { get; init; }
}

[WireContract("RunLogEventNotification")]
public sealed record RunLogEventNotification : NativeContract
{
    [JsonPropertyName("subscriptionId")]
    public required SubscriptionId SubscriptionId { get; init; }
    [JsonPropertyName("runId")]
    public required RunId RunId { get; init; }
    [JsonPropertyName("cursor")]
    public required Cursor Cursor { get; init; }
    [JsonPropertyName("timestamp")]
    public required UnixTimestampMillis Timestamp { get; init; }
    [JsonPropertyName("execution"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExecutionRef? Execution { get; init; }
    [JsonPropertyName("record")]
    public required LogRecord Record { get; init; }
}

[WireContract("LogRecord")]
public sealed record LogRecord : NativeContract
{
    [JsonPropertyName("level")]
    public required LogLevel Level { get; init; }
    [JsonPropertyName("target")]
    public required BoundedLogTarget Target { get; init; }
    [JsonPropertyName("message")]
    public required BoundedLogMessage Message { get; init; }
}

[JsonConverter(typeof(NativeStringConverterFactory))]
public sealed record BoundedLogTarget : NativeString
{
    public BoundedLogTarget(string value) : base(ValueRules.Check(nameof(BoundedLogTarget), value)) { }
}

[JsonConverter(typeof(NativeStringConverterFactory))]
public sealed record BoundedLogMessage : NativeString
{
    public BoundedLogMessage(string value) : base(ValueRules.Check(nameof(BoundedLogMessage), value)) { }
}

[WireContract("UnixTimestampMillis")]
[JsonConverter(typeof(SafeIntegerConverter<UnixTimestampMillis>))]
public readonly record struct UnixTimestampMillis : ISafeInteger<UnixTimestampMillis>
{
    public ulong Value { get; }
    public UnixTimestampMillis(ulong value) { _ = new PositiveInteger(value); Value = value; }
    static UnixTimestampMillis ISafeInteger<UnixTimestampMillis>.Create(ulong value) => new(value);
    public static implicit operator UnixTimestampMillis(ulong value) => new(value);
}

[WireContract("SubscriptionCloseReason")]
public enum SubscriptionCloseReason
{
    [JsonStringEnumMemberName("done")] Done,
    [JsonStringEnumMemberName("SLOW_CONSUMER")] SlowConsumer,
    [JsonStringEnumMemberName("SOURCE_UNAVAILABLE")] SourceUnavailable
}

[WireContract("SubscriptionClosedNotification")]
public sealed record SubscriptionClosedNotification : NativeContract
{
    [JsonPropertyName("subscriptionId")]
    public required SubscriptionId SubscriptionId { get; init; }
    [JsonPropertyName("reason")]
    public required SubscriptionCloseReason Reason { get; init; }
    /// <summary>Server delivery evidence, independent of the caller's last delivered cursor.</summary>
    [JsonPropertyName("lastDeliveredCursor"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Cursor? LastDeliveredCursor { get; init; }
}

[WireContract("SubscriptionCancelParams")]
public sealed record SubscriptionCancelParams : NativeContract
{
    [JsonPropertyName("subscriptionId")]
    public required SubscriptionId SubscriptionId { get; init; }
}
