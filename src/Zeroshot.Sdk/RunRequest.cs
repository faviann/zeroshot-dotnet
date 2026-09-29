using System.Text;
using System.Text.Json;
using Zeroshot.Native;
using Zeroshot.Native.Contracts;

namespace Zeroshot;

/// <summary>
/// Caller-prepared work for one run. Identity values left null are generated once by <see cref="Prepare"/>;
/// credentials are never part of a request.
/// </summary>
public sealed record RunRequest
{
    public required RunTitle Title { get; init; }
    public required GraphSpec Graph { get; init; }
    public required RuntimePlan Runtime { get; init; }
    public required JsonElement InitialInput { get; init; }
    public required ResolvedSource Source { get; init; }
    /// <summary>Omitted, explicit null and a present environment stay distinct.</summary>
    public Optional<RuntimeEnvironment?> Environment { get; init; }
    /// <summary>The proposed run ID; a canonical UUIDv7 is generated when null.</summary>
    public RunId? RunId { get; init; }
    /// <summary>The native submission key; <c>dotnet-</c> plus 32 lowercase hex digits is generated when null.</summary>
    public IdempotencyKey? SubmissionKey { get; init; }

    /// <summary>
    /// Parses the request file shape: the native submission fields (<c>submissionKey</c> optional) plus an
    /// optional <c>runId</c>. Unknown fields and invalid native values are rejected.
    /// </summary>
    public static RunRequest ParseUtf8(ReadOnlySpan<byte> utf8)
    {
        const string placeholderKey = "dotnet-placeholder";
        try
        {
            _ = new UTF8Encoding(false, true).GetCharCount(utf8);
            using var document = JsonDocument.Parse(utf8.ToArray(), new JsonDocumentOptions { MaxDepth = 128 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new JsonException();
            var names = new HashSet<string>(StringComparer.Ordinal);
            RunId? runId = null;
            // Validate the submission fields through the native contract, supplying a key only when omitted.
            using var output = new MemoryStream();
            using (var writer = new Utf8JsonWriter(output))
            {
                writer.WriteStartObject();
                foreach (var property in root.EnumerateObject())
                {
                    if (!names.Add(property.Name)) throw new JsonException();
                    if (property.Name == "runId")
                    {
                        if (property.Value.ValueKind != JsonValueKind.String) throw new JsonException();
                        runId = new RunId(property.Value.GetString()!);
                        TargetRunRequest.ValidateRunId(runId);
                        continue;
                    }
                    property.WriteTo(writer);
                }
                if (!names.Contains("submissionKey")) writer.WriteString("submissionKey", placeholderKey);
                writer.WriteEndObject();
            }
            var submission = NativeJson.DeserializeUtf8<RunSubmission>(output.ToArray());
            return new RunRequest
            {
                Title = submission.Title, Graph = submission.Graph, Runtime = submission.Runtime,
                InitialInput = submission.InitialInput, Source = submission.Source, Environment = submission.Environment,
                RunId = runId, SubmissionKey = names.Contains("submissionKey") ? submission.SubmissionKey : null
            };
        }
        catch (Exception error) when (error is ArgumentException or DecoderFallbackException or JsonException)
        { throw new JsonException("Invalid run request. Authored values are omitted from diagnostics."); }
    }

    /// <summary>Fixes identity and content locally, without network I/O. Each call generates any omitted identity afresh.</summary>
    public PreparedSubmission Prepare() => PreparedSubmission.Create(
        RunId ?? new RunId(Guid.CreateVersion7().ToString("D")),
        new RunSubmission
        {
            Title = Title, Graph = Graph, Runtime = Runtime, InitialInput = InitialInput, Source = Source,
            Environment = Environment,
            SubmissionKey = SubmissionKey ?? new IdempotencyKey("dotnet-" + Guid.NewGuid().ToString("N"))
        });

    // Authored content may be sensitive; record formatting would expose it.
    public override string ToString() => nameof(RunRequest);
}
