using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TUnit.Core;
using Zeroshot.Native;
using Zeroshot.Native.Contracts;

namespace Zeroshot.Client.Tests;

public sealed class HttpDiscoveryTests
{
    private const string Direct = """{"kind":"zeroshot.native-v2-target/v2","authentication":"none","runPath":"/native-v2/run","sessionPath":"/native-v2/oecp-session","oecpPath":"/native-v2/oecp","audience":"controller"}""";
    private static NativeClientOptions Options(TransportOptions? transport = null) => new()
    {
        Origin = new Uri("https://target.example/"), Transport = transport ?? new()
    };
    private static void Check(bool value, string message = "HTTP discovery assertion failed.")
    { if (!value) throw new InvalidOperationException(message); }
    private static async Task<NativeHttpException> Failure(Task task, NativeHttpFailureKind kind)
    {
        try { await task; }
        catch (NativeHttpException e) { Check(e.Kind == kind, e.ToString()); return e; }
        throw new InvalidOperationException("Expected HTTP failure.");
    }
    private static void Invalid(Action action)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("Expected invalid argument.");
    }
    private static HttpResponseMessage Reply(HttpRequestMessage request, string body = Direct, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { RequestMessage = request, Content = new StringContent(body) };

    [Test]
    public async Task CompleteDiscoveryTypesPreserveWireNamesAndIgnoreOnlyUnknownExtensions()
    {
        var bytes = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures/discovery.json"));
        var discovery = NativeJson.DeserializeUtf8<TargetDiscoveryDocument>(bytes);
        Check(discovery.Authentication == TargetAuthentication.HostedOauth && discovery.Audience == "controller");
        Check(discovery.RunPath == "/native-v2/run" && discovery.SessionPath == "/native-v2/oecp-session" && discovery.OecpPath == "/native-v2/oecp");
        Check(discovery.PrivateBootstrapPath == "/native-v2/private-bootstrap");
        Check(discovery.Oauth!.DeviceExchangeFields.SequenceEqual(new[] { "device_token", "device_label" }));
        Check(discovery.LoginSession!.CachePolicy == "no-store");
        var extensions = discovery.Extensions;
        Check(extensions.HostedRuns!.RouteTemplates.Watch == "/runs/{run_id}/watch{?from_cursor}");
        Check(extensions.HostedWorkspaceRecovery!.RouteTemplates.DiscardWorkspace == "/runs/{run_id}/discard");
        Check(extensions.RunHistory!.RouteTemplates.Page == "/runs/{run_id}/page{?after}");
        Check(extensions.Connections!.DynamicKinds.Single() == "custom-provider");
        Check(extensions.RunProfiles!.RouteTemplates.Default == "/default");
        Check(extensions.MergePlans!.RouteTemplates.Status == "/plans/{plan_id}");
        Check(extensions.WorkspaceRecovery!.Kind == "openengine.workspace-recovery/v1");
        Check(extensions.WorkspaceCheckpoints!.Kind == "openengine.workspace-checkpoints/v1");
        var original = JsonNode.Parse(bytes)!;
        original["extensions"]!.AsObject().Remove("future_extension");
        Check(JsonNode.DeepEquals(original, JsonNode.Parse(NativeJson.SerializeUtf8(discovery))));
        Check(NativeJson.DeserializeUtf8<TargetDiscoveryDocument>(Encoding.UTF8.GetBytes(Direct)).Extensions.HostedRuns is null);
        var connections = original["extensions"]!["connections"]!;
        connections.AsObject().Remove("dynamicKinds");
        Check(NativeJson.DeserializeUtf8<TargetDiscoveryDocument>(Encoding.UTF8.GetBytes(original.ToJsonString())).Extensions.Connections!.DynamicKinds.IsEmpty);
        foreach (var mutate in new Action<JsonNode>[]
        {
            x => x["unexpected"] = 1,
            x => x["extensions"]!["hosted_runs"]!["baseUrl"] = "https://wrong.example",
            x => x["extensions"]!["workspace_recovery"]!["unexpected"] = 1,
            x => x["oauth"]!["deviceExchangeFields"] = new JsonArray((JsonNode?)null),
            x => x["authentication"] = "NONE",
            x => x["extensions"] = null,
            x => x.AsObject().Remove("runPath"),
            x => x["runPath"] = null
        })
        {
            var bad = original.DeepClone(); mutate(bad);
            try { NativeJson.DeserializeUtf8<TargetDiscoveryDocument>(Encoding.UTF8.GetBytes(bad.ToJsonString())); }
            catch (JsonException) { continue; }
            throw new InvalidOperationException("Accepted malformed known discovery data.");
        }
    }

    [Test]
    public async Task DiscoveryUsesOneUnauthenticatedGetAndClassifiesFailures()
    {
        foreach (var (body, status, expected) in new[]
        {
            ("{broken", HttpStatusCode.OK, NativeHttpFailureKind.Protocol),
            (Direct.Replace("controller", "foreign"), HttpStatusCode.OK, NativeHttpFailureKind.Protocol),
            (Direct.Replace("zeroshot.native-v2-target/v2", "other"), HttpStatusCode.OK, NativeHttpFailureKind.Protocol),
            ("secret refusal", HttpStatusCode.NotFound, NativeHttpFailureKind.HttpStatus),
            ("", HttpStatusCode.Redirect, NativeHttpFailureKind.Redirect)
        })
        {
            using var handler = new Handler((request, _) =>
            {
                Check(request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/.well-known/zeroshot-native-v2");
                Check(request.Content is null && request.Headers.Authorization is null);
                return Task.FromResult(Reply(request, body, status));
            });
            using var http = new HttpClient(handler);
            using var native = NativeClient.ForHttp(Options(), http);
            var error = await Failure(native.Target.DiscoverAsync(), expected);
            Check(handler.Calls == 1 && error.CorrelationId != Guid.Empty && !error.ToString().Contains("secret"));
            if (expected == NativeHttpFailureKind.HttpStatus) Check(error.StatusCode == HttpStatusCode.NotFound);
            Check(error.ExportRawDiagnostic() is null);
        }
    }

    [Test]
    public async Task BoundedSuccessAndDiagnosticBodiesAndExplicitDiagnosticInspection()
    {
        foreach (var declaredLength in new long?[] { null, 0, 999999 })
        {
            var stream = new CountingStream(Encoding.UTF8.GetBytes(Direct));
            using var handler = new Handler((request, _) =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StreamContent(stream) };
                response.Content.Headers.ContentLength = declaredLength;
                return Task.FromResult(response);
            });
            using var http = new HttpClient(handler);
            using var native = NativeClient.ForHttp(Options(new() { MaxResponseBytes = 24 }), http);
            await Failure(native.Target.DiscoverAsync(), NativeHttpFailureKind.SizeLimit);
            Check(stream.BytesRead == 25);
        }
        using var errorHandler = new Handler((request, _) => Task.FromResult(Reply(request, "sensitive", HttpStatusCode.NotFound)));
        using var errorHttp = new HttpClient(errorHandler);
        using var small = NativeClient.ForHttp(Options(new() { MaxErrorBodyBytes = 4 }), errorHttp);
        await Failure(small.Target.DiscoverAsync(), NativeHttpFailureKind.SizeLimit);
        using var capture = NativeClient.ForHttp(Options(new() { CaptureRawDiagnostics = true }), errorHttp);
        var failure = await Failure(capture.Target.DiscoverAsync(), NativeHttpFailureKind.HttpStatus);
        Check(Encoding.UTF8.GetString(failure.ExportRawDiagnostic()!) == "sensitive" && !failure.ToString().Contains("sensitive"));
        failure.ExportRawDiagnostic()![0] = 0;
        Check(failure.ExportRawDiagnostic()![0] == (byte)'s');
    }

    [Test]
    public void InvalidConfigurationFailsWithoutContact()
    {
        using var handler = new Handler((request, _) => Task.FromResult(Reply(request)));
        using var http = new HttpClient(handler);
        foreach (var uri in new[] { "http://example.com", "http://localhost", "http://127.0.0.2", "http://127.1", "http://2130706433", "ftp://target.example", "https://u:p@target.example", "https://@target.example", "https://target.example/path", "https://target.example/../", "https://target.example/?", "https://target.example/#", "https://target.example/\\path", "/relative" })
            Invalid(() => NativeClient.ForHttp(Options() with { Origin = new Uri(uri, UriKind.RelativeOrAbsolute) }, http));
        Invalid(() => NativeClient.ForHttp(Options(new() { MaxResponseBytes = 0 }), http));
        Invalid(() => NativeClient.ForHttp(Options(new() { RequestTimeout = TimeSpan.Zero }), http));
        using var impatient = new HttpClient(handler, false) { Timeout = TimeSpan.FromSeconds(1) };
        Invalid(() => NativeClient.ForHttp(Options(), impatient));
        Check(handler.Calls == 0);
        foreach (var uri in new[] { "http://127.0.0.1:8080/", "http://[::1]:8080/", "https://target.example/" })
            using (NativeClient.ForHttp(Options() with { Origin = new Uri(uri) }, http)) { }
    }

    [Test]
    public async Task BorrowedClientSurvivesDisposalWhichCancelsOwnedWork()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath != "/still-usable")
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            return Reply(request);
        });
        using var http = new HttpClient(handler);
        var native = NativeClient.ForHttp(Options(), http);
        var pending = native.Target.DiscoverAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await native.DisposeAsync();
        try { await pending; throw new InvalidOperationException("Expected cancellation."); }
        catch (OperationCanceledException) { }
        Check(!handler.Disposed);
        using var usable = await http.GetAsync("https://target.example/still-usable");
        try { await native.Target.DiscoverAsync(); throw new InvalidOperationException("Expected disposed client."); }
        catch (ObjectDisposedException) { }
        using var ownedHandler = new Handler((request, _) => Task.FromResult(Reply(request)));
        var owned = new HttpClient(ownedHandler);
        await NativeClient.ForHttp(Options(), owned, ownsHttpClient: true).DisposeAsync();
        Check(ownedHandler.Disposed);
    }

    [Test]
    public async Task WholeBodyDeadlineAndImmediateRequestCapacityAreEnforced()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler((request, _) =>
        {
            started.TrySetResult();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { RequestMessage = request, Content = new StreamContent(new WaitingStream()) });
        });
        using var http = new HttpClient(handler);
        using var native = NativeClient.ForHttp(Options(new()
        {
            RequestTimeout = TimeSpan.FromMilliseconds(250), MaxConcurrentRequests = 2, ReservedControlRequests = 1
        }), http);
        var first = native.Target.DiscoverAsync();
        await started.Task;
        await Failure(native.Target.DiscoverAsync(), NativeHttpFailureKind.Capacity);
        await Failure(first, NativeHttpFailureKind.Deadline);
        Check(handler.Calls == 1);
    }

    [Test]
    public async Task ResponseCleanupFailureDoesNotEraseValidatedDiscovery()
    {
        using var handler = new Handler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request, Content = new StreamContent(new FailingDisposeStream(Encoding.UTF8.GetBytes(Direct)))
        }));
        using var http = new HttpClient(handler);
        using var native = NativeClient.ForHttp(Options(), http);
        Check((await native.Target.DiscoverAsync()).Authentication == TargetAuthentication.None);
    }

    [Test]
    public async Task ChangedResponseUrlIsRejectedEvenForOpaqueHandlers()
    {
        using var handler = new Handler((request, _) =>
        {
            request.RequestUri = new Uri("http://127.0.0.1/");
            return Task.FromResult(Reply(request));
        });
        using var http = new HttpClient(handler);
        using var native = NativeClient.ForHttp(Options(), http);
        await Failure(native.Target.DiscoverAsync(), NativeHttpFailureKind.Redirect);
    }

    [Test]
    public async Task DefaultAndSupportedHandlersRejectRedirectsBeforeFollowing()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var origin = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync();
            using var stream = socket.GetStream();
            var buffer = new byte[4096];
            Check(await stream.ReadAsync(buffer) > 0);
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 302 Found\r\nLocation: {origin}followed\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
        });
        using var native = NativeClient.ForHttp(new() { Origin = origin });
        await Failure(native.Target.DiscoverAsync(), NativeHttpFailureKind.Redirect);
        await server.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!listener.Pending());
        using var handler = NativeClient.CreateHttpHandler(new() { MaxHttpConnectionsPerOrigin = 3, ConnectTimeout = TimeSpan.FromSeconds(2) });
        Check(!handler.AllowAutoRedirect && handler.MaxConnectionsPerServer == 3 && handler.ConnectTimeout == TimeSpan.FromSeconds(2));
        Check(handler.SslOptions.RemoteCertificateValidationCallback is null && handler.ConnectCallback is null);
    }

    [Test]
    public async Task DefaultHttpsRejectsUntrustedCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(deadline.Token);
            using var tls = new SslStream(socket.GetStream());
            try { await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, deadline.Token); }
            catch (System.Security.Authentication.AuthenticationException) { }
            catch (IOException) { }
        });
        using var native = NativeClient.ForHttp(new() { Origin = new Uri($"https://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/") });
        await Failure(native.Target.DiscoverAsync(deadline.Token), NativeHttpFailureKind.Transport);
        await server;
    }

    [Test]
    public async Task TlsHandshakeUsesTheConnectDeadline()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var native = NativeClient.ForHttp(new()
        {
            Origin = new Uri($"https://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/"),
            Transport = new() { ConnectTimeout = TimeSpan.FromMilliseconds(150), RequestTimeout = TimeSpan.FromSeconds(5) }
        });
        var request = native.Target.DiscoverAsync();
        using var socket = await listener.AcceptTcpClientAsync(); // Deliberately never answer the TLS handshake.
        var failure = await Failure(request, NativeHttpFailureKind.Deadline);
        Check(failure.Stage == "Connect");
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public bool Disposed { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return send(request, cancellationToken); }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class CountingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public int BytesRead { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { var count = await base.ReadAsync(buffer, cancellationToken); BytesRead += count; return count; }
    }
    private sealed class WaitingStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
    }
    private sealed class FailingDisposeStream(byte[] bytes) : MemoryStream(bytes)
    {
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            throw new IOException("Untrusted cleanup detail.");
        }
    }
}
