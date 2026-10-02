using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TUnit.Core;
using Zeroshot.Native;
using Zeroshot.Native.Contracts;

namespace Zeroshot.Client.Tests;

public sealed class ContractRuleTests
{
    // MergePlanSubmitRequest's own rule reads Runs; an omitted array must still surface as the missing member.
    [Test]
    public void AnOmittedRequiredArrayIsReportedAsMissing()
    {
        var plan = JsonNode.Parse(NativeJson.SerializeUtf8(new MergePlanSubmitRequest
        {
            SubmissionKey = new("plan-key"), Title = new("Release"), ExpiresAt = "2026-09-28T00:00:00Z",
            Source = new() { Repository = new("acme/project"), Branch = new("main") },
            Profile = new() { Scope = RunProfileScope.Org, Name = new("software-change") },
            Runs = [new() { Name = new("build"), InitialInput = JsonDocument.Parse("{}").RootElement.Clone() }]
        }))!.AsObject();
        plan.Remove("runs");
        try { NativeJson.DeserializeUtf8<MergePlanSubmitRequest>(Encoding.UTF8.GetBytes(plan.ToJsonString())); }
        catch (JsonException) { return; }
        throw new InvalidOperationException("A merge plan without runs was accepted.");
    }
}
