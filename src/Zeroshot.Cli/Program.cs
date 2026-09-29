using System.Runtime.InteropServices;
using Zeroshot.Cli;

// The first Ctrl+C cancels the command (detaching observation, never stopping the run); a second one ends the process.
using var interrupted = new CancellationTokenSource();
using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, context =>
{
    context.Cancel = !interrupted.IsCancellationRequested;
    interrupted.Cancel();
});
return await CliApp.RunAsync(args, Console.Out, Console.Error, interrupted.Token);
