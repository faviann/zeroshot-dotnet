# Zeroshot .NET

The .NET home for a future Zeroshot client and a small CLI that uses it. This
repository is a scaffold for upcoming protocol and API design work. It does not
submit or inspect Zeroshot runs yet.

## Projects

| Project | Purpose |
| --- | --- |
| `src/Zeroshot.Client` | Reusable .NET 10 class library for the future Zeroshot-facing client. Its public API is intentionally undecided. |
| `src/Zeroshot.Cli` | .NET 10 command-line application with a direct project reference to `Zeroshot.Client`. Currently provides help only. |
| `tests/Zeroshot.Client.Tests` | TUnit project directly referencing the client library; no client behavior tests exist yet. |
| `tests/Zeroshot.Cli.Tests` | TUnit project directly referencing the CLI, including its bootstrap smoke tests. |

The intended dependency direction is CLI → client → Zeroshot's external
protocol. Consumers such as Broodling will reference the client independently;
this repository contains no Broodling reference or Broodling-specific policy.

## Build and test

Install a .NET 10 SDK, then run:

```sh
dotnet build Zeroshot.sln --configuration Release
dotnet test --project tests/Zeroshot.Cli.Tests/Zeroshot.Cli.Tests.csproj --configuration Release --no-restore
dotnet test --project tests/Zeroshot.Client.Tests/Zeroshot.Client.Tests.csproj --configuration Release --no-restore --ignore-exit-code 8
dotnet run --project src/Zeroshot.Cli -- --help
```

`global.json` selects the Microsoft Testing Platform runner used by TUnit.
The client test project has no tests until client behavior is designed. Its test
command ignores only the runner's exit code 8 (zero tests); the CLI test command
remains strict. There is no running Zeroshot service requirement for the current
tests.
The repository is licensed under the [MIT License](LICENSE).

## Scope of the bootstrap

The client library intentionally has no protocol types or transport code yet.
The CLI help is a placeholder, so command names, SDK methods, HTTP/OECP behavior,
packaging and release policy remain decisions for later design work.
