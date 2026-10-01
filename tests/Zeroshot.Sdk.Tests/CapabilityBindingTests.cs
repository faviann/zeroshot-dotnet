using System.Net.Http.Headers;
using TUnit.Core;
using Zeroshot.Native;
using Zeroshot.Native.Contracts;

namespace Zeroshot.Client.Tests;

public sealed class CapabilityBindingTests
{
    private static void Check(bool value, string message = "Capability-binding assertion failed.")
    { if (!value) throw new InvalidOperationException(message); }

    // Every HTTP operation, each with arguments its route would refuse. The client's own usability is checked first.
    private static Func<Task>[] InvalidCalls(NativeClient client) =>
    [
        () => client.Target.DiscoverAsync(),
        () => client.Target.CreateOecpSessionAsync(null!),
        () => client.Target.SubmitAttemptAsync((TargetRunRequest)null!),
        () => client.History.ListAsync(null!),
        () => client.History.HeadPageAsync(null!, null!),
        () => client.Connections.ListAsync(null!, null!, null!),
        () => client.Connections.SetAsync(null!, null!, null!),
        () => client.Profiles.ShowAsync(null!, null!, null!),
        () => client.Profiles.RunAsync(null!, null!, null!),
        () => client.HostedRuns.StatusAsync(null!, null!, null!),
        () => client.HostedRuns.WatchAsync(null!, null!, null!),
        () => client.HostedRuns.ForceAsync(null!, null!, null!),
        () => client.HostedRecovery.CheckpointsAsync(null!, null!, null!),
        () => client.HostedRecovery.ResumeAsync(null!, null!, null!, null!),
        () => client.MergePlans.CreateAsync(null!, null!, null!),
        () => client.MergePlans.StatusAsync(null!, null!, null!),
        () => client.OAuth.MetadataAsync(null!),
        () => client.OAuth.RefreshAsync(null!, null!),
        () => client.Private.BootstrapAsync(null!, null!),
        () => client.Private.GetOperatorDiagnosticsAsync(null!, null!),
        () => client.Private.GetHistoryPageAsync(null!, null!),
        () => client.Dashboard.GetAssetAsync("../escape"),
        () => client.Dashboard.HeadAssetAsync(null!),
        () => client.Dashboard.ValidateAsync(null!),
        () => client.Dashboard.SaveProfileAsync(null!, null!),
        () => client.Dashboard.GetHistoryAsync(null!),
        () => client.Dashboard.HeadProfileAsync(null!),
        () => client.Dashboard.OpenRunEventsAsync(null!)
    ];

    [Test]
    public async Task DisposedClientRefusesEveryHttpOperationBeforeItsArguments()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("No request may be sent."));
        var client = NativeClient.ForHttp(new NativeClientOptions { Origin = new Uri("https://target.example/") },
            new HttpClient(handler), ownsHttpClient: true);
        client.Dispose();
        var calls = InvalidCalls(client);
        for (var i = 0; i < calls.Length; i++)
        {
            try { await calls[i](); }
            catch (ObjectDisposedException) { continue; }
            catch (Exception error) { throw new InvalidOperationException($"Call {i} threw {error.GetType().Name} before the disposal check."); }
            throw new InvalidOperationException($"Call {i} did not throw.");
        }
        Check(handler.Calls == 0);
    }

    [Test]
    public async Task DefaultHttpAuthorizationIsRefusedBeforeArgumentsOnEveryOperation()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("No request may be sent."));
        using var http = new HttpClient(handler);
        using var client = NativeClient.ForHttp(new NativeClientOptions { Origin = new Uri("https://target.example/") }, http);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "ambient");
        var calls = InvalidCalls(client);
        for (var i = 0; i < calls.Length; i++)
        {
            try { await calls[i](); }
            catch (ArgumentException error) when (error is not ArgumentNullException &&
                error.Message.StartsWith("Supply credentials per operation", StringComparison.Ordinal)) { continue; }
            catch (Exception error) { throw new InvalidOperationException($"Call {i} threw {error.GetType().Name}: {error.Message}"); }
            throw new InvalidOperationException($"Call {i} did not throw.");
        }
        Check(handler.Calls == 0);
    }
}
