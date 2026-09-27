using System.Collections.Immutable;
using System.Text.Json;
using Zeroshot;
using Zeroshot.Native;
using Zeroshot.Native.Contracts;

var assembly = typeof(PreparedSubmission).Assembly;
if (assembly.GetName().Name != "Zeroshot.Client" ||
    assembly.GetExportedTypes().Any(t => t.Namespace is not ("Zeroshot" or "Zeroshot.Native" or "Zeroshot.Native.Contracts")))
    throw new InvalidOperationException("Unexpected public package surface.");

var graph = new GraphSpec
{
    Profile = GraphProfile.Full,
    InitialInput = new NullPayload(),
    Policy = new PolicyBinding { Policy = new PolicyRef("policy.native-v2@1"), Default = PolicyDefault.Deny },
    Root = new SucceedNode { Name = new NodeName("done"), Output = new NullPayload(), Bindings = [] }
};
var submission = new RunSubmission
{
    Title = new RunTitle("Local contract example"),
    Graph = graph,
    InitialInput = JsonSerializer.SerializeToElement<object?>(null),
    Runtime = new ClaudeRuntime
    {
        Provider = ClaudeProvider.Anthropic,
        Size = RunSize.Medium,
        Nodes = ImmutableDictionary<string, NodeRuntimeBinding>.Empty
    },
    Environment = new RuntimeEnvironment(),
    Source = new ResolvedSource
    {
        Repository = new SourceRepositoryId("acme/project"),
        Branch = new SourceBranchId("main"),
        Revision = new SourceRevisionId(new string('a', 40))
    },
    SubmissionKey = new IdempotencyKey("external-consumer-stable-key")
};
var graphCopy = NativeJson.DeserializeUtf8<GraphSpec>(NativeJson.SerializeUtf8(graph));
var prepared = PreparedSubmission.Create(new RunId("018f5e78-7f95-7c22-8d98-3f15af20c991"), submission);
var retained = prepared.ExportUtf8();
var imported = PreparedSubmission.ImportUtf8(retained);
if (!retained.SequenceEqual(imported.ExportUtf8()) || graphCopy.Root is not SucceedNode ||
    imported.Submission.SubmissionKey.Value != submission.SubmissionKey.Value ||
    imported.Submission.Source != submission.Source)
    throw new InvalidOperationException("Local contract round-trip failed.");
Console.WriteLine("Created, serialized and reimported typed definitions and an exact prepared request locally.");
