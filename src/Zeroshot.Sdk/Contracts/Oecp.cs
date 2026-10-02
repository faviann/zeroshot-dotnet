// Native 10.9.0 / 75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa OECP inspection contracts.
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zeroshot.Native.Contracts;

[WireContract("InitializeParams")]
public sealed record InitializeParams : NativeContract
{
    [JsonPropertyName("protocolVersion")]
    public required string ProtocolVersion { get; init; }
}

[WireContract("InitializeResult")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Skip)]
public sealed record InitializeResult : NativeContract
{
    [JsonPropertyName("protocolVersion")]
    public required string ProtocolVersion { get; init; }
    [JsonPropertyName("capabilities")]
    public required ServerCapabilities Capabilities { get; init; }
    [JsonPropertyName("status")]
    public required ClusterStatus Status { get; init; }
}

[WireContract("ServerCapabilities")]
public sealed record ServerCapabilities : NativeContract
{
    [JsonPropertyName("graphProfiles")]
    public ImmutableArray<GraphProfile> GraphProfiles { get; init; } = [];
    [JsonPropertyName("logs")]
    public bool Logs { get; init; }
    [JsonPropertyName("agentAttach")]
    public bool AgentAttach { get; init; }
}

[WireContract("GetParams")]
public sealed record GetParams : NativeContract
{
    [JsonPropertyName("atCursor"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Cursor? AtCursor { get; init; }
}

[WireContract("GetResult")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Skip)]
public sealed record GetResult : NativeContract
{
    [JsonPropertyName("spec")]
    public GraphSpec? Spec { get; init; }
    [JsonPropertyName("status")]
    public required ClusterStatus Status { get; init; }
    [JsonPropertyName("atCursor")]
    public Cursor? AtCursor { get; init; }
    [JsonPropertyName("terminalResult")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TerminalResult? TerminalResult { get; init; }
}

/// <summary>Legacy cluster status returned by initialize and get, distinct from a native run status.</summary>
[WireContract("ClusterStatus")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Skip)]
public sealed record ClusterStatus : NativeContract
{
    [JsonPropertyName("phase")]
    public required Phase Phase { get; init; }
    [JsonPropertyName("observedGeneration")]
    public Generation? ObservedGeneration { get; init; }
    [JsonPropertyName("currentRunId")]
    public RunId? CurrentRunId { get; init; }
    [JsonPropertyName("atCursor")]
    public Cursor? AtCursor { get; init; }
    [JsonPropertyName("operational")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OperationalStatus? Operational { get; init; }
}

[WireContract("Phase")]
public enum Phase
{
    [JsonStringEnumMemberName("empty")] Empty,
    [JsonStringEnumMemberName("admitting")] Admitting,
    [JsonStringEnumMemberName("running")] Running,
    [JsonStringEnumMemberName("finished")] Finished,
    [JsonStringEnumMemberName("deleting")] Deleting
}

[WireContract("OperationalStatus")]
public sealed record OperationalStatus : NativeContract
{
    [JsonPropertyName("labels")]
    public required ImmutableDictionary<Label, Label> Labels { get; init; }
    [JsonPropertyName("logLevel")]
    public required LogLevel LogLevel { get; init; }
    [JsonPropertyName("dispatchState")]
    public required DispatchState DispatchState { get; init; }
    [JsonPropertyName("stopMode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StopMode? StopMode { get; init; }
    [JsonPropertyName("inFlight")]
    [JsonConverter(typeof(OecpUnsigned32Converter))]
    public required uint InFlight { get; init; }
}

[JsonConverter(typeof(NativeStringConverterFactory))]
public sealed record Label : NativeString
{
    public Label(string value) : base(value, ValueRules.Text) { }
}

[WireContract("LogLevel")]
public enum LogLevel
{
    [JsonStringEnumMemberName("trace")] Trace,
    [JsonStringEnumMemberName("debug")] Debug,
    [JsonStringEnumMemberName("info")] Info,
    [JsonStringEnumMemberName("warn")] Warn,
    [JsonStringEnumMemberName("error")] Error
}

[WireContract("DispatchState")]
public enum DispatchState
{
    [JsonStringEnumMemberName("active")] Active,
    [JsonStringEnumMemberName("suspended")] Suspended,
    [JsonStringEnumMemberName("draining")] Draining,
    [JsonStringEnumMemberName("force_stopping")] ForceStopping,
    [JsonStringEnumMemberName("stopped")] Stopped
}

[WireContract("StopMode")]
public enum StopMode
{
    [JsonStringEnumMemberName("drain")] Drain,
    [JsonStringEnumMemberName("force")] Force
}

[WireContract("TerminalResult")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "status")]
[JsonDerivedType(typeof(SucceededTerminalResult), "succeeded")]
[JsonDerivedType(typeof(FailedTerminalResult), "failed")]
public abstract record TerminalResult : NativeContract;

[WireContract("TerminalResult")]
public sealed record SucceededTerminalResult : TerminalResult
{
    [JsonPropertyName("output")]
    public required JsonElement Output { get; init; }
}

[WireContract("TerminalResult")]
public sealed record FailedTerminalResult : TerminalResult
{
    [JsonPropertyName("reason")]
    public required EnumLabel Reason { get; init; }
}

[WireContract("RunListParams")]
public sealed record RunListParams : NativeContract;

[WireContract("RunListResult")]
public sealed record RunListResult : NativeContract
{
    [JsonPropertyName("runs")]
    public required ImmutableArray<RunStatusResult> Runs { get; init; }
}

[WireContract("RunStatusParams")]
public sealed record RunStatusParams : NativeContract
{
    [JsonPropertyName("runId")]
    public required RunId RunId { get; init; }
}

[WireContract("RunStatusResult")]
public sealed record RunStatusResult : NativeContract
{
    [JsonPropertyName("runId")]
    public required RunId RunId { get; init; }
    [JsonPropertyName("title")]
    public required RunTitle Title { get; init; }
    [JsonPropertyName("source")]
    public required ResolvedSource Source { get; init; }
    [JsonPropertyName("size")]
    public required RunSize Size { get; init; }
    [JsonPropertyName("atCursor")]
    public required Cursor AtCursor { get; init; }
    [JsonPropertyName("status")]
    public required RunStatus Status { get; init; }
    [JsonPropertyName("workspaceRecovery")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Optional<WorkspaceRecovery> WorkspaceRecovery { get; init; }

    internal void Require(RunId runId, ResolvedSource? expectedSource)
    {
        if (RunId != runId || (expectedSource is not null && Source != expectedSource)) throw new JsonException();
    }
}

[WireContract("RunForceParams")]
public sealed record RunForceParams : NativeContract
{
    [JsonPropertyName("runId")]
    public required RunId RunId { get; init; }
}

/// <summary>The status projected after native recorded force. Stopping is not terminal.</summary>
[WireContract("RunForceResult")]
public sealed record RunForceResult : NativeContract
{
    [JsonPropertyName("runId")]
    public required RunId RunId { get; init; }
    [JsonPropertyName("title")]
    public required RunTitle Title { get; init; }
    [JsonPropertyName("source")]
    public required ResolvedSource Source { get; init; }
    [JsonPropertyName("size")]
    public required RunSize Size { get; init; }
    [JsonPropertyName("atCursor")]
    public required Cursor AtCursor { get; init; }
    [JsonPropertyName("status")]
    public required RunStatus Status { get; init; }
    [JsonPropertyName("workspaceRecovery")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Optional<WorkspaceRecovery> WorkspaceRecovery { get; init; }

    internal void Require(RunId runId)
    {
        if (RunId != runId) throw new JsonException();
    }
}

/// <summary>Native run status. Finished status contains exactly one terminal result.</summary>
[WireContract("RunStatus")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "phase")]
[JsonDerivedType(typeof(AdmittedRunStatus), "admitted")]
[JsonDerivedType(typeof(RunningRunStatus), "running")]
[JsonDerivedType(typeof(StoppingRunStatus), "stopping")]
[JsonDerivedType(typeof(FinishedRunStatus), "finished")]
public abstract record RunStatus : NativeContract;

[WireContract("RunStatus")]
public sealed record AdmittedRunStatus : RunStatus;

[WireContract("RunStatus")]
public sealed record RunningRunStatus : RunStatus
{
    [JsonPropertyName("activeExecutions")]
    public required ImmutableArray<ActiveExecution> ActiveExecutions { get; init; }
}

[WireContract("RunStatus")]
public sealed record StoppingRunStatus : RunStatus
{
    [JsonPropertyName("activeExecutions")]
    public required ImmutableArray<ActiveExecution> ActiveExecutions { get; init; }
}

[WireContract("RunStatus")]
public sealed record FinishedRunStatus : RunStatus
{
    [JsonPropertyName("terminalResult")]
    public required TerminalResult TerminalResult { get; init; }
    [JsonPropertyName("metadata")]
    public RunMetadata Metadata { get; init; } = new();
}

[WireContract("ActiveExecution")]
public sealed record ActiveExecution : NativeContract
{
    [JsonPropertyName("execution")]
    public required ExecutionRef Execution { get; init; }
    [JsonPropertyName("node")]
    public required NodeName Node { get; init; }
}

[WireContract("RunMetadata")]
public sealed record RunMetadata : NativeContract
{
    [JsonPropertyName("tokenUsage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TokenUsage? TokenUsage { get; init; }
}

[WireContract("TokenUsage")]
public sealed record TokenUsage : NativeContract
{
    [JsonPropertyName("inputTokens")]
    public required TokenCount InputTokens { get; init; }
    [JsonPropertyName("outputTokens")]
    public required TokenCount OutputTokens { get; init; }
    [JsonPropertyName("cacheReadInputTokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TokenCount? CacheReadInputTokens { get; init; }
    [JsonPropertyName("cacheCreationInputTokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TokenCount? CacheCreationInputTokens { get; init; }
    [JsonPropertyName("complete")]
    public required bool Complete { get; init; }
}

[WireContract("TokenCount")]
[JsonConverter(typeof(SafeIntegerConverter<TokenCount>))]
public readonly record struct TokenCount : ISafeInteger<TokenCount>
{
    public ulong Value { get; }
    public TokenCount(ulong value) { if (value > Generation.Maximum) throw new ArgumentOutOfRangeException(nameof(value)); Value = value; }
    static TokenCount ISafeInteger<TokenCount>.Create(ulong value) => new(value);
    public static implicit operator TokenCount(ulong value) => new(value);
}

[WireContract("WorkspaceRecovery")]
public sealed record WorkspaceRecovery : NativeContract
{
    [JsonPropertyName("recoverable")]
    public required bool Recoverable { get; init; }
    [JsonPropertyName("connectionRequirements")]
    public ImmutableDictionary<ConnectionKey, ImmutableArray<EnvironmentVariableName>> ConnectionRequirements { get; init; } = ImmutableDictionary<ConnectionKey, ImmutableArray<EnvironmentVariableName>>.Empty;
    [JsonPropertyName("resumedFrom")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RunId? ResumedFrom { get; init; }
    [JsonPropertyName("successorRunId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RunId? SuccessorRunId { get; init; }
}

/// <summary>The native numeric RPC code and optional open domain code and details.</summary>
[WireContract("JsonRpcError")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Skip)]
public sealed record JsonRpcError : NativeContract
{
    [JsonPropertyName("code")]
    public required long Code { get; init; }
    [JsonPropertyName("message")]
    public required string Message { get; init; }
    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DomainErrorData? Data { get; init; }
}

[WireContract("DomainErrorData")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Skip)]
public sealed record DomainErrorData : NativeContract
{
    [JsonPropertyName("code")]
    public required string Code { get; init; }
    [JsonPropertyName("details")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Details { get; init; }

    /// <summary>The reason of a NO_RETRYABLE_FRONTIER refusal. Null for other codes and unrecognized reasons, which remain in Details.</summary>
    [JsonIgnore]
    public NoRetryableFrontierReason? NoRetryableFrontierReason =>
        Code == "NO_RETRYABLE_FRONTIER" && Details is { ValueKind: JsonValueKind.Object } details &&
        details.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String
            ? reason.GetString() switch
            {
                "exhausted" => Contracts.NoRetryableFrontierReason.Exhausted,
                "success" => Contracts.NoRetryableFrontierReason.Success,
                "active" => Contracts.NoRetryableFrontierReason.Active,
                "consumed" => Contracts.NoRetryableFrontierReason.Consumed,
                _ => null
            }
            : null;
}

internal sealed class OecpUnsigned32Converter : JsonConverter<uint>
{
    public override uint Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => reader.GetUInt32();
    public override void Write(Utf8JsonWriter writer, uint value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
}
