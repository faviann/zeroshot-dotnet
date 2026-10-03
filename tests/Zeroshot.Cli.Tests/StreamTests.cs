using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using TUnit.Assertions;
using TUnit.Core;
using TUnit.Core.Enums;

namespace Zeroshot.Cli.Tests;

/// <summary>
/// watch, logs and attach as separate processes against a loopback direct target. Records reach stdout one per line
/// as they are delivered; a normal native close adds nothing; failures keep the last delivered cursor on stderr.
/// </summary>
public sealed class StreamTests
{
    private const string RunId = "0195af77-1000-7000-8000-000000000002";
    private const string Execution = "nv2-execution-1";
    private static readonly string Release = Zeroshot.Native.NativeSchemas.NativeVersion;
    private static readonly string Revision = Zeroshot.Native.NativeSchemas.SourceRevision;
    private const ulong Timestamp = 1_767_225_600_000; // 2026-01-01T00:00:00Z

    private static CliWorkspace Workspace(TargetPeer peer, string transport = "{}")
    {
        var workspace = new CliWorkspace();
        workspace.Write("target.json", $$"""
            { "schema": "zeroshot-dotnet/target-config/v1", "target": "{{peer.Origin}}",
              "nativeBinding": { "provenance": "caller-supplied", "release": "{{Release}}", "sourceRevision": "{{Revision}}" },
              "transport": {{transport}}, "observation": { "recoveryDelay": "0ms" } }
            """);
        return workspace;
    }

    private static string[] Known(string command, params string[] extra) => [command, RunId, "--config", "target.json", "--json", .. extra];

    private static string? Text(JsonElement record, params string[] path)
    {
        foreach (var name in path)
            if (!record.TryGetProperty(name, out record)) return null;
        return record.ValueKind == JsonValueKind.String ? record.GetString() : record.GetRawText();
    }

    private static string Kinds(CliResult result) => string.Join(",", result.Records.Select(r => Text(r, "kind")));
    private static string Cursors(CliResult result) => string.Join(",", result.Records.Select(r => Text(r, "cursor")));

    private static string LogEvent(string cursor, string? execution = Execution, string message = "step done")
        => $$$"""{"subscriptionId":"l1","runId":"{{{RunId}}}","cursor":"{{{cursor}}}","timestamp":{{{Timestamp}}},{{{(execution is null ? "" : $"\"execution\":\"{execution}\",")}}}"record":{"level":"warn","target":"worker","message":"{{{message}}}"}}""";

    private static string AttachEvent(string body) => $$"""{"subscriptionId":"a1","runId":"{{RunId}}","execution":"{{Execution}}","event":{{body}}}""";

    /// <summary>
    /// A subscription answer: the establishment (its <c>atCursor</c> echoes <c>fromCursor</c>), each event, then a
    /// <c>done</c> close, or a dropped connection when <paramref name="drop"/> is set, or silence when neither.
    /// </summary>
    private static Func<TargetPeer.Call, Task> Open(string id, string[] events, bool close = false, bool drop = false,
        ConcurrentQueue<JsonNode?>? requests = null) => async call =>
    {
        requests?.Enqueue(call.Params?.DeepClone());
        var establishment = id == "a1"
            ? $$"""{"subscriptionId":"a1","runId":"{{call.RunId}}","execution":{{call.Params!["execution"]!.ToJsonString()}}}"""
            : $$"""{"subscriptionId":"{{id}}","runId":"{{call.RunId}}","atCursor":{{call.Params?["fromCursor"]?.ToJsonString() ?? "\"start\""}}}""";
        await call.ReplyAsync(establishment);
        foreach (var record in events) await call.NotifyAsync("event", record);
        if (close) await call.NotifyAsync("subscription/closed", $$"""{"subscriptionId":"{{id}}","reason":"done"}""");
        if (drop) throw new IOException("Scripted connection loss.");
    };

    private static async Task Failure(CliResult result, int exitCode, string category)
    {
        await Assert.That(result.ExitCode).IsEqualTo(exitCode);
        await Assert.That(Text(result.Error, "category")).IsEqualTo(category);
        await Assert.That(Text(result.Error, "runId")).IsEqualTo(RunId);
        await Assert.That(result.Stderr).DoesNotContain(PrepareTests.Title);
    }

    [Test]
    public async Task WatchReplaysAfterTheCursorAndANormalCloseAddsNoResult()
    {
        await using var peer = new TargetPeer();
        var requests = new ConcurrentQueue<JsonNode?>();
        peer.Oecp["run/watch"] = Open("w1",
            [TargetPeer.WatchEvent(RunId, "c2", TargetPeer.Running), TargetPeer.WatchEvent(RunId, "c3", TargetPeer.Succeeded)],
            close: true, requests: requests);
        using var workspace = Workspace(peer);

        var result = await workspace.RunAsync(Known("watch", "--after", "c1"));

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.Stderr).IsEmpty();
        await Assert.That(requests.Single()!["fromCursor"]!.GetValue<string>()).IsEqualTo("c1");
        // Two native records and nothing else: the retained success is watch data, not a synthesized result.
        await Assert.That(Kinds(result)).IsEqualTo("watch,watch");
        await Assert.That(Cursors(result)).IsEqualTo("c2,c3");
        var last = result.Records[1];
        await Assert.That(Text(last, "runId")).IsEqualTo(RunId);
        await Assert.That(Text(last, "data", "cursor")).IsEqualTo("c3");
        await Assert.That(Text(last, "data", "title")).IsEqualTo(PrepareTests.Title);
        await Assert.That(Text(last, "data", "status", "terminalResult", "output", "answer")).IsEqualTo("42");
        await Assert.That(Text(last, "checkpoint", "stream")).IsEqualTo("watch");
        await Assert.That(Text(last, "checkpoint", "cursor")).IsEqualTo("c3");
        await Assert.That(Text(last, "checkpoint", "execution")).IsEqualTo("null");
        await Assert.That(peer.Count("run/status")).IsEqualTo(0);
    }

    [Test]
    public async Task LogsFilterToOneExecutionAndResumeFromARecordsCheckpoint()
    {
        await using var peer = new TargetPeer();
        var requests = new ConcurrentQueue<JsonNode?>();
        peer.Oecp["run/logs"] = Open("l1", [LogEvent("c2"), LogEvent("c3")], close: true, requests: requests);
        using var workspace = Workspace(peer);

        var first = await workspace.RunAsync(Known("logs", "--execution", Execution, "--after", "c1"));

        await Assert.That(first.ExitCode).IsEqualTo(0);
        await Assert.That(first.Stderr).IsEmpty();
        await Assert.That(Kinds(first)).IsEqualTo("log,log");
        var record = first.Records[1];
        await Assert.That(Text(record, "cursor")).IsEqualTo("c3");
        await Assert.That(Text(record, "execution")).IsEqualTo(Execution);
        await Assert.That(Text(record, "timestamp")).IsEqualTo(Timestamp.ToString());
        await Assert.That(Text(record, "data", "record", "level")).IsEqualTo("warn");
        await Assert.That(Text(record, "data", "record", "message")).IsEqualTo("step done");
        await Assert.That(Text(record, "checkpoint", "stream")).IsEqualTo("logs");
        await Assert.That(Text(record, "checkpoint", "execution")).IsEqualTo(Execution);

        // The record's checkpoint, saved as-is, resumes the same filtered stream after it.
        workspace.Write("checkpoint.json", record.GetProperty("checkpoint").GetRawText());
        var resumed = await workspace.RunAsync(Known("logs", "--execution", Execution, "--checkpoint", "checkpoint.json"));
        await Assert.That(resumed.ExitCode).IsEqualTo(0);

        // A checkpoint is refused for any other scope, before anything is sent.
        var unfiltered = await workspace.RunAsync(Known("logs", "--checkpoint", "checkpoint.json"));
        await Assert.That(unfiltered.ExitCode).IsEqualTo(2);
        await Assert.That(Text(unfiltered.Error, "category")).IsEqualTo("input");

        var sent = requests.ToArray();
        await Assert.That(sent.Length).IsEqualTo(2);
        foreach (var (request, from) in sent.Zip(["c1", "c3"]))
        {
            await Assert.That(request!["execution"]!.GetValue<string>()).IsEqualTo(Execution);
            await Assert.That(request["fromCursor"]!.GetValue<string>()).IsEqualTo(from);
        }
    }

    [Test]
    public async Task ReadableOutputIsOneLinePerRecord()
    {
        await using var peer = new TargetPeer();
        peer.Oecp["run/logs"] = Open("l1", [LogEvent("c2"), LogEvent("c3", execution: null, message: "run-wide")], close: true);
        peer.Oecp["run/watch"] = Open("w1",
            [TargetPeer.WatchEvent(RunId, "c4", TargetPeer.Running), TargetPeer.WatchEvent(RunId, "c5", TargetPeer.Failed)], close: true);
        using var workspace = Workspace(peer);

        var logs = await workspace.RunAsync("logs", RunId, "--config", "target.json");
        var watch = await workspace.RunAsync("watch", RunId, "--config", "target.json");

        var nl = Environment.NewLine;
        await Assert.That(logs.ExitCode).IsEqualTo(0);
        await Assert.That(logs.Stdout).IsEqualTo(
            $"c2 2026-01-01T00:00:00.000Z warn worker [{Execution}]: step done{nl}c3 2026-01-01T00:00:00.000Z warn worker: run-wide{nl}");
        await Assert.That(watch.ExitCode).IsEqualTo(0);
        await Assert.That(watch.Stdout).IsEqualTo($"c4 Run {RunId} is running{nl}c5 Run {RunId} failed: runtime_failed{nl}");
    }

    [Test]
    public async Task AnInterruptedWatchResumesAfterTheLastDeliveredRecord()
    {
        await using var peer = new TargetPeer();
        var requests = new ConcurrentQueue<JsonNode?>();
        var opened = 0;
        var first = Open("w1", [TargetPeer.WatchEvent(RunId, "c2", TargetPeer.Running)], drop: true, requests: requests);
        var second = Open("w1", [TargetPeer.WatchEvent(RunId, "c3", TargetPeer.Succeeded)], close: true, requests: requests);
        peer.Oecp["run/watch"] = call => Interlocked.Increment(ref opened) == 1 ? first(call) : second(call);
        using var workspace = Workspace(peer);

        var result = await workspace.RunAsync(Known("watch"));

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.Stderr).IsEmpty();
        await Assert.That(Cursors(result)).IsEqualTo("c2,c3");
        var sent = requests.ToArray();
        await Assert.That(sent[0]?["fromCursor"]).IsNull();
        await Assert.That(sent[1]!["fromCursor"]!.GetValue<string>()).IsEqualTo("c2");
    }

    [Test]
    public async Task WithRecoveryOffAnInterruptionFailsWithTheLastDeliveredCursor()
    {
        await using var peer = new TargetPeer();
        peer.Oecp["run/watch"] = Open("w1", [TargetPeer.WatchEvent(RunId, "c2", TargetPeer.Running)], drop: true);
        using var workspace = Workspace(peer);

        var result = await workspace.RunAsync(Known("watch", "--recovery", "none"));

        await Failure(result, 1, "operational");
        await Assert.That(Cursors(result)).IsEqualTo("c2");
        await Assert.That(Text(result.Error, "observation", "failure")).IsEqualTo("interrupted");
        await Assert.That(Text(result.Error, "observation", "recoveries")).IsEqualTo("0");
        await Assert.That(Text(result.Error, "observation", "lastDeliveredCursor")).IsEqualTo("c2");
        await Assert.That(peer.Count("run/watch")).IsEqualTo(1);
    }

    [Test]
    public async Task ARecordBeyondTheMessageLimitEndsTheStreamWithoutRecovery()
    {
        await using var peer = new TargetPeer();
        peer.Oecp["run/logs"] = Open("l1", [LogEvent("c2"), LogEvent("c3", message: new string('x', 8000))]);
        using var workspace = Workspace(peer, transport: """{ "maxMessageBytes": 4096 }""");

        var result = await workspace.RunAsync(Known("logs"));

        await Failure(result, 1, "operational");
        await Assert.That(Cursors(result)).IsEqualTo("c2");
        await Assert.That(Text(result.Error, "observation", "failure")).IsEqualTo("resource-limit");
        await Assert.That(Text(result.Error, "observation", "lastDeliveredCursor")).IsEqualTo("c2");
        await Assert.That(peer.Count("run/logs")).IsEqualTo(1);
    }

    [Test]
    [ExcludeOn(OS.Windows)] // SIGINT delivery
    public async Task CtrlCDetachesWithExit130AndTheLastDeliveredCursor()
    {
        await using var peer = new TargetPeer();
        peer.Oecp["run/watch"] = Open("w1", [TargetPeer.WatchEvent(RunId, "c2", TargetPeer.Running)]); // then quiet
        using var workspace = Workspace(peer);

        var process = workspace.Start(Known("watch"));
        await process.StdoutLines(1);
        await process.InterruptAsync();
        var result = await process.Completion;

        await Failure(result, 130, "cancelled");
        await Assert.That(Kinds(result)).IsEqualTo("watch");
        await Assert.That(Text(result.Error, "observation", "lastDeliveredCursor")).IsEqualTo("c2");
        await Assert.That(peer.Count("run/force")).IsEqualTo(0);
    }

    [Test]
    public async Task AClosedStdoutEndsTheStreamWithoutStoppingTheRun()
    {
        await using var peer = new TargetPeer();
        // A live stream that keeps producing until its reader goes away.
        peer.Oecp["run/watch"] = async call =>
        {
            await call.ReplyAsync($$"""{"subscriptionId":"w1","runId":"{{call.RunId}}","atCursor":"start"}""");
            for (var i = 1; i <= 600; i++)
            {
                await call.NotifyAsync("event", TargetPeer.WatchEvent(RunId, $"c{i}", TargetPeer.Running));
                await Task.Delay(50);
            }
        };
        using var workspace = Workspace(peer);

        var result = await workspace.RunClosingStdoutAsync(1, Known("watch"));

        await Failure(result, 1, "output");
        await Assert.That(Cursors(result)).IsEqualTo("c1"); // the one line the reader kept
        await Assert.That(Text(result.Error, "observation", "lastDeliveredCursor")).StartsWith("c");
        await Assert.That(peer.Count("run/force")).IsEqualTo(0);
    }

    [Test]
    public async Task AttachmentStreamsLiveEventsWithoutACursorOrResult()
    {
        await using var peer = new TargetPeer();
        var requests = new ConcurrentQueue<JsonNode?>();
        peer.Oecp["run/attach"] = Open("a1",
            [AttachEvent("""{"type":"working"}"""), AttachEvent("""{"type":"output","text":"hello"}"""), AttachEvent("""{"type":"settled"}""")],
            close: true, requests: requests);
        using var workspace = Workspace(peer);

        var result = await workspace.RunAsync("attach", RunId, Execution, "--config", "target.json", "--json");
        var readable = await workspace.RunAsync("attach", RunId, Execution, "--config", "target.json");

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.Stderr).IsEmpty();
        await Assert.That(Kinds(result)).IsEqualTo("attachment,attachment,attachment");
        var output = result.Records[1];
        await Assert.That(Text(output, "runId")).IsEqualTo(RunId);
        await Assert.That(Text(output, "execution")).IsEqualTo(Execution);
        await Assert.That(Text(output, "data", "event", "text")).IsEqualTo("hello");
        await Assert.That(Text(output, "cursor")).IsNull();
        await Assert.That(Text(output, "checkpoint")).IsNull();
        // Attachment asks for exactly one live execution: no cursor to replay from.
        await Assert.That(string.Join(",", requests.First()!.AsObject().Select(p => p.Key))).IsEqualTo("runId,execution");

        var nl = Environment.NewLine;
        await Assert.That(readable.Stdout).IsEqualTo($"Execution {Execution} is working{nl}hello{nl}Execution {Execution} settled{nl}");
    }

    [Test]
    public async Task AnInterruptedAttachmentIsNeverReopened()
    {
        await using var peer = new TargetPeer();
        peer.Oecp["run/attach"] = Open("a1", [AttachEvent("""{"type":"working"}""")], drop: true);
        using var workspace = Workspace(peer);

        var result = await workspace.RunAsync("attach", RunId, Execution, "--config", "target.json", "--json");

        await Failure(result, 1, "operational");
        await Assert.That(Kinds(result)).IsEqualTo("attachment");
        await Assert.That(Text(result.Error, "observation", "failure")).IsEqualTo("unexpected-disconnect");
        await Assert.That(Text(result.Error, "observation", "lastDeliveredCursor")).IsNull();
        await Assert.That(peer.Count("run/attach")).IsEqualTo(1);
    }

    [Test]
    public async Task ACursorBeginningWithDashesIsGivenWithAnEqualsSign()
    {
        await using var peer = new TargetPeer();
        var requests = new ConcurrentQueue<JsonNode?>();
        peer.Oecp["run/watch"] = Open("w1", [], close: true, requests: requests);
        using var workspace = Workspace(peer);

        var result = await workspace.RunAsync(Known("watch", "--after=--c1"));
        var separate = await workspace.RunAsync(Known("watch", "--after", "--c1"));

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(requests.Single()!["fromCursor"]!.GetValue<string>()).IsEqualTo("--c1");
        // The separate form still treats a following option as a missing value.
        await Assert.That(separate.ExitCode).IsEqualTo(2);
        await Assert.That(Text(separate.Error, "category")).IsEqualTo("invocation");
    }
}
