using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using TUnit.Core;
using Zeroshot.Native;
using Zeroshot.Native.Contracts;

namespace Zeroshot.Client.Tests;

public sealed class DashboardHistoryTests
{
    private const string Run = "018f5e78-7f95-7c22-8d98-3f15af20c991";
    private static readonly Uri Origin = new("http://127.0.0.1:4173/");
    private static readonly JsonNode Golden = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/history.json")))!;

    // The golden page restricted to (from, to] with native's cursor/completeness fields for that slice.
    private static string Page(int from, int to, int head = 9)
    {
        var page = Golden["page"]!.DeepClone();
        bool Within(JsonNode? entry) => int.Parse(entry!["cursor"]!.GetValue<string>()[3..]) is var at && at > from && at <= to;
        foreach (var name in new[] { "events", "control" })
            page[name] = new JsonArray(page[name]!.AsArray().Where(Within).Select(entry => entry!.DeepClone()).ToArray());
        page["nextCursor"] = $"v2:{to}";
        page["headCursor"] = $"v2:{head}";
        page["complete"] = to == head;
        return page.ToJsonString();
    }

    private static string History(int from, int to, string? id = null, string newline = "\n")
        => $"event: history{newline}id: {id ?? $"v2:{to}"}{newline}data: {Page(from, to)}{newline}{newline}";

    private static HttpResponseMessage Reply(HttpRequestMessage request, HttpStatusCode status, string body = "", string contentType = "application/json")
    {
        var reply = new HttpResponseMessage(status) { RequestMessage = request, Content = new StringContent(body) };
        reply.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        return reply;
    }

    private static HttpResponseMessage Stream(HttpRequestMessage request, FeedStream feed)
    {
        var reply = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StreamContent(feed) };
        reply.Content.Headers.ContentType = new("text/event-stream");
        return reply;
    }

    private static NativeClient Client(Handler handler, TransportOptions? transport = null)
        => NativeClient.ForHttp(new() { Origin = Origin, Transport = transport ?? new() }, new HttpClient(handler), ownsHttpClient: true);

    [Test]
    public async Task EveryRunRouteSendsOneBrowserRequestAndAppliesNativeHistoryRules()
    {
        var run = new RunId(Run);
        var definition = Golden["definition"]!.ToJsonString();
        var list = Golden["list"]!.ToJsonString();
        foreach (var (method, target, call, body, verify) in new (HttpMethod, string, Func<NativeDashboardClient, Task<object>>, string, Func<object, bool>)[]
        {
            (HttpMethod.Get, "/ui/api/runs", async d => await d.ListRunsAsync(), list, x => x is RunHistoryList { Runs.Length: 2 }),
            (HttpMethod.Head, "/ui/api/runs?after=" + Run, async d => await d.HeadRunsAsync(run), "", x => x is NativeHeadResult { StatusCode: HttpStatusCode.OK }),
            (HttpMethod.Get, "/ui/api/runs/" + Run, async d => await d.GetRunAsync(run), definition, x => x is RunDefinition { RunId.Value: Run }),
            (HttpMethod.Head, "/ui/api/runs/" + Run, async d => await d.HeadRunAsync(run), "", x => x is NativeHeadResult),
            (HttpMethod.Get, $"/ui/api/runs/{Run}/history?after=v2%3A4", async d => await d.GetHistoryAsync(run, new Cursor("v2:4")), Page(4, 9),
                x => x is HistoryPage { Events.Length: 5, Complete: true }),
            (HttpMethod.Head, $"/ui/api/runs/{Run}/history", async d => await d.HeadHistoryAsync(run), "", x => x is NativeHeadResult),
            (HttpMethod.Head, $"/ui/api/runs/{Run}/events?after=v2%3A2", async d => await d.HeadRunEventsAsync(run, new Cursor("v2:2")), "",
                x => x is NativeHeadResult { MediaType: "text/event-stream" })
        })
        {
            var handler = new Handler((request, _) =>
            {
                Check(request.Method == method && request.RequestUri!.PathAndQuery == target, $"{method} {request.RequestUri}");
                Check(request.Headers.ConnectionClose == true && !request.Headers.Contains("Origin") && !request.Headers.Contains("Sec-Fetch-Site"));
                return Task.FromResult(Reply(request, HttpStatusCode.OK, body, target.Contains("/events") ? "text/event-stream" : "application/json"));
            });
            using var native = Client(handler);
            Check(verify(await call(native.Dashboard)) && handler.Calls == 1, $"{method} {target}");
        }

        // The #35 host checks apply to each JSON route: a page that does not continue its requested cursor is rejected.
        foreach (var call in new Func<NativeDashboardClient, Task>[]
        {
            d => d.ListRunsAsync(new RunId(Golden["list"]!["runs"]![1]!["runId"]!.GetValue<string>())),
            d => d.GetRunAsync(new RunId("0195af77-1000-7000-8000-000000000001")),
            d => d.GetHistoryAsync(run, new Cursor("v2:2"))
        })
        {
            using var native = Client(new Handler((request, _) => Task.FromResult(Reply(request, HttpStatusCode.OK,
                request.RequestUri!.AbsolutePath.EndsWith("/history") ? Page(4, 9) : request.RequestUri.AbsolutePath == "/ui/api/runs" ? list : definition))));
            Check((await Expect(call(native.Dashboard))).Kind == NativeHttpFailureKind.Protocol);
        }
    }

    [Test]
    public async Task RefusalsAndInvalidArgumentsKeepTheirMeaning()
    {
        using var refused = Client(new Handler((request, _) => Task.FromResult(Reply(request, HttpStatusCode.NotFound,
            """{"code":"run_not_found","message":"No retained run."}"""))));
        foreach (var call in new Func<NativeDashboardClient, Task>[]
        {
            d => d.GetRunAsync(new RunId(Run)), d => d.OpenRunEventsAsync(new RunId(Run))
        })
        {
            var error = await Expect(call(refused.Dashboard));
            Check(error is { Kind: NativeHttpFailureKind.HttpStatus, StatusCode: HttpStatusCode.NotFound, HistoryProblem: RunHistoryProblemCode.RunNotFound } &&
                error.UiProblem?.Code == "run_not_found" && !error.ToString().Contains("No retained run"));
        }

        var handler = new Handler((request, _) => throw new InvalidOperationException("dispatched"));
        using var native = Client(handler);
        foreach (var call in new Action[]
        {
            () => native.Dashboard.ListRunsAsync(new RunId("not-a-run")),
            () => native.Dashboard.GetHistoryAsync(new RunId(Run), new Cursor("v2:+1")),
            () => native.Dashboard.OpenRunEventsAsync(new RunId(Run), lastEventId: new Cursor("v2:01"))
        })
        {
            try { call(); throw new InvalidOperationException("Expected argument refusal."); }
            catch (ArgumentException) { }
        }
        Check(handler.Calls == 0);
    }

    [Test]
    public async Task EventsFollowLastEventIdAndSurviveArbitraryFraming()
    {
        // BOM, keepalive comments, CR, LF and CRLF line ends, multi-line data, an idle head page and a trailing error.
        var page = Page(3, 6);
        var stream = "﻿:\n\n" + History(3, 6, newline: "\r\n") + ":\r\r" +
            "event: history\rid: v2:9\rdata: " + Page(6, 9).Replace(",\"headCursor\"", "\ndata: ,\"headCursor\"") + "\r\r" +
            History(9, 9) + "event: history_error\ndata: {\"code\":\"runtime_unavailable\",\"message\":\"gone\"}\n\n";
        var feed = new FeedStream();
        HttpRequestMessage? sent = null;
        using var native = Client(new Handler((request, _) => { sent = request; return Task.FromResult(Stream(request, feed)); }));
        await using var events = await native.Dashboard.OpenRunEventsAsync(new RunId(Run), new Cursor("v2:0"), new Cursor("v2:3"));
        Check(sent!.RequestUri!.PathAndQuery == $"/ui/api/runs/{Run}/events?after=v2%3A0" &&
            sent.Headers.GetValues("Last-Event-ID").Single() == "v2:3" && sent.Headers.Accept.ToString() == "text/event-stream");
        foreach (var b in Encoding.UTF8.GetBytes(stream)) feed.Write([b]);
        feed.End();
        var received = new List<DashboardRunEvent>();
        await foreach (var entry in events.ReadAllAsync()) received.Add(entry);
        Check(received is [DashboardHistoryPageEvent { Page.Events.Length: 3 } first, DashboardHistoryPageEvent { Page.Events.Length: 3 },
            DashboardHistoryPageEvent { Page.Events.Length: 0, Page.Complete: true },
            DashboardHistoryErrorEvent { Code: RunHistoryProblemCode.RuntimeUnavailable, Problem.Message: "gone" } error] &&
            first.Page.Events[0].Cursor.Value == "v2:4" && error.ToString() == nameof(DashboardHistoryErrorEvent));
        Check(events.LastDeliveredCursor?.Value == "v2:9" && (await events.Completion) is { Origin: NativeSubscriptionOrigin.ServerClosed, Failure: null });
    }

    [Test]
    public async Task MalformedGappedOrForeignEventsFailAfterDrainingValidOnes()
    {
        foreach (var (bad, kind, origin) in new (string, NativeSubscriptionFailureKind, NativeSubscriptionOrigin)[]
        {
            (History(4, 6), NativeSubscriptionFailureKind.Protocol, NativeSubscriptionOrigin.LocalFailure),         // gap after v2:3
            (History(2, 6), NativeSubscriptionFailureKind.Protocol, NativeSubscriptionOrigin.LocalFailure),         // rewind
            (History(3, 6, id: "v2:5"), NativeSubscriptionFailureKind.Protocol, NativeSubscriptionOrigin.LocalFailure), // foreign id
            (History(3, 6).Replace("id: v2:6\n", ""), NativeSubscriptionFailureKind.Protocol, NativeSubscriptionOrigin.LocalFailure),
            ("event: progress\ndata: {}\n\n", NativeSubscriptionFailureKind.Protocol, NativeSubscriptionOrigin.LocalFailure),
            ("data: {}\n\n", NativeSubscriptionFailureKind.Protocol, NativeSubscriptionOrigin.LocalFailure),
            ("event: history\nid: v2:6\ndata: {\"events\":\n\n", NativeSubscriptionFailureKind.Protocol, NativeSubscriptionOrigin.LocalFailure),
            ("event: history_error\ndata: {\"code\":1}\n\n", NativeSubscriptionFailureKind.Protocol, NativeSubscriptionOrigin.LocalFailure),
            ("event: history\nid: v2:6\ndata: {", NativeSubscriptionFailureKind.UnexpectedDisconnect, NativeSubscriptionOrigin.UnexpectedDisconnect)
        })
        {
            var feed = new FeedStream();
            using var native = Client(new Handler((request, _) => Task.FromResult(Stream(request, feed))));
            await using var events = await native.Dashboard.OpenRunEventsAsync(new RunId(Run));
            feed.Write(Encoding.UTF8.GetBytes(History(0, 3) + bad));
            feed.End();
            var delivered = 0;
            try { await foreach (var _ in events.ReadAllAsync()) delivered++; throw new InvalidOperationException(bad); }
            catch (NativeSubscriptionException error) { Check(error.Kind == kind && !error.ToString().Contains("v2:"), bad); }
            Check(delivered == 1 && events.LastDeliveredCursor?.Value == "v2:3" && (await events.Completion).Origin == origin, bad);
        }

        var broken = new FeedStream();
        using var client = Client(new Handler((request, _) => Task.FromResult(Stream(request, broken))));
        await using var disconnected = await client.Dashboard.OpenRunEventsAsync(new RunId(Run));
        broken.Fail();
        Check((await disconnected.Completion).Failure?.Kind == NativeSubscriptionFailureKind.UnexpectedDisconnect);
    }

    [Test]
    public async Task EventCeilingBoundsDataWhileFramingHasASmallAllowance()
    {
        // A page exactly at the message limit passes with its framing; one data byte over fails, and so does
        // a non-data run (here comment lines without a dispatch) beyond the fixed framing allowance.
        var data = Encoding.UTF8.GetByteCount(Page(0, 3));
        var flood = string.Concat(Enumerable.Repeat(":\n", 600));
        foreach (var (ceiling, stream, delivered) in new[] { (data, History(0, 3), true), (data - 1, History(0, 3), false), (data, flood + History(0, 3), false) })
        {
            var feed = new FeedStream();
            using var native = Client(new Handler((request, _) => Task.FromResult(Stream(request, feed))), new() { MaxOecpMessageBytes = ceiling });
            await using var events = await native.Dashboard.OpenRunEventsAsync(new RunId(Run));
            feed.Write(Encoding.UTF8.GetBytes(stream));
            feed.End();
            var completion = await events.Completion;
            Check(delivered ? completion.Origin == NativeSubscriptionOrigin.ServerClosed
                : completion is { Origin: NativeSubscriptionOrigin.LocalFailure, Failure.Kind: NativeSubscriptionFailureKind.SizeLimit }, $"{ceiling} {delivered}");
        }
    }

    [Test]
    public async Task SlowConsumerOverflowIsExplicitAfterQueuedPagesDrain()
    {
        var feed = new FeedStream();
        using var native = Client(new Handler((request, _) => Task.FromResult(Stream(request, feed))), new() { MaxQueuedObservationRecords = 2 });
        await using var events = await native.Dashboard.OpenRunEventsAsync(new RunId(Run));
        feed.Write(Encoding.UTF8.GetBytes(History(0, 3) + History(3, 6) + History(6, 9)));
        var completion = await events.Completion;
        Check(completion is { Origin: NativeSubscriptionOrigin.LocalFailure, Failure.Kind: NativeSubscriptionFailureKind.RecordLimit } && feed.Disposed);
        var cursors = new List<string>();
        try { await foreach (var entry in events.ReadAllAsync()) cursors.Add(((DashboardHistoryPageEvent)entry).Page.NextCursor.Value); }
        catch (NativeSubscriptionException error) { Check(error.Kind == NativeSubscriptionFailureKind.RecordLimit); }
        Check(cursors.SequenceEqual(["v2:3", "v2:6"]) && events.LastDeliveredCursor?.Value == "v2:6");
    }

    [Test]
    public async Task DisposalAndCancellationCloseOnlyThisObservation()
    {
        var feeds = new List<FeedStream>();
        var list = Golden["list"]!.ToJsonString();
        using var native = Client(new Handler((request, _) =>
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/events")) return Task.FromResult(Reply(request, HttpStatusCode.OK, list));
            var feed = new FeedStream();
            lock (feeds) feeds.Add(feed);
            return Task.FromResult(Stream(request, feed));
        }));
        var disposed = await native.Dashboard.OpenRunEventsAsync(new RunId(Run));
        using var cancel = new CancellationTokenSource();
        var cancelled = await native.Dashboard.OpenRunEventsAsync(new RunId(Run), cancellationToken: cancel.Token);
        var survivor = await native.Dashboard.OpenRunEventsAsync(new RunId(Run));

        await disposed.DisposeAsync();
        cancel.Cancel();
        Check((await disposed.Completion).Origin == NativeSubscriptionOrigin.Disposed && feeds[0].Disposed);
        Check((await cancelled.Completion).Origin == NativeSubscriptionOrigin.Cancelled && feeds[1].Disposed);
        Check(!feeds[2].Disposed && !survivor.Completion.IsCompleted && (await native.Dashboard.ListRunsAsync()).Runs.Length == 2);
        feeds[2].Write(Encoding.UTF8.GetBytes(History(0, 3)));
        var reader = survivor.ReadAllAsync().GetAsyncEnumerator();
        Check(await reader.MoveNextAsync() && reader.Current is DashboardHistoryPageEvent);

        native.Dispose();
        Check((await survivor.Completion).Origin == NativeSubscriptionOrigin.Disposed && feeds[2].Disposed);
        await reader.DisposeAsync();
    }

    [Test]
    public async Task OpeningFailuresReleaseTheResponseAndAdmissionPrecedesDispatch()
    {
        var feed = new FeedStream();
        using (var json = Client(new Handler((request, _) =>
        {
            var reply = Stream(request, feed);
            reply.Content.Headers.ContentType = new("application/json");
            return Task.FromResult(reply);
        })))
            Check((await Expect(json.Dashboard.OpenRunEventsAsync(new RunId(Run)))).Kind == NativeHttpFailureKind.Protocol && feed.Disposed);

        var handler = new Handler((request, _) => Task.FromResult(Stream(request, new FeedStream())));
        using var native = Client(handler, new() { MaxConcurrentSubscriptions = 1 });
        await using var open = await native.Dashboard.OpenRunEventsAsync(new RunId(Run));
        try { await native.Dashboard.OpenRunEventsAsync(new RunId(Run)); throw new InvalidOperationException("Expected admission failure."); }
        catch (NativeSubscriptionException error) { Check(error.Kind == NativeSubscriptionFailureKind.Admission); }
        Check(handler.Calls == 1);
    }

    private static async Task<NativeHttpException> Expect(Task task)
    {
        try { await task; }
        catch (NativeHttpException error) { return error; }
        throw new InvalidOperationException("Expected HTTP failure.");
    }
}
