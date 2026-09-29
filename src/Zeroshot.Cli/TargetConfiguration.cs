using System.Collections.Immutable;
using System.Text.Json;
using Zeroshot.Native;
using Zeroshot.Native.Contracts;

namespace Zeroshot.Cli;

/// <summary>
/// The <c>zeroshot-dotnet/target-config/v1</c> file: target, caller-supplied binding, transport/observation
/// settings and the names of the environment variables holding credentials. It never holds a secret value.
/// Value rules (origin, timeouts, limits, binding form) are the SDK's, applied when the client is built.
/// </summary>
internal sealed class TargetConfiguration
{
    private const string Schema = "zeroshot-dotnet/target-config/v1";

    private sealed record ResolverSettings(string Endpoint, ImmutableArray<ConnectionKey> Keys, ConnectionKey? Source, string BearerEnvironment);

    private readonly string path;
    private string? targetBearerEnvironment;
    private string? githubTokenEnvironment;
    private ImmutableDictionary<string, ImmutableDictionary<string, string>> connectionEnvironment =
        ImmutableDictionary<string, ImmutableDictionary<string, string>>.Empty;
    private ResolverSettings? resolver;

    public Uri Target { get; private set; } = null!;
    public NativeBinding? Binding { get; private set; }
    public TransportOptions Transport { get; private set; } = new();
    public ObservationOptions Observation { get; private set; } = new();

    private TargetConfiguration(string path) => this.path = path;

    public static TargetConfiguration Load(string path)
    {
        var configuration = new TargetConfiguration(path);
        try
        {
            using var document = JsonDocument.Parse(CliFiles.Read(path, "configuration"));
            configuration.Read(document.RootElement);
        }
        catch (JsonException) { throw configuration.Invalid("is not valid JSON"); }
        return configuration;
    }

    /// <summary>
    /// Builds the SDK client, resolving only the target bearer variable (when configured). Construction does no I/O.
    /// </summary>
    public ZeroshotClient CreateClient(TimeSpan? requestTimeout, bool? recover)
    {
        TargetControlCredentials? credentials = null;
        if (targetBearerEnvironment is { } name)
        {
            try { credentials = new TargetControlCredentials(TargetAuthentication.PrivateCapability, Resolve(name)); }
            catch (ArgumentException) { throw CliFailure.Credentials($"Environment variable {name} does not hold a valid target bearer token."); }
        }
        return CreateClient(new ZeroshotClientOptions
        {
            Target = Target, NativeBinding = Binding, TargetCredentials = credentials,
            Transport = requestTimeout is { } timeout ? Transport with { RequestTimeout = timeout } : Transport,
            Observation = recover is { } value ? Observation with { Recover = value } : Observation,
        }, requestTimeout is null ? $"The configuration '{path}'" : $"The configuration '{path}' with --request-timeout");
    }

    /// <summary>Builds a client from settings that were already parsed, turning SDK value refusals into a configuration error.</summary>
    public static ZeroshotClient CreateClient(ZeroshotClientOptions options, string source)
    {
        try { return new ZeroshotClient(options); }
        catch (ArgumentException error)
        { throw CliFailure.Configuration($"{source} has an invalid target, timeout or limit: {error.Message}"); }
    }

    /// <summary>Resolves the submission-only credentials: GitHub token, provider connections and resolver bearer.</summary>
    public TargetRunCredentials ResolveRunCredentials() => new()
    {
        GithubToken = githubTokenEnvironment is { } github ? Resolve(github) : null,
        Connections = connectionEnvironment.ToImmutableDictionary(
            connection => connection.Key,
            connection => connection.Value.ToImmutableDictionary(field => field.Key, field => Resolve(field.Value))),
        ConnectionResolver = resolver is { } r
            ? new TargetConnectionResolver { Endpoint = r.Endpoint, Keys = r.Keys, SourceConnection = r.Source, BearerToken = Resolve(r.BearerEnvironment) }
            : null,
    };

    private static string Resolve(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrEmpty(value)
            ? throw CliFailure.Credentials($"Environment variable {name} is not set; this command needs the credential it names.")
            : value;
    }

    private void Read(JsonElement root)
    {
        var fields = Fields(root, "", ["schema", "target"], ["nativeBinding", "credentials", "transport", "observation"]);
        if (Text(fields["schema"], "schema") != Schema) throw Invalid($"must declare schema {Schema}");
        Target = Uri.TryCreate(Text(fields["target"], "target"), UriKind.Absolute, out var target)
            ? target : throw Invalid("field 'target' must be an absolute URL");
        if (fields.TryGetValue("nativeBinding", out var binding)) Binding = ReadBinding(binding);
        if (fields.TryGetValue("credentials", out var credentials)) ReadCredentials(credentials);
        if (fields.TryGetValue("transport", out var transport))
            foreach (var (name, value) in Fields(transport, "transport.", [], TransportFields))
                Transport = ReadTransport(Transport, name, value);
        if (fields.TryGetValue("observation", out var observation))
            foreach (var (name, value) in Fields(observation, "observation.", [], ["recovery", "recoveryDelay", "subscriptionOpenTimeout"]))
                Observation = name switch
                {
                    "recovery" => Observation with { Recover = Recovery(Text(value, "observation.recovery")) ?? throw Invalid("field 'observation.recovery' must be 'established-interruptions' or 'none'") },
                    "recoveryDelay" => Observation with { ReopenDelay = Duration(value, "observation.recoveryDelay") },
                    _ => Observation with { SetupTimeout = Duration(value, "observation.subscriptionOpenTimeout") },
                };
    }

    /// <summary>Maps the CLI observation mode to the SDK recovery switch; null when unrecognized.</summary>
    public static bool? Recovery(string mode) => mode switch { "established-interruptions" => true, "none" => false, _ => null };

    private NativeBinding ReadBinding(JsonElement value)
    {
        var fields = Fields(value, "nativeBinding.", ["provenance", "release", "sourceRevision"], []);
        if (Text(fields["provenance"], "nativeBinding.provenance") != "caller-supplied")
            throw Invalid("field 'nativeBinding.provenance' must be 'caller-supplied'");
        try { return NativeBinding.CallerSupplied(Text(fields["release"], "nativeBinding.release"), Text(fields["sourceRevision"], "nativeBinding.sourceRevision")); }
        catch (ArgumentException) { throw Invalid("field 'nativeBinding' must hold printable release and source revision tokens"); }
    }

    private void ReadCredentials(JsonElement value)
    {
        var fields = Fields(value, "credentials.", [], ["targetBearerEnvironment", "githubTokenEnvironment", "connections", "connectionResolver"]);
        if (fields.TryGetValue("targetBearerEnvironment", out var bearer)) targetBearerEnvironment = VariableName(bearer, "credentials.targetBearerEnvironment");
        if (fields.TryGetValue("githubTokenEnvironment", out var github)) githubTokenEnvironment = VariableName(github, "credentials.githubTokenEnvironment");
        if (fields.TryGetValue("connections", out var connections))
        {
            if (connections.ValueKind != JsonValueKind.Object) throw Invalid("field 'credentials.connections' must be an object");
            var map = ImmutableDictionary.CreateBuilder<string, ImmutableDictionary<string, string>>(StringComparer.Ordinal);
            foreach (var connection in connections.EnumerateObject())
            {
                var at = $"credentials.connections.{connection.Name}";
                if (connection.Value.ValueKind != JsonValueKind.Object) throw Invalid($"field '{at}' must map requested fields to environment variable names");
                var names = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
                foreach (var field in connection.Value.EnumerateObject())
                    if (!names.TryAdd(field.Name, VariableName(field.Value, $"{at}.{field.Name}"))) throw Invalid($"field '{at}.{field.Name}' is duplicated");
                if (!map.TryAdd(connection.Name, names.ToImmutable())) throw Invalid($"field '{at}' is duplicated");
            }
            connectionEnvironment = map.ToImmutable();
        }
        if (fields.TryGetValue("connectionResolver", out var resolverValue))
        {
            var at = "credentials.connectionResolver";
            var settings = Fields(resolverValue, at + ".", ["endpoint", "keys", "bearerEnvironment"], ["sourceConnection"]);
            if (settings["keys"].ValueKind != JsonValueKind.Array) throw Invalid($"field '{at}.keys' must be an array of connection keys");
            resolver = new ResolverSettings(
                Text(settings["endpoint"], at + ".endpoint"),
                [.. settings["keys"].EnumerateArray().Select(key => Key(key, at + ".keys"))],
                settings.TryGetValue("sourceConnection", out var source) ? Key(source, at + ".sourceConnection") : null,
                VariableName(settings["bearerEnvironment"], at + ".bearerEnvironment"));
        }
    }

    private static readonly string[] TransportFields =
    [
        "connectTimeout", "requestTimeout", "cleanupTimeout", "maxResponseBytes", "maxMessageBytes", "maxRequestBytes",
        "maxBufferedRecordsPerStream", "maxBufferedBytesPerStream", "maxBufferedBytesTotal", "maxConcurrentSubscriptions",
        "maxConcurrentRequests", "maxOecpConnections", "maxHttpConnectionsPerOrigin", "maxErrorBodyBytes",
    ];

    // Configuration keys follow the accepted CLI option names; the SDK owns each value's range rules.
    private TransportOptions ReadTransport(TransportOptions options, string name, JsonElement value)
    {
        var at = "transport." + name;
        return name switch
        {
            "connectTimeout" => options with { ConnectTimeout = Duration(value, at) },
            "requestTimeout" => options with { RequestTimeout = Duration(value, at) },
            "cleanupTimeout" => options with { CleanupTimeout = Duration(value, at) },
            "maxResponseBytes" => options with { MaxResponseBytes = Int32(value, at) },
            "maxMessageBytes" => options with { MaxOecpMessageBytes = Int32(value, at) },
            "maxRequestBytes" => options with { MaxRequestBytes = Int32(value, at) },
            "maxBufferedRecordsPerStream" => options with { MaxQueuedObservationRecords = Int32(value, at) },
            "maxBufferedBytesPerStream" => options with { MaxQueuedObservationBytes = Int32(value, at) },
            "maxBufferedBytesTotal" => options with { MaxAggregateObservationBytes = Int64(value, at) },
            "maxConcurrentSubscriptions" => options with { MaxConcurrentSubscriptions = Int32(value, at) },
            "maxConcurrentRequests" => options with { MaxConcurrentRequests = Int32(value, at) },
            "maxOecpConnections" => options with { MaxOecpConnections = Int32(value, at) },
            "maxHttpConnectionsPerOrigin" => options with { MaxHttpConnectionsPerOrigin = Int32(value, at) },
            _ => options with { MaxErrorBodyBytes = Int32(value, at) },
        };
    }

    private Dictionary<string, JsonElement> Fields(JsonElement value, string prefix, string[] required, string[] optional)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid(prefix.Length == 0 ? "must be a JSON object" : $"field '{prefix.TrimEnd('.')}' must be an object");
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!required.Contains(property.Name) && !optional.Contains(property.Name)) throw Invalid($"has unknown field '{prefix}{property.Name}'");
            if (!fields.TryAdd(property.Name, property.Value)) throw Invalid($"has duplicate field '{prefix}{property.Name}'");
        }
        foreach (var name in required)
            if (!fields.ContainsKey(name)) throw Invalid($"is missing field '{prefix}{name}'");
        return fields;
    }

    private string Text(JsonElement value, string at)
        => value.ValueKind == JsonValueKind.String ? value.GetString()! : throw Invalid($"field '{at}' must be a string");

    private TimeSpan Duration(JsonElement value, string at)
        => value.ValueKind == JsonValueKind.String && CliDuration.TryParse(value.GetString()!, out var duration)
            ? duration : throw Invalid($"field '{at}' must be a duration string, {CliDuration.Rule}");

    private int Int32(JsonElement value, string at)
        => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number
            : throw Invalid($"field '{at}' must be an integer number of bytes or records");

    private long Int64(JsonElement value, string at)
        => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number
            : throw Invalid($"field '{at}' must be an integer number of bytes");

    private string VariableName(JsonElement value, string at)
    {
        var name = value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
        return name.Length > 0 && !name.Contains('=') && !name.Contains('\0') ? name
            : throw Invalid($"field '{at}' must name an environment variable");
    }

    private ConnectionKey Key(JsonElement value, string at)
    {
        try { return new ConnectionKey(Text(value, at)); }
        catch (ArgumentException) { throw Invalid($"field '{at}' must hold native connection keys"); }
    }

    private CliFailure Invalid(string problem) => CliFailure.Configuration($"The configuration '{path}' {problem}.");
}
