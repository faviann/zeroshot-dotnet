# Zeroshot .NET SDK

`Zeroshot.Client` is a .NET 10 library for native Zeroshot **10.10.0** at source
`3ee1192cec359a0b997f464e703a936e8b67d63c`. It currently provides typed execution
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
   that `zeroshot-dotnet --version` names that version and commit. On a `v*` tag run, the
   tag must name that version. Package metadata, license, dependencies, files, the CLI
   grammar, the `--json` record kinds and fields, and the versioned file schemas, all read
   from the candidate, must equal [`contract.txt`](tools/qualification/contract.txt). The
   record fields come from the CLI's declared catalog; on every platform, each CLI suite run
   must emit every declared kind and field and no undeclared one. The step prints the
   compatibility baseline it chose. The public API analyzer holds the library to
   `src/Zeroshot.Sdk/PublicAPI.*.txt`.
2. **platform** runs natively on Windows Server 2025 x64 (`windows-2025`), Windows 11 arm64
   (`windows-11-vs2026-arm`), Ubuntu 24.04 x64 and arm64 (`ubuntu-24.04`,
   `ubuntu-24.04-arm`) and macOS 15 x64 and arm64 (`macos-15-intel`, `macos-15`). It
   verifies the package hashes, then restores the candidate into fresh copies of both test
   suites and the controlled-peer consumers through a local feed, package source mapping
   and an empty package cache. These copies compile only against the package. It runs the
   SDK suite, the consumers and the CLI suite four times: against the framework-dependent
   CLI built from the checkout, and against the candidate tool installed as an isolated
   global tool, in a local tool manifest and at an explicit tool path. Suites and consumers
   run with a `PATH` that holds only .NET, behind `python`, `python3`, `py` and `zeroshot`
   commands that fail and leave a marker. The leg fails if any marker exists afterwards or
   the rest of the `PATH` holds such a command. `evidence.json` records the OS, the process
   and OS architectures, the .NET runtime and SDK versions, and each suite's skipped tests.
3. **native-witness** runs [`run.sh`](tools/native-witness/README.md) on Linux x64 with
   `ZEROSHOT_WITNESS_CANDIDATE`: its consumers restore the candidate library and `cli.sh`
   installs the candidate tool, so nothing is packed again. The artifact keeps the native
   release, source and executable identities, test-asset hashes, manifests and results.
4. **qualification** refuses the candidate unless it was packed from a clean checkout and
   all six platforms and the witness produced passing evidence for the same package
   hashes. A missing, duplicated or failed platform refuses it, and so does a skipped test
   other than named-pipe tests off Windows and Ctrl+C tests on Windows. The job writes
   `qualification.json`, which combines the evidence.

The compatibility baseline is the latest `v*` release tag that precedes the candidate:
every public API line and CLI line (grammar, output fields, schemas and exit codes)
recorded at that tag must remain. Only the next minor version can remove one, and it needs
migration notes at `docs/migration/MAJOR.MINOR.md`. When no release precedes the
candidate, the baseline is the accepted usage prototype: every public symbol, command,
option, exit code and file schema in
[`prototype-contract.txt`](tools/qualification/prototype-contract.txt) must exist. Its
output entries are the decision's normative `cli/v1` envelope and record kinds; the
prototype pages' field examples were refined in implementation, so this first release's
`contract.txt` fixes the field-level shapes for later releases. The release commit moves
the `PublicAPI.Unshipped.txt` entries to `PublicAPI.Shipped.txt`.

```sh
dotnet run --project tools/qualification -c Release -- pack --out /tmp/candidate
dotnet run --project tools/qualification -c Release -- platform --candidate /tmp/candidate --runner ubuntu-24.04 --out /tmp/evidence
ZEROSHOT_WITNESS_CANDIDATE=/tmp/candidate tools/native-witness/run.sh
```

A local `platform` run reports its own OS; the `--runner` label only names the platform it
must match.

### Publication

Only `Zeroshot.Client` is published, to the GitHub Packages feed
`https://nuget.pkg.github.com/faviann/index.json`. The `Zeroshot.Cli` package is never
pushed anywhere. To release version `X`, set `<Version>` in `src/Directory.Build.props`,
merge to `main`, and push the tag `vX` on that commit. The tag run qualifies the commit as
above, then:

5. **publish** runs only on a `v*` tag, and only after **qualification** succeeded. It is
   the only job with `packages: write`. Its `release` step refuses unless
   `qualification.json` qualified exactly the downloaded candidate, the candidate's release
   tag is the pushed tag, and the library package hashes to the value the pack job
   reported. It then pushes that one `.nupkg` with the workflow token. A re-run skips a
   version that the feed already holds; the next job still checks the bytes that the feed
   serves.
6. **verify-publication** has `packages: read` only. It reads the package record from the
   GitHub REST API: the package must be `public` and linked to
   `faviann/zeroshot-dotnet-sdk`. It then creates a fresh consumer outside the checkout.
   The consumer's `nuget.config` maps `Zeroshot.*` to the GitHub feed and everything else
   to nuget.org, its package and HTTP caches start empty, it pins `[X]` exactly, and the
   feed credential is in the `NuGetPackageSourceCredentials_github` environment variable.
   The job restores, builds and runs the [contracts consumer](examples/ContractsConsumer).
   The restored `.nupkg` must come from the feed and hash to the qualified candidate's
   value. The workflow token can also restore a private package linked to this repository,
   so the visibility check, not the restore, shows that other consumers can read it. The
   job uploads `publication.json` as the `publication` artifact of the same run, beside the
   `candidate` and `qualification` artifacts. The release is complete only when this job
   passes.

A new GitHub package is private. If `verify-publication` reports a private package, the
package owner opens the package settings on GitHub, changes the visibility to public, and
re-runs the failed job. Nothing is pushed again.

SDK versions are independent of native Zeroshot versions. `0.2.0-preview.1` supports
native Zeroshot 10.10.0 at source `3ee1192cec359a0b997f464e703a936e8b67d63c` only.
`0.1.0-preview.1` supports native 10.9.0 at source
`75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa` only. Move your native pin and target image
together with the SDK; [the 0.2 migration notes](docs/migration/0.2.md) list every change.

## Using the published package

GitHub's NuGet feed requires authentication, even for public packages. Create a personal
access token (classic) with only the `read:packages` scope, and keep it out of committed
files. Add this `nuget.config` beside your solution:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="github" value="https://nuget.pkg.github.com/faviann/index.json" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="github"><package pattern="Zeroshot.*" /></packageSource>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
```

Supply the credential for the `github` source through the environment, for example in CI:

```sh
export NuGetPackageSourceCredentials_github="Username=YOUR_GITHUB_USER;Password=YOUR_TOKEN"
```

Or store it in your user-level NuGet configuration, never in the repository's
(`~/.nuget/NuGet/NuGet.Config`; on Windows `%APPDATA%\NuGet\NuGet.Config`, where you can
omit `--store-password-in-clear-text` to encrypt it):

```sh
dotnet nuget add source https://nuget.pkg.github.com/faviann/index.json --name github \
  --username YOUR_GITHUB_USER --password YOUR_TOKEN --store-password-in-clear-text \
  --configfile ~/.nuget/NuGet/NuGet.Config
```

Pin the exact preview:

```xml
<PackageReference Include="Zeroshot.Client" Version="[0.2.0-preview.1]" />
```

The CLI is not published. Build it from the tag that matches the SDK version, as the
[CLI guide](docs/cli/README.md#build-and-install) describes.

The repository is licensed under the [MIT License](LICENSE). Pinned native
schemas include their [upstream MIT notice](src/Zeroshot.Sdk/Schemas/NATIVE-LICENSE).
