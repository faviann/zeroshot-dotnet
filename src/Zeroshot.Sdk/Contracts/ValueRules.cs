using System.Text;
using System.Text.RegularExpressions;

namespace Zeroshot.Native.Contracts;

internal static class ValueRules
{
    internal static string Check(string kind, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        // Reject unpaired UTF-16 surrogates instead of silently replacing authored content at export.
        try { _ = new UTF8Encoding(false, true).GetByteCount(value); }
        catch (EncoderFallbackException) { throw new ArgumentException("Invalid Unicode text."); }
        var valid = kind switch
        {
            nameof(RunId) or nameof(Cursor) or nameof(SubscriptionId) => true,
            nameof(NodeName) or nameof(FieldName) or nameof(EnumLabel) => Identifier(value, 128),
            nameof(WorkerRef) or nameof(PolicyRef) or nameof(CredentialHandle) or nameof(TypeId) =>
                value.Length <= 256 && Regex.IsMatch(value, @"\A[A-Za-z_][A-Za-z0-9_.-]*@[1-9][0-9]*\z"),
            nameof(Sha256Digest) or nameof(GraphIdentity) or nameof(RequestFingerprint) => Regex.IsMatch(value, @"\A[0-9a-f]{64}\z"),
            nameof(SourceRevisionId) => Regex.IsMatch(value, @"\A[0-9a-f]{40}\z"),
            nameof(EnvironmentVariableName) => value.Length <= 128 && Regex.IsMatch(value, @"\A[A-Za-z_][A-Za-z0-9_]*\z"),
            nameof(ConnectionKey) or nameof(ExecutionRef) or nameof(BoundedLogTarget) => NonControl(value, 128, bytes: true),
            nameof(BoundedLogMessage) => !value.Any(char.IsControl) && Encoding.UTF8.GetByteCount(value) <= 16_384,
            nameof(NodeInstructions) => !string.IsNullOrWhiteSpace(value) && !value.Contains('\0') && Encoding.UTF8.GetByteCount(value) <= 16_384,
            nameof(FailReason) => Identifier(value, 128) && value is not ("unhandled" or "runtime_failed" or "runtime_lost" or "environment_setup_failed" or "environment_startup_failed" or "environment_preparation_timeout"),
            nameof(ModelId) => NonControl(value, 2048),
            nameof(SourceRepositoryId) => value.Split('/') is [var owner, var repo] && RepositoryPart(owner) && RepositoryPart(repo),
            nameof(SourceBranchId) => value.Length is > 0 and <= 255 && !value.StartsWith('-') &&
                !value.EndsWith('.') && !value.EndsWith('/') && !value.EndsWith(".lock", StringComparison.Ordinal) &&
                !value.Contains("..", StringComparison.Ordinal) && !value.Contains("@{", StringComparison.Ordinal) &&
                value.All(c => c is >= '!' and <= '~' && !"~^:?*[\\".Contains(c)),
            _ => NonControl(value, 256)
        };
        if (!valid) throw new ArgumentException($"Invalid {kind}.");
        return value;
    }

    internal static bool Identifier(string value, int max) => value.Length <= max && Regex.IsMatch(value, @"\A[A-Za-z_][A-Za-z0-9_.-]*\z");
    private static bool RepositoryPart(string value) => value.Length is > 0 and <= 100 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
    private static bool NonControl(string value, int max, bool bytes = false) => value.Length > 0 && !value.Any(char.IsControl) &&
        (bytes ? Encoding.UTF8.GetByteCount(value) : value.EnumerateRunes().Count()) <= max;
}
