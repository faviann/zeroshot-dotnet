using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zeroshot.Native.Contracts;

/// <summary>Source-backed target HTTP shapes absent from the generated OECP schema.</summary>
public abstract record TargetHttpContract : NativeContract;

public sealed record TargetOecpSessionRequest : TargetHttpContract
{
    [JsonPropertyName("runId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RunId? RunId { get; init; }
}

/// <summary>Session authority returned by the target, separate from control credentials.</summary>
public sealed record TargetOecpSession : TargetHttpContract
{
    [JsonPropertyName("endpoint")]
    public required string Endpoint { get; init; }
    [JsonPropertyName("bearerToken")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BearerToken { get; init; }
}

/// <summary>Bounded remote refusal facts. Explicit property inspection may reveal remote data.</summary>
public sealed record TargetHttpProblem : TargetHttpContract
{
    [JsonPropertyName("code")]
    public required string Code { get; init; }
    [JsonPropertyName("message")]
    public required string Message { get; init; }
    [JsonPropertyName("details")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Details { get; init; }

    internal void Validate()
    {
        if (string.IsNullOrEmpty(Code) || Code.Length > 128 ||
            Code.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.')) ||
            string.IsNullOrEmpty(Message) || Encoding.UTF8.GetByteCount(Message) > 1024 || Message.Any(char.IsControl))
            throw new JsonException();
        if (Details is { } details && details.ValueKind != JsonValueKind.Null &&
            (details.ValueKind != JsonValueKind.Object || JsonSerializer.SerializeToUtf8Bytes(details,
                new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }).Length > 60 * 1024))
            throw new JsonException();
    }
}
