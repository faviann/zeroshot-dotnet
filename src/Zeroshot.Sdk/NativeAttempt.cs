using System.Net;
using Zeroshot.Native.Contracts;

namespace Zeroshot.Native;

/// <summary>A successful HEAD response. Its absent body is never parsed or synthesized.</summary>
public sealed class NativeHeadResult
{
    public HttpStatusCode StatusCode { get; }
    /// <summary>The advertised length of the corresponding GET body, when the peer sent one.</summary>
    public long? ContentLength { get; }
    public string? MediaType { get; }
    internal NativeHeadResult(HttpStatusCode statusCode, long? contentLength, string? mediaType)
        => (StatusCode, ContentLength, MediaType) = (statusCode, contentLength, mediaType);
}

public enum NativeAttemptOutcome { Acknowledged, Rejected, NotSent, Unknown }

/// <summary>Evidence from one operation, without automatic retry or consumer acceptance policy.</summary>
public class NativeAttempt<T> where T : class
{
    public Uri Origin { get; }
    public string Operation { get; }
    public Guid CorrelationId { get; }
    public NativeAttemptOutcome Outcome { get; }
    /// <summary>A fully validated response, present only for an acknowledged attempt.</summary>
    public T? Response { get; }
    /// <summary>Safe operational or cancellation evidence. Invalid use throws instead of returning an attempt.</summary>
    public Exception? Failure { get; }

    internal NativeAttempt(Uri origin, string operation, Guid correlationId, NativeAttemptOutcome outcome, T? response, Exception? failure)
        => (Origin, Operation, CorrelationId, Outcome, Response, Failure) = (origin, operation, correlationId, outcome, response, failure);

    public override string ToString() => $"{nameof(NativeAttempt<T>)}: {Outcome}";
}

/// <summary>Submission identity evidence. A different acknowledged ID is valid native data.</summary>
public sealed class TargetSubmissionAttempt : NativeAttempt<TargetRunReceipt>
{
    public PreparedSubmission Prepared { get; }
    public RunId ProposedRunId => Prepared.RunId;
    public RunId? AcknowledgedRunId => Response?.RunId;
    public bool? RunIdsMatch => Response is null ? null : ProposedRunId == Response.RunId;

    internal TargetSubmissionAttempt(Uri origin, Guid correlationId, PreparedSubmission prepared, NativeAttemptOutcome outcome,
        TargetRunReceipt? response, Exception? failure) : base(origin, NativeTargetClient.SubmitOperation.Name, correlationId, outcome, response, failure)
        => Prepared = prepared;
}
