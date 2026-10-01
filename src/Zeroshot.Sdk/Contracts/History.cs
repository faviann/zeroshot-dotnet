// Native 10.9.0 / 75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa public run-history wire records
// (native_v2_observability/history/{wire,status,control}.rs, v2_run_ledger.rs). Native publishes
// no schema for them; nested graph/runtime/source/result/outcome values use the pinned schemas.
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zeroshot.Native.Contracts;

/// <summary>Source-backed run-history records. Required nullable fields are always present on the wire.</summary>
// Validate checks native numeric domains that System.Text.Json does not express by type alone.
public abstract record HistoryContract : NativeContract;

public enum RunHistoryProblemCode
{
    [JsonStringEnumMemberName("run_not_found")] RunNotFound,
    [JsonStringEnumMemberName("not_found")] NotFound,
    [JsonStringEnumMemberName("forbidden")] Forbidden,
    [JsonStringEnumMemberName("invalid_cursor")] InvalidCursor,
    [JsonStringEnumMemberName("history_gap")] HistoryGap,
    [JsonStringEnumMemberName("runtime_unavailable")] RuntimeUnavailable,
    [JsonStringEnumMemberName("history_unavailable")] HistoryUnavailable,
    [JsonStringEnumMemberName("history_incomplete")] HistoryIncomplete,
    [JsonStringEnumMemberName("history_pending")] HistoryPending,
    [JsonStringEnumMemberName("history_invalid")] HistoryInvalid,
    [JsonStringEnumMemberName("history_expired")] HistoryExpired,
    [JsonStringEnumMemberName("history_incompatible")] HistoryIncompatible
}

internal static class RunHistoryProblems
{
    private static readonly Dictionary<string, RunHistoryProblemCode> Codes = new(StringComparer.Ordinal)
    {
        ["run_not_found"] = RunHistoryProblemCode.RunNotFound, ["not_found"] = RunHistoryProblemCode.NotFound,
        ["forbidden"] = RunHistoryProblemCode.Forbidden, ["invalid_cursor"] = RunHistoryProblemCode.InvalidCursor,
        ["history_gap"] = RunHistoryProblemCode.HistoryGap, ["runtime_unavailable"] = RunHistoryProblemCode.RuntimeUnavailable,
        ["history_unavailable"] = RunHistoryProblemCode.HistoryUnavailable, ["history_incomplete"] = RunHistoryProblemCode.HistoryIncomplete,
        ["history_pending"] = RunHistoryProblemCode.HistoryPending, ["history_invalid"] = RunHistoryProblemCode.HistoryInvalid,
        ["history_expired"] = RunHistoryProblemCode.HistoryExpired, ["history_incompatible"] = RunHistoryProblemCode.HistoryIncompatible
    };

    internal static RunHistoryProblemCode? Parse(string? code)
        => code is not null && Codes.TryGetValue(code, out var value) ? value : null;
}

/// <summary>Host-only queue state plus the native phases used after admission.</summary>
public enum RunHistoryPhase
{
    [JsonStringEnumMemberName("queued")] Queued,
    [JsonStringEnumMemberName("admitted")] Admitted,
    [JsonStringEnumMemberName("running")] Running,
    [JsonStringEnumMemberName("stopping")] Stopping,
    [JsonStringEnumMemberName("finished")] Finished,
    [JsonStringEnumMemberName("unavailable")] Unavailable
}

/// <summary>The native ledger phase of an admitted run.</summary>
public enum RunPhase
{
    [JsonStringEnumMemberName("admitted")] Admitted,
    [JsonStringEnumMemberName("running")] Running,
    [JsonStringEnumMemberName("stopping")] Stopping,
    [JsonStringEnumMemberName("finished")] Finished
}

/// <summary>Observation availability, independent of run phase and terminal result.</summary>
public enum HistoryObservationState
{
    [JsonStringEnumMemberName("active")] Active,
    [JsonStringEnumMemberName("collecting")] Collecting,
    [JsonStringEnumMemberName("complete")] Complete,
    [JsonStringEnumMemberName("incomplete")] Incomplete,
    [JsonStringEnumMemberName("expired")] Expired,
    [JsonStringEnumMemberName("unavailable")] Unavailable
}

public enum SafeLogStream
{
    [JsonStringEnumMemberName("output")] Output,
    [JsonStringEnumMemberName("error")] Error,
    [JsonStringEnumMemberName("system")] System
}

public enum ExecutionVoidReason
{
    [JsonStringEnumMemberName("parallel_join")] ParallelJoin,
    [JsonStringEnumMemberName("map_terminal")] MapTerminal
}

/// <summary>A bounded terminal summary. Successful output stays in detail and history pages.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "status")]
[JsonDerivedType(typeof(SucceededHistorySynopsis), "succeeded")]
[JsonDerivedType(typeof(FailedHistorySynopsis), "failed")]
public abstract record RunHistoryTerminalSynopsis : HistoryContract;

public sealed record SucceededHistorySynopsis : RunHistoryTerminalSynopsis;

public sealed record FailedHistorySynopsis : RunHistoryTerminalSynopsis
{
    [JsonPropertyName("reason")]
    public required string Reason { get; init; }
}

public sealed record RunHistorySummary : HistoryContract
{
    [JsonPropertyName("runId")]
    public required RunId RunId { get; init; }
    [JsonPropertyName("title")]
    public required RunTitle Title { get; init; }
    [JsonPropertyName("phase")]
    public required RunHistoryPhase Phase { get; init; }
    [JsonPropertyName("cursor")]
    public required Cursor? Cursor { get; init; }
    [JsonPropertyName("terminal")]
    public required RunHistoryTerminalSynopsis? Terminal { get; init; }
    [JsonPropertyName("source")]
    public required ResolvedSource? Source { get; init; }
    [JsonPropertyName("createdAt")]
    public required ulong? CreatedAt { get; init; }
    [JsonPropertyName("historyAvailable")]
    public required bool HistoryAvailable { get; init; }
    [JsonPropertyName("runtimeFailure"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RuntimeFailure? RuntimeFailure { get; init; }
}

/// <summary>One UUIDv7-descending page of at most 50 runs.</summary>
public sealed record RunHistoryList : HistoryContract
{
    [JsonPropertyName("runs")]
    public required ImmutableArray<RunHistorySummary> Runs { get; init; }
    [JsonPropertyName("nextCursor")]
    public required RunId? NextCursor { get; init; }
}

/// <summary>The admitted definition. Observation availability is separate from phase and terminal.</summary>
public sealed record RunDefinition : HistoryContract
{
    [JsonPropertyName("version")]
    public required byte Version { get; init; }
    [JsonPropertyName("projectionVersion")]
    public required byte ProjectionVersion { get; init; }
    [JsonPropertyName("runId")]
    public required RunId RunId { get; init; }
    [JsonPropertyName("title")]
    public required RunTitle Title { get; init; }
    [JsonPropertyName("createdAt")]
    public required ulong? CreatedAt { get; init; }
    [JsonPropertyName("phase")]
    public required RunPhase Phase { get; init; }
    [JsonPropertyName("cursor")]
    public required Cursor Cursor { get; init; }
    [JsonPropertyName("terminal")]
    public required TerminalResult? Terminal { get; init; }
    [JsonPropertyName("historyAvailable")]
    public required bool HistoryAvailable { get; init; }
    [JsonPropertyName("graph")]
    public required GraphSpec Graph { get; init; }
    [JsonPropertyName("runtime")]
    public required RuntimePlan Runtime { get; init; }
    /// <summary>Arbitrary caller-authored JSON, which can contain sensitive data.</summary>
    [JsonPropertyName("initialInput")]
    public required JsonElement InitialInput { get; init; }
    [JsonPropertyName("source")]
    public required ResolvedSource Source { get; init; }
    [JsonPropertyName("history")]
    public required HistoryMetadata History { get; init; }
    [JsonPropertyName("observation")]
    public required HistoryObservation Observation { get; init; }
    [JsonPropertyName("runtimeFailure"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RuntimeFailure? RuntimeFailure { get; init; }

    // Pre-v1 archives embedded an unbounded snapshot. Native accepts and strips it.
    [JsonInclude, JsonPropertyName("snapshot"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    private JsonElement? LegacySnapshot { get => null; init { } }
}

public sealed record HistoryMetadata : HistoryContract
{
    [JsonPropertyName("initialCursor")]
    public required Cursor InitialCursor { get; init; }
    [JsonPropertyName("cursor")]
    public required Cursor Cursor { get; init; }
    [JsonPropertyName("complete")]
    public required bool Complete { get; init; }
    [JsonPropertyName("limitations")]
    public required ImmutableArray<string> Limitations { get; init; }
}

public sealed record HistoryObservation : HistoryContract
{
    [JsonPropertyName("state")]
    public required HistoryObservationState State { get; init; }
    [JsonPropertyName("code"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Code { get; init; }
}

/// <summary>Status-only runtime failure. It is observation metadata, never a retained terminal event.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Skip)]
public sealed record RuntimeFailure : HistoryContract
{
    [JsonPropertyName("atCursor")]
    public required Cursor AtCursor { get; init; }
    [JsonPropertyName("reason")]
    public required string Reason { get; init; }
}

/// <summary>Derived control activity for one retained page event. It is never a ledger event.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Skip)]
public sealed record ControlRecord : HistoryContract
{
    [JsonPropertyName("cursor")]
    public required Cursor Cursor { get; init; }
    [JsonPropertyName("node")]
    public required string Node { get; init; }
    [JsonPropertyName("mapIndices")]
    public required ImmutableArray<ulong> MapIndices { get; init; }
    [JsonPropertyName("visitId")]
    public required string VisitId { get; init; }
    /// <summary>Native entered/completed/succeeded/failed or synthesized stopped; an open string.</summary>
    [JsonPropertyName("state")]
    public required string State { get; init; }
    [JsonPropertyName("branch"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Branch { get; init; }
    [JsonPropertyName("detail"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Detail { get; init; }
    /// <summary>Present null is authored output; omission means this visit has no output.</summary>
    [JsonPropertyName("output"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Optional<JsonElement> Output { get; init; }
}

public sealed record HistoryPage : HistoryContract
{
    [JsonPropertyName("events")]
    public required ImmutableArray<HistoryEventRecord> Events { get; init; }
    [JsonPropertyName("nextCursor")]
    public required Cursor NextCursor { get; init; }
    [JsonPropertyName("headCursor")]
    public required Cursor HeadCursor { get; init; }
    [JsonPropertyName("complete")]
    public required bool Complete { get; init; }
    /// <summary>Run state only. Do not end observation on this alone; see <see cref="Observation"/>.</summary>
    [JsonPropertyName("finished")]
    public required bool Finished { get; init; }
    [JsonPropertyName("observation")]
    public required HistoryObservation Observation { get; init; }
    [JsonPropertyName("runtimeFailure"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RuntimeFailure? RuntimeFailure { get; init; }
    [JsonIgnore]
    public ImmutableArray<ControlRecord> Control { get; init; } = [];
    [JsonPropertyName("controlError"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ControlError { get; init; }

    // Native defaults an omitted list and omits an empty one; explicit null is invalid.
    [JsonInclude, JsonPropertyName("control"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    private ImmutableArray<ControlRecord>? WireControl
    {
        get => Control.IsDefaultOrEmpty ? null : Control;
        init => Control = value ?? throw new JsonException();
    }
}

/// <summary>One retained ledger event with its canonical cursor.</summary>
public sealed record HistoryEventRecord : HistoryContract
{
    [JsonPropertyName("cursor")]
    public required Cursor Cursor { get; init; }
    [JsonPropertyName("event")]
    public required HistoryEvent Event { get; init; }
}

/// <summary>The nine retained native ledger events. This is not a run-watch notification.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(PriorExecutionHistoryEvent), "prior_execution")]
[JsonDerivedType(typeof(RunStartedHistoryEvent), "run_started")]
[JsonDerivedType(typeof(NodeStartedHistoryEvent), "node_started")]
[JsonDerivedType(typeof(NodeCompletedHistoryEvent), "node_completed")]
[JsonDerivedType(typeof(ExecutionVoidedHistoryEvent), "execution_voided")]
[JsonDerivedType(typeof(SafeLogHistoryEvent), "safe_log")]
[JsonDerivedType(typeof(TokenUsageObservedHistoryEvent), "token_usage_observed")]
[JsonDerivedType(typeof(ForceStopRequestedHistoryEvent), "force_stop_requested")]
[JsonDerivedType(typeof(TerminalHistoryEvent), "terminal")]
public abstract record HistoryEvent : HistoryContract;

/// <summary>Imported prerequisite work, serialized as the native durable record with numeric identities.</summary>
public sealed record PriorExecutionHistoryEvent : HistoryEvent
{
    [JsonPropertyName("execution")]
    public required DurableExecution Execution { get; init; }
}

// serde ignores unknown fields on internally tagged unit variants despite deny_unknown_fields.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Skip)]
public sealed record RunStartedHistoryEvent : HistoryEvent;

public sealed record NodeStartedHistoryEvent : HistoryEvent
{
    [JsonPropertyName("reference")]
    public required HistoryExecutionReference Reference { get; init; }
    [JsonPropertyName("occurrence")]
    public required StructuralOccurrence Occurrence { get; init; }
    [JsonPropertyName("attempt")]
    public required PositiveInteger Attempt { get; init; }
    /// <summary>Arbitrary execution input, which can contain sensitive data.</summary>
    [JsonPropertyName("input")]
    public required JsonElement Input { get; init; }
}

public sealed record NodeCompletedHistoryEvent : HistoryEvent
{
    [JsonPropertyName("completion")]
    public required NodeCompletion Completion { get; init; }
}

public sealed record ExecutionVoidedHistoryEvent : HistoryEvent
{
    [JsonPropertyName("reference")]
    public required HistoryExecutionReference Reference { get; init; }
    [JsonPropertyName("reason")]
    public required ExecutionVoidReason Reason { get; init; }
}

public sealed record SafeLogHistoryEvent : HistoryEvent
{
    [JsonPropertyName("execution")]
    public required HistoryIdentity? Execution { get; init; }
    [JsonPropertyName("timestamp")]
    public required UnixTimestampMillis Timestamp { get; init; }
    [JsonPropertyName("stream")]
    public required SafeLogStream Stream { get; init; }
    [JsonPropertyName("line")]
    public required SafeLogLine Line { get; init; }
}

public sealed record TokenUsageObservedHistoryEvent : HistoryEvent
{
    [JsonPropertyName("execution")]
    public required HistoryIdentity Execution { get; init; }
    [JsonPropertyName("usage")]
    public required TokenUsageDelta? Usage { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Skip)]
public sealed record ForceStopRequestedHistoryEvent : HistoryEvent;

/// <summary>Terminal evidence retained by the target, distinct from a status report.</summary>
public sealed record TerminalHistoryEvent : HistoryEvent
{
    [JsonPropertyName("result")]
    public required TerminalResult Result { get; init; }
}

/// <summary>Native execution address; the history projection encodes both numeric identities as decimal strings.</summary>
public sealed record HistoryExecutionReference : HistoryContract
{
    [JsonPropertyName("runId")]
    public required RunId RunId { get; init; }
    [JsonPropertyName("node")]
    public required NodeName Node { get; init; }
    [JsonPropertyName("nodeInstance")]
    public required HistoryIdentity NodeInstance { get; init; }
    [JsonPropertyName("execution")]
    public required HistoryIdentity Execution { get; init; }
}

public sealed record NodeCompletion : HistoryContract
{
    [JsonPropertyName("reference")]
    public required HistoryExecutionReference Reference { get; init; }
    [JsonPropertyName("outcome")]
    public required WorkerOutcome Outcome { get; init; }
}

public sealed record StructuralOccurrence : HistoryContract
{
    [JsonPropertyName("node")]
    public required NodeName Node { get; init; }
    [JsonPropertyName("mapIndices")]
    public required ImmutableArray<ulong> MapIndices { get; init; }
}

public sealed record TokenUsageDelta : HistoryContract
{
    [JsonPropertyName("inputTokens")]
    public required TokenCount InputTokens { get; init; }
    [JsonPropertyName("outputTokens")]
    public required TokenCount OutputTokens { get; init; }
    [JsonPropertyName("cacheReadInputTokens")]
    public required TokenCount? CacheReadInputTokens { get; init; }
    [JsonPropertyName("cacheCreationInputTokens")]
    public required TokenCount? CacheCreationInputTokens { get; init; }
}

/// <summary>The unmodified native durable record: snake_case fields and numeric identities.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Skip)]
public sealed record DurableExecution : HistoryContract
{
    [JsonPropertyName("dispatch_position")]
    public required ulong DispatchPosition { get; init; }
    [JsonPropertyName("node_instance")]
    public required ulong NodeInstance { get; init; }
    [JsonPropertyName("execution")]
    public required ulong Execution { get; init; }
    [JsonPropertyName("occurrence")]
    public required StructuralOccurrence Occurrence { get; init; }
    [JsonPropertyName("attempt")]
    public required PositiveInteger Attempt { get; init; }
    [JsonPropertyName("input")]
    public required JsonElement Input { get; init; }
    [JsonPropertyName("state")]
    public required DurableExecutionState State { get; init; }

    internal override void Validate(JsonElement json)
    {
        if (DispatchPosition > long.MaxValue || NodeInstance == 0 || Execution == 0) throw new JsonException();
    }
}

/// <summary>Externally tagged: <c>"Active"</c>, <c>{"Settled":{...}}</c> or <c>{"Voided":{...}}</c>.</summary>
[JsonConverter(typeof(DurableExecutionStateConverter))]
public abstract record DurableExecutionState : HistoryContract;

public sealed record ActiveExecutionState : DurableExecutionState;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Skip)]
public sealed record SettledExecutionState : DurableExecutionState
{
    [JsonPropertyName("position")]
    public required ulong Position { get; init; }
    [JsonPropertyName("outcome")]
    public required WorkerOutcome Outcome { get; init; }
    internal override void Validate(JsonElement json) { if (Position > long.MaxValue) throw new JsonException(); }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Skip)]
public sealed record VoidedExecutionState : DurableExecutionState
{
    [JsonPropertyName("position")]
    public required ulong Position { get; init; }
    [JsonPropertyName("reason")]
    public required ExecutionVoidReason Reason { get; init; }
    internal override void Validate(JsonElement json) { if (Position > long.MaxValue) throw new JsonException(); }
}

internal sealed class DurableExecutionStateConverter : JsonConverter<DurableExecutionState>
{
    public override DurableExecutionState Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return reader.GetString() == "Active" ? new ActiveExecutionState() : throw new JsonException();
        if (reader.TokenType != JsonTokenType.StartObject || !reader.Read() || reader.TokenType != JsonTokenType.PropertyName)
            throw new JsonException();
        var variant = reader.GetString();
        reader.Read();
        DurableExecutionState state = variant switch
        {
            "Active" when reader.TokenType == JsonTokenType.Null => new ActiveExecutionState(),
            "Settled" => JsonSerializer.Deserialize<SettledExecutionState>(ref reader, options) ?? throw new JsonException(),
            "Voided" => JsonSerializer.Deserialize<VoidedExecutionState>(ref reader, options) ?? throw new JsonException(),
            _ => throw new JsonException()
        };
        if (!reader.Read() || reader.TokenType != JsonTokenType.EndObject) throw new JsonException();
        return state;
    }

    public override void Write(Utf8JsonWriter writer, DurableExecutionState value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case ActiveExecutionState: writer.WriteStringValue("Active"); return;
            case SettledExecutionState settled:
                writer.WriteStartObject(); writer.WritePropertyName("Settled");
                JsonSerializer.Serialize(writer, settled, options); writer.WriteEndObject(); return;
            case VoidedExecutionState voided:
                writer.WriteStartObject(); writer.WritePropertyName("Voided");
                JsonSerializer.Serialize(writer, voided, options); writer.WriteEndObject(); return;
            default: throw new JsonException();
        }
    }
}

/// <summary>A positive native u64 identity projected as a canonical decimal string.</summary>
[JsonConverter(typeof(NativeStringConverterFactory))]
public sealed record HistoryIdentity : NativeString
{
    public HistoryIdentity(string value) : base(ValueRules.Check(nameof(HistoryIdentity), value)) { }
}

/// <summary>A retained safe log line: at most 16 KiB of UTF-8 without NUL.</summary>
[JsonConverter(typeof(NativeStringConverterFactory))]
public sealed record SafeLogLine : NativeString
{
    public SafeLogLine(string value) : base(ValueRules.Check(nameof(SafeLogLine), value)) { }
}
