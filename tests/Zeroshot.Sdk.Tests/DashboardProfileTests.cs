using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TUnit.Core;
using Zeroshot.Native;
using Zeroshot.Native.Contracts;

namespace Zeroshot.Client.Tests;

public sealed class DashboardProfileTests
{
    private static readonly Uri Origin = new("http://127.0.0.1:4173/");
    private const string Workspace = "0199aa00-0000-7000-8000-000000000001";
    private static readonly string Graph = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/dashboard-bootstrap.json")))
        .RootElement.GetProperty("templates")[0].GetProperty("graph").GetRawText();
    private const string Runtime = """{"harness":"codex","provider":"openai","size":"small","nodes":{"work":{"kind":"agent","model":"gpt-5.6-sol"}}}""";
    private static readonly string Profile =
        $$"""{"id":"0199aa00-0000-7000-8000-000000000002","name":"review","scope":"user","graph":{{Graph}},"runtime":{{Runtime}},"isDefault":false}""";
    private static readonly string Envelope = $$"""{"profile":{{Profile}},"revision":"rev-1"}""";

    private static DashboardProfileSaveRequest Save(string? expectedRevision = null) => new()
    {
        Name = new("review"),
        Graph = NativeJson.DeserializeUtf8<GraphSpec>(Encoding.UTF8.GetBytes(Graph)),
        Runtime = NativeJson.DeserializeUtf8<RuntimePlan>(Encoding.UTF8.GetBytes(Runtime)),
        ExpectedRevision = expectedRevision
    };

    private static void Check(bool value, string message = "Dashboard profile assertion failed.")
    { if (!value) throw new InvalidOperationException(message); }

    private static HttpResponseMessage Reply(HttpRequestMessage request, HttpStatusCode status, string body = "", string? contentType = "application/json")
    {
        var reply = new HttpResponseMessage(status) { RequestMessage = request, Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)) };
        if (contentType is not null) reply.Content.Headers.ContentType = new(contentType);
        return reply;
    }

    private static (NativeClient Native, Handler Handler) Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send,
        TransportOptions? transport = null, TimeProvider? time = null)
    {
        var handler = new Handler(send);
        return (NativeClient.ForHttp(new NativeClientOptions { Origin = Origin, Transport = transport ?? new(), Time = time ?? TimeProvider.System },
            new HttpClient(handler), ownsHttpClient: true), handler);
    }

    [Test]
    public async Task ProfileRoutesSendExactBrowserRequestsAndPreserveTheNativeRevision()
    {
        var create = JsonNode.Parse($$"""{"name":"review","graph":{{Graph}},"runtime":{{Runtime}}}""");
        var update = JsonNode.Parse($$"""{"name":"review","graph":{{Graph}},"runtime":{{Runtime}},"expectedRevision":"opaque rev/1"}""");
        foreach (var (method, path, call, reply, expectedBody, verify) in new (HttpMethod, string, Func<NativeDashboardClient, Task<object>>, string, JsonNode?, Func<object, bool>)[]
        {
            (HttpMethod.Get, "/ui/api/profiles", async d => await d.ListProfilesAsync(),
                """{"profiles":[{"id":"p-1","name":"review","scope":"user","isDefault":false}]}""", null,
                x => x is RunProfileListResult { Profiles: [{ Name.Value: "review", Scope: RunProfileScope.User }] }),
            (HttpMethod.Head, "/ui/api/profiles", async d => await d.HeadProfilesAsync(), "", null,
                x => x is NativeHeadResult { StatusCode: HttpStatusCode.OK, MediaType: "application/json" }),
            (HttpMethod.Get, "/ui/api/profiles/review", async d => await d.GetProfileAsync(new("review")), Envelope, null,
                x => x is DashboardProfile { Revision: "rev-1", Profile: { Name.Value: "review", Scope: RunProfileScope.User, IsDefault: false } }),
            (HttpMethod.Head, "/ui/api/profiles/review", async d => await d.HeadProfileAsync(new("review")), "", null,
                x => x is NativeHeadResult { StatusCode: HttpStatusCode.OK }),
            // A new name omits expectedRevision; an update sends the last read revision verbatim, uninterpreted.
            (HttpMethod.Post, "/ui/api/profiles", async d => await d.SaveProfileAsync(Save(), Workspace), Envelope, create,
                x => x is NativeAttempt<DashboardProfile> { Outcome: NativeAttemptOutcome.Acknowledged, Operation: "dashboard.saveProfile",
                    Response.Revision: "rev-1", Failure: null } a && a.Origin == Origin && a.CorrelationId != Guid.Empty),
            (HttpMethod.Post, "/ui/api/profiles", async d => await d.SaveProfileAsync(Save("opaque rev/1"), Workspace),
                Envelope.Replace("rev-1", "rev-2"), update,
                x => x is NativeAttempt<DashboardProfile> { Outcome: NativeAttemptOutcome.Acknowledged, Response.Revision: "rev-2" })
        })
        {
            var (native, handler) = Client(async (request, _) =>
            {
                Check(request.Method == method && request.RequestUri!.AbsolutePath == path && request.RequestUri.Query == "", $"{method} {request.RequestUri}");
                Check(request.Headers.ConnectionClose == true && request.Headers.Authorization is null &&
                    !request.Headers.Contains("Origin") && !request.Headers.Contains("Sec-Fetch-Site"));
                if (expectedBody is null)
                    Check(request.Content is null && !request.Headers.Contains("X-Zeroshot-Workspace"));
                else
                {
                    Check(request.Headers.GetValues("X-Zeroshot-Workspace").SequenceEqual([Workspace]), "Exactly one verbatim workspace header.");
                    Check(request.Content!.Headers.ContentType!.MediaType == "application/json");
                    Check(JsonNode.DeepEquals(JsonNode.Parse(await request.Content.ReadAsStringAsync()), expectedBody), "Unexpected save body.");
                }
                return Reply(request, HttpStatusCode.OK, reply);
            });
            using (native)
            {
                var result = await call(native.Dashboard);
                Check(verify(result) && handler.Calls == 1, $"{method} {path}");
            }
        }
    }

    [Test]
    public async Task OnlyNativePreWriteRefusalsRejectASave()
    {
        foreach (var (status, code, rejected) in new[]
        {
            // workspace_changed is also native's answer to a matching header after the store was replaced.
            (409, "workspace_changed", true), (409, "profile_conflict", true), (422, "invalid_profile", true),
            (403, "origin_rejected", true), (415, "json_required", true), (503, "server_stopping", true),
            // A store error can follow the write; a code under another status or a non-UI refusal proves nothing.
            (500, "profile_store_error", false), (409, "invalid_profile", false), (404, "request.not_found", false)
        })
        {
            var (native, handler) = Client((request, _) => Task.FromResult(Reply(request, (HttpStatusCode)status,
                JsonSerializer.Serialize(new { code, message = "refused" }))));
            using (native)
            {
                var attempt = await native.Dashboard.SaveProfileAsync(Save("rev-1"), Workspace);
                Check(attempt.Outcome == (rejected ? NativeAttemptOutcome.Rejected : NativeAttemptOutcome.Unknown) && attempt.Response is null &&
                    attempt.Failure is NativeHttpException { Kind: NativeHttpFailureKind.HttpStatus, UiProblem: { } problem } http &&
                    problem.Code == code && http.StatusCode == (HttpStatusCode)status && handler.Calls == 1, $"{status} {code}");
            }
        }
    }

    [Test]
    public async Task ALostOrUnreadableReplyLeavesTheSaveUnknownAndIsNeverResent()
    {
        // The unanswered save's deadline runs on a manual clock that expires once the request is in flight.
        var time = new ManualTime();
        foreach (var (send, kind) in new (Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>, NativeHttpFailureKind)[]
        {
            ((_, _) => throw new HttpRequestException("connection reset"), NativeHttpFailureKind.Transport),
            ((request, _) => Task.FromResult(Reply(request, HttpStatusCode.OK, """{"profile":null,"revision":"rev-1"}""")), NativeHttpFailureKind.Protocol),
            (async (_, token) =>
            {
                time.Advance(new TransportOptions().RequestTimeout);
                await Task.Delay(Timeout.Infinite, token);
                throw new InvalidOperationException();
            }, NativeHttpFailureKind.Deadline)
        })
        {
            var (native, handler) = Client(send, time: time);
            using (native)
            {
                var attempt = await native.Dashboard.SaveProfileAsync(Save("rev-1"), Workspace);
                Check(attempt.Outcome == NativeAttemptOutcome.Unknown && attempt.Response is null &&
                    attempt.Failure is NativeHttpException failure && failure.Kind == kind && handler.Calls == 1, kind.ToString());
            }
        }
        var (idle, unsent) = Client((_, _) => throw new InvalidOperationException("Dispatched."));
        using (idle)
        {
            var attempt = await idle.Dashboard.SaveProfileAsync(Save(), Workspace, new CancellationToken(canceled: true));
            Check(attempt.Outcome == NativeAttemptOutcome.NotSent && attempt.Failure is OperationCanceledException && unsent.Calls == 0);
        }
    }
}
