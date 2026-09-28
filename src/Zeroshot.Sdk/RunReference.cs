using System.Text.Json;
using Zeroshot.Native;
using Zeroshot.Native.Contracts;

namespace Zeroshot;

/// <summary>
/// The caller's declaration of the native build serving a target. Native publishes no build identity,
/// so the SDK compares this declaration only with its supported release; it is never remote attestation.
/// </summary>
public sealed record NativeBinding
{
    internal static readonly NativeBinding Supported = new(NativeSchemas.NativeVersion, NativeSchemas.SourceRevision);

    public string Release { get; }
    public string SourceRevision { get; }
    /// <summary>Always <c>caller-supplied</c>: this binding records what the caller asserted.</summary>
    public string Provenance => "caller-supplied";

    private NativeBinding(string release, string sourceRevision) { Release = release; SourceRevision = sourceRevision; }

    /// <summary>Checks form only. An unsupported release is refused when a run operation is dispatched.</summary>
    public static NativeBinding CallerSupplied(string release, string sourceRevision)
        => new(Token(release, nameof(release)), Token(sourceRevision, nameof(sourceRevision)));

    private static string Token(string value, string name)
    {
        ArgumentNullException.ThrowIfNull(value, name);
        if (value.Length is 0 or > 128 || value.Any(c => c is < '!' or > '~'))
            throw new ArgumentException("A binding value must be 1..128 printable ASCII characters without spaces.", name);
        return value;
    }

    public override string ToString() => $"caller-supplied native {Release} ({SourceRevision})";
}

public enum NativeBindingProblem { Missing, Mismatched }

/// <summary>The SDK refused a run operation before dispatch because of its configured native binding.</summary>
public sealed class NativeBindingException : Exception
{
    public NativeBindingProblem Reason { get; }
    /// <summary>The refused declaration, or null when none was configured.</summary>
    public NativeBinding? Declared { get; }
    /// <summary>The binding the declaration had to equal.</summary>
    public NativeBinding Required { get; }

    internal NativeBindingException(NativeBindingProblem reason, NativeBinding? declared, NativeBinding required, string message)
        : base(message)
    { Reason = reason; Declared = declared; Required = required; }
}

/// <summary>
/// A portable record of a known run: exact target, run ID and caller-supplied native binding.
/// It holds no credentials, request or history, and reconnecting never submits work.
/// </summary>
public sealed record RunReference
{
    private const string Schema = "zeroshot-dotnet/run-reference/v1";

    public Uri Target { get; }
    public RunId RunId { get; }
    public NativeBinding NativeBinding { get; }

    public RunReference(Uri target, RunId runId, NativeBinding nativeBinding)
    {
        ArgumentNullException.ThrowIfNull(runId);
        ArgumentNullException.ThrowIfNull(nativeBinding);
        Target = NativeClient.ValidateOrigin(target);
        RunId = runId;
        NativeBinding = nativeBinding;
    }

    /// <summary>Compact JSON with a fixed property order.</summary>
    public string ToJson()
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", Schema);
            writer.WriteString("target", Target.AbsoluteUri);
            writer.WriteString("runId", RunId.Value);
            writer.WriteStartObject("nativeBinding");
            writer.WriteString("provenance", NativeBinding.Provenance);
            writer.WriteString("release", NativeBinding.Release);
            writer.WriteString("sourceRevision", NativeBinding.SourceRevision);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(output.ToArray());
    }

    /// <summary>Strict import: exactly the exported fields, the v1 schema and caller-supplied provenance.</summary>
    public static RunReference Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = Fields(document.RootElement, "schema", "target", "runId", "nativeBinding");
            var binding = Fields(root["nativeBinding"], "provenance", "release", "sourceRevision");
            if (Text(root["schema"]) != Schema || Text(binding["provenance"]) != "caller-supplied") throw new JsonException();
            return new RunReference(new Uri(Text(root["target"]), UriKind.Absolute), new RunId(Text(root["runId"])),
                NativeBinding.CallerSupplied(Text(binding["release"]), Text(binding["sourceRevision"])));
        }
        catch (Exception error) when (error is ArgumentException or UriFormatException or InvalidOperationException)
        { throw new JsonException("Invalid run reference."); }
    }

    private static Dictionary<string, JsonElement> Fields(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("Invalid run reference.");
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!names.Contains(property.Name) || !fields.TryAdd(property.Name, property.Value)) throw new JsonException("Invalid run reference.");
        if (fields.Count != names.Length) throw new JsonException("Invalid run reference.");
        return fields;
    }

    private static string Text(JsonElement value)
        => value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new JsonException("Invalid run reference.");
}
