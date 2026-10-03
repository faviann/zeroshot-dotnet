using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Zeroshot.Native;

/// <summary>Validation of HTTPS target certificates against <see cref="TransportOptions.TrustedRootCertificatePath"/>.</summary>
internal static class TrustedRoot
{
    private static readonly Oid ServerAuthentication = new("1.3.6.1.5.5.7.3.1");

    /// <summary>Refuses, before anything is sent, a root that cannot be read.</summary>
    internal static void RequireReadable(string path)
    {
        if (TryRead(path) is not { } root)
            throw new ArgumentException("The trusted root certificate must be a readable PEM certificate.", nameof(TransportOptions.TrustedRootCertificatePath));
        root.Dispose();
    }

    internal static void Apply(SslClientAuthenticationOptions ssl, string path)
        => ssl.RemoteCertificateValidationCallback = (_, certificate, presented, errors) => ChainsTo(path, certificate, presented, errors);

    /// <summary>
    /// One handshake's validation against the root, read now. Any failure other than the system-trust chain
    /// (a host name mismatch, no certificate) refuses; only a chain to exactly that root, for server
    /// authentication, accepts. Revocation is unchecked, as for ordinary TLS. An unreadable root refuses.
    /// </summary>
    private static bool ChainsTo(string path, X509Certificate? certificate, X509Chain? presented, SslPolicyErrors errors)
    {
        if ((errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None || certificate is not X509Certificate2 leaf)
            return false;
        using var root = TryRead(path);
        if (root is null) return false;
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.ApplicationPolicy.Add(ServerAuthentication);
        chain.ChainPolicy.CustomTrustStore.Add(root);
        if (presented is not null) chain.ChainPolicy.ExtraStore.AddRange(presented.ChainPolicy.ExtraStore);
        return chain.Build(leaf);
    }

    private static X509Certificate2? TryRead(string path)
    {
        try { return X509Certificate2.CreateFromPem(File.ReadAllText(path)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException or NotSupportedException)
        { return null; }
    }
}
