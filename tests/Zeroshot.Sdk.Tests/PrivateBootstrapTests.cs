using System.Net;
using System.Text;
using System.Text.Json;
using TUnit.Core;
using Zeroshot.Native;
using Zeroshot.Native.Contracts;

namespace Zeroshot.Client.Tests;

public sealed class PrivateBootstrapTests
{
    private static readonly string Nonce = new('a', 24);
    private static readonly string Ciphertext = string.Concat(Enumerable.Repeat("0123456789abcdef", 10));
    private static readonly TargetPrivateBootstrapRequest Envelope = new() { Nonce = Nonce, Ciphertext = Ciphertext };
    private static TargetDiscoveryDocument Discovery(TargetAuthentication authentication = TargetAuthentication.PrivateCapability,
        string? path = "/native-v2/private-bootstrap")
        => TestDiscovery.Controller(authentication) with { PrivateBootstrapPath = path };
    private static NativeClient Client(Handler handler) => NativeClient.ForHttp(
        new NativeClientOptions { Origin = new Uri("https://target.example/") }, new HttpClient(handler), ownsHttpClient: true);
    private static HttpResponseMessage Reply(HttpRequestMessage request, HttpStatusCode status, string? body = null) => new(status)
    {
        RequestMessage = request,
        Content = body is null ? new ByteArrayContent([]) : new StringContent(body, Encoding.UTF8, "application/json")
    };
    private static void Check(bool value, string message = "Private bootstrap assertion failed.")
    { if (!value) throw new InvalidOperationException(message); }

    [Test]
    public async Task PostsTheExactEnvelopeOnceWithoutCredentialsAndAcknowledgesEmpty204()
    {
        HttpRequestMessage? sent = null;
        string? body = null;
        using var handler = new Handler(async (request, token) =>
        {
            (sent, body) = (request, await request.Content!.ReadAsStringAsync(token));
            return Reply(request, HttpStatusCode.NoContent);
        });
        using var client = Client(handler);
        var attempt = await client.Private.BootstrapAsync(Discovery(), Envelope);
        Check(sent!.Method == HttpMethod.Post && sent.RequestUri!.AbsoluteUri == "https://target.example/native-v2/private-bootstrap");
        Check(sent.Headers.Authorization is null && sent.Content!.Headers.ContentType!.MediaType == "application/json");
        Check(body == $$"""{"nonce":"{{Nonce}}","ciphertext":"{{Ciphertext}}"}""");
        Check(attempt.Outcome == NativeAttemptOutcome.Acknowledged && attempt.Response is not null && attempt.Failure is null);
        Check(attempt.Operation == "private.bootstrap" && handler.Calls == 1);
    }

    [Test]
    public async Task OnlyNativePreEffectRefusalsReject()
    {
        foreach (var (status, code, rejected) in new[]
        {
            (400, "request.invalid", true), (404, "request.not_found", true), (408, "request.timeout", true),
            (401, "request.unauthorized", false), (503, "target.unavailable", false), (400, "run.rejected", false),
            (404, "request.invalid", false), (500, "target.http_error", false)
        })
        {
            using var handler = new Handler((request, _) => Task.FromResult(Reply(request, (HttpStatusCode)status,
                JsonSerializer.Serialize(new { code, message = "refused" }))));
            using var client = Client(handler);
            var attempt = await client.Private.BootstrapAsync(Discovery(), Envelope);
            Check(attempt.Outcome == (rejected ? NativeAttemptOutcome.Rejected : NativeAttemptOutcome.Unknown), $"{status} {code}");
            Check(attempt.Failure is NativeHttpException { Problem: { } problem } http && http.StatusCode == (HttpStatusCode)status && problem.Code == code);
            Check(attempt.Response is null && handler.Calls == 1);
        }
    }

    [Test]
    public async Task ContactWithoutAnExactEmpty204LeavesTheEffectUnknown()
    {
        foreach (var reply in new Func<HttpRequestMessage, HttpResponseMessage>[]
        {
            request => Reply(request, HttpStatusCode.OK),
            request => Reply(request, HttpStatusCode.NoContent, "{}"),
            request => Reply(request, HttpStatusCode.NotFound, "not json"),
            _ => throw new HttpRequestException("lost reply")
        })
        {
            using var handler = new Handler((request, _) => Task.FromResult(reply(request)));
            using var client = Client(handler);
            var attempt = await client.Private.BootstrapAsync(Discovery(), Envelope);
            Check(attempt.Outcome == NativeAttemptOutcome.Unknown && attempt.Failure is NativeHttpException && handler.Calls == 1);
        }
    }

    [Test]
    public async Task WrongModeDiscoveryAndInvalidEnvelopesAreNeverSent()
    {
        using var handler = new Handler((request, _) => Task.FromResult(Reply(request, HttpStatusCode.NoContent)));
        using var client = Client(handler);
        foreach (var discovery in new[]
        {
            Discovery(TargetAuthentication.None, path: null), Discovery(TargetAuthentication.HostedOauth, path: null),
            Discovery(path: null), Discovery(path: "https://other.example/native-v2/private-bootstrap")
        })
            await Invalid<ArgumentException>(() => client.Private.BootstrapAsync(discovery, Envelope));
        foreach (var envelope in new[]
        {
            Envelope with { Nonce = Nonce.ToUpperInvariant() }, Envelope with { Nonce = Nonce[..22] },
            Envelope with { Nonce = Nonce + "a" }, Envelope with { Ciphertext = Ciphertext[..^2] },
            Envelope with { Ciphertext = Ciphertext[..^1] + "g" }, Envelope with { Ciphertext = Ciphertext + "00" }
        })
            await Invalid<JsonException>(() => client.Private.BootstrapAsync(Discovery(), envelope));
        Check(handler.Calls == 0);
    }

    private static async Task Invalid<TException>(Func<Task> action) where TException : Exception
    {
        try { await action(); }
        catch (Exception error) { Check(error.GetType() == typeof(TException), error.ToString()); return; }
        throw new InvalidOperationException("Expected invalid caller input.");
    }
}
