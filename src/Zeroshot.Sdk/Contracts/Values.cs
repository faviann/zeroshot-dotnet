using System.Text.Json.Serialization;

namespace Zeroshot.Native.Contracts;

/// <summary>A validated native string domain. No formatting of authored text is implicit.</summary>
public abstract record NativeString
{
    public string Value { get; }
    private protected NativeString(string value, Func<string, bool> rule) { Value = ValueRules.Check(value, rule, GetType().Name); }
    public sealed override string ToString() => GetType().Name;
}

[JsonConverter(typeof(NativeStringConverterFactory))]
public sealed record RunId : NativeString
{
    public RunId(string value) : base(value, ValueRules.Any) { }
}

[JsonConverter(typeof(NativeStringConverterFactory))]
public sealed record Cursor : NativeString
{
    public Cursor(string value) : base(value, ValueRules.Any) { }
}

[JsonConverter(typeof(NativeStringConverterFactory))]
public sealed record SubscriptionId : NativeString
{
    public SubscriptionId(string value) : base(value, ValueRules.Any) { }
}

[JsonConverter(typeof(NativeStringConverterFactory))]
public sealed record ExecutionRef : NativeString
{
    public ExecutionRef(string value) : base(value, ValueRules.Key) { }
}

[JsonConverter(typeof(NativeStringConverterFactory))]
public sealed record CheckpointId : NativeString
{
    public CheckpointId(string value) : base(value, ValueRules.Text) { }
}

[JsonConverter(typeof(NativeStringConverterFactory))]
public sealed record IdempotencyKey : NativeString
{
    public IdempotencyKey(string value) : base(value, ValueRules.Text) { }
}

[JsonConverter(typeof(NativeStringConverterFactory))]
public sealed record NodeName : NativeString
{
    public NodeName(string value) : base(value, ValueRules.Identifier) { }
}

[JsonConverter(typeof(NativeStringConverterFactory))]
public sealed record FieldName : NativeString
{
    public FieldName(string value) : base(value, ValueRules.Identifier) { }
}

[JsonConverter(typeof(NativeStringConverterFactory))]
public sealed record EnumLabel : NativeString
{
    public EnumLabel(string value) : base(value, ValueRules.Identifier) { }
}

[JsonConverter(typeof(NativeStringConverterFactory))]
public sealed record WorkerRef : NativeString
{
    public WorkerRef(string value) : base(value, ValueRules.VersionedRef) { }
}

[JsonConverter(typeof(NativeStringConverterFactory))]
public sealed record PolicyRef : NativeString
{
    public PolicyRef(string value) : base(value, ValueRules.VersionedRef) { }
}

[JsonConverter(typeof(NativeStringConverterFactory))]
public sealed record CredentialHandle : NativeString
{
    public CredentialHandle(string value) : base(value, ValueRules.VersionedRef) { }
}

[JsonConverter(typeof(NativeStringConverterFactory))]
public sealed record TypeId : NativeString
{
    public TypeId(string value) : base(value, ValueRules.VersionedRef) { }
}

[JsonConverter(typeof(NativeStringConverterFactory))]
public sealed record ArtifactId : NativeString
{
    public ArtifactId(string value) : base(value, ValueRules.Text) { }
}

[JsonConverter(typeof(NativeStringConverterFactory))]
public sealed record MediaType : NativeString
{
    public MediaType(string value) : base(value, ValueRules.Text) { }
}

[JsonConverter(typeof(NativeStringConverterFactory))]
public sealed record Sha256Digest : NativeString
{
    public Sha256Digest(string value) : base(value, ValueRules.Sha256) { }
}

[JsonConverter(typeof(NativeStringConverterFactory))]
public sealed record NodeInstructions : NativeString
{
    public NodeInstructions(string value) : base(value, ValueRules.Instructions) { }
}

[JsonConverter(typeof(NativeStringConverterFactory))]
public sealed record RunTitle : NativeString
{
    public RunTitle(string value) : base(value, ValueRules.Text) { }
}

[JsonConverter(typeof(NativeStringConverterFactory))]
public sealed record ModelId : NativeString
{
    public ModelId(string value) : base(value, ValueRules.Model) { }
}

[JsonConverter(typeof(NativeStringConverterFactory))]
public sealed record SourceRepositoryId : NativeString
{
    public SourceRepositoryId(string value) : base(value, ValueRules.Repository) { }
}

[JsonConverter(typeof(NativeStringConverterFactory))]
public sealed record SourceBranchId : NativeString
{
    public SourceBranchId(string value) : base(value, ValueRules.Branch) { }
}

[JsonConverter(typeof(NativeStringConverterFactory))]
public sealed record SourceRevisionId : NativeString
{
    public SourceRevisionId(string value) : base(value, ValueRules.GitRevision) { }
}

[JsonConverter(typeof(NativeStringConverterFactory))]
public sealed record ConnectionKey : NativeString
{
    public ConnectionKey(string value) : base(value, ValueRules.Key) { }
}

[JsonConverter(typeof(NativeStringConverterFactory))]
public sealed record RunProfileName : NativeString
{
    public RunProfileName(string value) : base(value, ValueRules.ProfileName) { }
}

[JsonConverter(typeof(NativeStringConverterFactory))]
public sealed record EnvironmentVariableName : NativeString
{
    public EnvironmentVariableName(string value) : base(value, ValueRules.EnvironmentName) { }
}

[JsonConverter(typeof(NativeStringConverterFactory))]
public sealed record FailReason : NativeString
{
    public FailReason(string value) : base(value, ValueRules.AuthoredFailReason) { }
}

[JsonConverter(typeof(NativeStringConverterFactory))]
public sealed record GraphIdentity : NativeString
{
    public GraphIdentity(string value) : base(value, ValueRules.Sha256) { }
}

[JsonConverter(typeof(NativeStringConverterFactory))]
public sealed record RequestFingerprint : NativeString
{
    public RequestFingerprint(string value) : base(value, ValueRules.Sha256) { }
}
