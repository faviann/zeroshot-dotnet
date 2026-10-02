using System.Text;
using System.Text.Json;
using TUnit.Assertions;
using TUnit.Core;
using TUnit.Core.Enums;
using Zeroshot;

namespace Zeroshot.Cli.Tests;

/// <summary>
/// run, status, wait and force-stop as separate processes against a loopback direct target. The target counts every
/// mutation it receives, independently of what the CLI reports, and Ctrl+C is a real SIGINT.
/// </summary>
public sealed class RunTests
{
    private const string Proposed = PrepareTests.RunId;
    private const string Acknowledged = "0195af77-1000-7000-8000-000000000002";
    private const string Revision = "75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa";

    private static readonly string Binding = $$"""{ "provenance": "caller-supplied", "release": "10.9.0", "sourceRevision": "{{Revision}}" }""";

    /// <summary>A workspace holding target.json for <paramref name="peer"/> and a request that proposes <see cref="Proposed"/>.</summary>
    private static CliWorkspace Workspace(TargetPeer peer, bool binding = true)
    {
        var workspace = new CliWorkspace();
        workspace.Write("target.json", $$"""
            { "schema": "zeroshot-dotnet/target-config/v1", "target": "{{peer.Origin}}"{{(binding ? $", \"nativeBinding\": {Binding}" : "")}} }
            """);
        workspace.Write("request.json", PrepareTests.Request($"""
             "runId": "{Proposed}", "submissionKey": "retained-key",
            """));
        return workspace;
    }

    private static string[] Run(params string[] extra) => ["run", "--config", "target.json", "--request", "request.json", "--json", .. extra];
    private static string[] Known(string command, params string[] extra) => [command, Acknowledged, "--config", "target.json", "--json", .. extra];

    private static string? Text(JsonElement record, params string[] path)
    {
        foreach (var name in path)
            if (!record.TryGetProperty(name, out record)) return null;
        return record.ValueKind == JsonValueKind.String ? record.GetString() : record.GetRawText();
    }

    private static async Task Failure(CliResult result, int exitCode, string category)
    {
        await Assert.That(result.ExitCode).IsEqualTo(exitCode);
        await Assert.That(Text(result.Error, "category")).IsEqualTo(category);
        await Assert.That(result.Stderr).DoesNotContain(PrepareTests.Title);
    }

    private static TargetPeer Acknowledging(string runId) => new() { Submit = exchange => exchange.ReplyAsync(200, $$"""{"runId":"{{runId}}"}""") };

    [Test]
    public async Task RunSubmitsOnceSavesItsFilesWaitsAndTheSavedRunReconnects()
    {
        await using var peer = Acknowledging(Acknowledged);
        peer.Oecp["run/status"] = TargetPeer.Reply(call => TargetPeer.Status(call.RunId, "s1", TargetPeer.Running));
        peer.Oecp["run/watch"] = TargetPeer.Watch(
            call => TargetPeer.WatchEvent(call.RunId, "c2", TargetPeer.Running),
            call => TargetPeer.WatchEvent(call.RunId, "c3", TargetPeer.Succeeded));
        using var workspace = Workspace(peer);

        var run = await workspace.RunAsync(Run("--save-request", "saved.json", "--save-run", "run.json"));

        await Assert.That(run.ExitCode).IsEqualTo(0);
        await Assert.That(run.Stderr).IsEmpty();
        var records = run.Records;
        await Assert.That(string.Join(",", records.Select(r => Text(r, "kind")))).IsEqualTo("submission,result");
        var submission = records[0];
        await Assert.That(Text(submission, "runId")).IsEqualTo(Acknowledged);
        await Assert.That(Text(submission, "target")).IsEqualTo(peer.Origin.AbsoluteUri);
        await Assert.That(Text(submission, "attempt", "outcome")).IsEqualTo("acknowledged");
        await Assert.That(Text(submission, "attempt", "proposedRunId")).IsEqualTo(Proposed);
        await Assert.That(Text(submission, "attempt", "runIdsMatch")).IsEqualTo("false");
        var result = records[1];
        await Assert.That(Text(result, "runId")).IsEqualTo(Acknowledged);
        await Assert.That(Text(result, "result", "succeeded")).IsEqualTo("true");
        await Assert.That(Text(result, "result", "output", "answer")).IsEqualTo("42");
        await Assert.That(Text(result, "result", "evidence", "kind")).IsEqualTo("retained-terminal-event");
        await Assert.That(Text(result, "result", "evidence", "cursor")).IsEqualTo("c3");

        // The saved request is the exact prepared submission that was sent once; the saved run is the acknowledged one.
        var saved = await File.ReadAllBytesAsync(workspace.PathOf("saved.json"));
        await Assert.That(PreparedSubmission.ImportUtf8(saved).RunId.Value).IsEqualTo(Proposed);
        var retained = Encoding.UTF8.GetString(saved);
        await Assert.That(peer.Submissions.Count).IsEqualTo(1);
        await Assert.That(peer.Submissions[0]).StartsWith(retained[..retained.LastIndexOf('}')]);
        var reference = RunReference.Parse(await File.ReadAllTextAsync(workspace.PathOf("run.json")));
        await Assert.That(reference.RunId.Value).IsEqualTo(Acknowledged);
        await Assert.That(reference.Target).IsEqualTo(peer.Origin);

        // Reconnecting from the saved run file: a failed run is status data (0) but a failed completion (3).
        peer.Oecp["run/status"] = TargetPeer.Reply(call => TargetPeer.Status(call.RunId, "s9", TargetPeer.Failed));
        var status = await workspace.RunAsync("status", "--run-file", "run.json", "--json");
        await Assert.That(status.ExitCode).IsEqualTo(0);
        var report = CliResult.Record(status.Stdout);
        await Assert.That(Text(report, "kind")).IsEqualTo("status");
        await Assert.That(Text(report, "runId")).IsEqualTo(Acknowledged);
        await Assert.That(Text(report, "status", "status", "phase")).IsEqualTo("finished");
        await Assert.That(Text(report, "result", "succeeded")).IsEqualTo("false");
        await Assert.That(Text(report, "result", "failureReason")).IsEqualTo("runtime_failed");

        var wait = await workspace.RunAsync("wait", "--run-file", "run.json", "--json");
        await Assert.That(wait.ExitCode).IsEqualTo(3);
        await Assert.That(wait.Stderr).IsEmpty();
        await Assert.That(Text(CliResult.Record(wait.Stdout), "result", "evidence", "kind")).IsEqualTo("status-report");
        await Assert.That(peer.Submissions.Count).IsEqualTo(1);
        await Assert.That(peer.Count("run/force")).IsEqualTo(0);
    }

    [Test]
    public async Task DetachedRunFromAPreparedFileSendsTheExactRetainedBytesAndDoesNotObserve()
    {
        await using var peer = new TargetPeer();
        using var workspace = Workspace(peer);
        await Assert.That((await workspace.RunAsync("prepare", "--request", "request.json", "--out", "prepared.json")).ExitCode).IsEqualTo(0);

        var result = await workspace.RunAsync("run", "--config", "target.json", "--prepared", "prepared.json", "--detach", "--json");

        await Assert.That(result.ExitCode).IsEqualTo(0);
        var submission = CliResult.Record(result.Stdout);
        await Assert.That(Text(submission, "kind")).IsEqualTo("submission");
        await Assert.That(Text(submission, "runId")).IsEqualTo(Proposed);
        await Assert.That(Text(submission, "attempt", "runIdsMatch")).IsEqualTo("true");
        var retained = await File.ReadAllTextAsync(workspace.PathOf("prepared.json"));
        await Assert.That(peer.Submissions.Count).IsEqualTo(1);
        await Assert.That(peer.Submissions[0]).StartsWith(retained[..retained.LastIndexOf('}')]);
        await Assert.That(peer.Methods).IsEmpty();
    }

    [Test]
    public async Task RunThatObservesAFailedRunReportsItAsTheResultWithExitThree()
    {
        await using var peer = new TargetPeer();
        peer.Oecp["run/status"] = TargetPeer.Reply(call => TargetPeer.Status(call.RunId, "s1", TargetPeer.Running));
        peer.Oecp["run/watch"] = TargetPeer.Watch(call => TargetPeer.WatchEvent(call.RunId, "c2", TargetPeer.Failed));
        using var workspace = Workspace(peer);

        var result = await workspace.RunAsync("run", "--config", "target.json", "--request", "request.json");

        await Assert.That(result.ExitCode).IsEqualTo(3);
        await Assert.That(result.Stderr).IsEmpty();
        await Assert.That(result.Stdout).IsEqualTo($"Submitted run {Proposed}{Environment.NewLine}Run {Proposed} failed: runtime_failed{Environment.NewLine}");
    }

    [Test]
    public async Task NativeRejectionIsAnOperationalExitWithTheAttemptAndNoRemoteText()
    {
        await using var peer = new TargetPeer
        {
            Submit = exchange => exchange.ReplyAsync(409, """{"code":"request.conflict","message":"secret-canary-4b1d"}"""),
        };
        using var workspace = Workspace(peer);

        var result = await workspace.RunAsync(Run());

        await Failure(result, 1, "rejected");
        await Assert.That(result.Stdout).IsEmpty();
        await Assert.That(result.Stderr).DoesNotContain("secret-canary-4b1d");
        await Assert.That(Text(result.Error, "attempt", "outcome")).IsEqualTo("rejected");
        await Assert.That(Text(result.Error, "attempt", "proposedRunId")).IsEqualTo(Proposed);
        await Assert.That(Text(result.Error, "runId")).IsNull();
        await Assert.That(Text(result.Error, "native", "transport")).IsEqualTo("http");
        await Assert.That(Text(result.Error, "native", "kind")).IsEqualTo("httpStatus");
        await Assert.That(Text(result.Error, "native", "httpStatus")).IsEqualTo("409");
        await Assert.That(Text(result.Error, "native", "problemCode")).IsEqualTo("request.conflict");
        await Assert.That(peer.Submissions.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ARemoteDomainCodeThatIsNotABoundedIdentifierIsOmitted()
    {
        await using var peer = new TargetPeer();
        // Native's JSON-RPC domain code is an unconstrained string: here an escape sequence and free text.
        peer.Oecp["run/force"] = call => call.FailAsync("""{"code":-32000,"message":"m","data":{"code":"NOT_FOUND\u001b[31m canary-3e9a"}}""");
        using var workspace = Workspace(peer);

        var result = await workspace.RunAsync("force-stop", Acknowledged, "--config", "target.json", "--request-only");

        await Assert.That(result.ExitCode).IsEqualTo(5); // an unrecognised domain code does not prove refusal
        await Assert.That(result.Stderr).DoesNotContain("canary-3e9a");
        await Assert.That(result.Stderr).DoesNotContain("\u001b");
        await Assert.That(result.Stderr).Contains("[oecp rpcError -32000]");
    }

    [Test]
    public async Task AnOverlongProblemCodeIsOmitted()
    {
        var code = "request.conflict." + new string('a', 50) + ".canary-3e9a";
        await using var peer = new TargetPeer { Submit = exchange => exchange.ReplyAsync(409, $$"""{"code":"{{code}}","message":"m"}""") };
        using var workspace = Workspace(peer);

        var result = await workspace.RunAsync(Run());

        await Assert.That(result.ExitCode).IsEqualTo(5); // an unrecognised 409 code does not prove refusal
        await Assert.That(result.Stderr).DoesNotContain("canary-3e9a");
        await Assert.That(Text(result.Error, "native", "httpStatus")).IsEqualTo("409");
        await Assert.That(Text(result.Error, "native", "problemCode")).IsNull();
    }

    [Test]
    public async Task ANativeForceRefusalKeepsItsRpcCodesAndNoRemoteText()
    {
        await using var peer = new TargetPeer();
        peer.Oecp["run/force"] = call => call.FailAsync("""{"code":-32000,"message":"secret-canary-7c2e","data":{"code":"NOT_FOUND"}}""");
        using var workspace = Workspace(peer);

        var result = await workspace.RunAsync(Known("force-stop", "--request-only"));

        await Failure(result, 1, "rejected");
        await Assert.That(result.Stderr).DoesNotContain("secret-canary-7c2e");
        await Assert.That(Text(result.Error, "attempt", "outcome")).IsEqualTo("rejected");
        await Assert.That(Text(result.Error, "native", "transport")).IsEqualTo("oecp");
        await Assert.That(Text(result.Error, "native", "kind")).IsEqualTo("rpcError");
        await Assert.That(Text(result.Error, "native", "rpcCode")).IsEqualTo("-32000");
        await Assert.That(Text(result.Error, "native", "domainCode")).IsEqualTo("NOT_FOUND");
        await Assert.That(peer.Count("run/force")).IsEqualTo(1);
    }

    [Test]
    public async Task AMissingBindingIsRefusedBeforeAnythingIsSent()
    {
        await using var peer = new TargetPeer();
        using var workspace = Workspace(peer, binding: false);

        await Failure(await workspace.RunAsync(Run()), 2, "binding");
        await Failure(await workspace.RunAsync(Known("status")), 2, "binding");
        await Failure(await workspace.RunAsync(Known("force-stop", "--request-only")), 2, "binding");

        await Assert.That(peer.Submissions).IsEmpty();
        await Assert.That(peer.Methods).IsEmpty();
    }

    [Test]
    public async Task AWaitTimeoutIsExitFourWithTheRunAndItsLatestEvidence()
    {
        // The budget runs on the CLI's own clock, so the test orders it through the protocol instead: the wait
        // reads status again only after the watch's event and normal close, and that read is never answered.
        // A budget that ended before the held read did not reach the scenario and is retried with a longer one;
        // runner speed changes only how long this takes, never what it asserts.
        for (var budget = 2; ; budget *= 2)
        {
            await using var peer = new TargetPeer();
            var statusReads = 0;
            var held = new TaskCompletionSource();
            peer.Oecp["run/status"] = call =>
            {
                if (Interlocked.Increment(ref statusReads) == 1) return call.ReplyAsync(TargetPeer.Status(call.RunId, "s1", TargetPeer.Running));
                held.TrySetResult();
                return peer.Hold();
            };
            peer.Oecp["run/watch"] = async call =>
            {
                await TargetPeer.Watch(watched => TargetPeer.WatchEvent(watched.RunId, "c2", TargetPeer.Running))(call);
                await call.NotifyAsync("subscription/closed", """{"subscriptionId":"w1","reason":"done"}""");
            };
            using var workspace = Workspace(peer);

            var result = await workspace.RunAsync(Known("wait", "--timeout", $"{budget}s"));

            await Failure(result, 4, "timeout"); // Any other outcome is a real failure, never a reason to retry.
            if (!held.Task.IsCompleted)
            {
                // The CLI process is allowed 60s, so 32s is the longest budget.
                if (budget >= 32) throw new InvalidOperationException($"The wait never reached its held status read within {budget}s.");
                continue;
            }
            await Assert.That(result.Stdout).IsEmpty();
            await Assert.That(Text(result.Error, "runId")).IsEqualTo(Acknowledged);
            await Assert.That(Text(result.Error, "evidence", "statusCursor")).IsEqualTo("s1");
            await Assert.That(Text(result.Error, "evidence", "lastEventCursor")).IsEqualTo("c2");
            await Assert.That(Text(result.Error, "evidence", "resumeAfter")).IsEqualTo("c2");
            await Assert.That(peer.Count("run/force")).IsEqualTo(0);
            return;
        }
    }

    [Test]
    public async Task AStatusReadFailureIsAnOperationalExit()
    {
        await using var peer = new TargetPeer();
        peer.Oecp["run/status"] = _ => throw new IOException("Scripted connection loss.");
        using var workspace = Workspace(peer);

        var result = await workspace.RunAsync(Known("status"));

        await Failure(result, 1, "operational");
        await Assert.That(result.Stdout).IsEmpty();
        await Assert.That(Text(result.Error, "runId")).IsEqualTo(Acknowledged);
        await Assert.That(Text(result.Error, "native", "transport")).IsEqualTo("oecp");
        await Assert.That(Text(result.Error, "native", "kind")).IsEqualTo("transport");
    }

    [Test]
    public async Task ALostSubmissionReplyIsAnUnknownOutcomeThatIsNeverResentOrSaved()
    {
        await using var peer = new TargetPeer { Submit = exchange => exchange.Drop() };
        using var workspace = Workspace(peer);

        var result = await workspace.RunAsync(Run("--save-run", "run.json"));

        await Failure(result, 5, "unknown-outcome");
        await Assert.That(result.Stdout).IsEmpty();
        await Assert.That(Text(result.Error, "attempt", "outcome")).IsEqualTo("unknown");
        await Assert.That(Text(result.Error, "attempt", "proposedRunId")).IsEqualTo(Proposed);
        await Assert.That(Text(result.Error, "attempt", "acknowledgedRunId")).IsNull();
        await Assert.That(Text(result.Error, "runId")).IsNull();
        await Assert.That(File.Exists(workspace.PathOf("run.json"))).IsFalse();
        await Assert.That(peer.Submissions.Count).IsEqualTo(1);
        await Assert.That(peer.Methods).IsEmpty();
    }

    [Test]
    [ExcludeOn(OS.Windows)] // SIGINT delivery
    public async Task CtrlCAfterTheSubmissionWasSentIsAnUnknownOutcome()
    {
        await using var peer = new TargetPeer();
        peer.Submit = _ => peer.Hold();
        using var workspace = Workspace(peer);

        var process = workspace.Start(Run("--save-run", "run.json"));
        await peer.Received("submit");
        await process.InterruptAsync();
        var result = await process.Completion;

        await Failure(result, 5, "unknown-outcome");
        await Assert.That(result.Stdout).IsEmpty();
        await Assert.That(Text(result.Error, "attempt", "outcome")).IsEqualTo("unknown");
        await Assert.That(Text(result.Error, "attempt", "cancelled")).IsEqualTo("true");
        await Assert.That(Text(result.Error, "attempt", "proposedRunId")).IsEqualTo(Proposed);
        await Assert.That(Text(result.Error, "runId")).IsNull();
        await Assert.That(File.Exists(workspace.PathOf("run.json"))).IsFalse();
        await Assert.That(peer.Submissions.Count).IsEqualTo(1);
    }

    [Test]
    [ExcludeOn(OS.Windows)] // SIGINT delivery
    public async Task CtrlCWhileWaitingAfterAcknowledgementIsExit130AndKeepsTheAcknowledgedRun()
    {
        await using var peer = Acknowledging(Acknowledged);
        peer.Oecp["run/status"] = TargetPeer.Reply(call => TargetPeer.Status(call.RunId, "s1", TargetPeer.Running));
        peer.Oecp["run/watch"] = TargetPeer.Watch();
        using var workspace = Workspace(peer);

        var process = workspace.Start(Run("--save-run", "run.json"));
        await peer.Received("run/watch");
        await process.InterruptAsync();
        var result = await process.Completion;

        await Failure(result, 130, "cancelled");
        await Assert.That(Text(CliResult.Record(result.Stdout), "runId")).IsEqualTo(Acknowledged);
        await Assert.That(Text(result.Error, "runId")).IsEqualTo(Acknowledged);
        await Assert.That(Text(result.Error, "attempt", "acknowledgedRunId")).IsEqualTo(Acknowledged);
        await Assert.That(Text(result.Error, "attempt", "proposedRunId")).IsEqualTo(Proposed);
        await Assert.That(Text(result.Error, "evidence", "statusCursor")).IsEqualTo("s1");
        await Assert.That(RunReference.Parse(await File.ReadAllTextAsync(workspace.PathOf("run.json"))).RunId.Value).IsEqualTo(Acknowledged);
        await Assert.That(peer.Submissions.Count).IsEqualTo(1);
        await Assert.That(peer.Count("run/force")).IsEqualTo(0);
    }

    [Test]
    public async Task ASaveRunFailureAfterAcknowledgementKeepsTheAcknowledgedRunAndDoesNotWait()
    {
        await using var peer = Acknowledging(Acknowledged);
        using var workspace = Workspace(peer);

        var result = await workspace.RunAsync(Run("--save-run", Path.Combine("absent-directory", "run.json")));

        await Failure(result, 1, "output");
        await Assert.That(Text(CliResult.Record(result.Stdout), "runId")).IsEqualTo(Acknowledged);
        await Assert.That(Text(result.Error, "runId")).IsEqualTo(Acknowledged);
        await Assert.That(Text(result.Error, "attempt", "acknowledgedRunId")).IsEqualTo(Acknowledged);
        await Assert.That(result.Stderr).Contains(Acknowledged);
        await Assert.That(peer.Submissions.Count).IsEqualTo(1);
        await Assert.That(peer.Methods).IsEmpty();
    }

    [Test]
    public async Task AClosedStdoutReaderDoesNotKeepAnAcknowledgedRunFromBeingSaved()
    {
        await using var peer = Acknowledging(Acknowledged);
        using var workspace = Workspace(peer);

        // As in `run ... | true`: the reader is gone before the submission record is written.
        var result = await workspace.RunClosingStdoutAsync(0, Run("--detach", "--save-run", "run.json"));

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.Stderr).IsEmpty();
        await Assert.That(RunReference.Parse(await File.ReadAllTextAsync(workspace.PathOf("run.json"))).RunId.Value).IsEqualTo(Acknowledged);
        await Assert.That(peer.Submissions.Count).IsEqualTo(1);
    }

    [Test]
    public async Task FileFailuresBeforeDispatchSendNothing()
    {
        await using var peer = new TargetPeer();
        using var workspace = Workspace(peer);
        workspace.Write("run.json", "earlier reference");

        await Failure(await workspace.RunAsync(Run("--save-request", Path.Combine("absent-directory", "saved.json"))), 1, "output");
        await Failure(await workspace.RunAsync(Run("--save-run", "run.json")), 2, "output-exists");

        await Assert.That(await File.ReadAllTextAsync(workspace.PathOf("run.json"))).IsEqualTo("earlier reference");
        await Assert.That(peer.Submissions).IsEmpty();
    }

    [Test]
    public async Task ForceStopSendsOneForceThenWaitsForTheTerminalResult()
    {
        await using var peer = new TargetPeer();
        peer.Oecp["run/force"] = TargetPeer.Reply(call => TargetPeer.Status(call.RunId, "f1", TargetPeer.Stopping));
        peer.Oecp["run/status"] = TargetPeer.Reply(call => TargetPeer.Status(call.RunId, "s2", TargetPeer.ForceStopped));
        using var workspace = Workspace(peer);

        var result = await workspace.RunAsync(Known("force-stop"));

        await Assert.That(result.ExitCode).IsEqualTo(3);
        var record = CliResult.Record(result.Stdout);
        await Assert.That(Text(record, "kind")).IsEqualTo("result");
        await Assert.That(Text(record, "result", "failureReason")).IsEqualTo("force_stopped");
        await Assert.That(peer.Count("run/force")).IsEqualTo(1);
    }

    [Test]
    public async Task RequestOnlyForceStopReportsTheAcknowledgementWithoutWaiting()
    {
        await using var peer = new TargetPeer();
        peer.Oecp["run/force"] = TargetPeer.Reply(call => TargetPeer.Status(call.RunId, "f1", TargetPeer.Stopping));
        using var workspace = Workspace(peer);

        var result = await workspace.RunAsync(Known("force-stop", "--request-only"));

        await Assert.That(result.ExitCode).IsEqualTo(0);
        var record = CliResult.Record(result.Stdout);
        await Assert.That(Text(record, "kind")).IsEqualTo("force");
        await Assert.That(Text(record, "runId")).IsEqualTo(Acknowledged);
        await Assert.That(Text(record, "attempt", "operation")).IsEqualTo("run/force");
        await Assert.That(Text(record, "attempt", "outcome")).IsEqualTo("acknowledged");
        await Assert.That(Text(record, "status", "status", "phase")).IsEqualTo("stopping");
        await Assert.That(peer.Count("run/force")).IsEqualTo(1);
        await Assert.That(peer.Count("run/status")).IsEqualTo(0);
    }

    [Test]
    public async Task ALostForceReplyIsAnUnknownOutcomeThatIsNeverResent()
    {
        await using var peer = new TargetPeer();
        peer.Oecp["run/force"] = _ => throw new IOException("Scripted lost reply.");
        using var workspace = Workspace(peer);

        var result = await workspace.RunAsync(Known("force-stop"));

        await Failure(result, 5, "unknown-outcome");
        await Assert.That(result.Stdout).IsEmpty();
        await Assert.That(Text(result.Error, "runId")).IsEqualTo(Acknowledged);
        await Assert.That(Text(result.Error, "attempt", "operation")).IsEqualTo("run/force");
        await Assert.That(Text(result.Error, "attempt", "outcome")).IsEqualTo("unknown");
        await Assert.That(peer.Count("run/force")).IsEqualTo(1);
        await Assert.That(peer.Count("run/status")).IsEqualTo(0);
    }

    [Test]
    [ExcludeOn(OS.Windows)] // SIGINT delivery
    public async Task CtrlCBeforeTheForceIsSentIsExit130WithNothingSent()
    {
        await using var peer = new TargetPeer();
        peer.Discovery = _ => peer.Hold();
        using var workspace = Workspace(peer);

        var process = workspace.Start(Known("force-stop"));
        await peer.Received("discover");
        await process.InterruptAsync();
        var result = await process.Completion;

        await Failure(result, 130, "cancelled");
        await Assert.That(result.Stdout).IsEmpty();
        await Assert.That(Text(result.Error, "attempt", "outcome")).IsEqualTo("not-sent");
        await Assert.That(peer.Count("run/force")).IsEqualTo(0);
    }
}
