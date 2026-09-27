// Discovery wire shapes from native 10.9.0 / 75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa.
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zeroshot.Native.Contracts;

/// <summary>Native discovery wire data; descriptors alone do not establish endpoint support.</summary>
public abstract record DiscoveryContract : NativeContract;

[JsonConverter(typeof(TargetAuthenticationConverter))]
public enum TargetAuthentication { None, HostedOauth, PrivateCapability }

internal sealed class TargetAuthenticationConverter : JsonConverter<TargetAuthentication>
{
    public override TargetAuthentication Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String ? reader.GetString() switch
        {
            "none" => TargetAuthentication.None,
            "hosted_oauth" => TargetAuthentication.HostedOauth,
            "private_capability" => TargetAuthentication.PrivateCapability,
            _ => throw new JsonException()
        } : throw new JsonException();
    public override void Write(Utf8JsonWriter writer, TargetAuthentication value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch
        {
            TargetAuthentication.None => "none",
            TargetAuthentication.HostedOauth => "hosted_oauth",
            TargetAuthentication.PrivateCapability => "private_capability",
            _ => throw new JsonException()
        });
}

public sealed record TargetDiscoveryDocument : DiscoveryContract
{
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }
    [JsonPropertyName("authentication")]
    public required TargetAuthentication Authentication { get; init; }
    [JsonPropertyName("runPath")]
    public required string RunPath { get; init; }
    [JsonPropertyName("sessionPath")]
    public required string SessionPath { get; init; }
    [JsonPropertyName("oecpPath")]
    public required string OecpPath { get; init; }
    [JsonPropertyName("audience")]
    public required string Audience { get; init; }
    [JsonPropertyName("privateBootstrapPath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PrivateBootstrapPath { get; init; }
    [JsonPropertyName("oauth")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TargetOAuthDiscovery? Oauth { get; init; }
    [JsonPropertyName("loginSession")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TargetLoginSessionDiscovery? LoginSession { get; init; }
    [JsonPropertyName("extensions")]
    public TargetDiscoveryExtensions Extensions { get; init; } = new();
}

public sealed record TargetOAuthDiscovery : DiscoveryContract
{
    [JsonPropertyName("metadataUrl")]
    public required string MetadataUrl { get; init; }
    [JsonPropertyName("deviceAuthorizationEndpoint")]
    public required string DeviceAuthorizationEndpoint { get; init; }
    [JsonPropertyName("tokenEndpoint")]
    public required string TokenEndpoint { get; init; }
    [JsonPropertyName("revocationEndpoint")]
    public required string RevocationEndpoint { get; init; }
    [JsonPropertyName("clientId")]
    public required string ClientId { get; init; }
    [JsonPropertyName("deviceGrantType")]
    public required string DeviceGrantType { get; init; }
    [JsonPropertyName("deviceExchangeFields")]
    public required ImmutableArray<string> DeviceExchangeFields { get; init; }
}

public sealed record TargetLoginSessionDiscovery : DiscoveryContract
{
    [JsonPropertyName("routeTemplate")]
    public required string RouteTemplate { get; init; }
    [JsonPropertyName("method")]
    public required string Method { get; init; }
    [JsonPropertyName("cachePolicy")]
    public required string CachePolicy { get; init; }
}

public sealed record TargetHostedRunRoutes : DiscoveryContract
{
    [JsonPropertyName("list")]
    public required string List { get; init; }
    [JsonPropertyName("status")]
    public required string Status { get; init; }
    [JsonPropertyName("watch")]
    public required string Watch { get; init; }
    [JsonPropertyName("logs")]
    public required string Logs { get; init; }
    [JsonPropertyName("force")]
    public required string Force { get; init; }
}

public sealed record TargetHostedRunsDiscovery : DiscoveryContract
{
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }
    [JsonPropertyName("base_url")]
    public required string BaseUrl { get; init; }
    [JsonPropertyName("route_templates")]
    public required TargetHostedRunRoutes RouteTemplates { get; init; }
}

public sealed record TargetHostedWorkspaceRecoveryRoutes : DiscoveryContract
{
    [JsonPropertyName("resume")]
    public required string Resume { get; init; }
    [JsonPropertyName("checkpoints")]
    public required string Checkpoints { get; init; }
    [JsonPropertyName("discard_workspace")]
    public required string DiscardWorkspace { get; init; }
}

public sealed record TargetHostedWorkspaceRecoveryDiscovery : DiscoveryContract
{
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }
    [JsonPropertyName("route_templates")]
    public required TargetHostedWorkspaceRecoveryRoutes RouteTemplates { get; init; }
}

public sealed record TargetRunHistoryRoutes : DiscoveryContract
{
    [JsonPropertyName("list")]
    public required string List { get; init; }
    [JsonPropertyName("detail")]
    public required string Detail { get; init; }
    [JsonPropertyName("page")]
    public required string Page { get; init; }
}

public sealed record TargetRunHistoryDiscovery : DiscoveryContract
{
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }
    [JsonPropertyName("baseUrl")]
    public required string BaseUrl { get; init; }
    [JsonPropertyName("routeTemplates")]
    public required TargetRunHistoryRoutes RouteTemplates { get; init; }
}

public sealed record TargetConnectionRoutes : DiscoveryContract
{
    [JsonPropertyName("list")]
    public required string List { get; init; }
    [JsonPropertyName("set")]
    public required string Set { get; init; }
    [JsonPropertyName("delete")]
    public required string Delete { get; init; }
    [JsonPropertyName("resolve")]
    public required string Resolve { get; init; }
}

public sealed record TargetConnectionsDiscovery : DiscoveryContract
{
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }
    [JsonPropertyName("baseUrl")]
    public required string BaseUrl { get; init; }
    [JsonPropertyName("routeTemplates")]
    public required TargetConnectionRoutes RouteTemplates { get; init; }
    [JsonPropertyName("dynamicKinds")]
    public ImmutableArray<string> DynamicKinds { get; init; } = [];
}

public sealed record TargetRunProfileRoutes : DiscoveryContract
{
    [JsonPropertyName("list")]
    public required string List { get; init; }
    [JsonPropertyName("show")]
    public required string Show { get; init; }
    [JsonPropertyName("set")]
    public required string Set { get; init; }
    [JsonPropertyName("delete")]
    public required string Delete { get; init; }
    [JsonPropertyName("default")]
    public required string Default { get; init; }
    [JsonPropertyName("run")]
    public required string Run { get; init; }
}

public sealed record TargetRunProfilesDiscovery : DiscoveryContract
{
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }
    [JsonPropertyName("baseUrl")]
    public required string BaseUrl { get; init; }
    [JsonPropertyName("routeTemplates")]
    public required TargetRunProfileRoutes RouteTemplates { get; init; }
}

public sealed record TargetMergePlanRoutes : DiscoveryContract
{
    [JsonPropertyName("create")]
    public required string Create { get; init; }
    [JsonPropertyName("status")]
    public required string Status { get; init; }
    [JsonPropertyName("force")]
    public required string Force { get; init; }
}

public sealed record TargetMergePlansDiscovery : DiscoveryContract
{
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }
    [JsonPropertyName("baseUrl")]
    public required string BaseUrl { get; init; }
    [JsonPropertyName("routeTemplates")]
    public required TargetMergePlanRoutes RouteTemplates { get; init; }
}

public sealed record TargetWorkspaceRecoveryDiscovery : DiscoveryContract
{
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }
}

public sealed record TargetWorkspaceCheckpointsDiscovery : DiscoveryContract
{
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }
}

// Native ignores unknown extension names; each known descriptor remains strict.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Skip)]
public sealed record TargetDiscoveryExtensions : DiscoveryContract
{
    [JsonPropertyName("hosted_runs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TargetHostedRunsDiscovery? HostedRuns { get; init; }
    [JsonPropertyName("run_history")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TargetRunHistoryDiscovery? RunHistory { get; init; }
    [JsonPropertyName("merge_plans")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TargetMergePlansDiscovery? MergePlans { get; init; }
    [JsonPropertyName("connections")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TargetConnectionsDiscovery? Connections { get; init; }
    [JsonPropertyName("run_profiles")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TargetRunProfilesDiscovery? RunProfiles { get; init; }
    [JsonPropertyName("workspace_recovery")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TargetWorkspaceRecoveryDiscovery? WorkspaceRecovery { get; init; }
    [JsonPropertyName("workspace_checkpoints")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TargetWorkspaceCheckpointsDiscovery? WorkspaceCheckpoints { get; init; }
    [JsonPropertyName("hosted_workspace_recovery")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TargetHostedWorkspaceRecoveryDiscovery? HostedWorkspaceRecovery { get; init; }
}

