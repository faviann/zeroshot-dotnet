using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zeroshot.Native.Contracts;

[WireContract("Generation")]
[JsonConverter(typeof(SafeIntegerConverter<Generation>))]
public readonly record struct Generation : ISafeInteger<Generation>
{
    public const ulong Maximum = 9_007_199_254_740_991;
    public ulong Value { get; }
    public Generation(ulong value) { if (value > Maximum) throw new ArgumentOutOfRangeException(nameof(value)); Value = value; }
    static Generation ISafeInteger<Generation>.Create(ulong value) => new(value);
    public static implicit operator Generation(ulong value) => new(value);
}

[WireContract("PositiveInteger")]
[JsonConverter(typeof(SafeIntegerConverter<PositiveInteger>))]
public readonly record struct PositiveInteger : ISafeInteger<PositiveInteger>
{
    public ulong Value { get; }
    public PositiveInteger(ulong value) { if (value == 0 || value > Generation.Maximum) throw new ArgumentOutOfRangeException(nameof(value)); Value = value; }
    static PositiveInteger ISafeInteger<PositiveInteger>.Create(ulong value) => new(value);
    public static implicit operator PositiveInteger(ulong value) => new(value);
}

[WireContract("ByteLength")]
[JsonConverter(typeof(SafeIntegerConverter<ByteLength>))]
public readonly record struct ByteLength : ISafeInteger<ByteLength>
{
    public ulong Value { get; }
    public ByteLength(ulong value) { if (value > Generation.Maximum) throw new ArgumentOutOfRangeException(nameof(value)); Value = value; }
    static ByteLength ISafeInteger<ByteLength>.Create(ulong value) => new(value);
    public static implicit operator ByteLength(ulong value) => new(value);
}

internal interface ISafeInteger<T> where T : ISafeInteger<T>
{
    ulong Value { get; }
    static abstract T Create(ulong value);
}

internal sealed class SafeIntegerConverter<T> : JsonConverter<T> where T : ISafeInteger<T>
{
    public override T Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => T.Create(new NativeUnsignedConverter().Read(ref reader, typeof(ulong), options));
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) => writer.WriteNumberValue(T.Create(value.Value).Value);
}

/// <summary>An OECP correlation ID: an opaque string or a signed 64-bit integer.</summary>
[WireContract("RequestId")]
[JsonConverter(typeof(RequestIdConverter))]
public readonly record struct RequestId
{
    public string? Text { get; }
    public long? Number { get; }
    public RequestId(string text) { ArgumentNullException.ThrowIfNull(text); Text = text; }
    public RequestId(long number) { Number = number; }
    public override string ToString() => nameof(RequestId);
}

internal sealed class RequestIdConverter : JsonConverter<RequestId>
{
    public override RequestId Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return new(reader.GetString()!);
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var number)) return new(number);
        throw new JsonException("A request ID must be a string or signed integer.");
    }
    public override void Write(Utf8JsonWriter writer, RequestId value, JsonSerializerOptions options)
    {
        if (value.Text is { } text) writer.WriteStringValue(text);
        else if (value.Number is { } number) writer.WriteNumberValue(number);
        else throw new JsonException("A request ID must be a string or signed integer.");
    }
}
