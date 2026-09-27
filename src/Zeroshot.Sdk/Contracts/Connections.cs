// Hosted connection-management wire shapes from native 10.9.0 / 75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa.
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zeroshot.Native.Contracts;

public enum ConnectionScope
{
    [JsonStringEnumMemberName("user")]
    User,
    [JsonStringEnumMemberName("org")]
    Org
}

/// <summary>Kind strings named by native. Summary kinds are open; hosts may report others.</summary>
public static class ConnectionKinds
{
    public const string Static = "static";
    public const string GithubAppInstallation = "github-app-installation";
}

public sealed record ConnectionListRequest : TargetHttpContract
{
    [JsonPropertyName("scope")]
    public required ConnectionScope Scope { get; init; }
}

/// <summary>Stores caller-supplied static secret values. Default formatting omits them.</summary>
public sealed record ConnectionSetRequest : TargetHttpContract
{
    [JsonPropertyName("key")]
    public required ConnectionKey Key { get; init; }
    [JsonPropertyName("scope")]
    public required ConnectionScope Scope { get; init; }
    [JsonPropertyName("values")]
    public required ImmutableDictionary<string, string> Values { get; init; }
}

public sealed record ConnectionDeleteRequest : TargetHttpContract
{
    [JsonPropertyName("key")]
    public required ConnectionKey Key { get; init; }
    [JsonPropertyName("scope")]
    public required ConnectionScope Scope { get; init; }
}

/// <summary>Secret-free record metadata: field names only.</summary>
public sealed record ConnectionSummary : TargetHttpContract
{
    [JsonPropertyName("key")]
    public required ConnectionKey Key { get; init; }
    [JsonPropertyName("scope")]
    public required ConnectionScope Scope { get; init; }
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }
    [JsonPropertyName("fields")]
    public required ImmutableArray<EnvironmentVariableName> Fields { get; init; }
}

public sealed record ConnectionListResult : TargetHttpContract
{
    [JsonPropertyName("connections")]
    public required ImmutableArray<ConnectionSummary> Connections { get; init; }
}

public sealed record ConnectionMutationResult : TargetHttpContract
{
    [JsonPropertyName("connection")]
    public required ConnectionSummary Connection { get; init; }
}

public sealed record ConnectionDeleteResult : TargetHttpContract
{
    [JsonPropertyName("deleted")]
    public required bool Deleted { get; init; }
}

// Native StaticConnectionValues::new, shared by run credentials and stored connections.
internal static class StaticConnectionValues
{
    // Native RunConnectionValues: fresh per-run static values keyed by connection.
    internal static void ValidateRun(ImmutableDictionary<string, ImmutableDictionary<string, string>> connections)
    {
        foreach (var (key, values) in connections)
        {
            _ = new ConnectionKey(key);
            Validate(values);
        }
    }

    internal static void Validate(ImmutableDictionary<string, string>? values)
    {
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
