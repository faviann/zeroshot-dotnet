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

    // Native compile_run_id_route_segments (history.rs and hosted_runs.rs): literal segments, at most one
    // whole {run_id} segment and exactly the operation's query suffix, if any. Returns the path template.
    // Merge plans (merge_plans.rs compile_route) apply the same rules to {plan_id}.
    internal static string RunIdPath(string template, bool requiresRunId, string? query, string variable = "{run_id}")
    {
        if (string.IsNullOrEmpty(template) || template.Length > 2048 || !template.StartsWith('/') ||
            template.StartsWith("//", StringComparison.Ordinal) || template.IndexOfAny(['\\', '#']) >= 0 ||
            template.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
            throw Invalid();
        var path = query is not null && template.EndsWith(query, StringComparison.Ordinal) ? template[..^query.Length] : template;
        var segments = path.Split('/').Skip(1).ToArray();
        if ((path != template) != (query is not null) || path.Contains('?') ||
            segments.Count(segment => segment == variable) != (requiresRunId ? 1 : 0) ||
            !segments.All(segment => segment == variable || IsLiteralSegment(segment)))
            throw Invalid();
        return path;
    }

    internal static Uri RunIdRoute(Uri baseUrl, string template, string? runId, string? query,
        params (string Name, string? Value)[] values)
        => VariableRoute(baseUrl, template, "{run_id}", runId, query, values);

    // Segments append to the capability base path. The variable's value is one percent-encoded segment and
    // present query values are form-encoded in the template's order.
    internal static Uri VariableRoute(Uri baseUrl, string template, string variable, string? value, string? query,
        (string Name, string? Value)[] values)
    {
        var path = RunIdPath(template, value is not null, query, variable);
        var prefix = baseUrl.AbsoluteUri.EndsWith('/') ? baseUrl.AbsoluteUri[..^1] : baseUrl.AbsoluteUri;
        var url = SameOriginUrl(baseUrl, prefix + (value is null ? path : path.Replace(variable, EscapeSegment(value), StringComparison.Ordinal)));
        var form = FormEncode(values.Where(pair => pair.Value is not null).Select(pair => (pair.Name, pair.Value!)));
        return form.Length == 0 ? url
            : new Uri(url.OriginalString + "?" + form, new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true });
    }

    /// <summary>
    /// Whether native's url path-segment setter sends <paramref name="value"/> unchanged: it skips <c>.</c>
    /// and <c>..</c> segments and strips tab, CR and LF.
    /// </summary>
    internal static bool IsAddressableSegment(string value)
        => value is not ("." or "..") && value.IndexOfAny(['\t', '\r', '\n']) < 0;

    // The url crate's special-scheme path-segment percent-encode set, with uppercase hex.
    private static string EscapeSegment(string value)
    {
        var escaped = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(value))
            escaped.Append(b is < 0x20 or >= 0x7F or (byte)' ' or (byte)'"' or (byte)'#' or (byte)'<' or (byte)'>' or
                (byte)'?' or (byte)'`' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%' or (byte)'\\'
                ? $"%{b:X2}" : ((char)b).ToString());
        return escaped.ToString();
    }

    // The WHATWG application/x-www-form-urlencoded byte serializer used by native's url/reqwest forms.
    internal static string FormEncode(IEnumerable<(string Name, string Value)> pairs)
    {
        var form = new StringBuilder();
        foreach (var (name, value) in pairs)
        {
            if (form.Length > 0) form.Append('&');
            Append(name); form.Append('='); Append(value);
        }
        return form.ToString();

        void Append(string text)
        {
            foreach (var b in Encoding.UTF8.GetBytes(text))
                form.Append(char.IsAsciiLetterOrDigit((char)b) || b is (byte)'*' or (byte)'-' or (byte)'.' or (byte)'_'
                    ? ((char)b).ToString() : b == (byte)' ' ? "+" : $"%{b:X2}");
        }
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
