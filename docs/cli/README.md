# zeroshot-dotnet

`zeroshot-dotnet` is a thin command over `ZeroshotClient`. The SDK parses requests, run
references and checkpoints, prepares submissions and validates values. The CLI parses
arguments and the target configuration, reads and writes the files you name, and
formats output. Run `zeroshot-dotnet --help` for the complete grammar.

The SDK does all waiting, stream recovery and outcome classification. The CLI never
retries or replays a mutation, and it never sends a stop that you did not request.

## Build and install

The CLI is framework-dependent: running it needs a .NET 10 runtime, and building or
installing it needs the .NET 10 SDK. It is never published to a package feed or as a
release binary. Build it from the release tag of the matching `Zeroshot.Client` version,
so that the CLI and the SDK have the same version and source commit:

```sh
git clone https://github.com/faviann/zeroshot-dotnet-sdk.git
cd zeroshot-dotnet-sdk
git checkout v0.1.0-preview.1
dotnet build src/Zeroshot.Cli --configuration Release
dotnet run --project src/Zeroshot.Cli --configuration Release --no-build -- --version
```

To use it as a .NET tool, pack it and install it from that local folder:

```sh
dotnet pack src/Zeroshot.Cli --configuration Release --output ./packages
dotnet tool install --global Zeroshot.Cli --version 0.1.0-preview.1 --source ./packages
dotnet tool install --local Zeroshot.Cli --version 0.1.0-preview.1 --source ./packages   # needs a tool manifest
dotnet tool install Zeroshot.Cli --version 0.1.0-preview.1 --source ./packages --tool-path ./tools
```

Run a local tool as `dotnet tool run zeroshot-dotnet -- ARGUMENTS`. Without `--`,
`dotnet tool run` handles `--help` itself. `zeroshot-dotnet --version` prints the command's
and its `Zeroshot.Client` library's versions, each followed by `+` and the source commit.

These are SDK versions, independent of native Zeroshot versions. Version
`0.1.0-preview.1` supports native Zeroshot 10.9.0 at source
`75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa` only.

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

## Watch, logs and attach

```sh
zeroshot-dotnet watch RUN [--after CURSOR | --checkpoint FILE] [--recovery MODE]
zeroshot-dotnet logs RUN [--execution EXECUTION] [--after CURSOR | --checkpoint FILE] [--recovery MODE]
zeroshot-dotnet attach RUN EXECUTION
```

Each command writes one record for each native record, as the SDK delivers it. The
command ends when native closes the stream normally, with exit 0. A normal close ends
the stream, not the run: the command writes no result for it, and the exit code says
nothing about the run's outcome. Use `wait` for the run's result.

- `watch` uses `Run.WatchAsync`. It replays the run's retained status history, then
  follows it live.
- `logs` uses `Run.LogsAsync`. It replays the retained logs, then follows them live.
  With `--execution`, it includes only the records of that exact execution, and it
  leaves out run-wide records.
- `--after CURSOR` starts the replay after that record; the record itself is not
  repeated. `--checkpoint FILE` does the same with a checkpoint file: the `checkpoint`
  object of a `watch` or `log` record, saved as it is. The SDK refuses a checkpoint
  from another target, run, stream or execution filter (`input`, exit 2) before
  anything is sent. Without either option, the replay starts at the beginning.
- Cursors are opaque native values. A cursor that begins with `--` must be given as
  `--after=CURSOR`. Any value option accepts the `--option=VALUE` form.
- If an established stream is interrupted by a disconnection, an unexpected end or a
  remote slow-consumer close, the SDK opens it again after the last record that was
  written. `--recovery none`, or `observation.recovery` in the configuration, turns
  this off, and the interruption then fails the command. A failure to open the stream,
  malformed data, a local size or queue limit, or retained history that native reports
  as unavailable always fails the command.
- `attach` uses `Run.AttachAsync`. It shows the live output of one active execution.
  It has no cursor, replays nothing, never opens the attachment again and cannot send
  input.

The CLI never saves a cursor or checkpoint for you. To resume, pass the cursor of the
last record you processed, or its checkpoint, to a new command.

When a stream fails, stdout keeps every record that was already written, and the error
record on stderr includes an `observation` object:

- `failure`: how the stream failed, such as `establishment`, `interrupted`,
  `source-unavailable`, `protocol` or `resource-limit` for `watch` and `logs`, or
  `unexpected-disconnect` for `attach`.
- `recoveries`: how many times the SDK opened the stream again (`watch` and `logs`).
- `lastDeliveredCursor`: the cursor of the last record written to stdout. It is absent
  when no record was written, or for `attach`.

```json
{"schema":"zeroshot-dotnet/cli/v1","kind":"error","category":"operational","operation":"watch","message":"The watch of run 0195af77-1000-7000-8000-000000000002 failed: it was interrupted and recovery is disabled.","runId":"0195af77-1000-7000-8000-000000000002","observation":{"failure":"interrupted","recoveries":0,"lastDeliveredCursor":"v2:7"}}
```

If stdout is a pipe and its reader closes it, such as `| head`, the command stops the
observation and exits with an `output` error (exit 1). The last delivered cursor is the
last record written before the pipe closed, which the reader might not have processed.

## Ctrl+C

Ctrl+C cancels the current operation. It never stops a run, and it never sends a request.

- Before a request is sent, during a read such as `status`, during `watch`, `logs` or
  `attach`, or while the command waits after an acknowledgement, the command exits with
  130. Anything that was already acknowledged or written stays in the output, and a
  stream's error record includes its last delivered cursor.
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
| `watch` | `watch`, one per record | `runId`, `cursor`, `checkpoint`, `data` (the complete native `RunWatchEventNotification`) |
| `log` | `logs`, one per record | `runId`, `cursor`, `execution` (null for a run-wide record), `timestamp` (native Unix milliseconds), `checkpoint`, `data` (the complete native `RunLogEventNotification`) |
| `attachment` | `attach`, one per event | `runId`, `execution`, `data` (the complete native `RunAttachEventNotification`) |
| `error` | any command, on stderr | `category`, `operation`, `message`, and `runId`, `attempt`, `evidence`, `observation` when known |

`run` writes a `submission` record, then a `result` record (unless `--detach` is given).

A `checkpoint` object is the SDK's `zeroshot-dotnet/history-checkpoint/v1` export: the
target, run, stream (`watch` or `logs`), execution filter (null when unfiltered) and
cursor. An `attachment` record has no cursor or checkpoint. A `watch` record that
reports a finished run is native history, not a `result`.

```json
{"schema":"zeroshot-dotnet/cli/v1","kind":"log","runId":"0195af77-1000-7000-8000-000000000002","cursor":"v2:4","execution":"nv2-5953…","timestamp":1767225600000,"checkpoint":{"schema":"zeroshot-dotnet/history-checkpoint/v1","target":"https://target.example/","runId":"0195af77-1000-7000-8000-000000000002","stream":"logs","execution":"nv2-5953…","cursor":"v2:4"},"data":{"subscriptionId":"…","runId":"0195af77-1000-7000-8000-000000000002","cursor":"v2:4","timestamp":1767225600000,"execution":"nv2-5953…","record":{"level":"info","target":"worker","message":"…"}}}
```

In readable mode, each record is one line:

- `watch`: the cursor, then the run's phase or its terminal result, such as
  `v2:3 Run 0195af77-… is running`.
- `logs`: the cursor, the UTC timestamp, the level, the log target, the execution in
  brackets when present, and the message.
- `attach`: the output text as it is, and `Execution … is working` or
  `Execution … settled` for the other events.

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
`native` holds the SDK's typed facts about a native HTTP or OECP failure:

- `transport`: `http` or `oecp`.
- `kind`: the failure kind, such as `httpStatus`, `transport` or `rpcError`.
- `httpStatus` and `problemCode`: from a native HTTP problem response.
- `rpcCode` and `domainCode`: from a native JSON-RPC error.

```json
{"schema":"zeroshot-dotnet/cli/v1","kind":"error","category":"rejected","operation":"run","message":"The submission of proposed run 0195af77-1000-7000-8000-000000000001 was rejected by the target.","attempt":{"operation":"target.submit","outcome":"rejected","correlationId":"6f0c2d1e-8a44-4d2b-9f8e-2b8f1f0f7a10","proposedRunId":"0195af77-1000-7000-8000-000000000001"},"native":{"transport":"http","kind":"httpStatus","httpStatus":409,"problemCode":"request.conflict"}}
```

In text mode, the same facts follow the message in brackets.

The CLI writes every error message itself. A message can name a file, field, environment
variable or run ID. It never includes request content, credential values, native status
content, remote error messages or the text of an underlying exception.

## Exit codes

| Exit | Meaning | Error categories |
| --- | --- | --- |
| 0 | Success, including a `status` that reports a failed run, and a normal close of `watch`, `logs` or `attach` | |
| 1 | Operational, transport, protocol or file failure, an incomplete or failed observation, a closed stdout, or a native rejection | `operational`, `rejected`, `output`, `internal` |
| 2 | Invalid invocation, configuration or input, or a missing or mismatched native binding | `invocation`, `configuration`, `input`, `credentials`, `output-exists`, `binding` |
| 3 | `run`, `wait` or `force-stop` observed a failed run (the `result` record is still written) | |
| 4 | The wait timeout expired; the run was not stopped | `timeout` |
| 5 | A request was sent, and it is unknown whether it took effect | `unknown-outcome` |
| 130 | Ctrl+C before a request was sent, during a read such as `status` or a stream, or while waiting after an acknowledgement | `cancelled` |
