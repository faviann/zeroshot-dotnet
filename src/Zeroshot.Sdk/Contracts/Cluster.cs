// Native 10.9.0 / 75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa shared cluster admission, lifecycle and
// observation contracts, plus the trusted run/submit result. Backend support is per target.
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zeroshot.Native.Contracts;

[WireContract("PlanParams")]
public sealed record PlanParams : NativeContract
{
    [JsonPropertyName("graph")]
    public required GraphSpec Graph { get; init; }
}

/// <summary>Graph verification only. Nothing is admitted.</summary>
[WireContract("PlanResult")]
public sealed record PlanResult : NativeContract
{
    [JsonPropertyName("ok")]
    public required bool Ok { get; init; }
    [JsonPropertyName("diagnostics")]
    public required ImmutableArray<GraphDiagnostic> Diagnostics { get; init; }
    [JsonPropertyName("bounds"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StructuralBounds? Bounds { get; init; }
}

/// <summary>A committed apply requires IdempotencyKey. A dry run must omit both the key and Input.</summary>
[WireContract("ApplyParams")]
public sealed record ApplyParams : NativeContract
{
    [JsonPropertyName("graph")]
    public required GraphSpec Graph { get; init; }
    /// <summary>Present null is a root input; omission is required when the graph is unchanged.</summary>
    [JsonPropertyName("input"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Optional<JsonElement> Input { get; init; }
    [JsonPropertyName("dryRun"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool DryRun { get; init; }
    /// <summary>Zero requires an empty cluster; omission skips the generation fence.</summary>
    [JsonPropertyName("ifGeneration"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Generation? IfGeneration { get; init; }
    [JsonPropertyName("idempotencyKey"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IdempotencyKey? IdempotencyKey { get; init; }
}

[WireContract("ApplyResult")]
public sealed record ApplyResult : NativeContract
{
    [JsonPropertyName("generation")]
    public Generation? Generation { get; init; }
    [JsonPropertyName("runId")]
    public RunId? RunId { get; init; }
    [JsonPropertyName("phase")]
    public required Phase Phase { get; init; }
    [JsonPropertyName("deduped")]
    public required bool Deduped { get; init; }
    /// <summary>Node-name changes against the current graph, including for a dry run.</summary>
    [JsonPropertyName("diff"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GraphDiff? Diff { get; init; }
}

[WireContract("GraphDiff")]
public sealed record GraphDiff : NativeContract
{
    [JsonPropertyName("added")]
    public required ImmutableArray<NodeName> Added { get; init; }
    [JsonPropertyName("removed")]
    public required ImmutableArray<NodeName> Removed { get; init; }
    [JsonPropertyName("changed")]
    public required ImmutableArray<NodeName> Changed { get; init; }
}

/// <summary>Supply at least one of Labels, LogLevel or Suspended. Labels replace the whole map.</summary>
[WireContract("UpdateParams")]
public sealed record UpdateParams : NativeContract
{
    [JsonPropertyName("labels"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImmutableDictionary<Label, Label>? Labels { get; init; }
    [JsonPropertyName("logLevel"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LogLevel? LogLevel { get; init; }
    [JsonPropertyName("suspended"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Suspended { get; init; }
    [JsonPropertyName("ifGeneration")]
    public required Generation IfGeneration { get; init; }
    [JsonPropertyName("idempotencyKey")]
    public required IdempotencyKey IdempotencyKey { get; init; }
}

[WireContract("UpdateResult")]
public sealed record UpdateResult : NativeContract
{
    [JsonPropertyName("generation")]
    public required Generation Generation { get; init; }
    [JsonPropertyName("runId")]
    public required RunId RunId { get; init; }
    [JsonPropertyName("phase")]
    public required Phase Phase { get; init; }
    [JsonPropertyName("operational")]
    public required OperationalStatus Operational { get; init; }
    [JsonPropertyName("atCursor")]
    public required Cursor AtCursor { get; init; }
    [JsonPropertyName("deduped")]
    public required bool Deduped { get; init; }
}

/// <summary>Cluster lifecycle stop. This is not native run/force.</summary>
[WireContract("StopParams")]
public sealed record StopParams : NativeContract
{
    [JsonPropertyName("mode")]
    public required StopMode Mode { get; init; }
    [JsonPropertyName("ifGeneration")]
    public required Generation IfGeneration { get; init; }
    [JsonPropertyName("idempotencyKey")]
    public required IdempotencyKey IdempotencyKey { get; init; }
}

/// <summary>The requested mode and the mode the backend actually applied can differ.</summary>
[WireContract("StopResult")]
public sealed record StopResult : NativeContract
{
    [JsonPropertyName("generation")]
    public required Generation Generation { get; init; }
    [JsonPropertyName("runId")]
    public required RunId RunId { get; init; }
    [JsonPropertyName("phase")]
    public required Phase Phase { get; init; }
    [JsonPropertyName("acceptedMode")]
    public required StopMode AcceptedMode { get; init; }
    [JsonPropertyName("effectiveMode")]
    public required StopMode EffectiveMode { get; init; }
    [JsonPropertyName("operational")]
    public required OperationalStatus Operational { get; init; }
    [JsonPropertyName("atCursor")]
    public required Cursor AtCursor { get; init; }
    [JsonPropertyName("deduped")]
    public required bool Deduped { get; init; }
}

/// <summary>Retries the current failed frontier. This is not workspace recovery.</summary>
[WireContract("RetryParams")]
public sealed record RetryParams : NativeContract
{
    [JsonPropertyName("ifGeneration")]
    public required Generation IfGeneration { get; init; }
    [JsonPropertyName("idempotencyKey")]
    public required IdempotencyKey IdempotencyKey { get; init; }
}

[WireContract("RetryResult")]
public sealed record RetryResult : NativeContract
{
    [JsonPropertyName("generation")]
    public required Generation Generation { get; init; }
    [JsonPropertyName("runId")]
    public required RunId RunId { get; init; }
    [JsonPropertyName("phase")]
    public required Phase Phase { get; init; }
    [JsonPropertyName("retriedTurnId")]
    public required string RetriedTurnId { get; init; }
    [JsonPropertyName("retryTurnId")]
    public required string RetryTurnId { get; init; }
    [JsonPropertyName("operational")]
    public required OperationalStatus Operational { get; init; }
    [JsonPropertyName("atCursor")]
    public required Cursor AtCursor { get; init; }
    [JsonPropertyName("deduped")]
    public required bool Deduped { get; init; }
}

/// <summary>Why native found no retryable frontier, from NO_RETRYABLE_FRONTIER error details.</summary>
public enum NoRetryableFrontierReason { Exhausted, Success, Active, Consumed }

/// <summary>Starts a successor of the exact predecessor run. This is not run/resume.</summary>
[WireContract("ResubmitParams")]
public sealed record ResubmitParams : NativeContract
{
    [JsonPropertyName("ifGeneration")]
    public required Generation IfGeneration { get; init; }
    [JsonPropertyName("ifRunId")]
    public required RunId IfRunId { get; init; }
    [JsonPropertyName("idempotencyKey")]
    public required IdempotencyKey IdempotencyKey { get; init; }
    /// <summary>Present null replaces the input with null; omission reuses the predecessor's input.</summary>
    [JsonPropertyName("replacementInput"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Optional<JsonElement> ReplacementInput { get; init; }
}

[WireContract("ResubmitResult")]
public sealed record ResubmitResult : NativeContract
{
    [JsonPropertyName("generation")]
    public required Generation Generation { get; init; }
    [JsonPropertyName("priorRunId")]
    public required RunId PriorRunId { get; init; }
    [JsonPropertyName("runId")]
    public required RunId RunId { get; init; }
    [JsonPropertyName("phase")]
    public required Phase Phase { get; init; }
    [JsonPropertyName("operational")]
    public required OperationalStatus Operational { get; init; }
    [JsonPropertyName("atCursor")]
    public required Cursor AtCursor { get; init; }
    [JsonPropertyName("deduped")]
    public required bool Deduped { get; init; }
}

/// <summary>Cluster lifecycle deletion. It neither discards a workspace nor deletes a native run.</summary>
[WireContract("DeleteParams")]
public sealed record DeleteParams : NativeContract
{
    [JsonPropertyName("ifGeneration")]
    public required Generation IfGeneration { get; init; }
    [JsonPropertyName("ifRunId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RunId? IfRunId { get; init; }
    [JsonPropertyName("idempotencyKey")]
    public required IdempotencyKey IdempotencyKey { get; init; }
}

[WireContract("DeleteResult")]
public sealed record DeleteResult : NativeContract
{
    [JsonPropertyName("deleted")]
    public required bool Deleted { get; init; }
    [JsonPropertyName("phase")]
    public required Phase Phase { get; init; }
    [JsonPropertyName("generation"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Generation? Generation { get; init; }
    [JsonPropertyName("runId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RunId? RunId { get; init; }
    [JsonPropertyName("atCursor"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Cursor? AtCursor { get; init; }
    [JsonPropertyName("deduped")]
    public required bool Deduped { get; init; }
}

/// <summary>Without RunId, native selects the current run or parks until one exists. FromCursor is exclusive.</summary>
[WireContract("WatchParams")]
public sealed record WatchParams : NativeContract
{
    [JsonPropertyName("runId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RunId? RunId { get; init; }
    [JsonPropertyName("fromCursor"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Cursor? FromCursor { get; init; }
}

/// <summary>The resolved run and captured history tail. Both are null while parked.</summary>
[WireContract("WatchResult")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Skip)]
public sealed record WatchResult : NativeContract
{
    [JsonPropertyName("subscriptionId")]
    public required SubscriptionId SubscriptionId { get; init; }
    [JsonPropertyName("runId")]
    public RunId? RunId { get; init; }
    [JsonPropertyName("atCursor")]
    public Cursor? AtCursor { get; init; }
}

[WireContract("EventNotification")]
public sealed record EventNotification : NativeContract
{
    [JsonPropertyName("subscriptionId")]
    public required SubscriptionId SubscriptionId { get; init; }
    [JsonPropertyName("runId")]
    public required RunId RunId { get; init; }
    [JsonPropertyName("cursor")]
    public required Cursor Cursor { get; init; }
    [JsonPropertyName("event")]
    public required WatchEvent Event { get; init; }
}

/// <summary>Durable cluster events. Finished is the last event for a run; a fault never authorizes a retry.</summary>
[WireContract("WatchEvent")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PhaseWatchEvent), "phase")]
[JsonDerivedType(typeof(NodeBeginWatchEvent), "node_begin")]
[JsonDerivedType(typeof(NodeEndWatchEvent), "node_end")]
[JsonDerivedType(typeof(BookmarkWatchEvent), "bookmark")]
[JsonDerivedType(typeof(FaultWatchEvent), "fault")]
[JsonDerivedType(typeof(FinishedWatchEvent), "finished")]
public abstract record WatchEvent : NativeContract;

public sealed record PhaseWatchEvent : WatchEvent
{
    [JsonPropertyName("status")]
    public required ClusterStatus Status { get; init; }
    [JsonPropertyName("admission"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AdmissionTransition? Admission { get; init; }
}

public sealed record NodeBeginWatchEvent : WatchEvent
{
    [JsonPropertyName("node")]
    public required NodeAddress Node { get; init; }
    [JsonPropertyName("input")]
    public required JsonElement Input { get; init; }
}

public sealed record NodeEndWatchEvent : WatchEvent
{
    [JsonPropertyName("node")]
    public required NodeAddress Node { get; init; }
    [JsonPropertyName("outcome")]
    public required WorkerOutcome Outcome { get; init; }
}

public sealed record BookmarkWatchEvent : WatchEvent;

public sealed record FaultWatchEvent : WatchEvent
{
    [JsonPropertyName("fault")]
    public required BackendFault Fault { get; init; }
}

public sealed record FinishedWatchEvent : WatchEvent
{
    [JsonPropertyName("final_status")]
    public required ClusterStatus FinalStatus { get; init; }
    [JsonPropertyName("stop_mode"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StopMode? StopMode { get; init; }
}

[WireContract("AdmissionTransition")]
public sealed record AdmissionTransition : NativeContract
{
    [JsonPropertyName("runId")]
    public required RunId RunId { get; init; }
    [JsonPropertyName("spec")]
    public required GraphSpec Spec { get; init; }
    [JsonPropertyName("seedInput")]
    public required JsonElement SeedInput { get; init; }
}

[WireContract("NodeAddress")]
public sealed record NodeAddress : NativeContract
{
    [JsonPropertyName("node")]
    public required NodeName Node { get; init; }
    [JsonPropertyName("attempt")]
    public required PositiveInteger Attempt { get; init; }
}

/// <summary>A descriptive backend fault. Its retry and action fields never perform or authorize a retry.</summary>
[WireContract("BackendFault")]
public sealed record BackendFault : NativeContract
{
    [JsonPropertyName("eventId")]
    public required string EventId { get; init; }
    [JsonPropertyName("code")]
    public required FaultCode Code { get; init; }
    [JsonPropertyName("consequence")]
    public required FaultConsequence Consequence { get; init; }
    [JsonPropertyName("retry")]
    public required FaultRetryDisposition Retry { get; init; }
    [JsonPropertyName("action")]
    public required FaultAction Action { get; init; }
    [JsonPropertyName("severity")]
    public required FaultSeverity Severity { get; init; }
    [JsonPropertyName("summary")]
    public required string Summary { get; init; }
    [JsonPropertyName("source")]
    public required ImmutableArray<FaultSourceFrame> Source { get; init; }
    /// <summary>Optional opaque execution correlation, bounded at 256 characters rather than ExecutionRef's 128 bytes.</summary>
    [JsonPropertyName("executionRef"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExecutionRef { get; init; }
}

[WireContract("FaultSourceFrame")]
public sealed record FaultSourceFrame : NativeContract
{
    [JsonPropertyName("component")]
    public required string Component { get; init; }
}

[WireContract("FaultCode")]
public enum FaultCode
{
    [JsonStringEnumMemberName("unavailable")] Unavailable,
    [JsonStringEnumMemberName("resource_exhausted")] ResourceExhausted,
    [JsonStringEnumMemberName("deadline_exceeded")] DeadlineExceeded,
    [JsonStringEnumMemberName("permission_denied")] PermissionDenied,
    [JsonStringEnumMemberName("failed_precondition")] FailedPrecondition,
    [JsonStringEnumMemberName("not_found")] NotFound,
    [JsonStringEnumMemberName("aborted")] Aborted,
    [JsonStringEnumMemberName("internal")] Internal,
    [JsonStringEnumMemberName("unknown")] Unknown
}

[WireContract("FaultConsequence")]
public enum FaultConsequence
{
    [JsonStringEnumMemberName("turn_failed")] TurnFailed,
    [JsonStringEnumMemberName("run_failed")] RunFailed,
    [JsonStringEnumMemberName("run_degraded")] RunDegraded,
    [JsonStringEnumMemberName("no_observable_effect")] NoObservableEffect
}

[WireContract("FaultRetryDisposition")]
public enum FaultRetryDisposition
{
    [JsonStringEnumMemberName("retryable")] Retryable,
    [JsonStringEnumMemberName("retryable_after_backoff")] RetryableAfterBackoff,
    [JsonStringEnumMemberName("not_retryable")] NotRetryable,
    [JsonStringEnumMemberName("indeterminate")] Indeterminate
}

[WireContract("FaultAction")]
public enum FaultAction
{
    [JsonStringEnumMemberName("none")] None,
    [JsonStringEnumMemberName("retry")] Retry,
    [JsonStringEnumMemberName("wait")] Wait,
    [JsonStringEnumMemberName("escalate")] Escalate,
    [JsonStringEnumMemberName("abort")] Abort
}

[WireContract("FaultSeverity")]
public enum FaultSeverity
{
    [JsonStringEnumMemberName("info")] Info,
    [JsonStringEnumMemberName("warning")] Warning,
    [JsonStringEnumMemberName("error")] Error,
    [JsonStringEnumMemberName("critical")] Critical
}

/// <summary>Cluster-wide logs have no caller filters.</summary>
[WireContract("LogsParams")]
public sealed record LogsParams : NativeContract;

/// <summary>Cluster logs are future-only: no run, cursor or replay.</summary>
[WireContract("LogsResult")]
public sealed record LogsResult : NativeContract
{
    [JsonPropertyName("subscriptionId")]
    public required SubscriptionId SubscriptionId { get; init; }
}

[WireContract("LogEventNotification")]
public sealed record LogEventNotification : NativeContract
{
    [JsonPropertyName("subscriptionId")]
    public required SubscriptionId SubscriptionId { get; init; }
    [JsonPropertyName("record")]
    public required LogRecord Record { get; init; }
}

/// <summary>Cluster agent attachment has no run selector.</summary>
[WireContract("AgentAttachParams")]
public sealed record AgentAttachParams : NativeContract
{
    [JsonPropertyName("execution")]
    public required ExecutionRef Execution { get; init; }
}

[WireContract("AgentAttachResult")]
public sealed record AgentAttachResult : NativeContract
{
    [JsonPropertyName("subscriptionId")]
    public required SubscriptionId SubscriptionId { get; init; }
}

[WireContract("AgentAttachEventNotification")]
public sealed record AgentAttachEventNotification : NativeContract
{
    [JsonPropertyName("subscriptionId")]
    public required SubscriptionId SubscriptionId { get; init; }
    [JsonPropertyName("event")]
    public required AgentAttachEvent Event { get; init; }
}

/// <summary>The acknowledged run identity. Native deduplication can return an earlier run's ID.</summary>
[WireContract("RunSubmitResult")]
public sealed record RunSubmitResult : NativeContract
{
    [JsonPropertyName("runId")]
    public required RunId RunId { get; init; }
}
