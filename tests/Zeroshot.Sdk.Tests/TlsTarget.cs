using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Zeroshot.Native.Contracts;

namespace Zeroshot.Client.Tests;

/// <summary>
/// A loopback HTTPS peer whose certificate an intermediate of <see cref="Root"/> issued, as Caddy's internal CA does.
/// Each request is recorded in <see cref="Requests"/> before it is answered with the raw response from
/// <see cref="Respond"/>; a 101 response keeps the connection open until disposal.
/// </summary>
internal sealed class TlsTarget : IAsyncDisposable
{
    public const string NotFound = "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
    public sealed record Request(string Line, IReadOnlyList<string> Headers, string Body);

    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly List<Request> requests = new();
    private readonly RootFile rootFile;
    private readonly X509Certificate2 certificate;
    private readonly X509Certificate2 intermediate;
    private readonly SslStreamCertificateContext chain;
    private readonly Task serving;

    /// <summary>The certificate names 127.0.0.1 unless <paramref name="hostName"/> says otherwise.</summary>
    public TlsTarget(string? hostName = null)
    {
        Root = CreateRoot("CN=Test Root");
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=" + (hostName ?? "127.0.0.1"), key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        if (hostName is null) names.AddIpAddress(IPAddress.Loopback);
        else names.AddDnsName(hostName);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var issuer = Intermediate(Root);
        using var issued = request.Create(issuer, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(5), [1, 2, 3, 4]);
        using var withKey = issued.CopyWithPrivateKey(key);
        // Windows TLS (SChannel) cannot serve an ephemeral private key; a PKCS#12 round trip gives it a usable one.
        certificate = X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), null);
        intermediate = X509CertificateLoader.LoadCertificate(issuer.RawData);
        chain = SslStreamCertificateContext.Create(certificate, [intermediate], offline: true);
        rootFile = new RootFile(Root);
        listener.Start();
        Origin = new Uri($"https://127.0.0.1:{Port}/");
        serving = Serve();
    }

    public X509Certificate2 Root { get; }
    /// <summary>A PEM file holding <see cref="Root"/>, for <c>TransportOptions.TrustedRootCertificatePath</c>.</summary>
    public string RootPath => rootFile.Path;
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public Uri Origin { get; }
    public TargetOecpSession Session => new() { Endpoint = new UriBuilder(Origin) { Scheme = "wss", Path = "/native-v2/oecp" }.Uri.AbsoluteUri };
    public Func<Request, string> Respond { get; set; } = _ => NotFound;
    public IReadOnlyList<Request> Requests { get { lock (requests) return requests.ToList(); } }

    /// <summary>Answers a WebSocket upgrade with 101 and anything else with 404.</summary>
    public static string UpgradeOrNotFound(Request request)
    {
        var key = request.Headers.SingleOrDefault(h => h.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))?.Split(':', 2)[1].Trim();
        if (key is null) return NotFound;
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        return $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n";
    }

    public static X509Certificate2 CreateRoot(string name)
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
        var request = new CertificateRequest("CN=Test Intermediate", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        using var issued = request.Create(root, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(8), [5, 6, 7, 8]);
        return issued.CopyWithPrivateKey(key);
    }

    private async Task Serve()
    {
        var answering = new List<Task>();
        try
        {
            while (true) answering.Add(Answer(await listener.AcceptTcpClientAsync(stop.Token)));
        }
        catch (Exception) when (stop.IsCancellationRequested) { } // Stop() can fault a pending accept instead of cancelling it.
        await Task.WhenAll(answering);
    }

    private async Task Answer(TcpClient socket)
    {
        using var _ = socket;
        await using var tls = new SslStream(socket.GetStream());
        try
        {
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificateContext = chain }, stop.Token);
            using var reader = new StreamReader(tls, Encoding.ASCII, leaveOpen: true);
            // A client that refused the certificate can still let the server's handshake finish, then close.
            if (await reader.ReadLineAsync(stop.Token) is not { } line) return;
            var headers = new List<string>();
            while (await reader.ReadLineAsync(stop.Token) is { Length: > 0 } header) headers.Add(header);
            var length = headers.SingleOrDefault(h => h.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) is { } declared ? int.Parse(declared[15..]) : 0;
            var body = new char[length];
            // An empty read still waits for stream data, which a request without a body never sends.
            if (length > 0) await reader.ReadBlockAsync(body, stop.Token);
            var request = new Request(line, headers, new string(body));
            lock (requests) requests.Add(request);
            var response = Respond(request);
            await tls.WriteAsync(Encoding.ASCII.GetBytes(response), stop.Token);
            if (response.StartsWith("HTTP/1.1 101", StringComparison.Ordinal)) await Task.Delay(Timeout.Infinite, stop.Token);
        }
        catch (Exception error) when (error is System.Security.Authentication.AuthenticationException or IOException or OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        listener.Stop();
        await serving;
        rootFile.Dispose();
        certificate.Dispose();
        intermediate.Dispose();
        Root.Dispose();
        stop.Dispose();
    }
}

/// <summary>A temporary PEM file holding a root.</summary>
internal sealed class RootFile : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".pem");
    public RootFile(X509Certificate2 root) => Write(root);
    public void Write(X509Certificate2 root) => File.WriteAllText(Path, root.ExportCertificatePem());
    public void Dispose() => File.Delete(Path);
}
