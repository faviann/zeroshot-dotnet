# SDK run handles

`ZeroshotClient` prepares requests locally and opens exact known runs for status or live
attachment. It uses one `NativeClient` for every native call; direct `NativeClient` use
needs none of the SDK's binding, waiting or recovery policy.

```csharp
await using var zeroshot = new ZeroshotClient(new ZeroshotClientOptions
{
    Target = new Uri("https://target.example/"),
    NativeBinding = NativeBinding.CallerSupplied("10.9.0", "75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa"),
});

PreparedSubmission prepared = RunRequest.ParseUtf8(requestBytes).Prepare(); // or zeroshot.Prepare(request)
Run run = zeroshot.GetRun(knownRunId);                                      // no I/O
RunStatusResult status = await run.StatusAsync(ct);
if (RunResult.FromStatus(status) is { } result) result.EnsureSuccess();

await File.WriteAllTextAsync("run.json", run.Reference.ToJson(), ct);
Run reopened = zeroshot.GetRun(RunReference.Parse(await File.ReadAllTextAsync("run.json", ct)));

await foreach (RunAttachEventNotification output in run.AttachAsync(executionId, ct))
    Show(output);
```

Wrap an existing client with `new ZeroshotClient(native, binding, ownsClient: false)`.
Construction, preparation and `GetRun` perform no network I/O. Disposal never stops a run.
The SDK uses target-wide OECP sessions on direct and private targets. Hosted run
workflows stay with the lower client's `HostedRuns` operations.

## Requests and preparation

`RunRequest` holds title, graph, runtime, initial input and exact source, plus optional
environment, `RunId` and `SubmissionKey`. `ParseUtf8` reads the native submission fields
(with `submissionKey` optional) and an optional `runId`, and rejects unknown or duplicate
fields and invalid native values. `Prepare()` generates an omitted run ID as a canonical
UUIDv7 and an omitted key as `dotnet-` plus 32 lowercase hex digits, then fixes the
retained bytes through `PreparedSubmission.Create`. Each call prepares new work;
supplied identities are never replaced. Omitted, null and present environments stay
distinct.

## Native binding and run references

A `NativeBinding` is the caller's declaration of the target's native build. Native
publishes no build identity, so the binding is labelled `caller-supplied` and never
reported as remote attestation. Before each run operation, the SDK throws
`NativeBindingException` with:

- `Missing` when the client has no binding;
- `Mismatched` when the binding is not native 10.9.0 at `75ae54b6…`, or when a reopened
  reference's binding differs from the client's.

Neither case dispatches anything. Discovery and every other `NativeClient` call stay
available. `run.Reference` throws `Missing` when there is no binding to record.

A `RunReference` is the exact target, run ID and binding, with no credentials or history:

```json
{"schema":"zeroshot-dotnet/run-reference/v1","target":"https://target.example/","runId":"…","nativeBinding":{"provenance":"caller-supplied","release":"10.9.0","sourceRevision":"75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa"}}
```

`Parse` accepts exactly these fields. `GetRun(reference)` rejects a different target; it
never retargets or adopts the reference's binding.

## Status and results

`StatusAsync` returns the complete native `RunStatusResult`, including a failed run.
`RunResult.FromStatus` returns null until the run is finished, then gives `IsSuccess`,
generic JSON `Output` (a JSON null for a successful null output, C# null only on failure),
`FailureReason`, native `Metadata` and `Evidence`. Status yields
`TerminalEvidenceKind.StatusReport` with the status cursor. Native can report a status-only
terminal failure without a durable event, so this is distinct from
`RetainedTerminalEvent`, which only waiting on retained history will produce. Neither kind
proves retained completion or physical cessation. `EnsureSuccess()` throws
`RunFailedException` carrying the result.

## Connections

Status uses one lazily opened control connection per `ZeroshotClient`. The next
operation reopens it after a failure; the failed call is not retried. Initialize and
status use reserved control request capacity. Each `AttachAsync` enumeration opens its
own connection and live attachment, which it closes when enumeration ends. Cancelling
or disposing an open attachment also sends `subscription/cancel`. It never sends force,
reopens or replays; a new enumeration is a new live view.

Every `ZeroshotClient` keeps one of the native client's `MaxOecpConnections` for control.
SDK attachments are admitted only while the connections left over remain, so neither
concurrent attachment setup nor several SDK clients sharing one `NativeClient` can take
a control connection. Connections opened directly on a shared `NativeClient` count against
the same limit and remain the caller's responsibility. Request slots are only partly
separated: every connection's initialize draws on the reserved control slots, and
reopening the control connection runs discovery and connect as ordinary requests. With
SDK-only traffic at the default limits, observation setup on at most 17 connections cannot
exhaust the 32 request slots.

