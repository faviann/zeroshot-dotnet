using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using TUnit.Core;
using TUnit.Core.Enums;
using Zeroshot.Native;
using static Zeroshot.Client.Tests.SubscriptionContractTests;

namespace Zeroshot.Client.Tests;

/// <summary>Controlled local pipes with explicit descriptors; the native controller witness is separate.</summary>
[RunOn(OS.Windows)]
[SupportedOSPlatform("windows")]
public sealed class NamedPipeConnectionTests
{
    // Native's own controller descriptor: owned by the user, allowing only the user and SYSTEM.
    private const string Private = "O:{me}D:P(A;;FA;;;{me})(A;;FA;;;SY)";
    private const string Status = """{"runId":"run-1","title":"t","source":{"repository":"a/b","branch":"main","revision":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"},"size":"small","atCursor":"c","status":{"phase":"stopping","activeExecutions":[]}}""";

    private static string NewPath() => @"\\.\pipe\zeroshot-test-" + Guid.NewGuid().ToString("N");
    private static long Id(string line) => JsonDocument.Parse(line).RootElement.GetProperty("id").GetInt64();
    private static byte[] Reply(long id, string result) => Encoding.UTF8.GetBytes($$"""{"jsonrpc":"2.0","id":{{id}},"result":{{result}}}""" + "\n");

    private static async Task<T> Throws<T>(Task task) where T : Exception
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (T error) { return error; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    [Test]
    public async Task PrivatePipeCarriesOecpUntilTheControllerDisconnects()
    {
        var path = NewPath();
        await using var server = Server(path, Private);
        var accepted = server.WaitForConnectionAsync();
        await using var connection = await OecpConnection.ConnectNamedPipeAsync(path);
        await accepted;
        // Identification-only quality of service: the controller can identify, never impersonate, the caller.
        var level = TokenImpersonationLevel.None;
        server.RunAsClient(() => { using var client = WindowsIdentity.GetCurrent(true)!; level = client.ImpersonationLevel; });
        Check(level == TokenImpersonationLevel.Identification, "The controller cannot impersonate the caller.");
        using var reader = new StreamReader(server, leaveOpen: true);

        var force = connection.Runs.ForceAsync(new("run-1"));
        var line = (await reader.ReadLineAsync())!;
        Check(line.Contains("\"method\":\"run/force\""));
        await server.WriteAsync(Reply(Id(line), Status));
        var attempt = await force;
        Check(attempt is { Outcome: NativeAttemptOutcome.Acknowledged, Origin: { Scheme: "file", Host: "" } origin } &&
            Uri.UnescapeDataString(origin.AbsolutePath) == "/" + path, "The origin identifies exactly the supplied pipe.");

        var pending = connection.Runs.ListAsync();
        await reader.ReadLineAsync();
        server.Disconnect();
        Check((await Throws<NativeOecpException>(pending)).Kind == NativeOecpFailureKind.Transport);
        Check((await connection.Completion)!.Kind == NativeOecpFailureKind.Transport, "A disconnect is never a completion.");
    }

    // One row per native rule. Each refusal is the caller-visible security exception, never a
    // Transport failure, and happens before a byte is written.
    [Test]
    [Arguments("O:BAD:P(A;;FA;;;{me})(A;;FA;;;SY)", true)] // owner is not the process user
    [Arguments("O:{me}D:NO_ACCESS_CONTROL", true)] // null DACL
    [Arguments("O:{me}D:P(A;;FA;;;{me})(A;;FA;;;SY)(A;;GR;;;WD)", true)] // another trustee is allowed
    [Arguments("O:{me}D:P(D;;WDWO;;;{me})(A;;FA;;;{me})(A;;FA;;;SY)", true)] // an ACE that is not access-allowed
    [Arguments("O:{me}D:P(A;;FA;;;SY)", false)] // the pipe denies this user
    public async Task PipeThatIsNotPrivateToThisUserIsRefusedBeforeAnythingIsSent(string descriptor, bool opens)
    {
        Skip.When(descriptor.StartsWith("O:BA") && !new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator),
            "Assigning an Administrators owner requires an elevated process.");
        var path = NewPath();
        await using var server = Server(path, descriptor);
        var accepted = server.WaitForConnectionAsync();
        await Throws<UnauthorizedAccessException>(OecpConnection.ConnectNamedPipeAsync(path));
        if (opens)
        {
            await accepted;
            Check(await server.ReadAsync(new byte[1]) == 0, "The refused pipe is closed without a request.");
        }
    }

    [Test]
    public async Task OwnedPipeIsClosedOnDisposalAndABorrowedPipeStaysOpen()
    {
        var ownedPath = NewPath();
        await using (var owned = Server(ownedPath, Private))
        {
            var accepted = owned.WaitForConnectionAsync();
            var connection = await OecpConnection.ConnectNamedPipeAsync(ownedPath);
            await accepted;
            await connection.DisposeAsync();
            Check(await owned.ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(5)) == 0, "The owned pipe is closed.");
        }

        var borrowedPath = NewPath();
        await using var server = Server(borrowedPath, Private);
        var serverAccepted = server.WaitForConnectionAsync();
        await using var pipe = new NamedPipeClientStream(".", borrowedPath[@"\\.\pipe\".Length..], PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000);
        await serverAccepted;
        using var reader = new StreamReader(server, leaveOpen: true);
        await (await OecpConnection.FromStreamsAsync(pipe, pipe)).DisposeAsync();
        Check(pipe.IsConnected, "A borrowed pipe stays open after its connection is disposed.");
        await using var second = await OecpConnection.FromStreamsAsync(pipe, pipe);
        var list = second.Runs.ListAsync();
        await server.WriteAsync(Reply(Id((await reader.ReadLineAsync())!), """{"runs":[]}"""));
        Check((await list).Runs.Length == 0, "A later connection reuses the borrowed pipe.");
    }

    [Test]
    public async Task OnlyExactLocalPipesAreAcceptedAndConnectingIsCancellableAndBounded()
    {
        await Throws<ArgumentException>(OecpConnection.ConnectNamedPipeAsync(@"\\remote-host\pipe\zeroshot-test"));
        await Throws<ArgumentException>(OecpConnection.ConnectNamedPipeAsync(@"\\.\pipe\zeroshot/test"));

        // .NET waits for a missing or busy instance until ConnectTimeout (Deadline); native's client fails immediately.
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Throws<OperationCanceledException>(OecpConnection.ConnectNamedPipeAsync(NewPath(), cancellationToken: cancel.Token));
        var expired = await Throws<NativeOecpException>(OecpConnection.ConnectNamedPipeAsync(NewPath(),
            new TransportOptions { ConnectTimeout = TimeSpan.FromMilliseconds(200) }));
        Check(expired.Kind == NativeOecpFailureKind.Deadline);
    }

    private static NamedPipeServerStream Server(string path, string descriptor)
    {
        var sddl = descriptor.Replace("{me}", WindowsIdentity.GetCurrent().User!.Value);
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out var memory, 0))
            throw new InvalidOperationException("Invalid test descriptor.", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = memory };
            // Duplex, overlapped, first instance; byte mode rejecting remote clients, as native creates its pipe.
            var handle = CreateNamedPipeW(path, 0x3 | 0x40000000 | 0x80000, 0x8, 1, 65536, 65536, 0, ref attributes);
            if (handle.IsInvalid) throw new InvalidOperationException("Could not create the test pipe.", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
            return new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: false, handle);
        }
        finally { LocalFree(memory); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { public int Length; public nint Descriptor; public int InheritHandle; }

    [DllImport("advapi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision, out nint descriptor, nint size);

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateNamedPipeW(string name, uint openMode, uint pipeMode, uint maxInstances,
        uint outBuffer, uint inBuffer, uint timeout, ref SecurityAttributes attributes);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nint LocalFree(nint memory);
}
