# Zeroshot .NET SDK

`Zeroshot.Client` is a .NET 10 library for native Zeroshot **10.9.0** at source
`75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa`. It currently provides typed execution
definitions, local wire validation, immutable credential-free prepared submissions,
[bounded HTTP discovery, session acquisition, direct submission attempts, private target bootstrap and private operator diagnostics/history exports](docs/http/README.md), and
[WebSocket OECP inspection, bounded watch/log/attachment subscriptions, native force attempts, checkpoints and workspace recovery, shared cluster bindings and trusted run submit](docs/oecp/README.md) of existing targets,
[the same OECP operations over borrowed NDJSON streams and existing Unix controller sockets](docs/oecp/README.md#ndjson-streams-and-unix-controllers)
and [security-validated Windows controller pipes](docs/oecp/README.md#windows-controller-pipes),
[browser dashboard assets, bootstrap, draft transformations, revision-checked profiles, run history and SSE run events](docs/dashboard/README.md), and
[hosted run list, status, NDJSON watch/logs and force](docs/http/README.md#hosted-run-lifecycle). An
[internal operation and observation seam](docs/execution/README.md) supplies deadlines,
resource admission, shared bounded observation queues and safe failures. The
[SDK run handle](docs/sdk/README.md) prepares and submits requests, returning an acknowledged
handle or explicit attempt evidence, runs them to a terminal result within an optional
wait budget, and reopens known runs for status, waiting, one-attempt force-stop with an
optional wait, checkpointed watch/logs with delivered-cursor recovery, and live attachment
under a caller-supplied native binding. Remaining SDK run workflows follow;
native bindings are tracked in the [coverage manifest](docs/contracts/coverage.json).

Public namespaces are `Zeroshot`, `Zeroshot.Native` and
`Zeroshot.Native.Contracts`. The scaffold project path remains `src/Zeroshot.Sdk`;
its package and assembly are `Zeroshot.Client`.

Read the [contract guide](docs/contracts/README.md) and the
[external package consumer](examples/ContractsConsumer) for preparation and exact
UTF-8 import/export. Running these APIs requires no network, Python or native
executable. Authored inputs, instructions and scripts may contain sensitive data;
explicit serialization/export remains the caller's responsibility.

| Project | Purpose |
| --- | --- |
| `src/Zeroshot.Sdk` | `Zeroshot.Client` execution-contract library. |
| `src/Zeroshot.Cli` | `zeroshot-dotnet` command (local `Zeroshot.Cli` tool package); see the [CLI guide](docs/cli/README.md). |
| `tests/Zeroshot.Sdk.Tests` | Golden contracts, native value boundaries and retained-request ownership. |
| `tests/Zeroshot.Cli.Tests` | Process-level CLI fixtures against the built command. |
| `examples/ContractsConsumer` | External consumer referencing the packed library. |
| `examples/RunHandleConsumer` | Packed-library SDK ordinary and retained explicit submission, run-to-completion and zero waits, force-stop, reconnection, binding refusals, results, checkpointed watch/logs and attachment cancellation against controlled peers. |
| `examples/DiscoveryConsumer` | Packed-library discovery/session, native OECP inspection and stock cluster/run-submit refusal witness. |
| `examples/SubmissionConsumer` | Packed-library complete-asset admission, normalization and replay witness. |
| `examples/ObservationConsumer` | Packed-library watch/log replay (lower client and SDK checkpoints), live records, live dashboard SSE and target-restart witness. |
| `examples/AttachmentConsumer` | Packed-library exact active execution, live output, settlement and refusal witness. |
| `examples/ForceConsumer` | Packed-library active-run force acknowledgement, terminal history, refusal and SDK composed force-stop witness. |
| `examples/RecoveryConsumer` | Packed-library checkpoint paging, successor resume, workspace discard and refusal witness. |
| `examples/ControllerConsumer` | Packed-library Unix controller socket or Windows controller pipe, and borrowed NDJSON stream witness. |
| `examples/DashboardConsumer` | Packed-library stock UI mount static routes, bootstrap, draft transformations, profile create/update/conflict/race, run history/SSE and browser refusals witness. |
| `examples/PrivateBootstrapConsumer` | Packed-library private-mode target bootstrap (invalid, accepted, closed) and operator diagnostics/history export witness. |
| `tools/native-witness/run.sh` | Stock-native Linux x64 HTTP/OECP inspection, submission and observation witness. |
| `tools/native-witness/windows-controller.ps1` | Stock-native Windows x64 controller-pipe witness. |
| `tools/qualification` | Release-candidate qualification: pack once, test the exact packages on each platform, gate. |

Install a .NET 10 SDK, then run:

```sh
dotnet build Zeroshot.sln --configuration Release
dotnet test --project tests/Zeroshot.Cli.Tests/Zeroshot.Cli.Tests.csproj --configuration Release --no-restore
dotnet test --project tests/Zeroshot.Sdk.Tests/Zeroshot.Sdk.Tests.csproj --configuration Release --no-restore
```

`global.json` selects the Microsoft Testing Platform runner used by TUnit. Tests
need no running Zeroshot service. Package-consumer instructions are in its
[README](examples/ContractsConsumer/README.md).

## Release qualification

The [qualification workflow](.github/workflows/qualification.yml) qualifies one release
candidate. It runs on pull requests that change code, tests, examples or tools, on `v*`
tags and on demand. `tools/qualification` does the work, and each step also runs locally.

1. **candidate** builds once and packs `Zeroshot.Client` and the `Zeroshot.Cli` tool
   package from that build. `candidate.json` records the version, both package SHA-256
   values, the source commit and the release tag, which is `null` for a pull request. The
   step checks that both packages have one version and name the source commit and the
   repository, that the tool bundles the library package's exact `Zeroshot.Client.dll`, and
   that `zeroshot-dotnet --version` names that version and commit. Package metadata,
   license, dependencies, files and the CLI grammar, read from the candidate, must equal
   [`contract.txt`](tools/qualification/contract.txt). The public API analyzer holds the
   library to `src/Zeroshot.Sdk/PublicAPI.*.txt`.
2. **platform** runs natively on Windows Server 2025 x64 (`windows-2025`), Windows 11 arm64
   (`windows-11-vs2026-arm`), Ubuntu 24.04 x64 and arm64 (`ubuntu-24.04`,
   `ubuntu-24.04-arm`) and macOS 15 x64 and arm64 (`macos-15-intel`, `macos-15`). It
   verifies the package hashes, then restores the candidate into fresh copies of both test
   suites and the controlled-peer consumers through a local feed, package source mapping
   and an empty package cache. These copies compile only against the package. It runs the
   SDK suite, the consumers and the CLI suite four times: against the framework-dependent
   CLI built from the checkout, and against the candidate tool installed as an isolated
   global tool, in a local tool manifest and at an explicit tool path. Suites and consumers
   run with a `PATH` that holds only .NET; the step first checks that no `python`,
   `python3`, `py` or `zeroshot` is on it. `evidence.json` records the OS, the process and
   OS architectures, and the .NET runtime and SDK versions that the job observed.
3. **native-witness** runs [`run.sh`](tools/native-witness/README.md) on Linux x64 with
   `ZEROSHOT_WITNESS_CANDIDATE`: its consumers restore the candidate library and `cli.sh`
   installs the candidate tool, so nothing is packed again. The artifact keeps the native
   release, source and executable identities, test-asset hashes, manifests and results.
4. **qualification** refuses the candidate unless it was packed from a clean checkout and
   all six platforms and the witness produced passing evidence for the same package
   hashes. A missing, duplicated or failed platform refuses it. The job writes
   `qualification.json`, which combines the evidence.

[`baseline.json`](tools/qualification/baseline.json) names the compatibility baseline. For
the first preview it is the accepted usage prototype: every public symbol, command,
option, record schema and exit code in
[`prototype-contract.txt`](tools/qualification/prototype-contract.txt) must exist. After
a release is published, set the baseline to
`{"kind":"published","version":"VERSION","tag":"vVERSION"}` and move the
`PublicAPI.Unshipped.txt` entries to `PublicAPI.Shipped.txt`. Every public API line and CLI
line recorded at that tag must then remain. Only the next minor version can remove one,
and it needs migration notes at `docs/migration/MAJOR.MINOR.md`.

```sh
dotnet run --project tools/qualification -c Release -- pack --out /tmp/candidate
dotnet run --project tools/qualification -c Release -- platform --candidate /tmp/candidate --runner ubuntu-24.04 --out /tmp/evidence
ZEROSHOT_WITNESS_CANDIDATE=/tmp/candidate tools/native-witness/run.sh
```

A local `platform` run reports its own OS; the `--runner` label only names the platform it
must match.

The repository is licensed under the [MIT License](LICENSE). Pinned native
schemas include their [upstream MIT notice](src/Zeroshot.Sdk/Schemas/NATIVE-LICENSE).
