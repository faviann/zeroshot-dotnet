using System.Collections.Immutable;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using TUnit.Core;
using Zeroshot.Native;
using Zeroshot.Native.Contracts;

namespace Zeroshot.Client.Tests;

public sealed class ConnectionResolverTests
{
    private const string Bearer = "RESOLVER-BEARER-CANARY";
    private const string Secret = "RESOLVED-SECRET-CANARY";
    private const string Endpoint = "https://resolver.example/host/resolve";
    private static TargetConnectionResolver Resolver(string endpoint = Endpoint, string bearer = Bearer,
        ImmutableArray<ConnectionKey>? keys = null, ConnectionKey? source = null) => new()
    {
        Endpoint = endpoint, BearerToken = bearer, Keys = keys ?? [new("github"), new("registry")], SourceConnection = source
    };
    private static readonly ConnectionResolveRequest Request = new()
    {
        RunId = new("0195af77-1000-7000-8000-000000000001"),
        Connections = ImmutableDictionary<string, ImmutableArray<EnvironmentVariableName>>.Empty.Add("github", [new("GH_TOKEN")])
    };
    private static readonly string RequestJson =
        """{"runId":"0195af77-1000-7000-8000-000000000001","connections":{"github":["GH_TOKEN"]}}""";
    private static readonly string ResultJson = "{\"connections\":{\"github\":{\"GH_TOKEN\":\"" + Secret + "\"}}}";
    private static HttpResponseMessage Reply(HttpRequestMessage request, string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { RequestMessage = request, Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static void Check(bool value, string message = "Resolver assertion failed.")
    { if (!value) throw new InvalidOperationException(message); }

    private static async Task<ConnectionResolutionException> Failure(Task task, ConnectionResolutionError error)
    {
        try { await task; }
        catch (ConnectionResolutionException failure)
        {
            Check(failure.Error == error, $"{failure.Error} {failure.InnerException}");
            Check(!failure.ToString().Contains(Bearer) && !failure.ToString().Contains(Secret));
            return failure;
        }
        throw new InvalidOperationException("Expected a resolution failure.");
    }

    [Test]
    public async Task ResolvesOnceWithTheResolverBearerAndAcceptsAnySuccessStatus()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            calls++;
            Check(request.Method == HttpMethod.Post && request.RequestUri!.AbsoluteUri == Endpoint);
            Check(request.Headers.Authorization!.ToString() == "Bearer " + Bearer && request.Headers.CacheControl is null);
            Check(request.Content!.Headers.ContentType!.MediaType == "application/json");
            Check(await request.Content.ReadAsStringAsync(token) == RequestJson);
            return Reply(request, ResultJson, HttpStatusCode.Created);
        }));
        await using var client = ConnectionResolverClient.ForHttp(Resolver(), httpClient: http);
        var result = await client.ResolveAsync(Request);
        Check(calls == 1 && result.Connections["github"]["GH_TOKEN"] == Secret);
        Check(!client.ToString().Contains(Bearer) && !result.ToString().Contains(Secret));
        // The client borrows a supplied HttpClient by default and leaves no credential on it.
        Check(http.DefaultRequestHeaders.Authorization is null);
    }

    [Test]
    public async Task InvalidAuthorityOrUndeclaredKeysThrowBeforeDispatch()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler((request, _) => { calls++; return Task.FromResult(Reply(request, ResultJson)); }));
        var invalid = new List<TargetConnectionResolver>
        {
            // One case per native rule, then the narrower canonical-spelling and ASCII-bearer rules.
            Resolver(endpoint: "http://resolver.example/resolve"),
            Resolver(endpoint: "https://"),
            Resolver(endpoint: "https://user@resolver.example/resolve"),
            Resolver(endpoint: "https://resolver.example/resolve?run=1"),
            Resolver(endpoint: "https://resolver.example/resolve#fragment"),
            Resolver(endpoint: "https://resolver.example/a/../resolve"),
            Resolver(bearer: ""),
            Resolver(bearer: "line\nbreak"),
            Resolver(bearer: new string('x', 16 * 1024 + 1)),
            Resolver(bearer: "tokén"),
            Resolver(keys: []),
            Resolver(keys: [new("github"), new("github")]),
            Resolver(source: new("outside"))
        };
        foreach (var resolver in invalid)
        {
            try { ConnectionResolverClient.ForHttp(resolver, httpClient: http).Dispose(); }
            catch (ArgumentException error) { Check(!error.ToString().Contains(Bearer)); continue; }
            throw new InvalidOperationException("Expected an invalid resolver authority.");
        }
        // Declared keys with a matching source connection are accepted; the request is still scoped to them.
        await using var client = ConnectionResolverClient.ForHttp(Resolver(source: new("github")), httpClient: http);
        var outside = Request with { Connections = Request.Connections.Add("other", [new("TOKEN")]) };
        try { await client.ResolveAsync(outside); throw new InvalidOperationException("Expected an undeclared key refusal."); }
        catch (ArgumentException) { }
        Check(calls == 0);
    }

    [Test]
    public async Task StatusesMapToNativeCategoriesIgnoringProblemBodies()
    {
        foreach (var (status, expected) in new[]
        {
            (HttpStatusCode.Unauthorized, ConnectionResolutionError.Refused),
            (HttpStatusCode.Forbidden, ConnectionResolutionError.Refused),
            (HttpStatusCode.RequestTimeout, ConnectionResolutionError.Unavailable),
            (HttpStatusCode.TooManyRequests, ConnectionResolutionError.Unavailable),
            (HttpStatusCode.ServiceUnavailable, ConnectionResolutionError.Unavailable),
            ((HttpStatusCode)599, ConnectionResolutionError.Unavailable),
            ((HttpStatusCode)600, ConnectionResolutionError.InvalidResponse),
            (HttpStatusCode.Found, ConnectionResolutionError.InvalidResponse),
            (HttpStatusCode.NotFound, ConnectionResolutionError.InvalidResponse)
        })
        {
            // A valid target problem saying "unauthorized" does not change the status-derived category.
            var problem = $$"""{"code":"request.unauthorized","message":"{{Secret}}"}""";
            using var http = new HttpClient(new Handler((request, _) => Task.FromResult(Reply(request, problem, status))));
            await using var client = ConnectionResolverClient.ForHttp(Resolver(), httpClient: http);
            var failure = await Failure(client.ResolveAsync(Request), expected);
            Check(failure.StatusCode == status);
        }
    }

    [Test]
    public async Task MalformedOversizedAndInterruptedResponsesKeepTheirCategory()
    {
        var padded = ResultJson + new string(' ', 300 * 1024 - ResultJson.Length);
        await using (var bounded = Client((request, _) => Task.FromResult(Reply(request, padded))))
            Check((await bounded.ResolveAsync(Request)).Connections.Count == 1);

        foreach (var (body, kind) in new[]
        {
            ("not json", NativeHttpFailureKind.Protocol),
            ("""{"connections":{},"extra":1}""", NativeHttpFailureKind.Protocol),
            ("""{"connections":{"github":{"GH_TOKEN":""}}}""", NativeHttpFailureKind.Protocol),
            (padded + " ", NativeHttpFailureKind.SizeLimit)
        })
        {
            await using var client = Client((request, _) => Task.FromResult(Reply(request, body)));
            var failure = await Failure(client.ResolveAsync(Request), ConnectionResolutionError.InvalidResponse);
            Check(((NativeHttpException)failure.InnerException!).Kind == kind);
        }

        // Native rejects a declared oversized body before reading it, so a stalled body is not Unavailable.
        await using (var declared = Client((request, _) =>
        {
            var content = new StreamContent(new StalledStream());
            content.Headers.ContentLength = 300 * 1024 + 1;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = content });
        }, new TransportOptions { RequestTimeout = TimeSpan.FromSeconds(5) }))
            Check(((NativeHttpException)(await Failure(declared.ResolveAsync(Request), ConnectionResolutionError.InvalidResponse))
                .InnerException!).Kind == NativeHttpFailureKind.SizeLimit);

        await using (var truncated = Client((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { RequestMessage = request, Content = new StreamContent(new TruncatedStream()) })))
            Check(((NativeHttpException)(await Failure(truncated.ResolveAsync(Request), ConnectionResolutionError.Unavailable))
                .InnerException!).Kind == NativeHttpFailureKind.Transport);
    }

    [Test]
    public async Task DeadlineIsUnavailableAndCancellationIsNotAResolutionFailure()
    {
        static async Task<HttpResponseMessage> Stall(HttpRequestMessage _, CancellationToken token)
        { await Task.Delay(Timeout.Infinite, token); throw new InvalidOperationException(); }

        await using (var slow = Client(Stall, new TransportOptions { RequestTimeout = TimeSpan.FromMilliseconds(100) }))
            Check(((NativeHttpException)(await Failure(slow.ResolveAsync(Request), ConnectionResolutionError.Unavailable))
                .InnerException!).Kind == NativeHttpFailureKind.Deadline);

        // Native's fixed 30 s bound caps a longer configured request timeout.
        using var capped = new HttpClient(new Handler(Stall)) { Timeout = TimeSpan.FromSeconds(30) };
        await using var client = ConnectionResolverClient.ForHttp(Resolver(),
            new TransportOptions { RequestTimeout = TimeSpan.FromMinutes(5) }, capped);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        try { await client.ResolveAsync(Request, cancel.Token); throw new InvalidOperationException("Expected cancellation."); }
        catch (OperationCanceledException) { }
    }

    [Test]
    public async Task OffTargetHttpsResolverReceivesOnlyItsOwnBearer()
    {
        using var certificate = Certificate();
        using var resolver = new TlsPeer(certificate);
        var endpoint = $"https://127.0.0.1:{resolver.Port}/host/resolve";
        var served = resolver.ServeOnceAsync($"HTTP/1.1 200 OK\r\nContent-Length: {ResultJson.Length}\r\nConnection: close\r\n\r\n{ResultJson}");
        using var http = TrustingClient(certificate);
        await using var client = ConnectionResolverClient.ForHttp(Resolver(endpoint), httpClient: http);
        var result = await client.ResolveAsync(Request);
        var (line, headers, body) = await served;
        Check(line == "POST /host/resolve HTTP/1.1" && body == RequestJson);
        Check(headers.Count(h => h.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase)) == 1 &&
            headers.Contains("Authorization: Bearer " + Bearer) && headers.Contains($"Host: 127.0.0.1:{resolver.Port}"));
        Check(result.Connections["github"]["GH_TOKEN"] == Secret && client.Endpoint.AbsoluteUri == endpoint);
    }

    [Test]
    public async Task RedirectIsRefusedWithoutContactingItsLocation()
    {
        using var certificate = Certificate();
        using var resolver = new TlsPeer(certificate);
        using var elsewhere = new TlsPeer(certificate);
        var location = $"https://127.0.0.1:{elsewhere.Port}/stolen";
        var served = resolver.ServeOnceAsync($"HTTP/1.1 307 Temporary Redirect\r\nLocation: {location}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        using var http = TrustingClient(certificate);
        await using var client = ConnectionResolverClient.ForHttp(Resolver($"https://127.0.0.1:{resolver.Port}/host/resolve"), httpClient: http);
        var failure = await Failure(client.ResolveAsync(Request), ConnectionResolutionError.InvalidResponse);
        await served;
        Check(failure.StatusCode == HttpStatusCode.TemporaryRedirect &&
            ((NativeHttpException)failure.InnerException!).Kind == NativeHttpFailureKind.Redirect);
        Check(!elsewhere.Pending);
    }

    [Test]
    public async Task RedirectFollowedByASuppliedClientIsStillInvalidResponse()
    {
        using var certificate = Certificate();
        using var resolver = new TlsPeer(certificate);
        using var elsewhere = new TlsPeer(certificate);
        var redirected = resolver.ServeOnceAsync("HTTP/1.1 307 Temporary Redirect\r\n" +
            $"Location: https://127.0.0.1:{elsewhere.Port}/elsewhere\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        var refused = elsewhere.ServeOnceAsync("HTTP/1.1 401 Unauthorized\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        // A caller client that breaks the documented contract by following redirects.
        using var http = TrustingClient(certificate, followRedirects: true);
        await using var client = ConnectionResolverClient.ForHttp(Resolver($"https://127.0.0.1:{resolver.Port}/host/resolve"), httpClient: http);
        var failure = await Failure(client.ResolveAsync(Request), ConnectionResolutionError.InvalidResponse);
        await redirected;
        var (_, headers, _) = await refused;
        Check(((NativeHttpException)failure.InnerException!).Kind == NativeHttpFailureKind.Redirect);
        Check(!headers.Any(h => h.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase)));
    }

    private static ConnectionResolverClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send,
        TransportOptions? transport = null)
        => ConnectionResolverClient.ForHttp(Resolver(), transport, new HttpClient(new Handler(send)));

    private static X509Certificate2 Certificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=resolver-tests", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(10));
        // Windows TLS (SChannel) cannot serve an ephemeral private key; a PKCS#12 round trip gives it a usable one.
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pkcs12), null);
    }

    private static HttpClient TrustingClient(X509Certificate2 certificate, bool followRedirects = false)
    {
        var handler = NativeClient.CreateHttpHandler();
        handler.AllowAutoRedirect = followRedirects;
        handler.SslOptions.CertificateChainPolicy = new X509ChainPolicy
        {
            TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck
        };
        handler.SslOptions.CertificateChainPolicy.CustomTrustStore.Add(certificate);
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private sealed class TlsPeer : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly X509Certificate2 certificate;
        public TlsPeer(X509Certificate2 certificate) { this.certificate = certificate; listener.Start(); }
        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
        public bool Pending => listener.Pending();

        public async Task<(string Line, List<string> Headers, string Body)> ServeOnceAsync(string response)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var socket = await listener.AcceptTcpClientAsync(timeout.Token);
            using var tls = new SslStream(socket.GetStream());
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, timeout.Token);
            using var reader = new StreamReader(tls, Encoding.ASCII, leaveOpen: true);
            var line = await reader.ReadLineAsync(timeout.Token) ?? "";
            var headers = new List<string>();
            while (await reader.ReadLineAsync(timeout.Token) is { Length: > 0 } header) headers.Add(header);
            var length = int.Parse(headers.Single(h => h.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))[15..]);
            var body = new char[length];
            await reader.ReadBlockAsync(body, timeout.Token);
            await tls.WriteAsync(Encoding.ASCII.GetBytes(response), timeout.Token);
            return (line, headers, new string(body));
        }

        public void Dispose() => listener.Stop();
    }

    // A body that ends before its declared content, like a dropped connection.
    private sealed class TruncatedStream : MemoryStream
    {
        private bool sent;
        public TruncatedStream() : base(Encoding.ASCII.GetBytes("{\"connections\":")) { }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (sent) throw new IOException("The response ended prematurely.");
            sent = true;
            return base.ReadAsync(buffer, cancellationToken);
        }
    }

    private sealed class StalledStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
    }
}
