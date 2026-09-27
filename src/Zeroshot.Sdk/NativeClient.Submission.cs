using System.Net;
using Zeroshot.Native.Contracts;
using Zeroshot.Native.Execution;

namespace Zeroshot.Native;

public sealed partial class NativeClient
{
    internal Task<TargetSubmissionAttempt> SubmitAttemptAsync(TargetRunRequest request,
        TargetControlCredentials? credentials, CancellationToken cancellationToken)
    {
        ValidateSubmissionUse();
        ArgumentNullException.ThrowIfNull(request);
        var body = NativeJson.SerializeUtf8(request);
        return SubmitAttemptAsync(PreparedSubmission.Create(request.RunId, request.Submission), body, credentials, cancellationToken);
    }

    internal Task<TargetSubmissionAttempt> SubmitAttemptAsync(PreparedSubmission prepared,
        TargetRunCredentials runCredentials, TargetControlCredentials? credentials, CancellationToken cancellationToken)
    {
        ValidateSubmissionUse();
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(runCredentials);
        return SubmitAttemptAsync(prepared, prepared.WithCredentials(runCredentials), credentials, cancellationToken);
    }

    private void ValidateSubmissionUse()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (http.DefaultRequestHeaders.Authorization is not null)
            throw new ArgumentException("Supply credentials per operation, not as HTTP default headers.");
    }

    private async Task<TargetSubmissionAttempt> SubmitAttemptAsync(PreparedSubmission prepared, byte[] body,
        TargetControlCredentials? credentials, CancellationToken cancellationToken)
    {
        var dispatched = 0;
        var correlationId = Guid.Empty;
        TargetRunReceipt? receipt = null;
        Exception? failure = null;
        try
        {
            await ExecuteJsonAsync<TargetRunReceipt>(NativeTargetClient.SubmitOperation,
                new Uri(Origin, "/native-v2/run"), body, credentials, _ => { }, cancellationToken,
                onDispatch: id => { correlationId = id; Interlocked.Exchange(ref dispatched, 1); },
                onResponse: value => Volatile.Write(ref receipt, value)).ConfigureAwait(false);
        }
        catch (Exception error) when (error is NativeHttpException or OperationCanceledException)
        {
            failure = error;
            correlationId = error switch
            {
                NativeHttpException httpFailure => httpFailure.CorrelationId,
                OperationCancelled cancelled => cancelled.CorrelationId,
                _ => correlationId
            };
        }

        // ExecuteJsonAsync has finished its bounded cleanup. A response already validated
        // by the adapter wins a cancellation race, including cancellation during cleanup.
        var captured = Volatile.Read(ref receipt);
        var outcome = captured is not null ? NativeAttemptOutcome.Acknowledged
            : Volatile.Read(ref dispatched) == 0 ? NativeAttemptOutcome.NotSent
            : IsSubmissionRefusal(failure) ? NativeAttemptOutcome.Rejected : NativeAttemptOutcome.Unknown;
        return new TargetSubmissionAttempt(Origin, correlationId, prepared, outcome, captured, captured is null ? failure : null);
    }

    private static bool IsSubmissionRefusal(Exception? failure) => failure is NativeHttpException
        { Kind: NativeHttpFailureKind.HttpStatus, Problem: { } problem } httpFailure &&
        (httpFailure.StatusCode, problem.Code) is
            (HttpStatusCode.BadRequest, "request.invalid" or "run.rejected") or
            (HttpStatusCode.Unauthorized, "request.unauthorized") or
            (HttpStatusCode.NotFound, "request.not_found") or
            (HttpStatusCode.RequestTimeout, "request.timeout") or
            (HttpStatusCode.Conflict, "request.conflict");
    // In particular 503/target.unavailable can follow run creation. Neither that,
    // a local timeout, an arbitrary HTTP error, nor an invalid receipt proves rejection.
}

public sealed partial class NativeTargetClient
{
    internal static readonly OperationDescriptor SubmitOperation = new("target.submit", OperationTransport.Http,
        requestBytes: 4 * 1024 * 1024, responseBytes: 64 * 1024);

    /// <summary>Sends the typed fixed HTTP request once. Operational failures and cancellation return evidence.</summary>
    public Task<TargetSubmissionAttempt> SubmitAttemptAsync(TargetRunRequest request,
        TargetControlCredentials? credentials = null, CancellationToken cancellationToken = default)
        => client.SubmitAttemptAsync(request, credentials, cancellationToken);

    /// <summary>Sends exact retained content with freshly supplied outer credentials, without regeneration or replay policy.</summary>
    public Task<TargetSubmissionAttempt> SubmitAttemptAsync(PreparedSubmission prepared,
        TargetRunCredentials runCredentials, TargetControlCredentials? credentials = null,
        CancellationToken cancellationToken = default)
        => client.SubmitAttemptAsync(prepared, runCredentials, credentials, cancellationToken);
}
