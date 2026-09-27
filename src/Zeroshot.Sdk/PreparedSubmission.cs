using System.Text.Json;
using Zeroshot.Native;
using Zeroshot.Native.Contracts;

namespace Zeroshot;

/// <summary>An immutable, credential-free retained request. Authored content may still be sensitive.</summary>
public sealed class PreparedSubmission
{
    private readonly byte[] utf8;
    public RunId RunId { get; }
    public RunSubmission Submission { get; }

    private PreparedSubmission(byte[] utf8, RunSubmitParams envelope)
    {
        this.utf8 = utf8;
        RunId = envelope.RunId;
        Submission = envelope.Submission;
    }

    public static PreparedSubmission Create(RunId runId, RunSubmission submission)
    {
        ArgumentNullException.ThrowIfNull(runId);
        ArgumentNullException.ThrowIfNull(submission);
        return ImportUtf8(NativeJson.SerializeUtf8(new RunSubmitParams { RunId = runId, Submission = submission }));
    }

    public static PreparedSubmission ImportUtf8(ReadOnlySpan<byte> utf8)
    {
        var owned = utf8.ToArray();
        var envelope = NativeJson.DeserializeUtf8<RunSubmitParams>(owned);
        // This retained envelope is intended for the fixed target HTTP submission boundary.
        var id = envelope.RunId.Value;
        if (!Guid.TryParseExact(id, "D", out var guid) || guid.ToString("D") != id || id[14] != '7' || "89ab".IndexOf(id[19]) < 0)
            throw new JsonException("Prepared submission requires a canonical UUIDv7 run ID.");
        return new PreparedSubmission(owned, envelope);
    }

    /// <summary>Returns an independent copy of the exact retained UTF-8, including whitespace and property order.</summary>
    public byte[] ExportUtf8() => (byte[])utf8.Clone();
    public override string ToString() => nameof(PreparedSubmission);
}
