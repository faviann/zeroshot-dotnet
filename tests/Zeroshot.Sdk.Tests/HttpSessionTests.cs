using System.Net;
using System.Net.Http.Headers;
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
    private static async Task<NativeHttpException> Failure(Task task, NativeHttpFailureKind kind)
    {
        var error = await TestKit.Failure(task, kind);
        Check(!error.ToString().Contains(Control) && !error.ToString().Contains(Session));
        return error;
    }
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
                    return TextReply(request, Encoding.UTF8.GetString(NativeJson.SerializeUtf8(Discovery(authentication))));
                }
                Check(request.Method == HttpMethod.Post && request.RequestUri!.AbsoluteUri == "https://target.example/native-v2/oecp-session");
                Check(request.Headers.Authorization?.Scheme == (expectedBearer is null ? null : "Bearer"));
                Check(request.Headers.Authorization?.Parameter == expectedBearer);
                Check(request.Content!.Headers.ContentType!.MediaType == "application/json");
                var body = await request.Content.ReadAsStringAsync();
                Check(body == (handlerBodyWithRun ? $"{{\"runId\":\"{Run}\"}}" : "{}"));
                return TextReply(request, Result(token: issuedBearer), HttpStatusCode.Created);
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
        using var handler = new Handler((request, _) => Task.FromResult(TextReply(request, Result(token: Session))));
        using var http = new HttpClient(handler);
        using var client = NativeClient.ForHttp(Options(), http);
        var credentials = new TargetControlCredentials(TargetAuthentication.HostedOauth, Control);
        foreach (var path in new[] { "https://foreign.example/", "//foreign.example/session", "/session?x=1", "/session#x", "/session/{run_id}", "/session{?run_id}", "/session\\x", "/../session", "/session/%", "/session/ space" })
        {
            var discovery = Discovery(TargetAuthentication.HostedOauth) with { SessionPath = path };
            await Invalid(() => client.Target.CreateOecpSessionAsync(discovery, credentials: credentials), Control, Session);
        }
        await Invalid(() => client.Target.CreateOecpSessionAsync(Discovery(), credentials: credentials), Control, Session);
        await Invalid(() => client.Target.CreateOecpSessionAsync(Discovery(TargetAuthentication.PrivateCapability), credentials: credentials), Control, Session);
        await Invalid(() => client.Target.CreateOecpSessionAsync(Discovery(TargetAuthentication.HostedOauth)), Control, Session);
        foreach (var id in new[] { "not-a-run", Run.ToUpperInvariant(), "0195af77-1000-4000-8000-000000000001", "0195af77-1000-7000-c000-000000000001" })
            await Invalid(() => client.Target.CreateOecpSessionAsync(Discovery(), new() { RunId = new(id) }), Control, Session);
        Check(handler.Calls == 0);
    }

    [Test]
    public async Task EscapedCanonicalSessionPathsKeepTheirReceivedSpelling()
    {
        using var handler = new Handler((request, _) =>
        {
            Check(request.RequestUri!.AbsoluteUri == "https://target.example/sessions/%41");
            Check(request.Headers.Authorization!.Parameter == Control);
            return Task.FromResult(TextReply(request, Result(token: Session)));
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
        using var handler = new Handler((request, _) => Task.FromResult(TextReply(request, Result())));
        using var http = new HttpClient(handler);
        foreach (var token in new[] { "", "a b", "a\r\nb", "é", new string('a', 16385) })
            await Invalid(() => Task.FromResult(new TargetControlCredentials(TargetAuthentication.HostedOauth, token)), Control, Session);
        _ = new TargetControlCredentials(TargetAuthentication.PrivateCapability, new string('a', 16384));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Control);
        await Invalid(() => Task.FromResult(NativeClient.ForHttp(Options(), http)), Control, Session);
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
            using var handler = new Handler((request, _) => Task.FromResult(TextReply(request, Result(endpoint, Session))));
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
            using var handler = new Handler((request, _) => Task.FromResult(TextReply(request, Result(endpoint))));
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
            using var handler = new Handler((request, _) => Task.FromResult(TextReply(request, body)));
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
        using var handler = new Handler((request, _) => Task.FromResult(TextReply(request, body, HttpStatusCode.Forbidden)));
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
            using var badHandler = new Handler((request, _) => Task.FromResult(TextReply(request, badProblem, HttpStatusCode.Unauthorized)));
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
            using var handler = new Handler((request, _) => Task.FromResult(TextReply(request, body, HttpStatusCode.Forbidden)));
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
            if (request.Method == HttpMethod.Post) return TextReply(request, Result());
            if (Interlocked.Increment(ref entered) == 2) discoveriesStarted.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return TextReply(request, Encoding.UTF8.GetString(NativeJson.SerializeUtf8(Discovery())));
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
            using var handler = new Handler((request, _) => Task.FromResult(TextReply(request, body, status)));
            using var http = new HttpClient(handler);
            using var client = NativeClient.ForHttp(Options(transport), http);
            var failure = await Failure(client.Target.CreateOecpSessionAsync(Discovery()), NativeHttpFailureKind.SizeLimit);
            Check(failure.StatusCode == status);
        }
        using var noDispatch = new Handler((request, _) => Task.FromResult(TextReply(request, Result())));
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
                var reply = TextReply(request, Result(token: Session), redirected ? HttpStatusCode.TemporaryRedirect : HttpStatusCode.OK);
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
        await Invalid(() => Task.FromResult(NativeRoutes.SessionEndpoint(new Uri("https://127.0.0.1/"), "wss://127.1/session")), Control, Session);
        foreach (var invalid in new[] { "https://foreign.example/api", "http://target.example/api", "https://TARGET.example/api", "https://target.example:443/api", "https://target.example", "https://@target.example/api", "https://target.example/a/../b", "https://target.example/api?", "https://target.example/api#", "https://target.example/%2e%2e/session", "https://target.example/é" })
            await Invalid(() => Task.FromResult(NativeRoutes.SameOriginUrl(origin, invalid)), Control, Session);
        foreach (var invalid in new[] { "relative", "//foreign", "/", "/a//b", "/a/", "/a/..", "/a/.", "/{run_id}", "/{unknown}", "/{run_id}/{run_id}", "/list{?after}", "/list?x", "/list#x", "/a\\b", "/%2e", "/a b", "/" + new string('a', 2048) })
            await Invalid(() => Task.FromResult(NativeRoutes.CompileLiteralRoute(origin, invalid)), Control, Session);
    }

    [Test]
    public async Task HttpsSessionAcquisitionUsesTrustedTlsAndReturnsMatchingWssAuthority()
    {
        await using var target = new TlsTarget();
        var endpoint = $"wss://127.0.0.1:{target.Port}/native-v2/oecp";
        var result = Result(endpoint, Session);
        target.Respond = _ => $"HTTP/1.1 200 OK\r\nContent-Length: {result.Length}\r\nConnection: close\r\n\r\n{result}";
        using var client = NativeClient.ForHttp(new() { Origin = target.Origin, Transport = new() { TrustedRootCertificatePath = target.RootPath } });
        var session = await client.Target.CreateOecpSessionAsync(Discovery(TargetAuthentication.HostedOauth),
            credentials: new(TargetAuthentication.HostedOauth, Control));
        Check(session.Endpoint == endpoint && session.BearerToken == Session);
        var (line, headers, body) = await target.Requests.ReadAsync();
        Check(line == "POST /native-v2/oecp-session HTTP/1.1" && body == "{}");
        Check(headers.Count(h => h.StartsWith("Authorization:")) == 1 && headers.Contains("Authorization: Bearer " + Control));
        Check(!headers.Any(h => h.Contains(Session)));
    }
}
