using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Zeroshot.Client.Tests;
using Zeroshot.Native;
using Zeroshot.Native.Contracts;

namespace Zeroshot.Sdk.Tests;

public sealed class HttpSubmissionTests
{
    private const string Proposed = "018f5e78-7f95-7c22-8d98-3f15af20c991";
    private const string Acknowledged = "0195af77-1000-7000-8000-000000000002";
    private static TargetRunCredentials Empty => new() { Connections = ImmutableDictionary<string, ImmutableDictionary<string, string>>.Empty };
    private static NativeClientOptions Options(TransportOptions? transport = null) => new()
    { Origin = new Uri("https://target.example/"), Transport = transport ?? new() };
    private static PreparedSubmission Prepared() => PreparedSubmission.ImportUtf8(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures/prepared.json")));
    private static TargetRunRequest Typed() => new() { RunId = Prepared().RunId, Submission = Prepared().Submission, Connections = Empty.Connections };
    private static string Receipt(string id = Proposed) => JsonSerializer.Serialize(new { runId = id });

    [Test]
    public async Task TypedAndRetainedRequestsShareFixedRouteAndPreserveDifferentAcknowledgedIdentity()
    {
        using var handler = new Handler(async (request, token) =>
        {
            Check(request.Method == HttpMethod.Post && request.RequestUri == new Uri("https://target.example/native-v2/run"));
            var envelope = NativeJson.DeserializeUtf8<TargetRunRequest>(await request.Content!.ReadAsByteArrayAsync(token));
            Check(envelope.RunId.Value == Proposed && envelope.Submission.SubmissionKey.Value == "debug-fixture");
            return Reply(request, Receipt(Acknowledged));
        });
        using var http = new HttpClient(handler);
        using var client = NativeClient.ForHttp(Options(), http);
        foreach (var attempt in new[] { await client.Target.SubmitAttemptAsync(Typed()), await client.Target.SubmitAttemptAsync(Prepared(), Empty) })
        {
            NativeAttempt<TargetRunReceipt> shared = attempt;
            Check(shared.Outcome == NativeAttemptOutcome.Acknowledged && shared.Failure is null);
            Check(shared.Origin == client.Origin && shared.Operation == "target.submit" && shared.CorrelationId != Guid.Empty);
            Check(attempt.Prepared.RunId == attempt.ProposedRunId && attempt.Prepared.Submission.Source == Typed().Submission.Source);
            Check(attempt.ProposedRunId.Value == Proposed && attempt.AcknowledgedRunId!.Value == Acknowledged && attempt.RunIdsMatch == false);
        }
        Check(handler.Calls == 2);
    }

    [Test]
    public async Task RetainedContentIsNeverReserializedAndEachExplicitReplayUsesFreshOuterCredentials()
    {
        var original = Encoding.UTF8.GetString(Prepared().ExportUtf8()).Replace("\"test\"", "\"t\\u0065st\"")
            .Replace("\"initialInput\": null", "\"initialInput\": { \"number\" : 1e+01, \"text\": \"\\u0061\" }");
        var retained = PreparedSubmission.ImportUtf8(Encoding.UTF8.GetBytes(original));
        var bodies = new List<byte[]>();
        using var handler = new Handler(async (request, token) =>
        {
            var body = await request.Content!.ReadAsByteArrayAsync(token);
            bodies.Add(body);
            Check(request.Headers.Authorization!.Parameter == "control-" + bodies.Count);
            var wire = NativeJson.DeserializeUtf8<TargetRunRequest>(body);
            Check(wire.Connections["gateway"]["GATEWAY_API_KEY"] == "provider-" + bodies.Count);
            Check(wire.GithubToken == "github-" + bodies.Count && wire.ConnectionResolver!.BearerToken == "resolver-" + bodies.Count);
            return Reply(request, Receipt());
        });
        using var http = new HttpClient(handler);
        using var client = NativeClient.ForHttp(Options(), http);
        for (var i = 1; i <= 2; i++)
        {
            var fresh = new TargetRunCredentials
            {
                Connections = ImmutableDictionary<string, ImmutableDictionary<string, string>>.Empty.Add("gateway",
                    ImmutableDictionary<string, string>.Empty.Add("GATEWAY_API_KEY", "provider-" + i)),
                GithubToken = "github-" + i,
                ConnectionResolver = new() { Endpoint = "https://resolver.example/callback", BearerToken = "resolver-" + i, Keys = [new("dynamic")], SourceConnection = new("source") }
            };
            var attempt = await client.Target.SubmitAttemptAsync(retained, fresh, new(TargetAuthentication.HostedOauth, "control-" + i));
            Check(attempt.Outcome == NativeAttemptOutcome.Acknowledged && attempt.RunIdsMatch == true);
            Check(ReferenceEquals(attempt.Prepared, retained));
            Check(!fresh.ToString().Contains("provider-") && !fresh.ConnectionResolver.ToString().Contains("resolver-"));
        }
        Check(handler.Calls == 2 && original == Encoding.UTF8.GetString(retained.ExportUtf8()));
        foreach (var body in bodies)
            Check(Encoding.UTF8.GetString(body).StartsWith(original[..original.LastIndexOf('}')], StringComparison.Ordinal));
        Check(!original.Contains("provider-") && !original.Contains("github-") && !original.Contains("resolver-"));
    }

    [Test]
    public async Task OnlyPinnedStatusAndProblemPairsEstablishRejection()
    {
        foreach (var (status, code, rejected) in new[]
        {
            (400, "request.invalid", true), (400, "run.rejected", true), (401, "request.unauthorized", true),
            (404, "request.not_found", true), (408, "request.timeout", true), (409, "request.conflict", true),
            (503, "target.unavailable", false), (500, "target.internal_error", false),
            (403, "request.unauthorized", false), (409, "unknown.code", false), (500, "run.rejected", false)
        })
        {
            var body = JsonSerializer.Serialize(new { code, message = "secret-canary", details = new { runId = Acknowledged } });
            using var handler = new Handler((request, _) => Task.FromResult(Reply(request, body, (HttpStatusCode)status)));
            using var http = new HttpClient(handler);
            using var client = NativeClient.ForHttp(Options(new() { CaptureRawDiagnostics = true }), http);
            var attempt = await client.Target.SubmitAttemptAsync(Typed());
            Check(attempt.Outcome == (rejected ? NativeAttemptOutcome.Rejected : NativeAttemptOutcome.Unknown));
            Check(attempt.Failure is NativeHttpException { Problem: not null } failure && failure.StatusCode == (HttpStatusCode)status &&
                failure.Problem.Code == code && Encoding.UTF8.GetString(failure.ExportRawDiagnostic()!) == body && !failure.ToString().Contains("secret-canary"));
            Check(attempt.ProposedRunId.Value == Proposed && attempt.AcknowledgedRunId is null && attempt.RunIdsMatch is null && handler.Calls == 1);
        }
    }

    [Test]
    public async Task MalformedOversizedUnexpectedAndRedirectedRepliesRemainUnknownWithoutResend()
    {
        foreach (var (status, body, limit, kind) in new[]
        {
            (200, "{}", 65536, NativeHttpFailureKind.Protocol),
            (200, "{broken", 65536, NativeHttpFailureKind.Protocol),
            (200, "{\"runId\":null}", 65536, NativeHttpFailureKind.Protocol),
            (200, "{\"runId\":\"a\",\"runId\":\"b\"}", 65536, NativeHttpFailureKind.Protocol),
            (200, "{\"runId\":\"a\",\"extra\":true}", 65536, NativeHttpFailureKind.Protocol),
            (200, Receipt(), 8, NativeHttpFailureKind.SizeLimit),
            (200, new string('x', 65537), 100000, NativeHttpFailureKind.SizeLimit),
            (409, "malformed", 65536, NativeHttpFailureKind.HttpStatus),
            (409, new string('x', 65537), 100000, NativeHttpFailureKind.SizeLimit),
            (201, Receipt(), 65536, NativeHttpFailureKind.HttpStatus),
            (307, Receipt(), 65536, NativeHttpFailureKind.Redirect)
        })
        {
            using var handler = new Handler((request, _) => Task.FromResult(Reply(request, body, (HttpStatusCode)status)));
            using var http = new HttpClient(handler);
            using var client = NativeClient.ForHttp(Options(new() { MaxResponseBytes = limit }), http);
            var attempt = await client.Target.SubmitAttemptAsync(Prepared(), Empty);
            Check(attempt.Outcome == NativeAttemptOutcome.Unknown && attempt.Failure is NativeHttpException error && error.Kind == kind);
            Check(attempt.Response is null && handler.Calls == 1);
        }
    }

    [Test]
    public async Task LostReplyIsUnknownAndDoesNotResend()
    {
        using var handler = new Handler((_, _) => throw new HttpRequestException("credential-canary"));
        using var http = new HttpClient(handler);
        using var client = NativeClient.ForHttp(Options(), http);
        var attempt = await client.Target.SubmitAttemptAsync(Typed());
        Check(attempt.Outcome == NativeAttemptOutcome.Unknown && attempt.Failure is NativeHttpException { Kind: NativeHttpFailureKind.Transport });
        Check(handler.Calls == 1 && !attempt.Failure!.ToString().Contains("credential-canary"));
    }

    [Test]
    public async Task CancellationBeforeDispatchAndLocalSizeAdmissionReturnNotSent()
    {
        using var handler = new Handler((request, _) => Task.FromResult(Reply(request, Receipt())));
        using var http = new HttpClient(handler);
        using var client = NativeClient.ForHttp(Options(), http);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var before = await client.Target.SubmitAttemptAsync(Typed(), cancellationToken: cancelled.Token);
        Check(before.Outcome == NativeAttemptOutcome.NotSent && before.Failure is OperationCanceledException && handler.Calls == 0);
        Check(before.CorrelationId != Guid.Empty);
        using var small = NativeClient.ForHttp(Options(new() { MaxRequestBytes = 1 }), http);
        var oversized = await small.Target.SubmitAttemptAsync(Prepared(), Empty);
        Check(oversized.Outcome == NativeAttemptOutcome.NotSent && oversized.Failure is NativeHttpException { Kind: NativeHttpFailureKind.SizeLimit } && handler.Calls == 0);
    }

    [Test]
    public async Task CancellationAfterDispatchAndLateReplyReturnUnknownAndDisposeLateResponse()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler(async (request, _) =>
        {
            entered.SetResult();
            await release.Task; // A supplied transport can ignore cancellation.
            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new CleanupContent(Receipt(), () => closed.TrySetResult()) };
        });
        using var http = new HttpClient(handler);
        using var client = NativeClient.ForHttp(Options(), http);
        using var cancellation = new CancellationTokenSource();
        var pending = client.Target.SubmitAttemptAsync(Typed(), cancellationToken: cancellation.Token);
        await entered.Task;
        cancellation.Cancel();
        var attempt = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Check(attempt.Outcome == NativeAttemptOutcome.Unknown && attempt.Failure is OperationCanceledException);
        release.SetResult();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(attempt.Response is null && handler.Calls == 1);
    }

    [Test]
    public async Task CapturedReceiptSurvivesCancellationAndFailingCleanup()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new Handler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new CleanupContent(Receipt(Acknowledged), () => { cancellation.Cancel(); throw new IOException("cleanup-canary"); })
        }));
        using var http = new HttpClient(handler);
        using var client = NativeClient.ForHttp(Options(), http);
        var attempt = await client.Target.SubmitAttemptAsync(Typed(), cancellationToken: cancellation.Token);
        Check(cancellation.IsCancellationRequested && attempt.Outcome == NativeAttemptOutcome.Acknowledged);
        Check(attempt.AcknowledgedRunId!.Value == Acknowledged && attempt.Failure is null && handler.Calls == 1);
    }

    [Test]
    public async Task CapturedReceiptSurvivesCancellationThatLandsBeforeTheOperationCompletes()
    {
        using var handler = new Handler(async (request, _) =>
        {
            await Task.Yield(); // The operation must be pending, not complete synchronously, when cancellation lands.
            return Reply(request, Receipt(Acknowledged));
        });
        using var http = new HttpClient(handler);
        using var client = NativeClient.ForHttp(Options(), http);
        using var cancellation = new CancellationTokenSource();
        // Cancels inside the operation, after validation and capture: the executor reports cancellation, not the result.
        var attempt = await client.AttemptAsync<TargetRunReceipt>(NativeTargetClient.SubmitOperation, HttpResponsePolicy.OkOnly, new Uri(client.Origin, "/native-v2/run"),
            NativeJson.SerializeUtf8(Typed()), null, (_, _) => false, cancellation.Token, afterCapture: cancellation.Cancel);
        Check(cancellation.IsCancellationRequested && attempt is { Outcome: NativeAttemptOutcome.Acknowledged, Failure: null });
        Check(attempt.Response!.RunId.Value == Acknowledged && attempt.CorrelationId != Guid.Empty && handler.Calls == 1);
    }

    [Test]
    public async Task DeadlineAndCapacityAreEvidenceWithDispatchSpecificOutcomes()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler(async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException();
        });
        using var http = new HttpClient(handler);
        // The first attempt's deadline runs on a manual clock, so it holds its slot until the refusal is observed.
        var time = new Zeroshot.Client.Tests.ManualTime();
        using var client = NativeClient.ForHttp(Options(new() { MaxConcurrentRequests = 2, ReservedControlRequests = 1 }) with { Time = time }, http);
        var first = client.Target.SubmitAttemptAsync(Typed());
        await entered.Task;
        var capacity = await client.Target.SubmitAttemptAsync(Typed());
        Check(capacity.Outcome == NativeAttemptOutcome.NotSent && capacity.Failure is NativeHttpException { Kind: NativeHttpFailureKind.Capacity });
        time.Advance(new TransportOptions().RequestTimeout);
        var expired = await first;
        Check(expired.Outcome == NativeAttemptOutcome.Unknown && expired.Failure is NativeHttpException { Kind: NativeHttpFailureKind.Deadline } && handler.Calls == 1);
    }

    [Test]
    public async Task InvalidUseThrowsWithoutDispatch()
    {
        using var handler = new Handler((request, _) => Task.FromResult(Reply(request, Receipt())));
        using var http = new HttpClient(handler);
        using var client = NativeClient.ForHttp(Options(), http);
        await Throws<ArgumentNullException>(() => client.Target.SubmitAttemptAsync((TargetRunRequest)null!));
        await Throws<ArgumentNullException>(() => client.Target.SubmitAttemptAsync(Prepared(), null!));
        await Throws<JsonException>(() => client.Target.SubmitAttemptAsync(Typed() with { RunId = new("noncanonical") }));
        await Throws<JsonException>(() => client.Target.SubmitAttemptAsync(Typed() with { Connections = Empty.Connections.Add("empty", ImmutableDictionary<string, string>.Empty) }));
        await Throws<JsonException>(() => client.Target.SubmitAttemptAsync(Typed() with { Connections = Empty.Connections.Add("key", ImmutableDictionary<string, string>.Empty.Add("TOKEN", "\0")) }));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong-scope");
        await Throws<ArgumentException>(() => client.Target.SubmitAttemptAsync(Typed()));
        http.DefaultRequestHeaders.Authorization = null;
        client.Dispose();
        await Throws<ObjectDisposedException>(() => client.Target.SubmitAttemptAsync(Typed()));
        Check(handler.Calls == 0);
    }

    private static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected invalid-use exception.");
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
