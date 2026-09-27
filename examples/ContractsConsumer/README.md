# External package consumer

Run from the repository root:

```sh
dotnet pack src/Zeroshot.Sdk/Zeroshot.Sdk.csproj -c Release -o artifacts/packages
dotnet restore examples/ContractsConsumer/ContractsConsumer.csproj --source artifacts/packages --source https://api.nuget.org/v3/index.json
dotnet run --project examples/ContractsConsumer/ContractsConsumer.csproj -c Release --no-restore
```

This project references the package, not the library project. Restore obtains build dependencies; executing the program performs local contract work only, with no network, Python or native executable. The example does not claim that native semantic admission or execution would accept the sample graph/runtime.
