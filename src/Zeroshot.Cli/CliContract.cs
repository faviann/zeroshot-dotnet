using System.Text.Json;

namespace Zeroshot.Cli;

/// <summary>
/// The declared <c>zeroshot-dotnet/cli/v1</c> output: every record kind and its field paths. A nested path such as
/// <c>attempt.outcome</c> declares a CLI-owned object's fields; an object with no declared nested paths (native status,
/// data, metadata, checkpoint, output) is carried whole. <see cref="CliOutput"/> refuses to write an undeclared field,
/// and release qualification reads this catalog from the packed tool into its contract.
/// </summary>
internal static class CliContract
{
    private static readonly string[] Attempt =
    [
        "attempt", "attempt.operation", "attempt.outcome", "attempt.correlationId", "attempt.cancelled",
        "attempt.proposedRunId", "attempt.acknowledgedRunId", "attempt.runIdsMatch",
    ];

    private static readonly string[] Result =
    [
        "result", "result.succeeded", "result.output", "result.failureReason", "result.metadata",
        "result.evidence", "result.evidence.kind", "result.evidence.cursor",
    ];

    public static readonly IReadOnlyDictionary<string, string[]> Records = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["*"] = ["schema", "kind"],
        ["prepared"] = ["proposedRunId", "path"],
        ["submission"] = ["runId", "target", .. Attempt],
        ["status"] = ["runId", "status", .. Result],
        ["result"] = ["runId", .. Result],
        ["force"] = ["runId", "status", .. Attempt],
        ["watch"] = ["runId", "cursor", "checkpoint", "data"],
        ["log"] = ["runId", "cursor", "execution", "timestamp", "checkpoint", "data"],
        ["attachment"] = ["runId", "execution", "data"],
        ["error"] =
        [
            "category", "operation", "message", "runId", .. Attempt,
            "evidence", "evidence.statusCursor", "evidence.lastEventCursor", "evidence.resumeAfter",
            "observation", "observation.failure", "observation.recoveries", "observation.lastDeliveredCursor",
            "native", "native.transport", "native.kind", "native.httpStatus", "native.problemCode", "native.rpcCode", "native.domainCode",
        ],
    };

    /// <summary>Throws unless every field of <paramref name="record"/> is declared for its kind.</summary>
    public static void Check(string kind, byte[] record)
    {
        using var document = JsonDocument.Parse(record);
        Check(document.RootElement, "", [.. Records["*"], .. Records[kind]]);
    }

    private static void Check(JsonElement value, string prefix, HashSet<string> declared)
    {
        foreach (var property in value.EnumerateObject())
        {
            var path = prefix + property.Name;
            if (!declared.Contains(path)) throw new InvalidOperationException($"'{path}' is not a declared field of its cli/v1 record.");
            if (property.Value.ValueKind == JsonValueKind.Object && declared.Any(field => field.StartsWith(path + ".", StringComparison.Ordinal)))
                Check(property.Value, path + ".", declared);
        }
    }
}
