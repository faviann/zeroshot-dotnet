// Hosted merge-plan wire shapes from native 10.9.0 / 75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa.
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zeroshot.Native.Contracts;

public enum MergePlanState
{
    [JsonStringEnumMemberName("queued")]
    Queued,
    [JsonStringEnumMemberName("running")]
    Running,
    [JsonStringEnumMemberName("succeeded")]
    Succeeded,
    [JsonStringEnumMemberName("failed")]
    Failed,
    [JsonStringEnumMemberName("cancelled")]
    Cancelled,
    [JsonStringEnumMemberName("expired")]
    Expired
}

public enum MergePlanRunState
{
    [JsonStringEnumMemberName("blocked")]
    Blocked,
    [JsonStringEnumMemberName("materializing")]
    Materializing,
    [JsonStringEnumMemberName("queued")]
    Queued,
    [JsonStringEnumMemberName("provisioning")]
    Provisioning,
    [JsonStringEnumMemberName("running")]
    Running,
    [JsonStringEnumMemberName("cancelling")]
    Cancelling,
    [JsonStringEnumMemberName("succeeded")]
    Succeeded,
    [JsonStringEnumMemberName("failed")]
    Failed,
    [JsonStringEnumMemberName("cancelled")]
    Cancelled,
    [JsonStringEnumMemberName("expired")]
    Expired
}

public sealed record MergePlanSource : TargetHttpContract
{
    [JsonPropertyName("repository")]
    public required SourceRepositoryId Repository { get; init; }
    [JsonPropertyName("branch")]
    public required SourceBranchId Branch { get; init; }
}

/// <summary>One plan run; the host assigns its source revision only after its dependencies pass.</summary>
public sealed record MergePlanRunRequest : TargetHttpContract
{
    [JsonPropertyName("name")]
    public required RunProfileName Name { get; init; }
    /// <summary>Sent verbatim; null omits the field, which native reads as no dependencies.</summary>
    [JsonPropertyName("needs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImmutableArray<RunProfileName>? Needs { get; init; }
    [JsonPropertyName("initialInput")]
    public required JsonElement InitialInput { get; init; }
}

/// <summary>An atomic hosted merge-plan submission of 1-64 runs. Default formatting omits connection values and the GitHub token.</summary>
public sealed record MergePlanSubmitRequest : TargetHttpContract
{
    internal const int MaxRuns = 64;
    [JsonPropertyName("submissionKey")]
    public required IdempotencyKey SubmissionKey { get; init; }
    [JsonPropertyName("title")]
    public required RunTitle Title { get; init; }
    /// <summary>Sent verbatim; the host owns its deadline rules.</summary>
    [JsonPropertyName("expiresAt")]
    public required string ExpiresAt { get; init; }
    [JsonPropertyName("source")]
    public required MergePlanSource Source { get; init; }
    [JsonPropertyName("profile")]
    public required RunProfileSelector Profile { get; init; }
    [JsonPropertyName("runs")]
    public required ImmutableArray<MergePlanRunRequest> Runs { get; init; }
    /// <summary>Shared by every plan run. Null omits the field so the host may apply its default; an empty definition selects the base environment.</summary>
    [JsonPropertyName("environment")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RuntimeEnvironment? Environment { get; init; }
    /// <summary>Fresh static values for the plan's runs. Null omits the field, which native reads as none.</summary>
    [JsonPropertyName("connections")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImmutableDictionary<string, ImmutableDictionary<string, string>>? Connections { get; init; }
    [JsonPropertyName("githubToken")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GithubToken { get; init; }
}

/// <summary>A hosted merge plan's aggregate state and its member runs.</summary>
public sealed record MergePlan : TargetHttpContract
{
    [JsonPropertyName("planId")]
    public required RunId PlanId { get; init; }
    [JsonPropertyName("title")]
    public required RunTitle Title { get; init; }
    [JsonPropertyName("state")]
    public required MergePlanState State { get; init; }
    [JsonPropertyName("repository")]
    public required SourceRepositoryId Repository { get; init; }
    [JsonPropertyName("branch")]
    public required SourceBranchId Branch { get; init; }
    [JsonPropertyName("submittedAt")]
    public required string SubmittedAt { get; init; }
    [JsonPropertyName("expiresAt")]
    public required string ExpiresAt { get; init; }
    [JsonPropertyName("runs")]
    public required ImmutableArray<MergePlanRunStatus> Runs { get; init; }
}

/// <summary>One member run. Each nullable field must be present, even when null.</summary>
public sealed record MergePlanRunStatus : TargetHttpContract
{
    [JsonPropertyName("name")]
    public required RunProfileName Name { get; init; }
    [JsonPropertyName("runId")]
    public required RunId RunId { get; init; }
    [JsonPropertyName("state")]
    public required MergePlanRunState State { get; init; }
    [JsonPropertyName("needs")]
    public required ImmutableArray<RunProfileName> Needs { get; init; }
    [JsonPropertyName("sourceRevision")]
    public required SourceRevisionId? SourceRevision { get; init; }
    [JsonPropertyName("readyAt")]
    public required string? ReadyAt { get; init; }
    [JsonPropertyName("queueExpiresAt")]
    public required string? QueueExpiresAt { get; init; }
    [JsonPropertyName("terminalAt")]
    public required string? TerminalAt { get; init; }
    [JsonPropertyName("waitingReason")]
    public required string? WaitingReason { get; init; }
    [JsonPropertyName("errorCode")]
    public required string? ErrorCode { get; init; }
}
