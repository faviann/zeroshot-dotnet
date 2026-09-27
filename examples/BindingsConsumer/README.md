# Binding coverage consumer

Run from the repository root:

```sh
dotnet pack src/Zeroshot.Sdk/Zeroshot.Sdk.csproj -c Release -o artifacts/packages
dotnet restore examples/BindingsConsumer/BindingsConsumer.csproj --source artifacts/packages --source https://api.nuget.org/v3/index.json
dotnet run --project examples/BindingsConsumer/BindingsConsumer.csproj -c Release --no-restore -- docs/contracts/coverage.json
```

The project references the packed `Zeroshot.Client` package, not the library project, so it can
use only the public API. It calls every implemented `coverage.json` binding once, as a lower-client
call with no SDK binding assertion, waiting or recovery. The calls go to controlled peers:

- an in-process HTTP handler for fixed, private, history, dashboard, hosted, OAuth and resolver routes;
- a Unix-socket NDJSON peer for the borrowed-stream and Unix controller bindings;
- a loopback WebSocket peer for the upgrade route and `$/cancelRequest`.

The peers answer with the SDK tests' fixtures, linked from `tests/Zeroshot.Sdk.Tests/Fixtures`,
plus the minimal inline source-backed bodies those tests use. The consumer then checks the manifest:

- IDs are unique and every row has an owner issue.
- Every row's status agrees with its `evidenceClass`.
- The set of invoked bindings equals the set of `implemented` rows.

`binding:windows-controller` is always exempt from that comparison: the Windows pipe tests and
the `windows-native-witness` workflow exercise it.

Checks the consumer makes on each result:

- The call succeeded.
- The result decoded to the expected shape.

Refusal, fault and bound behaviour are left to the unit tests. A pass shows that the package
reaches every named binding. It is not live-native conformance evidence.
