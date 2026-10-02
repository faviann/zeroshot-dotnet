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
    private static TargetDiscoveryDocument Discovery()
        => TestDiscovery.Controller(TargetAuthentication.PrivateCapability) with { PrivateBootstrapPath = "/native-v2/private-bootstrap" };
    private static HttpResponseMessage Reply(HttpRequestMessage request, HttpStatusCode status, string? body = null) => new(status)
    {
        RequestMessage = request,
        Content = body is null ? new ByteArrayContent([]) : new StringContent(body, Encoding.UTF8, "application/json")
    };

    // Wire, discovery gate and refusal pairs: CapabilityConformanceTests.
    [Test]
    public async Task AnExactEmpty204AcknowledgesTheEnvelope()
    {
        using var handler = new Handler((request, _) => Task.FromResult(Reply(request, HttpStatusCode.NoContent)));
        using var client = ClientFor(handler);
        var attempt = await client.Private.BootstrapAsync(Discovery(), Envelope);
        Check(attempt.Outcome == NativeAttemptOutcome.Acknowledged && attempt.Response is not null && attempt.Failure is null);
        Check(attempt.Operation == "private.bootstrap" && handler.Calls == 1);
    }

    [Test]
    public async Task ContactWithoutAnExactEmpty204LeavesTheEffectUnknown()
    {
        foreach (var reply in new Func<HttpRequestMessage, HttpResponseMessage>[]
        {
            request => Reply(request, HttpStatusCode.OK),
            request => Reply(request, HttpStatusCode.NoContent, "{}")
        })
        {
            using var handler = new Handler((request, _) => Task.FromResult(reply(request)));
            using var client = ClientFor(handler);
            var attempt = await client.Private.BootstrapAsync(Discovery(), Envelope);
            Check(attempt.Outcome == NativeAttemptOutcome.Unknown && attempt.Failure is NativeHttpException && handler.Calls == 1);
        }
    }

    [Test]
    public async Task InvalidEnvelopesAreNeverSent()
    {
        using var handler = new Handler((request, _) => Task.FromResult(Reply(request, HttpStatusCode.NoContent)));
        using var client = ClientFor(handler);
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
