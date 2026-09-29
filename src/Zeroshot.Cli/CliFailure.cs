using Zeroshot.Native;
using Zeroshot.Native.Contracts;

namespace Zeroshot.Cli;

internal static class ExitCodes
{
    public const int Success = 0;
    public const int Failure = 1;
    public const int Invalid = 2;
    public const int RunFailed = 3;
    public const int Timeout = 4;
    public const int UnknownOutcome = 5;
    public const int Cancelled = 130;
}

/// <summary>
/// A failure reported as one safe error record. The message is CLI-authored: it may name files, fields and
/// environment variables, but never request content, credential values or remote text. Known run identity,
/// attempt evidence and wait evidence travel with it so a later failure never loses an acknowledgement.
/// </summary>
internal sealed class CliFailure(string category, string message, int exitCode) : Exception(message)
{
    public string Category { get; } = category;
    public int ExitCode { get; } = exitCode;
    /// <summary>The exact run this failure concerns: acknowledged, reopened or forced. Never a merely proposed ID.</summary>
    public RunId? RunId { get; init; }
    public AttemptEvidence? Attempt { get; init; }
    public RunWaitEvidence? Evidence { get; init; }
    public NativeFailure? Native { get; init; }

    public static CliFailure Invocation(string message) => new("invocation", message, ExitCodes.Invalid);
    public static CliFailure Configuration(string message) => new("configuration", message, ExitCodes.Invalid);
    public static CliFailure Input(string message) => new("input", message, ExitCodes.Invalid);
    public static CliFailure Credentials(string message) => new("credentials", message, ExitCodes.Invalid);
    public static CliFailure OutputExists(string message) => new("output-exists", message, ExitCodes.Invalid);
    public static CliFailure Output(string message) => new("output", message, ExitCodes.Failure);
}

/// <summary>
/// The SDK's typed facts about a native HTTP or OECP failure: kind, status and codes. Remote messages and details stay out.
/// </summary>
internal sealed record NativeFailure(string Transport, string Kind, int? HttpStatus, string? ProblemCode, long? RpcCode, string? DomainCode)
{
    /// <summary>The first native HTTP or OECP failure in <paramref name="error"/> or its inner exceptions.</summary>
    public static NativeFailure? Of(Exception? error)
    {
        for (; error is not null; error = error.InnerException)
        {
            if (error is NativeHttpException http)
                return new("http", Camel(http.Kind.ToString()), (int?)http.StatusCode, http.Problem?.Code, null, null);
            if (error is NativeOecpException oecp)
                return new("oecp", Camel(oecp.Kind.ToString()), null, null, oecp.RpcError?.Code, oecp.RpcError?.Data?.Code);
        }
        return null;
    }

    private static string Camel(string name) => char.ToLowerInvariant(name[0]) + name[1..];
}

/// <summary>What is known of one mutation attempt: the SDK's classification, never re-derived by the CLI.</summary>
internal sealed record AttemptEvidence(string Operation, NativeAttemptOutcome Outcome, Guid CorrelationId, bool Cancelled,
    RunId? ProposedRunId, RunId? AcknowledgedRunId)
{
    public static AttemptEvidence Of<T>(NativeAttempt<T> attempt) where T : class
    {
        var submission = attempt as TargetSubmissionAttempt;
        return new(attempt.Operation, attempt.Outcome, attempt.CorrelationId, attempt.Failure is OperationCanceledException,
            submission?.ProposedRunId, submission?.AcknowledgedRunId);
    }
}
