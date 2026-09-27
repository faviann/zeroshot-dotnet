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
    private static readonly ConcurrentDictionary<string, JsonSchema> Schemas = new();

    internal static void Validate(JsonElement value, Type type)
    {
        CheckUnicode(value);
        if (typeof(DiscoveryContract).IsAssignableFrom(type) || typeof(TargetHttpContract).IsAssignableFrom(type))
        {
            // Required fields, nullability, exact field names and per-type extension strictness.
            var result = JsonSerializer.Deserialize(value, type, NativeJson.Options) ?? throw new JsonException();
            if (result is TargetHttpProblem problem) problem.Validate();
            CheckDiscovery(value, type);
            return;
        }
        var name = type.GetCustomAttribute<WireContractAttribute>()?.Name;
        if (name is null)
        {
            if (typeof(NativeString).IsAssignableFrom(type)) return;
            throw new ArgumentException("Unsupported native contract type.");
        }
        var schema = Schemas.GetOrAdd(name, key => JsonSchema.FromText(new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["$ref"] = "#/$defs/" + key,
            ["$defs"] = Definitions.DeepClone()
        }.ToJsonString()));
        if (!schema.Evaluate(value).IsValid) throw new JsonException("Native wire shape is invalid.");
        CheckNative(value, Definitions[name]!, name);
    }

    private static void CheckDiscovery(JsonElement value, Type type)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                var member = type.GetProperties().FirstOrDefault(p =>
                    p.GetCustomAttribute<System.Text.Json.Serialization.JsonPropertyNameAttribute>()?.Name == property.Name);
                // In the extension container, unknown names and all their content are ignored by native.
                if (member is null) continue;
                if (!names.Add(property.Name)) throw new JsonException();
                CheckDiscovery(property.Value, member.PropertyType);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Null) throw new JsonException();
                CheckDiscovery(item, typeof(string));
            }
    }

    private static JsonObject LoadDefinitions()
    {
        var definitions = JsonNode.Parse(NativeSchemas.Read("contracts.schema.json"))!["$defs"]!.AsObject();
        // The native decoder accepts these legacy spellings although its generated enum schema does not.
        definitions["RunSize"]!["enum"] = new JsonArray("small", "medium", "large", "tiny", "standard");
        // Rust string::trim and byte counts, rather than a regex character bound, own instructions.
        definitions["NodeInstructions"]!.AsObject().Remove("pattern");
        // These collection restrictions belong to WorkerDescriptor::validate, not standalone serde DTOs.
        definitions["WorkerContract"]!["properties"]!["errors"] = new JsonObject
        {
            ["type"] = "array", ["items"] = new JsonObject { ["$ref"] = "#/$defs/WorkerErrorCode" }
        };
        foreach (var property in new[] { "allowedTypeIds", "allowedMediaTypes" })
        {
            var collection = definitions["ArtifactResultProfile"]!["properties"]![property]!.AsObject();
            collection.Remove("minItems"); collection.Remove("uniqueItems");
        }
        foreach (var name in new[] { "Generation", "PositiveInteger", "ByteLength" })
            definitions[name] = new JsonObject { ["type"] = "integer", ["minimum"] = name == "PositiveInteger" ? 1 : 0, ["maximum"] = Generation.Maximum };
        definitions["RequestId"] = JsonNode.Parse("""{"anyOf":[{"type":"string"},{"type":"integer","minimum":-9223372036854775808,"maximum":9223372036854775807}]}""");
        FixPatterns(definitions);
        return definitions;
    }

    private static void FixPatterns(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            if (obj["pattern"] is JsonValue pattern && pattern.TryGetValue<string>(out var text) && text.EndsWith('$'))
                obj["pattern"] = text + "(?![\\s\\S])"; // Require the actual end, including after a trailing LF.
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

    private static void CheckNative(JsonElement value, JsonNode schema, string? name = null)
    {
        if (schema is JsonValue) return; // Arbitrary caller-authored JSON stays open.
        if (schema["$ref"] is { } reference)
        {
            name = reference.GetValue<string>().Split('/')[^1];
            CheckNative(value, Definitions[name]!, name); return;
        }
        if (value.ValueKind == JsonValueKind.Null) return;
        if (name == "NodeInstructions") _ = ValueRules.Check(nameof(NodeInstructions), value.GetString()!);
        if (name == "DeclaredConnections") CheckConnections(value);
        if (name == "RuntimePlan")
            foreach (var node in value.GetProperty("nodes").EnumerateObject()) _ = new NodeName(node.Name);
        if (name == "RuntimeEnvironment" && value.TryGetProperty("variables", out var variables))
            foreach (var variable in variables.EnumerateObject()) _ = new EnvironmentVariableName(variable.Name);
        if (name == "WorkerDescriptor") CheckDescriptor(value);
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

    private static void CheckDescriptor(JsonElement descriptor)
    {
        static bool Unique(JsonElement values, bool nonempty) => (!nonempty || values.GetArrayLength() > 0) &&
            values.EnumerateArray().Select(v => v.GetString()).Distinct(StringComparer.Ordinal).Count() == values.GetArrayLength();
        var errors = descriptor.GetProperty("contract").GetProperty("errors");
        var artifacts = descriptor.GetProperty("artifactProfile");
        if (!Unique(descriptor.GetProperty("graphProfiles"), true) || !Unique(errors, true) || errors.GetArrayLength() != 4 ||
            !Unique(artifacts.GetProperty("allowedTypeIds"), true) || !Unique(artifacts.GetProperty("allowedMediaTypes"), true) ||
            !Unique(descriptor.GetProperty("credentialRequirements"), false)) throw new JsonException("Invalid worker descriptor collections.");
    }
}
