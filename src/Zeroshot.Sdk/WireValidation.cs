using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Zeroshot.Native.Contracts;

namespace Zeroshot.Native;

internal static class WireValidation
{
    private static readonly JsonObject Definitions = LoadDefinitions();
    // Lazy: compiling one schema clones every definition, so concurrent first use must not repeat it.
    private static readonly ConcurrentDictionary<string, Lazy<JsonSchema>> Schemas = new();

    /// <summary>Validates native wire data; returns the typed instance when validation already decoded it.</summary>
    internal static object? Validate(JsonElement value, Type type)
    {
        CheckUnicode(value);
        if (type.GetCustomAttribute<WireContractAttribute>() is not null)
        {
            CheckSchema(value, type);
            return null;
        }
        if (typeof(NativeString).IsAssignableFrom(type) || typeof(INativeScalar).IsAssignableFrom(type)) return null;
        if (!typeof(NativeContract).IsAssignableFrom(type)) throw new ArgumentException("Unsupported native contract type.");
        // Required fields, nullability, exact field names, per-type extension strictness and nested pinned schemas.
        var result = (NativeContract?)JsonSerializer.Deserialize(value, type, NativeJson.Options) ?? throw new JsonException();
        result.CheckWire(value);
        return result;
    }

    /// <summary>The pinned schema and native rules of a schema-named contract and everything nested in it.</summary>
    internal static void CheckSchema(JsonElement value, Type type)
    {
        var name = type.GetCustomAttribute<WireContractAttribute>()!.Name;
        var schema = Schemas.GetOrAdd(name, key => new Lazy<JsonSchema>(() => JsonSchema.FromText(new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["$ref"] = "#/$defs/" + key,
            ["$defs"] = Definitions.DeepClone()
        }.ToJsonString()))).Value;
        if (!schema.Evaluate(value).IsValid) throw new JsonException("Native wire shape is invalid.");
        CheckNative(value, Definitions[name]!, name);
    }


    private static JsonObject LoadDefinitions()
    {
        var definitions = JsonNode.Parse(NativeSchemas.Read("contracts.schema.json"))!["$defs"]!.AsObject();
        var oecp = JsonNode.Parse(NativeSchemas.Read("oecp.schema.json"))!["$defs"]!.AsObject();
        foreach (var definition in oecp) definitions[definition.Key] = definition.Value!.DeepClone();
        FixPatterns(definitions);
        return definitions;
    }

    // JsonSchema.Net evaluates patterns with .NET Regex, where $ also matches before a final LF.
    // JSON Schema patterns are ECMA-262, where it matches only at the end (issue #105).
    private static void FixPatterns(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            if (obj["pattern"] is JsonValue pattern && pattern.TryGetValue<string>(out var text) && text.EndsWith('$'))
                obj["pattern"] = text + "(?![\\s\\S])";
            foreach (var property in obj.ToArray()) if (property.Value is { } child) FixPatterns(child);
        }
        else if (node is JsonArray array) foreach (var child in array) if (child is not null) FixPatterns(child);
    }

    private static void CheckUnicode(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && (!value.TryGetDouble(out var number) || !double.IsFinite(number))) throw new JsonException("Non-finite native JSON number.");
        if (value.ValueKind == JsonValueKind.String) _ = new UTF8Encoding(false, true).GetByteCount(value.GetString()!);
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) CheckUnicode(item);
        else if (value.ValueKind == JsonValueKind.Object)
            foreach (var item in value.EnumerateObject()) { _ = new UTF8Encoding(false, true).GetByteCount(item.Name); CheckUnicode(item.Value); }
    }

    // Native rules its published schemas do not express, by schema definition.
    private static readonly Dictionary<string, Action<JsonElement>> NativeRules = new(StringComparer.Ordinal)
    {
        ["NodeInstructions"] = value => _ = new NodeInstructions(value.GetString()!),
        ["DeclaredConnections"] = CheckConnections,
        ["RuntimePlan"] = value =>
        {
            foreach (var node in value.GetProperty("nodes").EnumerateObject()) _ = new NodeName(node.Name);
        },
        ["RuntimeEnvironment"] = value =>
        {
            if (!value.TryGetProperty("variables", out var variables)) return;
            foreach (var variable in variables.EnumerateObject()) _ = new EnvironmentVariableName(variable.Name);
        },
    };

    private static void CheckNative(JsonElement value, JsonNode schema, string? name = null)
    {
        if (schema is JsonValue) return; // Arbitrary caller-authored JSON stays open.
        if (schema["$ref"] is { } reference)
        {
            name = reference.GetValue<string>().Split('/')[^1];
            CheckNative(value, Definitions[name]!, name); return;
        }
        if (value.ValueKind == JsonValueKind.Null) return;
        if (name is not null && NativeRules.TryGetValue(name, out var rule)) rule(value);
        if (schema["allOf"] is JsonArray all) foreach (var child in all) if (child is not null && child["if"] is null) CheckNative(value, child);
        if ((schema["oneOf"] ?? schema["anyOf"]) is JsonArray variants)
        {
            foreach (var variant in variants)
            {
                if (variant is null) continue;
                if (variant["$ref"] is not null) { CheckNative(value, variant); break; }
                if (variant["properties"] is JsonObject properties && properties.Any(p => p.Value is JsonObject prop && prop["const"] is { } tag && value.TryGetProperty(p.Key, out var actual) && actual.ValueKind == JsonValueKind.String && actual.GetString() == tag.GetValue<string>()))
                { CheckNative(value, variant); break; }
            }
        }
        if (value.ValueKind == JsonValueKind.Object)
        {
            var properties = schema["properties"] as JsonObject;
            if (properties is not null && value.EnumerateObject().Select(p => p.Name).Distinct().Count() != value.EnumerateObject().Count())
                throw new JsonException("Duplicate native object field.");
            foreach (var property in value.EnumerateObject())
            {
                var child = properties?[property.Name];
                if (child is null && schema["patternProperties"] is JsonObject patterns)
                    child = patterns.FirstOrDefault(p => System.Text.RegularExpressions.Regex.IsMatch(property.Name, p.Key)).Value;
                child ??= schema["additionalProperties"];
                if (child is not null) CheckNative(property.Value, child);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array && schema["items"] is { } items)
            foreach (var item in value.EnumerateArray()) CheckNative(item, items);
    }

    private static void CheckConnections(JsonElement connections)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var connection in connections.EnumerateObject())
        {
            _ = new ConnectionKey(connection.Name);
            if (connection.Value.GetArrayLength() == 0) throw new JsonException("Empty declared connection.");
            foreach (var field in connection.Value.EnumerateArray())
            {
                _ = new EnvironmentVariableName(field.GetString()!);
                if (!names.Add(field.GetString()!)) throw new JsonException("Duplicate declared environment name.");
            }
        }
        if (names.Count > 64) throw new JsonException("Too many declared environment names.");
    }
}
