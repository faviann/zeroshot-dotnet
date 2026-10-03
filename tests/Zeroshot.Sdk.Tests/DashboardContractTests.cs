using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TUnit.Core;
using Zeroshot.Native;
using Zeroshot.Native.Contracts;

namespace Zeroshot.Client.Tests;

public sealed class DashboardContractTests
{
    private static void Rejected<T>(JsonNode json)
    {
        try { NativeJson.DeserializeUtf8<T>(Encoding.UTF8.GetBytes(json.ToJsonString())); }
        catch (JsonException) { return; }
        throw new InvalidOperationException($"Accepted malformed {typeof(T).Name}: {json.ToJsonString()}");
    }
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Test]
    public void BootstrapChecksNestedPinnedSchemasAndNativeRules()
    {
        static JsonNode Bootstrap() => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/dashboard-bootstrap.json")))!;
        var repeatedLabels = Bootstrap();
        repeatedLabels["templates"]![0]!["graph"]!["root"]!["children"]![1]!["branches"]![0]!["when"]!["labels"] = new JsonArray("crash", "crash");
        Rejected<DashboardBootstrap>(repeatedLabels);
        var emptyConnection = Bootstrap();
        emptyConnection["workers"]![1]!["runtimeBinding"]!["connections"]!["github"] = new JsonArray();
        Rejected<DashboardBootstrap>(emptyConnection);
    }

    [Test]
    public async Task BootstrapPreservesNativeCatalogAndRejectsMalformedData()
    {
        // Trimmed from stock 10.9.0 `target serve` output: two templates, both worker kinds, target workspace.
        var bytes = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures/dashboard-bootstrap.json"));
        var bootstrap = NativeJson.DeserializeUtf8<DashboardBootstrap>(bytes);
        Check(bootstrap.Workspace.Kind == DashboardWorkspaceKind.Target && bootstrap.Workspace.Id == "01a0e31e-4f6e-7f51-b114-59a1df1c2c17");
        Check(bootstrap.Templates.Select(t => t.Id).SequenceEqual(["single-worker:none", "software-change:push"]));
        Check(bootstrap.Templates[1].RuntimeBindings["deliver"] is GitDeliveryBinding);
        var agent = (DashboardAgentWorker)bootstrap.Workers[0];
        Check(agent.WorkerRefs.IsEmpty && agent.RuntimeBinding.GetProperty("model").GetString() == "");
        var push = (DashboardGitDeliveryWorker)bootstrap.Workers[1];
        Check(push.WorkerRefs.Single().Value == "builtin.git-delivery.push@1" && push.Node is VerifierNode && push.RuntimeBinding is GitDeliveryBinding);
        var original = JsonNode.Parse(bytes)!;
        Check(JsonNode.DeepEquals(original, JsonNode.Parse(NativeJson.SerializeUtf8(bootstrap))));

        foreach (var mutate in new Action<JsonNode>[]
        {
            x => x["unexpected"] = 1,
            x => x["version"] = 2,
            x => x["runtimeSchema"] = "schema",
            x => x["workspace"]!["kind"] = "cloud",
            x => x["workspace"]!["id"] = "01A0E31E-4F6E-7F51-B114-59A1DF1C2C17",
            x => x["templates"]![0]!.AsObject().Remove("runtimeBindings"),
            x => x["templates"]![0]!["graph"]!["profile"] = "unknown",
            x => x["templates"]![1]!["runtimeBindings"]!["deliver"]!["kind"] = "other",
            x => x["templates"]![1]!["runtimeBindings"]!["not a node"] = x["templates"]![1]!["runtimeBindings"]!["deliver"]!.DeepClone(),
            x => x["workers"]![0]!["runtimeKind"] = "shell",
            x => x["workers"]![0]!["node"] = x["workers"]![1]!["node"]!.DeepClone(),
            x => x["workers"]![1]!.AsObject().Remove("node"),
            x => x["workers"]![1]!["workerRefs"] = new JsonArray("not-a-ref"),
            x => x["workers"]![1]!["node"]!["kind"] = "unknown"
        })
        {
            var bad = original.DeepClone(); mutate(bad);
            Rejected<DashboardBootstrap>(bad);
        }
    }

    [Test]
    public void DraftActionsUseExactNativeTaggedJson()
    {
        var node = new NodeName("work");
        var path = ImmutableArray.Create(new FieldName("result"));
        foreach (var (action, expected) in new (DashboardContract, string)[]
        {
            (new FailureReasonAuthoringAction { Terminal = node, Reason = new FailReason("declined") }, """{"kind":"failure_reason","terminal":"work","reason":"declined"}"""),
            (new CompleteAuthoringAction { Owner = node }, """{"kind":"complete","owner":"work"}"""),
            (new ProtectAuthoringAction { Node = node }, """{"kind":"protect","node":"work"}"""),
            (new ConnectDataAction { Target = new() { Node = node, Input = new("task") }, Source = new RunInputSource { Path = path } },
                """{"kind":"connect","target":{"node":"work","input":"task"},"source":{"kind":"run_input","path":["result"]}}"""),
            (new RemoveInputDataAction { Target = new() { Node = node, Input = new("task") } }, """{"kind":"remove_input","target":{"node":"work","input":"task"}}"""),
            (new MapCollectionDataAction { Node = node, Source = new NodeOutputSource { Node = new("plan"), Channel = NodeOutputChannel.Signal, Path = path } },
                """{"kind":"map_collection","node":"work","source":{"kind":"node_output","path":["result"],"node":"plan","channel":"signal"}}"""),
            (new MapCollectionDataAction { Node = node, Source = new MapItemSource { Path = [] } }, """{"kind":"map_collection","node":"work","source":{"kind":"map_item","path":[]}}"""),
            (new ConnectDataAction { Target = new() { Node = node, Input = new("task") }, Source = new LoopInputSource { Node = new("loop"), Path = path } },
                """{"kind":"connect","target":{"node":"work","input":"task"},"source":{"kind":"loop_input","path":["result"],"node":"loop"}}"""),
            (new RunInputFieldDataAction { Name = new("title"), Type = new StringPayload(), Required = true }, """{"kind":"run_input_field","name":"title","type":{"kind":"string"},"required":true}"""),
            (new RunInputFieldDataAction { Before = new("title"), Name = new("body"), Type = new NullPayload(), Required = false },
                """{"kind":"run_input_field","before":"title","name":"body","type":{"kind":"null"},"required":false}"""),
            (new RemoveRunInputDataAction { Name = new("title") }, """{"kind":"remove_run_input","name":"title"}""")
        })
        {
            var bytes = NativeJson.SerializeUtf8(action);
            Check(JsonNode.DeepEquals(JsonNode.Parse(bytes), JsonNode.Parse(expected)), Encoding.UTF8.GetString(bytes));
            var parsed = action is DashboardAuthoringAction
                ? (DashboardContract)NativeJson.DeserializeUtf8<DashboardAuthoringAction>(bytes)
                : NativeJson.DeserializeUtf8<DashboardDataAction>(bytes);
            Check(JsonNode.DeepEquals(JsonNode.Parse(NativeJson.SerializeUtf8(parsed)), JsonNode.Parse(expected)));
        }
        foreach (var bad in new[]
        {
            """{"kind":"complete","owner":"work","extra":1}""",
            """{"kind":"failure_reason","terminal":"work","reason":"runtime_failed"}""",
            """{"kind":"complete","owner":"not a node"}""",
            """{"kind":"merge","owner":"work"}"""
        }) Rejected<DashboardAuthoringAction>(JsonNode.Parse(bad)!);
        foreach (var bad in new[]
        {
            """{"kind":"run_input_field","name":"title","type":{"kind":"text"},"required":true}""",
            """{"kind":"run_input_field","name":"title","type":{"kind":"string"}}""",
            """{"kind":"connect","target":{"node":"work","input":"task"},"source":{"kind":"node_output","node":"plan","channel":"stdout","path":[]}}""",
            """{"kind":"remove_input","target":{"node":"work"}}"""
        }) Rejected<DashboardDataAction>(JsonNode.Parse(bad)!);
    }

    [Test]
    public void RequestsAndDraftsKeepArbitraryDraftJsonAndTypedDefinitions()
    {
        var graph = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/dashboard-bootstrap.json")))!["templates"]![0]!["graph"]!;
        var spec = NativeJson.DeserializeUtf8<GraphSpec>(Encoding.UTF8.GetBytes(graph.ToJsonString()));
        var draftRuntime = Json("""{"harness":"codex","nodes":{"work":{"kind":"agent","model":""}}}""");
        var authoring = new DashboardAuthoringRequest { Graph = spec, Runtime = draftRuntime, Action = new ProtectAuthoringAction { Node = new("work") } };
        Check(JsonNode.Parse(NativeJson.SerializeUtf8(authoring))!["runtime"]!["nodes"]!["work"]!["model"]!.GetValue<string>() == "");
        // Native data edits accept incomplete draft graphs; the client preserves them as JSON.
        var data = new DashboardDataRequest { Graph = Json("""{"root":{"kind":"seq"}}"""), Runtime = Json("null"), Action = new RemoveRunInputDataAction { Name = new("task") } };
        Check(JsonNode.DeepEquals(JsonNode.Parse(NativeJson.SerializeUtf8(data)),
            JsonNode.Parse("""{"graph":{"root":{"kind":"seq"}},"runtime":null,"action":{"kind":"remove_run_input","name":"task"}}""")));
        Rejected<DashboardAuthoringDraft>(new JsonObject { ["graph"] = JsonNode.Parse("""{"root":{"kind":"seq"}}"""), ["runtime"] = null });
        Check(NativeJson.DeserializeUtf8<DashboardAuthoringDraft>(Encoding.UTF8.GetBytes(new JsonObject { ["graph"] = graph.DeepClone(), ["runtime"] = null }.ToJsonString())).Graph.Root is SeqNode);
        Check(NativeJson.DeserializeUtf8<DashboardValidation>("""{"valid":true}"""u8).Valid);
        Rejected<DashboardValidation>(JsonNode.Parse("""{"valid":false}""")!);
        Rejected<DashboardProfileDocument>(new JsonObject { ["graph"] = graph.DeepClone(), ["runtime"] = JsonNode.Parse(draftRuntime.GetRawText()) });
    }
}
