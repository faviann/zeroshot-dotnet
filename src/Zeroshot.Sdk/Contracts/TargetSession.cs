using System.Globalization;
using System.Text;
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

/// <summary>
/// A refusal from a direct target's UI router (native profile_ui.rs ApiError): <c>{code,message}</c>.
/// Its message is unconstrained native text, bounded only by the operation's problem-body limit, and can
/// carry admission detail; inspect it explicitly.
/// </summary>
public sealed record UiProblem : TargetHttpContract
{
    [JsonPropertyName("code")]
    public required string Code { get; init; }
    [JsonPropertyName("message")]
    public required string Message { get; init; }

    internal void Validate()
    {
        if (string.IsNullOrEmpty(Code) || Code.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.')))
            throw new JsonException();
    }
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
            (details.ValueKind != JsonValueKind.Object || NativeJsonBytes(details) > 60 * 1024))
            throw new JsonException();
    }

    // serde_json::Value is measured after compact serialization, independently of
    // incoming whitespace/escapes and .NET's additional Unicode escaping.
    private static long NativeJsonBytes(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => 2 + value.EnumerateObject().GroupBy(p => p.Name, StringComparer.Ordinal)
            .Select(group => group.Last()).Select((p, index) =>
                StringBytes(p.Name) + 1 + NativeJsonBytes(p.Value) + (index == 0 ? 0 : 1)).Sum(),
        JsonValueKind.Array => 2 + value.EnumerateArray().Select((item, index) =>
            NativeJsonBytes(item) + (index == 0 ? 0 : 1)).Sum(),
        JsonValueKind.String => StringBytes(value.GetString()!),
        JsonValueKind.Number => NumberBytes(value),
        JsonValueKind.False => 5,
        _ => 4 // true or null; undefined is rejected by NativeJson.
    };

    private static long StringBytes(string value)
    {
        long bytes = 2 + Encoding.UTF8.GetByteCount(value);
        foreach (var character in value)
            bytes += character switch
            {
                '"' or '\\' or '\b' or '\f' or '\n' or '\r' or '\t' => 1,
                < ' ' => 5,
                _ => 0
            };
        return bytes;
    }

    private static int NumberBytes(JsonElement value)
    {
        var token = value.GetRawText();
        if (token != "-0" && token.IndexOfAny(['.', 'e', 'E']) < 0 &&
            (value.TryGetInt64(out _) || value.TryGetUInt64(out _))) return token.Length;
        var number = value.GetDouble();
        var sign = double.IsNegative(number) ? 1 : 0;
        if (number == 0) return sign + 3; // serde_json preserves floating-point 0.0 and -0.0.
        var parts = Math.Abs(number).ToString("R", CultureInfo.InvariantCulture).Split('E');
        var exponent = parts.Length == 2 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 0;
        var point = parts[0].IndexOf('.');
        var digits = parts[0].Replace(".", "");
        exponent += (point < 0 ? parts[0].Length : point) - 1 - (digits.Length - digits.TrimStart('0').Length);
        var length = digits.Trim('0').Length;
        // Pinned serde_json 1.0.150 / zmij 1.0.23: shortest f64 digits, fixed
        // notation for decimal exponents -5..15, otherwise e and an explicit sign.
        if (exponent is >= -5 and <= 15)
            return sign + (length - 1 <= exponent ? exponent + 3 : exponent >= 0 ? length + 1 : 1 - exponent + length);
        return sign + length + (length > 1 ? 1 : 0) + 2 + Math.Abs(exponent).ToString(CultureInfo.InvariantCulture).Length;
    }
}
