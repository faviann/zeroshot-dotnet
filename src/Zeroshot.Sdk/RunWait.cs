using Zeroshot.Native.Contracts;

namespace Zeroshot;

/// <summary>
/// The latest validated observations a wait made before it ended without a terminal result. Each part may be null.
/// None of it is a claim about the run's outcome.
/// </summary>
public sealed class RunWaitEvidence
{
    /// <summary>The latest complete status report read by this wait.</summary>
    public RunStatusResult? Status { get; }
    /// <summary>The latest watch record delivered to this wait.</summary>
    public RunWatchEventNotification? LastEvent { get; }
    /// <summary>
    /// Where a new watch would continue: the last delivered record's checkpoint, else the status cursor the watch
    /// started from, else null when no watch had started.
    /// </summary>
    public HistoryCheckpoint? ResumeAfter { get; }

    internal RunWaitEvidence(RunStatusResult? status, RunWatchEventNotification? lastEvent, HistoryCheckpoint? resumeAfter)
    { Status = status; LastEvent = lastEvent; ResumeAfter = resumeAfter; }
}

public enum RunWaitFailureKind
{
    /// <summary>A status read failed; the inner exception is the native or transport failure.</summary>
    Status,
    /// <summary>Watch observation failed; the inner exception is the <see cref="RunObservationException"/>.</summary>
    Observation,
    /// <summary>The watch ended normally without a terminal event and the final status was not terminal.</summary>
    Incomplete,
    /// <summary>The wait budget expired; always a <see cref="RunWaitTimeoutException"/>.</summary>
    Timeout
}

/// <summary>
/// A wait ended without a terminal result. This is an SDK observation failure, never native run failure or completion.
/// <see cref="Run"/> is the exact handle waited on, including any submission acknowledgement.
/// </summary>
public class RunWaitException : Exception
{
    public RunWaitFailureKind Kind { get; }
    public Run Run { get; }
    public RunWaitEvidence Evidence { get; }

    internal RunWaitException(RunWaitFailureKind kind, Run run, RunWaitEvidence evidence, string message, Exception? inner)
        : base(message, inner)
    { Kind = kind; Run = run; Evidence = evidence; }
}

/// <summary>The wait's observation budget expired. The run was not stopped and may still finish.</summary>
public sealed class RunWaitTimeoutException : RunWaitException
{
    public TimeSpan Timeout { get; }

    internal RunWaitTimeoutException(Run run, TimeSpan timeout, RunWaitEvidence evidence, Exception? inner)
        : base(RunWaitFailureKind.Timeout, run, evidence, "The run did not report a terminal result within the wait timeout.", inner)
        => Timeout = timeout;
}

/// <summary>A wait was cancelled. Observation was detached; the run was not stopped.</summary>
public sealed class RunWaitCanceledException : OperationCanceledException
{
    public Run Run { get; }
    public RunWaitEvidence Evidence { get; }

    internal RunWaitCanceledException(Run run, RunWaitEvidence evidence, Exception inner, CancellationToken token)
        : base("Waiting for the run was cancelled.", inner, token)
    { Run = run; Evidence = evidence; }
}

internal static class RunWait
{
    /// <summary>Null is the only infinite spelling; zero is allowed and performs no observation.</summary>
    internal static void ValidateTimeout(TimeSpan? timeout)
    {
        if (timeout is { } value && (value < TimeSpan.Zero || value.TotalMilliseconds > uint.MaxValue - 1))
            throw new ArgumentOutOfRangeException(nameof(timeout), "A wait timeout is null (indefinite) or a non-negative, finite duration.");
    }
}
