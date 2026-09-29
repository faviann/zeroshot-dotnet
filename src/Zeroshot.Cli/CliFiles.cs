namespace Zeroshot.Cli;

/// <summary>
/// Explicit file reads and writes named by the invocation. Writes are plain file writes: no transactional
/// replacement and no fsync durability.
/// </summary>
internal static class CliFiles
{
    public static byte[] Read(string path, string what)
    {
        try { return File.ReadAllBytes(path); }
        catch (Exception error) when (IsFileError(error))
        { throw CliFailure.Input($"Cannot read the {what} file '{path}': {Describe(error)}."); }
    }

    /// <summary>
    /// Writes exactly <paramref name="bytes"/>. An existing destination is refused unless the invocation gave
    /// <c>--overwrite</c>; a file this call created (with or without <c>--overwrite</c>) is removed again if writing it fails.
    /// </summary>
    public static void Write(string path, ReadOnlySpan<byte> bytes, bool overwrite, string what)
    {
        var existed = File.Exists(path) || Directory.Exists(path);
        FileStream stream;
        try { stream = new FileStream(path, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.None); }
        catch (IOException) when (!overwrite && (File.Exists(path) || Directory.Exists(path)))
        { throw CliFailure.OutputExists($"The {what} destination '{path}' already exists; pass --overwrite to replace it."); }
        catch (Exception error) when (IsFileError(error))
        { throw CliFailure.Output($"Cannot write the {what} file '{path}': {Describe(error)}."); }

        try
        {
            using (stream) stream.Write(bytes);
        }
        catch (Exception error) when (IsFileError(error))
        {
            if (!existed) try { File.Delete(path); } catch (Exception cleanup) when (IsFileError(cleanup)) { }
            throw CliFailure.Output($"Cannot write the {what} file '{path}': {Describe(error)}.");
        }
    }

    private static bool IsFileError(Exception error)
        => error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;

    private static string Describe(Exception error) => error switch
    {
        FileNotFoundException or DirectoryNotFoundException => "not found",
        UnauthorizedAccessException => "access denied",
        ArgumentException or NotSupportedException => "invalid path",
        _ => "I/O failure",
    };
}
