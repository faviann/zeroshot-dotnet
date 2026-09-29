namespace Zeroshot.Cli;

internal static class ExitCodes
{
    public const int Success = 0;
    public const int Failure = 1;
    public const int Invalid = 2;
}

/// <summary>
/// A failure reported as one safe error record. The message is CLI-authored: it may name files, fields and
/// environment variables, but never request content, credential values or remote text.
/// </summary>
internal sealed class CliFailure(string category, string message, int exitCode) : Exception(message)
{
    public string Category { get; } = category;
    public int ExitCode { get; } = exitCode;

    public static CliFailure Invocation(string message) => new("invocation", message, ExitCodes.Invalid);
    public static CliFailure Configuration(string message) => new("configuration", message, ExitCodes.Invalid);
    public static CliFailure Input(string message) => new("input", message, ExitCodes.Invalid);
    public static CliFailure Credentials(string message) => new("credentials", message, ExitCodes.Invalid);
    public static CliFailure OutputExists(string message) => new("output-exists", message, ExitCodes.Invalid);
    public static CliFailure Output(string message) => new("output", message, ExitCodes.Failure);
}
