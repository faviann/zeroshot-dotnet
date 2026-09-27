using Zeroshot.Native.Contracts;

namespace Zeroshot.Native;

/// <summary>Current caller-supplied control authority. Never stored, refreshed or used as session authority.</summary>
public sealed class TargetControlCredentials
{
    public TargetAuthentication Authentication { get; }
    internal string BearerToken { get; }

    public TargetControlCredentials(TargetAuthentication authentication, string bearerToken)
    {
        if (authentication is not (TargetAuthentication.HostedOauth or TargetAuthentication.PrivateCapability))
            throw new ArgumentException("Control credentials require hosted or private authentication.", nameof(authentication));
        ValidateBearer(bearerToken);
        Authentication = authentication;
        BearerToken = bearerToken;
    }

    internal static void ValidateBearer(string token)
    {
        // Target transport's valid_issued_bearer: 1..16384 ASCII graphic bytes.
        if (string.IsNullOrEmpty(token) || token.Length > 16 * 1024 || token.Any(c => c < '!' || c > '~'))
            throw new ArgumentException("A bearer must contain 1..16384 ASCII graphic bytes.");
    }

    public override string ToString() => nameof(TargetControlCredentials);
}
