namespace Zeroshot.Native;

/// <summary>
/// Classifies one submission attempt without retrying. A transport adapter sends once and explains its own
/// failures: whether the request may have reached native and whether the failure is a refusal that proves no effect.
/// </summary>
internal static class SubmissionAttempt
{
    /// <summary>What a transport proves about a failure that is attempt evidence rather than invalid use.</summary>
    internal readonly record struct Evidence(Guid CorrelationId, bool Sent, bool Refused);

    /// <param name="send">Sends once and passes each fully validated response, with its correlation ID, to the capture callback.</param>
    /// <param name="explain">Evidence for a failure the attempt reports; null rethrows it.</param>
    /// <param name="afterCapture">Lets tests place cancellation between capture and operation completion.</param>
    internal static async Task<NativeAttempt<T>> RunAsync<T>(Uri? origin, string operation, Func<Action<Guid, T>, Task> send,
        Func<Exception, Evidence?> explain, Action? afterCapture = null) where T : class
    {
        var acknowledgedId = Guid.Empty;
        T? acknowledged = null;
        Evidence? evidence = null;
        Exception? failure = null;
        try
        {
            await send((id, response) =>
            {
                acknowledgedId = id;
                Volatile.Write(ref acknowledged, response);
                afterCapture?.Invoke();
            }).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            if ((evidence = explain(error)) is null) throw;
            failure = error;
        }

        // The operation has finished its bounded cleanup. A validated response wins a cancellation race that ends
        // the operation after capture, and its failure is not reported. Adapters capture before they complete.
        var captured = Volatile.Read(ref acknowledged);
        var outcome = captured is not null ? NativeAttemptOutcome.Acknowledged
            : evidence is not { Sent: true } sent ? NativeAttemptOutcome.NotSent
            : sent.Refused ? NativeAttemptOutcome.Rejected : NativeAttemptOutcome.Unknown;
        return new(origin, operation, evidence?.CorrelationId ?? acknowledgedId, outcome, captured, captured is null ? failure : null);
    }
}
