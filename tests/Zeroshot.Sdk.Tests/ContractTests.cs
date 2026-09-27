using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TUnit.Core;
using Zeroshot;
using Zeroshot.Native;
using Zeroshot.Native.Contracts;

namespace Zeroshot.Client.Tests;

public sealed class ContractTests
{
    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (JsonException error) { Check(!error.ToString().Contains("must-not-be-retained"), "Error exposed authored data."); return; }
        throw new InvalidOperationException("Invalid contract was accepted.");
    }
    private static void Roundtrip<T>(string fixture)
    {
        using var data = JsonDocument.Parse(Fixture(fixture));
        foreach (var value in data.RootElement.EnumerateArray())
        {
            var parsed = NativeJson.DeserializeUtf8<T>(Encoding.UTF8.GetBytes(value.GetRawText()));
            _ = NativeJson.DeserializeUtf8<T>(NativeJson.SerializeUtf8(parsed));
        }
    }

    [Test]
    public void GoldenGraphPayloadRuntimeAndWorkerAlternatives()
    {
        Roundtrip<GraphSpec>("graphs.json");
        Roundtrip<PayloadType>("payloads.json");
        Roundtrip<RuntimePlan>("runtimes.json");
        Roundtrip<WorkerOutcome>("outcomes.json");
        _ = NativeJson.DeserializeUtf8<WorkerDescriptor>(Fixture("worker.json"));
        _ = NativeJson.DeserializeUtf8<ArtifactRef>(Fixture("artifact.json"));
        using var schema = JsonDocument.Parse(NativeSchemas.ExportCompiledIrUtf8());
        Check(schema.RootElement.GetProperty("title").GetString() == "CompiledGraphIr", "Missing compiled IR schema.");
    }

    [Test]
    public void GoldenInvalidEnvelopesFailLocally()
    {
        using var cases = JsonDocument.Parse(Fixture("negative-prepared.json"));
        foreach (var example in cases.RootElement.EnumerateArray())
        {
            try { Reject(() => PreparedSubmission.ImportUtf8(Encoding.UTF8.GetBytes(example.GetProperty("value").GetRawText()))); }
            catch (InvalidOperationException) { throw new InvalidOperationException("Accepted negative fixture: " + example.GetProperty("name").GetString()); }
        }
    }

    [Test]
    public void ImportPreservesBytesAndOwnsItsImmutableSnapshot()
    {
        var node = JsonNode.Parse(Fixture("prepared.json"))!;
        node["submission"]!["initialInput"] = JsonNode.Parse("""{"z":"must-not-be-retained","a":[true,null,3]}""");
        node["submission"]!["environment"] = JsonNode.Parse("""{"setup":"sensitive-script","variables":{"PUBLIC":"authored"}}""");
        var reordered = " \r\n{ \"submission\" : " + node["submission"]!.ToJsonString() + ", \"runId\":\"018f5e78-7f95-7c22-8d98-3f15af20c991\" }\t\n";
        var input = Encoding.UTF8.GetBytes(reordered);
        var expected = input.ToArray();
        var prepared = PreparedSubmission.ImportUtf8(input);
        Array.Fill(input, (byte)'x');
        var export = prepared.ExportUtf8();
        Check(export.SequenceEqual(expected), "Import changed retained bytes.");
        Array.Fill(export, (byte)'x');
        Check(prepared.ExportUtf8().SequenceEqual(expected), "Export exposed mutable retained storage.");
        Check(prepared.Submission.Source.Revision.Value == new string('a', 40), "Source changed.");
        Check(prepared.Submission.SubmissionKey.Value == "debug-fixture", "Key changed.");
        Check(prepared.Submission.InitialInput.GetProperty("z").GetString() == "must-not-be-retained", "Input changed.");
        Check(!prepared.ToString().Contains("must-not-be-retained") && !prepared.Submission.ToString().Contains("must-not-be-retained"), "Default formatting exposed payload.");
        PreparedSubmission created;
        using (var authored = JsonDocument.Parse("""{"private":"content"}"""))
            created = PreparedSubmission.Create(prepared.RunId, prepared.Submission with { InitialInput = authored.RootElement });
        Check(created.Submission.InitialInput.GetProperty("private").GetString() == "content", "Prepared content depended on caller document lifetime.");
        var modified = created.Submission with { Title = new RunTitle("changed") };
        Check(created.Submission.Title.Value != modified.Title.Value, "With expression mutated prepared content.");
    }

    [Test]
    public void OmissionExplicitNullAndOpenAuthoredValuesSurvive()
    {
        var original = PreparedSubmission.ImportUtf8(Fixture("prepared.json"));
        Check(!original.Submission.Environment.HasValue, "Omitted environment became present.");
        var explicitNull = PreparedSubmission.Create(original.RunId, original.Submission with { Environment = new Optional<RuntimeEnvironment?>(null) });
        Check(explicitNull.Submission.Environment.HasValue && explicitNull.Submission.Environment.Value is null, "Null became omitted.");
        var empty = PreparedSubmission.Create(original.RunId, original.Submission with { Environment = new RuntimeEnvironment() });
        Check(empty.Submission.Environment.Value is not null, "Empty environment became null.");
        var raw = JsonNode.Parse(Fixture("prepared.json"))!;
        raw["submission"]!["initialInput"] = JsonNode.Parse("""{"kind":"unknown","credentials":{"callerAuthored":"sensitive"},"future":null}""");
        _ = PreparedSubmission.ImportUtf8(Encoding.UTF8.GetBytes(raw.ToJsonString()));
        // Execution/admission validation belongs to native, including reserved environment names.
        _ = NativeJson.DeserializeUtf8<RuntimeEnvironment>("""{"variables":{"PATH":"authored"}}"""u8);
    }

    [Test]
    public void UnicodeAndNativeNumericBoundaries()
    {
        var run = PreparedSubmission.ImportUtf8(Fixture("prepared.json"));
        _ = PreparedSubmission.Create(run.RunId, run.Submission with { Title = new RunTitle(string.Concat(Enumerable.Repeat("😀", 256))) });
        var graph = NativeJson.DeserializeUtf8<GraphSpec>(Encoding.UTF8.GetBytes(JsonDocument.Parse(Fixture("graphs.json")).RootElement[0].GetRawText()));
        var leaf = (StepNode)graph.Root;
        _ = NativeJson.DeserializeUtf8<StepNode>(NativeJson.SerializeUtf8(leaf));
        Reject(() => NativeJson.SerializeUtf8(new RuntimeEnvironment { Setup = "\ud800" }));
        _ = NativeJson.SerializeUtf8(graph with { Root = leaf with { Instructions = new NodeInstructions(new string('é', 8192)), Attempts = new PositiveInteger(Generation.Maximum) } });
        Reject(() => NativeJson.DeserializeUtf8<NodeInstructions>(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new string('é', 8193)))));
        Reject(() => NativeJson.DeserializeUtf8<ConnectionKey>(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new string('é', 65)))));
        _ = NativeJson.DeserializeUtf8<ConnectionKey>(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new string('é', 64))));
        Check(NativeJson.DeserializeUtf8<PositiveInteger>("1e0"u8).Value == 1, "Native integral float spelling rejected.");
        Reject(() => NativeJson.DeserializeUtf8<PositiveInteger>("9007199254740992"u8));
        Reject(() => NativeJson.DeserializeUtf8<Generation>("-1"u8));
        Check(NativeJson.DeserializeUtf8<RequestId>("-9223372036854775808"u8).Number == long.MinValue, "Signed correlation ID boundary lost.");
        _ = NativeJson.DeserializeUtf8<RunId>("\"\""u8); // Generic RunId is not the target UUIDv7 boundary.
        Reject(() => PreparedSubmission.ImportUtf8("{\"runId\":\"\\ud800\",\"submission\":{}}"u8));
        var bytes = Fixture("prepared.json");
        var text = Encoding.UTF8.GetString(bytes).Replace("\"test\"", "\"\\ud800\"", StringComparison.Ordinal);
        Reject(() => PreparedSubmission.ImportUtf8(Encoding.UTF8.GetBytes(text)));
        var malformed = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("\"test\"", "\"INVALID\"", StringComparison.Ordinal));
        malformed[Array.IndexOf(malformed, (byte)'I')] = 0xff;
        Reject(() => PreparedSubmission.ImportUtf8(malformed));
    }

    [Test]
    public void NativeAliasesCanonicalizeOnlyTypedSerialization()
    {
        var raw = Encoding.UTF8.GetString(Fixture("prepared.json")).Replace("\"small\"", "\"tiny\"", StringComparison.Ordinal);
        var prepared = PreparedSubmission.ImportUtf8(Encoding.UTF8.GetBytes(raw));
        Check(((CodexRuntime)prepared.Submission.Runtime).Size == RunSize.Small, "Alias not decoded.");
        Check(Encoding.UTF8.GetString(prepared.ExportUtf8()) == raw, "Alias changed in retained bytes.");
        Check(Encoding.UTF8.GetString(NativeJson.SerializeUtf8(prepared.Submission.Runtime)).Contains("\"small\""), "Typed runtime serialization is not canonical.");
    }

    [Test]
    public void WorkerInvariantsAndStandaloneWireShapesDiffer()
    {
        var worker = JsonNode.Parse(Fixture("worker.json"))!;
        worker["binding"]!["protocol"] = "builtin";
        Reject(() => NativeJson.DeserializeUtf8<WorkerDescriptor>(Encoding.UTF8.GetBytes(worker.ToJsonString())));
        worker = JsonNode.Parse(Fixture("worker.json"))!;
        worker["contract"]!["errors"] = new JsonArray("crash");
        Reject(() => NativeJson.DeserializeUtf8<WorkerDescriptor>(Encoding.UTF8.GetBytes(worker.ToJsonString())));
        _ = NativeJson.DeserializeUtf8<WorkerContract>(Encoding.UTF8.GetBytes(worker["contract"]!.ToJsonString()));
        Reject(() => NativeJson.DeserializeUtf8<WorkerOutcome>("""{"status":"error","code":"crash","reason":"policy_denied"}"""u8));
        Reject(() => NativeJson.DeserializeUtf8<PayloadType>("""{"kind":"null","extra":true}"""u8));
        // Escaped tags must receive the same native-only validation as literal spellings.
        Reject(() => NativeJson.DeserializeUtf8<RuntimePlan>("""{"harness":"co\u0064ex","provider":"openai","size":"small","nodes":{"work":{"kind":"ag\u0065nt","model":"ok","connections":{"a":["TOKEN"],"b":["TOKEN"]}}}}"""u8));
        var duplicateErrors = Encoding.UTF8.GetString(Fixture("worker.json")).Replace("\"timeout\"", "\"cr\\u0061sh\"", StringComparison.Ordinal);
        Reject(() => NativeJson.DeserializeUtf8<WorkerDescriptor>(Encoding.UTF8.GetBytes(duplicateErrors)));
    }
}
