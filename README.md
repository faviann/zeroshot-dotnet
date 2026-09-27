# Zeroshot .NET SDK

`Zeroshot.Client` is a .NET 10 library for native Zeroshot **10.9.0** at source
`75ae54b6693b6ae4cedeedd37a79ce3919d9a8fa`. It currently provides typed execution
definitions, local wire validation, immutable credential-free prepared submissions,
[bounded HTTP discovery, session acquisition and direct submission attempts](docs/http/README.md), and
[WebSocket OECP inspection, bounded watch/log/attachment subscriptions and native force attempts](docs/oecp/README.md) of existing targets. An
[internal operation and observation seam](docs/execution/README.md) supplies deadlines,
resource admission, shared bounded observation queues and safe failures. Remaining transport bindings and SDK run workflows
are tracked in the [coverage manifest](docs/contracts/coverage.json).

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
| `src/Zeroshot.Cli` | CLI scaffold; currently provides help only. |
| `tests/Zeroshot.Sdk.Tests` | Golden contracts, native value boundaries and retained-request ownership. |
| `tests/Zeroshot.Cli.Tests` | CLI bootstrap smoke tests. |
| `examples/ContractsConsumer` | External consumer referencing the packed library. |
| `examples/DiscoveryConsumer` | Packed-library discovery/session and native OECP inspection witness. |
| `examples/SubmissionConsumer` | Packed-library complete-asset admission, normalization and replay witness. |
| `examples/ObservationConsumer` | Packed-library watch/log replay, live records and target-restart witness. |
| `examples/AttachmentConsumer` | Packed-library exact active execution, live output, settlement and refusal witness. |
| `examples/ForceConsumer` | Packed-library active-run force acknowledgement, terminal history and refusal witness. |
| `tools/native-witness/run.sh` | Stock-native Linux x64 HTTP/OECP inspection, submission and observation witness. |

Install a .NET 10 SDK, then run:

```sh
dotnet build Zeroshot.sln --configuration Release
dotnet test --project tests/Zeroshot.Cli.Tests/Zeroshot.Cli.Tests.csproj --configuration Release --no-restore
dotnet test --project tests/Zeroshot.Sdk.Tests/Zeroshot.Sdk.Tests.csproj --configuration Release --no-restore
```

`global.json` selects the Microsoft Testing Platform runner used by TUnit. Tests
need no running Zeroshot service. Package-consumer instructions are in its
[README](examples/ContractsConsumer/README.md).

The repository is licensed under the [MIT License](LICENSE). Pinned native
schemas include their [upstream MIT notice](src/Zeroshot.Sdk/Schemas/NATIVE-LICENSE).
