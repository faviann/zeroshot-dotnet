using System.Globalization;
using System.Text.Json;
using Zeroshot.Native.Contracts;

namespace Zeroshot.Native;

/// <summary>Native history/contract.rs and profile_ui/runs.rs checks for records received from a host.</summary>
internal static class RunHistoryRules
{
    /// <summary>The cursor before a run's first history record; native reads from it when no cursor is sent.</summary>
    internal static readonly Cursor InitialCursor = new("v2:0");
    internal const string PageCursorMessage = "A history page cursor must be canonical v2:<sequence>.";
    private const int MaxListEntries = 50;
    private const int MaxReplayEvents = 256;
    private static readonly string[] RuntimeFailureReasons = ["runtime_failed", "runtime_lost"];

    // Caller input every history binding (public, dashboard and private export) checks before sending.
    internal static void RequireRunId(RunId runId, string name)
    {
        ArgumentNullException.ThrowIfNull(runId, name);
        if (!TargetRunRequest.IsCanonicalRunId(runId.Value))
            throw new ArgumentException("Run history requires a canonical UUIDv7 run ID.", name);
    }

    internal static void RequireCursor(Cursor cursor, string name, string message = PageCursorMessage)
    {
        if (!TryCanonical(cursor, out _)) throw new ArgumentException(message, name);
    }

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
