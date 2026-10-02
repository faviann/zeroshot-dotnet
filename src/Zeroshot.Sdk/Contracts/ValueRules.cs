using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Zeroshot.Native.Contracts;

/// <summary>Native string domains. Each <see cref="NativeString"/> type names the rule it follows.</summary>
internal static partial class ValueRules
{
    internal static string Check(string value, Func<string, bool> rule, string kind)
    {
        ArgumentNullException.ThrowIfNull(value);
        // Reject unpaired UTF-16 surrogates instead of silently replacing authored content at export.
        try { _ = new UTF8Encoding(false, true).GetByteCount(value); }
        catch (EncoderFallbackException) { throw new ArgumentException("Invalid Unicode text."); }
        return rule(value) ? value : throw new ArgumentException($"Invalid {kind}.");
    }

    internal static bool Any(string value) => true;
    internal static bool Text(string value) => NonControl(value, 256);
    internal static bool Identifier(string value) => value.Length <= 128 && IdentifierPattern().IsMatch(value);
    internal static bool VersionedRef(string value) => value.Length <= 256 && VersionedRefPattern().IsMatch(value);
    internal static bool Sha256(string value) => Sha256Pattern().IsMatch(value);
    internal static bool GitRevision(string value) => GitRevisionPattern().IsMatch(value);
    internal static bool ProfileName(string value) => value.Length <= 64 && ProfileNamePattern().IsMatch(value);
    internal static bool EnvironmentName(string value) => value.Length <= 128 && EnvironmentNamePattern().IsMatch(value);
    internal static bool Key(string value) => NonControl(value, 128, bytes: true);
    internal static bool LogMessage(string value) => !value.Any(char.IsControl) && Encoding.UTF8.GetByteCount(value) <= 16_384;
    internal static bool LogLine(string value) => !value.Contains('\0') && Encoding.UTF8.GetByteCount(value) <= 16_384;
    internal static bool Instructions(string value) =>
        !string.IsNullOrWhiteSpace(value) && !value.Contains('\0') && Encoding.UTF8.GetByteCount(value) <= 16_384;
    internal static bool PositiveDecimal(string value) => value.Length > 0 && value[0] != '0' && value.All(char.IsAsciiDigit) &&
        ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _);
    // Native reserves these reasons for its own failures.
    internal static bool AuthoredFailReason(string value) => Identifier(value) && value is not
        ("unhandled" or "runtime_failed" or "runtime_lost" or "environment_setup_failed" or "environment_startup_failed" or "environment_preparation_timeout");
    internal static bool Model(string value) => NonControl(value, 2048);
    internal static bool Repository(string value) => value.Split('/') is [var owner, var repo] && RepositoryPart(owner) && RepositoryPart(repo);
    internal static bool Branch(string value) => value.Length is > 0 and <= 255 && !value.StartsWith('-') &&
        !value.EndsWith('.') && !value.EndsWith('/') && !value.EndsWith(".lock", StringComparison.Ordinal) &&
        !value.Contains("..", StringComparison.Ordinal) && !value.Contains("@{", StringComparison.Ordinal) &&
        value.All(c => c is >= '!' and <= '~' && !"~^:?*[\\".Contains(c));

    private static bool RepositoryPart(string value) => value.Length is > 0 and <= 100 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
    private static bool NonControl(string value, int max, bool bytes = false) => value.Length > 0 && !value.Any(char.IsControl) &&
        (bytes ? Encoding.UTF8.GetByteCount(value) : value.EnumerateRunes().Count()) <= max;

    [GeneratedRegex(@"\A[A-Za-z_][A-Za-z0-9_.-]*\z")] private static partial Regex IdentifierPattern();
    [GeneratedRegex(@"\A[A-Za-z_][A-Za-z0-9_.-]*@[1-9][0-9]*\z")] private static partial Regex VersionedRefPattern();
    [GeneratedRegex(@"\A[0-9a-f]{64}\z")] private static partial Regex Sha256Pattern();
    [GeneratedRegex(@"\A[0-9a-f]{40}\z")] private static partial Regex GitRevisionPattern();
    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._-]*\z")] private static partial Regex ProfileNamePattern();
    [GeneratedRegex(@"\A[A-Za-z_][A-Za-z0-9_]*\z")] private static partial Regex EnvironmentNamePattern();
}
