using TUnit.Assertions;
using TUnit.Core;

namespace Zeroshot.Cli.Tests;

/// <summary>
/// Target commands parse configuration, durations, run files and the credentials their operation needs through the
/// SDK before any I/O. Until their workflows land they then stop with <c>unavailable</c>, which marks a fully valid invocation.
/// </summary>
public sealed class TargetInputTests
{
    private const string TargetBearer = "ZS_CLI_TEST_TARGET_BEARER";
    private const string ProviderKey = "ZS_CLI_TEST_PROVIDER_KEY";
    private const string Revision = "75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa";

    private static string Config(string transport = """{ "requestTimeout": "45s", "connectTimeout": "1500ms" }""",
        string observation = """{ "recovery": "none", "recoveryDelay": "0ms" }""", string target = "https://target.example/") => $$"""
        {
          "schema": "zeroshot-dotnet/target-config/v1",
          "target": "{{target}}",
          "nativeBinding": { "provenance": "caller-supplied", "release": "10.9.0", "sourceRevision": "{{Revision}}" },
          "credentials": {
            "targetBearerEnvironment": "{{TargetBearer}}",
            "connections": { "openai": { "OPENAI_API_KEY": "{{ProviderKey}}" } }
          },
          "transport": {{transport}},
          "observation": {{observation}}
        }
        """;

    private static readonly Dictionary<string, string> BearerOnly = new() { [TargetBearer] = "target-token" };

    private static async Task<CliResult> Run(IReadOnlyDictionary<string, string> environment, string config, params string[] args)
    {
        using var workspace = new CliWorkspace();
        workspace.Write("target.json", config);
        workspace.Write("request.json", PrepareTests.Request());
        workspace.Write("run.json", $$$"""{"schema":"zeroshot-dotnet/run-reference/v1","target":"https://target.example/","runId":"{{{PrepareTests.RunId}}}","nativeBinding":{"provenance":"caller-supplied","release":"10.9.0","sourceRevision":"{{{Revision}}}"}}""");
        return await workspace.RunAsync(environment, [.. args, "--json"]);
    }

    private static async Task Category(CliResult result, string category, int exitCode = 2)
    {
        await Assert.That(result.Stdout).IsEmpty();
        await Assert.That(result.Error.GetProperty("category").GetString()).IsEqualTo(category);
        await Assert.That(result.ExitCode).IsEqualTo(exitCode);
    }

    [Test]
    public async Task KnownRunCommandsResolveOnlyTheTargetBearer()
    {
        // The provider variable is unset: status never needs submission credentials.
        await Category(await Run(BearerOnly, Config(), "status", PrepareTests.RunId, "--config", "target.json"), "unavailable");
        await Category(await Run(new Dictionary<string, string>(), Config(), "status", PrepareTests.RunId, "--config", "target.json"), "credentials");
    }

    [Test]
    public async Task SubmissionResolvesItsProviderCredentialsWithoutEchoingValues()
    {
        var missing = await Run(BearerOnly, Config(), "run", "--config", "target.json", "--request", "request.json");
        await Category(missing, "credentials");
        await Assert.That(missing.Stderr).Contains(ProviderKey);

        var complete = new Dictionary<string, string>(BearerOnly) { [ProviderKey] = "provider-secret-5d2b" };
        await Category(await Run(complete, Config(), "run", "--config", "target.json", "--request", "request.json"), "unavailable");

        var malformed = await Run(new Dictionary<string, string> { [TargetBearer] = "not a bearer 8e4d" }, Config(),
            "status", PrepareTests.RunId, "--config", "target.json");
        await Category(malformed, "credentials");
        await Assert.That(malformed.Stderr).DoesNotContain("8e4d");
    }

    [Test]
    public async Task RunFileSuppliesTargetAndBindingWithoutConfiguration()
    {
        await Category(await Run(new Dictionary<string, string>(), Config(), "wait", "--run-file", "run.json"), "unavailable");
        await Category(await Run(BearerOnly, Config(target: "https://other.example/"),
            "status", "--run-file", "run.json", "--config", "target.json"), "input");
        await Category(await Run(BearerOnly, Config(), "status", PrepareTests.RunId), "invocation");
    }

    [Test]
    [Arguments("""{ "requestTimeout": "30" }""", "{}")]
    [Arguments("""{ "requestTimeout": 30 }""", "{}")]
    [Arguments("""{ "requestTimeout": "1.5s" }""", "{}")]
    [Arguments("""{ "requestTimeout": "infinite" }""", "{}")]
    [Arguments("""{ "requestTimeout": "0s" }""", "{}")]
    [Arguments("""{ "maxBufferedBytesTotal": "32MiB" }""", "{}")]
    [Arguments("""{ "maxConcurrentRequests": 4 }""", "{}")]
    [Arguments("""{ "requestTimeOut": "30s" }""", "{}")]
    [Arguments("{}", """{ "recovery": "always" }""")]
    [Arguments("{}", """{ "subscriptionOpenTimeout": "0ms" }""")]
    public async Task InvalidConfigurationValuesAreRefused(string transport, string observation)
        => await Category(await Run(BearerOnly, Config(transport, observation), "status", PrepareTests.RunId, "--config", "target.json"), "configuration");

    [Test]
    [Arguments("http://target.example/")]
    [Arguments("https://target.example/path")]
    public async Task TargetOriginRulesAreTheSdks(string target)
        => await Category(await Run(BearerOnly, Config(target: target), "status", PrepareTests.RunId, "--config", "target.json"), "configuration");

    [Test]
    public async Task ConfigurationMustDeclareItsSchemaAndKnownFields()
    {
        await Category(await Run(BearerOnly, Config().Replace("target-config/v1", "target-config/v2"), "status", PrepareTests.RunId, "--config", "target.json"), "configuration");
        await Category(await Run(BearerOnly, Config().Replace("\"target\":", "\"registry\": \"x\", \"target\":"), "status", PrepareTests.RunId, "--config", "target.json"), "configuration");
        await Category(await Run(BearerOnly, "not json", "status", PrepareTests.RunId, "--config", "target.json"), "configuration");
    }

    [Test]
    [Arguments("wait", "--timeout", "infinite", "unavailable")]
    [Arguments("wait", "--timeout", "0s", "unavailable")]
    [Arguments("wait", "--timeout", "2h", "unavailable")]
    [Arguments("force-stop", "--wait-timeout", "infinite", "unavailable")]
    [Arguments("status", "--request-timeout", "1500ms", "unavailable")]
    [Arguments("wait", "--timeout", "10", "invocation")]
    [Arguments("wait", "--timeout", "10M", "invocation")]
    [Arguments("wait", "--timeout", "-1s", "invocation")]
    [Arguments("status", "--request-timeout", "infinite", "invocation")]
    [Arguments("status", "--request-timeout", "0ms", "configuration")]
    public async Task DurationsNeedExplicitUnitsAndOnlyWaitBudgetsMayBeInfinite(string command, string option, string value, string category)
        => await Category(await Run(BearerOnly, Config(), command, PrepareTests.RunId, "--config", "target.json", option, value), category);

    [Test]
    [Arguments("run --config target.json --request request.json --prepared request.json")]
    [Arguments("run --config target.json --request request.json --detach --timeout 1m")]
    [Arguments("run --request request.json")]
    [Arguments("force-stop RUN --config target.json --request-only --wait-timeout 1m")]
    [Arguments("logs RUN --config target.json --after c1 --checkpoint cp.json")]
    [Arguments("watch RUN --config target.json --recovery sometimes")]
    [Arguments("watch RUN --config target.json --execution worker")]
    [Arguments("attach RUN --config target.json")]
    [Arguments("status --run-file run.json RUN")]
    public async Task CommandGrammarIsEnforcedBeforeAnyIo(string command)
    {
        var args = command.Replace("RUN", PrepareTests.RunId).Split(' ');
        await Category(await Run(BearerOnly, Config(), args), "invocation");
    }
}
