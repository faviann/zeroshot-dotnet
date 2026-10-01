using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Zeroshot.Native.Contracts;
using Zeroshot.Native.Execution;

namespace Zeroshot.Native;

public sealed partial class NativeClient
{
    internal Task<OAuthMetadata> GetOAuthMetadataAsync(TargetDiscoveryDocument discovery, CancellationToken cancellationToken)
    {
        var (routes, oauth) = PrepareOAuthCall(discovery);
        return ExecuteJsonAsync<OAuthMetadata>(NativeOAuthClient.MetadataOperation, routes.Metadata, null, null, metadata =>
        {
            // Native validate_metadata_routes. Discovered URLs are already validated, so equality suffices.
            foreach (var (advertised, discovered) in new[]
            {
                (metadata.DeviceAuthorizationEndpoint, oauth.DeviceAuthorizationEndpoint),
                (metadata.TokenEndpoint, oauth.TokenEndpoint),
                (metadata.RevocationEndpoint, oauth.RevocationEndpoint)
            })
                if (advertised != discovered) throw new JsonException();
        }, cancellationToken, configure: AcceptJson);
    }

    internal Task<DeviceAuthorization> BeginDeviceAuthorizationAsync(TargetDiscoveryDocument discovery, CancellationToken cancellationToken)
    {
        var (routes, oauth) = PrepareOAuthCall(discovery);
        return ExecuteJsonAsync<DeviceAuthorization>(NativeOAuthClient.BeginDeviceAuthorizationOperation, routes.Device,
            Form(("client_id", oauth.ClientId)), null, _ => { }, cancellationToken, configure: FormContent);
    }

    internal async Task<NativeAttempt<OAuthTokens>> ExchangeDeviceTokenAsync(TargetDiscoveryDocument discovery,
        DeviceAuthorization authorization, Guid deviceToken, CancellationToken cancellationToken)
    {
        var (routes, oauth) = PrepareOAuthCall(discovery);
        ArgumentNullException.ThrowIfNull(authorization);
        _ = NativeJson.SerializeUtf8(authorization);
        var body = Form(("grant_type", oauth.DeviceGrantType), ("device_code", authorization.DeviceCode),
            ("client_id", oauth.ClientId), ("device_token", deviceToken.ToString("D")),
            ("device_label", NativeOAuthClient.DeviceLabel), ("audience", discovery.Audience));
        var operation = NativeOAuthClient.DeviceTokenOperation;
        return await AttemptAsync<OAuthTokens>(operation, routes.Token, body, null,
            (_, _) => false, cancellationToken, configure: FormContent).ConfigureAwait(false);
    }

    internal async Task<NativeAttempt<OAuthTokens>> RefreshOAuthAsync(TargetDiscoveryDocument discovery,
        string refreshToken, CancellationToken cancellationToken)
    {
        var (routes, oauth) = PrepareOAuthCall(discovery);
        ArgumentNullException.ThrowIfNull(refreshToken);
        if (!OAuthValues.Bounded(refreshToken, 16 * 1024))
            throw new ArgumentException("A refresh token must be 1..16384 UTF-8 bytes without control characters.", nameof(refreshToken));
        var body = Form(("grant_type", "refresh_token"), ("client_id", oauth.ClientId),
            ("refresh_token", refreshToken), ("audience", discovery.Audience));
        var operation = NativeOAuthClient.RefreshOperation;
        return await AttemptAsync<OAuthTokens>(operation, routes.Token, body, null,
            IsHostedRefusal, cancellationToken, configure: FormContent).ConfigureAwait(false);
    }

    internal Task<TargetLoginSession> VerifyLoginSessionAsync(TargetDiscoveryDocument discovery,
        TargetControlCredentials access, CancellationToken cancellationToken)
    {
        var (routes, _) = PrepareOAuthCall(discovery);
        ArgumentNullException.ThrowIfNull(access);
        if (access.Authentication != TargetAuthentication.HostedOauth)
            throw new ArgumentException("Session verification requires a hosted OAuth access token.", nameof(access));
        return ExecuteJsonAsync<TargetLoginSession>(NativeOAuthClient.VerifySessionOperation, routes.Session, null, access,
            _ => { }, cancellationToken, configure: request => { NoStore(request); AcceptJson(request); });
    }

    // Native validate_hosted_discovery and oauth_routes, limited to the fields these operations use.
    // Invalid use throws before any request is sent.
    private ((Uri Metadata, Uri Device, Uri Token, Uri Session) Routes, TargetOAuthDiscovery OAuth) PrepareOAuthCall(
        TargetDiscoveryDocument discovery)
    {
        ValidateHttpUse();
        ArgumentNullException.ThrowIfNull(discovery);
        // No remote descriptor may influence dispatch until validated.
        _ = NativeJson.SerializeUtf8(discovery);
        var oauth = discovery.Oauth;
        var fields = oauth?.DeviceExchangeFields ?? [];
        if (discovery.Kind != "zeroshot.native-v2-target/v2" || discovery.Audience != "controller" ||
            discovery.Authentication != TargetAuthentication.HostedOauth || oauth is null ||
            oauth.DeviceGrantType != NativeOAuthClient.DeviceGrant ||
            fields.Length != 2 || !fields.Contains("device_token") || !fields.Contains("device_label") ||
            discovery.LoginSession is not { Method: "GET", CachePolicy: "no-store" } login ||
            !OAuthValues.Bounded(oauth.ClientId, 256))
            throw new ArgumentException("OAuth operations require compatible hosted OAuth discovery.");
        // The revocation URL is route-validated as native does, though no operation uses it.
        _ = NativeRoutes.SameOriginUrl(Origin, oauth.RevocationEndpoint);
        return ((NativeRoutes.SameOriginUrl(Origin, oauth.MetadataUrl), NativeRoutes.SameOriginUrl(Origin, oauth.DeviceAuthorizationEndpoint),
            NativeRoutes.SameOriginUrl(Origin, oauth.TokenEndpoint), NativeRoutes.SameOriginPath(Origin, login.RouteTemplate)), oauth);
    }

    private static byte[] Form(params (string Name, string Value)[] pairs) => Encoding.ASCII.GetBytes(NativeRoutes.FormEncode(pairs));

    // Native reqwest form posts carry the form content type and reqwest's default Accept: */*.
    private static void FormContent(HttpRequestMessage request)
    {
        request.Content!.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
    }

    private static void AcceptJson(HttpRequestMessage request) => request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
}

/// <summary>
/// Individual hosted OAuth device-flow calls at URLs advertised by explicitly supplied discovery. There is no
/// polling loop, token store, automatic refresh or revocation operation.
/// </summary>
public sealed class NativeOAuthClient
{
    internal const string DeviceGrant = "urn:ietf:params:oauth:grant-type:device_code";
    internal const string DeviceLabel = "zeroshot-cli";
    internal static readonly OperationDescriptor MetadataOperation = new("oauth.metadata", OperationTransport.Http, responseBytes: 64 * 1024);
    internal static readonly OperationDescriptor BeginDeviceAuthorizationOperation = new("oauth.beginDeviceAuthorization", OperationTransport.Http, responseBytes: 64 * 1024);
    internal static readonly OperationDescriptor DeviceTokenOperation = new("oauth.exchangeDeviceToken", OperationTransport.Http, responseBytes: 64 * 1024, oauthErrors: true);
    internal static readonly OperationDescriptor RefreshOperation = new("oauth.refresh", OperationTransport.Http, responseBytes: 64 * 1024);
    internal static readonly OperationDescriptor VerifySessionOperation = new("oauth.verifySession", OperationTransport.Http, responseBytes: 64 * 1024);
    private readonly NativeClient client;
    internal NativeOAuthClient(NativeClient client) => this.client = client;

    /// <summary>Reads authorization-server metadata and requires its endpoints to match discovery. Failures throw NativeHttpException.</summary>
    public Task<OAuthMetadata> MetadataAsync(TargetDiscoveryDocument discovery, CancellationToken cancellationToken = default)
        => client.GetOAuthMetadataAsync(discovery, cancellationToken);

    /// <summary>Starts one device authorization. Failures throw NativeHttpException.</summary>
    public Task<DeviceAuthorization> BeginDeviceAuthorizationAsync(TargetDiscoveryDocument discovery, CancellationToken cancellationToken = default)
        => client.BeginDeviceAuthorizationAsync(discovery, cancellationToken);

    /// <summary>
    /// Polls the token endpoint once with the caller's registered device token. A recognized OAuth error is
    /// <see cref="NativeAttemptOutcome.Rejected"/> with <see cref="NativeHttpException.DeviceTokenError"/> set;
    /// the caller owns waiting, <c>slow_down</c> backoff and expiry.
    /// </summary>
    public Task<NativeAttempt<OAuthTokens>> ExchangeDeviceTokenAsync(TargetDiscoveryDocument discovery,
        DeviceAuthorization authorization, Guid deviceToken, CancellationToken cancellationToken = default)
        => client.ExchangeDeviceTokenAsync(discovery, authorization, deviceToken, cancellationToken);

    /// <summary>Exchanges a refresh token once. The host may rotate it, so an Unknown outcome leaves the stored token uncertain.</summary>
    public Task<NativeAttempt<OAuthTokens>> RefreshAsync(TargetDiscoveryDocument discovery, string refreshToken,
        CancellationToken cancellationToken = default)
        => client.RefreshOAuthAsync(discovery, refreshToken, cancellationToken);

    /// <summary>Verifies an access token at the discovered login-session route. Failures throw NativeHttpException.</summary>
    public Task<TargetLoginSession> VerifySessionAsync(TargetDiscoveryDocument discovery, TargetControlCredentials access,
        CancellationToken cancellationToken = default)
        => client.VerifyLoginSessionAsync(discovery, access, cancellationToken);
}
