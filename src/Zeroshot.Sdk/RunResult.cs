using System.Text.Json;
using Zeroshot.Native.Contracts;

namespace Zeroshot;

/// <summary>Where a terminal result was observed. Neither kind proves retained completion or physical cessation.</summary>
public enum TerminalEvidenceKind
{
    /// <summary>
    /// A status report, including the status returned by a force acknowledgement. Native can report status-only
    /// terminal failure without a durable event.
    /// </summary>
    StatusReport,
    /// <summary>A terminal event read from the target's retained native history.</summary>
    RetainedTerminalEvent
}

public sealed class TerminalEvidence
{
    public TerminalEvidenceKind Kind { get; }
    /// <summary>The status report's cursor or the retained event's cursor.</summary>
    public Cursor Cursor { get; }
    internal TerminalEvidence(TerminalEvidenceKind kind, Cursor cursor) { Kind = kind; Cursor = cursor; }
    public override string ToString() => Kind.ToString();
}

/// <summary>
/// A native terminal result. A failed run is result data, not an exception; success output is generic JSON
/// with no application approval implied.
/// </summary>
public sealed class RunResult
{
    public RunId RunId { get; }
    public bool IsSuccess { get; }
    /// <summary>Successful output, which may be a JSON null. Null only when the run failed.</summary>
    public JsonElement? Output { get; }
    /// <summary>The native failure reason. Null only when the run succeeded.</summary>
    public EnumLabel? FailureReason { get; }
    public RunMetadata Metadata { get; }
    public TerminalEvidence Evidence { get; }

    private RunResult(RunId runId, TerminalResult result, RunMetadata metadata, TerminalEvidence evidence)
    {
        RunId = runId; Metadata = metadata; Evidence = evidence;
        switch (result)
        {
            case SucceededTerminalResult succeeded: IsSuccess = true; Output = succeeded.Output; break;
            case FailedTerminalResult failed: FailureReason = failed.Reason; break;
            default: throw new ArgumentException("Unknown terminal result.", nameof(result));
        }
    }

    /// <summary>The terminal result reported by a status, labelled as status-report evidence; null while the run is not finished.</summary>
    public static RunResult? FromStatus(RunStatusResult status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return status.Status is FinishedRunStatus finished
            ? new RunResult(status.RunId, finished.TerminalResult, finished.Metadata, new(TerminalEvidenceKind.StatusReport, status.AtCursor))
            : null;
    }

    /// <summary>
    /// The terminal result already reported by a force acknowledgement, labelled as status-report evidence at the
    /// acknowledgement's cursor; null while it reports a nonterminal phase such as stopping.
    /// </summary>
    public static RunResult? FromForce(RunForceResult acknowledgement)
    {
        ArgumentNullException.ThrowIfNull(acknowledgement);
        return acknowledgement.Status is FinishedRunStatus finished
            ? new RunResult(acknowledgement.RunId, finished.TerminalResult, finished.Metadata,
                new(TerminalEvidenceKind.StatusReport, acknowledgement.AtCursor))
            : null;
    }

    /// <summary>The terminal result carried by a retained watch event; null for a nonterminal event.</summary>
    internal static RunResult? FromEvent(RunWatchEventNotification record)
        => record.Status is FinishedRunStatus finished
            ? new RunResult(record.RunId, finished.TerminalResult, finished.Metadata, new(TerminalEvidenceKind.RetainedTerminalEvent, record.Cursor))
            : null;

    /// <summary>Throws <see cref="RunFailedException"/> when the run failed.</summary>
    public void EnsureSuccess()
    {
        if (!IsSuccess) throw new RunFailedException(this);
    }

    public override string ToString() => IsSuccess ? "Run succeeded." : "Run failed.";
}

/// <summary>Raised only by <see cref="RunResult.EnsureSuccess"/>; carries the failed terminal result.</summary>
public sealed class RunFailedException : Exception
{
    public RunResult Result { get; }
    internal RunFailedException(RunResult result) : base($"Run failed: {result.FailureReason!.Value}.") => Result = result;
}
