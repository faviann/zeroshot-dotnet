using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Zeroshot;
using Zeroshot.Client.Tests;
using Zeroshot.Native;
using Zeroshot.Native.Contracts;

namespace Zeroshot.Sdk.Tests;

// SDK submission over the lower Target.SubmitAttemptAsync. Outcome classification itself is covered by
// HttpSubmissionTests; these cases prove what the SDK adds: preparation, the exception mapping and the handle.
public sealed class SubmitTests
{
    private const string Acknowledged = "0195af77-1000-7000-8000-000000000002";
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");
    private static readonly NativeBinding Supported = NativeBinding.CallerSupplied("10.9.0", "75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa");
    private static PreparedSubmission Retained() => PreparedSubmission.ImportUtf8(File.ReadAllBytes(Path.Combine(Fixtures, "prepared.json")));
    private static string Receipt(string id) => JsonSerializer.Serialize(new { runId = id });

    private static ZeroshotClient Client(Handler handler, NativeBinding? binding = null, TransportOptions? transport = null)
        => new(NativeClient.ForHttp(new NativeClientOptions { Origin = new Uri("https://target.example/"), Transport = transport ?? new() },
            new HttpClient(handler), ownsHttpClient: true), binding ?? Supported, ownsClient: true);

    private static string Prefix(PreparedSubmission prepared)
    {
        var text = Encoding.UTF8.GetString(prepared.ExportUtf8());
        return text[..text.LastIndexOf('}')];
    }

    private static async Task<T> CatchAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T error) { return error; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    [Test]
    public async Task OrdinaryRequestGeneratesIdentityOnceAndTheHandleFollowsADifferentAcknowledgedRun()
    {
        var fields = JsonNode.Parse(File.ReadAllText(Path.Combine(Fixtures, "prepared.json")))!["submission"]!.AsObject();
        fields.Remove("submissionKey");
        var request = RunRequest.ParseUtf8(Encoding.UTF8.GetBytes(fields.ToJsonString()));
        var bodies = new List<string>();
        var handler = new Handler(async (request, token) =>
        {
            bodies.Add(await request.Content!.ReadAsStringAsync(token));
            return Reply(request, Receipt(Acknowledged));
        });
        await using var sdk = Client(handler);

        var run = await sdk.SubmitAsync(request);
        var attempt = run.Submission!;
        var sent = NativeJson.DeserializeUtf8<TargetRunRequest>(Encoding.UTF8.GetBytes(bodies.Single()));
        Check(handler.Calls == 1, "One mutation attempt.");
        Check(sent.RunId == attempt.ProposedRunId && sent.Submission.SubmissionKey == attempt.Prepared.Submission.SubmissionKey &&
            sent.Submission.SubmissionKey.Value.StartsWith("dotnet-", StringComparison.Ordinal), "The generated identity is the one sent.");
        Check(bodies[0].StartsWith(Prefix(attempt.Prepared), StringComparison.Ordinal), "The prepared bytes are sent.");
        Check(run.Id.Value == Acknowledged && attempt.AcknowledgedRunId == run.Id && attempt.ProposedRunId != run.Id &&
            attempt.RunIdsMatch == false && attempt.Outcome == NativeAttemptOutcome.Acknowledged, "Both IDs and the mismatch are kept.");
        Check(run.Reference.RunId == run.Id && sdk.GetRun(run.Id).Submission is null, "The handle follows the acknowledged run.");
    }

    [Test]
    public async Task PreparedOverloadsSendTheExactRetainedRequestWithoutRegenerating()
    {
        var retained = Retained();
        var bodies = new List<string>();
        var handler = new Handler(async (request, token) =>
        {
            bodies.Add(await request.Content!.ReadAsStringAsync(token));
            return Reply(request, Receipt(retained.RunId.Value));
        });
        await using var sdk = Client(handler);

        var run = await sdk.SubmitAsync(retained);
        var attempt = await sdk.SubmitAttemptAsync(retained);
        Check(handler.Calls == 2 && bodies.All(body => body.StartsWith(Prefix(retained), StringComparison.Ordinal)), "Exact bytes, one send each.");
        Check(ReferenceEquals(run.Submission!.Prepared, retained) && ReferenceEquals(attempt.Prepared, retained), "Same prepared request.");
        Check(run.Id == retained.RunId && run.Submission.RunIdsMatch == true && attempt.RunIdsMatch == true, "Matching identity.");
    }

    [Test]
    public async Task UnacknowledgedOutcomesThrowCarryingTheSameEvidenceAsTheExplicitAttempt()
    {
        foreach (var (status, body, limit, outcome) in new[]
        {
            // 503 can follow native run creation, so it is never a rejection.
            (503, """{"code":"target.unavailable","message":"secret-canary"}""", 65536, NativeAttemptOutcome.Unknown),
            (409, """{"code":"request.conflict","message":"secret-canary"}""", 65536, NativeAttemptOutcome.Rejected),
            (200, "{broken", 65536, NativeAttemptOutcome.Unknown),
            (200, Receipt(Acknowledged), 8, NativeAttemptOutcome.Unknown)
        })
        {
            var handler = new Handler((request, _) => Task.FromResult(Reply(request, body, (HttpStatusCode)status)));
            await using var sdk = Client(handler, transport: new() { MaxResponseBytes = limit });
            var retained = Retained();

            var explicitAttempt = await sdk.SubmitAttemptAsync(retained);
            Check(explicitAttempt.Outcome == outcome && handler.Calls == 1, $"Explicit {status} is {outcome}.");
            var error = await CatchAsync<SubmissionException>(() => sdk.SubmitAsync(retained));
            Check(handler.Calls == 2, "Each call makes exactly one attempt; nothing is resent.");
            Check(error.Attempt.Outcome == outcome && ReferenceEquals(error.Attempt.Prepared, retained) &&
                error.Attempt.AcknowledgedRunId is null && error.InnerException == error.Attempt.Failure, $"Ordinary {status} carries the attempt.");
            Check(!error.ToString().Contains("secret-canary"), "No remote body in diagnostics.");
        }
    }

    [Test]
    public async Task CancellationThrowsAnOperationCanceledSubtypeWithNotSentOrUnknownEvidence()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler(async (request, _) =>
        {
            entered.SetResult();
            await release.Task; // A supplied transport can ignore cancellation and reply late.
            return Reply(request, Receipt(Acknowledged));
        });
        await using var sdk = Client(handler);

        using var before = new CancellationTokenSource();
        before.Cancel();
        Check((await sdk.SubmitAttemptAsync(Retained(), cancellationToken: before.Token)).Outcome == NativeAttemptOutcome.NotSent, "Explicit not sent.");
        OperationCanceledException notSent = await CatchAsync<SubmissionCanceledException>(() => sdk.SubmitAsync(Retained(), cancellationToken: before.Token));
        Check(notSent is SubmissionCanceledException { Attempt.Outcome: NativeAttemptOutcome.NotSent } &&
            notSent.CancellationToken == before.Token && handler.Calls == 0, "Cancelled before dispatch.");

        using var after = new CancellationTokenSource();
        var pending = CatchAsync<SubmissionCanceledException>(() => sdk.SubmitAsync(Retained(), cancellationToken: after.Token));
        await entered.Task;
        after.Cancel();
        var unknown = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        release.SetResult();
        Check(unknown.Attempt is { Outcome: NativeAttemptOutcome.Unknown, AcknowledgedRunId: null } &&
            unknown.CancellationToken == after.Token && handler.Calls == 1, "Cancelled after dispatch is unknown; a late reply is not adopted.");
    }

    [Test]
    public async Task DisposalMidFlightReportsTheNativeLifetimeToken()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler(async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException();
        });
        var sdk = Client(handler);
        var pending = CatchAsync<SubmissionCanceledException>(() => sdk.SubmitAsync(Retained()));
        await entered.Task;
        sdk.Dispose();
        var error = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Check(error.Attempt.Outcome == NativeAttemptOutcome.Unknown && error.CancellationToken.IsCancellationRequested &&
            handler.Calls == 1, "The cancelling lifetime token is reported, not the caller's uncancelled token.");
    }

    [Test]
    public async Task ACapturedAcknowledgementWinsACancellationRace()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new Handler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new CleanupContent(Receipt(Acknowledged), () => { cancellation.Cancel(); throw new IOException("cleanup"); })
        }));
        await using var sdk = Client(handler);
        var run = await sdk.SubmitAsync(Retained(), cancellationToken: cancellation.Token);
        Check(cancellation.IsCancellationRequested && run.Id.Value == Acknowledged && handler.Calls == 1, "The acknowledged handle is returned.");
    }

    [Test]
    public async Task BindingRefusalIsNotSentAndInvalidUseThrowsWithoutDispatch()
    {
        var handler = new Handler((request, _) => Task.FromResult(Reply(request, Receipt(Acknowledged))));
        await using (var missing = new ZeroshotClient(NativeClient.ForHttp(new NativeClientOptions { Origin = new Uri("https://target.example/") },
            new HttpClient(handler), ownsHttpClient: true), null, ownsClient: true))
        {
            var retained = Retained();
            var attempt = await missing.SubmitAttemptAsync(retained);
            Check(attempt is { Outcome: NativeAttemptOutcome.NotSent, Failure: NativeBindingException { Reason: NativeBindingProblem.Missing } } &&
                ReferenceEquals(attempt.Prepared, retained), "Explicit binding refusal is not sent.");
            var error = await CatchAsync<SubmissionException>(() => missing.SubmitAsync(retained));
            Check(error.Attempt.Outcome == NativeAttemptOutcome.NotSent &&
                error.InnerException is NativeBindingException { Reason: NativeBindingProblem.Missing }, "Ordinary binding refusal carries the attempt.");
        }
        var sdk = Client(handler);
        await CatchAsync<ArgumentNullException>(() => sdk.SubmitAttemptAsync(null!));
        await CatchAsync<ArgumentNullException>(() => sdk.SubmitAsync((RunRequest)null!));
        sdk.Dispose();
        await CatchAsync<ObjectDisposedException>(() => sdk.SubmitAsync(Retained()));
        Check(handler.Calls == 0, "Nothing dispatched.");
    }

    private sealed class CleanupContent(string body, Action cleanup) : StringContent(body)
    {
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) cleanup();
        }
    }
}
