// Hosted run lifecycle wire shapes from native 10.9.0 / 75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa
// (openengine-cluster-protocol native_v2_hosted.rs).
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zeroshot.Native.Contracts;

/// <summary>
/// Native's untagged hosted lifecycle: the host-only strict <c>{"phase":"queued"}</c>, or a projected
/// target <see cref="RunStatus"/>. Neither queued nor stopping is terminal.
/// </summary>
[JsonConverter(typeof(HostedRunStatusConverter))]
public abstract record HostedRunStatus : TargetHttpContract;

/// <summary>The host owns the run before its target exists.</summary>
public sealed record QueuedHostedRunStatus : HostedRunStatus;

public sealed record TargetHostedRunStatus : HostedRunStatus
{
    public required RunStatus Status { get; init; }
}

/// <summary>Hosted status, list entry and force result. Status does not prove a submission key or request digest.</summary>
public sealed record HostedRunStatusResult : TargetHttpContract
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
    public required HostedRunStatus Status { get; init; }
    [JsonPropertyName("workspaceRecovery")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Optional<WorkspaceRecovery> WorkspaceRecovery { get; init; }
}

public sealed record HostedRunListResult : TargetHttpContract
{
    [JsonPropertyName("runs")]
    public required ImmutableArray<HostedRunStatusResult> Runs { get; init; }
}

/// <summary>One hosted watch record. Unlike status, it never carries <c>workspaceRecovery</c>.</summary>
public sealed record HostedRunWatchEventNotification : TargetHttpContract
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
    public required HostedRunStatus Status { get; init; }
}

internal sealed class HostedRunStatusConverter : JsonConverter<HostedRunStatus>
{
    public override HostedRunStatus Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("phase", out var phase) &&
            phase.ValueKind == JsonValueKind.String && phase.GetString() == "queued")
            return root.EnumerateObject().Count() == 1 ? new QueuedHostedRunStatus() : throw new JsonException();
        return new TargetHostedRunStatus { Status = root.Deserialize<RunStatus>(options) ?? throw new JsonException() };
    }

    public override void Write(Utf8JsonWriter writer, HostedRunStatus value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case QueuedHostedRunStatus:
                writer.WriteStartObject(); writer.WriteString("phase", "queued"); writer.WriteEndObject();
                break;
            case TargetHostedRunStatus target:
                JsonSerializer.Serialize(writer, target.Status ?? throw new JsonException(), options);
                break;
            default: throw new JsonException();
        }
    }
}
