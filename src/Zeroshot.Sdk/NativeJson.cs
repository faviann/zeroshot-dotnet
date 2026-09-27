using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Zeroshot.Native.Contracts;

namespace Zeroshot.Native;

/// <summary>Local native wire-shape validation and serialization. Performs no I/O or graph admission.</summary>
public static class NativeJson
{
    internal static readonly JsonSerializerOptions Options = CreateOptions();
    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            AllowOutOfOrderMetadataProperties = true,
            MaxDepth = 128,
            RespectNullableAnnotations = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        options.Converters.Add(new StrictStringConverter());
        options.Converters.Add(new RunSizeConverter());
        options.Converters.Add(new NativeUnsignedConverter());
        options.Converters.Add(new NativeUnsigned32Converter());
        options.Converters.Add(new TargetAuthenticationConverter());
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        return options;
    }

    public static byte[] SerializeUtf8<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        try
        {
            var type = WireType(value.GetType());
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, type, Options);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 128 });
            WireValidation.Validate(document.RootElement, type);
            return bytes;
        }
        catch (Exception e) when (IsContractError(e)) { throw Invalid(); }
    }

    public static T DeserializeUtf8<T>(ReadOnlySpan<byte> utf8)
    {
        try
        {
            // JSON parsers need not reject malformed UTF-8 in every arbitrary string value.
            _ = new UTF8Encoding(false, true).GetCharCount(utf8);
            using var document = JsonDocument.Parse(utf8.ToArray(), new JsonDocumentOptions { MaxDepth = 128 });
            // Typed validation paths already decoded the value; schema-validated contracts decode here.
            var decoded = WireValidation.Validate(document.RootElement, WireType(typeof(T)))
                ?? JsonSerializer.Deserialize(utf8, WireType(typeof(T)), Options);
            return decoded is T result ? result : throw Invalid();
        }
        catch (Exception e) when (IsContractError(e)) { throw Invalid(); }
    }

    internal static Type WireType(Type type)
    {
        var name = type.GetCustomAttribute<WireContractAttribute>()?.Name;
        while (name is not null && type.Name != name && type.BaseType is { } parent && parent != typeof(NativeContract)) type = parent;
        // Tagged alternatives without a schema name serialize through their discriminating base.
        while (type.BaseType?.GetCustomAttribute<JsonPolymorphicAttribute>(inherit: false) is not null) type = type.BaseType;
        return type;
    }

    private static bool IsContractError(Exception e) => e is JsonException or ArgumentException or InvalidOperationException or OverflowException;
    private static JsonException Invalid() => new("Invalid native contract. Authored values are omitted from diagnostics.");
}

/// <summary>Pinned, local schema data. No compiler or remote compilation operation is supplied.</summary>
public static class NativeSchemas
{
    public const string NativeVersion = "10.9.0";
    public const string SourceRevision = "75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa";
    public static byte[] ExportCompiledIrUtf8() => Read("compiled-ir.schema.json");
    public static byte[] ExportContractsUtf8() => Read("contracts.schema.json");
    internal static byte[] Read(string name)
    {
        using var stream = typeof(NativeSchemas).Assembly.GetManifestResourceStream("Zeroshot.Schemas." + name)
            ?? throw new InvalidOperationException("Embedded native schema is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
