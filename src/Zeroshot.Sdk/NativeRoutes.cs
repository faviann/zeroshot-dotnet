using System.Text;

namespace Zeroshot.Native;

// Pinned controller_authority/contract.rs foundation. Capability bindings add their
// exact variable and descriptor contracts here when their operations are implemented.
internal static class NativeRoutes
{
    internal static Uri SameOriginUrl(Uri origin, string value)
    {
        if (!TrySafeAbsolute(value, out var url) || url.Scheme != origin.Scheme ||
            !SameAuthority(origin, url) || url.Host.Trim('[', ']') != url.IdnHost ||
            !value.StartsWith(url.GetLeftPart(UriPartial.Authority) + "/", StringComparison.Ordinal))
            throw Invalid();
        var path = value[(value.IndexOf('/', value.IndexOf("://", StringComparison.Ordinal) + 3))..];
        if (path.Any(c => c > 127 || c is '"' or '<' or '>' or '`') || path.Split('/').Any(segment =>
            segment.Replace("%2e", ".", StringComparison.OrdinalIgnoreCase) is "." or ".."))
            throw Invalid();
        // System.Uri otherwise decodes unreserved escapes (e.g. %41), unlike native's
        // URL contract. Keep the validated path's exact spelling on HTTP dispatch.
        return new Uri(value, new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true });
    }

    internal static Uri CapabilityBaseUrl(Uri origin, string value)
        => value == origin.AbsoluteUri.TrimEnd('/') ? origin : SameOriginUrl(origin, value);

    internal static Uri SameOriginPath(Uri origin, string path)
    {
        if (string.IsNullOrEmpty(path) || !path.StartsWith('/') || path.StartsWith("//", StringComparison.Ordinal))
            throw Invalid();
        return SameOriginUrl(origin, origin.GetLeftPart(UriPartial.Authority) + path);
    }

    internal static Uri CompileLiteralRoute(Uri baseUrl, string template)
    {
        if (string.IsNullOrEmpty(template) || template.Length > 2048 || !template.StartsWith('/') ||
            !template.Split('/').Skip(1).All(IsLiteralSegment))
            throw Invalid();
        var prefix = baseUrl.AbsoluteUri.EndsWith('/') ? baseUrl.AbsoluteUri[..^1] : baseUrl.AbsoluteUri;
        return SameOriginUrl(baseUrl, prefix + template);
    }

    // Native contract/history.rs compile_route: literal segments, at most one whole {run_id}
    // segment and an optional {?after} suffix, appended to the capability base path.
    internal static Uri RunHistoryRoute(Uri baseUrl, string template, string? runId, string? after, bool allowsAfter)
    {
        if (string.IsNullOrEmpty(template) || template.Length > 2048 || !template.StartsWith('/') ||
            template.StartsWith("//", StringComparison.Ordinal) || template.IndexOfAny(['\\', '#']) >= 0 ||
            template.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
            throw Invalid();
        var path = template.EndsWith("{?after}", StringComparison.Ordinal) ? template[..^"{?after}".Length] : template;
        var segments = path.Split('/').Skip(1).ToArray();
        if ((path != template) != allowsAfter || path.Contains('?') ||
            segments.Count(segment => segment == "{run_id}") != (runId is null ? 0 : 1) ||
            !segments.All(segment => segment == "{run_id}" || IsLiteralSegment(segment)))
            throw Invalid();
        var prefix = baseUrl.AbsoluteUri.EndsWith('/') ? baseUrl.AbsoluteUri[..^1] : baseUrl.AbsoluteUri;
        var url = SameOriginUrl(baseUrl, prefix + (runId is null ? path : path.Replace("{run_id}", runId, StringComparison.Ordinal)));
        if (after is null) return url;
        // Native appends the pair with application/x-www-form-urlencoded byte serialization.
        var query = new StringBuilder("?after=");
        foreach (var b in Encoding.UTF8.GetBytes(after))
            query.Append(char.IsAsciiLetterOrDigit((char)b) || b is (byte)'*' or (byte)'-' or (byte)'.' or (byte)'_'
                ? ((char)b).ToString() : b == (byte)' ' ? "+" : $"%{b:X2}");
        return new Uri(url.OriginalString + query, new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true });
    }

    internal static Uri SessionEndpoint(Uri origin, string endpoint)
    {
        var scheme = origin.Scheme == "https" ? "wss" : "ws";
        if (!TrySafeAbsolute(endpoint, out var url) || !endpoint.StartsWith(scheme + "://", StringComparison.Ordinal) ||
            !SameAuthority(origin, url) || !RawHost(endpoint).Equals(origin.IdnHost, StringComparison.OrdinalIgnoreCase))
            throw Invalid();
        return url;
    }

    private static bool IsLiteralSegment(string value) => value.Length > 0 && value is not ("." or "..") &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~');

    private static bool SameAuthority(Uri origin, Uri url)
        => string.Equals(origin.IdnHost, url.IdnHost, StringComparison.OrdinalIgnoreCase) && origin.Port == url.Port;

    private static string RawHost(string value)
    {
        var authority = value[(value.IndexOf("://", StringComparison.Ordinal) + 3)..].Split('/')[0];
        return authority.StartsWith('[') ? authority[1..authority.IndexOf(']')] : authority.Split(':')[0];
    }

    private static bool TrySafeAbsolute(string value, out Uri url)
    {
        url = null!;
        if (string.IsNullOrEmpty(value) || value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) ||
            value.IndexOfAny(['\\', '?', '#', '{', '}']) >= 0 ||
            !Uri.TryCreate(value, UriKind.Absolute, out var parsed) || string.IsNullOrEmpty(parsed.Host) ||
            !string.IsNullOrEmpty(parsed.UserInfo)) return false;
        var schemeEnd = value.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0) return false;
        var authorityEnd = value.IndexOf('/', schemeEnd + 3);
        if (value.AsSpan(schemeEnd + 3, (authorityEnd < 0 ? value.Length : authorityEnd) - schemeEnd - 3).Contains('@'))
            return false;
        for (var i = 0; i < value.Length; i++)
            if (value[i] == '%' && (i + 2 >= value.Length || !Uri.IsHexDigit(value[++i]) || !Uri.IsHexDigit(value[++i])))
                return false;
        url = parsed;
        return true;
    }

    private static ArgumentException Invalid() => new("Invalid native route or endpoint.");
}
