using TUnit.Assertions;
using TUnit.Core;
using Zeroshot.Cli;

namespace Zeroshot.Cli.Tests;

public sealed class CliSmokeTests
{
    [Test]
    public async Task HelpRunsWithoutAProtocolConnection()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApp.Run(["--help"], output, error);

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(output.ToString()).Contains("Usage: zeroshot-cli [--help]");
        await Assert.That(error.ToString()).IsEmpty();
    }

    [Test]
    public async Task UnknownArgumentReturnsUsageError()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApp.Run(["run"], output, error);

        await Assert.That(exitCode).IsEqualTo(2);
        await Assert.That(error.ToString()).Contains("Unknown argument: run");
        await Assert.That(output.ToString()).IsEmpty();
    }
}
