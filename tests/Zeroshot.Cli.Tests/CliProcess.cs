using System.Diagnostics;
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

    public async Task<CliResult> RunAsync(IReadOnlyDictionary<string, string> environment, params string[] args)
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

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw new TimeoutException("zeroshot-dotnet did not exit."); }
        return new CliResult(process.ExitCode, await stdout, await stderr);
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
