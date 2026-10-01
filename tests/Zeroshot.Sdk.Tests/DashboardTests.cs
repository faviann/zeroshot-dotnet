using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TUnit.Core;
using Zeroshot.Native;
using Zeroshot.Native.Contracts;

namespace Zeroshot.Client.Tests;

public sealed class DashboardTests
{
    private static readonly Uri Origin = new("http://127.0.0.1:4173/");
    private static readonly string Bootstrap = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/dashboard-bootstrap.json"));
    private static readonly string Graph = JsonDocument.Parse(Bootstrap).RootElement.GetProperty("templates")[0].GetProperty("graph").GetRawText();
    private static readonly GraphSpec Spec = NativeJson.DeserializeUtf8<GraphSpec>(Encoding.UTF8.GetBytes(Graph));
    private static readonly RuntimePlan Runtime = NativeJson.DeserializeUtf8<RuntimePlan>(
        """{"harness":"codex","provider":"openai","size":"small","nodes":{"work":{"kind":"agent","model":"gpt-5.6-sol"}}}"""u8);
    private static readonly JsonElement DraftRuntime = JsonDocument.Parse("""{"nodes":{"work":{"kind":"agent","model":""}}}""").RootElement.Clone();

    private static void Check(bool value, string message = "Dashboard assertion failed.")
    { if (!value) throw new InvalidOperationException(message); }

    private static HttpResponseMessage Reply(HttpRequestMessage request, HttpStatusCode status, string? body = null,
        string? contentType = null, string? location = null)
    {
        var reply = new HttpResponseMessage(status) { RequestMessage = request, Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body ?? "")) };
        if (contentType is not null) reply.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        if (location is not null) reply.Headers.TryAddWithoutValidation("Location", location);
        return reply;
    }

    private static (NativeClient Native, Handler Handler) Client(Func<HttpRequestMessage, HttpResponseMessage> reply)
    {
        var handler = new Handler((request, _) => Task.FromResult(reply(request)));
        return (NativeClient.ForHttp(new NativeClientOptions { Origin = Origin }, new HttpClient(handler), ownsHttpClient: true), handler);
    }

    private static readonly DashboardAuthoringRequest Authoring = new() { Graph = Spec, Runtime = DraftRuntime, Action = new ProtectAuthoringAction { Node = new("work") } };
    private static readonly DashboardDataRequest Data = new()
    {
        Graph = JsonDocument.Parse(Graph).RootElement.Clone(), Runtime = DraftRuntime,
        Action = new RunInputFieldDataAction { Name = new("title"), Type = new StringPayload(), Required = true }
    };

    [Test]
    public async Task EveryOperationSendsOneExactBrowserRequestAndMapsItsNativeResult()
    {
        var draft = "{\"graph\":" + Graph + ",\"runtime\":{\"nodes\":{}}}";
        foreach (var (method, path, call, respond, verify) in new (HttpMethod, string, Func<NativeDashboardClient, Task<object>>, Func<HttpRequestMessage, HttpResponseMessage>, Func<object, bool>)[]
        {
            (HttpMethod.Get, "/", async d => await d.GetRootAsync(), r => Reply(r, HttpStatusCode.TemporaryRedirect, location: "/ui/"),
                x => x is DashboardRedirect { StatusCode: HttpStatusCode.TemporaryRedirect, Location: "/ui/" }),
            (HttpMethod.Head, "/", async d => await d.HeadRootAsync(), r => Reply(r, HttpStatusCode.TemporaryRedirect, location: "/ui/"),
                x => x is DashboardRedirect { Location: "/ui/" }),
            (HttpMethod.Get, "/ui", async d => await d.GetUiAsync(), r => Reply(r, HttpStatusCode.TemporaryRedirect, location: "/ui/"),
                x => x is DashboardRedirect { Location: "/ui/" }),
            (HttpMethod.Head, "/ui", async d => await d.HeadUiAsync(), r => Reply(r, HttpStatusCode.TemporaryRedirect, location: "/ui/"),
                x => x is DashboardRedirect { Location: "/ui/" }),
            (HttpMethod.Get, "/ui/", async d => await d.GetIndexAsync(), r => Reply(r, HttpStatusCode.OK, "<!doctype html>", "text/html; charset=utf-8"),
                x => x is DashboardContent { MediaType: "text/html", CharSet: "utf-8" } c && Encoding.UTF8.GetString(c.ExportBody()) == "<!doctype html>"),
            (HttpMethod.Head, "/ui/", async d => await d.HeadIndexAsync(), r => Reply(r, HttpStatusCode.OK, contentType: "text/html; charset=utf-8"),
                x => x is NativeHeadResult { StatusCode: HttpStatusCode.OK, MediaType: "text/html" }),
            (HttpMethod.Get, "/ui/assets/app-1_a.js", async d => await d.GetAssetAsync("assets/app-1_a.js"), r => Reply(r, HttpStatusCode.OK, "export{}", "text/javascript; charset=utf-8"),
                x => x is DashboardContent { MediaType: "text/javascript", Length: 8 }),
            (HttpMethod.Head, "/ui/assets/app-1_a.js", async d => await d.HeadAssetAsync("assets/app-1_a.js"), r => Reply(r, HttpStatusCode.OK, contentType: "font/woff2"),
                x => x is NativeHeadResult { MediaType: "font/woff2" }),
            (HttpMethod.Get, "/ui/api/bootstrap", async d => await d.GetBootstrapAsync(), r => Reply(r, HttpStatusCode.OK, Bootstrap, "application/json"),
                x => x is DashboardBootstrap { Version: 1 } b && b.Workers.Length == 2),
            (HttpMethod.Head, "/ui/api/bootstrap", async d => await d.HeadBootstrapAsync(), r => Reply(r, HttpStatusCode.OK, contentType: "application/json"),
                x => x is NativeHeadResult { MediaType: "application/json" }),
            (HttpMethod.Post, "/ui/api/validate", async d => await d.ValidateAsync(new() { Graph = Spec, Runtime = Runtime }), r => Reply(r, HttpStatusCode.OK, """{"valid":true}"""),
                x => x is DashboardValidation { Valid: true }),
            (HttpMethod.Post, "/ui/api/authoring", async d => await d.AuthorAsync(Authoring), r => Reply(r, HttpStatusCode.OK, draft),
                x => x is DashboardAuthoringDraft { Graph.Root: SeqNode }),
            (HttpMethod.Post, "/ui/api/data", async d => await d.TransformDataAsync(Data), r => Reply(r, HttpStatusCode.OK, """{"graph":{"draft":true},"runtime":null}"""),
                x => x is DashboardDataDraft d && d.Graph.GetProperty("draft").GetBoolean() && d.Runtime.ValueKind == JsonValueKind.Null)
        })
        {
            var (native, handler) = Client(request =>
            {
                Check(request.Method == method && request.RequestUri!.GetLeftPart(UriPartial.Authority) + "/" == Origin.AbsoluteUri &&
                    request.RequestUri.AbsolutePath == path, $"{method} {request.RequestUri}");
                // Host comes from the configured origin; no browser-origin header is claimed.
                Check(request.Headers.Host is null && !request.Headers.Contains("Origin") && !request.Headers.Contains("Sec-Fetch-Site"));
                Check(request.Headers.ConnectionClose == true && request.Headers.Authorization is null);
                Check(method == HttpMethod.Post ? request.Content!.Headers.ContentType!.MediaType == "application/json" : request.Content is null);
                return respond(request);
            });
            using (native)
            {
                var result = await call(native.Dashboard);
                Check(verify(result) && handler.Calls == 1, $"{method} {path}");
            }
        }
    }

    [Test]
    public async Task BrowserRefusalsAndUnexpectedShapesStayDistinct()
    {
        foreach (var (call, respond, kind, status, code) in new (Func<NativeDashboardClient, Task>, Func<HttpRequestMessage, HttpResponseMessage>, NativeHttpFailureKind, HttpStatusCode?, string?)[]
        {
            (d => d.GetAssetAsync("missing.js"), r => Reply(r, HttpStatusCode.NotFound), NativeHttpFailureKind.HttpStatus, HttpStatusCode.NotFound, null),
            (d => d.HeadAssetAsync("missing.js"), r => Reply(r, HttpStatusCode.NotFound), NativeHttpFailureKind.HttpStatus, HttpStatusCode.NotFound, null),
            (d => d.GetBootstrapAsync(), r => Reply(r, HttpStatusCode.Forbidden, """{"code":"origin_rejected","message":"Open the UI using its configured public URL."}"""),
                NativeHttpFailureKind.HttpStatus, HttpStatusCode.Forbidden, "origin_rejected"),
            // Native strips HEAD bodies, so a HEAD refusal carries only its status.
            (d => d.HeadRootAsync(), r => Reply(r, HttpStatusCode.Forbidden), NativeHttpFailureKind.HttpStatus, HttpStatusCode.Forbidden, null),
            (d => d.GetIndexAsync(), r => Reply(r, HttpStatusCode.TemporaryRedirect, location: "/elsewhere"), NativeHttpFailureKind.Redirect, HttpStatusCode.TemporaryRedirect, null),
            (d => d.GetBootstrapAsync(), r => Reply(r, HttpStatusCode.Found, location: "/login"), NativeHttpFailureKind.Redirect, HttpStatusCode.Found, null),
            (d => d.GetRootAsync(), r => Reply(r, HttpStatusCode.OK, "<!doctype html>", "text/html"), NativeHttpFailureKind.Protocol, HttpStatusCode.OK, null),
            (d => d.GetUiAsync(), r => Reply(r, HttpStatusCode.TemporaryRedirect), NativeHttpFailureKind.Protocol, HttpStatusCode.TemporaryRedirect, null),
            (d => d.GetAssetAsync("a.bin"), r => Reply(r, HttpStatusCode.OK, "x"), NativeHttpFailureKind.Protocol, HttpStatusCode.OK, null),
            (d => d.GetIndexAsync(), r => Reply(r, HttpStatusCode.NoContent, contentType: "text/html"), NativeHttpFailureKind.Protocol, HttpStatusCode.NoContent, null),
            (d => d.GetBootstrapAsync(), r => Reply(r, HttpStatusCode.OK, Bootstrap.Replace("\"target\"", "\"cloud\"")), NativeHttpFailureKind.Protocol, HttpStatusCode.OK, null)
        })
        {
            var (native, handler) = Client(respond);
            using (native)
            {
                try { await call(native.Dashboard); }
                catch (NativeHttpException error)
                {
                    Check(error.Kind == kind && error.StatusCode == status && error.UiProblem?.Code == code && error.Problem is null && handler.Calls == 1, error.ToString());
                    continue;
                }
                throw new InvalidOperationException($"Expected {kind}.");
            }
        }
    }

    [Test]
    public async Task AssetPathsAndTheNativeDraftBodyLimitAreCheckedBeforeDispatch()
    {
        var (native, handler) = Client(request => throw new InvalidOperationException("Dispatched."));
        using (native)
        {
            foreach (var path in new[] { "", "/assets/a.js", "assets//a.js", "../index.html", "assets/./a.js", "a%2e.js", "a?b", "a#b", "a\\b", "a b", new string('a', 2048) })
            {
                try { await native.Dashboard.GetAssetAsync(path); }
                catch (ArgumentException) { continue; }
                throw new InvalidOperationException($"Accepted asset path {path}.");
            }
            // Native's UI router takes at most 2 MiB even though the shared HTTP request ceiling is 4 MiB.
            var large = new DashboardDataRequest
            {
                Graph = JsonDocument.Parse(JsonSerializer.Serialize(new string('x', 2 * 1024 * 1024))).RootElement.Clone(),
                Runtime = DraftRuntime, Action = new RemoveRunInputDataAction { Name = new("title") }
            };
            try { await native.Dashboard.TransformDataAsync(large); throw new InvalidOperationException("Sent an oversized draft."); }
            catch (NativeHttpException error) { Check(error.Kind == NativeHttpFailureKind.SizeLimit); }
            Check(handler.Calls == 0);
        }
    }
}
