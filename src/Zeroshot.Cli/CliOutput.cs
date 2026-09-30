using System.Globalization;
using System.Text;
using System.Text.Json;
using Zeroshot.Native;
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

    /// <summary>An acknowledged submission: the acknowledged run, which can differ from the proposed one.</summary>
    public void Submission(TargetSubmissionAttempt attempt)
    {
        var id = attempt.AcknowledgedRunId!;
        if (json)
        {
            Record(stdout, "submission", w =>
            {
                w.WriteString("runId", id.Value);
                w.WriteString("target", attempt.Origin!.AbsoluteUri);
                Attempt(w, AttemptEvidence.Of(attempt));
            });
            return;
        }
        stdout.WriteLine(attempt.RunIdsMatch == true ? $"Submitted run {id.Value}"
            : $"Submitted run {id.Value} (acknowledged instead of proposed run {attempt.ProposedRunId.Value})");
    }

    /// <summary>The complete native status, plus its terminal result when the run has finished.</summary>
    public void Status(RunStatusResult status)
    {
        var result = RunResult.FromStatus(status);
        if (json)
        {
            Record(stdout, "status", w =>
            {
                w.WriteString("runId", status.RunId.Value);
                Native(w, "status", status);
                if (result is not null) { w.WritePropertyName("result"); ResultFields(w, result); }
            });
            return;
        }
        stdout.WriteLine(result is null ? $"Run {status.RunId.Value} is {Phase(status.Status)}" : Summary(result));
        if (result is { IsSuccess: true }) stdout.WriteLine(result.Output!.Value.GetRawText());
    }

    public void Result(RunResult result)
    {
        if (json)
        {
            Record(stdout, "result", w =>
            {
                w.WriteString("runId", result.RunId.Value);
                w.WritePropertyName("result");
                ResultFields(w, result);
            });
            return;
        }
        stdout.WriteLine(Summary(result));
        if (result.IsSuccess) stdout.WriteLine(result.Output!.Value.GetRawText());
    }

    /// <summary>An acknowledged force request: the native acknowledgement, which can still report a nonterminal phase.</summary>
    public void Force(NativeAttempt<RunForceResult> attempt)
    {
        var acknowledgement = attempt.Response!;
        if (json)
        {
            Record(stdout, "force", w =>
            {
                w.WriteString("runId", acknowledgement.RunId.Value);
                Attempt(w, AttemptEvidence.Of(attempt));
                Native(w, "status", acknowledgement);
            });
            return;
        }
        stdout.WriteLine($"Force acknowledged for run {acknowledgement.RunId.Value}; it is {Phase(acknowledgement.Status)}");
    }

    /// <summary>One retained or live run status record: the complete native event and its scoped checkpoint.</summary>
    public void Watch(HistoryRecord<RunWatchEventNotification> record)
    {
        var watched = record.Event;
        if (json)
        {
            Record(stdout, "watch", w =>
            {
                w.WriteString("runId", watched.RunId.Value);
                w.WriteString("cursor", watched.Cursor.Value);
                Checkpoint(w, record.Checkpoint);
                Native(w, "data", watched);
            });
            return;
        }
        var run = $"Run {watched.RunId.Value}";
        stdout.WriteLine($"{Printable(watched.Cursor.Value)} " + watched.Status switch
        {
            FinishedRunStatus { TerminalResult: FailedTerminalResult failed } => $"{run} failed: {failed.Reason.Value}",
            FinishedRunStatus => $"{run} succeeded",
            var status => $"{run} is {Phase(status)}",
        });
    }

    /// <summary>One retained or live log record: the complete native event, its execution, timestamp and scoped checkpoint.</summary>
    public void Log(HistoryRecord<RunLogEventNotification> record)
    {
        var log = record.Event;
        if (json)
        {
            Record(stdout, "log", w =>
            {
                w.WriteString("runId", log.RunId.Value);
                w.WriteString("cursor", log.Cursor.Value);
                if (log.Execution is { } execution) w.WriteString("execution", execution.Value); else w.WriteNull("execution");
                w.WriteNumber("timestamp", log.Timestamp.Value);
                Checkpoint(w, record.Checkpoint);
                Native(w, "data", log);
            });
            return;
        }
        // Native bounds log targets, messages and execution references to text without control characters.
        var at = log.Timestamp.Value <= MaxTimestamp
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)log.Timestamp.Value).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)
            : log.Timestamp.Value.ToString(CultureInfo.InvariantCulture);
        var execution = log.Execution is { } ex ? $" [{ex.Value}]" : "";
        stdout.WriteLine($"{Printable(log.Cursor.Value)} {at} {Wire(log.Record.Level)} {log.Record.Target.Value}{execution}: {log.Record.Message.Value}");
    }

    /// <summary>One live attachment event. It has no cursor: attachment is never replayed.</summary>
    public void Attachment(RunAttachEventNotification attached)
    {
        if (json)
        {
            Record(stdout, "attachment", w =>
            {
                w.WriteString("runId", attached.RunId.Value);
                w.WriteString("execution", attached.Execution.Value);
                Native(w, "data", attached);
            });
            return;
        }
        var execution = $"Execution {attached.Execution.Value}";
        stdout.WriteLine(attached.Event switch
        {
            OutputAgentAttachEvent output => output.Text.Value,
            SettledAgentAttachEvent => $"{execution} settled",
            _ => $"{execution} is working",
        });
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
                if (failure.RunId is { } runId) w.WriteString("runId", runId.Value);
                if (failure.Attempt is { } attempt) Attempt(w, attempt);
                if (failure.Evidence is { } evidence) Evidence(w, evidence);
                if (failure.Observation is { } observation)
                {
                    w.WriteStartObject("observation");
                    if (observation.Failure is { } kind) w.WriteString("failure", kind);
                    if (observation.Recoveries is { } recoveries) w.WriteNumber("recoveries", recoveries);
                    if (observation.LastDelivered is { } delivered) w.WriteString("lastDeliveredCursor", delivered.Value);
                    w.WriteEndObject();
                }
                if (failure.Native is { } native)
                {
                    w.WriteStartObject("native");
                    w.WriteString("transport", native.Transport);
                    w.WriteString("kind", native.Kind);
                    if (native.HttpStatus is { } status) w.WriteNumber("httpStatus", status);
                    if (native.ProblemCode is { } problem) w.WriteString("problemCode", problem);
                    if (native.RpcCode is { } rpc) w.WriteNumber("rpcCode", rpc);
                    if (native.DomainCode is { } domain) w.WriteString("domainCode", domain);
                    w.WriteEndObject();
                }
            });
            return;
        }
        var facts = failure.Native is not { } n ? "" : " [" + string.Join(' ', new object?[]
            { n.Transport, n.Kind, n.HttpStatus, n.ProblemCode, n.RpcCode, n.DomainCode }.Where(fact => fact is not null)) + "]";
        var resume = failure.Observation?.LastDelivered is { } last ? $" Last delivered cursor: {Printable(last.Value)}." : "";
        stderr.WriteLine($"zeroshot-dotnet{(operation is null ? "" : " " + operation)}: {failure.Message}{resume}{facts}");
        if (failure.Category == "invocation") stderr.WriteLine("Run 'zeroshot-dotnet --help' for usage.");
    }

    private static string Summary(RunResult result)
        => result.IsSuccess ? $"Run {result.RunId.Value} succeeded" : $"Run {result.RunId.Value} failed: {result.FailureReason!.Value}";

    private static string Phase(RunStatus status) => status switch
    {
        AdmittedRunStatus => "admitted",
        RunningRunStatus => "running",
        StoppingRunStatus => "stopping",
        _ => "finished",
    };

    /// <summary>Generic output, native failure reason and metadata, and where the terminal result was observed.</summary>
    private static void ResultFields(Utf8JsonWriter w, RunResult result)
    {
        w.WriteStartObject();
        w.WriteBoolean("succeeded", result.IsSuccess);
        if (result.Output is { } output) { w.WritePropertyName("output"); output.WriteTo(w); }
        if (result.FailureReason is { } reason) w.WriteString("failureReason", reason.Value);
        Native(w, "metadata", result.Metadata);
        w.WriteStartObject("evidence");
        w.WriteString("kind", result.Evidence.Kind == TerminalEvidenceKind.StatusReport ? "status-report" : "retained-terminal-event");
        w.WriteString("cursor", result.Evidence.Cursor.Value);
        w.WriteEndObject();
        w.WriteEndObject();
    }

    private static void Attempt(Utf8JsonWriter w, AttemptEvidence attempt)
    {
        w.WriteStartObject("attempt");
        w.WriteString("operation", attempt.Operation);
        w.WriteString("outcome", attempt.Outcome switch
        {
            NativeAttemptOutcome.Acknowledged => "acknowledged",
            NativeAttemptOutcome.Rejected => "rejected",
            NativeAttemptOutcome.NotSent => "not-sent",
            _ => "unknown",
        });
        w.WriteString("correlationId", attempt.CorrelationId);
        if (attempt.Cancelled) w.WriteBoolean("cancelled", true);
        if (attempt.ProposedRunId is { } proposed) w.WriteString("proposedRunId", proposed.Value);
        if (attempt.AcknowledgedRunId is { } acknowledged)
        {
            w.WriteString("acknowledgedRunId", acknowledged.Value);
            w.WriteBoolean("runIdsMatch", acknowledged == attempt.ProposedRunId);
        }
        w.WriteEndObject();
    }

    /// <summary>The latest validated positions a wait reached. Status bodies stay out of errors: they hold authored titles.</summary>
    private static void Evidence(Utf8JsonWriter w, RunWaitEvidence evidence)
    {
        w.WriteStartObject("evidence");
        if (evidence.Status is { } status) w.WriteString("statusCursor", status.AtCursor.Value);
        if (evidence.LastEvent is { } last) w.WriteString("lastEventCursor", last.Cursor.Value);
        if (evidence.ResumeAfter is { } resume) w.WriteString("resumeAfter", resume.Cursor.Value);
        w.WriteEndObject();
    }

    /// <summary>The largest Unix millisecond timestamp a <see cref="DateTimeOffset"/> can hold.</summary>
    private const ulong MaxTimestamp = 253_402_300_799_999;

    /// <summary>A native cursor is opaque, unconstrained text; readable output must not pass control characters to a terminal.</summary>
    private static string Printable(string value) => string.Concat(value.Select(c => char.IsControl(c) ? '�' : c));

    /// <summary>A native enum's wire name, such as <c>warn</c>.</summary>
    private static string Wire<T>(T value) => Encoding.UTF8.GetString(NativeJson.SerializeUtf8(value)).Trim('"');

    /// <summary>The scoped checkpoint exactly as <see cref="HistoryCheckpoint.ToJson"/> exports it, so it can be saved and passed to --checkpoint.</summary>
    private static void Checkpoint(Utf8JsonWriter w, HistoryCheckpoint checkpoint)
    {
        w.WritePropertyName("checkpoint");
        w.WriteRawValue(checkpoint.ToJson(), skipInputValidation: true);
    }

    private static void Native<T>(Utf8JsonWriter w, string name, T value)
    {
        w.WritePropertyName(name);
        w.WriteRawValue(NativeJson.SerializeUtf8(value), skipInputValidation: true);
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
        var record = buffer.ToArray();
        CliContract.Check(kind, record);
        writer.WriteLine(Encoding.UTF8.GetString(record));
    }
}
