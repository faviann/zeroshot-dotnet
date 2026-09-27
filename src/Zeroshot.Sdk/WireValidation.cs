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
        if (typeof(DashboardContract).IsAssignableFrom(type))
        {
            var dashboard = JsonSerializer.Deserialize(value, type, NativeJson.Options) as DashboardContract ?? throw new JsonException();
            dashboard.Validate(value);
            return dashboard;
        }
        if (typeof(DiscoveryContract).IsAssignableFrom(type) || typeof(TargetHttpContract).IsAssignableFrom(type))
        {
            // Nested pinned-schema definitions validate first: typed decoding cannot classify every malformed runtime.
            if (type == typeof(RunProfile) || type == typeof(RunProfileSetRequest)) CheckProfile(value);
            if (type == typeof(RunProfileMutationResult) && value.ValueKind == JsonValueKind.Object &&
                value.TryGetProperty("profile", out var profile)) CheckProfile(profile);
            // Required fields, nullability, exact field names and per-type extension strictness.
            var result = JsonSerializer.Deserialize(value, type, NativeJson.Options) ?? throw new JsonException();
            if (result is TargetHttpProblem problem) problem.Validate();
            if (result is TargetRunCredentials credentials) credentials.Validate();
            if (result is ConnectionSetRequest connection) StaticConnectionValues.Validate(connection.Values);
            if (result is TargetPrivateBootstrapRequest bootstrap) bootstrap.Validate();
            if (result is RunProfileRunRequest run)
            {
                StaticConnectionValues.ValidateRun(run.Connections);
                Validate(value.GetProperty("source"), typeof(ResolvedSource));
                if (run.Environment is not null) Validate(value.GetProperty("environment"), typeof(RuntimeEnvironment));
            }
            if (result is TargetRunRequest request)
            {
                TargetRunRequest.ValidateRunId(request.RunId);
                Validate(value.GetProperty("submission"), typeof(RunSubmission));
            }
            CheckDiscovery(value, type);
            return result;
        }
        if (typeof(HistoryContract).IsAssignableFrom(type))
        {
            // Typed decoding owns field names, presence and nullability; nested pinned-schema values still validate.
            var history = JsonSerializer.Deserialize(value, type, NativeJson.Options) ?? throw new JsonException();
            CheckHistory(value, history);
            return history;
        }
        var name = type.GetCustomAttribute<WireContractAttribute>()?.Name;
        if (name is null)
        {
            if (typeof(NativeString).IsAssignableFrom(type)) return null;
            throw new ArgumentException("Unsupported native contract type.");
        }
        var schema = Schemas.GetOrAdd(name, key => new Lazy<JsonSchema>(() => JsonSchema.FromText(new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["$ref"] = "#/$defs/" + key,
            ["$defs"] = Definitions.DeepClone()
        }.ToJsonString()))).Value;
        if (!schema.Evaluate(value).IsValid) throw new JsonException("Native wire shape is invalid.");
        CheckNative(value, Definitions[name]!, name);
        return null;
    }

    // Missing or misplaced members are left to typed decoding, which reports them.
    private static void CheckProfile(JsonElement profile)
    {
        if (profile.ValueKind != JsonValueKind.Object) return;
        if (profile.TryGetProperty("graph", out var graph)) Validate(graph, typeof(GraphSpec));
        if (profile.TryGetProperty("runtime", out var runtime)) Validate(runtime, typeof(RuntimePlan));
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

    private static void CheckHistory(JsonElement value, object? instance)
    {
        switch (instance)
        {
            case null or JsonElement or NativeString or string: return; // Arbitrary JSON or already validated text.
            case HistoryContract contract:
                contract.CheckShape();
                // Externally tagged durable state wraps its fields in one variant-named member.
                if (contract is DurableExecutionState && value.ValueKind == JsonValueKind.Object)
                    value = value.EnumerateObject().Single().Value;
                if (value.ValueKind != JsonValueKind.Object) return;
                var members = HistoryMembers.GetOrAdd(contract.GetType(), type => type
                    .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(p => p.GetCustomAttribute<System.Text.Json.Serialization.JsonPropertyNameAttribute>() is not null)
                    .ToDictionary(p => p.GetCustomAttribute<System.Text.Json.Serialization.JsonPropertyNameAttribute>()!.Name, StringComparer.Ordinal));
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                {
                    if (!members.TryGetValue(property.Name, out var member)) continue; // Open native records.
                    if (!names.Add(property.Name)) throw new JsonException("Duplicate native object field.");
                    CheckHistory(property.Value, member.GetValue(contract));
                }
                return;
            case System.Collections.IEnumerable items:
                using (var elements = value.EnumerateArray().GetEnumerator())
                    foreach (var item in items) { elements.MoveNext(); CheckHistory(elements.Current, item); }
                return;
        }
        var type = instance.GetType();
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Optional<>)) return; // Only Optional<JsonElement>.
        if (type.GetCustomAttribute<WireContractAttribute>() is not null) Validate(value, type);
    }

    private static readonly ConcurrentDictionary<Type, Dictionary<string, PropertyInfo>> HistoryMembers = new();

    private static JsonObject LoadDefinitions()
    {
        var definitions = JsonNode.Parse(NativeSchemas.Read("contracts.schema.json"))!["$defs"]!.AsObject();
        var oecp = JsonNode.Parse(NativeSchemas.Read("oecp.schema.json"))!["$defs"]!.AsObject();
        foreach (var definition in oecp) definitions[definition.Key] = definition.Value!.DeepClone();
        // Version negotiation accepts arbitrary request versions so the peer can report its
        // unsupported-protocol error. A successful reply must still name the supported version.
        definitions["InitializeParams"]!["properties"]!["protocolVersion"]!.AsObject().Remove("const");
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
