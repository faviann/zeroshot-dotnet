# zeroshot-dotnet

`zeroshot-dotnet` is a thin command over `ZeroshotClient`. The SDK parses requests, run
references and checkpoints, prepares submissions and validates values. The CLI parses
arguments and the target configuration, reads and writes the files you name, and
formats output. Run `zeroshot-dotnet --help` for the complete grammar.

`prepare`, `run`, `status`, `wait` and `force-stop` run in this build. `watch`, `logs`
and `attach` check their arguments, configuration, run file and credentials. They then
stop with an `unavailable` error (exit 2), before any network I/O.

The SDK does all waiting, stream recovery and outcome classification. The CLI never
retries or replays a mutation, and it never sends a stop that you did not request.

## Prepare a retained request

```sh
zeroshot-dotnet prepare --request request.json --out prepared.json [--overwrite] [--json]
```

`RunRequest.ParseUtf8(bytes).Prepare()` fixes the run ID and submission key. Omitted
identity is generated, and supplied identity is kept. The file receives exactly
`PreparedSubmission.ExportUtf8()`. Preparation needs no configuration, target,
credentials or network. The receipt names the file and the proposed run ID. It never
contains request content.

## Run

```sh
zeroshot-dotnet run --config target.json (--request request.json | --prepared prepared.json)
                    [--detach | --timeout WAIT] [--save-request FILE] [--save-run FILE] [--overwrite]
```

1. With `--request`, the SDK prepares the request and fixes its proposed run ID. With
   `--prepared`, the exact retained bytes are sent unchanged.
2. `--save-request FILE` writes `PreparedSubmission.ExportUtf8()` before anything is sent.
   It applies only to `--request`. If the write fails, nothing is sent.
3. `ZeroshotClient.SubmitAttemptAsync` sends the request once.
4. When the target acknowledges the request, the `submission` record is written. The
   acknowledged run ID can differ from the proposed run ID. From this point, every
   command output and file uses the acknowledged run ID.
5. `--save-run FILE` then writes `Run.Reference.ToJson()`: the target, the acknowledged
   run ID and the native binding. It never contains credentials or history. An existing
   file is refused before anything is sent, unless you give `--overwrite`. If the write
   fails after the acknowledgement, the command stops with an `output` error (exit 1).
   The error includes the acknowledged run and the attempt, and the command does not wait.
6. With `--detach`, the command ends after the acknowledgement. Otherwise
   `Run.WaitAsync` waits for the terminal result. `--timeout` limits only this wait and
   starts after the acknowledgement. The default is indefinite.

If the target does not acknowledge the request, the error record includes the attempt:
its outcome, its correlation ID and the proposed run ID. It never includes an
acknowledged run.

## Known runs

```sh
zeroshot-dotnet status RUN
zeroshot-dotnet wait RUN [--timeout WAIT]
zeroshot-dotnet force-stop RUN [--wait-timeout WAIT | --request-only]
```

`RUN` is an exact run ID with `--config FILE`, or `--run-file FILE` (for example, a file
written by `--save-run`). A run file supplies the target and the binding. You can also
give `--config` with a run file, but the configuration must name the same target.

- `status` reads the complete native status. A failed run is status data, so the command
  exits with 0.
- `wait` uses `Run.WaitAsync`.
- `force-stop` uses `Run.ForceStopAsync`. It sends one force request, then waits for the
  terminal result. `--wait-timeout` limits only that wait.
- `force-stop --request-only` uses `Run.ForceAttemptAsync`. It sends one force request and
  reports the acknowledgement, which can still be `stopping`.

## Ctrl+C

Ctrl+C cancels the current operation. It never stops a run, and it never sends a request.

- Before a request is sent, or while the command waits after an acknowledgement, the
  command exits with 130. Anything that was already acknowledged stays in the output.
- After a request was sent but before it was acknowledged, the outcome is unknown. The
  command exits with 5, and the error record includes the attempt with `"cancelled": true`.

Press Ctrl+C a second time to end the process immediately.

## Files

- Commands read and write only the paths given in the invocation.
- An existing output file is refused (`output-exists`, exit 2) unless the invocation
  includes `--overwrite`.
- If writing a new file fails, the partial file is removed (`output`, exit 1).
- Writes are plain file writes, with no atomic replacement and no fsync.

## Target configuration

`--config FILE` holds the target, the native binding the caller asserts, transport and
observation settings, and the names of the environment variables that hold credentials.
Secret values are never stored in this file. Unknown or duplicate fields are refused.

```json
{
  "schema": "zeroshot-dotnet/target-config/v1",
  "target": "https://target.example/",
  "nativeBinding": { "provenance": "caller-supplied", "release": "10.9.0", "sourceRevision": "75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa" },
  "credentials": {
    "targetBearerEnvironment": "ZEROSHOT_TARGET_TOKEN",
    "githubTokenEnvironment": "ZEROSHOT_GITHUB_TOKEN",
    "connections": { "openai": { "OPENAI_API_KEY": "ZEROSHOT_OPENAI_KEY" } },
    "connectionResolver": { "endpoint": "https://resolver.example/", "keys": ["openai"], "sourceConnection": "github", "bearerEnvironment": "ZEROSHOT_RESOLVER_TOKEN" }
  },
  "transport": { "requestTimeout": "30s", "maxBufferedBytesTotal": 33554432 },
  "observation": { "recovery": "established-interruptions", "recoveryDelay": "250ms", "subscriptionOpenTimeout": "30s" }
}
```

Only `schema` and `target` are required.

- **Transport durations:** `connectTimeout`, `requestTimeout` and `cleanupTimeout`.
- **Transport integer limits:** `maxResponseBytes`, `maxMessageBytes`, `maxRequestBytes`,
  `maxBufferedRecordsPerStream`, `maxBufferedBytesPerStream`, `maxBufferedBytesTotal`,
  `maxConcurrentSubscriptions`, `maxConcurrentRequests`, `maxOecpConnections`,
  `maxHttpConnectionsPerOrigin` and `maxErrorBodyBytes`.

The SDK's `TransportOptions` and `ObservationOptions` set the defaults and value rules:
timeouts must be positive, limits must be positive, and all limits must be consistent
with each other.

Durations are strings made of a whole number and a unit: `ms`, `s`, `m` or `h`. A unit is
always required. `infinite` is valid only for a wait budget (`--timeout` or
`--wait-timeout`), and indefinite is the default for those options. `--request-timeout`
overrides `transport.requestTimeout` for one invocation.

A command reads only the credentials that its own operation uses. The target bearer is
read by every target command. The GitHub token, connection values and resolver bearer
are read only by `run`. A missing variable, or a target bearer that is not valid, is a
`credentials` error (exit 2). The error names the variable but not its value.

## Output

Readable text is the default. With `--json`, each result is one `zeroshot-dotnet/cli/v1`
record per line on stdout:

```json
{"schema":"zeroshot-dotnet/cli/v1","kind":"prepared","proposedRunId":"019f6ba6-7c00-7000-8000-000000000001","path":"prepared.json"}
```

The record kinds are:

| Kind | Command | Fields |
| --- | --- | --- |
| `prepared` | `prepare` | `proposedRunId`, `path` |
| `submission` | `run` | `runId` (acknowledged), `target`, `attempt` |
| `status` | `status` | `runId`, `status` (the complete native `RunStatusResult`), and `result` when the run has finished |
| `result` | `run`, `wait`, `force-stop` | `runId`, `result` |
| `force` | `force-stop --request-only` | `runId`, `attempt`, `status` (the native `RunForceResult`) |
| `error` | any command, on stderr | `category`, `operation`, `message`, and `runId`, `attempt`, `evidence` when known |

`run` writes a `submission` record, then a `result` record (unless `--detach` is given).

A `result` object holds:

- `succeeded`: true or false.
- `output`: the generic JSON output of a successful run. It can be `null`.
- `failureReason`: the native reason of a failed run.
- `metadata`: the native `RunMetadata`.
- `evidence`: `kind` (`status-report` or `retained-terminal-event`) and `cursor`. This
  tells you where the terminal result was observed.

An `attempt` object describes one mutation attempt, as the SDK classified it:

- `operation`: `target.submit` or `run/force`.
- `outcome`: `acknowledged`, `rejected`, `not-sent` or `unknown`.
- `correlationId`.
- `cancelled`: present and true when Ctrl+C ended the attempt.
- `proposedRunId`, `acknowledgedRunId` and `runIdsMatch`: submissions only.

```json
{"schema":"zeroshot-dotnet/cli/v1","kind":"submission","runId":"0195af77-1000-7000-8000-000000000002","target":"https://target.example/","attempt":{"operation":"target.submit","outcome":"acknowledged","correlationId":"6f0c2d1e-8a44-4d2b-9f8e-2b8f1f0f7a10","proposedRunId":"0195af77-1000-7000-8000-000000000001","acknowledgedRunId":"0195af77-1000-7000-8000-000000000002","runIdsMatch":false}}
{"schema":"zeroshot-dotnet/cli/v1","kind":"result","runId":"0195af77-1000-7000-8000-000000000002","result":{"succeeded":true,"output":{"answer":42},"metadata":{},"evidence":{"kind":"retained-terminal-event","cursor":"c3"}}}
```

Errors are written to stderr. In JSON mode, an error is one record:

```json
{"schema":"zeroshot-dotnet/cli/v1","kind":"error","category":"input","operation":"prepare","message":"The request file 'request.json' is not a valid run request."}
```

An error keeps what was already known. `runId` is the acknowledged, reopened or forced
run. It is never a run ID that was only proposed. `attempt` is the attempt that failed,
or the acknowledged attempt that came before the failure. `evidence` holds the latest
positions that a wait reached: `statusCursor`, `lastEventCursor` and `resumeAfter`.

The CLI writes every error message itself. A message can name a file, field, environment
variable or run ID. It never includes request content, credential values, native status
content or the text of an underlying exception.

## Exit codes

| Exit | Meaning | Error categories |
| --- | --- | --- |
| 0 | Success, including a `status` that reports a failed run | |
| 1 | Operational, transport, protocol or file failure, an incomplete observation, or a native rejection | `operational`, `rejected`, `output`, `internal` |
| 2 | Invalid invocation, configuration or input, or a missing or mismatched native binding | `invocation`, `configuration`, `input`, `credentials`, `output-exists`, `binding`, `unavailable` |
| 3 | `run`, `wait` or `force-stop` observed a failed run (the `result` record is still written) | |
| 4 | The wait timeout expired; the run was not stopped | `timeout` |
| 5 | A request was sent, and it is unknown whether it took effect | `unknown-outcome` |
| 130 | Ctrl+C before a request was sent, or while waiting after an acknowledgement | `cancelled` |
