using System.Net;
using System.Text;
using System.Text.Json;
using TUnit.Core;
using Zeroshot.Native;
using Zeroshot.Native.Contracts;

namespace Zeroshot.Client.Tests;

public sealed class HttpOAuthTests
{
    private const string Access = "ACCESS-TOKEN-CANARY";
    private const string Refresh = "REFRESH-TOKEN-CANARY";
    private const string DeviceCode = "DEVICE-CODE-CANARY";
    private static readonly Guid DeviceToken = Guid.Parse("0f3c2a9e-58b1-4d7e-9a61-2b4c8d0e6f17");
    private static readonly TargetControlCredentials Hosted = new(TargetAuthentication.HostedOauth, Access);
    private static readonly TargetOAuthDiscovery OAuth = new()
    {
        MetadataUrl = "https://target.example/.well-known/oauth-authorization-server",
        DeviceAuthorizationEndpoint = "https://target.example/oauth/device",
        TokenEndpoint = "https://target.example/oauth/token",
        RevocationEndpoint = "https://target.example/oauth/revoke",
        ClientId = "zeroshot cli~1", DeviceGrantType = "urn:ietf:params:oauth:grant-type:device_code",
        DeviceExchangeFields = ["device_label", "device_token"]
    };
    private static readonly TargetLoginSessionDiscovery Login = new() { RouteTemplate = "/login/session", Method = "GET", CachePolicy = "no-store" };
    private static TargetDiscoveryDocument Discovery(TargetOAuthDiscovery? oauth = null, TargetLoginSessionDiscovery? login = null)
        => TestDiscovery.Controller(TargetAuthentication.HostedOauth) with { Oauth = oauth ?? OAuth, LoginSession = login ?? Login };
    private static readonly DeviceAuthorization Authorization = new()
    {
        DeviceCode = DeviceCode, UserCode = "ABCD-EFGH", VerificationUri = "https://login.example/device",
        VerificationUriComplete = "https://login.example/device?user_code=ABCD-EFGH", ExpiresIn = 600, Interval = 5
    };
    private static string Metadata(string? token = null) =>
        $$"""{"issuer":"https://target.example","device_authorization_endpoint":"{{OAuth.DeviceAuthorizationEndpoint}}","token_endpoint":"{{token ?? OAuth.TokenEndpoint}}","revocation_endpoint":"{{OAuth.RevocationEndpoint}}","grant_types_supported":["refresh_token"]}""";
    private static string Code(string fields = "") =>
        $$"""{"device_code":"{{DeviceCode}}","user_code":"ABCD-EFGH","verification_uri":"https://login.example/device","expires_in":600,"interval":5{{fields}}}""";
    private static string Tokens(string type = "Bearer", string scope = "controller", ulong expires = 3600, ulong refreshExpires = 2592000, string access = Access) =>
        $$"""{"access_token":"{{access}}","refresh_token":"{{Refresh}}","token_type":"{{type}}","expires_in":{{expires}},"refresh_expires_in":{{refreshExpires}},"scope":"{{scope}}"}""";
    private static Handler Replying(string body, HttpStatusCode status = HttpStatusCode.OK) => new((request, _) => Task.FromResult(Reply(request, body, status)));

    [Test]
    public async Task EachOperationSendsTheExactNativeRequestToItsDiscoveredUrl()
    {
        var seen = new List<(HttpMethod Method, string Uri, string? Authorization, string? Accept, string? Type, bool NoStore, string? Body)>();
        using var handler = new Handler(async (request, token) =>
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(token);
            seen.Add((request.Method, request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(),
                request.Headers.Accept.Count == 0 ? null : request.Headers.Accept.ToString(), request.Content?.Headers.ContentType?.ToString(),
                request.Headers.CacheControl?.NoStore == true, body));
            return request.RequestUri!.AbsolutePath switch
            {
                "/.well-known/oauth-authorization-server" => Reply(request, Metadata()),
                // Hosts may use any 2xx status.
                "/oauth/device" => Reply(request, Code(""","verification_uri_complete":"https://login.example/device?code=1" """), HttpStatusCode.Created),
                "/oauth/token" => Reply(request, Tokens()),
                _ => Reply(request, """{"kind":"openengine.target-session/v1","organization_id":"org-1"}""")
            };
        });
        using var client = ClientFor(handler);

        var metadata = await client.OAuth.MetadataAsync(Discovery());
        var code = await client.OAuth.BeginDeviceAuthorizationAsync(Discovery());
        var exchange = await client.OAuth.ExchangeDeviceTokenAsync(Discovery(), code, DeviceToken);
        var refresh = await client.OAuth.RefreshAsync(Discovery(), "rotating refresh/1");
        var session = await client.OAuth.VerifySessionAsync(Discovery(), Hosted);

        Check(metadata.RevocationEndpoint == OAuth.RevocationEndpoint && code.VerificationUriComplete == "https://login.example/device?code=1");
        Check(exchange.Outcome == NativeAttemptOutcome.Acknowledged && exchange.Operation == "oauth.exchangeDeviceToken" && exchange.Response!.AccessToken == Access);
        Check(refresh.Outcome == NativeAttemptOutcome.Acknowledged && refresh.Operation == "oauth.refresh" && refresh.Response!.RefreshToken == Refresh);
        Check(session.OrganizationId == "org-1");
        const string Form = "application/x-www-form-urlencoded";
        var expected = new (HttpMethod, string, string?, string?, string?, bool, string?)[]
        {
            (HttpMethod.Get, OAuth.MetadataUrl, null, "application/json", null, false, null),
            (HttpMethod.Post, OAuth.DeviceAuthorizationEndpoint, null, "*/*", Form, false, "client_id=zeroshot+cli%7E1"),
            (HttpMethod.Post, OAuth.TokenEndpoint, null, "*/*", Form, false,
                "grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Adevice_code&device_code=DEVICE-CODE-CANARY&client_id=zeroshot+cli%7E1" +
                "&device_token=0f3c2a9e-58b1-4d7e-9a61-2b4c8d0e6f17&device_label=zeroshot-cli&audience=controller"),
            (HttpMethod.Post, OAuth.TokenEndpoint, null, "*/*", Form, false,
                "grant_type=refresh_token&client_id=zeroshot+cli%7E1&refresh_token=rotating+refresh%2F1&audience=controller"),
            (HttpMethod.Get, "https://target.example/login/session", "Bearer " + Access, "application/json", null, true, null)
        };
        Check(seen.SequenceEqual(expected), string.Join("\n", seen));
    }

    [Test]
    public async Task InvalidDiscoveryAndCallerInputFailBeforeDispatch()
    {
        var discoveries = new[]
        {
            Discovery() with { Authentication = TargetAuthentication.None },
            Discovery() with { Authentication = TargetAuthentication.PrivateCapability },
            Discovery() with { Audience = "operator" },
            Discovery() with { Oauth = null },
            Discovery() with { LoginSession = null },
            Discovery(OAuth with { DeviceGrantType = "device_code" }),
            Discovery(OAuth with { DeviceExchangeFields = ["device_token"] }),
            Discovery(OAuth with { DeviceExchangeFields = ["device_token", "device_token"] }),
            Discovery(OAuth with { DeviceExchangeFields = ["device_token", "device_label", "device_name"] }),
            Discovery(OAuth with { ClientId = "" }),
            Discovery(OAuth with { ClientId = new string('é', 128) + "x" }),
            Discovery(OAuth with { ClientId = "client\u007f" }),
            Discovery(OAuth with { MetadataUrl = "https://attacker.example/metadata" }),
            Discovery(OAuth with { DeviceAuthorizationEndpoint = "https://attacker.example/oauth/device" }),
            Discovery(OAuth with { TokenEndpoint = "https://attacker.example/oauth/token" }),
            Discovery(OAuth with { RevocationEndpoint = "https://attacker.example/oauth/revoke" }),
            Discovery(login: Login with { Method = "POST" }),
            Discovery(login: Login with { CachePolicy = "no-cache" }),
            Discovery(login: Login with { RouteTemplate = "//attacker.example/session" }),
            Discovery(login: Login with { RouteTemplate = "/login/session?device_label=x" })
        };
        using var handler = Replying("{}");
        using var client = ClientFor(handler);
        foreach (var discovery in discoveries)
        {
            await Invalid(() => client.OAuth.MetadataAsync(discovery), Access, Refresh, DeviceCode);
            await Invalid(() => client.OAuth.BeginDeviceAuthorizationAsync(discovery), Access, Refresh, DeviceCode);
            await Invalid(() => client.OAuth.ExchangeDeviceTokenAsync(discovery, Authorization, DeviceToken), Access, Refresh, DeviceCode);
            await Invalid(() => client.OAuth.RefreshAsync(discovery, Refresh), Access, Refresh, DeviceCode);
            await Invalid(() => client.OAuth.VerifySessionAsync(discovery, Hosted), Access, Refresh, DeviceCode);
        }
        foreach (var refresh in new[] { "", "line\nbreak", new string('r', 16 * 1024 + 1) })
            await Invalid(() => client.OAuth.RefreshAsync(Discovery(), refresh), Access, Refresh, DeviceCode);
        foreach (var authorization in new[] { Authorization with { DeviceCode = "" }, Authorization with { ExpiresIn = 0 } })
            await Invalid(() => client.OAuth.ExchangeDeviceTokenAsync(Discovery(), authorization, DeviceToken), Access, Refresh, DeviceCode);
        await Invalid(() => client.OAuth.VerifySessionAsync(Discovery(), new(TargetAuthentication.PrivateCapability, Access)), Access, Refresh, DeviceCode);
        Check(handler.Calls == 0);
    }

    [Test]
    public async Task MetadataEndpointsMustMatchDiscovery()
    {
        foreach (var token in new[]
        {
            "https://target.example/oauth/other", "https://target.example:443/oauth/token",
            "https://attacker.example/oauth/token", "https://target.example/oauth/token/"
        })
        {
            using var handler = Replying(Metadata(token));
            using var client = ClientFor(handler);
            await Failure(client.OAuth.MetadataAsync(Discovery()), NativeHttpFailureKind.Protocol);
        }
        foreach (var body in new[] { """{"token_endpoint":"x"}""", Metadata().Replace("\"issuer\"", "\"token_endpoint\"", StringComparison.Ordinal) })
        {
            using var handler = Replying(body);
            using var client = ClientFor(handler);
            await Failure(client.OAuth.MetadataAsync(Discovery()), NativeHttpFailureKind.Protocol);
        }
    }

    [Test]
    public async Task DeviceAuthorizationEnforcesNativeBoundsAndVerificationUrlRules()
    {
        var valid = new[]
        {
            Code(""","verification_uri_complete":null"""),
            Code().Replace("\"expires_in\":600", "\"expires_in\":86400").Replace("\"interval\":5", "\"interval\":0"),
            Code().Replace("\"interval\":5", "\"interval\":300").Replace("ABCD-EFGH", new string('é', 128)),
            Code().Replace("https://login.example/device", "http://127.0.0.1:8080/device"),
            Code(""","verification_uri_complete":"https://login.example:443/other?user_code=1" """)
        };
        foreach (var body in valid)
        {
            using var handler = Replying(body);
            using var client = ClientFor(handler);
            _ = await client.OAuth.BeginDeviceAuthorizationAsync(Discovery());
        }
        var invalid = new[]
        {
            Code().Replace("\"expires_in\":600", "\"expires_in\":0"),
            Code().Replace("\"expires_in\":600", "\"expires_in\":86401"),
            Code().Replace("\"interval\":5", "\"interval\":301"),
            Code().Replace(DeviceCode, ""),
            Code().Replace(DeviceCode, new string('d', 16 * 1024 + 1)),
            Code().Replace("ABCD-EFGH", new string('u', 257)),
            Code().Replace("ABCD-EFGH", "AB\\u0001"),
            Code().Replace("https://login.example/device", "https://login.example/device?x=1"),
            Code().Replace("https://login.example/device", "https://login.example/device#x"),
            Code().Replace("https://login.example/device", "https://user@login.example/device"),
            Code().Replace("https://login.example/device", "http://login.example/device"),
            Code().Replace("https://login.example/device", "http://[::1]/device"),
            Code().Replace("https://login.example/device", "https://login.example/" + new string('p', 4096)),
            Code(""","verification_uri_complete":"https://other.example/device?x=1" """),
            Code(""","verification_uri_complete":"https://login.example:8443/device" """),
            Code(""","verification_uri_complete":"https://login.example/device#x" """),
            Code(""","extra":1"""),
            Code().Replace(",\"interval\":5", "")
        };
        foreach (var body in invalid)
        {
            using var handler = Replying(body);
            using var client = ClientFor(handler);
            await Failure(client.OAuth.BeginDeviceAuthorizationAsync(Discovery()), NativeHttpFailureKind.Protocol);
        }
    }

    [Test]
    public async Task BothGrantsRequireAValidBearerTokenResponse()
    {
        var invalid = new[]
        {
            Tokens(type: "bearer"), Tokens(expires: 0), Tokens(expires: 86401), Tokens(refreshExpires: 0),
            Tokens(refreshExpires: 31536001), Tokens(scope: ""), Tokens(scope: new string('s', 513)),
            Tokens(scope: "a\\tb"), Tokens(access: ""), Tokens(access: new string('a', 16 * 1024 + 1)),
            Tokens()[..^1] + ",\"id_token\":\"x\"}"
        };
        foreach (var body in invalid)
        {
            using var handler = Replying(body);
            using var client = ClientFor(handler);
            // The grant may have taken effect before the malformed reply, so the attempt stays uncertain.
            foreach (var attempt in new[]
            {
                await client.OAuth.ExchangeDeviceTokenAsync(Discovery(), Authorization, DeviceToken),
                await client.OAuth.RefreshAsync(Discovery(), Refresh)
            })
                Check(attempt.Outcome == NativeAttemptOutcome.Unknown && attempt.Response is null &&
                    attempt.Failure is NativeHttpException { Kind: NativeHttpFailureKind.Protocol }, body);
        }
        using var boundary = Replying(Tokens(expires: 86400, refreshExpires: 31536000, scope: new string('s', 512)));
        using var peer = ClientFor(boundary);
        Check((await peer.OAuth.RefreshAsync(Discovery(), Refresh)).Outcome == NativeAttemptOutcome.Acknowledged);
    }

    [Test]
    public async Task DeviceTokenErrorsAreRejectedDataAndOtherFailuresStayUncertain()
    {
        foreach (var (error, code) in new[]
        {
            ("authorization_pending", DeviceTokenError.AuthorizationPending), ("slow_down", DeviceTokenError.SlowDown),
            ("access_denied", DeviceTokenError.AccessDenied), ("expired_token", DeviceTokenError.ExpiredToken)
        })
        {
            using var handler = Replying($$"""{"error":"{{error}}"}""", HttpStatusCode.BadRequest);
            using var client = ClientFor(handler);
            var attempt = await client.OAuth.ExchangeDeviceTokenAsync(Discovery(), Authorization, DeviceToken);
            Check(attempt.Outcome == NativeAttemptOutcome.Rejected && attempt.Failure is NativeHttpException
                { Kind: NativeHttpFailureKind.HttpStatus, StatusCode: HttpStatusCode.BadRequest, Problem: null } failure &&
                failure.DeviceTokenError == code, error);
        }
        foreach (var body in new[]
        {
            """{"error":"invalid_grant"}""", """{"error":"slow_down","error_description":"wait"}""",
            """{"code":"invalid_request","message":"refused"}""", "not json"
        })
        {
            using var handler = Replying(body, HttpStatusCode.BadRequest);
            using var client = ClientFor(handler);
            var attempt = await client.OAuth.ExchangeDeviceTokenAsync(Discovery(), Authorization, DeviceToken);
            Check(attempt.Outcome == NativeAttemptOutcome.Unknown &&
                attempt.Failure is NativeHttpException { DeviceTokenError: null, Problem: null }, body);
        }

        using var lost = new Handler((_, _) => throw new HttpRequestException("lost"));
        using var peer = ClientFor(lost);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        foreach (var (attempt, outcome) in new[]
        {
            (await peer.OAuth.ExchangeDeviceTokenAsync(Discovery(), Authorization, DeviceToken), NativeAttemptOutcome.Unknown),
            (await peer.OAuth.RefreshAsync(Discovery(), Refresh), NativeAttemptOutcome.Unknown),
            (await peer.OAuth.ExchangeDeviceTokenAsync(Discovery(), Authorization, DeviceToken, cancelled.Token), NativeAttemptOutcome.NotSent),
            (await peer.OAuth.RefreshAsync(Discovery(), Refresh, cancelled.Token), NativeAttemptOutcome.NotSent)
        })
            Check(attempt.Outcome == outcome);
        Check(lost.Calls == 2);
    }

    [Test]
    public async Task RefreshRejectsOnlyNativePreEffectProblemPairs()
    {
        foreach (var (status, body, rejected) in new[]
        {
            (400, """{"code":"invalid_request","message":"refused"}""", true),
            (401, """{"code":"unauthorized","message":"refused"}""", true),
            (403, """{"code":"forbidden","message":"refused"}""", true),
            (404, """{"code":"not_found","message":"refused"}""", true),
            (409, """{"code":"request_conflict","message":"refused"}""", false),
            (503, """{"code":"target.unavailable","message":"refused"}""", false),
            (400, """{"error":"invalid_grant"}""", false)
        })
        {
            using var handler = Replying(body, (HttpStatusCode)status);
            using var client = ClientFor(handler);
            var attempt = await client.OAuth.RefreshAsync(Discovery(), Refresh);
            Check(attempt.Outcome == (rejected ? NativeAttemptOutcome.Rejected : NativeAttemptOutcome.Unknown) &&
                attempt.Failure is NativeHttpException { DeviceTokenError: null } failure && failure.StatusCode == (HttpStatusCode)status, body);
        }
    }

    [Test]
    public async Task SessionVerificationRequiresTheNativeSessionShape()
    {
        foreach (var body in new[]
        {
            """{"kind":"openengine.target-session/v2","organization_id":"org"}""",
            """{"kind":"openengine.target-session/v1","organization_id":""}""",
            $$"""{"kind":"openengine.target-session/v1","organization_id":"{{new string('é', 128)}}x"}""",
            """{"kind":"openengine.target-session/v1","organization_id":"org","user_id":"u"}"""
        })
        {
            using var handler = Replying(body);
            using var client = ClientFor(handler);
            await Failure(client.OAuth.VerifySessionAsync(Discovery(), Hosted), NativeHttpFailureKind.Protocol);
        }
        using var refused = Replying("""{"code":"unauthorized","message":"expired"}""", HttpStatusCode.Unauthorized);
        using var peer = ClientFor(refused);
        var failure = await Failure(peer.OAuth.VerifySessionAsync(Discovery(), Hosted), NativeHttpFailureKind.HttpStatus);
        Check(failure.Problem!.Code == "unauthorized");
    }

    [Test]
    public async Task DefaultFormattingOmitsTokensAndDeviceSecretsWhileExplicitDataRemains()
    {
        using var handler = new Handler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath == "/oauth/device"
            ? Reply(request, Code()) : Reply(request, Tokens())));
        using var client = ClientFor(handler);
        var code = await client.OAuth.BeginDeviceAuthorizationAsync(Discovery());
        var tokens = await client.OAuth.ExchangeDeviceTokenAsync(Discovery(), code, DeviceToken);
        using var refusing = Replying("""{"error":"slow_down"}""", HttpStatusCode.BadRequest);
        using var peer = ClientFor(refusing);
        var refused = await peer.OAuth.RefreshAsync(Discovery(), Refresh);
        foreach (var text in new[] { code.ToString(), tokens.ToString(), tokens.Response!.ToString(), refused.ToString(), refused.Failure!.ToString() })
            Check(!text.Contains(Access) && !text.Contains(Refresh) && !text.Contains(DeviceCode) && !text.Contains(DeviceToken.ToString()), text);
        Check(code.DeviceCode == DeviceCode && tokens.Response.AccessToken == Access && tokens.Response.RefreshToken == Refresh);
    }

}
