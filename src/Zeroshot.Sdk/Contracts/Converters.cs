using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zeroshot.Native.Contracts;

internal sealed class OptionalConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type type) => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Optional<>);
    public override JsonConverter CreateConverter(Type type, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(OptionalConverter<>).MakeGenericType(type.GetGenericArguments()))!;

    private sealed class OptionalConverter<T> : JsonConverter<Optional<T>>
    {
        public override bool HandleNull => true;
        public override Optional<T> Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => new(JsonSerializer.Deserialize<T>(ref reader, options)!);
        public override void Write(Utf8JsonWriter writer, Optional<T> value, JsonSerializerOptions options)
        {
            if (!value.HasValue) throw new JsonException("An omitted field has no JSON value.");
            JsonSerializer.Serialize(writer, value.Value, options);
        }
    }
}

internal sealed class NativeStringConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type type) => typeof(NativeString).IsAssignableFrom(type);
    public override JsonConverter CreateConverter(Type type, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(StringConverter<>).MakeGenericType(type))!;

    private sealed class StringConverter<T> : JsonConverter<T> where T : NativeString
    {
        private static readonly ConstructorInfo Constructor = typeof(T).GetConstructor([typeof(string)])!;
        private static T Create(string? value)
        {
            if (value is null) throw new JsonException("A native string cannot be null.");
            try { return (T)Constructor.Invoke([value]); }
            catch (TargetInvocationException) { throw new JsonException("Invalid native string."); }
        }
        public override T Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => Create(reader.GetString());
        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) => writer.WriteStringValue(value.Value);
        public override T ReadAsPropertyName(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => Create(reader.GetString());
        public override void WriteAsPropertyName(Utf8JsonWriter writer, T value, JsonSerializerOptions options) => writer.WritePropertyName(value.Value);
    }
}

internal sealed class NativeUnsignedConverter : JsonConverter<ulong>
{
    public override ulong Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        // Native accepts integral floating-point JSON representations too.
        if (reader.TryGetUInt64(out var integer)) return integer;
        if (reader.TryGetDecimal(out var number) && number >= 0 && number <= ulong.MaxValue && decimal.Truncate(number) == number)
            return (ulong)number;
        throw new JsonException("Expected an unsigned integer.");
    }
    public override void Write(Utf8JsonWriter writer, ulong value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
}

internal sealed class NativeUnsigned32Converter : JsonConverter<uint>
{
    public override uint Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
        checked((uint)new NativeUnsignedConverter().Read(ref reader, typeof(ulong), options));
    public override void Write(Utf8JsonWriter writer, uint value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
}

internal sealed class RunSizeConverter : JsonConverter<RunSize>
{
    public override RunSize Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => reader.GetString() switch
    {
        "tiny" or "small" => RunSize.Small,
        "standard" or "medium" => RunSize.Medium,
        "large" => RunSize.Large,
        _ => throw new JsonException("Invalid run size.")
    };
    public override void Write(Utf8JsonWriter writer, RunSize value, JsonSerializerOptions options) => writer.WriteStringValue(value switch
    {
        RunSize.Small => "small", RunSize.Medium => "medium", RunSize.Large => "large", _ => throw new JsonException("Invalid run size.")
    });
}

internal sealed class StrictStringConverter : JsonConverter<string>
{
    private static string Check(string value)
    {
        _ = new System.Text.UTF8Encoding(false, true).GetByteCount(value);
        return value;
    }
    public override string Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => Check(reader.GetString()!);
    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(Check(value));
    public override string ReadAsPropertyName(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => Check(reader.GetString()!);
    public override void WriteAsPropertyName(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WritePropertyName(Check(value));
}

// Arbitrary JSON follows serde_json::Value, which keeps the last of repeated keys; typed fields reject them.
internal sealed class ArbitraryJsonConverter : JsonConverter<JsonElement>
{
    public override JsonElement Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => JsonElement.ParseValue(ref reader);
    public override void Write(Utf8JsonWriter writer, JsonElement value, JsonSerializerOptions options) => value.WriteTo(writer);
}

// No contract declares nullable items, and native Vec<T> rejects null for non-Option T.
internal sealed class NonNullItemsConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type type) => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ImmutableArray<>) &&
        !type.GetGenericArguments()[0].IsValueType;
    public override JsonConverter CreateConverter(Type type, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(NonNullItemsConverter<>).MakeGenericType(type.GetGenericArguments()))!;

    private sealed class NonNullItemsConverter<T> : JsonConverter<ImmutableArray<T>> where T : class
    {
        public override ImmutableArray<T> Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            var items = JsonSerializer.Deserialize<T[]>(ref reader, options) ?? throw new JsonException("A native array cannot be null.");
            return Array.IndexOf(items, null) < 0 ? [.. items] : throw new JsonException("A native array item cannot be null.");
        }
        public override void Write(Utf8JsonWriter writer, ImmutableArray<T> value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize<IEnumerable<T>>(writer, value, options);
    }
}

// Native maps (serde HashMap and BTreeMap) keep the last of repeated keys.
internal sealed class LastKeyWinsConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type type) => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ImmutableDictionary<,>);
    public override JsonConverter CreateConverter(Type type, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(LastKeyWinsConverter<,>).MakeGenericType(type.GetGenericArguments()))!;

    private sealed class LastKeyWinsConverter<TKey, TValue> : JsonConverter<ImmutableDictionary<TKey, TValue>> where TKey : notnull
    {
        public override ImmutableDictionary<TKey, TValue> Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Expected a native map.");
            var keys = (JsonConverter<TKey>)options.GetConverter(typeof(TKey));
            var map = ImmutableDictionary.CreateBuilder<TKey, TValue>();
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var key = keys.ReadAsPropertyName(ref reader, typeof(TKey), options);
                reader.Read();
                map[key] = JsonSerializer.Deserialize<TValue>(ref reader, options)!;
            }
            return map.ToImmutable();
        }
        public override void Write(Utf8JsonWriter writer, ImmutableDictionary<TKey, TValue> value, JsonSerializerOptions options)
        {
            var keys = (JsonConverter<TKey>)options.GetConverter(typeof(TKey));
            writer.WriteStartObject();
            foreach (var (key, item) in value)
            {
                keys.WriteAsPropertyName(writer, key, options);
                JsonSerializer.Serialize(writer, item, options);
            }
            writer.WriteEndObject();
        }
    }
}
