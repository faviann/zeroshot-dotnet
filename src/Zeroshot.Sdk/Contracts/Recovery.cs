// Native 10.9.0 / 75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa workspace checkpoint and recovery contracts.
using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Zeroshot.Native.Contracts;

/// <summary>One checkpoint page. After is exclusive; an omitted limit means native's 50, and 1..100 are accepted.</summary>
[WireContract("RunCheckpointsParams")]
public sealed record RunCheckpointsParams : NativeContract
{
    public const uint DefaultLimit = 50;
    public const uint MaximumLimit = 100;
    [JsonPropertyName("runId")]
    public required RunId RunId { get; init; }
    [JsonPropertyName("after")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CheckpointId? After { get; init; }
    [JsonPropertyName("limit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public uint? Limit { get; init; }
}

/// <summary>NextAfter is the last returned checkpoint only when more remain.</summary>
[WireContract("RunCheckpointsResult")]
public sealed record RunCheckpointsResult : NativeContract
{
    [JsonPropertyName("runId")]
    public required RunId RunId { get; init; }
    [JsonPropertyName("checkpoints")]
    public required ImmutableArray<RunCheckpoint> Checkpoints { get; init; }
    [JsonPropertyName("nextAfter")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CheckpointId? NextAfter { get; init; }
}

/// <summary>A secret-free entry point before a node or outer concurrent group starts.</summary>
[WireContract("RunCheckpoint")]
public sealed record RunCheckpoint : NativeContract
{
    [JsonPropertyName("checkpointId")]
    public required CheckpointId CheckpointId { get; init; }
    [JsonPropertyName("sequence")]
    public required PositiveInteger Sequence { get; init; }
    [JsonPropertyName("node")]
    public required NodeName Node { get; init; }
    [JsonPropertyName("mapIndices")]
    public required ImmutableArray<Generation> MapIndices { get; init; }
    [JsonPropertyName("loopIterations")]
    public required ImmutableArray<Generation> LoopIterations { get; init; }
    [JsonPropertyName("createdAt")]
    public required UnixTimestampMillis CreatedAt { get; init; }
}

/// <summary>Successor selection. Omitting it and sending Restart both restart on the latest retained workspace.</summary>
[WireContract("RunResumeFrom")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(RestartResumeFrom), "restart")]
[JsonDerivedType(typeof(CheckpointResumeFrom), "checkpoint")]
public abstract record RunResumeFrom : NativeContract;

[WireContract("RunResumeFrom")]
public sealed record RestartResumeFrom : RunResumeFrom;

[WireContract("RunResumeFrom")]
public sealed record CheckpointResumeFrom : RunResumeFrom
{
    [JsonPropertyName("checkpointId")]
    public required CheckpointId CheckpointId { get; init; }
}

// Callers pass fresh credentials separately; this complete envelope exists only while sending.
[WireContract("RunResumeParams")]
internal sealed record RunResumeParams : NativeContract
{
    [JsonPropertyName("runId")]
    public required RunId RunId { get; init; }
    [JsonPropertyName("successorRunId")]
    public required RunId SuccessorRunId { get; init; }
    [JsonPropertyName("from")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RunResumeFrom? From { get; init; }
    [JsonPropertyName("connections")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImmutableDictionary<string, ImmutableDictionary<string, string>>? Connections { get; init; }
    [JsonPropertyName("connectionResolver")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TargetConnectionResolver? ConnectionResolver { get; init; }
    [JsonPropertyName("githubToken")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GithubToken { get; init; }
}

/// <summary>The admitted successor and its exact source run.</summary>
[WireContract("RunResumeResult")]
public sealed record RunResumeResult : NativeContract
{
    [JsonPropertyName("runId")]
    public required RunId RunId { get; init; }
    [JsonPropertyName("resumedFrom")]
    public required RunId ResumedFrom { get; init; }
}

[WireContract("RunDiscardWorkspaceParams")]
public sealed record RunDiscardWorkspaceParams : NativeContract
{
    [JsonPropertyName("runId")]
    public required RunId RunId { get; init; }
}

/// <summary>False when no recoverable workspace was retained. Run and history identity are unaffected.</summary>
[WireContract("RunDiscardWorkspaceResult")]
public sealed record RunDiscardWorkspaceResult : NativeContract
{
    [JsonPropertyName("runId")]
    public required RunId RunId { get; init; }
    [JsonPropertyName("discarded")]
    public required bool Discarded { get; init; }
}
