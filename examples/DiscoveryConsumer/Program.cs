using Zeroshot.Native;
using Zeroshot.Native.Contracts;

if (args.Length != 1) throw new ArgumentException("Supply an existing target origin.");
await using var native = NativeClient.ForHttp(new NativeClientOptions { Origin = new Uri(args[0]) });
TargetDiscoveryDocument discovery = await native.Target.DiscoverAsync();
if (discovery.Authentication != TargetAuthentication.None || discovery.RunPath != "/native-v2/run" ||
    discovery.SessionPath != "/native-v2/oecp-session" || discovery.OecpPath != "/native-v2/oecp")
    throw new InvalidOperationException("Unexpected direct target discovery.");
Console.WriteLine(System.Text.Encoding.UTF8.GetString(NativeJson.SerializeUtf8(discovery)));
