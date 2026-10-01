using System.Globalization;
using System.Text.RegularExpressions;

namespace Zeroshot.Cli;

/// <summary>CLI duration text: a whole number with an explicit unit (ms, s, m, h), or <c>infinite</c> for a wait budget.</summary>
internal static partial class CliDuration
{
    public const string Rule = "a whole number with a unit ms, s, m or h (for example 1500ms, 45s, 10m)";

    [GeneratedRegex("^([0-9]{1,18})(ms|s|m|h)$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    /// <summary>Parses a finite duration. Range rules (such as positive timeouts) stay with the SDK.</summary>
    public static bool TryParse(string text, out TimeSpan duration)
    {
        duration = default;
        var match = Pattern().Match(text);
        if (!match.Success) return false;
        var count = long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var unit = match.Groups[2].Value switch { "ms" => 1L, "s" => 1_000L, "m" => 60_000L, _ => 3_600_000L };
        if (count > (long)TimeSpan.MaxValue.TotalMilliseconds / unit) return false;
        duration = TimeSpan.FromMilliseconds(count * unit);
        return true;
    }

    /// <summary>
    /// The SDK's longest finite wait. It is checked while parsing because <c>run</c> waits only after it has
    /// submitted, and a budget the SDK would refuse must not surface after the mutation.
    /// </summary>
    public const string MaxWaitRule = "no longer than 4294967294ms (about 49.7 days)";
    private static readonly TimeSpan MaxWaitBudget = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    /// <summary>A wait budget: a finite duration up to the SDK's limit, or null for <c>infinite</c>, the only indefinite spelling.</summary>
    public static bool TryParseWaitBudget(string text, out TimeSpan? budget)
    {
        budget = null;
        if (text == "infinite") return true;
        if (!TryParse(text, out var finite) || finite > MaxWaitBudget) return false;
        budget = finite;
        return true;
    }
}
