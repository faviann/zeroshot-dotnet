using Zeroshot.Native;
using Zeroshot.Native.Contracts;

if (args.Length != 1) throw new ArgumentException("Supply an existing target origin.");
await using var native = NativeClient.ForHttp(new NativeClientOptions { Origin = new Uri(args[0]) });
TargetDiscoveryDocument discovery = await native.Target.DiscoverAsync();
if (discovery.Authentication != TargetAuthentication.None || discovery.RunPath != "/native-v2/run" ||
    discovery.SessionPath != "/native-v2/oecp-session" || discovery.OecpPath != "/native-v2/oecp")
    throw new InvalidOperationException("Unexpected direct target discovery.");
TargetOecpSession session = await native.Target.CreateOecpSessionAsync(discovery);
TargetOecpSession selected = await native.Target.CreateOecpSessionAsync(discovery,
    new TargetOecpSessionRequest { RunId = new RunId("0195af77-1000-7000-8000-000000000001") });
var expectedEndpoint = new UriBuilder(native.Origin) { Scheme = native.Origin.Scheme == "https" ? "wss" : "ws", Path = "/native-v2/oecp" }.Uri;
if (new Uri(session.Endpoint) != expectedEndpoint || selected.Endpoint != session.Endpoint ||
    session.BearerToken is not null || selected.BearerToken is not null)
    throw new InvalidOperationException("Unexpected direct target session authority.");
Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { discovery, session, selected }));

await using var oecp = await native.ConnectOecpAsync(session);
var initialized = await oecp.InitializeAsync();
if (initialized.ProtocolVersion != OecpConnection.ProtocolVersion || !initialized.Capabilities.Logs || !initialized.Capabilities.AgentAttach)
    throw new InvalidOperationException("Unexpected native OECP capabilities.");
var empty = await oecp.Cluster.GetAsync();
if (empty.Spec is not null || empty.AtCursor is not null || empty.TerminalResult is not null || empty.Status.Phase != Phase.Empty)
    throw new InvalidOperationException("Native cluster get must remain empty even when runs are present.");
var inventory = await oecp.Runs.ListAsync();
if (inventory.Runs.Length != 1) throw new InvalidOperationException("Expected the witness run in the native inventory.");
var known = inventory.Runs[0];
var expectedSource = new ResolvedSource { Repository = new("acme/project"), Branch = new("main"), Revision = new(new string('a', 40)) };
var runId = new RunId("018f5e78-7f95-7c22-8d98-3f15af20c991");
if (known.RunId != runId || known.Source != expectedSource) throw new InvalidOperationException("Native inventory identity mismatch.");
var status = await oecp.Runs.StatusAsync(runId, expectedSource);
// Allocation may fail on hosts without native containment privileges. Such an actual native
// terminal failure is valid inspection evidence, not a successful execution claim.
using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
while (status.Status is not FinishedRunStatus)
{
    await Task.Delay(100, budget.Token);
    status = await oecp.Runs.StatusAsync(runId, expectedSource, cancellationToken: budget.Token);
}
JsonRpcError unsupported;
try
{
    await oecp.InitializeAsync(new() { ProtocolVersion = "openengine.cluster/unsupported-witness" });
    throw new InvalidOperationException("Native accepted unsupported protocol.");
}
catch (NativeOecpException error) when (error.Kind == NativeOecpFailureKind.RpcError && error.RpcError?.Data?.Code == "UNSUPPORTED_PROTOCOL_VERSION")
{ unsupported = error.RpcError; }
Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
{
    initialize = System.Text.Json.JsonDocument.Parse(NativeJson.SerializeUtf8(initialized)).RootElement,
    cluster = System.Text.Json.JsonDocument.Parse(NativeJson.SerializeUtf8(empty)).RootElement,
    inventory = System.Text.Json.JsonDocument.Parse(NativeJson.SerializeUtf8(inventory)).RootElement,
    status = System.Text.Json.JsonDocument.Parse(NativeJson.SerializeUtf8(status)).RootElement,
    unsupported = System.Text.Json.JsonDocument.Parse(NativeJson.SerializeUtf8(unsupported)).RootElement
}));
