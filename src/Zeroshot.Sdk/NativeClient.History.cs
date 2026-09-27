using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Zeroshot.Native.Contracts;
using Zeroshot.Native.Execution;

namespace Zeroshot.Native;

public sealed partial class NativeClient
{
    private NativeHistoryClient? history;
    /// <summary>Discovered public run-history reads. No retention guarantee or history SSE route is implied.</summary>
    public NativeHistoryClient History => history ??= new NativeHistoryClient(this);
}

/// <summary>
/// Binds the discovered <c>run_history</c> capability (native controller_authority/history.rs). A
/// direct target serves it through its UI mount, which also answers HEAD for the same routes.
/// </summary>
public sealed class NativeHistoryClient
{
    internal const string Kind = "zeroshot.run-history/v1";
    private const int ProblemBytes = 64 * 1024;
    internal const string AfterQuery = "{?after}";
    internal static readonly Cursor InitialCursor = new("v2:0");
    // Direct history is served by the target's UI router; hosted history is a host-owned HTTP API.
    private static readonly HistoryOperation List = new("history.list", 4);
    private static readonly HistoryOperation Detail = new("history.detail", 8);
    private static readonly HistoryOperation Page = new("history.page", 8);
    private static readonly HistoryOperation HeadList = new("history.list.head", 4);
    private static readonly HistoryOperation HeadDetail = new("history.detail.head", 8);
    private static readonly HistoryOperation HeadPage = new("history.page.head", 8);
    private readonly NativeClient client;

    internal NativeHistoryClient(NativeClient client) => this.client = client;

    private sealed class HistoryOperation(string name, int responseMebibytes)
    {
            private readonly OperationDescriptor direct = Create(name, responseMebibytes, uiRouter: true);
        private readonly OperationDescriptor hosted = Create(name, responseMebibytes, uiRouter: false);
        private static OperationDescriptor Create(string name, int responseMebibytes, bool uiRouter) => new(name, OperationTransport.Http,
            responseBytes: responseMebibytes * 1024 * 1024, problemBytes: ProblemBytes, uiRouter: uiRouter, historyProblems: true);
        public OperationDescriptor For(TargetDiscoveryDocument discovery)
            => discovery.Authentication == TargetAuthentication.HostedOauth ? hosted : direct;
    }

    /// <summary>Reads one list page, optionally strictly after a canonical UUIDv7 run ID.</summary>
    public Task<RunHistoryList> ListAsync(TargetDiscoveryDocument discovery, RunId? after = null,
        TargetControlCredentials? credentials = null, CancellationToken cancellationToken = default)
    {
        var url = ListUrl(discovery, after, credentials);
        return client.ExecuteJsonAsync<RunHistoryList>(List.For(discovery), url, null, credentials,
            list => RunHistoryRules.List(list, after), cancellationToken, configure: Configure);
    }

    /// <summary>Reads the admitted definition. Legacy snapshots are accepted and never re-emitted.</summary>
    public Task<RunDefinition> DetailAsync(TargetDiscoveryDocument discovery, RunId runId,
        TargetControlCredentials? credentials = null, CancellationToken cancellationToken = default)
    {
        var url = DetailUrl(discovery, runId, credentials);
        return client.ExecuteJsonAsync<RunDefinition>(Detail.For(discovery), url, null, credentials,
            definition => RunHistoryRules.Definition(definition, runId), cancellationToken, configure: Configure);
    }

    /// <summary>Reads one bounded page strictly after <paramref name="after"/>, or from <c>v2:0</c>.</summary>
    public Task<HistoryPage> PageAsync(TargetDiscoveryDocument discovery, RunId runId, Cursor? after = null,
        TargetControlCredentials? credentials = null, CancellationToken cancellationToken = default)
    {
        var requested = after ?? InitialCursor;
        var url = PageUrl(discovery, runId, requested, credentials);
        return client.ExecuteJsonAsync<HistoryPage>(Page.For(discovery), url, null, credentials,
            page => RunHistoryRules.Page(page, requested), cancellationToken, configure: Configure);
    }

    /// <summary>HEAD for the list route. Native serves it only on the direct target UI mount.</summary>
    public Task<NativeHeadResult> HeadListAsync(TargetDiscoveryDocument discovery, RunId? after = null,
        TargetControlCredentials? credentials = null, CancellationToken cancellationToken = default)
        => client.ExecuteHeadAsync(HeadList.For(discovery), ListUrl(discovery, after, credentials), credentials, cancellationToken, Configure);

    public Task<NativeHeadResult> HeadDetailAsync(TargetDiscoveryDocument discovery, RunId runId,
        TargetControlCredentials? credentials = null, CancellationToken cancellationToken = default)
        => client.ExecuteHeadAsync(HeadDetail.For(discovery), DetailUrl(discovery, runId, credentials), credentials, cancellationToken, Configure);

    public Task<NativeHeadResult> HeadPageAsync(TargetDiscoveryDocument discovery, RunId runId, Cursor? after = null,
        TargetControlCredentials? credentials = null, CancellationToken cancellationToken = default)
        => client.ExecuteHeadAsync(HeadPage.For(discovery), PageUrl(discovery, runId, after ?? InitialCursor, credentials),
            credentials, cancellationToken, Configure);

    // Native TargetRunHistoryTransport sends exactly these headers.
    private static void Configure(HttpRequestMessage request)
    {
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        NativeClient.NoStore(request);
    }

    private Uri ListUrl(TargetDiscoveryDocument discovery, RunId? after, TargetControlCredentials? credentials)
    {
        if (after is not null) RequireRunId(after, nameof(after));
        return Route(discovery, credentials, d => d.List, runId: null, after?.Value, allowsAfter: true);
    }

    private Uri DetailUrl(TargetDiscoveryDocument discovery, RunId runId, TargetControlCredentials? credentials)
    {
        RequireRunId(runId, nameof(runId));
        return Route(discovery, credentials, d => d.Detail, runId.Value, after: null, allowsAfter: false);
    }

    private Uri PageUrl(TargetDiscoveryDocument discovery, RunId runId, Cursor after, TargetControlCredentials? credentials)
    {
        RequireRunId(runId, nameof(runId));
        RequireCursor(after, nameof(after));
        return Route(discovery, credentials, d => d.Page, runId.Value, after.Value, allowsAfter: true);
    }

    internal static void RequireRunId(RunId runId, string name)
    {
        ArgumentNullException.ThrowIfNull(runId, name);
        if (!TargetRunRequest.IsCanonicalRunId(runId.Value))
            throw new ArgumentException("Run history requires a canonical UUIDv7 run ID.", name);
    }

    internal static void RequireCursor(Cursor cursor, string name)
    {
        if (!RunHistoryRules.TryCanonical(cursor, out _))
            throw new ArgumentException("A history page cursor must be canonical v2:<sequence>.", name);
    }

    private Uri Route(TargetDiscoveryDocument discovery, TargetControlCredentials? credentials,
        Func<TargetRunHistoryRoutes, string> select, string? runId, string? after, bool allowsAfter)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        // No remote descriptor may influence credential-bearing dispatch until validated.
        _ = NativeJson.SerializeUtf8(discovery);
        if (discovery.Kind != "zeroshot.native-v2-target/v2" || discovery.Audience != "controller" ||
            discovery.Authentication == TargetAuthentication.PrivateCapability ||
            (credentials?.Authentication ?? TargetAuthentication.None) != discovery.Authentication)
            throw new ArgumentException("Discovery and supplied history authority are incompatible.");
        var capability = discovery.Extensions.RunHistory;
        if (capability is null || capability.Kind != Kind)
            throw new ArgumentException("Discovery does not advertise compatible run history.");
        var baseUrl = NativeRoutes.CapabilityBaseUrl(client.Origin, capability.BaseUrl);
        // Native build_run_history_descriptor compiles all three routes before any is used.
        var routes = capability.RouteTemplates;
        _ = NativeRoutes.RunIdPath(routes.List, requiresRunId: false, AfterQuery);
        _ = NativeRoutes.RunIdPath(routes.Detail, requiresRunId: true, query: null);
        _ = NativeRoutes.RunIdPath(routes.Page, requiresRunId: true, AfterQuery);
        return NativeRoutes.RunIdRoute(baseUrl, select(routes), runId, allowsAfter ? AfterQuery : null, ("after", after));
    }
}

/// <summary>Native history/contract.rs and profile_ui/runs.rs checks for records received from a host.</summary>
internal static class RunHistoryRules
{
    private const int MaxListEntries = 50;
    private const int MaxReplayEvents = 256;
    private static readonly string[] RuntimeFailureReasons = ["runtime_failed", "runtime_lost"];

    internal static void List(RunHistoryList list, RunId? after)
    {
        var runs = list.Runs;
        Require(runs.Length <= MaxListEntries &&
            (list.NextCursor is null || TargetRunRequest.IsCanonicalRunId(list.NextCursor.Value)) &&
            runs.All(ValidSummary));
        for (var i = 1; i < runs.Length; i++)
            Require(string.CompareOrdinal(runs[i - 1].RunId.Value, runs[i].RunId.Value) > 0);
        Require(after is null || runs.All(run => string.CompareOrdinal(run.RunId.Value, after.Value) < 0));
        Require(list.NextCursor is null || (runs.Length > 0 && runs[^1].RunId == list.NextCursor));
    }

    private static bool ValidSummary(RunHistorySummary run)
    {
        if (!TargetRunRequest.IsCanonicalRunId(run.RunId.Value) ||
            (run.Cursor is not null && !TryLenient(run.Cursor, out _)) ||
            run.HistoryAvailable != (run.Cursor is not null) ||
            (run.Phase == RunHistoryPhase.Unavailable && (run.HistoryAvailable || run.Terminal is not null)) ||
            (run.Terminal is not null && run.Phase != RunHistoryPhase.Finished))
            return false;
        return run.RuntimeFailure is null ||
            (run.Terminal is FailedHistorySynopsis failed && run.Phase == RunHistoryPhase.Finished &&
             failed.Reason == run.RuntimeFailure.Reason);
    }

    internal static void Definition(RunDefinition definition, RunId expected)
    {
        Require(definition.Version == 1 && definition.ProjectionVersion == 1 && definition.RunId == expected &&
            definition.HistoryAvailable);
        if (!TryCanonical(definition.Cursor, out var cursor) || !TryCanonical(definition.History.Cursor, out var history) ||
            !TryCanonical(definition.History.InitialCursor, out var initial) || initial != 0 || cursor != history)
            throw Invalid();
        if (definition.RuntimeFailure is not { } failure) return;
        ValidRuntimeFailure(failure, cursor);
        Require(definition.Phase == RunPhase.Finished && !definition.History.Complete &&
            definition.Terminal is FailedTerminalResult { Reason.Value: var reason } && reason == failure.Reason);
    }

    internal static void Page(HistoryPage page, Cursor after)
    {
        if (!TryCanonical(after, out var requested) || !TryCanonical(page.NextCursor, out var next) ||
            !TryCanonical(page.HeadCursor, out var head))
            throw Invalid();
        Require(page.Events.Length <= MaxReplayEvents && next >= requested && next <= head && page.Complete == (next == head));
        var positions = new Dictionary<ulong, int>();
        var sequence = requested;
        foreach (var record in page.Events)
        {
            if (!TryCanonical(record.Cursor, out var at) || at != sequence + 1) throw Invalid();
            sequence = at;
            positions[at] = positions.Count;
        }
        Require(sequence == next && (page.Events.Length > 0 || next == head));
        var previous = -1;
        foreach (var control in page.Control)
        {
            if (!TryCanonical(control.Cursor, out var at) || !positions.TryGetValue(at, out var position) || position < previous)
                throw Invalid();
            previous = position;
        }
        if (page.RuntimeFailure is not { } failure) return;
        ValidRuntimeFailure(failure, head);
        Require(page.Finished);
    }

    private static void ValidRuntimeFailure(RuntimeFailure failure, ulong head)
        => Require(RuntimeFailureReasons.Contains(failure.Reason) && TryCanonical(failure.AtCursor, out var at) && at <= head);

    /// <summary>Exact <c>v2:&lt;sequence&gt;</c> spelling with sequence at most i64::MAX.</summary>
    internal static bool TryCanonical(Cursor cursor, out ulong sequence)
        => TryLenient(cursor, out sequence) && sequence <= long.MaxValue &&
            cursor.Value == "v2:" + sequence.ToString(CultureInfo.InvariantCulture);

    // Native cursor_sequence: "v2:" followed by Rust's u64 parser, which admits one leading '+'.
    private static bool TryLenient(Cursor cursor, out ulong sequence)
    {
        sequence = 0;
        if (!cursor.Value.StartsWith("v2:", StringComparison.Ordinal)) return false;
        var digits = cursor.Value.AsSpan(3);
        if (digits.StartsWith("+")) digits = digits[1..];
        return ulong.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out sequence);
    }

    private static void Require(bool valid)
    {
        if (!valid) throw Invalid();
    }

    private static JsonException Invalid() => new("Run history violates the native contract.");
}
