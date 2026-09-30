using System.Diagnostics;
using System.Runtime.InteropServices;
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
/// A temporary working directory in which zeroshot-dotnet runs as its own process, with real arguments, standard
/// streams, exit code and only the environment variables each test chooses to add.
/// </summary>
/// <remarks>
/// By default the command is the repository build this project references. Release qualification instead names an
/// installed candidate in <c>ZEROSHOT_CLI_COMMAND</c>: a JSON array of the executable and any leading arguments,
/// such as an explicit tool path, a command found on PATH or <c>dotnet tool run zeroshot-dotnet</c>.
/// <c>ZEROSHOT_CLI_WORKSPACES</c> places the workspaces in a chosen directory, such as a local tool manifest's.
/// </remarks>
internal sealed class CliWorkspace : IDisposable
{
    private static readonly string[] Command = Environment.GetEnvironmentVariable("ZEROSHOT_CLI_COMMAND") is { Length: > 0 } named
        ? JsonSerializer.Deserialize<string[]>(named) is [_, ..] command ? command : throw new InvalidOperationException("ZEROSHOT_CLI_COMMAND names no command.")
        : [DotnetHost(), Path.Combine(AppContext.BaseDirectory, "zeroshot-dotnet.dll")];

    public string Root { get; } = Environment.GetEnvironmentVariable("ZEROSHOT_CLI_WORKSPACES") is { Length: > 0 } parent
        ? Directory.CreateDirectory(Path.Combine(parent, "zeroshot-cli-" + Guid.NewGuid().ToString("N"))).FullName
        : Directory.CreateTempSubdirectory("zeroshot-cli-").FullName;

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
        var start = new ProcessStartInfo(Command[0])
        {
            WorkingDirectory = Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in Command.Skip(1).Concat(args)) start.ArgumentList.Add(arg);
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

    /// <summary>
    /// Delivers Ctrl+C as a terminal does, to the command's whole foreground process tree: a launcher such as
    /// <c>dotnet tool run</c> and the zeroshot-dotnet process it starts both receive SIGINT. Tests using it exclude Windows.
    /// </summary>
    public async Task InterruptAsync()
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("SIGINT delivery needs a POSIX host.");
        const int SigInt = 2; // the same on Linux and macOS
        foreach (var descendant in await DescendantsAsync(process.Id)) Kill(descendant, SigInt);
        if (Kill(process.Id, SigInt) != 0) throw new InvalidOperationException($"SIGINT was not delivered (errno {Marshal.GetLastPInvokeError()}).");
    }

    // Signalled directly and listed through absolute paths: the suite can run with a PATH that holds only .NET.
    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Kill(int pid, int signal);

    private static async Task<List<int>> DescendantsAsync(int parent)
    {
        using var pgrep = Process.Start(new ProcessStartInfo("/usr/bin/pgrep", ["-P", parent.ToString()]) { RedirectStandardOutput = true, UseShellExecute = false })!;
        var children = (await pgrep.StandardOutput.ReadToEndAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList();
        await pgrep.WaitForExitAsync();
        var descendants = new List<int>();
        foreach (var child in children)
        {
            descendants.AddRange(await DescendantsAsync(child));
            descendants.Add(child);
        }
        return descendants;
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
                // Like head, the reader keeps exactly its first lines; the chunk may have held more, or part of one.
                var text = stdout.ToString();
                var end = -1;
                for (var kept = 0; kept < closeStdoutAfter; kept++) end = text.IndexOf('\n', end + 1);
                stdout.Length = end + 1;
                Volatile.Write(ref stdoutLines, closeStdoutAfter.Value);
                return;
            }
        }
    }
}
