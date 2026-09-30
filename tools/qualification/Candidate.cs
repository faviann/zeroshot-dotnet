using System.IO.Compression;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

/// <summary>
/// Builds once, packs the library and the CLI tool from that one build, checks the packages and the compatibility
/// contract, and writes <c>candidate.json</c> beside the two packages. Later jobs only ever receive these bytes.
/// </summary>
internal static class Candidate
{
    private const string ContractFile = "tools/qualification/contract.txt";
    private const string PrototypeFile = "tools/qualification/prototype-contract.txt";
    private const string PrototypeVersion = "0.1.0-preview.1";
    private const string PrototypeDecision = "https://github.com/faviann/zeroshot-dotnet-sdk/issues/7#issuecomment-5852136561";
    private static readonly string[] ApiFiles = ["src/Zeroshot.Sdk/PublicAPI.Shipped.txt", "src/Zeroshot.Sdk/PublicAPI.Unshipped.txt"];

    public static int Pack(string output)
    {
        if (!File.Exists("Zeroshot.sln")) throw new QualificationException("Run from the repository root.");
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new QualificationException($"{output} must be empty: a candidate is packed exactly once.");
        Directory.CreateDirectory(output);
        var commit = Tools.Git("rev-parse", "HEAD");
        var clean = Tools.Git("status", "--porcelain").Length == 0;
        var tags = Tools.Git("tag", "--points-at", "HEAD").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // One build: the library package and the tool package are both packed from its output. The public API
        // analyzer fails this build if the declared API (the PublicAPI files) differs from the compiled one.
        Tools.Checked(Tools.DotnetHost, ["build", "src/Zeroshot.Cli/Zeroshot.Cli.csproj", "-c", "Release", "--no-incremental", "-p:ContinuousIntegrationBuild=true"]);
        foreach (var project in new[] { "src/Zeroshot.Sdk/Zeroshot.Sdk.csproj", "src/Zeroshot.Cli/Zeroshot.Cli.csproj" })
            Tools.Checked(Tools.DotnetHost, ["pack", project, "-c", "Release", "--no-build", "-p:ContinuousIntegrationBuild=true", "-o", output]);
        var clientPath = Directory.GetFiles(output, "Zeroshot.Client.*.nupkg").Single();
        var cliPath = Directory.GetFiles(output, "Zeroshot.Cli.*.nupkg").Single();

        using var client = ZipFile.OpenRead(clientPath);
        using var cli = ZipFile.OpenRead(cliPath);
        var clientSpec = Nuspec(client);
        var cliSpec = Nuspec(cli);
        var version = Value(clientSpec, "version") ?? throw new QualificationException("The library package has no version.");
        if (Value(cliSpec, "version") != version) throw new QualificationException($"The CLI package version {Value(cliSpec, "version")} differs from the library's {version}.");
        foreach (var tag in tags.Where(tag => tag.StartsWith('v')))
            if (tag != "v" + version) throw new QualificationException($"Tag {tag} does not name version {version}.");
        // A tag-triggered run qualifies exactly the release that tag names.
        if (Environment.GetEnvironmentVariable("GITHUB_REF") is { } gitRef && gitRef.StartsWith("refs/tags/v", StringComparison.Ordinal)
            && (gitRef != $"refs/tags/v{version}" || !tags.Contains("v" + version)))
            throw new QualificationException($"{gitRef} does not name version {version} at the checked-out commit.");
        foreach (var spec in new[] { clientSpec, cliSpec })
        {
            var repository = spec.Elements().Single(e => e.Name.LocalName == "repository");
            if ((string?)repository.Attribute("commit") != commit)
                throw new QualificationException($"{Value(spec, "id")} records commit {(string?)repository.Attribute("commit")}, not {commit}.");
        }

        // The tool runs the very library the library package ships.
        var library = Tools.Sha256(client.GetEntry("lib/net10.0/Zeroshot.Client.dll") ?? throw new QualificationException("The library package has no lib/net10.0/Zeroshot.Client.dll."));
        var bundled = Tools.Sha256(cli.GetEntry("tools/net10.0/any/Zeroshot.Client.dll") ?? throw new QualificationException("The tool package bundles no Zeroshot.Client.dll."));
        if (bundled != library) throw new QualificationException("The tool package's Zeroshot.Client.dll differs from the library package's.");

        var tool = Path.Combine(Path.GetTempPath(), "zeroshot-candidate-tool-" + Guid.NewGuid().ToString("N"));
        foreach (var entry in cli.Entries.Where(e => e.FullName.StartsWith("tools/net10.0/any/", StringComparison.Ordinal)))
            entry.ExtractToFile(Path.Combine(Directory.CreateDirectory(tool).FullName, entry.Name));
        var command = Path.Combine(tool, "zeroshot-dotnet.dll");
        var expectedVersion = $"{version}+{commit}";
        var reported = Tools.Checked(Tools.DotnetHost, [command, "--version"]).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!reported.SequenceEqual([$"zeroshot-dotnet {expectedVersion}", $"Zeroshot.Client {expectedVersion}"]))
            throw new QualificationException($"zeroshot-dotnet --version reports '{string.Join(" / ", reported)}', not {expectedVersion}.");

        // The contract: package metadata and the CLI grammar read from the candidate itself. Any change must be
        // committed to tools/qualification/contract.txt, so it is reviewed rather than discovered.
        var contract = PackageLines(client, clientSpec).Concat(PackageLines(cli, cliSpec))
            .Concat(CliLines(Tools.Checked(Tools.DotnetHost, [command, "--help"])))
            .Concat(OutputLines(command))
            .Distinct().Order(StringComparer.Ordinal).ToList();
        File.WriteAllLines(Path.Combine(output, "contract.txt"), contract);
        Compare("tools/qualification/contract.txt", Entries(File.ReadAllLines(ContractFile)), contract);

        var api = ApiFiles.SelectMany(File.ReadAllLines).Where(IsApiLine).Select(line => "api " + line).ToList();
        var compatibility = Compatibility(version, api.Concat(contract.Where(line => line.StartsWith("cli ", StringComparison.Ordinal))).ToList());

        var manifest = new JsonObject
        {
            ["schema"] = "zeroshot-dotnet/qualification-candidate/v1",
            ["version"] = version,
            ["sourceCommit"] = commit,
            ["worktreeClean"] = clean,
            ["tags"] = new JsonArray([.. tags.Select(tag => (JsonNode?)tag)]),
            ["releaseTag"] = tags.FirstOrDefault(tag => tag == "v" + version),
            ["ref"] = Environment.GetEnvironmentVariable("GITHUB_REF"),
            ["workflowRun"] = Environment.GetEnvironmentVariable("GITHUB_RUN_ID") is { } run
                ? $"{Environment.GetEnvironmentVariable("GITHUB_SERVER_URL")}/{Environment.GetEnvironmentVariable("GITHUB_REPOSITORY")}/actions/runs/{run}" : null,
            ["dotnetSdk"] = Tools.Checked(Tools.DotnetHost, ["--version"]).Trim(),
            ["informationalVersion"] = expectedVersion,
            ["packages"] = new JsonObject
            {
                ["client"] = new JsonObject { ["id"] = "Zeroshot.Client", ["file"] = Path.GetFileName(clientPath), ["sha256"] = Tools.Sha256(clientPath), ["librarySha256"] = library },
                ["cli"] = new JsonObject
                {
                    ["id"] = "Zeroshot.Cli", ["file"] = Path.GetFileName(cliPath), ["sha256"] = Tools.Sha256(cliPath),
                    ["commandSha256"] = Tools.Sha256(cli.GetEntry("tools/net10.0/any/zeroshot-dotnet.dll")!), ["bundledLibrarySha256"] = bundled,
                },
            },
            ["compatibility"] = compatibility,
        };
        Tools.WriteJson(Path.Combine(output, "candidate.json"), manifest);
        Console.WriteLine(manifest.ToJsonString(Tools.Indented));
        return 0;
    }

    private static XElement Nuspec(ZipArchive package)
    {
        var entry = package.Entries.Single(e => !e.FullName.Contains('/') && e.Name.EndsWith(".nuspec", StringComparison.Ordinal));
        using var stream = entry.Open();
        return XDocument.Load(stream).Root!.Elements().Single(e => e.Name.LocalName == "metadata");
    }

    private static string? Value(XElement metadata, string name) => metadata.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value;

    /// <summary>Identity, license, repository association, target frameworks, dependencies, tool shape and files.</summary>
    private static IEnumerable<string> PackageLines(ZipArchive package, XElement spec)
    {
        var id = Value(spec, "id");
        var prefix = $"package {id}";
        yield return $"{prefix} authors {Value(spec, "authors")}";
        var license = spec.Elements().Single(e => e.Name.LocalName == "license");
        yield return $"{prefix} license {(string?)license.Attribute("type")} {license.Value}";
        yield return $"{prefix} project {Value(spec, "projectUrl")}";
        var repository = spec.Elements().Single(e => e.Name.LocalName == "repository");
        yield return $"{prefix} repository {(string?)repository.Attribute("type")} {(string?)repository.Attribute("url")}";
        if (Value(spec, "readme") is { } readme) yield return $"{prefix} readme {readme}";
        foreach (var type in spec.Descendants().Where(e => e.Name.LocalName == "packageType"))
            yield return $"{prefix} type {(string?)type.Attribute("name")}";
        foreach (var group in spec.Descendants().Where(e => e.Name.LocalName == "group"))
        {
            var framework = (string?)group.Attribute("targetFramework");
            yield return $"{prefix} framework {framework}";
            foreach (var dependency in group.Elements())
                yield return $"{prefix} dependency {framework} {(string?)dependency.Attribute("id")} {(string?)dependency.Attribute("version")}";
        }
        foreach (var entry in package.Entries)
        {
            if (entry.FullName is "[Content_Types].xml" || entry.FullName.StartsWith("_rels/", StringComparison.Ordinal)
                || entry.FullName.StartsWith("package/", StringComparison.Ordinal) || entry.FullName.EndsWith(".nuspec", StringComparison.Ordinal)) continue;
            yield return $"{prefix} file {entry.FullName}";
            if (entry.Name == "DotnetToolSettings.xml")
            {
                using var stream = entry.Open();
                foreach (var command in XDocument.Load(stream).Descendants("Command"))
                    yield return $"{prefix} command {(string?)command.Attribute("Name")} {(string?)command.Attribute("EntryPoint")} {(string?)command.Attribute("Runner")}";
            }
        }
    }

    /// <summary>
    /// The CLI grammar from the candidate's own help: commands, their options and positional arguments (RUN also
    /// admits the options its definition names), options every command accepts, the record schema and exit codes.
    /// </summary>
    internal static IEnumerable<string> CliLines(string help)
    {
        var lines = help.Replace("\r", "").Split('\n');
        var usage = new List<List<string>>();
        foreach (var line in lines.SkipWhile(line => line != "Usage:").Skip(1).TakeWhile(line => line.Trim().Length > 0))
        {
            var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(token => token.Trim('[', ']', '(', ')', '|')).Where(token => token.Length > 0).ToList();
            if (tokens[0] == "zeroshot-dotnet") usage.Add(tokens.Skip(1).ToList());
            else usage[^1].AddRange(tokens);
        }
        var definitions = lines.Where(line => Regex.IsMatch(line, "^  [A-Z]+ ")).ToDictionary(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0], line => line);
        var result = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var entry in usage)
        {
            if (entry[0].StartsWith("--", StringComparison.Ordinal)) { result.Add($"cli option {entry[0]}"); continue; }
            var command = entry[0];
            result.Add($"cli command {command}");
            for (var i = 1; i < entry.Count; i++)
            {
                var token = entry[i];
                if (token.StartsWith("--", StringComparison.Ordinal)) result.Add($"cli option {command} {token}");
                else if (!entry[i - 1].StartsWith("--", StringComparison.Ordinal))
                {
                    result.Add($"cli argument {command} {token}");
                    if (definitions.TryGetValue(token, out var definition))
                        foreach (Match option in Regex.Matches(definition, "--[a-z-]+")) result.Add($"cli option {command} {option.Value}");
                }
            }
        }
        var text = string.Join(' ', lines);
        var every = Regex.Match(text, @"Every command accepts ([^.]*)\.");
        foreach (Match option in Regex.Matches(every.Groups[1].Value, "--[a-z-]+")) result.Add($"cli option * {option.Value}");
        foreach (Match schema in Regex.Matches(text, @"zeroshot-dotnet/cli/v\d+")) result.Add($"cli schema {schema.Value}");
        var exits = text[(text.IndexOf("Exit codes:", StringComparison.Ordinal) + "Exit codes:".Length)..];
        foreach (var part in exits.Split(','))
            if (Regex.Match(part, @"^\s*(\d+) ") is { Success: true } code) result.Add($"cli exit {code.Groups[1].Value}");
        return result;
    }

    /// <summary>
    /// The machine-readable output, read from the packed tool itself: the record catalog that the CLI enforces on every
    /// cli/v1 record it writes, and every versioned file or record schema the CLI and library declare.
    /// </summary>
    private static IEnumerable<string> OutputLines(string command)
    {
        var cli = Assembly.LoadFrom(command);
        var records = (IReadOnlyDictionary<string, string[]>)cli.GetType("Zeroshot.Cli.CliContract", throwOnError: true)!
            .GetField("Records", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        foreach (var (kind, fields) in records)
        {
            if (kind != "*") yield return $"cli output {kind}";
            foreach (var field in fields) yield return $"cli output {kind} {field}";
        }
        var library = Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(command)!, "Zeroshot.Client.dll"));
        foreach (var type in cli.GetTypes().Concat(library.GetTypes()))
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                if (field is { Name: "Schema", IsLiteral: true } && field.GetRawConstantValue() is string schema && schema.StartsWith("zeroshot-dotnet/", StringComparison.Ordinal))
                    yield return $"cli schema {schema}";
    }

    private static bool IsApiLine(string line) => line.Length > 0 && !line.StartsWith('#');

    private static List<string> Entries(IEnumerable<string> lines) => [.. lines.Select(line => line.Trim()).Where(IsApiLine)];

    private static void Compare(string committed, List<string> expected, List<string> actual)
    {
        var missing = expected.Except(actual, StringComparer.Ordinal).ToList();
        var added = actual.Except(expected, StringComparer.Ordinal).ToList();
        if (missing.Count == 0 && added.Count == 0) return;
        foreach (var line in missing) Console.Error.WriteLine($"- {line}");
        foreach (var line in added) Console.Error.WriteLine($"+ {line}");
        throw new QualificationException($"The candidate differs from {committed} (lines above). Commit the candidate's contract.txt if the change is intended.");
    }

    /// <summary>
    /// Every baseline entry must still be present. The baseline is the latest release tag (v<version>) that precedes
    /// the candidate, whose recorded API and CLI contract are read from that tag. Only when no release precedes it is
    /// the baseline the accepted usage prototype. Entries may disappear only in a later minor version, and only with
    /// migration notes for it.
    /// </summary>
    private static JsonObject Compatibility(string version, List<string> candidate)
    {
        var prior = Tools.Git("tag", "--list", "v*").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(tag => SemVer.IsMatch(tag[1..]) && Precedence(tag[1..], version) < 0)
            .Order(Comparer<string>.Create((a, b) => Precedence(a[1..], b[1..]))).LastOrDefault();
        var baseline = prior is null
            ? new JsonObject { ["kind"] = "accepted-prototype", ["version"] = PrototypeVersion, ["contract"] = PrototypeFile, ["decision"] = PrototypeDecision }
            : new JsonObject { ["kind"] = "published", ["version"] = prior[1..], ["tag"] = prior };
        List<string> entries = prior is null
            ? Entries(File.ReadAllLines(PrototypeFile))
            : [.. ApiFiles.SelectMany(file => Tools.Git("show", $"{prior}:{file}").Split('\n')).Where(IsApiLine).Select(line => "api " + line.Trim()),
                .. Entries(Tools.Git("show", $"{prior}:{ContractFile}").Split('\n')).Where(line => line.StartsWith("cli ", StringComparison.Ordinal))];
        var baselineVersion = (string)baseline["version"]!;
        var missing = entries.Where(entry => !candidate.Any(line => Matches(entry, line))).ToList();
        var (major, minor) = Minor(version);
        var (baselineMajor, baselineMinor) = Minor(baselineVersion);
        var notes = $"docs/migration/{major}.{minor}.md";
        var laterMinor = (major, minor).CompareTo((baselineMajor, baselineMinor)) > 0;
        if ((major, minor).CompareTo((baselineMajor, baselineMinor)) < 0)
            throw new QualificationException($"Version {version} precedes its baseline {baselineVersion}.");
        if (missing.Count > 0)
        {
            foreach (var entry in missing) Console.Error.WriteLine($"- {entry}");
            if (!laterMinor) throw new QualificationException($"{missing.Count} {baseline["kind"]} baseline entries (above) are missing; {major}.{minor} releases must keep them.");
            if (!File.Exists(notes)) throw new QualificationException($"Breaking changes (above) in {major}.{minor} need migration notes at {notes}.");
        }
        return new JsonObject
        {
            ["baseline"] = baseline,
            ["entries"] = entries.Count,
            ["missing"] = new JsonArray([.. missing.Select(entry => (JsonNode?)entry)]),
            ["migrationNotes"] = missing.Count > 0 ? notes : null,
        };
    }

    private static readonly Regex SemVer = new(@"^(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.-]+))?$");

    /// <summary>Semantic-version precedence (numeric core, then a prerelease before its release, then identifiers).</summary>
    private static int Precedence(string left, string right)
    {
        var (a, b) = (SemVer.Match(left), SemVer.Match(right));
        if (!a.Success || !b.Success) throw new QualificationException($"'{left}' or '{right}' is not a semantic version.");
        for (var i = 1; i <= 3; i++)
            if (int.Parse(a.Groups[i].Value).CompareTo(int.Parse(b.Groups[i].Value)) is not 0 and var core) return core;
        if (a.Groups[4].Success != b.Groups[4].Success) return a.Groups[4].Success ? -1 : 1;
        if (!a.Groups[4].Success) return 0;
        var (x, y) = (a.Groups[4].Value.Split('.'), b.Groups[4].Value.Split('.'));
        for (var i = 0; i < Math.Min(x.Length, y.Length); i++)
        {
            var (xNumber, yNumber) = (int.TryParse(x[i], out var xn), int.TryParse(y[i], out var yn));
            var order = xNumber && yNumber ? xn.CompareTo(yn) : xNumber != yNumber ? (xNumber ? -1 : 1) : string.CompareOrdinal(x[i], y[i]);
            if (order != 0) return order;
        }
        return x.Length.CompareTo(y.Length);
    }

    /// <summary>
    /// A published baseline entry is an exact line. A prototype entry names a symbol: it matches any declared API line
    /// for that symbol or its members, whatever modifiers (static, override, ...) precede the name.
    /// </summary>
    private static bool Matches(string entry, string line)
    {
        if (entry == line) return true;
        if (!entry.StartsWith("api ", StringComparison.Ordinal) || !line.StartsWith("api ", StringComparison.Ordinal)) return false;
        var name = entry[4..];
        var start = line.IndexOf("Zeroshot.", StringComparison.Ordinal);
        if (start < 0) return false;
        var core = line[start..];
        return core == name || core.StartsWith(name, StringComparison.Ordinal) && core[name.Length] is '.' or '(' or ' ' or '<';
    }

    private static (int Major, int Minor) Minor(string version)
    {
        var match = Regex.Match(version, @"^(\d+)\.(\d+)\.");
        return match.Success ? (int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value))
            : throw new QualificationException($"'{version}' is not a semantic version.");
    }
}
