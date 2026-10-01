using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;

/// <summary>
/// Tests the candidate's exact package bytes on this machine: the SDK test suite and fresh consumers restored through
/// an isolated package source and cache, and the CLI test suite against the repository build and the three installed
/// forms of the candidate tool package. Suites and consumers run with a PATH that holds only .NET (and, for the
/// global tool, its command directory), in which neither Python nor a native zeroshot executable can be found.
/// Writes evidence.json and logs to the output directory; exits non-zero unless every check passed.
/// </summary>
internal static class PlatformLeg
{
    private static readonly string[] Examples = ["ContractsConsumer", "BindingsConsumer", "RunHandleConsumer"];
    private static readonly string[] Absent = ["python", "python3", "py", "zeroshot"];

    /// <summary>Every check a leg must pass; the gate requires each of them.</summary>
    public static readonly string[] Checks =
    [
        "platform", "fresh-consumers-restore-candidate", "sdk-tests", .. Examples,
        "cli-repository", "cli-global-tool", "cli-local-manifest", "cli-explicit-path", "no-python-or-native-used",
    ];

    public static int Run(string candidateDirectory, string runner, string output)
    {
        if (!File.Exists("Zeroshot.sln")) throw new QualificationException("Run from the repository root.");
        var repo = Directory.GetCurrentDirectory();
        var candidate = CandidateFiles.Load(candidateDirectory);
        var version = candidate.Version;
        if (Tools.Git("rev-parse", "HEAD") != candidate.Commit)
            throw new QualificationException($"The checkout is not the candidate's source commit {candidate.Commit}.");
        output = Path.GetFullPath(output);
        var logs = Directory.CreateDirectory(Path.Combine(output, "logs")).FullName;
        var work = RealPath(Directory.CreateTempSubdirectory("zeroshot-qualification-").FullName);
        Console.WriteLine($"Work directory: {work}");
        var checks = new JsonArray();
        var expected = Required.Platforms.SingleOrDefault(platform => platform.Runner == runner);
        var platform = Platform();
        var evidence = new JsonObject
        {
            ["schema"] = "zeroshot-dotnet/qualification-platform/v1",
            ["runner"] = runner,
            ["expected"] = expected.Runner is null ? null : new JsonObject { ["os"] = expected.Os, ["architecture"] = expected.Architecture.ToString() },
            ["candidate"] = candidate.Identity(),
            ["platform"] = platform,
            ["checks"] = checks,
        };

        void Check(string name, Func<JsonObject?> check)
        {
            Console.WriteLine($"::group::{name}");
            var record = new JsonObject { ["name"] = name };
            try
            {
                if (check() is { } detail) record["detail"] = detail;
                record["passed"] = true;
            }
            catch (Exception failure)
            {
                record["passed"] = false;
                record["failure"] = failure.Message;
            }
            Console.WriteLine("::endgroup::");
            Console.WriteLine($"{((bool)record["passed"]! ? "PASS" : "FAIL")} {name}{(record["failure"] is { } reason ? ": " + reason : "")}");
            checks.Add(record);
        }

        Check("platform", () =>
        {
            if (expected.Runner is null) throw new QualificationException($"{runner} is not a required qualification runner.");
            if ((string)platform["os"]! != expected.Os) throw new QualificationException($"Expected {expected.Os}, found {platform["os"]}.");
            if (RuntimeInformation.ProcessArchitecture != expected.Architecture || RuntimeInformation.OSArchitecture != expected.Architecture)
                throw new QualificationException($"Expected a native {expected.Architecture} process and OS, found {RuntimeInformation.ProcessArchitecture} on {RuntimeInformation.OSArchitecture}.");
            if (Environment.Version.Major != 10) throw new QualificationException($"Expected .NET 10, found {Environment.Version}.");
            return null;
        });

        // Isolated package source and cache: the local feed holds only the candidate, and source mapping lets
        // Zeroshot packages come from nowhere else. Third-party dependencies come from nuget.org.
        var feed = Directory.CreateDirectory(Path.Combine(work, "feed")).FullName;
        File.Copy(candidate.ClientFile, Path.Combine(feed, Path.GetFileName(candidate.ClientFile)));
        File.Copy(candidate.CliFile, Path.Combine(feed, Path.GetFileName(candidate.CliFile)));
        var packages = Path.Combine(work, "packages");
        var fresh = Directory.CreateDirectory(Path.Combine(work, "fresh")).FullName;
        File.WriteAllText(Path.Combine(fresh, "nuget.config"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="candidate" value="{feed}" />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
              </packageSources>
              <packageSourceMapping>
                <packageSource key="candidate"><package pattern="Zeroshot.*" /></packageSource>
                <packageSource key="nuget.org"><package pattern="*" /></packageSource>
              </packageSourceMapping>
            </configuration>
            """);
        var isolated = new Dictionary<string, string?> { ["NUGET_PACKAGES"] = packages };
        var dotnetRoot = Tools.DotnetRoot;
        // Suites and consumers see only .NET, behind poison python/py/zeroshot commands that record any use.
        var (poison, markers) = Poison(work);
        var bare = new Dictionary<string, string?>(isolated) { ["PATH"] = poison + Path.PathSeparator + dotnetRoot };

        Check("fresh-consumers-restore-candidate", () =>
        {
            // Fresh copies with no route back to src/: they compile only against the restored candidate package.
            File.Copy("global.json", Path.Combine(fresh, "global.json"));
            foreach (var project in new[] { "tests/Zeroshot.Sdk.Tests", "tests/Zeroshot.Cli.Tests" }.Concat(Examples.Select(name => "examples/" + name)))
                CopyProject(project, Path.Combine(fresh, project));
            foreach (var example in Examples)
            {
                var file = Path.Combine(fresh, "examples", example, example + ".csproj");
                var text = File.ReadAllText(file);
                var pinned = Regex.Replace(text, """Include="Zeroshot.Client" Version="[^"]*" """.Trim(), $"""Include="Zeroshot.Client" Version="[{version}]" """.Trim());
                if (pinned == text) throw new QualificationException($"{example} has no Zeroshot.Client reference to pin.");
                File.WriteAllText(file, pinned);
                Tools.Checked(Tools.DotnetHost, ["build", file, "-c", "Release"], environment: isolated, log: Path.Combine(logs, $"build-{example}.log"));
            }
            foreach (var suite in new[] { "Zeroshot.Sdk.Tests", "Zeroshot.Cli.Tests" })
                Tools.Checked(Tools.DotnetHost, ["build", Path.Combine(fresh, "tests", suite, suite + ".csproj"), "-c", "Release", $"-p:ZeroshotCandidateVersion={version}"],
                    environment: isolated, log: Path.Combine(logs, $"build-{suite}.log"));
            var cached = Path.Combine(packages, "zeroshot.client", version.ToLowerInvariant(), $"zeroshot.client.{version.ToLowerInvariant()}.nupkg");
            if (!File.Exists(cached) || Tools.Sha256(cached) != candidate.ClientSha256)
                throw new QualificationException("The isolated cache does not hold the candidate library package bytes.");
            var libraries = Directory.GetFiles(fresh, "Zeroshot.Client.dll", SearchOption.AllDirectories);
            var librarySha256 = (string)candidate.Manifest["packages"]!["client"]!["librarySha256"]!;
            if (libraries.Length == 0 || libraries.Any(file => Tools.Sha256(file) != librarySha256))
                throw new QualificationException("A fresh consumer's output holds a Zeroshot.Client.dll that is not the candidate's.");
            return new JsonObject { ["cachedPackageSha256"] = Tools.Sha256(cached), ["consumerLibraries"] = libraries.Length };
        });

        Check("sdk-tests", () => Suite("sdk-tests", Path.Combine(fresh, "tests", "Zeroshot.Sdk.Tests"), bare, logs, output));
        foreach (var example in Examples)
            Check(example, () =>
            {
                var bin = Path.Combine(fresh, "examples", example, "bin", "Release", "net10.0");
                string[] arguments = example == "BindingsConsumer" ? [Path.Combine(bin, example + ".dll"), Path.Combine(repo, "docs", "contracts", "coverage.json")] : [Path.Combine(bin, example + ".dll")];
                Tools.Checked(Tools.DotnetHost, arguments, workingDirectory: bin, environment: bare, log: Path.Combine(logs, $"{example}.log"));
                return null;
            });

        // The CLI: built from this checkout, then the candidate tool package installed three ways from the local feed.
        var commandSha256 = (string)candidate.Manifest["packages"]!["cli"]!["commandSha256"]!;
        var exe = OperatingSystem.IsWindows() ? ".exe" : "";
        Check("cli-repository", () =>
        {
            Tools.Checked(Tools.DotnetHost, ["build", "src/Zeroshot.Cli/Zeroshot.Cli.csproj", "-c", "Release"], log: Path.Combine(logs, "build-cli-repository.log"));
            Version(Tools.Checked(Tools.DotnetHost, ["run", "--project", "src/Zeroshot.Cli", "-c", "Release", "--no-build", "--", "--version"]), candidate);
            var entry = Path.Combine(repo, "src", "Zeroshot.Cli", "bin", "Release", "net10.0", "zeroshot-dotnet.dll");
            return CliSuite("cli-repository", [Tools.DotnetHost, entry], bare, null, work, fresh, logs, output, candidate);
        });
        Check("cli-global-tool", () =>
        {
            var home = Path.Combine(work, "global-home");
            var global = new Dictionary<string, string?>(isolated) { ["DOTNET_CLI_HOME"] = home };
            Tools.Checked(Tools.DotnetHost, ["tool", "install", "--global", "Zeroshot.Cli", "--version", version, "--source", feed], workingDirectory: work, environment: global, log: Path.Combine(logs, "install-global.log"));
            var commands = Path.Combine(home, ".dotnet", "tools");
            Installed(commands, commandSha256);
            var onPath = new Dictionary<string, string?>(bare) { ["DOTNET_CLI_HOME"] = home, ["PATH"] = string.Join(Path.PathSeparator, poison, commands, dotnetRoot) };
            if (Unreachable([commands]).Count > 0) throw new QualificationException("The global tool directory holds Python or native zeroshot.");
            return CliSuite("cli-global-tool", ["zeroshot-dotnet"], onPath, null, work, fresh, logs, output, candidate);
        });
        Check("cli-local-manifest", () =>
        {
            var project = Directory.CreateDirectory(Path.Combine(work, "local-tool")).FullName;
            // The SDK caches where it last found a local tool's package id and version under DOTNET_CLI_HOME, so an
            // earlier install of the same version elsewhere would otherwise run instead of this one.
            var home = Path.Combine(work, "local-home");
            var local = new Dictionary<string, string?>(isolated) { ["DOTNET_CLI_HOME"] = home };
            Tools.Checked(Tools.DotnetHost, ["new", "tool-manifest"], workingDirectory: project, environment: local, log: Path.Combine(logs, "manifest-local.log"));
            Tools.Checked(Tools.DotnetHost, ["tool", "install", "--local", "Zeroshot.Cli", "--version", version, "--source", feed], workingDirectory: project, environment: local, log: Path.Combine(logs, "install-local.log"));
            Installed(Path.Combine(packages, "zeroshot.cli"), commandSha256);
            return CliSuite("cli-local-manifest", [Tools.DotnetHost, "tool", "run", "zeroshot-dotnet", "--"], new Dictionary<string, string?>(bare) { ["DOTNET_CLI_HOME"] = home },
                Path.Combine(project, "workspaces"), project, fresh, logs, output, candidate);
        });
        Check("cli-explicit-path", () =>
        {
            var toolPath = Path.Combine(work, "tool-path");
            Tools.Checked(Tools.DotnetHost, ["tool", "install", "Zeroshot.Cli", "--tool-path", toolPath, "--version", version, "--source", feed], workingDirectory: work, environment: isolated, log: Path.Combine(logs, "install-explicit.log"));
            Installed(toolPath, commandSha256);
            return CliSuite("cli-explicit-path", [Path.Combine(toolPath, "zeroshot-dotnet" + exe)], bare, null, work, fresh, logs, output, candidate);
        });

        Check("no-python-or-native-used", () =>
        {
            var used = Directory.GetFiles(markers).Select(Path.GetFileName).ToArray();
            if (used.Length > 0) throw new QualificationException($"Invoked: {string.Join(", ", used)}.");
            return new JsonObject
            {
                ["path"] = new JsonArray(poison, dotnetRoot),
                ["poisoned"] = new JsonArray([.. Absent.Select(name => (JsonNode?)name)]),
                ["foundBeyondPoison"] = Unreachable([dotnetRoot]),
                ["invoked"] = new JsonArray(),
            };
        });

        var passed = checks.All(check => (bool)check!["passed"]!);
        evidence["passed"] = passed;
        Tools.WriteJson(Path.Combine(output, "evidence.json"), evidence);
        Console.WriteLine(evidence.ToJsonString(Tools.Indented));
        return passed ? 0 : 1;
    }

    /// <summary>What this process observes of the machine: OS, architectures and .NET servicing versions.</summary>
    private static JsonObject Platform()
    {
        var (os, detail) = OperatingSystemName();
        return new JsonObject
        {
            ["os"] = os,
            ["osDetail"] = detail,
            ["osDescription"] = RuntimeInformation.OSDescription,
            ["processArchitecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
            ["osArchitecture"] = RuntimeInformation.OSArchitecture.ToString(),
            ["runtimeIdentifier"] = RuntimeInformation.RuntimeIdentifier,
            ["runtime"] = RuntimeInformation.FrameworkDescription,
            ["runtimeVersion"] = Environment.Version.ToString(),
            ["sdkVersion"] = Tools.Checked(Tools.DotnetHost, ["--version"]).Trim(),
            ["dotnetRoot"] = Tools.DotnetRoot,
        };
    }

    private static (string Os, string Detail) OperatingSystemName()
    {
        if (OperatingSystem.IsLinux())
        {
            var release = File.ReadAllLines("/etc/os-release").Select(line => line.Split('=', 2)).Where(pair => pair.Length == 2)
                .ToDictionary(pair => pair[0], pair => pair[1].Trim('"'));
            return ($"{release.GetValueOrDefault("NAME")} {release.GetValueOrDefault("VERSION_ID")}", release.GetValueOrDefault("PRETTY_NAME") ?? "");
        }
        if (OperatingSystem.IsMacOS())
        {
            var plist = File.ReadAllText("/System/Library/CoreServices/SystemVersion.plist");
            string Key(string key) => Regex.Match(plist, $@"<key>{key}</key>\s*<string>([^<]*)</string>").Groups[1].Value;
            var productVersion = Key("ProductVersion");
            return ($"{Key("ProductName")} {productVersion.Split('.')[0]}", $"{Key("ProductName")} {productVersion} ({Key("ProductBuildVersion")})");
        }
        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion")!;
            var product = (string)key.GetValue("ProductName")!;
            var build = int.Parse((string)key.GetValue("CurrentBuildNumber")!);
            var detail = $"{key.GetValue("DisplayVersion")} build {build}.{key.GetValue("UBR")} (ProductName '{product}', {key.GetValue("InstallationType")}, {key.GetValue("EditionID")})";
            // Client editions of Windows 11 still report "Windows 10" as ProductName; build 22000 and later is Windows 11.
            var os = (string?)key.GetValue("InstallationType") == "Server"
                ? Regex.Match(product, @"^Windows Server \d{4}").Value is { Length: > 0 } server ? server : product
                : build >= 22000 ? "Windows 11" : "Windows 10";
            return (os, $"{os} {detail}");
        }
        return (RuntimeInformation.OSDescription, RuntimeInformation.OSDescription);
    }

    /// <summary>
    /// A directory of python, python3, py and zeroshot commands that fail and leave a marker, placed first on the
    /// suites' PATH. On Windows they are .cmd files, found by shells and PATHEXT lookups; a bare CreateProcess would
    /// look for .exe files, which <see cref="Unreachable"/> shows the rest of the PATH does not hold.
    /// </summary>
    private static (string Directory, string Markers) Poison(string work)
    {
        var directory = Directory.CreateDirectory(Path.Combine(work, "poison")).FullName;
        var markers = Directory.CreateDirectory(Path.Combine(work, "poison-used")).FullName;
        foreach (var name in Absent)
        {
            var marker = Path.Combine(markers, name);
            if (OperatingSystem.IsWindows())
                File.WriteAllText(Path.Combine(directory, name + ".cmd"), $"@echo invoked> \"{marker}\"\r\n@exit /b 97\r\n");
            else
            {
                var script = Path.Combine(directory, name);
                File.WriteAllText(script, $"#!/bin/sh\n: > '{marker}'\nexit 97\n");
                File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        return (directory, markers);
    }

    /// <summary>Python or native zeroshot executables that a process with this PATH could find.</summary>
    private static JsonArray Unreachable(IEnumerable<string> path)
    {
        var extensions = OperatingSystem.IsWindows()
            ? [.. (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries), ""]
            : new[] { "" };
        var found = new JsonArray();
        foreach (var directory in path)
            foreach (var name in Absent)
                foreach (var extension in extensions)
                    if (File.Exists(Path.Combine(directory, name + extension))) found.Add(Path.Combine(directory, name + extension));
        if (found.Count > 0) throw new QualificationException($"Reachable: {found.ToJsonString()}");
        return found;
    }

    /// <summary>
    /// The path with every symbolic link resolved. macOS reaches its temporary directory through /var, a link to
    /// /private/var, and MSBuild then copies no globbed items (such as test fixtures) to the output.
    /// </summary>
    private static string RealPath(string path)
    {
        var real = Path.GetPathRoot(path)!;
        foreach (var part in Path.GetRelativePath(real, path).Split(Path.DirectorySeparatorChar))
        {
            real = Path.Combine(real, part);
            if (new DirectoryInfo(real).ResolveLinkTarget(returnFinalTarget: true) is { } target) real = target.FullName;
        }
        return real;
    }

    private static void CopyProject(string source, string destination)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var first = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            if (first is "bin" or "obj" or "TestResults") continue;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(destination, relative))!);
            File.Copy(file, Path.Combine(destination, relative));
        }
    }

    /// <summary>Runs a built test application directly (no MSBuild) and records its summary.</summary>
    private static JsonObject Suite(string name, string project, IReadOnlyDictionary<string, string?> environment, string logs, string output)
    {
        var bin = Path.Combine(project, "bin", "Release", "net10.0");
        var assembly = Path.Combine(bin, Path.GetFileName(project) + ".dll");
        var (exitCode, text) = Tools.Run(Tools.DotnetHost, [assembly, "--no-ansi", "--progress", "off", "--results-directory", Path.Combine(output, "results", name)],
            workingDirectory: bin, environment: environment, log: Path.Combine(logs, name + ".log"));
        int Count(string label) => Regex.Match(text, $@"^\s*{label}: (\d+)", RegexOptions.Multiline) is { Success: true } match ? int.Parse(match.Groups[1].Value) : -1;
        // Named so the gate can hold skips to the platform-specific allowlist.
        var skipped = Regex.Matches(text, @"^skipped (.+) \([^()]*\)\r?$", RegexOptions.Multiline).Select(match => (JsonNode?)match.Groups[1].Value).ToArray();
        var summary = new JsonObject
        {
            ["total"] = Count("total"), ["failed"] = Count("failed"), ["succeeded"] = Count("succeeded"), ["skipped"] = Count("skipped"),
            ["skippedTests"] = new JsonArray(skipped), ["exitCode"] = exitCode,
        };
        if (exitCode != 0 || Count("failed") != 0 || Count("total") <= 0 || skipped.Length != Count("skipped"))
            throw new QualificationException($"{name} did not pass: {summary.ToJsonString()}");
        return summary;
    }

    /// <summary>The CLI test suite against one form of zeroshot-dotnet, after its --version names the candidate.</summary>
    private static JsonObject CliSuite(string name, string[] command, IReadOnlyDictionary<string, string?> environment, string? workspaces,
        string workingDirectory, string fresh, string logs, string output, CandidateFiles candidate)
    {
        // A bare command name is found through the PATH it will run with, as the test suite's own launches find it.
        var file = Path.IsPathRooted(command[0]) ? command[0]
            : environment["PATH"]!.Split(Path.PathSeparator).Select(directory => Path.Combine(directory, command[0] + (OperatingSystem.IsWindows() ? ".exe" : "")))
                .FirstOrDefault(File.Exists) ?? throw new QualificationException($"{command[0]} is not on the suite's PATH.");
        Version(Tools.Checked(file, [.. command.Skip(1), "--version"], workingDirectory: workingDirectory, environment: environment), candidate);
        var observedFile = Path.Combine(logs, name + "-output-paths.txt");
        var suite = new Dictionary<string, string?>(environment)
        {
            ["ZEROSHOT_CLI_COMMAND"] = JsonSerializer.Serialize(command),
            ["ZEROSHOT_CLI_WORKSPACES"] = workspaces,
            ["ZEROSHOT_CLI_OBSERVED"] = observedFile,
        };
        var summary = Suite(name, Path.Combine(fresh, "tests", "Zeroshot.Cli.Tests"), suite, logs, output);
        var (unobserved, undeclared) = OutputCoverage(candidate, File.Exists(observedFile) ? File.ReadAllLines(observedFile) : []);
        summary["unobservedOutput"] = new JsonArray([.. unobserved.Select(path => (JsonNode?)path)]);
        summary["undeclaredOutput"] = new JsonArray([.. undeclared.Select(path => (JsonNode?)path)]);
        if (unobserved.Count > 0 || undeclared.Count > 0)
            throw new QualificationException($"{name} output differs from the declared contract: {summary.ToJsonString()}");
        return summary;
    }

    /// <summary>
    /// Compares the cli/v1 field paths the suite saw ("kind path" lines) with the candidate's declared output contract.
    /// Every declared kind and path must appear, so a field the CLI stopped writing cannot stay in the contract; the
    /// compatibility check already holds the candidate's contract to its baseline's. A path is undeclared when it is
    /// top-level or inside an object whose fields are declared; fields inside carried native objects are not the CLI's.
    /// </summary>
    private static (List<string> Unobserved, List<string> Undeclared) OutputCoverage(CandidateFiles candidate, string[] observed)
    {
        var declared = candidate.Manifest["cliOutput"]!.AsArray().Select(line => ((string)line!)["cli output ".Length..]).ToHashSet();
        var seen = observed.ToHashSet();
        foreach (var line in observed.Where(line => line.Contains(' ')))
            seen.Add("* " + line.Split(' ', 2)[1]);
        var unobserved = declared.Where(entry => !seen.Contains(entry) && !Required.MayBeUnobserved(OperatingSystem.IsWindows(), entry))
            .Order(StringComparer.Ordinal).ToList();
        var undeclared = new List<string>();
        foreach (var line in observed.Where(line => line.Contains(' ')))
        {
            var (kind, path) = (line.Split(' ', 2)[0], line.Split(' ', 2)[1]);
            if (declared.Contains(line) || declared.Contains("* " + path)) continue;
            var parent = path.Contains('.') ? path[..path.LastIndexOf('.')] : null;
            if (parent is null || declared.Any(entry => entry.StartsWith($"{kind} {parent}.", StringComparison.Ordinal))) undeclared.Add(line);
        }
        return (unobserved, undeclared);
    }

    private static void Version(string reported, CandidateFiles candidate)
    {
        var expected = $"{candidate.Version}+{candidate.Commit}";
        var lines = reported.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!lines.SequenceEqual([$"zeroshot-dotnet {expected}", $"Zeroshot.Client {expected}"]))
            throw new QualificationException($"--version reports '{string.Join(" / ", lines)}', not {expected}.");
    }

    /// <summary>Every installed copy of the command under <paramref name="root"/> is the candidate's.</summary>
    private static void Installed(string root, string commandSha256)
    {
        var copies = Directory.GetFiles(root, "zeroshot-dotnet.dll", SearchOption.AllDirectories);
        if (copies.Length == 0 || copies.Any(file => Tools.Sha256(file) != commandSha256))
            throw new QualificationException($"The installed zeroshot-dotnet.dll under {root} is not the candidate's.");
    }
}
