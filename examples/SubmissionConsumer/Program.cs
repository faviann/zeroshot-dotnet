using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Zeroshot;
using Zeroshot.Native;
using Zeroshot.Native.Contracts;

if (args.Length != 3) throw new ArgumentException("Supply an existing target origin, complete asset directory and evidence directory.");
await using var native = NativeClient.ForHttp(new() { Origin = new Uri(args[0]) });
var graphBytes = File.ReadAllBytes(Path.Combine(args[1], "graph.json"));
var runtimeBytes = File.ReadAllBytes(Path.Combine(args[1], "runtime.json"));
var graph = NativeJson.DeserializeUtf8<GraphSpec>(graphBytes);
var explicitRuntime = NativeJson.DeserializeUtf8<CodexRuntime>(runtimeBytes);
Check(explicitRuntime.Provider == CodexProvider.Gateway && explicitRuntime.Nodes.Values.OfType<AgentBinding>().Any(), "complete provider asset");
Check(explicitRuntime.Nodes.Values.OfType<GitDeliveryBinding>().Any(), "complete PR delivery asset");
var implicitRuntime = explicitRuntime with
{
    Nodes = explicitRuntime.Nodes.ToImmutableDictionary(pair => pair.Key, pair => pair.Value is AgentBinding agent
        ? (NodeRuntimeBinding)(agent with { Connections = default }) : pair.Value)
};
var submission = new RunSubmission
{
    Title = new("Complete asset admission witness"), Graph = graph,
    InitialInput = JsonSerializer.SerializeToElement(new { task = "Test-owned admission only; no live provider or forge credentials." }),
    Runtime = implicitRuntime,
    Source = new() { Repository = new("acme/project"), Branch = new("main"), Revision = new(new string('a', 40)) },
    SubmissionKey = new("complete-normalization-witness")
};
var proposed = new RunId("0195af77-1000-7000-8000-000000000010");
var empty = ImmutableDictionary<string, ImmutableDictionary<string, string>>.Empty;
var github = ImmutableDictionary<string, string>.Empty.Add("GH_TOKEN", "witness-only-github");
var connections = empty.Add("github", github).Add("gateway", ImmutableDictionary<string, string>.Empty
    .Add("GATEWAY_API_KEY", "witness-only-provider").Add("GATEWAY_BASE_URL", "http://127.0.0.1:1"));
var prepared = PreparedSubmission.Create(proposed, submission);
var retainedBytes = prepared.ExportUtf8();
var retainedPath = Path.Combine(args[2], "complete-retained.json");
File.WriteAllBytes(retainedPath, retainedBytes);
var imported = PreparedSubmission.ImportUtf8(File.ReadAllBytes(retainedPath));

// With agent connections omitted, contained native normalization still requires
// the gateway fields. A partial outer map is a native admission refusal.
var refused = await native.Target.SubmitAttemptAsync(imported, new() { Connections = empty.Add("github", github) });
Check(refused.Outcome == NativeAttemptOutcome.Rejected && refused.Failure is NativeHttpException
    { StatusCode: System.Net.HttpStatusCode.BadRequest, Problem.Code: "run.rejected" }, "contained provider admission");

var accepted = await native.Target.SubmitAttemptAsync(imported, new() { Connections = connections, GithubToken = "witness-only-source" });
Check(accepted.Outcome == NativeAttemptOutcome.Acknowledged && accepted.RunIdsMatch == true, "complete asset admission");

// Native normalizes omitted agent access before identity. Authoring the explicit
// equivalent changes the retained bytes, but resolves the existing submission.
var alternate = new RunId("0195af77-1000-7000-8000-000000000011");
var normalized = await native.Target.SubmitAttemptAsync(new TargetRunRequest
{
    RunId = alternate, Submission = submission with { Runtime = explicitRuntime }, Connections = empty
});
Check(normalized.Outcome == NativeAttemptOutcome.Acknowledged && normalized.ProposedRunId == alternate &&
    normalized.AcknowledgedRunId == proposed && normalized.RunIdsMatch == false, "normalized deduplication and identity");

// Exact retained replay neither regenerates the submission nor refreshes stored
// credentials. An empty replacement map is ignored by native's duplicate lookup.
var replay = await native.Target.SubmitAttemptAsync(imported, new() { Connections = empty });
Check(replay.Outcome == NativeAttemptOutcome.Acknowledged && replay.AcknowledgedRunId == proposed &&
    replay.Prepared.ExportUtf8().SequenceEqual(retainedBytes) && File.ReadAllBytes(retainedPath).SequenceEqual(retainedBytes), "exact retained replay");

var conflict = await native.Target.SubmitAttemptAsync(new TargetRunRequest
{
    RunId = alternate, Submission = submission with { Title = new("Different immutable content") }, Connections = empty
});
Check(conflict.Outcome == NativeAttemptOutcome.Rejected && conflict.Failure is NativeHttpException
    { StatusCode: System.Net.HttpStatusCode.Conflict, Problem.Code: "request.conflict" }, "immutable conflict");

Console.WriteLine(JsonSerializer.Serialize(new
{
    asset = new { graphSha256 = Convert.ToHexStringLower(SHA256.HashData(graphBytes)), runtimeSha256 = Convert.ToHexStringLower(SHA256.HashData(runtimeBytes)), runtimeNodes = explicitRuntime.Nodes.Count },
    retainedSha256 = Convert.ToHexStringLower(SHA256.HashData(retainedBytes)),
    admission = Evidence(accepted), normalization = Evidence(normalized), replay = Evidence(replay),
    rejected = Evidence(refused), conflict = Evidence(conflict)
}));

static object Evidence(TargetSubmissionAttempt attempt) => new
{
    outcome = attempt.Outcome.ToString(), proposedRunId = attempt.ProposedRunId.Value,
    acknowledgedRunId = attempt.AcknowledgedRunId?.Value, attempt.RunIdsMatch, attempt.CorrelationId,
    status = (attempt.Failure as NativeHttpException)?.StatusCode,
    code = (attempt.Failure as NativeHttpException)?.Problem?.Code
};
static void Check(bool condition, string evidence)
{
    if (!condition) throw new InvalidOperationException("Native witness failed: " + evidence);
}
