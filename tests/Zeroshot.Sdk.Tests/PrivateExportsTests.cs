using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TUnit.Core;
using Zeroshot.Native;
using Zeroshot.Native.Contracts;

namespace Zeroshot.Client.Tests;

public sealed class PrivateExportsTests
{
    private const string Run = "018f5e78-7f95-7c22-8d98-3f15af20c991";
    private const string Capability = "PRIVATE-CAPABILITY-CANARY";
    private const string Output = "STDOUT-CANARY";
    private static readonly TargetControlCredentials Private = new(TargetAuthentication.PrivateCapability, Capability);
    private static readonly JsonNode History = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/history.json")))!;
    private static string Diagnostics(string? stdout = null) => $$"""
        {"diagnostics":[
          {"id":"1","runId":"{{Run}}","code":"git_checkout_failed","operation":"source.checkout","exitStatus":128,
           "stdout":{{JsonSerializer.Serialize(stdout ?? Output)}},"stderr":"fatal: repository not found\n","stdoutTruncated":false,"stderrTruncated":true},
          {"id":"2","runId":"{{Run}}","code":"runtime_failed","operation":"supervisor.drive",
           "stdout":"","stderr":"supervisor.drive: lost","stdoutTruncated":false,"stderrTruncated":false}]}
        """;
    private static void Check(bool value, string message = "Private export assertion failed.")
    { if (!value) throw new InvalidOperationException(message); }

    private static NativeClient Client(Handler handler) => NativeClient.ForHttp(
        new NativeClientOptions { Origin = new Uri("https://target.example/") }, new HttpClient(handler), ownsHttpClient: true);

    [Test]
    public async Task ReadsUseTheFixedRoutesExactBodiesAndPrivateCapability()
    {
        var seen = new List<(HttpRequestMessage Request, string? Body)>();
        using var native = Client(new Handler(async (request, token) =>
        {
            seen.Add((request, request.Content is null ? null : await request.Content.ReadAsStringAsync(token)));
            var path = request.RequestUri!.AbsolutePath;
            return Reply(request, path.EndsWith("/page") ? History["page"]!.ToJsonString()
                : path.EndsWith("/definition") ? History["definition"]!.ToJsonString() : Diagnostics());
        }));
        var run = new RunId(Run);
        var snapshot = await native.Private.GetOperatorDiagnosticsAsync(run, Private);
        var definition = await native.Private.GetHistoryDefinitionAsync(run, Private);
        var first = await native.Private.GetHistoryPageAsync(run, Private);
        // The page is validated against the requested cursor: this fixture page starts at v2:1, not after v2:4.
        await Expect(native.Private.GetHistoryPageAsync(run, Private, new Cursor("v2:4")), NativeHttpFailureKind.Protocol);
        Check(seen.Select(s => s.Request.Method.Method + " " + s.Request.RequestUri!.PathAndQuery + " " + s.Body).SequenceEqual(new[]
        {
            $"GET /native-v2/operator-diagnostics/{Run} ",
            $$"""POST /native-v2/history/definition {"runId":"{{Run}}"}""",
            $$"""POST /native-v2/history/page {"runId":"{{Run}}"}""",
            $$"""POST /native-v2/history/page {"runId":"{{Run}}","after":"v2:4"}"""
        }));
        Check(seen.All(s => s.Request.Headers.Authorization is { Scheme: "Bearer", Parameter: Capability }));
        Check(definition.RunId == run && first.Events.Length == 9);

        // Truncation flags and an omitted exit status survive exactly; default formatting shows no payload.
        var failed = snapshot.Diagnostics[0];
        Check(failed is { ExitStatus: 128, StdoutTruncated: false, StderrTruncated: true, Stdout: Output } &&
            snapshot.Diagnostics[1].ExitStatus is null);
        Check(JsonNode.DeepEquals(JsonNode.Parse(Diagnostics()), JsonNode.Parse(NativeJson.SerializeUtf8(snapshot))));
        Check(!snapshot.ToString().Contains(Output) && !failed.ToString().Contains(Output));
    }

    public static IEnumerable<(string Case, Func<NativeClient, Task> Call)> InvalidUses()
    {
        var run = new RunId(Run);
        yield return ("absent capability", n => n.Private.GetOperatorDiagnosticsAsync(run, null!));
        yield return ("hosted credentials", n => n.Private.GetHistoryDefinitionAsync(run, new(TargetAuthentication.HostedOauth, Capability)));
        yield return ("non-UUIDv7 run", n => n.Private.GetOperatorDiagnosticsAsync(new RunId("018f5e78-7f95-4c22-8d98-3f15af20c991"), Private));
        yield return ("non-canonical cursor", n => n.Private.GetHistoryPageAsync(run, Private, new Cursor("v2:01")));
    }

    [Test]
    [MethodDataSource(nameof(InvalidUses))]
    public async Task InvalidAuthorityOrArgumentsAreNeverSent(string name, Func<NativeClient, Task> call)
    {
        var handler = new Handler((request, _) => Task.FromResult(Reply(request, "{}")));
        using var native = Client(handler);
        try { await call(native); }
        catch (ArgumentException error) { Check(handler.Calls == 0 && !error.ToString().Contains(Capability), name); return; }
        throw new InvalidOperationException("Expected invalid use: " + name);
    }

    public static IEnumerable<(string Operation, int Status, string Code, RunHistoryProblemCode? History)> Refusals()
    {
        yield return ("diagnostics", 401, "request.unauthorized", null);
        yield return ("definition", 404, "request.not_found", null); // A target that is not in private mode.
        yield return ("definition", 404, "run_not_found", RunHistoryProblemCode.RunNotFound);
        yield return ("page", 400, "invalid_cursor", RunHistoryProblemCode.InvalidCursor);
    }

    [Test]
    [MethodDataSource(nameof(Refusals))]
    public async Task RefusalsKeepStatusProblemAndOperationSpecificCategory(string operation, int status, string code, RunHistoryProblemCode? history)
    {
        const string message = "PROBLEM-MESSAGE-CANARY";
        using var native = Client(new Handler((request, _) => Task.FromResult(
            Reply(request, JsonSerializer.Serialize(new { code, message }), (HttpStatusCode)status))));
        var error = await Expect(Call(native, operation), NativeHttpFailureKind.HttpStatus);
        Check(error.StatusCode == (HttpStatusCode)status && error.Problem?.Code == code && error.HistoryProblem == history, $"{operation} {code}");
        Check(!error.ToString().Contains(message) && !error.ToString().Contains(Capability));
    }

    public static IEnumerable<(string Case, string Operation, Func<string> Body, NativeHttpFailureKind Kind)> MalformedResponses()
    {
        static string Diagnostic(Action<JsonNode> change) { var node = JsonNode.Parse(Diagnostics())!; change(node["diagnostics"]![0]!); return node.ToJsonString(); }
        yield return ("foreign run", "diagnostics", () => Diagnostic(d => d["runId"] = "0195af77-1000-7000-8000-000000000010"), NativeHttpFailureKind.Protocol);
        yield return ("stderr beyond 4 KiB", "diagnostics", () => Diagnostic(d => d["stderr"] = new string('é', 2049)), NativeHttpFailureKind.Protocol);
        yield return ("truncation flag omitted", "diagnostics", () => Diagnostic(d => d.AsObject().Remove("stdoutTruncated")), NativeHttpFailureKind.Protocol);
        yield return ("over 64 KiB", "diagnostics", () => Diagnostics() + new string(' ', 64 * 1024), NativeHttpFailureKind.SizeLimit);
        yield return ("foreign definition", "definition", () => { var d = History["definition"]!.DeepClone(); d["runId"] = "0195af77-1000-7000-8000-000000000010"; return d.ToJsonString(); }, NativeHttpFailureKind.Protocol);
        yield return ("definition over 8 MiB", "definition", () => History["definition"]!.ToJsonString() + new string(' ', 8 * 1024 * 1024), NativeHttpFailureKind.SizeLimit);
        yield return ("page gap", "page", () => { var p = History["page"]!.DeepClone(); p["events"]![4]!["cursor"] = "v2:6"; return p.ToJsonString(); }, NativeHttpFailureKind.Protocol);
        yield return ("page over 8 MiB", "page", () => History["page"]!.ToJsonString() + new string(' ', 8 * 1024 * 1024), NativeHttpFailureKind.SizeLimit);
    }

    [Test]
    [MethodDataSource(nameof(MalformedResponses))]
    public async Task MalformedOrOversizedResponsesFailBounded(string name, string operation, Func<string> body, NativeHttpFailureKind kind)
    {
        var text = body();
        using var native = Client(new Handler((request, _) => Task.FromResult(Reply(request, text))));
        var error = await Expect(Call(native, operation), kind);
        Check(error.StatusCode == HttpStatusCode.OK && !error.ToString().Contains(Output), name);
    }

    [Test]
    public async Task DiagnosticTextAtNativeBoundIsAccepted()
    {
        // 4096 UTF-8 bytes in 2048 two-byte characters: native cuts on a character boundary within 4 KiB.
        var text = new string('é', 2048);
        using var native = Client(new Handler((request, _) => Task.FromResult(Reply(request, Diagnostics(text)))));
        var snapshot = await native.Private.GetOperatorDiagnosticsAsync(new RunId(Run), Private);
        Check(snapshot.Diagnostics[0].Stdout == text);
    }

    private static Task Call(NativeClient native, string operation) => operation switch
    {
        "diagnostics" => native.Private.GetOperatorDiagnosticsAsync(new RunId(Run), Private),
        "definition" => native.Private.GetHistoryDefinitionAsync(new RunId(Run), Private),
        _ => native.Private.GetHistoryPageAsync(new RunId(Run), Private)
    };

    private static async Task<NativeHttpException> Expect(Task task, NativeHttpFailureKind kind)
    {
        try { await task; }
        catch (NativeHttpException error) { Check(error.Kind == kind, error.ToString()); return error; }
        throw new InvalidOperationException("Expected HTTP failure.");
    }

    private static HttpResponseMessage Reply(HttpRequestMessage request, string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { RequestMessage = request, Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
