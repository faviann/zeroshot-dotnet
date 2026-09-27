using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zeroshot.Native.Contracts;

/// <summary>Fresh, ephemeral credentials for one submission, excluded from retained submission identity.</summary>
public record TargetRunCredentials : TargetHttpContract
{
    [JsonPropertyName("connections")]
    public required ImmutableDictionary<string, ImmutableDictionary<string, string>> Connections { get; init; }
    [JsonPropertyName("connectionResolver")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TargetConnectionResolver? ConnectionResolver { get; init; }
    [JsonPropertyName("githubToken")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GithubToken { get; init; }

    internal void Validate() => StaticConnectionValues.ValidateRun(Connections);
}

/// <summary>The complete fixed target HTTP envelope; no identity or content is generated at send time.</summary>
public sealed record TargetRunRequest : TargetRunCredentials
{
    [JsonPropertyName("runId")]
    public required RunId RunId { get; init; }
    [JsonPropertyName("submission")]
    public required RunSubmission Submission { get; init; }

    internal static void ValidateRunId(RunId runId)
    {
        if (!IsCanonicalRunId(runId.Value)) throw new JsonException("Target submission requires a canonical UUIDv7 run ID.");
    }

    // Native is_canonical_uuid_v7: lowercase hyphenated text, version 7 and an RFC variant.
    internal static bool IsCanonicalRunId(string id)
        => Guid.TryParseExact(id, "D", out var guid) && guid.ToString("D") == id && id[14] == '7' && "89ab".IndexOf(id[19]) >= 0;
}

/// <summary>Run-scoped callback authority. Endpoint and key admission remain native responsibilities.</summary>
public sealed record TargetConnectionResolver : TargetHttpContract
{
    [JsonPropertyName("endpoint")]
    public required string Endpoint { get; init; }
    [JsonPropertyName("bearerToken")]
    public required string BearerToken { get; init; }
    [JsonPropertyName("keys")]
    public required ImmutableArray<ConnectionKey> Keys { get; init; }
    [JsonPropertyName("sourceConnection")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ConnectionKey? SourceConnection { get; init; }
}

public sealed record TargetRunReceipt : TargetHttpContract
{
    [JsonPropertyName("runId")]
    public required RunId RunId { get; init; }
}
