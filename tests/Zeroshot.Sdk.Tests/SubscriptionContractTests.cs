using System.Text;
using System.Text.Json;
using TUnit.Core;
using Zeroshot.Native;
using Zeroshot.Native.Contracts;

namespace Zeroshot.Client.Tests;

public sealed class SubscriptionContractTests
{
    internal const string Source = """{"repository":"owner/repo","branch":"main","revision":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""";
    internal const string Watch = """{"subscriptionId":"watch","runId":"run-1","title":"title","source":SOURCE,"size":"small","cursor":"opaque cursor /? SECRET","status":{"phase":"finished","terminalResult":{"status":"succeeded","output":{"answer":42}},"metadata":{"tokenUsage":{"inputTokens":7,"outputTokens":3,"complete":true}}}}""";
    internal static string WatchEvent => Watch.Replace("SOURCE", Source);
    internal const string LogEvent = """{"subscriptionId":"logs","runId":"run-1","cursor":"log boundary /?","timestamp":1234567,"execution":"worker:1","record":{"level":"warn","target":"environment.setup","message":"retained text"}}""";
    private static T Read<T>(string value) => NativeJson.DeserializeUtf8<T>(Encoding.UTF8.GetBytes(value));
    internal static void Check(bool condition, string message = "Subscription assertion failed.") { if (!condition) throw new InvalidOperationException(message); }
    private static void Reject<T>(string value)
    {
        try { _ = Read<T>(value); }
        catch (JsonException) { return; }
        throw new InvalidOperationException("Invalid subscription contract was accepted.");
    }

    [Test]
    public void FullDistinctNativeEventsRetainMetadataAndOpaqueValues()
    {
        var watch = Read<RunWatchEventNotification>(WatchEvent);
        Check(watch.Source.Repository.Value == "owner/repo" && watch.Size == RunSize.Small);
        var status = (FinishedRunStatus)watch.Status;
        Check(((SucceededTerminalResult)status.TerminalResult).Output.GetProperty("answer").GetInt32() == 42);
        Check(status.Metadata.TokenUsage is { InputTokens.Value: 7, OutputTokens.Value: 3, Complete: true });
        Check(Read<RunWatchEventNotification>(Encoding.UTF8.GetString(NativeJson.SerializeUtf8(watch))).Cursor == watch.Cursor);
        var log = Read<RunLogEventNotification>(LogEvent);
        Check(log.Timestamp.Value == 1234567 && log.Execution!.Value == "worker:1" && log.Record.Level == LogLevel.Warn);
        Check(log.Record.Target.Value == "environment.setup" && log.Record.Message.Value == "retained text");
        Check(!watch.ToString().Contains("SECRET") && !log.ToString().Contains("retained"));
        Reject<RunLogEventNotification>(WatchEvent);
        Reject<RunWatchEventNotification>(LogEvent);
        Reject<RunWatchEventNotification>(WatchEvent[..^1] + ",\"workspaceRecovery\":{}}");
    }

    [Test]
    public void OptionalSelectorsAndCursorsPreserveNativeOmissionAndNull()
    {
        var parameters = Read<RunLogsParams>("""{"runId":"run-1","fromCursor":null,"execution":null}""");
        Check(Encoding.UTF8.GetString(NativeJson.SerializeUtf8(parameters)) == "{\"runId\":\"run-1\"}");
        parameters = parameters with { FromCursor = new("opaque /? boundary"), Execution = new("worker:1") };
        var roundtrip = NativeJson.DeserializeUtf8<RunLogsParams>(NativeJson.SerializeUtf8(parameters));
        Check(roundtrip.FromCursor == parameters.FromCursor && roundtrip.Execution == parameters.Execution);
        var closed = Read<SubscriptionClosedNotification>("""{"subscriptionId":"s","reason":"SOURCE_UNAVAILABLE","lastDeliveredCursor":"server cursor"}""");
        Check(closed.Reason == SubscriptionCloseReason.SourceUnavailable && closed.LastDeliveredCursor!.Value == "server cursor");
        Reject<SubscriptionClosedNotification>("""{"subscriptionId":"s","reason":"success"}""");
        Reject<RunWatchParams>("""{"runId":"run-1","execution":"worker:1"}""");
        _ = NativeJson.SerializeUtf8(new SubscriptionCancelParams { SubscriptionId = new("opaque / cancel") });
    }

    [Test]
    public void LogsEnforcePositiveSafeTimestampsAndNativeUtf8TextBounds()
    {
        foreach (var timestamp in new[] { "0", "9007199254740992", "1.5", "null" })
            Reject<RunLogEventNotification>(LogEvent.Replace("1234567", timestamp));
        Check(Read<RunLogEventNotification>(LogEvent.Replace("1234567", "1234567e0")).Timestamp.Value == 1234567);
        Reject<RunLogEventNotification>(LogEvent.Replace("environment.setup", new string('é', 65)));
        Reject<RunLogEventNotification>(LogEvent.Replace("retained text", new string('é', 8193)));
        Reject<RunLogEventNotification>(LogEvent.Replace("retained text", "bad\\nmessage"));
        Reject<RunLogEventNotification>(LogEvent.Replace("\"timestamp\":1234567,", ""));
        _ = Read<RunLogEventNotification>(LogEvent.Replace("retained text", ""));
        _ = Read<RunLogEventNotification>(LogEvent.Replace("retained text", new string('é', 8192)));
    }
}
