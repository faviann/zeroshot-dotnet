using System.Text.Json;
using Zeroshot.Native;
using Zeroshot.Native.Contracts;
using Zeroshot.Native.Execution;

namespace Zeroshot;

/// <summary>SDK watch/log observation policy. Lower-client subscriptions never recover on their own.</summary>
public sealed record ObservationOptions
{
    /// <summary>
    /// Reopen after an established stream is interrupted by disconnection, unexpected EOF or remote
    /// <c>SLOW_CONSUMER</c>. False surfaces that interruption as <see cref="RunObservationFailureKind.Interrupted"/>.
    /// </summary>
    public bool Recover { get; init; } = true;
    /// <summary>Cancellable wait before each reopen. Zero reopens immediately.</summary>
    public TimeSpan ReopenDelay { get; init; } = TimeSpan.FromMilliseconds(250);
    /// <summary>One budget for each (re)establishment: discovery, session, connect, initialize and subscription.</summary>
    public TimeSpan SetupTimeout { get; init; } = TimeSpan.FromSeconds(30);

    internal ObservationOptions Validated()
    {
        OperationLimits.ValidateTimeout(SetupTimeout, nameof(SetupTimeout));
        if (ReopenDelay < TimeSpan.Zero || ReopenDelay.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(ReopenDelay), "A non-negative, finite delay is required.");
        return this;
    }
}

public enum HistoryStream { Watch, Logs }

/// <summary>
/// An opaque run history cursor with the exact scope it belongs to: target, run, stream kind and, for logs,
/// the execution filter (null for run-wide logs). A checkpoint resumes only the observation it came from.
/// Retaining one after processing is the caller's job; resuming from it may repeat records processed after it.
/// </summary>
public sealed record HistoryCheckpoint
{
    private const string Schema = "zeroshot-dotnet/history-checkpoint/v1";

    public Uri Target { get; }
    public RunId RunId { get; }
    public HistoryStream Stream { get; }
    public ExecutionRef? Execution { get; }
    public Cursor Cursor { get; }

    public HistoryCheckpoint(Uri target, RunId runId, HistoryStream stream, ExecutionRef? execution, Cursor cursor)
    {
        ArgumentNullException.ThrowIfNull(runId);
        ArgumentNullException.ThrowIfNull(cursor);
        if (!Enum.IsDefined(stream)) throw new ArgumentOutOfRangeException(nameof(stream));
        if (stream == HistoryStream.Watch && execution is not null)
            throw new ArgumentException("Watch history has no execution filter.", nameof(execution));
        Target = NativeClient.ValidateOrigin(target);
        RunId = runId; Stream = stream; Execution = execution; Cursor = cursor;
    }

    /// <summary>Compact JSON with a fixed property order. The cursor is opaque native data.</summary>
    public string ToJson()
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", Schema);
            writer.WriteString("target", Target.AbsoluteUri);
            writer.WriteString("runId", RunId.Value);
            writer.WriteString("stream", Stream == HistoryStream.Watch ? "watch" : "logs");
            if (Execution is null) writer.WriteNull("execution"); else writer.WriteString("execution", Execution.Value);
            writer.WriteString("cursor", Cursor.Value);
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(output.ToArray());
    }

    /// <summary>Strict import of exactly the exported fields.</summary>
    public static HistoryCheckpoint Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
            var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            string[] names = ["schema", "target", "runId", "stream", "execution", "cursor"];
            foreach (var property in document.RootElement.EnumerateObject())
                if (!names.Contains(property.Name) || !fields.TryAdd(property.Name, property.Value)) throw new JsonException();
            if (fields.Count != names.Length || Text(fields["schema"]) != Schema) throw new JsonException();
            var stream = Text(fields["stream"]) switch
            {
                "watch" => HistoryStream.Watch,
                "logs" => HistoryStream.Logs,
                _ => throw new JsonException()
            };
            var execution = fields["execution"].ValueKind == JsonValueKind.Null ? null : new ExecutionRef(Text(fields["execution"]));
            return new HistoryCheckpoint(new Uri(Text(fields["target"]), UriKind.Absolute), new RunId(Text(fields["runId"])),
                stream, execution, new Cursor(Text(fields["cursor"])));
        }
        catch (Exception error) when (error is JsonException or ArgumentException or UriFormatException or InvalidOperationException)
        { throw new JsonException("Invalid history checkpoint."); }
    }

    private static string Text(JsonElement value)
        => value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new JsonException();

    /// <summary>Cursor values stay out of default formatting.</summary>
    public override string ToString() => $"History checkpoint ({Stream}) for run {RunId.Value}";
}

/// <summary>One complete native record and the scoped checkpoint of its cursor.</summary>
public sealed record HistoryRecord<TEvent>(TEvent Event, HistoryCheckpoint Checkpoint);

public enum RunObservationFailureKind
{
    /// <summary>Initial or reopened establishment failed, including the setup budget. Never recovered.</summary>
    Establishment,
    /// <summary>An eligible interruption with recovery disabled.</summary>
    Interrupted,
    /// <summary>Native <c>SOURCE_UNAVAILABLE</c>: retained history is incomplete. Retrying the cursor cannot heal it.</summary>
    SourceUnavailable,
    /// <summary>Malformed or foreign data.</summary>
    Protocol,
    /// <summary>A local queue or message size limit.</summary>
    ResourceLimit
}

/// <summary>
/// SDK watch/log observation ended without native completion. Never a claim about the run's outcome.
/// Cursors and record bodies stay out of the message.
/// </summary>
public sealed class RunObservationException : Exception
{
    public RunObservationFailureKind Kind { get; }
    public RunId RunId { get; }
    public HistoryStream Stream { get; }
    /// <summary>
    /// The last record delivered to the caller, else the starting checkpoint, else null (from the start).
    /// A new enumeration after it misses nothing; buffered and server-reported positions never advance it.
    /// </summary>
    public HistoryCheckpoint? ResumeAfter { get; }
    /// <summary>Reopens performed by this enumeration before the failure.</summary>
    public int Recoveries { get; }

    internal RunObservationException(RunObservationFailureKind kind, RunId runId, HistoryStream stream,
        HistoryCheckpoint? resumeAfter, int recoveries, Exception inner)
        : base($"Run {stream.ToString().ToLowerInvariant()} observation failed: {kind}.", inner)
    { Kind = kind; RunId = runId; Stream = stream; ResumeAfter = resumeAfter; Recoveries = recoveries; }
}
