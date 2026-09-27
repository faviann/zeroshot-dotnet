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
        TargetRunRequest.ValidateRunId(envelope.RunId);
        return new PreparedSubmission(owned, envelope);
    }

    /// <summary>Returns an independent copy of the exact retained UTF-8, including whitespace and property order.</summary>
    public byte[] ExportUtf8() => (byte[])utf8.Clone();

    internal byte[] WithCredentials(TargetRunCredentials credentials)
    {
        // Insert outer credentials before the closing brace. Every retained field keeps its
        // original bytes, including whitespace, ordering, escapes and numeric spellings.
        var fresh = NativeJson.SerializeUtf8(new TargetRunCredentials
        {
            Connections = credentials.Connections, ConnectionResolver = credentials.ConnectionResolver,
            GithubToken = credentials.GithubToken
        });
        var end = utf8.Length - 1;
        while (utf8[end] != (byte)'}') end--;
        var body = new byte[utf8.Length + fresh.Length - 1];
        utf8.AsSpan(0, end).CopyTo(body);
        body[end] = (byte)',';
        fresh.AsSpan(1, fresh.Length - 2).CopyTo(body.AsSpan(end + 1));
        utf8.AsSpan(end).CopyTo(body.AsSpan(end + fresh.Length - 1));
        return body;
    }
    public override string ToString() => nameof(PreparedSubmission);
}
