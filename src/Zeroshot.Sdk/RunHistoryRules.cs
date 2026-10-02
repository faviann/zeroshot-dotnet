using System.Globalization;
using System.Text.Json;
using Zeroshot.Native.Contracts;

namespace Zeroshot.Native;

/// <summary>
/// Native history/contract.rs and profile_ui/runs.rs checks that depend on the request. Checks intrinsic to a
/// received record run when it is decoded.
/// </summary>
internal static class RunHistoryRules
{
    /// <summary>The cursor before a run's first history record; native reads from it when no cursor is sent.</summary>
    internal static readonly Cursor InitialCursor = new("v2:0");
    internal const string PageCursorMessage = "A history page cursor must be canonical v2:<sequence>.";

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
        => Require(after is null || list.Runs.All(run => string.CompareOrdinal(run.RunId.Value, after.Value) < 0));

    internal static void Definition(RunDefinition definition, RunId expected) => Require(definition.RunId == expected);

    // The page's events must start right after the requested cursor.
    internal static void Page(HistoryPage page, Cursor after)
        => Require(TryCanonical(after, out var requested) && TryCanonical(page.NextCursor, out var next) &&
            requested + (ulong)page.Events.Length == next);

    /// <summary>Exact <c>v2:&lt;sequence&gt;</c> spelling with sequence at most i64::MAX.</summary>
    internal static bool TryCanonical(Cursor cursor, out ulong sequence)
        => TryLenient(cursor, out sequence) && sequence <= long.MaxValue &&
            cursor.Value == "v2:" + sequence.ToString(CultureInfo.InvariantCulture);

    // Native cursor_sequence: "v2:" followed by Rust's u64 parser, which admits one leading '+'.
    internal static bool TryLenient(Cursor cursor, out ulong sequence)
    {
        sequence = 0;
        if (!cursor.Value.StartsWith("v2:", StringComparison.Ordinal)) return false;
        var digits = cursor.Value.AsSpan(3);
        if (digits.StartsWith("+")) digits = digits[1..];
        return ulong.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out sequence);
    }

    private static void Require(bool valid)
    {
        if (!valid) throw new JsonException("Run history violates the native contract.");
    }
}
