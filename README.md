# Zeroshot .NET

The .NET home for a future Zeroshot client and a small CLI that uses it. This
repository is a scaffold for upcoming protocol and API design work. It does not
submit or inspect Zeroshot runs yet.

## Projects

| Project | Purpose |
| --- | --- |
| `src/Zeroshot.Client` | Reusable .NET 10 class library for the future Zeroshot-facing client. Its public API is intentionally undecided. |
| `src/Zeroshot.Cli` | .NET 10 command-line application with a direct project reference to `Zeroshot.Client`. Currently provides help only. |
| `tests/Zeroshot.Tests` | TUnit tests for the bootstrap behavior. |

The intended dependency direction is CLI → client → Zeroshot's external
protocol. Consumers such as Broodling will reference the client independently;
this repository contains no Broodling reference or Broodling-specific policy.

## Build and test

Install a .NET 10 SDK, then run:

```sh
dotnet build Zeroshot.sln
dotnet test --solution Zeroshot.sln
dotnet run --project src/Zeroshot.Cli -- --help
```

`global.json` selects the Microsoft Testing Platform runner used by TUnit.
There is no running Zeroshot service requirement for the current tests.

## Scope of the bootstrap

The client library intentionally has no protocol types or transport code yet.
The CLI help is a placeholder, so command names, SDK methods, HTTP/OECP behavior,
packaging and release policy remain decisions for later design work.
