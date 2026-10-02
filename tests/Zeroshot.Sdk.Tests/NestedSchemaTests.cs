using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TUnit.Core;
using Zeroshot.Native;
using Zeroshot.Native.Contracts;

namespace Zeroshot.Client.Tests;

// Typed decoding accepts an empty declared connection; only the pinned native rules refuse it.
public sealed class NestedSchemaTests
{
    private static readonly JsonNode EmptyConnection = JsonNode.Parse("""{"connections":{"github":[]}}""")!;
    private static readonly JsonElement Input = JsonDocument.Parse("""{"items":[null,1]}""").RootElement.Clone();

    private static JsonNode Fixture(string name) => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)))!;
    private static JsonNode Wire<T>(T value) => JsonNode.Parse(NativeJson.SerializeUtf8(value))!;

    private static void Refused<T>(JsonNode json, string name, Action<JsonNode> change)
    {
        NativeJson.DeserializeUtf8<T>(Encoding.UTF8.GetBytes(json.ToJsonString()));
        change(json);
        try { NativeJson.DeserializeUtf8<T>(Encoding.UTF8.GetBytes(json.ToJsonString())); }
        catch (JsonException) { return; }
        throw new InvalidOperationException($"{name} accepted an empty declared connection.");
    }

    [Test]
    public void ProfileRunEnvironment()
    {
        var run = Wire(new RunProfileRunRequest
        {
            RunId = new("018f5e78-7f95-7c22-8d98-3f15af20c991"), Profile = new() { Scope = RunProfileScope.Org, Name = new("review") },
            Title = new("Profile run"), InitialInput = Input, SubmissionKey = new("profile-key"),
            Source = new() { Repository = new("acme/project"), Branch = new("main"), Revision = new(new string('a', 40)) },
            Connections = ImmutableDictionary<string, ImmutableDictionary<string, string>>.Empty
        });
        Refused<RunProfileRunRequest>(run, nameof(RunProfileRunRequest), n => n["environment"] = EmptyConnection.DeepClone());
    }

    [Test]
    public void MergePlanEnvironment()
    {
        var plan = Wire(new MergePlanSubmitRequest
        {
            SubmissionKey = new("plan-key"), Title = new("Release"), ExpiresAt = "2026-09-28T00:00:00Z",
            Source = new() { Repository = new("acme/project"), Branch = new("main") },
            Profile = new() { Scope = RunProfileScope.Org, Name = new("software-change") },
            Runs = [new() { Name = new("build"), InitialInput = Input }]
        });
        Refused<MergePlanSubmitRequest>(plan, nameof(MergePlanSubmitRequest), n => n["environment"] = EmptyConnection.DeepClone());
    }

    [Test]
    public void TargetSubmissionEnvironment()
    {
        var request = Fixture("prepared.json");
        request["connections"] = new JsonObject();
        Refused<TargetRunRequest>(request, nameof(TargetRunRequest), n => n["submission"]!["environment"] = EmptyConnection.DeepClone());
    }

    [Test]
    public void DashboardTemplateRuntimeBinding()
    {
        Refused<DashboardBootstrap>(Fixture("dashboard-bootstrap.json"), nameof(DashboardTemplate),
            n => n["templates"]![1]!["runtimeBindings"]!["deliver"]!["connections"]!["github"] = new JsonArray());
    }
}
