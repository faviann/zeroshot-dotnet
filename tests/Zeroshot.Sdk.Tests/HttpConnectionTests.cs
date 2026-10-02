using System.Collections.Immutable;
using System.Net;
using TUnit.Core;
using Zeroshot.Native;
using Zeroshot.Native.Contracts;

namespace Zeroshot.Client.Tests;

public sealed class HttpConnectionTests
{
    private const string Bearer = "ACCESS-BEARER-CANARY";
    private const string Secret = "SECRET-VALUE-CANARY";
    private static readonly TargetControlCredentials Hosted = new(TargetAuthentication.HostedOauth, Bearer);
    private static readonly TargetConnectionsDiscovery Capability = new()
    {
        Kind = "zeroshot.connections/v1", BaseUrl = "https://target.example/api/",
        RouteTemplates = new() { List = "/connections/list", Set = "/connections/set", Delete = "/connections/delete", Resolve = "/connections/resolve" },
        DynamicKinds = ["github-app-installation", new string('é', 64)]
    };
    private static TargetDiscoveryDocument Discovery(TargetConnectionsDiscovery connections)
        => TestDiscovery.Controller(TargetAuthentication.HostedOauth) with { Extensions = new() { Connections = connections } };
    private static ConnectionSetRequest SetRequest(ImmutableDictionary<string, string>? values = null) => new()
    {
        Key = new("github"), Scope = ConnectionScope.User,
        Values = values ?? ImmutableDictionary<string, string>.Empty.Add("GH_TOKEN", Secret)
    };
    private static readonly ConnectionDeleteRequest DeleteRequest = new() { Key = new("github"), Scope = ConnectionScope.Org };
    private static readonly ConnectionListRequest ListRequest = new() { Scope = ConnectionScope.User };
    private static string Summary(string scope = "user", string kind = "static") =>
        $$"""{"key":"github","scope":"{{scope}}","kind":"{{kind}}","fields":["GH_TOKEN"]}""";

    // Wire, gate, refusal and formatting rules: CapabilityConformanceTests.
    [Test]
    public async Task EachOperationDecodesItsHostOwnedResultWithOpenKinds()
    {
        foreach (var (scope, wire) in new[] { (ConnectionScope.User, "user"), (ConnectionScope.Org, "org") })
        {
            using var handler = new Handler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath switch
            {
                // Host-owned responses: any 2xx carrying a valid body, with open kind strings.
                "/api/connections/list" => Reply(request, $$"""{"connections":[{{Summary(wire, "github-app-installation")}},{{Summary(wire, "future-kind")}}]}"""),
                "/api/connections/set" => Reply(request, $$"""{"connection":{{Summary(wire)}}}""", HttpStatusCode.Created),
                _ => Reply(request, """{"deleted":false}""", HttpStatusCode.Accepted)
            }));
            using var client = ClientFor(handler);
            var list = await client.Connections.ListAsync(Discovery(Capability), new() { Scope = scope }, Hosted);
            var set = await client.Connections.SetAsync(Discovery(Capability), SetRequest() with { Scope = scope }, Hosted);
            var delete = await client.Connections.DeleteAsync(Discovery(Capability), DeleteRequest with { Scope = scope }, Hosted);

            Check(list.Connections.Select(c => c.Kind).SequenceEqual(new[] { "github-app-installation", "future-kind" }));
            Check(list.Connections.All(c => c.Scope == scope && c.Fields.Single().Value == "GH_TOKEN"));
            Check(set.Outcome == NativeAttemptOutcome.Acknowledged && set.Operation == "connections.set" && set.Failure is null);
            Check(set.Response!.Connection.Kind == ConnectionKinds.Static && set.Origin == client.Origin && set.CorrelationId != Guid.Empty);
            Check(delete.Outcome == NativeAttemptOutcome.Acknowledged && delete.Operation == "connections.delete" && delete.Response!.Deleted == false);
        }
    }

    [Test]
    public async Task StaticValueBoundsFailLocallyAndMalformedResultsAreProtocolFailures()
    {
        static ImmutableDictionary<string, string> Values(int count, Func<int, string> value) =>
            Enumerable.Range(0, count).ToImmutableDictionary(i => "FIELD_" + i, value);
        var invalidValues = new[]
        {
            ImmutableDictionary<string, string>.Empty, Values(65, _ => "v"), Values(1, _ => ""), Values(1, _ => "a\0b"),
            Values(1, _ => new string('x', 64 * 1024 + 1)), Values(5, _ => new string('x', 60 * 1024)),
            ImmutableDictionary<string, string>.Empty.Add("1_INVALID", "v")
        };
        using var handler = new Handler((request, _) => Task.FromResult(Reply(request, "{}")));
        using var client = ClientFor(handler);
        foreach (var values in invalidValues)
            await Invalid(() => client.Connections.SetAsync(Discovery(Capability), SetRequest(values), Hosted), Secret, Bearer);
        await Invalid(() => client.Connections.ListAsync(Discovery(Capability), new() { Scope = (ConnectionScope)7 }, Hosted), Secret, Bearer);
        Check(handler.Calls == 0);
        var boundary = await client.Connections.SetAsync(Discovery(Capability), SetRequest(Values(64, _ => "v")), Hosted);
        Check(boundary.Outcome == NativeAttemptOutcome.Unknown && handler.Calls == 1, "Valid bounds must dispatch.");

        foreach (var summary in new[]
        {
            """{"key":"github","scope":"team","kind":"static","fields":[]}""",
            """{"key":"github","scope":"user","kind":null,"fields":[]}""",
            """{"key":"github","scope":"user","fields":[]}""",
            """{"key":"github","scope":"user","kind":"static","fields":["1_INVALID"]}""",
            """{"key":"","scope":"user","kind":"static","fields":[]}""",
            """{"key":"github","scope":"user","kind":"static","fields":[],"values":{}}"""
        })
        {
            using var malformed = new Handler((request, _) => Task.FromResult(Reply(request,
                request.RequestUri!.AbsolutePath.EndsWith("list") ? $$"""{"connections":[{{summary}}]}""" : $$"""{"connection":{{summary}}}""")));
            using var peer = ClientFor(malformed);
            await Failure(peer.Connections.ListAsync(Discovery(Capability), ListRequest, Hosted), NativeHttpFailureKind.Protocol);
            var set = await peer.Connections.SetAsync(Discovery(Capability), SetRequest(), Hosted);
            Check(set.Outcome == NativeAttemptOutcome.Unknown && set.Failure is NativeHttpException { Kind: NativeHttpFailureKind.Protocol });
        }
        foreach (var body in new[] { """{"deleted":"true"}""", """{"deleted":true,"key":"github"}""", "{}" })
        {
            using var malformed = new Handler((request, _) => Task.FromResult(Reply(request, body)));
            using var peer = ClientFor(malformed);
            var delete = await peer.Connections.DeleteAsync(Discovery(Capability), DeleteRequest, Hosted);
            Check(delete.Outcome == NativeAttemptOutcome.Unknown && delete.Failure is NativeHttpException { Kind: NativeHttpFailureKind.Protocol });
        }
    }
}
