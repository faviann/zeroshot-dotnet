using System.Net;
using System.Net.Http.Headers;
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

public sealed class HttpSessionTests
{
    private const string Run = "0195af77-1000-7000-8000-000000000001";
    private const string Control = "CONTROL-CREDENTIAL-CANARY";
    private const string Session = "SESSION-CREDENTIAL-CANARY";
    private static TargetDiscoveryDocument Discovery(TargetAuthentication authentication = TargetAuthentication.None)
        => TestDiscovery.Controller(authentication);
    private static NativeClientOptions Options(TransportOptions? transport = null) => new()
    { Origin = new Uri("https://target.example/"), Transport = transport ?? new() };
    private static void Check(bool value, string message = "Session assertion failed.")
    { if (!value) throw new InvalidOperationException(message); }
    private static async Task Invalid(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) when (error is ArgumentException or JsonException)
        { Check(!error.ToString().Contains(Control) && !error.ToString().Contains(Session)); return; }
        throw new InvalidOperationException("Expected invalid caller input.");
    }
    private static async Task<NativeHttpException> Failure(Task task, NativeHttpFailureKind kind)
    {
        try { await task; }
        catch (NativeHttpException error)
        {
            Check(error.Kind == kind, error.ToString());
            Check(!error.ToString().Contains(Control) && !error.ToString().Contains(Session));
            return error;
        }
        throw new InvalidOperationException("Expected native failure.");
    }
    private static HttpResponseMessage Reply(HttpRequestMessage request, string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { RequestMessage = request, Content = new StringContent(body) };
    private static string Result(string endpoint = "wss://target.example/native-v2/oecp", string? token = null)
        => Encoding.UTF8.GetString(NativeJson.SerializeUtf8(new TargetOecpSession { Endpoint = endpoint, BearerToken = token }));

    [Test]
    public async Task ExactSessionRequestAndCurrentAuthorityAreSeparatedFromReceivedSession()
    {
        foreach (var authentication in Enum.GetValues<TargetAuthentication>())
        {
            var expectedBearer = authentication == TargetAuthentication.None ? null : Control;
            var issuedBearer = authentication == TargetAuthentication.None ? null :
                authentication == TargetAuthentication.PrivateCapability ? Control : Session;
            var handlerBodyWithRun = false;
            using var handler = new Handler(async (request, _) =>
            {
                if (request.Method == HttpMethod.Get)
                {
                    Check(request.Headers.Authorization is null);
                    return Reply(request, Encoding.UTF8.GetString(NativeJson.SerializeUtf8(Discovery(authentication))));
                }
                Check(request.Method == HttpMethod.Post && request.RequestUri!.AbsoluteUri == "https://target.example/native-v2/oecp-session");
                Check(request.Headers.Authorization?.Scheme == (expectedBearer is null ? null : "Bearer"));
                Check(request.Headers.Authorization?.Parameter == expectedBearer);
                Check(request.Content!.Headers.ContentType!.MediaType == "application/json");
                var body = await request.Content.ReadAsStringAsync();
                Check(body == (handlerBodyWithRun ? $"{{\"runId\":\"{Run}\"}}" : "{}"));
                return Reply(request, Result(token: issuedBearer), HttpStatusCode.Created);
            });
            using var http = new HttpClient(handler);
            using var client = NativeClient.ForHttp(Options(), http);
            var credentials = expectedBearer is null ? null : new TargetControlCredentials(authentication, expectedBearer);
            handlerBodyWithRun = false;
            var discovered = await client.Target.DiscoverAsync();
            var result = await client.Target.CreateOecpSessionAsync(discovered, credentials: credentials);
            Check(result.Endpoint == "wss://target.example/native-v2/oecp" && result.BearerToken == issuedBearer);
            Check(!result.ToString().Contains(Control) && !result.ToString().Contains(Session));
            Check(credentials is null || !credentials.ToString().Contains(Control));
            handlerBodyWithRun = true;
            _ = await client.Target.CreateOecpSessionAsync(discovered, new() { RunId = new(Run) }, credentials);
            _ = await client.Target.DiscoverAsync();
            Check(handler.Calls == 4 && http.DefaultRequestHeaders.Authorization is null);
        }
    }

    [Test]
    public async Task WrongAuthorityAndMalformedDescriptorsRefuseBeforeSendingCredentials()
    {
        using var handler = new Handler((request, _) => Task.FromResult(Reply(request, Result(token: Session))));
        using var http = new HttpClient(handler);
        using var client = NativeClient.ForHttp(Options(), http);
        var credentials = new TargetControlCredentials(TargetAuthentication.HostedOauth, Control);
        foreach (var path in new[] { "https://foreign.example/", "//foreign.example/session", "/session?x=1", "/session#x", "/session/{run_id}", "/session{?run_id}", "/session\\x", "/../session", "/session/%", "/session/ space" })
        {
            var discovery = Discovery(TargetAuthentication.HostedOauth) with { SessionPath = path };
            await Invalid(() => client.Target.CreateOecpSessionAsync(discovery, credentials: credentials));
        }
        await Invalid(() => client.Target.CreateOecpSessionAsync(Discovery(), credentials: credentials));
        await Invalid(() => client.Target.CreateOecpSessionAsync(Discovery(TargetAuthentication.PrivateCapability), credentials: credentials));
        await Invalid(() => client.Target.CreateOecpSessionAsync(Discovery(TargetAuthentication.HostedOauth)));
        foreach (var id in new[] { "not-a-run", Run.ToUpperInvariant(), "0195af77-1000-4000-8000-000000000001", "0195af77-1000-7000-c000-000000000001" })
            await Invalid(() => client.Target.CreateOecpSessionAsync(Discovery(), new() { RunId = new(id) }));
        Check(handler.Calls == 0);
    }

    [Test]
    public async Task EscapedCanonicalSessionPathsKeepTheirReceivedSpelling()
    {
        using var handler = new Handler((request, _) =>
        {
            Check(request.RequestUri!.AbsoluteUri == "https://target.example/sessions/%41");
            Check(request.Headers.Authorization!.Parameter == Control);
            return Task.FromResult(Reply(request, Result(token: Session)));
        });
        using var http = new HttpClient(handler);
        using var client = NativeClient.ForHttp(Options(), http);
        _ = await client.Target.CreateOecpSessionAsync(
            Discovery(TargetAuthentication.HostedOauth) with { SessionPath = "/sessions/%41" },
            credentials: new(TargetAuthentication.HostedOauth, Control));
        Check(handler.Calls == 1);
    }

    [Test]
    public async Task BearerBoundariesAndDefaultHttpAuthorityCannotBypassScoping()
    {
        using var handler = new Handler((request, _) => Task.FromResult(Reply(request, Result())));
        using var http = new HttpClient(handler);
        foreach (var token in new[] { "", "a b", "a\r\nb", "é", new string('a', 16385) })
            await Invalid(() => Task.FromResult(new TargetControlCredentials(TargetAuthentication.HostedOauth, token)));
        _ = new TargetControlCredentials(TargetAuthentication.PrivateCapability, new string('a', 16384));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Control);
        await Invalid(() => Task.FromResult(NativeClient.ForHttp(Options(), http)));
        Check(handler.Calls == 0);
    }

    [Test]
    public async Task SessionAuthorityRequiresMatchingWssSchemeHostAndEffectivePort()
    {
        foreach (var endpoint in new[]
        {
            "ws://target.example/native-v2/oecp", "WSS://target.example/native-v2/oecp", "https://target.example/native-v2/oecp", "wss://foreign.example/native-v2/oecp",
            "wss://target.example:444/native-v2/oecp", "wss://user@target.example/native-v2/oecp", "wss://@target.example/native-v2/oecp",
            "wss://target.example/native-v2/oecp?", "wss://target.example/native-v2/oecp#", "/native-v2/oecp",
            "wss://target.example/native-v2/oe cp", "wss://target.example/\\foreign", "wss://target.example/%zz"
        })
        {
            using var handler = new Handler((request, _) => Task.FromResult(Reply(request, Result(endpoint, Session))));
            using var http = new HttpClient(handler);
            using var client = NativeClient.ForHttp(Options(), http);
            await Failure(client.Target.CreateOecpSessionAsync(Discovery(TargetAuthentication.HostedOauth),
                credentials: new(TargetAuthentication.HostedOauth, Control)), NativeHttpFailureKind.Protocol);
            Check(handler.Calls == 1); // No dial or credential resend after an invalid result.
        }
        foreach (var (origin, endpoint) in new[]
        {
            ("https://target.example", "wss://TARGET.example:443/host-owned/session"),
            ("https://target.example:8443", "wss://target.example:8443/another/path"),
            ("http://127.0.0.1:8080", "ws://127.0.0.1:8080/native-v2/oecp"),
            ("http://[::1]", "ws://[::1]:80/native-v2/oecp")
        })
        {
            using var handler = new Handler((request, _) => Task.FromResult(Reply(request, Result(endpoint))));
            using var http = new HttpClient(handler);
            using var client = NativeClient.ForHttp(Options() with { Origin = new(origin) }, http);
            Check((await client.Target.CreateOecpSessionAsync(Discovery())).Endpoint == endpoint);
        }
    }

    [Test]
    public async Task SessionShapeAndAuthenticationRefuseMalformedResults()
    {
        foreach (var (body, authentication) in new[]
        {
            ("{}", TargetAuthentication.None),
            ("{\"endpoint\":null}", TargetAuthentication.None),
            ("{\"endpoint\":\"wss://target.example/\",\"endpoint\":\"wss://target.example/\"}", TargetAuthentication.None),
            ("{\"endpoint\":\"wss://target.example/\",\"extra\":true}", TargetAuthentication.None),
            (Result(token: Session), TargetAuthentication.None),
            (Result(), TargetAuthentication.HostedOauth),
            (Result(token: "bad token"), TargetAuthentication.HostedOauth),
            (Result(token: new string('x', 16385)), TargetAuthentication.PrivateCapability)
        })
        {
            using var handler = new Handler((request, _) => Task.FromResult(Reply(request, body)));
            using var http = new HttpClient(handler);
            using var client = NativeClient.ForHttp(Options(), http);
            await Failure(client.Target.CreateOecpSessionAsync(Discovery(authentication), credentials:
                authentication == TargetAuthentication.None ? null : new(authentication, Control)), NativeHttpFailureKind.Protocol);
        }
        Check(NativeJson.DeserializeUtf8<TargetOecpSessionRequest>("{\"runId\":null}"u8).RunId is null);
        Check(NativeJson.DeserializeUtf8<TargetOecpSession>("{\"endpoint\":\"wss://target.example/\",\"bearerToken\":null}"u8).BearerToken is null);
    }

    [Test]
    public async Task RefusalsRetainStatusAndValidatedProblemWithoutImplicitRemoteText()
    {
        var body = $"{{\"code\":\"request.unauthorized\",\"message\":\"{Control}\",\"details\":{{\"fact\":\"{Session}\",\"httpStatus\":123}}}}";
        using var handler = new Handler((request, _) => Task.FromResult(Reply(request, body, HttpStatusCode.Forbidden)));
        using var http = new HttpClient(handler);
        using var client = NativeClient.ForHttp(Options(new() { CaptureRawDiagnostics = true }), http);
        var failure = await Failure(client.Target.CreateOecpSessionAsync(Discovery()), NativeHttpFailureKind.HttpStatus);
        Check(failure.StatusCode == HttpStatusCode.Forbidden && failure.Problem!.Code == "request.unauthorized");
        var problem = failure.Problem!;
        var details = problem.Details!.Value;
        Check(problem.Message == Control && details.GetProperty("fact").GetString() == Session);
        Check(details.GetProperty("httpStatus").GetInt32() == 123);
        Check(!problem.ToString().Contains(Control));
        Check(Encoding.UTF8.GetString(failure.ExportRawDiagnostic()!) == body && handler.Calls == 1);
        foreach (var badProblem in new[]
        {
            "{}", "{broken", "{\"code\":\"!\",\"message\":\"bad\"}",
            "{\"code\":\"valid\",\"message\":\"bad\\n\"}",
            "{\"code\":\"valid\",\"message\":\"bad\",\"details\":[]}",
            "{\"code\":\"valid\",\"message\":\"bad\",\"unexpected\":1}",
            JsonSerializer.Serialize(new { code = "valid", message = new string('é', 513) }),
            JsonSerializer.Serialize(new { code = "valid", message = "bad", details = new { content = new string('x', 61440) } })
        })
        {
            using var badHandler = new Handler((request, _) => Task.FromResult(Reply(request, badProblem, HttpStatusCode.Unauthorized)));
            using var badHttp = new HttpClient(badHandler);
            using var badClient = NativeClient.ForHttp(Options(), badHttp);
            var malformed = await Failure(badClient.Target.CreateOecpSessionAsync(Discovery()), NativeHttpFailureKind.HttpStatus);
            Check(malformed.StatusCode == HttpStatusCode.Unauthorized && malformed.Problem is null && malformed.ExportRawDiagnostic() is null);
        }
    }

    [Test]
    public async Task UnicodeRefusalDetailsUseNativeCompactUtf8Bounds()
    {
        foreach (var (count, suffix, valid) in new[] { (6000, "", true), (15357, "x", true), (15357, "xx", false) })
        {
            // {"text":""} is 11 native bytes; an emoji is four UTF-8 bytes.
            // The latter two cases are exactly 61440 and 61441 serialized detail bytes.
            var text = string.Concat(Enumerable.Repeat("😀", count)) + suffix;
            var body = "{\"code\":\"refused\",\"message\":\"request refused\",\"details\":{\"text\":\"" + text + "\"}}";
            using var handler = new Handler((request, _) => Task.FromResult(Reply(request, body, HttpStatusCode.Forbidden)));
            using var http = new HttpClient(handler);
            using var client = NativeClient.ForHttp(Options(), http);
            var failure = await Failure(client.Target.CreateOecpSessionAsync(Discovery()), NativeHttpFailureKind.HttpStatus);
            Check(failure.StatusCode == HttpStatusCode.Forbidden && (failure.Problem is not null) == valid);
            if (valid) Check(failure.Problem!.Details!.Value.GetProperty("text").GetString() == text);
            Check(!failure.ToString().Contains("😀"));
        }
    }

    [Test]
    public void DetailSizeUsesDecodedValuesIncludingKeysNestedStringsAndNumbers()
    {
        // Pinned serde_json 1.0.150 serializes this mixed object to 113 UTF-8 bytes.
        // Whitespace, escaped surrogate pairs and padded exponents are wire spellings,
        // not extra bytes in its reserialized Value. Pad to either side of 60 KiB.
        const string details = """{"😀":["\ud83d\ude00","\u2028","\u0001","\b","\"","\\", true,false,null,1.0,1e+000020,0.000001,-0,18446744073709551615],"pad":""}""";
        foreach (var extra in new[] { 0, 1 })
        {
            var padded = details.Replace("\"pad\":\"\"", "\"pad\":\"" + new string('x', 61440 - 113 + extra) + "\"");
            var bytes = Encoding.UTF8.GetBytes("{\"code\":\"refused\",\"message\":\"request refused\",\"details\":" + padded + "}");
            try
            {
                var problem = NativeJson.DeserializeUtf8<TargetHttpProblem>(bytes);
                Check(extra == 0);
                // NativeJson's explicit export may use .NET escaping; validation still
                // measures the decoded native value on the subsequent round trip.
                _ = NativeJson.DeserializeUtf8<TargetHttpProblem>(NativeJson.SerializeUtf8(problem));
            }
            catch (JsonException) when (extra == 1) { }
        }
    }

    [Test]
    public async Task SessionAcquisitionRemainsAvailableWhenOrdinaryRequestsAreSaturated()
    {
        var entered = 0;
        var discoveriesStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler(async (request, cancellationToken) =>
        {
            if (request.Method == HttpMethod.Post) return Reply(request, Result());
            if (Interlocked.Increment(ref entered) == 2) discoveriesStarted.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return Reply(request, Encoding.UTF8.GetString(NativeJson.SerializeUtf8(Discovery())));
        });
        using var http = new HttpClient(handler);
        using var client = NativeClient.ForHttp(Options(new() { MaxConcurrentRequests = 3, ReservedControlRequests = 1 }), http);
        var first = client.Target.DiscoverAsync();
        var second = client.Target.DiscoverAsync();
        try
        {
            await discoveriesStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Failure(client.Target.DiscoverAsync(), NativeHttpFailureKind.Capacity);
            var session = await client.Target.CreateOecpSessionAsync(Discovery());
            Check(session.Endpoint == "wss://target.example/native-v2/oecp" && handler.Calls == 3);
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(first, second);
        }
    }

    [Test]
    public async Task SessionResponseRequestAndErrorBoundsAreEnforced()
    {
        foreach (var (status, transport, body) in new[]
        {
            (HttpStatusCode.OK, new TransportOptions(), new string('x', 65537)),
            (HttpStatusCode.BadRequest, new TransportOptions { MaxErrorBodyBytes = 8 }, "123456789"),
            (HttpStatusCode.OK, new TransportOptions { MaxResponseBytes = 8 }, Result())
        })
        {
            using var handler = new Handler((request, _) => Task.FromResult(Reply(request, body, status)));
            using var http = new HttpClient(handler);
            using var client = NativeClient.ForHttp(Options(transport), http);
            var failure = await Failure(client.Target.CreateOecpSessionAsync(Discovery()), NativeHttpFailureKind.SizeLimit);
            Check(failure.StatusCode == status);
        }
        using var noDispatch = new Handler((request, _) => Task.FromResult(Reply(request, Result())));
        using var noDispatchHttp = new HttpClient(noDispatch);
        using var limited = NativeClient.ForHttp(Options(new() { MaxRequestBytes = 1 }), noDispatchHttp);
        await Failure(limited.Target.CreateOecpSessionAsync(Discovery()), NativeHttpFailureKind.SizeLimit);
        Check(noDispatch.Calls == 0);
    }

    [Test]
    public async Task RedirectsAndChangedResponseUrlsNeverCauseAutomaticCredentialResends()
    {
        foreach (var redirected in new[] { true, false })
        {
            using var handler = new Handler((request, _) =>
            {
                Check(request.Headers.Authorization!.Parameter == Control);
                if (!redirected) request.RequestUri = new Uri("https://foreign.example/");
                var reply = Reply(request, Result(token: Session), redirected ? HttpStatusCode.TemporaryRedirect : HttpStatusCode.OK);
                reply.Headers.Location = new Uri("https://foreign.example/");
                return Task.FromResult(reply);
            });
            using var http = new HttpClient(handler);
            using var client = NativeClient.ForHttp(Options(), http);
            await Failure(client.Target.CreateOecpSessionAsync(Discovery(TargetAuthentication.HostedOauth),
                credentials: new(TargetAuthentication.HostedOauth, Control)), NativeHttpFailureKind.Redirect);
            Check(handler.Calls == 1);
        }
    }

    [Test]
    public async Task CanonicalUrlsAndLiteralCompilerPreserveBasePathsAndRejectVariables()
    {
        var origin = new Uri("https://target.example/");
        Check(NativeRoutes.CapabilityBaseUrl(origin, "https://target.example") == origin);
        Check(NativeRoutes.SameOriginUrl(origin, "https://target.example/%41").AbsoluteUri == "https://target.example/%41");
        Check(NativeRoutes.CompileLiteralRoute(NativeRoutes.SameOriginUrl(origin, "https://target.example/api/v1"), "/list")
            .AbsoluteUri == "https://target.example/api/v1/list");
        Check(NativeRoutes.CompileLiteralRoute(NativeRoutes.SameOriginUrl(origin, "https://target.example/api//"), "/list")
            .AbsoluteUri == "https://target.example/api//list");
        await Invalid(() => Task.FromResult(NativeRoutes.SessionEndpoint(new Uri("https://127.0.0.1/"), "wss://127.1/session")));
        foreach (var invalid in new[] { "https://foreign.example/api", "http://target.example/api", "https://TARGET.example/api", "https://target.example:443/api", "https://target.example", "https://@target.example/api", "https://target.example/a/../b", "https://target.example/api?", "https://target.example/api#", "https://target.example/%2e%2e/session", "https://target.example/é" })
            await Invalid(() => Task.FromResult(NativeRoutes.SameOriginUrl(origin, invalid)));
        foreach (var invalid in new[] { "relative", "//foreign", "/", "/a//b", "/a/", "/a/..", "/a/.", "/{run_id}", "/{unknown}", "/{run_id}/{run_id}", "/list{?after}", "/list?x", "/list#x", "/a\\b", "/%2e", "/a b", "/" + new string('a', 2048) })
            await Invalid(() => Task.FromResult(NativeRoutes.CompileLiteralRoute(origin, invalid)));
    }

    [Test]
    public async Task HttpsSessionAcquisitionUsesTrustedTlsAndReturnsMatchingWssAuthority()
    {
        using var rsa = RSA.Create(2048);
        var certificateRequest = new CertificateRequest("CN=session-tests", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        certificateRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        certificateRequest.CertificateExtensions.Add(san.Build());
        using var ephemeral = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(10));
        // Windows TLS (SChannel) cannot serve an ephemeral private key; a PKCS#12 round trip gives it a usable one.
        using var certificate = X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pkcs12), null);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var endpoint = $"wss://127.0.0.1:{port}/native-v2/oecp";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(timeout.Token);
            using var tls = new SslStream(socket.GetStream());
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, timeout.Token);
            using var reader = new StreamReader(tls, Encoding.ASCII, leaveOpen: true);
            Check(await reader.ReadLineAsync(timeout.Token) == "POST /native-v2/oecp-session HTTP/1.1");
            var headers = new List<string>();
            while (await reader.ReadLineAsync(timeout.Token) is { Length: > 0 } line) headers.Add(line);
            Check(headers.Count(h => h.StartsWith("Authorization:")) == 1 && headers.Contains("Authorization: Bearer " + Control));
            Check(!headers.Any(h => h.Contains(Session)));
            var body = new char[2];
            await reader.ReadBlockAsync(body, timeout.Token);
            Check(new string(body) == "{}");
            var result = Result(endpoint, Session);
            await tls.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {result.Length}\r\nConnection: close\r\n\r\n{result}"), timeout.Token);
        });
        using var handler = NativeClient.CreateHttpHandler();
        handler.SslOptions.CertificateChainPolicy = new X509ChainPolicy
        {
            TrustMode = X509ChainTrustMode.CustomRootTrust,
            RevocationMode = X509RevocationMode.NoCheck
        };
        handler.SslOptions.CertificateChainPolicy.CustomTrustStore.Add(certificate);
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var client = NativeClient.ForHttp(new() { Origin = new Uri($"https://127.0.0.1:{port}/") }, http);
        var session = await client.Target.CreateOecpSessionAsync(Discovery(TargetAuthentication.HostedOauth),
            credentials: new(TargetAuthentication.HostedOauth, Control), cancellationToken: timeout.Token);
        Check(session.Endpoint == endpoint && session.BearerToken == Session);
        await server;
    }
}
