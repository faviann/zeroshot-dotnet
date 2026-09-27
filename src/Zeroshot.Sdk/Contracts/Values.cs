using System.Text.Json.Serialization;

namespace Zeroshot.Native.Contracts;

/// <summary>A validated native string domain. No formatting of authored text is implicit.</summary>
public abstract record NativeString
{
    public string Value { get; }
    private protected NativeString(string value) { Value = value; }
    public sealed override string ToString() => GetType().Name;
}

[JsonConverter(typeof(NativeStringConverterFactory))]
public sealed record RunId : NativeString
{
    public RunId(string value) : base(ValueRules.Check(nameof(RunId), value)) { }
}

[JsonConverter(typeof(NativeStringConverterFactory))]
public sealed record Cursor : NativeString
{
    public Cursor(string value) : base(ValueRules.Check(nameof(Cursor), value)) { }
}

[JsonConverter(typeof(NativeStringConverterFactory))]
public sealed record SubscriptionId : NativeString
{
    public SubscriptionId(string value) : base(ValueRules.Check(nameof(SubscriptionId), value)) { }
}

[JsonConverter(typeof(NativeStringConverterFactory))]
public sealed record ExecutionRef : NativeString
{
    public ExecutionRef(string value) : base(ValueRules.Check(nameof(ExecutionRef), value)) { }
}

[JsonConverter(typeof(NativeStringConverterFactory))]
public sealed record CheckpointId : NativeString
{
    public CheckpointId(string value) : base(ValueRules.Check(nameof(CheckpointId), value)) { }
}

[JsonConverter(typeof(NativeStringConverterFactory))]
public sealed record IdempotencyKey : NativeString
{
    public IdempotencyKey(string value) : base(ValueRules.Check(nameof(IdempotencyKey), value)) { }
}

[JsonConverter(typeof(NativeStringConverterFactory))]
public sealed record NodeName : NativeString
{
    public NodeName(string value) : base(ValueRules.Check(nameof(NodeName), value)) { }
}

[JsonConverter(typeof(NativeStringConverterFactory))]
public sealed record FieldName : NativeString
{
    public FieldName(string value) : base(ValueRules.Check(nameof(FieldName), value)) { }
}

[JsonConverter(typeof(NativeStringConverterFactory))]
public sealed record EnumLabel : NativeString
{
    public EnumLabel(string value) : base(ValueRules.Check(nameof(EnumLabel), value)) { }
}

[JsonConverter(typeof(NativeStringConverterFactory))]
public sealed record WorkerRef : NativeString
{
    public WorkerRef(string value) : base(ValueRules.Check(nameof(WorkerRef), value)) { }
}

[JsonConverter(typeof(NativeStringConverterFactory))]
public sealed record PolicyRef : NativeString
{
    public PolicyRef(string value) : base(ValueRules.Check(nameof(PolicyRef), value)) { }
}

[JsonConverter(typeof(NativeStringConverterFactory))]
public sealed record CredentialHandle : NativeString
{
    public CredentialHandle(string value) : base(ValueRules.Check(nameof(CredentialHandle), value)) { }
}

[JsonConverter(typeof(NativeStringConverterFactory))]
public sealed record TypeId : NativeString
{
    public TypeId(string value) : base(ValueRules.Check(nameof(TypeId), value)) { }
}

[JsonConverter(typeof(NativeStringConverterFactory))]
public sealed record ArtifactId : NativeString
{
    public ArtifactId(string value) : base(ValueRules.Check(nameof(ArtifactId), value)) { }
}

[JsonConverter(typeof(NativeStringConverterFactory))]
public sealed record MediaType : NativeString
{
    public MediaType(string value) : base(ValueRules.Check(nameof(MediaType), value)) { }
}

[JsonConverter(typeof(NativeStringConverterFactory))]
public sealed record Sha256Digest : NativeString
{
    public Sha256Digest(string value) : base(ValueRules.Check(nameof(Sha256Digest), value)) { }
}

[JsonConverter(typeof(NativeStringConverterFactory))]
public sealed record NodeInstructions : NativeString
{
    public NodeInstructions(string value) : base(ValueRules.Check(nameof(NodeInstructions), value)) { }
}

[JsonConverter(typeof(NativeStringConverterFactory))]
public sealed record RunTitle : NativeString
{
    public RunTitle(string value) : base(ValueRules.Check(nameof(RunTitle), value)) { }
}

[JsonConverter(typeof(NativeStringConverterFactory))]
public sealed record ModelId : NativeString
{
    public ModelId(string value) : base(ValueRules.Check(nameof(ModelId), value)) { }
}

[JsonConverter(typeof(NativeStringConverterFactory))]
public sealed record SourceRepositoryId : NativeString
{
    public SourceRepositoryId(string value) : base(ValueRules.Check(nameof(SourceRepositoryId), value)) { }
}

[JsonConverter(typeof(NativeStringConverterFactory))]
public sealed record SourceBranchId : NativeString
{
    public SourceBranchId(string value) : base(ValueRules.Check(nameof(SourceBranchId), value)) { }
}

[JsonConverter(typeof(NativeStringConverterFactory))]
public sealed record SourceRevisionId : NativeString
{
    public SourceRevisionId(string value) : base(ValueRules.Check(nameof(SourceRevisionId), value)) { }
}

[JsonConverter(typeof(NativeStringConverterFactory))]
public sealed record ConnectionKey : NativeString
{
    public ConnectionKey(string value) : base(ValueRules.Check(nameof(ConnectionKey), value)) { }
}

[JsonConverter(typeof(NativeStringConverterFactory))]
public sealed record RunProfileName : NativeString
{
    public RunProfileName(string value) : base(ValueRules.Check(nameof(RunProfileName), value)) { }
}

[JsonConverter(typeof(NativeStringConverterFactory))]
public sealed record EnvironmentVariableName : NativeString
{
    public EnvironmentVariableName(string value) : base(ValueRules.Check(nameof(EnvironmentVariableName), value)) { }
}

[JsonConverter(typeof(NativeStringConverterFactory))]
public sealed record FailReason : NativeString
{
    public FailReason(string value) : base(ValueRules.Check(nameof(FailReason), value)) { }
}

[JsonConverter(typeof(NativeStringConverterFactory))]
public sealed record GraphIdentity : NativeString
{
    public GraphIdentity(string value) : base(ValueRules.Check(nameof(GraphIdentity), value)) { }
}

[JsonConverter(typeof(NativeStringConverterFactory))]
public sealed record RequestFingerprint : NativeString
{
    public RequestFingerprint(string value) : base(ValueRules.Check(nameof(RequestFingerprint), value)) { }
}
