using System.Net;
using System.Text.Json;
using Zeroshot.Native;
using Zeroshot.Native.Contracts;

if (args.Length != 2) throw new ArgumentException("Supply an existing target origin and witness directory.");
var directory = args[1];
var inspection = NativeJson.DeserializeUtf8<TargetRunRequest>(File.ReadAllBytes(Path.Combine(directory, "request.json")));
using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var token = budget.Token;
await using var native = NativeClient.ForHttp(new() { Origin = new Uri(args[0]) });
var discovery = await native.Target.DiscoverAsync(token);
Check(discovery.Extensions.RunHistory is { Kind: "zeroshot.run-history/v1" }, "direct target advertises run history");

// History availability is reported per run, independently of whether the run is terminal.
var list = await native.History.ListAsync(discovery, cancellationToken: token);
Check(list.Runs.Length >= 2 && list.NextCursor is null, "retained runs on one list page");
Check(list.Runs.All(run => run.HistoryAvailable && run.Cursor is not null && run.Source is not null), "admitted runs have history");
Check(list.Runs.Single(run => run.RunId == inspection.RunId) is { Phase: RunHistoryPhase.Finished, Terminal: FailedHistorySynopsis },
    "terminal inspection run keeps available history");
var after = await native.History.ListAsync(discovery, list.Runs[0].RunId, cancellationToken: token);
Check(after.Runs.Select(Json).SequenceEqual(list.Runs.Skip(1).Select(Json)), "list resumes strictly after a run");

var runId = inspection.RunId;
var definition = await native.History.DetailAsync(discovery, runId, cancellationToken: token);
Check(Json(definition.Graph) == Json(inspection.Submission.Graph) && Json(definition.Runtime) == Json(inspection.Submission.Runtime) &&
    definition.Source == inspection.Submission.Source && definition.Title == inspection.Submission.Title, "admitted definition");
Check(definition.Phase == RunPhase.Finished && definition.Terminal is FailedTerminalResult && definition.History.Complete, "retained terminal definition");
var page = await native.History.PageAsync(discovery, runId, cancellationToken: token);
Check(page.Complete && page.Finished && page.NextCursor == definition.Cursor && page.Events[^1].Event is TerminalHistoryEvent, "complete retained page");
var end = await native.History.PageAsync(discovery, runId, page.NextCursor, cancellationToken: token);
Check(end.Events.IsEmpty && end.Complete && end.NextCursor == page.NextCursor, "page boundary is exclusive");

var heads = new[]
{
    await native.History.HeadListAsync(discovery, cancellationToken: token),
    await native.History.HeadDetailAsync(discovery, runId, cancellationToken: token),
    await native.History.HeadPageAsync(discovery, runId, cancellationToken: token)
};
Check(heads.All(head => head.StatusCode == HttpStatusCode.OK && head.ContentLength > 0 && head.MediaType == "application/json"), "direct mount HEAD");

var unknown = new RunId("0195af77-9999-7000-8000-000000000001");
var missing = await Refused(native.History.DetailAsync(discovery, unknown, cancellationToken: token));
Check(missing is { StatusCode: HttpStatusCode.NotFound, HistoryProblem: RunHistoryProblemCode.RunNotFound, UiProblem.Code: "run_not_found", Problem: null },
    "unknown run problem from the UI router");
var ahead = await Refused(native.History.PageAsync(discovery, runId, new Cursor("v2:999999"), cancellationToken: token));
Check(ahead is { StatusCode: HttpStatusCode.BadRequest, HistoryProblem: RunHistoryProblemCode.InvalidCursor }, "cursor ahead problem");
var headMissing = await Refused(native.History.HeadDetailAsync(discovery, unknown, cancellationToken: token));
Check(headMissing is { StatusCode: HttpStatusCode.NotFound, Problem: null, UiProblem: null }, "HEAD refusal has status only");

// Private exports on a direct target: native refuses before checking any capability.
var operatorAuthority = new TargetControlCredentials(TargetAuthentication.PrivateCapability, "not-a-private-target");
var wrongMode = new[]
{
    await Refused(native.Private.GetOperatorDiagnosticsAsync(runId, operatorAuthority, token)),
    await Refused(native.Private.GetHistoryDefinitionAsync(runId, operatorAuthority, token)),
    await Refused(native.Private.GetHistoryPageAsync(runId, operatorAuthority, cancellationToken: token))
};
Check(wrongMode.All(error => error is { StatusCode: HttpStatusCode.NotFound, Problem.Code: "request.not_found", HistoryProblem: null }),
    "private exports refused by a direct target");

// The UI router would otherwise keep a pooled history connection and answer this control request with 404.
var session = await native.Target.CreateOecpSessionAsync(discovery, cancellationToken: token);
Check(session.BearerToken is null, "control request after history on the same client");

Console.WriteLine(JsonSerializer.Serialize(new
{
    list = Wire(list), definition = new { phase = definition.Phase.ToString(), cursor = definition.Cursor.Value, terminal = Wire(definition.Terminal!) },
    page = Wire(page), heads = heads.Select(head => new { head.StatusCode, head.ContentLength }),
    problems = new[] { missing.HistoryProblem.ToString(), ahead.HistoryProblem.ToString() },
    privateExportsWrongMode = wrongMode.Select(error => new { error.Operation, status = (int)error.StatusCode!, error.Problem!.Code }),
    sessionAfterHistory = true
}));

static async Task<NativeHttpException> Refused(Task task)
{
    try { await task; }
    catch (NativeHttpException error) when (error.Kind == NativeHttpFailureKind.HttpStatus) { return error; }
    throw new InvalidOperationException("Native history witness failed: expected a refusal.");
}
static string Json<T>(T value) where T : notnull => System.Text.Encoding.UTF8.GetString(NativeJson.SerializeUtf8(value));
static JsonElement Wire<T>(T value) where T : notnull
{
    using var document = JsonDocument.Parse(NativeJson.SerializeUtf8(value));
    return document.RootElement.Clone();
}
static void Check(bool condition, string evidence)
{
    if (!condition) throw new InvalidOperationException("Native history witness failed: " + evidence);
}
