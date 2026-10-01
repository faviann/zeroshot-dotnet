using System.Net;
using System.Net.Sockets;
using TUnit.Core;
using Zeroshot.Native;
using Zeroshot.Native.Contracts;
using Zeroshot.Native.Execution;
using static Zeroshot.Client.Tests.SubscriptionContractTests;

namespace Zeroshot.Client.Tests;

public sealed class OecpConnectTests
{
    private static readonly OperationDescriptor Dial = new("oecp.test", OperationTransport.Oecp);

    private static async Task<T> Throws<T>(Task task) where T : Exception
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (T error) { return error; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static bool Disposed(StandaloneOecpHost host)
    {
        try { host.Executor.RegisterOecpConnection(Dial).Dispose(); return false; }
        catch (ObjectDisposedException) { }
        try { host.Observations.Open<int, string>().Dispose(); return false; }
        catch (ObjectDisposedException) { return true; }
    }

    [Test]
    public async Task FailedOrRefusedStandaloneConnectReleasesWhatItCreated()
    {
        var host = StandaloneOecpHost.For(null);
        var socket = new Probe();
        var failure = await Throws<NativeOecpException>(OecpConnect.ConnectAsync(host, Dial, (_, resources) =>
        {
            resources.Own(socket);
            throw new IOException("refused");
        }, default));
        Check(failure.Kind == NativeOecpFailureKind.Transport && socket.Disposed && Disposed(host),
            "A failed dial releases its resources and the budgets created for it.");

        host = StandaloneOecpHost.For(null);
        socket = new Probe();
        var refused = await OecpConnect.ConnectAsync(host, Dial, (_, resources) =>
        {
            resources.Own(socket);
            return Task.FromResult<(Uri?, IOecpTransport)?>(null);
        }, default);
        Check(refused is null && socket.Disposed && Disposed(host), "A refused endpoint is released like a failure.");
    }

    [Test]
    public async Task ResourceCreatedByADialAbandonedAtItsDeadlineIsDisposed()
    {
        var host = StandaloneOecpHost.For(new TransportOptions { RequestTimeout = TimeSpan.FromMilliseconds(50) });
        var late = new Probe();
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var owned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connect = OecpConnect.ConnectAsync(host, Dial, async (_, resources) =>
        {
            await resume.Task;
            resources.Own(late);
            owned.SetResult();
            return null;
        }, default);
        Check((await Throws<NativeOecpException>(connect)).Kind == NativeOecpFailureKind.Deadline);
        resume.SetResult();
        await owned.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(late.Disposed, "Cleanup already ran, so a late resource cannot leak.");
    }

    [Test]
    public async Task ClientConnectFailureKeepsTheSharedBudgets()
    {
        // Accepting and closing fails the WebSocket handshake promptly on every platform; a bound but
        // non-listening port is refused on Linux but left unanswered on macOS.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var stop = new CancellationTokenSource();
        var closing = Task.Run(async () =>
        {
            try { while (true) (await listener.AcceptTcpClientAsync(stop.Token)).Dispose(); }
            catch (Exception) when (stop.IsCancellationRequested) { } // Stop() can fault a pending accept instead of cancelling it.
        });
        try
        {
            var origin = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}");
            using var client = NativeClient.ForHttp(new NativeClientOptions { Origin = origin });
            var session = new TargetOecpSession { Endpoint = new UriBuilder(origin) { Scheme = "ws", Path = "/native-v2/oecp" }.Uri.AbsoluteUri };
            for (var attempt = 0; attempt < 2; attempt++)
                Check((await Throws<NativeOecpException>(client.ConnectOecpAsync(session))).Kind == NativeOecpFailureKind.Transport,
                    "A failed connect leaves the client's executor usable.");
            client.Observations.Open<int, string>().Dispose();
        }
        finally
        {
            stop.Cancel();
            listener.Stop();
            await closing;
        }
    }

    private sealed class Probe : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
}
