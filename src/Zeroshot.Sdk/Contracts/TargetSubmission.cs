using System.Collections.Immutable;
using System.Text;
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

    internal void Validate()
    {
        foreach (var (key, values) in Connections)
        {
            _ = new ConnectionKey(key);
            if (values is null || values.Count is < 1 or > 64) throw new JsonException();
            long bytes = 0;
            foreach (var (name, value) in values)
            {
                _ = new EnvironmentVariableName(name);
                if (string.IsNullOrEmpty(value) || value.Contains('\0') || Encoding.UTF8.GetByteCount(value) > 64 * 1024)
                    throw new JsonException();
                bytes += Encoding.UTF8.GetByteCount(name) + Encoding.UTF8.GetByteCount(value);
            }
            if (bytes > 256 * 1024) throw new JsonException();
        }
    }
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
        var id = runId.Value;
        if (!Guid.TryParseExact(id, "D", out var guid) || guid.ToString("D") != id || id[14] != '7' || "89ab".IndexOf(id[19]) < 0)
            throw new JsonException("Target submission requires a canonical UUIDv7 run ID.");
    }
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
