using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using TUnit.Core;
using Zeroshot.Native;
using Zeroshot.Native.Contracts;

namespace Zeroshot.Client.Tests;

public sealed class TrustedRootTests
{
    [Test]
    public async Task HttpAndOecpAcceptOnlyATargetThatChainsToTheTrustedRoot()
    {
        using var root = Root("CN=Trusted Root");
        using var other = Root("CN=Other Root");
        await using var target = new TlsTarget(root);
        using var trusted = new RootFile(root);
        using var untrusted = new RootFile(other);

        foreach (var (path, admitted) in new[] { (trusted.Path, true), ((string?)null, false), (untrusted.Path, false) })
        {
            using var client = target.Client(path);
            // The target answers HTTP with 404, so HttpStatus proves the TLS handshake passed.
            await Failure(client.Target.DiscoverAsync(), admitted ? NativeHttpFailureKind.HttpStatus : NativeHttpFailureKind.Transport);
            if (admitted) await (await client.ConnectOecpAsync(target.Session)).DisposeAsync();
            else await Failure(client.ConnectOecpAsync(target.Session), NativeOecpFailureKind.Transport);
        }
    }

    [Test]
    public async Task EachNewConnectionRereadsTheRoot()
    {
        using var root = Root("CN=Trusted Root");
        using var other = Root("CN=Other Root");
        await using var target = new TlsTarget(root);
        using var file = new RootFile(other);
        using var client = target.Client(file.Path);
        await Failure(client.Target.DiscoverAsync(), NativeHttpFailureKind.Transport);
        file.Write(root);
        await Failure(client.Target.DiscoverAsync(), NativeHttpFailureKind.HttpStatus);
    }

    [Test]
    public async Task TheTrustedRootStillRequiresTheHostNameToMatch()
    {
        using var root = Root("CN=Trusted Root");
        await using var target = new TlsTarget(root, hostName: "other.example");
        using var file = new RootFile(root);
        using var client = target.Client(file.Path);
        await Failure(client.Target.DiscoverAsync(), NativeHttpFailureKind.Transport);
    }

    [Test]
    public void CreationRefusesAnUnusableRootOrASuppliedClientButIgnoresLoopbackHttp()
    {
        var https = new Uri("https://target.example/");
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".pem");
        using var invalid = new RootFile(null);
        foreach (var path in new[] { missing, invalid.Path })
            Check(Throws(() => NativeClient.ForHttp(Options(https, path))).ParamName == nameof(TransportOptions.TrustedRootCertificatePath));

        using var root = Root("CN=Trusted Root");
        using var readable = new RootFile(root);
        using var http = new HttpClient(NativeClient.CreateHttpHandler()) { Timeout = Timeout.InfiniteTimeSpan };
        Check(Throws(() => NativeClient.ForHttp(Options(https, readable.Path), http)).ParamName == nameof(TransportOptions.TrustedRootCertificatePath));

        NativeClient.ForHttp(Options(new Uri("http://127.0.0.1:1/"), missing)).Dispose();
    }

    private static NativeClientOptions Options(Uri origin, string path) => new() { Origin = origin, Transport = new() { TrustedRootCertificatePath = path } };

    private static ArgumentException Throws(Action create)
    {
        try { create(); }
        catch (ArgumentException error) { return error; }
        throw new InvalidOperationException("Expected ArgumentException.");
    }

    private static X509Certificate2 Root(string name)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(10));
    }

    private static X509Certificate2 Intermediate(X509Certificate2 root)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Intermediate", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        using var issued = request.Create(root, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(8), [5, 6, 7, 8]);
        return issued.CopyWithPrivateKey(key);
    }

    private sealed class RootFile : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".pem");
        public RootFile(X509Certificate2? root) { if (root is null) File.WriteAllText(Path, "not a certificate"); else Write(root); }
        public void Write(X509Certificate2 root) => File.WriteAllText(Path, root.ExportCertificatePem());
        public void Dispose() => File.Delete(Path);
    }

    /// <summary>A loopback TLS target whose certificate an intermediate of the root issued, as Caddy's internal CA does. It answers a WebSocket upgrade with 101 and any other request with 404.</summary>
    private sealed class TlsTarget : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly X509Certificate2 certificate;
        private readonly SslStreamCertificateContext chain;
        private readonly Task serving;
        private readonly Uri origin;

        public TlsTarget(X509Certificate2 root, string? hostName = null)
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=" + (hostName ?? "127.0.0.1"), key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder();
            if (hostName is null) names.AddIpAddress(IPAddress.Loopback);
            else names.AddDnsName(hostName);
            request.CertificateExtensions.Add(names.Build());
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
            using var intermediate = Intermediate(root);
            using var issued = request.Create(intermediate, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(5), [1, 2, 3, 4]);
            using var withKey = issued.CopyWithPrivateKey(key);
            // Windows TLS (SChannel) cannot serve an ephemeral private key; a PKCS#12 round trip gives it a usable one.
            certificate = X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), null);
            chain = SslStreamCertificateContext.Create(certificate, [X509CertificateLoader.LoadCertificate(intermediate.RawData)], offline: true);
            listener.Start();
            origin = new Uri($"https://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");
            serving = Serve();
        }

        public TargetOecpSession Session => new() { Endpoint = new UriBuilder(origin) { Scheme = "wss", Path = "/native-v2/oecp" }.Uri.AbsoluteUri };

        public NativeClient Client(string? root) => NativeClient.ForHttp(new() { Origin = origin, Transport = new() { TrustedRootCertificatePath = root } });

        private async Task Serve()
        {
            var connections = new List<Task>();
            try { while (true) connections.Add(Answer(await listener.AcceptTcpClientAsync(stop.Token))); }
            catch (Exception) when (stop.IsCancellationRequested) { }
            await Task.WhenAll(connections);
        }

        private async Task Answer(TcpClient socket)
        {
            using var _ = socket;
            await using var tls = new SslStream(socket.GetStream());
            try
            {
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificateContext = chain }, stop.Token);
                var header = new List<byte>();
                var one = new byte[1];
                while (!Encoding.ASCII.GetString(header.ToArray()).EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    if (await tls.ReadAsync(one, stop.Token) == 0) return;
                    header.Add(one[0]);
                }
                var key = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n")
                    .SingleOrDefault(line => line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))?.Split(':', 2)[1].Trim();
                if (key is null)
                {
                    await tls.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), stop.Token);
                    return;
                }
                var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                await tls.WriteAsync(Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"), stop.Token);
                await Task.Delay(Timeout.Infinite, stop.Token);
            }
            catch (Exception error) when (error is System.Security.Authentication.AuthenticationException or IOException or OperationCanceledException) { }
        }

        public async ValueTask DisposeAsync()
        {
            stop.Cancel();
            listener.Stop();
            await serving;
            certificate.Dispose();
            stop.Dispose();
        }
    }
}
