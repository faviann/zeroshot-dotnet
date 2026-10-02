using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Zeroshot;
using Zeroshot.Native;
using Zeroshot.Native.Contracts;

namespace Zeroshot.Sdk.Tests;

public sealed class RunHandleTests
{
    private const string Revision = "75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa";
    private const string RunA = "0195af77-1000-7000-8000-000000000001";
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");
    private static readonly NativeBinding Supported = NativeBinding.CallerSupplied("10.9.0", Revision);
    private static T Catch<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T error) { return error; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
    private static async Task<T> CatchAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T error) { return error; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
    private static JsonObject Submission()
        => JsonNode.Parse(File.ReadAllText(Path.Combine(Fixtures, "prepared.json")))!["submission"]!.AsObject();
    private static RunRequest Parse(JsonObject request) => RunRequest.ParseUtf8(Encoding.UTF8.GetBytes(request.ToJsonString()));
    private static JsonNode Exported(PreparedSubmission prepared) => JsonNode.Parse(prepared.ExportUtf8())!;

    [Test]
    public void RequestPreparationGeneratesOmittedIdentityOnceAndKeepsSuppliedIdentity()
    {
        var omitted = Submission();
        omitted.Remove("submissionKey");
        var request = Parse(omitted);
        Check(request.RunId is null && request.SubmissionKey is null && !request.Environment.HasValue, "Omitted fields stay omitted.");
        var first = request.Prepare();
        var second = request.Prepare();
        Check(Guid.TryParseExact(first.RunId.Value, "D", out var id) && id.Version == 7 && first.RunId.Value == id.ToString("D"), "Canonical UUIDv7.");
        Check(System.Text.RegularExpressions.Regex.IsMatch(first.Submission.SubmissionKey.Value, @"\Adotnet-[0-9a-f]{32}\z"), "dotnet- key.");
        Check(first.RunId != second.RunId && first.Submission.SubmissionKey != second.Submission.SubmissionKey, "Each preparation is new work.");
        Check(Exported(first)["submission"]!.AsObject().ContainsKey("environment") == false, "Omitted environment is not exported.");

        var supplied = Submission();
        supplied["runId"] = RunA;
        supplied["environment"] = null;
        var fixedRequest = Parse(supplied);
        var prepared = fixedRequest.Prepare();
        Check(prepared.RunId.Value == RunA && prepared.Submission.SubmissionKey.Value == "debug-fixture", "Supplied identity is kept.");
        Check(fixedRequest.Prepare().ExportUtf8().AsSpan().SequenceEqual(prepared.ExportUtf8()), "Supplied identity prepares identically.");
        var environment = Exported(prepared)["submission"]!.AsObject();
        Check(environment.ContainsKey("environment") && environment["environment"] is null, "Explicit null environment is kept.");
    }

    [Test]
    public void RequestParsingRejectsUnknownDuplicateAndNonCanonicalFields()
    {
        var unknown = Submission(); unknown["credentials"] = "secret";
        var nonCanonical = Submission(); nonCanonical["runId"] = "run-1";
        var duplicate = Submission().ToJsonString().Replace("\"title\":\"test\"", "\"title\":\"test\",\"title\":\"test\"");
        foreach (var bytes in new[] { unknown.ToJsonString(), nonCanonical.ToJsonString(), duplicate, "[]" })
            Check(!Catch<JsonException>(() => RunRequest.ParseUtf8(Encoding.UTF8.GetBytes(bytes))).Message.Contains("secret"), "Diagnostics omit values.");
    }

    [Test]
    public void RunReferenceExportsTheV1FormatAndRejectsAnythingElse()
    {
        var reference = new RunReference(new Uri("https://target.example"), new RunId(RunA), Supported);
        var json = reference.ToJson();
        Check(json == $$$"""{"schema":"zeroshot-dotnet/run-reference/v1","target":"https://target.example/","runId":"{{{RunA}}}","nativeBinding":{"provenance":"caller-supplied","release":"10.9.0","sourceRevision":"{{{Revision}}}"}}""", json);
        foreach (var (from, to) in new[]
        {
            ("run-reference/v1", "run-reference/v2"),
            ("\"caller-supplied\"", "\"remote-attested\""),
            ("\"runId\"", "\"credentials\":\"x\",\"runId\""),
            (",\"runId\":\"" + RunA + "\"", ""),
            ("\"sourceRevision\"", "\"hash\":\"x\",\"sourceRevision\""),
            ("https://target.example/", "https://target.example/path"),
            ("https://target.example/", "http://target.example/"),
            ("\"10.9.0\"", "10"),
        })
            Catch<JsonException>(() => RunReference.Parse(json.Replace(from, to)));
    }

    [Test]
    public async Task SdkObservationCannotTakeControlConnections()
    {
        var oecp = new OecpPeer(Fixtures);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var listener = OecpPeer.LoopbackListener();
        _ = oecp.ListenWebSocketsAsync(listener, stop.Token);
        using var http = new HttpClient(new HttpPeer(Fixtures, "{}"));
        await using var native = NativeClient.ForHttp(new NativeClientOptions
        {
            Origin = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/"),
            Transport = new() { MaxOecpConnections = 3 }
        }, http);
        await using var sdk = new ZeroshotClient(native, Supported);
        var run = sdk.GetRun(new RunId("run-1"));

        var held = new List<IAsyncEnumerator<RunAttachEventNotification>>();
        for (var i = 0; i < 2; i++)
        {
            var attachment = run.AttachAsync(new ExecutionRef("held")).GetAsyncEnumerator();
            Check(await attachment.MoveNextAsync() && attachment.Current.Event is WorkingAgentAttachEvent, "Held attachment.");
            held.Add(attachment);
        }
        // Two observations plus this client's control slot fill three connections.
        var refused = await CatchAsync<NativeSubscriptionException>(async () => { await foreach (var _ in run.AttachAsync(new ExecutionRef("held"))) { } });
        Check(refused.Kind == NativeSubscriptionFailureKind.Admission, "Observation admission.");
        Catch<InvalidOperationException>(() => new ZeroshotClient(native, Supported));
        Check((await run.StatusAsync()).RunId == run.Id, "Control stays available under observation.");
        Check(oecp.Methods.Count(m => m == "run/attach") == 2, "The refused observation dispatched nothing.");

        // Ending the observations returns their connections to the budget.
        foreach (var attachment in held) await attachment.DisposeAsync();
        await using (new ZeroshotClient(native, Supported)) { }
        stop.Cancel();
    }
}
