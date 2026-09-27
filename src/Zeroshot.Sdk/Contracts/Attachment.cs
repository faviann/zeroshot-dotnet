using System.Text.Json.Serialization;

namespace Zeroshot.Native.Contracts;

[WireContract("RunAttachParams")]
public sealed record RunAttachParams : NativeContract
{
    [JsonPropertyName("runId")]
    public required RunId RunId { get; init; }
    [JsonPropertyName("execution")]
    public required ExecutionRef Execution { get; init; }
}

[WireContract("RunAttachResult")]
public sealed record RunAttachResult : NativeContract
{
    [JsonPropertyName("subscriptionId")]
    public required SubscriptionId SubscriptionId { get; init; }
    [JsonPropertyName("runId")]
    public required RunId RunId { get; init; }
    [JsonPropertyName("execution")]
    public required ExecutionRef Execution { get; init; }
}

/// <summary>Live execution data. A settled event is not a terminal run result.</summary>
[WireContract("RunAttachEventNotification")]
public sealed record RunAttachEventNotification : NativeContract
{
    [JsonPropertyName("subscriptionId")]
    public required SubscriptionId SubscriptionId { get; init; }
    [JsonPropertyName("runId")]
    public required RunId RunId { get; init; }
    [JsonPropertyName("execution")]
    public required ExecutionRef Execution { get; init; }
    [JsonPropertyName("event")]
    public required AgentAttachEvent Event { get; init; }
}

[WireContract("AgentAttachEvent")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(WorkingAgentAttachEvent), "working")]
[JsonDerivedType(typeof(OutputAgentAttachEvent), "output")]
[JsonDerivedType(typeof(SettledAgentAttachEvent), "settled")]
public abstract record AgentAttachEvent : NativeContract;

public sealed record WorkingAgentAttachEvent : AgentAttachEvent;
public sealed record OutputAgentAttachEvent : AgentAttachEvent
{
    [JsonPropertyName("text")]
    public required BoundedAssistantOutput Text { get; init; }
}
public sealed record SettledAgentAttachEvent : AgentAttachEvent;

[JsonConverter(typeof(NativeStringConverterFactory))]
public sealed record BoundedAssistantOutput : NativeString
{
    public BoundedAssistantOutput(string value) : base(ValueRules.Check(nameof(BoundedAssistantOutput), value)) { }
}
