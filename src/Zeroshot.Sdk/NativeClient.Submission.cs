using System.Net;
using Zeroshot.Native.Contracts;
using Zeroshot.Native.Execution;

namespace Zeroshot.Native;

public sealed partial class NativeTargetClient
{
    internal static readonly OperationDescriptor SubmitOperation = new("target.submit", OperationTransport.Http,
        requestBytes: 4 * 1024 * 1024, responseBytes: 64 * 1024);
    private static readonly HttpBinding<TargetRunReceipt> Submit = new(SubmitOperation, refusals: IsSubmissionRefusal);

    /// <summary>Sends the typed fixed HTTP request once. Operational failures and cancellation return evidence.</summary>
    public Task<TargetSubmissionAttempt> SubmitAttemptAsync(TargetRunRequest request,
        TargetControlCredentials? credentials = null, CancellationToken cancellationToken = default)
        => SubmitAsync(() =>
        {
            ArgumentNullException.ThrowIfNull(request);
            var body = NativeJson.SerializeUtf8(request);
            return (PreparedSubmission.Create(request.RunId, request.Submission), body);
        }, credentials, cancellationToken);

    /// <summary>Sends exact retained content with freshly supplied outer credentials, without regeneration or replay policy.</summary>
    public Task<TargetSubmissionAttempt> SubmitAttemptAsync(PreparedSubmission prepared,
        TargetRunCredentials runCredentials, TargetControlCredentials? credentials = null,
        CancellationToken cancellationToken = default)
        => SubmitAsync(() =>
        {
            ArgumentNullException.ThrowIfNull(prepared);
            ArgumentNullException.ThrowIfNull(runCredentials);
            return (prepared, prepared.WithCredentials(runCredentials));
        }, credentials, cancellationToken);

    private async Task<TargetSubmissionAttempt> SubmitAsync(Func<(PreparedSubmission Prepared, byte[] Body)> prepare,
        TargetControlCredentials? credentials, CancellationToken cancellationToken)
    {
        PreparedSubmission? prepared = null;
        var attempt = await client.MutateAsync(Submit, () =>
        {
            (prepared, var body) = prepare();
            return new HttpCall(new Uri(client.Origin, "/native-v2/run"), body);
        }, credentials, cancellationToken).ConfigureAwait(false);
        return new TargetSubmissionAttempt(client.Origin, attempt.CorrelationId, prepared!, attempt.Outcome, attempt.Response, attempt.Failure);
    }

    private static bool IsSubmissionRefusal(HttpStatusCode? status, string code) =>
        (status, code) is
            (HttpStatusCode.BadRequest, "request.invalid" or "run.rejected") or
            (HttpStatusCode.Unauthorized, "request.unauthorized") or
            (HttpStatusCode.NotFound, "request.not_found") or
            (HttpStatusCode.RequestTimeout, "request.timeout") or
            (HttpStatusCode.Conflict, "request.conflict");
    // In particular 503/target.unavailable can follow run creation. Neither that,
    // a local timeout, an arbitrary HTTP error, nor an invalid receipt proves rejection.
}
