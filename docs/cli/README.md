# zeroshot-dotnet

`zeroshot-dotnet` is a thin command over `ZeroshotClient`. The SDK parses requests, run
references and checkpoints, prepares submissions and validates values. The CLI parses
arguments and the target configuration, reads and writes the files you name, and
formats output. Run `zeroshot-dotnet --help` for the complete grammar.

In this build only `prepare` runs. The other commands check their arguments,
configuration, run file and the credentials that operation needs. They then stop
with an `unavailable` error (exit 2), before any network I/O.

## Prepare a retained request

```sh
zeroshot-dotnet prepare --request request.json --out prepared.json [--overwrite] [--json]
```

`RunRequest.ParseUtf8(bytes).Prepare()` fixes the run ID and submission key. Omitted
identity is generated, and supplied identity is kept. The file receives exactly
`PreparedSubmission.ExportUtf8()`. Preparation needs no configuration, target,
credentials or network. The receipt names the file and the proposed run ID. It never
contains request content.

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

Errors are written to stderr. In JSON mode, an error is one record:

```json
{"schema":"zeroshot-dotnet/cli/v1","kind":"error","category":"input","operation":"prepare","message":"The request file 'request.json' is not a valid run request."}
```

The CLI writes every error message itself. A message can name a file, field or
environment variable. It never includes request content, credential values or the text
of an underlying exception.

The error categories are:

- `invocation`, `configuration`, `input`, `credentials` and `output-exists` exit with 2.
- `unavailable` exits with 2.
- `output` and `internal` exit with 1.

The complete exit code table is in `--help`.
