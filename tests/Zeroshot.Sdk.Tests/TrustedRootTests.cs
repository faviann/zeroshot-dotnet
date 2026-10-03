using TUnit.Core;
using Zeroshot.Native;

namespace Zeroshot.Client.Tests;

public sealed class TrustedRootTests
{
    [Test]
    public async Task HttpAndOecpAcceptOnlyATargetThatChainsToTheTrustedRoot()
    {
        await using var target = new TlsTarget { Respond = TlsTarget.UpgradeOrNotFound };
        using var other = TlsTarget.CreateRoot("CN=Other Root");
        using var untrusted = new RootFile(other);

        foreach (var (path, admitted) in new[] { (target.RootPath, true), ((string?)null, false), (untrusted.Path, false) })
        {
            using var client = Client(target, path);
            // The target answers HTTP with 404, so HttpStatus proves the TLS handshake passed.
            await Failure(client.Target.DiscoverAsync(), admitted ? NativeHttpFailureKind.HttpStatus : NativeHttpFailureKind.Transport);
            if (admitted) await (await client.ConnectOecpAsync(target.Session)).DisposeAsync();
            else await Failure(client.ConnectOecpAsync(target.Session), NativeOecpFailureKind.Transport);
        }
    }

    [Test]
    public async Task EachNewConnectionRereadsTheRoot()
    {
        await using var target = new TlsTarget();
        using var other = TlsTarget.CreateRoot("CN=Other Root");
        using var file = new RootFile(other);
        using var client = Client(target, file.Path);
        await Failure(client.Target.DiscoverAsync(), NativeHttpFailureKind.Transport);
        file.Write(target.Root);
        await Failure(client.Target.DiscoverAsync(), NativeHttpFailureKind.HttpStatus);
    }

    [Test]
    public async Task TheTrustedRootStillRequiresTheHostNameToMatch()
    {
        await using var target = new TlsTarget(hostName: "other.example");
        using var client = Client(target, target.RootPath);
        await Failure(client.Target.DiscoverAsync(), NativeHttpFailureKind.Transport);
    }

    [Test]
    public void CreationRefusesAnUnusableRootOrASuppliedClientButIgnoresLoopbackHttp()
    {
        var https = new Uri("https://target.example/");
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".pem");
        using var root = TlsTarget.CreateRoot("CN=Test Root");
        using var readable = new RootFile(root);
        File.WriteAllText(readable.Path, "not a certificate");
        Check(Throws(() => NativeClient.ForHttp(Options(https, readable.Path))).ParamName == nameof(TransportOptions.TrustedRootCertificatePath));
        Check(Throws(() => NativeClient.ForHttp(Options(https, missing))).ParamName == nameof(TransportOptions.TrustedRootCertificatePath));

        readable.Write(root);
        using var http = new HttpClient(NativeClient.CreateHttpHandler()) { Timeout = Timeout.InfiniteTimeSpan };
        Check(Throws(() => NativeClient.ForHttp(Options(https, readable.Path), http)).ParamName == nameof(TransportOptions.TrustedRootCertificatePath));

        NativeClient.ForHttp(Options(new Uri("http://127.0.0.1:1/"), missing)).Dispose();
    }

    private static NativeClient Client(TlsTarget target, string? root)
        => NativeClient.ForHttp(new() { Origin = target.Origin, Transport = new() { TrustedRootCertificatePath = root } });

    private static NativeClientOptions Options(Uri origin, string path) => new() { Origin = origin, Transport = new() { TrustedRootCertificatePath = path } };

    private static ArgumentException Throws(Action create)
    {
        try { create(); }
        catch (ArgumentException error) { return error; }
        throw new InvalidOperationException("Expected ArgumentException.");
    }
}
