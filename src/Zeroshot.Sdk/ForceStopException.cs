using Zeroshot.Native;
using Zeroshot.Native.Contracts;

namespace Zeroshot;

/// <summary>
/// <see cref="Run.ForceStopAsync"/> sent no acknowledged force. <see cref="Attempt"/> holds its outcome (rejected, not
/// sent or unknown) and safe native evidence; an unknown outcome may still have stopped the run. Nothing was resent.
/// </summary>
public sealed class ForceStopException : Exception
{
    public Run Run { get; }
    public NativeAttempt<RunForceResult> Attempt { get; }
    internal ForceStopException(Run run, NativeAttempt<RunForceResult> attempt)
        : base($"Force was not acknowledged: {attempt.Outcome}.", attempt.Failure) => (Run, Attempt) = (run, attempt);
}

/// <summary>
/// <see cref="Run.ForceStopAsync"/> was cancelled before force was acknowledged. <see cref="Attempt"/> says whether the
/// request was not sent or its effect is unknown. Its token is the caller's when the caller cancelled.
/// </summary>
public sealed class ForceStopCanceledException : OperationCanceledException
{
    public Run Run { get; }
    public NativeAttempt<RunForceResult> Attempt { get; }
    internal ForceStopCanceledException(Run run, NativeAttempt<RunForceResult> attempt, CancellationToken cancellationToken)
        : base($"Force was cancelled: {attempt.Outcome}.", attempt.Failure, cancellationToken) => (Run, Attempt) = (run, attempt);
}
