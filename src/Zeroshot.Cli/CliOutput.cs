using System.Text;
using System.Text.Json;
using Zeroshot.Native.Contracts;

namespace Zeroshot.Cli;

/// <summary>
/// Readable text by default; with <c>--json</c>, one <c>zeroshot-dotnet/cli/v1</c> record per line.
/// Requested results go to stdout and errors to stderr.
/// </summary>
internal sealed class CliOutput(TextWriter stdout, TextWriter stderr, bool json)
{
    public const string Schema = "zeroshot-dotnet/cli/v1";

    /// <summary>Confirms the written retained request by path and proposed ID, never by its content.</summary>
    public void Prepared(RunId proposedRunId, string path)
    {
        if (json) Record(stdout, "prepared", w => { w.WriteString("proposedRunId", proposedRunId.Value); w.WriteString("path", path); });
        else stdout.WriteLine($"Prepared run {proposedRunId.Value} in {path}");
    }

    public void Error(string? operation, CliFailure failure)
    {
        if (json)
        {
            Record(stderr, "error", w =>
            {
                w.WriteString("category", failure.Category);
                if (operation is not null) w.WriteString("operation", operation);
                w.WriteString("message", failure.Message);
            });
            return;
        }
        stderr.WriteLine($"zeroshot-dotnet{(operation is null ? "" : " " + operation)}: {failure.Message}");
        if (failure.Category == "invocation") stderr.WriteLine("Run 'zeroshot-dotnet --help' for usage.");
    }

    private static void Record(TextWriter writer, string kind, Action<Utf8JsonWriter> fields)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("schema", Schema);
            w.WriteString("kind", kind);
            fields(w);
            w.WriteEndObject();
        }
        writer.WriteLine(Encoding.UTF8.GetString(buffer.ToArray()));
    }
}
