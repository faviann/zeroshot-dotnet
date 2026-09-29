using System.Text;
using System.Text.Json;
using TUnit.Assertions;
using TUnit.Core;
using Zeroshot;

namespace Zeroshot.Cli.Tests;

public sealed class PrepareTests
{
    internal const string Title = "Sensitive title 7f3a";
    internal const string RunId = "0195af77-1000-7000-8000-000000000001";

    /// <summary>A valid request file; identity fields are added only when given.</summary>
    internal static string Request(string identity = "") => $$"""
        {
          "title": "{{Title}}",{{identity}}
          "graph": {
            "profile": "openengine.graph.full/v1",
            "initialInput": { "kind": "null" },
            "policy": { "policy": "policy.native-v2@1", "default": "deny" },
            "root": { "kind": "succeed", "name": "done", "output": { "kind": "null" }, "bindings": [] }
          },
          "initialInput": null,
          "runtime": { "harness": "codex", "provider": "openai", "size": "small", "nodes": {} },
          "source": { "repository": "acme/project", "branch": "main", "revision": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" }
        }
        """;

    [Test]
    public async Task HelpShowsTheAcceptedCommandGrammar()
    {
        using var workspace = new CliWorkspace();
        var result = await workspace.RunAsync("--help");

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.Stderr).IsEmpty();
        foreach (var synopsis in new[]
        {
            "prepare --request FILE --out FILE", "run --config FILE (--request FILE | --prepared FILE)", "status RUN",
            "wait RUN", "watch RUN", "logs RUN", "attach RUN EXECUTION", "force-stop RUN",
        })
            await Assert.That(result.Stdout).Contains("zeroshot-dotnet " + synopsis);
    }

    [Test]
    public async Task PrepareWritesTheSdkExportAndReportsOnlyPathAndProposedId()
    {
        using var workspace = new CliWorkspace();
        workspace.Write("request.json", Request());

        // No configuration, target or credential variables: preparation is local.
        var result = await workspace.RunAsync("prepare", "--request", "request.json", "--out", "prepared.json", "--json");

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.Stderr).IsEmpty();
        var receipt = CliResult.Record(result.Stdout);
        await Assert.That(Names(receipt)).IsEquivalentTo(new[] { "schema", "kind", "proposedRunId", "path" });
        await Assert.That(receipt.GetProperty("kind").GetString()).IsEqualTo("prepared");
        await Assert.That(receipt.GetProperty("path").GetString()).IsEqualTo("prepared.json");
        await Assert.That(result.Stdout).DoesNotContain(Title);

        var written = await File.ReadAllBytesAsync(workspace.PathOf("prepared.json"));
        var prepared = PreparedSubmission.ImportUtf8(written);
        await Assert.That(prepared.RunId.Value).IsEqualTo(receipt.GetProperty("proposedRunId").GetString());
        await Assert.That(prepared.Submission.SubmissionKey.Value).StartsWith("dotnet-");
    }

    [Test]
    public async Task PrepareKeepsSuppliedIdentityAndWritesExactlyTheSdkBytes()
    {
        using var workspace = new CliWorkspace();
        var request = Request($"""
             "runId": "{RunId}", "submissionKey": "retained-key",
            """);
        workspace.Write("request.json", request);

        var result = await workspace.RunAsync("prepare", "--request", "request.json", "--out", "prepared.json");

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.Stdout.Trim()).IsEqualTo($"Prepared run {RunId} in prepared.json");
        var expected = RunRequest.ParseUtf8(Encoding.UTF8.GetBytes(request)).Prepare().ExportUtf8();
        await Assert.That(await File.ReadAllBytesAsync(workspace.PathOf("prepared.json"))).IsEquivalentTo(expected);
    }

    [Test]
    public async Task InvalidRequestIsRefusedWithASafeErrorRecordAndNoFile()
    {
        using var workspace = new CliWorkspace();
        workspace.Write("request.json", Request("""
             "secretNote": "do-not-echo-9c1e",
            """));

        var result = await workspace.RunAsync("prepare", "--request", "request.json", "--out", "prepared.json", "--json");

        await Assert.That(result.ExitCode).IsEqualTo(2);
        await Assert.That(result.Stdout).IsEmpty();
        var error = result.Error;
        await Assert.That(Names(error)).IsEquivalentTo(new[] { "schema", "kind", "category", "operation", "message" });
        await Assert.That(error.GetProperty("kind").GetString()).IsEqualTo("error");
        await Assert.That(error.GetProperty("category").GetString()).IsEqualTo("input");
        await Assert.That(error.GetProperty("operation").GetString()).IsEqualTo("prepare");
        await Assert.That(result.Stderr).DoesNotContain("do-not-echo-9c1e");
        await Assert.That(result.Stderr).DoesNotContain(Title);
        await Assert.That(File.Exists(workspace.PathOf("prepared.json"))).IsFalse();
    }

    [Test]
    public async Task UnreadableRequestIsInvalidInput()
    {
        using var workspace = new CliWorkspace();

        var result = await workspace.RunAsync("prepare", "--request", "missing.json", "--out", "prepared.json", "--json");

        await Assert.That(result.ExitCode).IsEqualTo(2);
        await Assert.That(result.Error.GetProperty("category").GetString()).IsEqualTo("input");
        await Assert.That(File.Exists(workspace.PathOf("prepared.json"))).IsFalse();
    }

    [Test]
    public async Task ExistingDestinationNeedsOverwriteConsentInTheInvocation()
    {
        using var workspace = new CliWorkspace();
        workspace.Write("request.json", Request());
        workspace.Write("prepared.json", "earlier retained bytes");

        var refused = await workspace.RunAsync("prepare", "--request", "request.json", "--out", "prepared.json", "--json");

        await Assert.That(refused.ExitCode).IsEqualTo(2);
        await Assert.That(refused.Stdout).IsEmpty();
        await Assert.That(refused.Error.GetProperty("category").GetString()).IsEqualTo("output-exists");
        await Assert.That(await File.ReadAllTextAsync(workspace.PathOf("prepared.json"))).IsEqualTo("earlier retained bytes");

        var replaced = await workspace.RunAsync("prepare", "--request", "request.json", "--out", "prepared.json", "--overwrite", "--json");

        await Assert.That(replaced.ExitCode).IsEqualTo(0);
        var id = CliResult.Record(replaced.Stdout).GetProperty("proposedRunId").GetString();
        await Assert.That(PreparedSubmission.ImportUtf8(await File.ReadAllBytesAsync(workspace.PathOf("prepared.json"))).RunId.Value).IsEqualTo(id);
    }

    [Test]
    public async Task WriteFailureIsAnOperationalErrorWithoutAReceipt()
    {
        using var workspace = new CliWorkspace();
        workspace.Write("request.json", Request());
        var destination = Path.Combine("absent-directory", "prepared.json");

        var result = await workspace.RunAsync("prepare", "--request", "request.json", "--out", destination, "--json");

        await Assert.That(result.ExitCode).IsEqualTo(1);
        await Assert.That(result.Stdout).IsEmpty();
        await Assert.That(result.Error.GetProperty("category").GetString()).IsEqualTo("output");
        await Assert.That(result.Stderr).DoesNotContain(Title);
    }

    [Test]
    [Arguments("prepare --request request.json")]
    [Arguments("prepare --out prepared.json")]
    [Arguments("prepare --request request.json --out prepared.json --config target.json")]
    [Arguments("prepare --request request.json --request other.json --out prepared.json")]
    [Arguments("prepare extra --request request.json --out prepared.json")]
    [Arguments("submit --request request.json")]
    public async Task InvalidInvocationIsExitTwoWithUsageHint(string command)
    {
        using var workspace = new CliWorkspace();
        workspace.Write("request.json", Request());

        var result = await workspace.RunAsync(command.Split(' '));

        await Assert.That(result.ExitCode).IsEqualTo(2);
        await Assert.That(result.Stdout).IsEmpty();
        await Assert.That(result.Stderr).Contains("zeroshot-dotnet --help");
        await Assert.That(File.Exists(workspace.PathOf("prepared.json"))).IsFalse();
    }

    internal static string[] Names(JsonElement record) => [.. record.EnumerateObject().Select(property => property.Name)];
}
