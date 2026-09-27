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
