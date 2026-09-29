using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Zeroshot.Cli;

// The first Ctrl+C cancels the command (detaching observation, never stopping the run); a second one ends the process.
using var interrupted = new CancellationTokenSource();
using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, context =>
{
    context.Cancel = !interrupted.IsCancellationRequested;
    interrupted.Cancel();
});
return await CliApp.RunAsync(args, StandardOutput(), Console.Error, interrupted.Token);

// Console.Out silently discards writes once a pipe's reader has gone, which would leave a watch streaming into
// nothing. Standard output redirected to a pipe is written directly instead, so a closed reader surfaces as an
// IOException. A terminal or a file keeps Console.Out: neither has a reader that can go away.
static TextWriter StandardOutput()
{
    if (!Console.IsOutputRedirected) return Console.Out;
    var handle = new SafeFileHandle(OperatingSystem.IsWindows() ? GetStdHandle(-11) : 1, ownsHandle: false);
    FileStream stream;
    try { stream = new FileStream(handle, FileAccess.Write, bufferSize: 0); }
    catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException) { return Console.Out; }
    if (stream.CanSeek) { stream.Dispose(); return Console.Out; }
    return new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
}

[DllImport("kernel32.dll")]
static extern nint GetStdHandle(int standardHandle);
