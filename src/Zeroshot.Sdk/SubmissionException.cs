using Zeroshot.Native;

namespace Zeroshot;

/// <summary>
/// Ordinary submission ended without an acknowledgement. <see cref="Attempt"/> holds the prepared request, its outcome
/// (rejected, not sent or unknown) and safe native evidence; an unknown outcome may still have created a run.
/// </summary>
public sealed class SubmissionException : Exception
{
    public TargetSubmissionAttempt Attempt { get; }
    internal SubmissionException(TargetSubmissionAttempt attempt)
        : base($"Submission was not acknowledged: {attempt.Outcome}.", attempt.Failure) => Attempt = attempt;
}

/// <summary>
/// Ordinary submission was cancelled without an acknowledgement. <see cref="Attempt"/> says whether the request was
/// not sent or its effect is unknown, and holds the prepared request for an explicit replay. Its token is the one that
/// cancelled the attempt: the caller's, or the native client's lifetime when that client was disposed.
/// </summary>
public sealed class SubmissionCanceledException : OperationCanceledException
{
    public TargetSubmissionAttempt Attempt { get; }
    internal SubmissionCanceledException(TargetSubmissionAttempt attempt, CancellationToken cancellationToken)
        : base($"Submission was cancelled: {attempt.Outcome}.", attempt.Failure, cancellationToken) => Attempt = attempt;
}
