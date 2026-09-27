using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TUnit.Core;
using Zeroshot.Native;
using Zeroshot.Native.Contracts;

namespace Zeroshot.Client.Tests;

public sealed class OecpContractTests
{
    private const string Projection = """
        {"runId":"run-1","title":"Run title","source":{"repository":"owner/repo","branch":"main","revision":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"},"size":"small","atCursor":"cursor-1","status":{"phase":"finished","terminalResult":{"status":"succeeded","output":{"answer":42}},"metadata":{"tokenUsage":{"inputTokens":7,"outputTokens":3,"cacheReadInputTokens":2,"cacheCreationInputTokens":1,"complete":false}}},"workspaceRecovery":{"recoverable":true,"connectionRequirements":{"source":["TOKEN","TOKEN"]},"resumedFrom":"prior-run","successorRunId":"next-run"}}
        """;

    private static T Read<T>(string json) => NativeJson.DeserializeUtf8<T>(Encoding.UTF8.GetBytes(json));
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Reject<T>(string json)
    {
        try { _ = Read<T>(json); }
        catch (JsonException) { return; }
        throw new InvalidOperationException("Invalid OECP contract was accepted.");
    }

    [Test]
    public void RunInspectionPreservesIdentityTerminalMetadataAndRecovery()
    {
        var status = Read<RunStatusResult>(Projection);
        Check(status.RunId.Value == "run-1" && status.Source.Repository.Value == "owner/repo" && status.AtCursor.Value == "cursor-1", "Identity was lost.");
        var terminal = (FinishedRunStatus)status.Status;
        Check(((SucceededTerminalResult)terminal.TerminalResult).Output.GetProperty("answer").GetInt32() == 42, "Terminal output was lost.");
        Check(terminal.Metadata.TokenUsage is { InputTokens.Value: 7, OutputTokens.Value: 3, CacheReadInputTokens.Value: 2, CacheCreationInputTokens.Value: 1, Complete: false }, "Terminal usage was lost.");
        var recovery = status.WorkspaceRecovery.Value;
        Check(recovery.Recoverable && recovery.ResumedFrom?.Value == "prior-run" && recovery.SuccessorRunId?.Value == "next-run", "Recovery identity was lost.");
        Check(recovery.ConnectionRequirements[new ConnectionKey("source")].Length == 2, "Native connection requirement vector was changed.");
        var roundtrip = NativeJson.DeserializeUtf8<RunStatusResult>(NativeJson.SerializeUtf8(status));
        Check(roundtrip.WorkspaceRecovery.Value.ConnectionRequirements[new ConnectionKey("source")][0].Value == "TOKEN", "Recovery field changed during serialization.");
        Check(Read<RunListResult>("{\"runs\":[" + Projection + "]}").Runs.Length == 1, "Run inventory was lost.");
    }

    [Test]
    public void DistinctStatusVariantsAndNativeDefaultsRemainDistinct()
    {
        _ = Read<RunStatus>("""{"phase":"admitted"}""");
        Check(Read<RunStatus>("""{"phase":"running","activeExecutions":[{"execution":"worker-1","node":"build"}]}""") is RunningRunStatus { ActiveExecutions.Length: 1 }, "Active execution identity was lost.");
        Check(Read<RunStatus>("""{"phase":"stopping","activeExecutions":[]}""") is StoppingRunStatus, "Stopping was conflated with running.");
        Check(Read<RunStatus>("""{"phase":"finished","terminalResult":{"status":"failed","reason":"runtime_failed"}}""") is FinishedRunStatus { Metadata.TokenUsage: null, TerminalResult: FailedTerminalResult }, "Native metadata default was lost.");
        var cluster = Read<GetResult>("""{"spec":null,"status":{"phase":"empty","observedGeneration":null,"currentRunId":null,"atCursor":null},"atCursor":null}""");
        Check(cluster.Status.Phase == Phase.Empty && cluster.Spec is null && cluster.TerminalResult is null, "Native empty cluster shape was rejected.");
        Reject<RunStatus>("""{"phase":"empty"}""");
        Reject<ClusterStatus>("""{"phase":"admitted"}""");
        Reject<RunStatus>("""{"phase":"running"}""");
        Reject<RunStatus>("""{"phase":"finished","terminalResult":{"status":"failed","reason":"failed"},"metadata":null}""");
        Reject<RunStatus>("""{"phase":"finished","terminalResult":{"status":"succeeded","output":null},"activeExecutions":[]}""");
    }

    [Test]
    public void KnownRunAndSourceIdentityAndRecoveryAreRequiredAndStrict()
    {
        foreach (var name in new[] { "runId", "title", "source", "size", "atCursor", "status" })
        {
            var document = JsonNode.Parse(Projection)!;
            document.AsObject().Remove(name);
            Reject<RunStatusResult>(document.ToJsonString());
        }
        foreach (var name in new[] { "repository", "branch", "revision" })
        {
            var document = JsonNode.Parse(Projection)!;
            document["source"]!.AsObject().Remove(name);
            Reject<RunStatusResult>(document.ToJsonString());
        }
        var recoveryNull = JsonNode.Parse(Projection)!;
        recoveryNull["workspaceRecovery"] = null;
        Reject<RunStatusResult>(recoveryNull.ToJsonString());
        Reject<WorkspaceRecovery>("""{"recoverable":true,"connectionRequirements":{"source":["not a variable"]}}""");
        Reject<WorkspaceRecovery>("{\"recoverable\":true,\"connectionRequirements\":{\"" + new string('a', 129) + "\":[]}}");
        Reject<TokenUsage>("""{"inputTokens":9007199254740992,"outputTokens":0,"complete":true}""");
        Check(Read<TokenUsage>("""{"inputTokens":7e0,"outputTokens":0,"complete":true}""").InputTokens.Value == 7, "Native integral token spelling was rejected.");
    }

    [Test]
    public void InitializeAndErrorsPreserveNativeNegotiationAndExtensibility()
    {
        var request = new InitializeParams { ProtocolVersion = "unsupported-version" };
        Check(NativeJson.DeserializeUtf8<InitializeParams>(NativeJson.SerializeUtf8(request)).ProtocolVersion == request.ProtocolVersion, "Unsupported protocol could not reach native negotiation.");
        var initialized = Read<InitializeResult>("""{"protocolVersion":"openengine.cluster/v1","capabilities":{},"status":{"phase":"empty","extension":1},"extension":1}""");
        Check(initialized.Capabilities.GraphProfiles.Length == 0 && !initialized.Capabilities.Logs, "Native capability defaults were lost.");
        Reject<InitializeResult>("""{"protocolVersion":"unsupported-version","capabilities":{},"status":{"phase":"empty"}}""");
        Reject<ServerCapabilities>("""{"graphProfiles":["openengine.graph.single-worker/v1","openengine.graph.full/v1"]}""");
        var error = Read<JsonRpcError>("""{"code":-9223372036854775808,"message":"native message","data":{"code":"FUTURE_CODE","details":{"unknown":[1,null]},"extension":1},"extension":1}""");
        Check(error.Code == long.MinValue && error.Data?.Code == "FUTURE_CODE" && error.Data.Details?.GetProperty("unknown").GetArrayLength() == 2, "RPC/domain error details were projected away.");
        Reject<JsonRpcError>("""{"code":-32000,"message":"error","data":{"details":1}}""");
        Reject<JsonRpcError>("""{"code":-32000.5,"message":"error"}""");
    }
}
