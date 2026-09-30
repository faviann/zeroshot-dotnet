namespace Zeroshot.Cli;

/// <summary>
/// The declared <c>zeroshot-dotnet/cli/v1</c> output: every record kind and its field paths. A nested path such as
/// <c>attempt.outcome</c> declares a CLI-owned object's fields; an object with no declared nested paths (native status,
/// data, metadata, checkpoint, output) is carried whole. Release qualification reads this catalog from the packed tool
/// into its contract, and requires the CLI suite to emit every declared path and no undeclared one.
/// </summary>
internal static class CliContract
{
    // Every attempt has these; a submission attempt adds its run IDs, and a failed one whether cancellation ended it.
    private static readonly string[] Attempt = ["attempt", "attempt.operation", "attempt.outcome", "attempt.correlationId"];
    private static readonly string[] SubmissionIds = ["attempt.proposedRunId", "attempt.acknowledgedRunId", "attempt.runIdsMatch"];

    private static readonly string[] Result =
    [
        "result", "result.succeeded", "result.output", "result.failureReason", "result.metadata",
        "result.evidence", "result.evidence.kind", "result.evidence.cursor",
    ];

    public static readonly IReadOnlyDictionary<string, string[]> Records = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["*"] = ["schema", "kind"],
        ["prepared"] = ["proposedRunId", "path"],
        ["submission"] = ["runId", "target", .. Attempt, .. SubmissionIds],
        ["status"] = ["runId", "status", .. Result],
        ["result"] = ["runId", .. Result],
        ["force"] = ["runId", "status", .. Attempt],
        ["watch"] = ["runId", "cursor", "checkpoint", "data"],
        ["log"] = ["runId", "cursor", "execution", "timestamp", "checkpoint", "data"],
        ["attachment"] = ["runId", "execution", "data"],
        ["error"] =
        [
            "category", "operation", "message", "runId", .. Attempt, .. SubmissionIds, "attempt.cancelled",
            "evidence", "evidence.statusCursor", "evidence.lastEventCursor", "evidence.resumeAfter",
            "observation", "observation.failure", "observation.recoveries", "observation.lastDeliveredCursor",
            "native", "native.transport", "native.kind", "native.httpStatus", "native.problemCode", "native.rpcCode", "native.domainCode",
        ],
    };
}
