using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Zeroshot.Native;
using Zeroshot.Native.Contracts;

if (args.Length != 3) throw new ArgumentException("Supply the private target origin, its material directory and the inspection request.");
// The harness owns this isolated material; the client library performs no bootstrap cryptography.
var key = Convert.FromHexString(File.ReadAllText(Path.Combine(args[1], "bootstrap-key.hex")));
var capability = File.ReadAllText(Path.Combine(args[1], "capability.hex"));
using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var token = budget.Token;
await using var native = NativeClient.ForHttp(new() { Origin = new Uri(args[0]) });
var discovery = await native.Target.DiscoverAsync(token);
Check(discovery is { Authentication: TargetAuthentication.PrivateCapability, PrivateBootstrapPath: not null }, "private discovery");

// A well-formed envelope under another key fails native authentication and leaves the bootstrap open.
var invalid = await native.Private.BootstrapAsync(discovery, Envelope(RandomNumberGenerator.GetBytes(32), capability), token);
Check(invalid is { Outcome: NativeAttemptOutcome.Rejected, Failure: NativeHttpException { StatusCode: System.Net.HttpStatusCode.BadRequest, Problem.Code: "request.invalid" } }, "invalid envelope");
var envelope = Envelope(key, capability);
var accepted = await native.Private.BootstrapAsync(discovery, envelope, token);
Check(accepted is { Outcome: NativeAttemptOutcome.Acknowledged, Response: not null, Failure: null }, "accepted envelope: " + accepted.Failure?.Message);
// Native consumed its key: the same valid envelope now finds the bootstrap closed.
var closed = await native.Private.BootstrapAsync(discovery, envelope, token);
Check(closed is { Outcome: NativeAttemptOutcome.Rejected, Failure: NativeHttpException { StatusCode: System.Net.HttpStatusCode.NotFound, Problem.Code: "request.not_found" } }, "closed bootstrap");

// Operator exports with the bootstrapped capability. Admission uses the same capability as control authority.
var operatorAuthority = new TargetControlCredentials(TargetAuthentication.PrivateCapability, capability);
var inspection = NativeJson.DeserializeUtf8<TargetRunRequest>(File.ReadAllBytes(args[2]));
var runId = inspection.RunId;
var submitted = await native.Target.SubmitAttemptAsync(inspection, operatorAuthority, token);
Check(submitted is { Outcome: NativeAttemptOutcome.Acknowledged, RunIdsMatch: true }, "private admission: " + submitted.Failure?.Message);
var definition = await native.Private.GetHistoryDefinitionAsync(runId, operatorAuthority, token);
while (definition.Phase != RunPhase.Finished)
{
    await Task.Delay(100, token);
    definition = await native.Private.GetHistoryDefinitionAsync(runId, operatorAuthority, token);
}
Check(definition.Title == inspection.Submission.Title && definition.Source == inspection.Submission.Source, "private definition");
var page = await native.Private.GetHistoryPageAsync(runId, operatorAuthority, cancellationToken: token);
Check(page.Complete && page.NextCursor == definition.Cursor && page.Events[^1].Event is TerminalHistoryEvent, "private complete page");
var end = await native.Private.GetHistoryPageAsync(runId, operatorAuthority, page.NextCursor, token);
Check(end.Events.IsEmpty && end.Complete && end.NextCursor == page.NextCursor, "private page boundary is exclusive");
// Native records diagnostics only for checkout/push failures; this run's list is recorded as received.
var diagnostics = await native.Private.GetOperatorDiagnosticsAsync(runId, operatorAuthority, token);

var unknown = new RunId("0195af77-9999-7000-8000-000000000001");
var unknownDiagnostics = await native.Private.GetOperatorDiagnosticsAsync(unknown, operatorAuthority, token);
Check(unknownDiagnostics.Diagnostics.IsEmpty, "unknown run has no diagnostics");
var missing = new[]
{
    await Refused(native.Private.GetHistoryDefinitionAsync(unknown, operatorAuthority, token)),
    await Refused(native.Private.GetHistoryPageAsync(unknown, operatorAuthority, cancellationToken: token))
};
Check(missing.All(error => error is { StatusCode: System.Net.HttpStatusCode.NotFound, HistoryProblem: RunHistoryProblemCode.RunNotFound, Problem.Code: "run_not_found" }), "unknown run history");
var ahead = await Refused(native.Private.GetHistoryPageAsync(runId, operatorAuthority, new Cursor("v2:999999"), token));
Check(ahead is { StatusCode: System.Net.HttpStatusCode.BadRequest, HistoryProblem: RunHistoryProblemCode.InvalidCursor }, "cursor ahead");
var wrong = new TargetControlCredentials(TargetAuthentication.PrivateCapability, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)));
var unauthorized = new[]
{
    await Refused(native.Private.GetOperatorDiagnosticsAsync(runId, wrong, token)),
    await Refused(native.Private.GetHistoryDefinitionAsync(runId, wrong, token)),
    await Refused(native.Private.GetHistoryPageAsync(runId, wrong, cancellationToken: token))
};
Check(unauthorized.All(error => error is { StatusCode: System.Net.HttpStatusCode.Unauthorized, Problem.Code: "request.unauthorized", HistoryProblem: null }), "wrong capability");

Console.WriteLine(JsonSerializer.Serialize(new
{
    discovery = Wire(discovery),
    invalid = Evidence(invalid),
    accepted = Evidence(accepted),
    closed = Evidence(closed),
    exports = new
    {
        admitted = submitted.AcknowledgedRunId!.Value,
        definition = new { phase = definition.Phase.ToString(), cursor = definition.Cursor.Value, terminal = Wire(definition.Terminal!) },
        page = Wire(page),
        diagnostics = Wire(diagnostics),
        unknownDiagnostics = Wire(unknownDiagnostics),
        refusals = missing.Append(ahead).Concat(unauthorized)
            .Select(error => new { error.Operation, status = (int)error.StatusCode!, problem = Wire(error.Problem!) })
    }
}));

static async Task<NativeHttpException> Refused(Task task)
{
    try { await task; }
    catch (NativeHttpException error) when (error.Kind == NativeHttpFailureKind.HttpStatus) { return error; }
    throw new InvalidOperationException("Native private export witness failed: expected a refusal.");
}

static TargetPrivateBootstrapRequest Envelope(byte[] key, string capability)
{
    var nonce = RandomNumberGenerator.GetBytes(12);
    var plaintext = Encoding.ASCII.GetBytes(capability);
    var ciphertext = new byte[plaintext.Length + 16];
    using var aes = new AesGcm(key, 16);
    aes.Encrypt(nonce, plaintext, ciphertext.AsSpan(0, plaintext.Length), ciphertext.AsSpan(plaintext.Length),
        "zeroshot-capsule-bootstrap-v1"u8);
    return new() { Nonce = Convert.ToHexStringLower(nonce), Ciphertext = Convert.ToHexStringLower(ciphertext) };
}
static object Evidence(NativeAttempt<EmptyResponse> attempt) => new
{
    outcome = attempt.Outcome.ToString(),
    attempt.CorrelationId,
    status = (attempt.Failure as NativeHttpException)?.StatusCode is { } status ? (int)status : (int?)null,
    problem = (attempt.Failure as NativeHttpException)?.Problem is { } problem ? Wire(problem) : (JsonElement?)null
};
static JsonElement Wire<T>(T value)
{
    using var document = JsonDocument.Parse(NativeJson.SerializeUtf8(value));
    return document.RootElement.Clone();
}
static void Check(bool condition, string evidence)
{
    if (!condition) throw new InvalidOperationException("Native private bootstrap witness failed: " + evidence);
}
