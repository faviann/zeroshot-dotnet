// Browser dashboard wire shapes from native 10.9.0 / 75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa:
// zeroshot/src/profile_ui.rs, workspace.rs, profile_ui/{catalog,outcomes,data}.rs. Native publishes
// no schema for these serde DTOs; nested execution definitions reuse the pinned generated contracts.
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zeroshot.Native.Contracts;

/// <summary>Native browser-dashboard data. Drafts are editor state, not admitted or stored profiles.</summary>
public abstract record DashboardContract : NativeContract
{
    private protected static void Nested(JsonElement json, string property, Type type)
        => WireValidation.Validate(json.GetProperty(property), type);
}

/// <summary>`GET /ui/api/bootstrap`: native catalog plus the host's workspace identity.</summary>
public sealed record DashboardBootstrap : DashboardContract
{
    [JsonPropertyName("version")]
    public required int Version { get; init; }
    [JsonPropertyName("templates")]
    public required ImmutableArray<DashboardTemplate> Templates { get; init; }
    [JsonPropertyName("workers")]
    public required ImmutableArray<DashboardWorker> Workers { get; init; }
    /// <summary>Native's live schemars document for RuntimePlan; editor metadata, not a validation authority here.</summary>
    [JsonPropertyName("runtimeSchema")]
    public required JsonElement RuntimeSchema { get; init; }
    [JsonPropertyName("workspace")]
    public required DashboardWorkspace Workspace { get; init; }

    internal override void Validate(JsonElement json)
    {
        if (Version != 1 || RuntimeSchema.ValueKind != JsonValueKind.Object) throw new JsonException();
        Workspace.Validate(json.GetProperty("workspace"));
        foreach (var (template, element) in Templates.Zip(json.GetProperty("templates").EnumerateArray())) template.Validate(element);
        foreach (var (worker, element) in Workers.Zip(json.GetProperty("workers").EnumerateArray())) worker.Validate(element);
    }
}

/// <summary>A materialized built-in template for one delivery mode; `id` is `name:delivery`.</summary>
public sealed record DashboardTemplate : DashboardContract
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }
    [JsonPropertyName("name")]
    public required string Name { get; init; }
    [JsonPropertyName("delivery")]
    public required string Delivery { get; init; }
    [JsonPropertyName("label")]
    public required string Label { get; init; }
    [JsonPropertyName("graph")]
    public required GraphSpec Graph { get; init; }
    [JsonPropertyName("runtimeBindings")]
    public required ImmutableDictionary<string, NodeRuntimeBinding> RuntimeBindings { get; init; }

    internal override void Validate(JsonElement json)
    {
        Nested(json, "graph", typeof(GraphSpec));
        foreach (var binding in json.GetProperty("runtimeBindings").EnumerateObject())
        {
            _ = new NodeName(binding.Name);
            WireValidation.Validate(binding.Value, typeof(NodeRuntimeBinding));
        }
    }
}

/// <summary>An editor worker option, tagged by `runtimeKind`.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "runtimeKind")]
[JsonDerivedType(typeof(DashboardAgentWorker), "agent")]
[JsonDerivedType(typeof(DashboardGitDeliveryWorker), "git_delivery")]
public abstract record DashboardWorker : DashboardContract
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }
    [JsonPropertyName("label")]
    public required string Label { get; init; }
    [JsonPropertyName("workerRefs")]
    public required ImmutableArray<WorkerRef> WorkerRefs { get; init; }
}

public sealed record DashboardAgentWorker : DashboardWorker
{
    /// <summary>Native's blank editor binding (`{"kind":"agent","model":""}`), which is not an admissible AgentBinding.</summary>
    [JsonPropertyName("runtimeBinding")]
    public required JsonElement RuntimeBinding { get; init; }
}

public sealed record DashboardGitDeliveryWorker : DashboardWorker
{
    [JsonPropertyName("node")]
    public required GraphNode Node { get; init; }
    [JsonPropertyName("runtimeBinding")]
    public required NodeRuntimeBinding RuntimeBinding { get; init; }

    internal override void Validate(JsonElement json)
    {
        Nested(json, "node", typeof(GraphNode));
        Nested(json, "runtimeBinding", typeof(NodeRuntimeBinding));
    }
}

public enum DashboardWorkspaceKind
{
    [JsonStringEnumMemberName("local")]
    Local,
    [JsonStringEnumMemberName("target")]
    Target,
}

public sealed record DashboardWorkspace : DashboardContract
{
    [JsonPropertyName("kind")]
    public required DashboardWorkspaceKind Kind { get; init; }
    /// <summary>The host's canonical workspace UUID, required by profile saves.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    internal override void Validate(JsonElement json)
    {
        if (!Guid.TryParseExact(Id, "D", out var id) || id.ToString("D") != Id) throw new JsonException();
    }
}

/// <summary>`POST /ui/api/validate` request: native profile admission, with no save.</summary>
public sealed record DashboardProfileDocument : DashboardContract
{
    [JsonPropertyName("graph")]
    public required GraphSpec Graph { get; init; }
    [JsonPropertyName("runtime")]
    public required RuntimePlan Runtime { get; init; }

    internal override void Validate(JsonElement json)
    {
        Nested(json, "graph", typeof(GraphSpec));
        Nested(json, "runtime", typeof(RuntimePlan));
    }
}

/// <summary>The only native validate success, `{"valid":true}`. Rejection is a 422 dashboard problem.</summary>
public sealed record DashboardValidation : DashboardContract
{
    [JsonPropertyName("valid")]
    public required bool Valid { get; init; }

    internal override void Validate(JsonElement json)
    {
        if (!Valid) throw new JsonException();
    }
}

/// <summary>
/// `POST /ui/api/profiles` request: saves one user-scope profile after native admission. Native has no scope
/// or set-default field. A null expected revision is omitted, which native accepts only for a new name.
/// </summary>
public sealed record DashboardProfileSaveRequest : DashboardContract
{
    [JsonPropertyName("name")]
    public required RunProfileName Name { get; init; }
    [JsonPropertyName("graph")]
    public required GraphSpec Graph { get; init; }
    [JsonPropertyName("runtime")]
    public required RuntimePlan Runtime { get; init; }
    /// <summary>The opaque revision last read for this name, sent verbatim.</summary>
    [JsonPropertyName("expectedRevision")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExpectedRevision { get; init; }

    internal override void Validate(JsonElement json)
    {
        Nested(json, "graph", typeof(GraphSpec));
        Nested(json, "runtime", typeof(RuntimePlan));
    }
}

/// <summary>A stored user-scope profile with native's opaque revision of it, from a read or a save.</summary>
public sealed record DashboardProfile : DashboardContract
{
    [JsonPropertyName("profile")]
    public required RunProfile Profile { get; init; }
    [JsonPropertyName("revision")]
    public required string Revision { get; init; }

    internal override void Validate(JsonElement json) => Nested(json, "profile", typeof(RunProfile));
}

/// <summary>`POST /ui/api/authoring`: an outcome edit of a draft. The runtime draft is not interpreted.</summary>
public sealed record DashboardAuthoringRequest : DashboardContract
{
    [JsonPropertyName("graph")]
    public required GraphSpec Graph { get; init; }
    [JsonPropertyName("runtime")]
    public required JsonElement Runtime { get; init; }
    [JsonPropertyName("action")]
    public required DashboardAuthoringAction Action { get; init; }

    internal override void Validate(JsonElement json) => Nested(json, "graph", typeof(GraphSpec));
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(FailureReasonAuthoringAction), "failure_reason")]
[JsonDerivedType(typeof(CompleteAuthoringAction), "complete")]
[JsonDerivedType(typeof(ProtectAuthoringAction), "protect")]
public abstract record DashboardAuthoringAction : DashboardContract;

public sealed record FailureReasonAuthoringAction : DashboardAuthoringAction
{
    [JsonPropertyName("terminal")]
    public required NodeName Terminal { get; init; }
    [JsonPropertyName("reason")]
    public required FailReason Reason { get; init; }
}

public sealed record CompleteAuthoringAction : DashboardAuthoringAction
{
    [JsonPropertyName("owner")]
    public required NodeName Owner { get; init; }
}

public sealed record ProtectAuthoringAction : DashboardAuthoringAction
{
    [JsonPropertyName("node")]
    public required NodeName Node { get; init; }
}

/// <summary>An authoring draft; nothing was admitted, saved or run.</summary>
public sealed record DashboardAuthoringDraft : DashboardContract
{
    [JsonPropertyName("graph")]
    public required GraphSpec Graph { get; init; }
    [JsonPropertyName("runtime")]
    public required JsonElement Runtime { get; init; }

    internal override void Validate(JsonElement json) => Nested(json, "graph", typeof(GraphSpec));
}

/// <summary>`POST /ui/api/data`: an input/output edit. Native accepts arbitrary draft graph/runtime JSON.</summary>
public sealed record DashboardDataRequest : DashboardContract
{
    [JsonPropertyName("graph")]
    public required JsonElement Graph { get; init; }
    [JsonPropertyName("runtime")]
    public required JsonElement Runtime { get; init; }
    [JsonPropertyName("action")]
    public required DashboardDataAction Action { get; init; }

    internal override void Validate(JsonElement json) => Action.Validate(json.GetProperty("action"));
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ConnectDataAction), "connect")]
[JsonDerivedType(typeof(RemoveInputDataAction), "remove_input")]
[JsonDerivedType(typeof(MapCollectionDataAction), "map_collection")]
[JsonDerivedType(typeof(RunInputFieldDataAction), "run_input_field")]
[JsonDerivedType(typeof(RemoveRunInputDataAction), "remove_run_input")]
public abstract record DashboardDataAction : DashboardContract;

public sealed record DashboardInputTarget : DashboardContract
{
    [JsonPropertyName("node")]
    public required NodeName Node { get; init; }
    [JsonPropertyName("input")]
    public required FieldName Input { get; init; }
}

public sealed record ConnectDataAction : DashboardDataAction
{
    [JsonPropertyName("target")]
    public required DashboardInputTarget Target { get; init; }
    [JsonPropertyName("source")]
    public required DashboardDataSource Source { get; init; }
}

public sealed record RemoveInputDataAction : DashboardDataAction
{
    [JsonPropertyName("target")]
    public required DashboardInputTarget Target { get; init; }
}

public sealed record MapCollectionDataAction : DashboardDataAction
{
    [JsonPropertyName("node")]
    public required NodeName Node { get; init; }
    [JsonPropertyName("source")]
    public required DashboardDataSource Source { get; init; }
}

public sealed record RunInputFieldDataAction : DashboardDataAction
{
    /// <summary>Insert before this run-input field; null appends. Native treats omission and null alike.</summary>
    [JsonPropertyName("before")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FieldName? Before { get; init; }
    [JsonPropertyName("name")]
    public required FieldName Name { get; init; }
    [JsonPropertyName("type")]
    public required PayloadType Type { get; init; }
    [JsonPropertyName("required")]
    public required bool Required { get; init; }

    internal override void Validate(JsonElement json) => Nested(json, "type", typeof(PayloadType));
}

public sealed record RemoveRunInputDataAction : DashboardDataAction
{
    [JsonPropertyName("name")]
    public required FieldName Name { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(RunInputSource), "run_input")]
[JsonDerivedType(typeof(NodeOutputSource), "node_output")]
[JsonDerivedType(typeof(MapItemSource), "map_item")]
[JsonDerivedType(typeof(LoopInputSource), "loop_input")]
public abstract record DashboardDataSource : DashboardContract
{
    [JsonPropertyName("path")]
    public required ImmutableArray<FieldName> Path { get; init; }
}

public sealed record RunInputSource : DashboardDataSource;

public sealed record NodeOutputSource : DashboardDataSource
{
    [JsonPropertyName("node")]
    public required NodeName Node { get; init; }
    [JsonPropertyName("channel")]
    public required NodeOutputChannel Channel { get; init; }
}

public sealed record MapItemSource : DashboardDataSource;

public sealed record LoopInputSource : DashboardDataSource
{
    [JsonPropertyName("node")]
    public required NodeName Node { get; init; }
}

/// <summary>A data-flow draft; arbitrary JSON as returned by native, not admitted, saved or run.</summary>
public sealed record DashboardDataDraft : DashboardContract
{
    [JsonPropertyName("graph")]
    public required JsonElement Graph { get; init; }
    [JsonPropertyName("runtime")]
    public required JsonElement Runtime { get; init; }
}
