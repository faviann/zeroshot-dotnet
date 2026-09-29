using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Zeroshot.Cli.Tests;

internal sealed record CliResult(int ExitCode, string Stdout, string Stderr)
{
    /// <summary>The single JSON record a stream holds, checked for the CLI envelope schema.</summary>
    public static JsonElement Record(string stream)
    {
        var lines = stream.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length != 1) throw new InvalidOperationException($"Expected one record line, got {lines.Length}: {stream}");
        var record = JsonDocument.Parse(lines[0]).RootElement.Clone();
        if (record.GetProperty("schema").GetString() != "zeroshot-dotnet/cli/v1") throw new InvalidOperationException(stream);
        return record;
    }

    public JsonElement Error => Record(Stderr);

    /// <summary>Every JSON record on stdout, in order.</summary>
    public JsonElement[] Records => [.. Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => Record(line))];
}

/// <summary>
/// A temporary working directory in which the built zeroshot-dotnet runs as its own process, with real
/// arguments, standard streams, exit code and only the environment variables each test chooses to add.
/// </summary>
internal sealed class CliWorkspace : IDisposable
{
    private static readonly string Entry = Path.Combine(AppContext.BaseDirectory, "zeroshot-dotnet.dll");

    public string Root { get; } = Directory.CreateTempSubdirectory("zeroshot-cli-").FullName;

    public string PathOf(string name) => Path.Combine(Root, name);

    public string Write(string name, string content)
    {
        File.WriteAllText(PathOf(name), content);
        return name;
    }

    public Task<CliResult> RunAsync(params string[] args) => RunAsync(new Dictionary<string, string>(), args);

    public Task<CliResult> RunAsync(IReadOnlyDictionary<string, string> environment, params string[] args)
        => Start(environment, args).Completion;

    public CliProcess Start(params string[] args) => Start(new Dictionary<string, string>(), args);

    public CliProcess Start(IReadOnlyDictionary<string, string> environment, params string[] args) => Start(environment, null, args);

    /// <summary>
    /// Runs the command and closes the read end of its stdout pipe once <paramref name="lines"/> complete lines have
    /// arrived, as a reader such as <c>head</c> does. Whatever the process writes afterwards meets a closed pipe.
    /// </summary>
    public Task<CliResult> RunClosingStdoutAsync(int lines, params string[] args)
        => Start(new Dictionary<string, string>(), lines, args).Completion;

    private CliProcess Start(IReadOnlyDictionary<string, string> environment, int? closeStdoutAfter, string[] args)
    {
        var start = new ProcessStartInfo(DotnetHost())
        {
            WorkingDirectory = Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Entry);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        foreach (var (name, value) in environment) start.Environment[name] = value;

        return new CliProcess(Process.Start(start)!, closeStdoutAfter);
    }

    private static string DotnetHost()
    {
        if (Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host)) return host;
        // <dotnet root>/shared/Microsoft.NETCore.App/<version>/ holds the running runtime.
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "..", "..", ".."));
        return Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);
}

/// <summary>One running zeroshot-dotnet process.</summary>
internal sealed class CliProcess
{
    private readonly Process process;
    private readonly int? closeStdoutAfter;
    /// <summary>Stdout exactly as read, and the complete lines in it.</summary>
    private readonly StringBuilder stdout = new();
    private int stdoutLines;
    public Task<CliResult> Completion { get; }

    public CliProcess(Process process, int? closeStdoutAfter = null)
    {
        this.process = process;
        this.closeStdoutAfter = closeStdoutAfter;
        Completion = CompleteAsync();
    }

    /// <summary>Completes once the process has written <paramref name="count"/> complete stdout lines.</summary>
    public async Task StdoutLines(int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (Volatile.Read(ref stdoutLines) < count)
        {
            if (Completion.IsCompleted) throw new InvalidOperationException("zeroshot-dotnet exited first.");
            await Task.Delay(20, timeout.Token);
        }
    }

    /// <summary>Delivers Ctrl+C as the terminal would: SIGINT to the process. Tests using it exclude Windows.</summary>
    public async Task InterruptAsync()
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("SIGINT delivery needs a POSIX host.");
        using var kill = Process.Start(new ProcessStartInfo("kill", ["-INT", process.Id.ToString()]) { UseShellExecute = false })!;
        await kill.WaitForExitAsync();
        if (kill.ExitCode != 0) throw new InvalidOperationException("SIGINT was not delivered.");
    }

    private async Task<CliResult> CompleteAsync()
    {
        using (process)
        {
            var reading = ReadStdoutAsync(process.StandardOutput);
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw new TimeoutException("zeroshot-dotnet did not exit."); }
            await reading;
            return new CliResult(process.ExitCode, stdout.ToString(), await stderr);
        }
    }

    private async Task ReadStdoutAsync(StreamReader reader)
    {
        var buffer = new char[4096];
        int read;
        if (closeStdoutAfter == 0) { reader.Dispose(); return; } // a reader that never reads, such as `| true`
        while ((read = await reader.ReadAsync(buffer)) > 0)
        {
            stdout.Append(buffer, 0, read);
            var lines = Volatile.Read(ref stdoutLines) + buffer.AsSpan(0, read).Count('\n');
            Volatile.Write(ref stdoutLines, lines);
            if (lines >= closeStdoutAfter)
            {
                reader.Dispose(); // the reader has gone: later writes meet a closed pipe
                return;
            }
        }
    }
}
