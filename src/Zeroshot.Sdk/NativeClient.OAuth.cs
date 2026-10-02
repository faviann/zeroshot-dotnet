using System.Text;
using System.Text.Json;
using Zeroshot.Native.Contracts;
using Zeroshot.Native.Execution;

namespace Zeroshot.Native;

/// <summary>
/// Individual hosted OAuth device-flow calls at URLs advertised by explicitly supplied discovery. There is no
/// polling loop, token store, automatic refresh or revocation operation.
/// </summary>
public sealed class NativeOAuthClient
{
    internal const string DeviceGrant = "urn:ietf:params:oauth:grant-type:device_code";
    internal const string DeviceLabel = "zeroshot-cli";
    private const int MaxBytes = 64 * 1024;
    // Native reqwest form posts carry the form content type and reqwest's default Accept: */*.
    private static readonly HttpBinding<OAuthMetadata> Metadata = new(
        new("oauth.metadata", OperationTransport.Http, responseBytes: MaxBytes), RequestHeaders.AcceptJson);
    private static readonly HttpBinding<DeviceAuthorization> BeginDeviceAuthorization = new(
        new("oauth.beginDeviceAuthorization", OperationTransport.Http, responseBytes: MaxBytes), RequestHeaders.Form);
    // A recognized OAuth error is the only refusal: the server's statement that no tokens were issued.
    private static readonly HttpBinding<OAuthTokens> DeviceToken = new(
        new("oauth.exchangeDeviceToken", OperationTransport.Http, responseBytes: MaxBytes), RequestHeaders.Form, static (_, _) => false,
        response: HttpResponsePolicy.OAuth);
    private static readonly HttpBinding<OAuthTokens> Refresh = new(
        new("oauth.refresh", OperationTransport.Http, responseBytes: MaxBytes), RequestHeaders.Form, NativeClient.IsHostedRefusal);
    private static readonly HttpBinding<TargetLoginSession> VerifySession = new(
        new("oauth.verifySession", OperationTransport.Http, responseBytes: MaxBytes), RequestHeaders.Json);
    private readonly NativeClient client;
    internal NativeOAuthClient(NativeClient client) => this.client = client;

    /// <summary>Reads authorization-server metadata and requires its endpoints to match discovery. Failures throw NativeHttpException.</summary>
    public Task<OAuthMetadata> MetadataAsync(TargetDiscoveryDocument discovery, CancellationToken cancellationToken = default)
        => client.ReadAsync(Metadata, () => Routes(discovery).Metadata, null, cancellationToken, validate: metadata =>
        {
            // Native validate_metadata_routes. Discovered URLs are already validated, so equality suffices.
            var oauth = discovery.Oauth!;
            foreach (var (advertised, discovered) in new[]
            {
                (metadata.DeviceAuthorizationEndpoint, oauth.DeviceAuthorizationEndpoint),
                (metadata.TokenEndpoint, oauth.TokenEndpoint),
                (metadata.RevocationEndpoint, oauth.RevocationEndpoint)
            })
                if (advertised != discovered) throw new JsonException();
        });

    /// <summary>Starts one device authorization. Failures throw NativeHttpException.</summary>
    public Task<DeviceAuthorization> BeginDeviceAuthorizationAsync(TargetDiscoveryDocument discovery, CancellationToken cancellationToken = default)
        => client.ReadAsync(BeginDeviceAuthorization, () =>
        {
            var routes = Routes(discovery);
            return new HttpCall(routes.Device, Form(("client_id", discovery.Oauth!.ClientId)));
        }, null, cancellationToken);

    /// <summary>
    /// Polls the token endpoint once with the caller's registered device token. A recognized OAuth error is
    /// <see cref="NativeAttemptOutcome.Rejected"/> with <see cref="NativeHttpException.DeviceTokenError"/> set;
    /// the caller owns waiting, <c>slow_down</c> backoff and expiry.
    /// </summary>
    public Task<NativeAttempt<OAuthTokens>> ExchangeDeviceTokenAsync(TargetDiscoveryDocument discovery,
        DeviceAuthorization authorization, Guid deviceToken, CancellationToken cancellationToken = default)
        => client.MutateAsync(DeviceToken, () =>
        {
            var routes = Routes(discovery);
            ArgumentNullException.ThrowIfNull(authorization);
            _ = NativeJson.SerializeUtf8(authorization);
            var oauth = discovery.Oauth!;
            return new HttpCall(routes.Token, Form(("grant_type", oauth.DeviceGrantType), ("device_code", authorization.DeviceCode),
                ("client_id", oauth.ClientId), ("device_token", deviceToken.ToString("D")),
                ("device_label", DeviceLabel), ("audience", discovery.Audience)));
        }, null, cancellationToken);

    /// <summary>Exchanges a refresh token once. The host may rotate it, so an Unknown outcome leaves the stored token uncertain.</summary>
    public Task<NativeAttempt<OAuthTokens>> RefreshAsync(TargetDiscoveryDocument discovery, string refreshToken,
        CancellationToken cancellationToken = default)
        => client.MutateAsync(Refresh, () =>
        {
            var routes = Routes(discovery);
            ArgumentNullException.ThrowIfNull(refreshToken);
            if (!OAuthValues.Bounded(refreshToken, 16 * 1024))
                throw new ArgumentException("A refresh token must be 1..16384 UTF-8 bytes without control characters.", nameof(refreshToken));
            return new HttpCall(routes.Token, Form(("grant_type", "refresh_token"), ("client_id", discovery.Oauth!.ClientId),
                ("refresh_token", refreshToken), ("audience", discovery.Audience)));
        }, null, cancellationToken);

    /// <summary>Verifies an access token at the discovered login-session route. Failures throw NativeHttpException.</summary>
    public Task<TargetLoginSession> VerifySessionAsync(TargetDiscoveryDocument discovery, TargetControlCredentials access,
        CancellationToken cancellationToken = default)
        => client.ReadAsync(VerifySession, () =>
        {
            var routes = Routes(discovery);
            ArgumentNullException.ThrowIfNull(access);
            if (access.Authentication != TargetAuthentication.HostedOauth)
                throw new ArgumentException("Session verification requires a hosted OAuth access token.", nameof(access));
            return routes.Session;
        }, access, cancellationToken);

    // Native validate_hosted_discovery and oauth_routes, limited to the fields these operations use.
    private (Uri Metadata, Uri Device, Uri Token, Uri Session) Routes(TargetDiscoveryDocument discovery)
    {
        NativeClient.Admit(discovery, d => d.Authentication == TargetAuthentication.HostedOauth &&
            d.Oauth is { DeviceGrantType: DeviceGrant, DeviceExchangeFields: { Length: 2 } fields } oauth &&
            fields.Contains("device_token") && fields.Contains("device_label") &&
            d.LoginSession is { Method: "GET", CachePolicy: "no-store" } && OAuthValues.Bounded(oauth.ClientId, 256),
            "OAuth operations require compatible hosted OAuth discovery.");
        var oauth = discovery.Oauth!;
        var origin = client.Origin;
        // The revocation URL is route-validated as native does, though no operation uses it.
        _ = NativeRoutes.SameOriginUrl(origin, oauth.RevocationEndpoint);
        return (NativeRoutes.SameOriginUrl(origin, oauth.MetadataUrl), NativeRoutes.SameOriginUrl(origin, oauth.DeviceAuthorizationEndpoint),
            NativeRoutes.SameOriginUrl(origin, oauth.TokenEndpoint), NativeRoutes.SameOriginPath(origin, discovery.LoginSession!.RouteTemplate));
    }

    private static byte[] Form(params (string Name, string Value)[] pairs) => Encoding.ASCII.GetBytes(NativeRoutes.FormEncode(pairs));
}
