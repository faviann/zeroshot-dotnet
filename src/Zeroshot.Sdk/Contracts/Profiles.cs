// Hosted run-profile wire shapes from native 10.9.0 / 75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa.
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zeroshot.Native.Contracts;

public enum RunProfileScope
{
    [JsonStringEnumMemberName("user")]
    User,
    [JsonStringEnumMemberName("org")]
    Org
}

/// <summary>A stored profile with its complete graph and runtime definitions.</summary>
public sealed record RunProfile : TargetHttpContract
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }
    [JsonPropertyName("name")]
    public required RunProfileName Name { get; init; }
    [JsonPropertyName("scope")]
    public required RunProfileScope Scope { get; init; }
    [JsonPropertyName("graph")]
    public required GraphSpec Graph { get; init; }
    [JsonPropertyName("runtime")]
    public required RuntimePlan Runtime { get; init; }
    [JsonPropertyName("isDefault")]
    public required bool IsDefault { get; init; }
}

public sealed record RunProfileSummary : TargetHttpContract
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }
    [JsonPropertyName("name")]
    public required RunProfileName Name { get; init; }
    [JsonPropertyName("scope")]
    public required RunProfileScope Scope { get; init; }
    [JsonPropertyName("isDefault")]
    public required bool IsDefault { get; init; }
}

public sealed record RunProfileListRequest : TargetHttpContract
{
    [JsonPropertyName("scope")]
    public required RunProfileScope Scope { get; init; }
}

public sealed record RunProfileSelector : TargetHttpContract
{
    [JsonPropertyName("scope")]
    public required RunProfileScope Scope { get; init; }
    [JsonPropertyName("name")]
    public required RunProfileName Name { get; init; }
}

public sealed record RunProfileSetRequest : TargetHttpContract
{
    [JsonPropertyName("name")]
    public required RunProfileName Name { get; init; }
    [JsonPropertyName("scope")]
    public required RunProfileScope Scope { get; init; }
    [JsonPropertyName("graph")]
    public required GraphSpec Graph { get; init; }
    [JsonPropertyName("runtime")]
    public required RuntimePlan Runtime { get; init; }
    [JsonPropertyName("setDefault")]
    public bool SetDefault { get; init; }
}

/// <summary>Selects the scope's default profile; an omitted name clears it.</summary>
public sealed record RunProfileDefaultRequest : TargetHttpContract
{
    [JsonPropertyName("scope")]
    public required RunProfileScope Scope { get; init; }
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RunProfileName? Name { get; init; }
}

public sealed record RunProfileListResult : TargetHttpContract
{
    [JsonPropertyName("profiles")]
    public required ImmutableArray<RunProfileSummary> Profiles { get; init; }
}

public sealed record RunProfileMutationResult : TargetHttpContract
{
    [JsonPropertyName("profile")]
    public required RunProfile Profile { get; init; }
}

public sealed record RunProfileDeleteResult : TargetHttpContract
{
    [JsonPropertyName("deleted")]
    public required bool Deleted { get; init; }
}

/// <summary>The scope's default profile name, or null when it has none.</summary>
public sealed record RunProfileDefaultResult : TargetHttpContract
{
    [JsonPropertyName("scope")]
    public required RunProfileScope Scope { get; init; }
    // Native Option without serde default: a missing name also reads as None.
    [JsonPropertyName("name")]
    public RunProfileName? Name { get; init; }
}

/// <summary>One hosted run resolved from a stored profile. Default formatting omits connection values and the GitHub token.</summary>
public sealed record RunProfileRunRequest : TargetHttpContract
{
    [JsonPropertyName("runId")]
    public required RunId RunId { get; init; }
    [JsonPropertyName("profile")]
    public required RunProfileSelector Profile { get; init; }
    [JsonPropertyName("title")]
    public required RunTitle Title { get; init; }
    [JsonPropertyName("initialInput")]
    public required JsonElement InitialInput { get; init; }
    [JsonPropertyName("source")]
    public required ResolvedSource Source { get; init; }
    [JsonPropertyName("submissionKey")]
    public required IdempotencyKey SubmissionKey { get; init; }
    /// <summary>Null omits the field so the host may apply its default; an empty definition selects the base environment.</summary>
    [JsonPropertyName("environment")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RuntimeEnvironment? Environment { get; init; }
    /// <summary>Fresh static values for this run; required, possibly empty.</summary>
    [JsonPropertyName("connections")]
    public required ImmutableDictionary<string, ImmutableDictionary<string, string>> Connections { get; init; }
    [JsonPropertyName("githubToken")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GithubToken { get; init; }
}
