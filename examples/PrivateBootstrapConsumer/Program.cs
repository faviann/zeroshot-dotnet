using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Zeroshot.Native;
using Zeroshot.Native.Contracts;

if (args.Length != 2) throw new ArgumentException("Supply the private target origin and its material directory.");
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
Console.WriteLine(JsonSerializer.Serialize(new
{
    discovery = Wire(discovery),
    invalid = Evidence(invalid),
    accepted = Evidence(accepted),
    closed = Evidence(closed)
}));

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
