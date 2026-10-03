// Hosted OAuth wire shapes from the pinned native source
// (controller_authority/contract.rs). Every shape uses native snake_case field names.
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zeroshot.Native.Contracts;

/// <summary>Authorization-server metadata. Native reads three endpoints and ignores other fields.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Skip)]
public sealed record OAuthMetadata : TargetHttpContract
{
    [JsonPropertyName("device_authorization_endpoint")]
    public required string DeviceAuthorizationEndpoint { get; init; }
    [JsonPropertyName("token_endpoint")]
    public required string TokenEndpoint { get; init; }
    /// <summary>Advertised only: native has no revocation operation, so none is offered.</summary>
    [JsonPropertyName("revocation_endpoint")]
    public required string RevocationEndpoint { get; init; }
}

/// <summary>A device-authorization grant. The device code is a secret; default formatting omits it.</summary>
public sealed record DeviceAuthorization : TargetHttpContract
{
    [JsonPropertyName("device_code")]
    public required string DeviceCode { get; init; }
    [JsonPropertyName("user_code")]
    public required string UserCode { get; init; }
    [JsonPropertyName("verification_uri")]
    public required string VerificationUri { get; init; }
    [JsonPropertyName("verification_uri_complete")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? VerificationUriComplete { get; init; }
    [JsonPropertyName("expires_in")]
    public required ulong ExpiresIn { get; init; }
    [JsonPropertyName("interval")]
    public required ulong Interval { get; init; }

    // Native validate_device_code. A zero interval is valid.
    internal override void Validate()
    {
        if (!OAuthValues.Bounded(DeviceCode, 16 * 1024) || !OAuthValues.Bounded(UserCode, 256) ||
            ExpiresIn is < 1 or > 86_400 || Interval > 300 ||
            VerificationUrl(VerificationUri, allowQuery: false) is not { } verification ||
            (VerificationUriComplete is not null && (VerificationUrl(VerificationUriComplete, allowQuery: true) is not { } complete ||
                complete.Scheme != verification.Scheme || complete.Host != verification.Host || complete.Port != verification.Port)))
            throw new JsonException();
    }

    // Native safe_verification_url. Its loopback test compares the host text with "::1", which never
    // matches a bracketed IPv6 host, so only 127.0.0.1 may use plain HTTP.
    private static Uri? VerificationUrl(string value, bool allowQuery) =>
        Encoding.UTF8.GetByteCount(value) <= 4096 && !value.Any(c => c <= ' ' || c == '\x7f') &&
        !value.Contains('#') && (allowQuery || !value.Contains('?')) &&
        Uri.TryCreate(value, UriKind.Absolute, out var url) && url.UserInfo.Length == 0 &&
        (url.Scheme == "https" || (url.Scheme == "http" && url.Host == "127.0.0.1")) ? url : null;
}

/// <summary>Tokens issued by either grant. Default formatting omits both token values.</summary>
public sealed record OAuthTokens : TargetHttpContract
{
    [JsonPropertyName("access_token")]
    public required string AccessToken { get; init; }
    [JsonPropertyName("refresh_token")]
    public required string RefreshToken { get; init; }
    [JsonPropertyName("token_type")]
    public required string TokenType { get; init; }
    [JsonPropertyName("expires_in")]
    public required ulong ExpiresIn { get; init; }
    [JsonPropertyName("refresh_expires_in")]
    public required ulong RefreshExpiresIn { get; init; }
    [JsonPropertyName("scope")]
    public required string Scope { get; init; }

    // Native validate_token.
    internal override void Validate()
    {
        if (!OAuthValues.Bounded(AccessToken, 16 * 1024) || !OAuthValues.Bounded(RefreshToken, 16 * 1024) ||
            TokenType != "Bearer" || ExpiresIn is < 1 or > 86_400 || RefreshExpiresIn is < 1 or > 31_536_000 ||
            !OAuthValues.Bounded(Scope, 512))
            throw new JsonException();
    }
}

public sealed record TargetLoginSession : TargetHttpContract
{
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }
    [JsonPropertyName("organization_id")]
    public required string OrganizationId { get; init; }

    internal override void Validate()
    {
        if (Kind != "openengine.target-session/v1" || OrganizationId.Length == 0 || Encoding.UTF8.GetByteCount(OrganizationId) > 256)
            throw new JsonException();
    }
}

/// <summary>The device-token refusals native recognizes. Each means no tokens were issued.</summary>
public enum DeviceTokenError { AuthorizationPending, SlowDown, AccessDenied, ExpiredToken }

// Token-endpoint refusal body: exactly {error}, unlike TargetHttpProblem.
internal sealed record OAuthErrorResponse : TargetHttpContract
{
    [JsonPropertyName("error")]
    public required string Error { get; init; }

    internal DeviceTokenError? Known => Error switch
    {
        "authorization_pending" => DeviceTokenError.AuthorizationPending,
        "slow_down" => DeviceTokenError.SlowDown,
        "access_denied" => DeviceTokenError.AccessDenied,
        "expired_token" => DeviceTokenError.ExpiredToken,
        _ => null
    };
}

internal static class OAuthValues
{
    // Native bounded_value / validate_secret: nonempty, UTF-8 byte bound, no ASCII control bytes.
    internal static bool Bounded(string value, int maxBytes) => value.Length > 0 &&
        Encoding.UTF8.GetByteCount(value) <= maxBytes && !value.Any(c => c < ' ' || c == '\x7f');
}
