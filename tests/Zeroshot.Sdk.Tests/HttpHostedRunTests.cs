using System.Net;
using System.Net.Http.Headers;
using System.Text;
using TUnit.Core;
using Zeroshot.Native;
using Zeroshot.Native.Contracts;

namespace Zeroshot.Client.Tests;

public sealed class HttpHostedRunTests
{
    private const string Bearer = "ACCESS-BEARER-CANARY";
    private const string Run = "run/1 ?%é";
    private const string EncodedRun = "run%2F1%20%3F%25%C3%A9";
    private const string Source = """{"repository":"acme/project","branch":"main","revision":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""";
    private const string Queued = """{"phase":"queued"}""";
    private const string Stopping = """{"phase":"stopping","activeExecutions":[{"execution":"worker:1","node":"worker"}]}""";
    private const string Finished = """{"phase":"finished","terminalResult":{"status":"failed","reason":"force_stopped"}}""";
    private const string Recovery = ""","workspaceRecovery":{"recoverable":true}""";
    private static readonly TargetControlCredentials Hosted = new(TargetAuthentication.HostedOauth, Bearer);
    private static readonly TargetHostedRunRoutes Routes = new()
    {
        List = "/native-v2/runs", Status = "/native-v2/runs/{run_id}", Watch = "/native-v2/runs/{run_id}/watch{?from_cursor}",
        Logs = "/native-v2/runs/{run_id}/logs{?from_cursor,execution}", Force = "/native-v2/runs/{run_id}/force"
    };
    private static readonly TargetHostedRunsDiscovery Capability = new()
    {
        Kind = "zeroshot.hosted-runs/v1", BaseUrl = "https://target.example/api/", RouteTemplates = Routes
    };

    private static TargetDiscoveryDocument Discovery(TargetHostedRunsDiscovery? hostedRuns = null,
        TargetAuthentication authentication = TargetAuthentication.HostedOauth)
        => TestDiscovery.Controller(authentication) with { Extensions = new() { HostedRuns = hostedRuns } };

    private static string Status(string status, string runId = Run, string extra = "") =>
        $$"""{"runId":"{{Json(runId)}}","title":"test","source":{{Source}},"size":"small","atCursor":"cloud:2","status":{{status}}{{extra}}}""";
    private static string Watch(int cursor, string status = Queued, string runId = Run, string subscription = "sub-1", string source = Source, string extra = "") =>
        $$"""{"type":"event","event":{"subscriptionId":"{{subscription}}","runId":"{{Json(runId)}}","title":"test","source":{{source}},"size":"small","cursor":"cloud:{{cursor}}","status":{{status}}{{extra}}""" + "}}";
    private static string Log(int cursor, string execution = "worker:1") =>
        $$"""{"type":"event","event":{"subscriptionId":"sub-1","runId":"{{Json(Run)}}","cursor":"cloud:{{cursor}}","timestamp":1,"execution":"{{execution}}","record":{"level":"info","target":"worker","message":"hello"}""" + "}}";
    private static string Closed(string reason) => $$"""{"type":"closed","reason":"{{reason}}"}""";
    private static string Json(string value) => System.Text.Json.JsonEncodedText.Encode(value).ToString();

    private static void Check(bool value, string message = "Hosted run assertion failed.")
    { if (!value) throw new InvalidOperationException(message); }

    private static NativeClient Client(Handler handler, TransportOptions? transport = null) => NativeClient.ForHttp(
        new NativeClientOptions { Origin = new Uri("https://target.example/"), Transport = transport ?? new() },
        new HttpClient(handler), ownsHttpClient: true);

    private static HttpResponseMessage Reply(HttpRequestMessage request, string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { RequestMessage = request, Content = new StringContent(body, Encoding.UTF8, "application/json") };

    // Native requires no NDJSON Content-Type; this peer sends none.
    private static HttpResponseMessage Stream(HttpRequestMessage request, FeedStream feed)
        => new(HttpStatusCode.OK) { RequestMessage = request, Content = new StreamContent(feed) };

    [Test]
    public async Task EachOperationUsesItsAdvertisedRouteWithEncodedValuesAndOnlyTheHostedBearer()
    {
        var seen = new List<(HttpMethod Method, string Url, string? Accept, bool NoStore, string? Body)>();
        var feeds = new List<FeedStream>();
        using var native = Client(new Handler(async (request, token) =>
        {
            Check(request.Headers.Authorization!.ToString() == "Bearer " + Bearer);
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(token);
            seen.Add((request.Method, request.RequestUri!.OriginalString, request.Headers.Accept.ToString(),
                request.Headers.CacheControl?.NoStore == true, body));
            var path = request.RequestUri.AbsolutePath;
            if (path.EndsWith("/watch") || path.EndsWith("/logs"))
            {
                var feed = new FeedStream();
                feeds.Add(feed);
                return Stream(request, feed);
            }
            // Host-owned responses: any 2xx carrying a valid body.
            return path switch
            {
                "/api/native-v2/runs" => Reply(request, $$"""{"runs":[{{Status(Queued)}},{{Status(Finished, extra: Recovery)}}]}"""),
                "/api/native-v2/runs/" + EncodedRun => Reply(request, Status(Queued)),
                _ => Reply(request, Status(Stopping), HttpStatusCode.Accepted)
            };
        }));
        var discovery = Discovery(Capability);
        var run = new RunId(Run);

        var list = await native.HostedRuns.ListAsync(discovery, Hosted);
        Check(list.Runs is [{ Status: QueuedHostedRunStatus, WorkspaceRecovery.HasValue: false },
            { Status: TargetHostedRunStatus { Status: FinishedRunStatus }, WorkspaceRecovery.Value.Recoverable: true }]);
        Check((await native.HostedRuns.StatusAsync(discovery, run, Hosted)).Status is QueuedHostedRunStatus);
        var force = await native.HostedRuns.ForceAsync(discovery, run, Hosted);
        Check(force is { Outcome: NativeAttemptOutcome.Acknowledged, Response.Status: TargetHostedRunStatus { Status: StoppingRunStatus } });
        await using (await native.HostedRuns.WatchAsync(discovery, new() { RunId = run }, Hosted)) { }
        await using (await native.HostedRuns.WatchAsync(discovery, new() { RunId = run, FromCursor = new("cloud:7 &") }, Hosted)) { }
        await using (await native.HostedRuns.LogsAsync(discovery, new() { RunId = run }, Hosted)) { }
        await using (await native.HostedRuns.LogsAsync(discovery, new() { RunId = run, FromCursor = new("cloud:8"), Execution = new("worker/1") }, Hosted)) { }

        const string Base = "https://target.example/api/native-v2/runs";
        (HttpMethod, string, string?, bool, string?)[] expected =
        [
            (HttpMethod.Get, Base, "application/json", true, null),
            (HttpMethod.Get, $"{Base}/{EncodedRun}", "application/json", true, null),
            (HttpMethod.Post, $"{Base}/{EncodedRun}/force", "application/json", false, "{}"),
            (HttpMethod.Get, $"{Base}/{EncodedRun}/watch", "application/x-ndjson", true, null),
            (HttpMethod.Get, $"{Base}/{EncodedRun}/watch?from_cursor=cloud%3A7+%26", "application/x-ndjson", true, null),
            (HttpMethod.Get, $"{Base}/{EncodedRun}/logs", "application/x-ndjson", true, null),
            (HttpMethod.Get, $"{Base}/{EncodedRun}/logs?from_cursor=cloud%3A8&execution=worker%2F1", "application/x-ndjson", true, null)
        ];
        Check(seen.SequenceEqual(expected), string.Join("\n", seen));
        Check(feeds.All(feed => feed.Disposed));
    }

    [Test]
    public void HostedTypesKeepQueuedStatusAndWatchOmitsWorkspaceRecovery()
    {
        foreach (var phase in new[] { Queued, """{"phase":"admitted"}""", """{"phase":"running","activeExecutions":[]}""", Stopping, Finished })
            foreach (var extra in new[] { "", Recovery })
            {
                var wire = Status(phase, extra: extra);
                var parsed = NativeJson.DeserializeUtf8<HostedRunStatusResult>(Encoding.UTF8.GetBytes(wire));
                Check((parsed.Status is QueuedHostedRunStatus) == (phase == Queued) && parsed.WorkspaceRecovery.HasValue == (extra != ""));
                var serialized = Encoding.UTF8.GetString(NativeJson.SerializeUtf8(parsed));
                Check(serialized.Contains("\"status\":" + phase[..^1]) && serialized.Contains("workspaceRecovery") == (extra != ""), wire);
            }
        foreach (var invalid in new[]
        {
            Status("""{"phase":"queued","activeExecutions":[]}"""), Status("""{"phase":"paused"}"""),
            Status(Queued, extra: ""","deduped":true"""), Status(Queued).Replace("\"test\"", "\"\"")
        })
            Expect<HostedRunStatusResult>(invalid);
        // Queued is host-only: native OECP status cannot carry it.
        Expect<RunStatusResult>(Status(Queued));
        var watch = Watch(1)[("""{"type":"event","event":""".Length)..^1];
        Check(NativeJson.DeserializeUtf8<HostedRunWatchEventNotification>(Encoding.UTF8.GetBytes(watch)).Status is QueuedHostedRunStatus);
        Expect<HostedRunWatchEventNotification>(watch[..^1] + Recovery + "}");

        static void Expect<T>(string wire)
        {
            try { NativeJson.DeserializeUtf8<T>(Encoding.UTF8.GetBytes(wire)); }
            catch (System.Text.Json.JsonException) { return; }
            throw new InvalidOperationException("Accepted: " + wire);
        }
    }

    [Test]
    public async Task InvalidUseIsRefusedBeforeDispatch()
    {
        var handler = new Handler((_, _) => throw new InvalidOperationException("dispatched"));
        using var native = Client(handler);
        var run = new RunId("run-1");
        static TargetHostedRunsDiscovery With(TargetHostedRunRoutes routes) => Capability with { RouteTemplates = routes };
        var refused = new (TargetDiscoveryDocument Discovery, TargetControlCredentials Credentials, RunId Run)[]
        {
            // Direct and private targets do not serve hosted lifecycle; credentials must match hosted OAuth.
            (Discovery(Capability, TargetAuthentication.None), Hosted, run),
            (Discovery(Capability, TargetAuthentication.PrivateCapability), new(TargetAuthentication.PrivateCapability, Bearer), run),
            (Discovery(Capability), new(TargetAuthentication.PrivateCapability, Bearer), run),
            (Discovery(), Hosted, run),
            (Discovery(Capability with { Kind = "zeroshot.hosted-runs/v2" }), Hosted, run),
            (Discovery(Capability with { BaseUrl = "https://attacker.example/api" }), Hosted, run),
            (Discovery(With(Routes with { Status = "/native-v2/runs" })), Hosted, run),
            (Discovery(With(Routes with { List = "/native-v2/runs/{run_id}" })), Hosted, run),
            (Discovery(With(Routes with { Watch = "/native-v2/runs/{run_id}/watch" })), Hosted, run),
            (Discovery(With(Routes with { Logs = "/native-v2/runs/{run_id}/logs{?execution}" })), Hosted, run),
            (Discovery(With(Routes with { Force = "/native-v2/../runs/{run_id}/force" })), Hosted, run),
            (Discovery(With(Routes with { Force = "/runs/{run_id}/{run_id}" })), Hosted, run),
            // Native's path-segment setter would skip or rewrite these run IDs.
            (Discovery(Capability), Hosted, new(".")), (Discovery(Capability), Hosted, new("..")), (Discovery(Capability), Hosted, new("a\tb"))
        };
        foreach (var (discovery, credentials, id) in refused)
        {
            // List names no run, so only the discovery and credential rows apply to it.
            if (id == run) await Refused(() => native.HostedRuns.ListAsync(discovery, credentials));
            await Refused(() => native.HostedRuns.StatusAsync(discovery, id, credentials));
            await Refused(() => native.HostedRuns.WatchAsync(discovery, new() { RunId = id }, credentials));
            await Refused(() => native.HostedRuns.LogsAsync(discovery, new() { RunId = id }, credentials));
            await Refused(() => native.HostedRuns.ForceAsync(discovery, id, credentials));
        }
        Check(handler.Calls == 0);

        static async Task Refused(Func<Task> call)
        {
            try { await call(); }
            catch (ArgumentException) { return; }
            throw new InvalidOperationException("Expected refusal before dispatch.");
        }
    }

    [Test]
    public async Task StreamsFollowNativeFramingAndKeepEachCloseReason()
    {
        // CRLF and LF line ends, a byte-at-a-time peer, and bytes after the closed frame that are never read.
        var body = Watch(1) + "\r\n" + Watch(2, Stopping) + "\n" + Closed("done") + "\r\n" + "not json\n";
        var (events, completion, delivered) = await ObserveWatch(body, bytewise: true);
        Check(events is [{ Status: QueuedHostedRunStatus }, { Status: TargetHostedRunStatus { Status: StoppingRunStatus } }]);
        Check(completion is { Origin: NativeSubscriptionOrigin.ServerClosed, Failure: null, ServerClose: null } && delivered == "cloud:2");

        foreach (var (reason, kind) in new[] { ("SLOW_CONSUMER", NativeSubscriptionFailureKind.SlowConsumer), ("SOURCE_UNAVAILABLE", NativeSubscriptionFailureKind.SourceUnavailable) })
        {
            (events, completion, delivered) = await ObserveWatch(Watch(1) + "\n" + Closed(reason) + "\n");
            Check(events.Count == 1 && completion is { Origin: NativeSubscriptionOrigin.ServerClosed } && completion.Failure?.Kind == kind &&
                delivered == "cloud:1", reason);
        }
    }

    [Test]
    public async Task MalformedForeignOrTruncatedStreamsFailAfterDrainingValidRecords()
    {
        var cases = new (string Bad, NativeSubscriptionFailureKind Kind, NativeSubscriptionOrigin Origin)[]
        {
            ("\n", NativeSubscriptionFailureKind.Protocol, NativeSubscriptionOrigin.LocalFailure),
            ("\r\n", NativeSubscriptionFailureKind.Protocol, NativeSubscriptionOrigin.LocalFailure),
            ("""{"type":"progress","event":{}}""" + "\n", NativeSubscriptionFailureKind.Protocol, NativeSubscriptionOrigin.LocalFailure),
            (Closed("done")[..^1] + ""","event":null}""" + "\n", NativeSubscriptionFailureKind.Protocol, NativeSubscriptionOrigin.LocalFailure),
            (Watch(2, extra: Recovery) + "\n", NativeSubscriptionFailureKind.Protocol, NativeSubscriptionOrigin.LocalFailure),
            (Watch(2, runId: "run-2") + "\n", NativeSubscriptionFailureKind.Protocol, NativeSubscriptionOrigin.LocalFailure),
            (Watch(2, subscription: "sub-2") + "\n", NativeSubscriptionFailureKind.Protocol, NativeSubscriptionOrigin.LocalFailure),
            (Watch(2, source: Source.Replace("main", "other")) + "\n", NativeSubscriptionFailureKind.Protocol, NativeSubscriptionOrigin.LocalFailure),
            // EOF inside a frame, and EOF with no closed frame, are disconnects rather than completion.
            (Watch(2)[..20], NativeSubscriptionFailureKind.UnexpectedDisconnect, NativeSubscriptionOrigin.UnexpectedDisconnect),
            ("", NativeSubscriptionFailureKind.UnexpectedDisconnect, NativeSubscriptionOrigin.UnexpectedDisconnect)
        };
        foreach (var (bad, kind, origin) in cases)
        {
            var (events, completion, delivered) = await ObserveWatch(Watch(1) + "\n" + bad);
            Check(events.Count == 1 && completion.Origin == origin && completion.Failure?.Kind == kind && delivered == "cloud:1", bad);
        }
        var invalidUtf8 = await ObserveWatch(Watch(1) + "\n", tail: [.. "{\"type\":\"closed\",\"reason\":\""u8, 0xFF, .. "\"}\n"u8]);
        Check(invalidUtf8.Events.Count == 1 && invalidUtf8.Completion.Failure?.Kind == NativeSubscriptionFailureKind.Protocol);

        // The execution filter admits only that execution's records.
        var feed = new FeedStream();
        using var native = Client(new Handler((request, _) => Task.FromResult(Stream(request, feed))));
        await using var logs = await native.HostedRuns.LogsAsync(Discovery(Capability),
            new() { RunId = new(Run), Execution = new("worker:1") }, Hosted);
        feed.Write(Encoding.UTF8.GetBytes(Log(1) + "\n" + Log(2, execution: "worker:2") + "\n"));
        Check((await logs.Completion).Failure?.Kind == NativeSubscriptionFailureKind.Protocol);
        var received = 0;
        try { await foreach (var _ in logs.ReadAllAsync()) received++; }
        catch (NativeSubscriptionException) { }
        Check(received == 1 && logs.LastDeliveredCursor?.Value == "cloud:1");
    }

    [Test]
    public async Task FramesAreBoundedBySixtyFourKibibytesOrTheSmallerConfiguredLimit()
    {
        // JSON whitespace pads a valid frame to an exact size, measured without its line end.
        static string Padded(int bytes) { var frame = Watch(1); return frame[..^1] + new string(' ', bytes - Encoding.UTF8.GetByteCount(frame)) + "}"; }
        foreach (var (ceiling, frame, passes) in new (int?, string, bool)[]
        {
            (null, Padded(64 * 1024), true), (null, Padded(64 * 1024 + 1), false), (4096, Padded(4096), true), (4096, Padded(4097), false)
        })
        {
            var (events, completion, _) = await ObserveWatch(frame + "\r\n" + Closed("done") + "\n",
                transport: ceiling is { } limit ? new() { MaxOecpMessageBytes = limit } : null);
            Check(passes ? events.Count == 1 && completion.Origin == NativeSubscriptionOrigin.ServerClosed
                : completion is { Origin: NativeSubscriptionOrigin.LocalFailure, Failure.Kind: NativeSubscriptionFailureKind.SizeLimit },
                $"{ceiling} {Encoding.UTF8.GetByteCount(frame)}");
        }
    }

    [Test]
    public async Task OverflowEndsTheStreamExplicitlyAfterQueuedRecordsDrain()
    {
        var feed = new FeedStream();
        using var native = Client(new Handler((request, _) => Task.FromResult(Stream(request, feed))), new() { MaxQueuedObservationRecords = 2 });
        await using var watch = await native.HostedRuns.WatchAsync(Discovery(Capability), new() { RunId = new(Run) }, Hosted);
        feed.Write(Encoding.UTF8.GetBytes(Watch(1) + "\n" + Watch(2) + "\n" + Watch(3) + "\n"));
        Check((await watch.Completion) is { Origin: NativeSubscriptionOrigin.LocalFailure, Failure.Kind: NativeSubscriptionFailureKind.RecordLimit } && feed.Disposed);
        var cursors = new List<string>();
        try { await foreach (var record in watch.ReadAllAsync()) cursors.Add(record.Cursor.Value); }
        catch (NativeSubscriptionException error) { Check(error.Kind == NativeSubscriptionFailureKind.RecordLimit); }
        Check(cursors.SequenceEqual(["cloud:1", "cloud:2"]) && watch.LastDeliveredCursor?.Value == "cloud:2");
    }

    [Test]
    public async Task ForceAttemptsKeepUncertaintyAndReadsKeepIdentity()
    {
        var run = new RunId(Run);
        var problem = (string code) => $$"""{"code":"{{code}}","message":"refused"}""";
        foreach (var (reply, outcome) in new (Func<HttpRequestMessage, HttpResponseMessage>, NativeAttemptOutcome)[]
        {
            (r => Reply(r, problem("not_found"), HttpStatusCode.NotFound), NativeAttemptOutcome.Rejected),
            (r => Reply(r, problem("target.unavailable"), HttpStatusCode.ServiceUnavailable), NativeAttemptOutcome.Unknown),
            (r => Reply(r, problem("request.conflict"), HttpStatusCode.Conflict), NativeAttemptOutcome.Unknown),
            (r => Reply(r, "{\"runId\":"), NativeAttemptOutcome.Unknown),
            (r => Reply(r, Status(Finished, runId: "run-2")), NativeAttemptOutcome.Unknown),
            (_ => throw new HttpRequestException("reset"), NativeAttemptOutcome.Unknown)
        })
        {
            using var native = Client(new Handler((request, _) => Task.FromResult(reply(request))));
            var attempt = await native.HostedRuns.ForceAsync(Discovery(Capability), run, Hosted);
            Check(attempt.Outcome == outcome && attempt.Response is null && attempt.Failure is not null &&
                !attempt.ToString().Contains(Bearer), outcome.ToString());
        }

        var handler = new Handler((request, _) => Task.FromResult(Reply(request, Status(Finished))));
        using (var native = Client(handler))
        {
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Check((await native.HostedRuns.ForceAsync(Discovery(Capability), run, Hosted, cancelled.Token)).Outcome == NativeAttemptOutcome.NotSent &&
                handler.Calls == 0);
        }

        // A status for another run is foreign data; a refused stream is an HTTP failure before any record.
        using var reads = Client(new Handler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/watch")
            ? Reply(request, problem("unauthorized"), HttpStatusCode.Unauthorized) : Reply(request, Status(Queued, runId: "run-2")))));
        Check((await Expect(reads.HostedRuns.StatusAsync(Discovery(Capability), run, Hosted))).Kind == NativeHttpFailureKind.Protocol);
        Check((await Expect(reads.HostedRuns.WatchAsync(Discovery(Capability), new() { RunId = run }, Hosted)))
            is { Kind: NativeHttpFailureKind.HttpStatus, StatusCode: HttpStatusCode.Unauthorized });
    }

    private static async Task<(List<HostedRunWatchEventNotification> Events, NativeSubscriptionCompletion Completion, string? Delivered)>
        ObserveWatch(string body, bool bytewise = false, byte[]? tail = null, TransportOptions? transport = null)
    {
        var feed = new FeedStream();
        using var native = Client(new Handler((request, _) => Task.FromResult(Stream(request, feed))), transport);
        await using var watch = await native.HostedRuns.WatchAsync(Discovery(Capability), new() { RunId = new(Run) }, Hosted);
        byte[] bytes = [.. Encoding.UTF8.GetBytes(body), .. tail ?? []];
        if (bytewise) foreach (var b in bytes) feed.Write([b]);
        else feed.Write(bytes);
        feed.End();
        var events = new List<HostedRunWatchEventNotification>();
        try { await foreach (var record in watch.ReadAllAsync()) events.Add(record); }
        catch (NativeSubscriptionException error) { Check(!error.ToString().Contains("cloud:"), "Cursor in diagnostics."); }
        return (events, await watch.Completion, watch.LastDeliveredCursor?.Value);
    }

    private static async Task<NativeHttpException> Expect(Task task)
    {
        try { await task; }
        catch (NativeHttpException error) { return error; }
        throw new InvalidOperationException("Expected HTTP failure.");
    }
}
