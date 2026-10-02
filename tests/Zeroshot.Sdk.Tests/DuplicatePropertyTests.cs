using System.Text;
using TUnit.Core;
using Zeroshot.Native;
using Zeroshot.Native.Contracts;
using static Zeroshot.Client.Tests.TestKit;

namespace Zeroshot.Client.Tests;

// Native serde rejects a repeated field of a typed struct and keeps the last key of arbitrary JSON.
public sealed class DuplicatePropertyTests
{
    private static string Text(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
    private static readonly string Discovery = Text("discovery.json");
    private static readonly string Dashboard = Text("dashboard-bootstrap.json");
    private static readonly string Prepared = Text("prepared.json");
    private static readonly System.Text.Json.Nodes.JsonNode History = System.Text.Json.Nodes.JsonNode.Parse(Text("history.json"))!;

    private static string Edit(string json, string find, string replace)
    {
        Check(json.Contains(find, StringComparison.Ordinal), $"Fixture lacks {find}");
        var at = json.IndexOf(find, StringComparison.Ordinal);
        return json[..at] + replace + json[(at + find.Length)..];
    }

    private static bool Accepts<T>(string json)
    {
        try { NativeJson.DeserializeUtf8<T>(Encoding.UTF8.GetBytes(json)); return true; }
        catch (System.Text.Json.JsonException) { return false; }
    }

    private static string HistorySection(string name) => History[name]!.ToJsonString();

    [Test]
    public void DiscoveryRejectsRepeatedKnownFields()
    {
        Check(Accepts<TargetDiscoveryDocument>(Discovery));
        Check(!Accepts<TargetDiscoveryDocument>(Edit(Discovery, "{", "{\"audience\":\"controller\",")));
        Check(!Accepts<TargetDiscoveryDocument>(Edit(Discovery, "\"oauth\": {", "\"oauth\": {\"clientId\":\"zeroshot\",")));
    }

    [Test]
    public void DiscoveryIgnoresRepeatedUnknownExtensions()
    {
        Check(Accepts<TargetDiscoveryDocument>(Edit(Discovery, "\"extensions\": {", "\"extensions\": {\"future\":{},\"future\":{},")));
        Check(Accepts<TargetDiscoveryDocument>(Edit(Discovery, "\"extensions\": {", "\"extensions\": {\"future\":{\"a\":1,\"a\":2},")));
    }

    [Test]
    public void TargetProblemRejectsRepeatedFieldsAndKeepsArbitraryDetails()
    {
        const string problem = "{\"code\":\"refused\",\"message\":\"no\",\"details\":{\"a\":1}}";
        Check(Accepts<TargetHttpProblem>(problem));
        Check(!Accepts<TargetHttpProblem>(Edit(problem, "{", "{\"code\":\"refused\",")));
        Check(Accepts<TargetHttpProblem>(Edit(problem, "{\"a\":1", "{\"a\":1,\"a\":2")));
    }

    [Test]
    public void HistoryRejectsRepeatedNestedFieldsAndKeepsArbitraryInput()
    {
        var definition = HistorySection("definition");
        Check(Accepts<RunDefinition>(definition));
        Check(!Accepts<RunDefinition>(Edit(definition, "{", "{\"version\":1,")));
        Check(!Accepts<RunDefinition>(Edit(definition, "\"terminal\":{", "\"terminal\":{\"status\":\"succeeded\",")));
        Check(Accepts<RunDefinition>(Edit(definition, "{\"task\":\"authored\"", "{\"task\":\"authored\",\"task\":\"again\"")));
        var page = HistorySection("page");
        Check(!Accepts<HistoryPage>(Edit(page, "\"occurrence\":{", "\"occurrence\":{\"node\":\"work\",")));
        Check(Accepts<HistoryPage>(Edit(page, "{\"task\":\"earlier\"", "{\"task\":\"earlier\",\"task\":\"again\"")));
    }

    [Test]
    public void DashboardRejectsRepeatedKnownFieldsAndKeepsArbitraryJson()
    {
        Check(Accepts<DashboardBootstrap>(Dashboard));
        Check(!Accepts<DashboardBootstrap>(Edit(Dashboard, "{", "{\"version\":1,")));
        Check(Accepts<DashboardBootstrap>(Edit(Dashboard, "\"title\":\"RuntimePlan\"", "\"title\":\"RuntimePlan\",\"title\":\"again\"")));
    }

    private const string Diagnostic = "{\"id\":\"d1\",\"runId\":\"018f5e78-7f95-7c22-8d98-3f15af20c991\",\"code\":\"c\",\"operation\":\"o\",\"stdout\":\"\",\"stderr\":\"\",\"stdoutTruncated\":false,\"stderrTruncated\":false}";

    [Test]
    public void TypedArraysRejectNullItems()
    {
        Check(!Accepts<TargetDiscoveryDocument>(Edit(Discovery, "\"device_token\",", "\"device_token\",null,")));
        Check(Accepts<TargetOperatorDiagnostics>("{\"diagnostics\":[" + Diagnostic + "]}"));
        Check(!Accepts<TargetOperatorDiagnostics>("{\"diagnostics\":[" + Diagnostic + ",null]}"));
        Check(!Accepts<HistoryPage>(Edit(HistorySection("page"), "{\"events\":[", "{\"events\":[null,")));
        Check(Accepts<TargetDiscoveryDocument>(Edit(Discovery, "\"extensions\": {", "\"extensions\": {\"future\":[null],")));
    }

    [Test]
    public void ArrayItemsRejectRepeatedKnownFields()
    {
        Check(!Accepts<TargetOperatorDiagnostics>("{\"diagnostics\":[" + Edit(Diagnostic, "{", "{\"id\":\"d1\",") + "]}"));
    }

    [Test]
    public void DictionariesKeepTheLastRepeatedKey()
    {
        const string binding = "\"deliver\":{\"connections\":{\"github\":[\"GH_TOKEN\"]},\"kind\":\"git_delivery\"},";
        Check(Accepts<DashboardBootstrap>(Edit(Dashboard, "\"runtimeBindings\":{\"deliver\":", "\"runtimeBindings\":{" + binding + "\"deliver\":")));
        var request = NativeJson.DeserializeUtf8<ConnectionResolveRequest>(
            "{\"runId\":\"018f5e78-7f95-7c22-8d98-3f15af20c991\",\"connections\":{\"a\":[\"X\"],\"a\":[\"Y\"]}}"u8);
        Check(request.Connections["a"].Single().Value == "Y");
    }

    [Test]
    public void PreparedRequestKeepsArbitraryInitialInput()
    {
        Zeroshot.PreparedSubmission.ImportUtf8(Encoding.UTF8.GetBytes(Edit(Prepared, "\"initialInput\": null", "\"initialInput\": {\"a\":1,\"a\":2}")));
    }
}
