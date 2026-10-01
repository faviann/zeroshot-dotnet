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
var stdout = args is ["watch" or "logs" or "attach", ..] ? StreamOutput() : Console.Out;
return await CliApp.RunAsync(args, stdout, Console.Error, interrupted.Token);

// Console.Out silently discards writes once a pipe's reader has gone, which would leave a watch streaming into
// nothing. A stream's stdout pipe is written directly instead, so a closed reader surfaces as an IOException and
// ends the observation. Other commands keep Console.Out: once a mutation is acknowledged, a closed reader must not
// keep --save-run from being written. A terminal or a file keeps Console.Out too; neither has a reader that can go
// away, and a file shared with stderr (2>&1) needs the shared file offset that Console.Out writes through.
static TextWriter StreamOutput()
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
