using System.Text.Json;
using System.Text.Json.Nodes;
using Zeroshot.Native;
using Zeroshot.Native.Contracts;

if (args.Length != 3 || args[2] is not ("live" or "restarted"))
    throw new ArgumentException("Supply an existing target origin, witness directory and live/restarted phase.");
var directory = args[1];
var phase = args[2];
var request = NativeJson.DeserializeUtf8<TargetRunRequest>(File.ReadAllBytes(Path.Combine(directory, "observation-request.json")));
var runId = request.RunId;
var source = request.Submission.Source;
using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var token = budget.Token;
await using var native = NativeClient.ForHttp(new() { Origin = new Uri(args[0]) });
var discovery = await native.Target.DiscoverAsync(cancellationToken: token);
var session = await native.Target.CreateOecpSessionAsync(discovery, cancellationToken: token);
await using var connection = await native.ConnectOecpAsync(session, token);
await connection.InitializeAsync(cancellationToken: token);

if (phase == "live")
{
    var accepted = await native.Target.SubmitAttemptAsync(request, cancellationToken: token);
    Check(accepted.Outcome == NativeAttemptOutcome.Acknowledged && accepted.AcknowledgedRunId == runId, "observation admission identity");
    Cursor? existingHistory = null;
    await using (var preparation = await connection.Runs.LogsAsync(new() { RunId = runId }, cancellationToken: token))
    {
        await foreach (var entry in preparation.ReadAllAsync(token))
        {
            if (entry.Record.Message.Value != "[setup] history-ready") continue;
            existingHistory = entry.Cursor;
            break;
        }
    }
    Check(existingHistory is not null, "preparation reached controlled gate");
    var beforeEstablishment = await connection.Runs.StatusAsync(runId, source, cancellationToken: token);
    Check(beforeEstablishment.Status is AdmittedRunStatus && beforeEstablishment.AtCursor == existingHistory, "history predates both subscriptions");
    // The setup hook writes its first line and then waits. Both subscriptions are
    // acknowledged, and history is delivered, before this consumer releases it.
    await using var logs = await connection.Runs.LogsAsync(new() { RunId = runId }, cancellationToken: token);
    await using var watch = await connection.Runs.WatchAsync(new() { RunId = runId }, source, token);
    var historyReady = new TaskCompletionSource<RunLogEventNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
    var logsTask = Read(logs, token, entry =>
    {
        if (entry.Record.Message.Value == "[setup] history-ready") historyReady.TrySetResult(entry);
    });
    var watchTask = Read(watch, token);
    var history = await historyReady.Task.WaitAsync(token);
    Check(history.Cursor == existingHistory, "preexisting history replayed");
    var beforeRelease = await connection.Runs.StatusAsync(runId, source, cancellationToken: token);
    Check(beforeRelease.Status is AdmittedRunStatus && beforeRelease.AtCursor == history.Cursor, "history exists before gate release");
    // Retained run history is readable while the run is admitted and not terminal.
    var activeDefinition = await native.History.DetailAsync(discovery, runId, cancellationToken: token);
    Check(activeDefinition is { Phase: RunPhase.Admitted, Terminal: null, HistoryAvailable: true, History.Complete: false } &&
        activeDefinition.Cursor == history.Cursor, "history definition while the run is active");
    var activePage = await native.History.PageAsync(discovery, runId, cancellationToken: token);
    Check(activePage is { Complete: true, Finished: false } && activePage.HeadCursor == history.Cursor &&
        activePage.Events.Any(entry => entry.Event is SafeLogHistoryEvent { Execution: null }), "history page while the run is active");
    await File.WriteAllTextAsync(Path.Combine(directory, "observation-release"), "release", token);
    await Task.WhenAll(logsTask, watchTask);
    var logEvents = await logsTask;
    var watchEvents = await watchTask;
    Check(logEvents.Count >= 2 && logEvents.All(entry => entry.RunId == runId && entry.Execution is null), "native preparation logs");
    Check(logEvents.Select(entry => entry.Cursor).Distinct().Count() == logEvents.Count, "distinct delivered log cursors");
    var historyIndex = logEvents.FindIndex(entry => entry.Cursor == history.Cursor);
    Check(historyIndex >= 0 && historyIndex + 1 < logEvents.Count &&
        logEvents[historyIndex + 1].Record.Message.Value == "[setup] live-after-subscription", "genuinely live log after establishment");
    Check(watchEvents.Count == 1 && watchEvents[0].RunId == runId && watchEvents[0].Source == source &&
        watchEvents[0].Status is FinishedRunStatus { TerminalResult: FailedTerminalResult { Reason.Value: "environment_setup_failed" } }, "genuinely live terminal watch");
    var logClose = await Done(logs, token);
    var watchClose = await Done(watch, token);
    Check(logs.LastDeliveredCursor == logEvents[^1].Cursor && logClose.LastDeliveredCursor == logs.LastDeliveredCursor, "log delivery and server close positions");
    Check(watch.LastDeliveredCursor == watchEvents[^1].Cursor && watchClose.LastDeliveredCursor == watch.LastDeliveredCursor, "watch delivery and server close positions");
    var finishedPage = await native.History.PageAsync(discovery, runId, cancellationToken: token);
    Check(finishedPage is { Complete: true, Finished: true } && finishedPage.Events[^1] is
        { Event: TerminalHistoryEvent { Result: FailedTerminalResult { Reason.Value: "environment_setup_failed" } } } terminal &&
        terminal.Cursor == watchEvents[0].Cursor, "retained terminal event in history");
    File.WriteAllBytes(Path.Combine(directory, "observation-history.json"), NativeJson.SerializeUtf8(finishedPage));
    SaveEvents(directory, "observation-logs.json", logEvents);
    SaveEvents(directory, "observation-watch.json", watchEvents);
    File.WriteAllText(Path.Combine(directory, "observation-history-cursor.txt"), history.Cursor.Value);
    File.WriteAllText(Path.Combine(directory, "observation-live.json"), JsonSerializer.Serialize(new
    {
        beforeEstablishment = Wire(beforeEstablishment), beforeRelease = Wire(beforeRelease),
        logs = Wire(logs.Establishment), watch = Wire(watch.Establishment),
        logClose = Wire(logClose), watchClose = Wire(watchClose),
        historyCursor = history.Cursor.Value, liveLogCursor = logEvents[historyIndex + 1].Cursor.Value,
        liveWatchCursor = watchEvents[0].Cursor.Value
    }));
}

// The second invocation occurs only after the shell has stopped and restarted
// stock native against the same storage. Cursors are retained and reused verbatim.
var baselineLogs = LoadEvents<RunLogEventNotification>(directory, "observation-logs.json");
var baselineWatch = LoadEvents<RunWatchEventNotification>(directory, "observation-watch.json");
var boundary = new Cursor(File.ReadAllText(Path.Combine(directory, "observation-history-cursor.txt")));
var status = await connection.Runs.StatusAsync(runId, source, cancellationToken: token);
Check(status.AtCursor == baselineWatch[^1].Cursor && status.Status is FinishedRunStatus, "exact terminal run after observation/restart");
var replay = await Replay(connection, runId, source, boundary, baselineLogs, baselineWatch, token);
var retained = NativeJson.DeserializeUtf8<HistoryPage>(File.ReadAllBytes(Path.Combine(directory, "observation-history.json")));
var restartedPage = await native.History.PageAsync(discovery, runId, cancellationToken: token);
Check(restartedPage.Complete && Events(restartedPage) == Events(retained), "identical retained history page after restart");
Console.WriteLine(JsonSerializer.Serialize(new { phase, status = Wire(status), replay }));

static async Task<object> Replay(OecpConnection connection, RunId runId, ResolvedSource source, Cursor boundary,
    List<RunLogEventNotification> baselineLogs, List<RunWatchEventNotification> baselineWatch, CancellationToken token)
{
    await using var logs = await connection.Runs.LogsAsync(new() { RunId = runId, FromCursor = boundary }, cancellationToken: token);
    await using var watch = await connection.Runs.WatchAsync(new() { RunId = runId, FromCursor = boundary }, source, token);
    var logTask = Read(logs, token);
    var watchTask = Read(watch, token);
    await Task.WhenAll(logTask, watchTask);
    var logEvents = await logTask;
    var watchEvents = await watchTask;
    Check(logs.Establishment.AtCursor == boundary && watch.Establishment.AtCursor == boundary, "opaque requested replay position");
    var expectedLogs = baselineLogs.SkipWhile(entry => entry.Cursor != boundary).Skip(1).ToList();
    Check(expectedLogs.Count > 0 && Same(logEvents, expectedLogs), "exclusive log replay preserves every remaining record");
    Check(Same(watchEvents, baselineWatch), "watch terminal history replay");
    var logClose = await Done(logs, token);
    var watchClose = await Done(watch, token);

    await using var afterLogs = await connection.Runs.LogsAsync(new() { RunId = runId, FromCursor = baselineLogs[^1].Cursor }, cancellationToken: token);
    await using var afterWatch = await connection.Runs.WatchAsync(new() { RunId = runId, FromCursor = baselineWatch[^1].Cursor }, source, token);
    Check((await Read(afterLogs, token)).Count == 0 && (await Read(afterWatch, token)).Count == 0, "both stream boundaries excluded");
    await Done(afterLogs, token);
    await Done(afterWatch, token);
    return new
    {
        fromCursor = boundary.Value, logs = logEvents.Select(Wire).ToArray(), watch = watchEvents.Select(Wire).ToArray(),
        logClose = Wire(logClose), watchClose = Wire(watchClose), terminalBoundaryReplayEmpty = true
    };
}

static async Task<List<TEvent>> Read<TEstablishment, TEvent>(NativeSubscription<TEstablishment, TEvent> subscription,
    CancellationToken token, Action<TEvent>? delivered = null)
    where TEstablishment : NativeContract where TEvent : NativeContract
{
    var events = new List<TEvent>();
    await foreach (var entry in subscription.ReadAllAsync(token))
    {
        events.Add(entry);
        delivered?.Invoke(entry);
    }
    return events;
}

static async Task<SubscriptionClosedNotification> Done<TEstablishment, TEvent>(NativeSubscription<TEstablishment, TEvent> subscription,
    CancellationToken token) where TEstablishment : NativeContract where TEvent : NativeContract
{
    var completion = await subscription.Completion.WaitAsync(token);
    Check(completion.Origin == NativeSubscriptionOrigin.ServerClosed &&
        completion.ServerClose?.Reason == SubscriptionCloseReason.Done, "authoritative server completion");
    return completion.ServerClose!;
}

static JsonElement Wire<T>(T value)
{
    using var document = JsonDocument.Parse(NativeJson.SerializeUtf8(value));
    return document.RootElement.Clone();
}
static void SaveEvents<T>(string directory, string file, List<T> events)
    => File.WriteAllText(Path.Combine(directory, file), JsonSerializer.Serialize(events.Select(Wire).ToArray()));
static List<T> LoadEvents<T>(string directory, string file)
{
    using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, file)));
    return document.RootElement.EnumerateArray().Select(element => NativeJson.DeserializeUtf8<T>(System.Text.Encoding.UTF8.GetBytes(element.GetRawText()))).ToList();
}
static string Events(HistoryPage page)
    => string.Join('\n', page.Events.Select(entry => System.Text.Encoding.UTF8.GetString(NativeJson.SerializeUtf8(entry))));
static bool Same<T>(List<T> actual, List<T> expected) => actual.Select(Content).SequenceEqual(expected.Select(Content));
static string Content<T>(T value)
{
    var node = JsonNode.Parse(NativeJson.SerializeUtf8(value))!.AsObject();
    node.Remove("subscriptionId");
    return node.ToJsonString();
}
static void Check(bool condition, string evidence)
{
    if (!condition) throw new InvalidOperationException("Native observation witness failed: " + evidence);
}
