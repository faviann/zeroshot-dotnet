using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

/// <summary>
/// Qualifies the candidate only if every required platform and the Linux x64 native witness passed for exactly these
/// package bytes. A missing, duplicated, failed or foreign piece of evidence refuses qualification. Writes the combined
/// qualification manifest either way.
/// </summary>
internal static class Gate
{
    public static int Run(string artifacts, string output)
    {
        var failures = new List<string>();
        var candidate = CandidateFiles.Load(Path.Combine(artifacts, "candidate"));
        var identity = candidate.Identity();
        if (candidate.Manifest["worktreeClean"]?.GetValue<bool>() != true) failures.Add("The candidate was packed from a modified worktree.");

        var legs = Directory.GetFiles(artifacts, "evidence.json", SearchOption.AllDirectories)
            .Select(file => JsonNode.Parse(File.ReadAllText(file))!.AsObject()).ToList();
        foreach (var leg in legs.Where(leg => !Required.Platforms.Any(platform => platform.Runner == (string?)leg["runner"])))
            failures.Add($"Evidence from {leg["runner"]}, which is not a required platform.");
        var platforms = new JsonArray();
        foreach (var (runner, os, architecture) in Required.Platforms)
        {
            var matching = legs.Where(leg => (string?)leg["runner"] == runner).ToList();
            if (matching.Count != 1) { failures.Add($"{runner}: {matching.Count} evidence files, expected exactly one."); continue; }
            var leg = matching[0];
            var platform = leg["platform"]!.AsObject();
            void Require(bool condition, string failure) { if (!condition) failures.Add($"{runner}: {failure}"); }
            Require(JsonNode.DeepEquals(leg["candidate"], identity), "tested other package bytes than the candidate.");
            Require((string?)platform["os"] == os, $"ran on {platform["os"]}, not {os}.");
            Require((string?)platform["processArchitecture"] == architecture.ToString() && (string?)platform["osArchitecture"] == architecture.ToString(),
                $"ran a {platform["processArchitecture"]} process on {platform["osArchitecture"]}, not native {architecture}.");
            Require(((string?)platform["runtimeVersion"])?.StartsWith("10.", StringComparison.Ordinal) == true, $"ran .NET {platform["runtimeVersion"]}, not .NET 10.");
            var checks = leg["checks"]!.AsArray().Select(check => check!.AsObject()).ToList();
            foreach (var name in PlatformLeg.Checks)
                Require(checks.Count(check => (string?)check["name"] == name && check["passed"]?.GetValue<bool>() == true) == 1, $"check {name} did not pass.");
            Require(leg["passed"]?.GetValue<bool>() == true, "the leg did not pass.");
            platforms.Add(new JsonObject
            {
                ["runner"] = runner,
                ["os"] = platform["os"]?.DeepClone(),
                ["osDetail"] = platform["osDetail"]?.DeepClone(),
                ["processArchitecture"] = platform["processArchitecture"]?.DeepClone(),
                ["osArchitecture"] = platform["osArchitecture"]?.DeepClone(),
                ["runtime"] = platform["runtimeVersion"]?.DeepClone(),
                ["sdk"] = platform["sdkVersion"]?.DeepClone(),
                ["checks"] = new JsonArray([.. checks.Select(check => (JsonNode?)new JsonObject { ["name"] = check["name"]?.DeepClone(), ["passed"] = check["passed"]?.DeepClone(), ["detail"] = check["detail"]?.DeepClone() })]),
            });
        }

        var native = Native(Path.Combine(artifacts, "native-witness"), candidate, failures);
        var qualified = failures.Count == 0;
        var manifest = new JsonObject
        {
            ["schema"] = "zeroshot-dotnet/qualification/v1",
            ["qualified"] = qualified,
            ["candidate"] = candidate.Manifest.DeepClone(),
            ["platforms"] = platforms,
            ["nativeWitness"] = native,
            ["failures"] = new JsonArray([.. failures.Select(failure => (JsonNode?)failure)]),
        };
        Tools.WriteJson(output, manifest);
        Console.WriteLine(manifest.ToJsonString(Tools.Indented));
        foreach (var failure in failures) Console.Error.WriteLine($"Refused: {failure}");
        return qualified ? 0 : 1;
    }

    /// <summary>The witness's own provenance: pinned native build, test assets and the candidate packages it restored.</summary>
    private static JsonObject? Native(string directory, CandidateFiles candidate, List<string> failures)
    {
        var provenancePath = Path.Combine(directory, "provenance.txt");
        var resultPath = Path.Combine(directory, "result.txt");
        if (!File.Exists(provenancePath) || !File.Exists(resultPath)) { failures.Add("No native witness provenance and result."); return null; }
        var lines = File.ReadAllLines(provenancePath);
        string? Value(string key) => lines.FirstOrDefault(line => line.StartsWith(key + "=", StringComparison.Ordinal))?[(key.Length + 1)..];
        var hashes = lines.Select(line => Regex.Match(line, @"^([0-9a-f]{64})\s+(\S+)$")).Where(match => match.Success)
            .Select(match => (Sha256: match.Groups[1].Value, File: Regex.Replace(match.Groups[2].Value, @"^.*/zeroshot-native-witness\.[^/]+/", ""))).ToList();
        void Require(bool condition, string failure) { if (!condition) failures.Add($"native witness: {failure}"); }
        Require(File.ReadAllText(resultPath).StartsWith("PASS:", StringComparison.Ordinal), "did not pass.");
        Require(Value("nativeVersion") == Required.NativeVersion && Value("sourceRevision") == Required.NativeSourceRevision,
            $"exercised native {Value("nativeVersion")} at {Value("sourceRevision")}, not the supported {Required.NativeVersion} at {Required.NativeSourceRevision}.");
        Require(Value("sdkCandidate") == "supplied" && Value("sdkCommit") == candidate.Commit, "did not run the supplied candidate's commit.");
        Require(hashes.Contains((candidate.ClientSha256, "./" + Path.GetFileName(candidate.ClientFile))), "did not restore the candidate library package.");
        Require(hashes.Contains((candidate.CliSha256, "./" + Path.GetFileName(candidate.CliFile))), "did not install the candidate tool package.");
        Require(lines.Contains($"zeroshot-dotnet {candidate.Version}+{candidate.Commit}"), "did not run the candidate CLI.");
        var executable = hashes.Where(hash => hash.File == "bin/zeroshot").Select(hash => hash.Sha256).FirstOrDefault();
        Require(executable is not null, "recorded no native executable identity.");
        return new JsonObject
        {
            ["nativeVersion"] = Value("nativeVersion"),
            ["sourceRevision"] = Value("sourceRevision"),
            ["release"] = Value("release"),
            ["archiveSha256"] = Value("archiveSha256"),
            ["executableSha256"] = executable,
            ["identities"] = new JsonArray([.. hashes.Select(hash => (JsonNode?)new JsonObject { ["file"] = hash.File, ["sha256"] = hash.Sha256 })]),
            ["result"] = File.ReadAllText(resultPath).Trim(),
        };
    }
}
