# Technology Stack

**Analysis Date:** 2026-08-17

## Languages

**Primary:**
- C# (nullable-enabled, implicit usings) - all of `src/DataverseDuck`, `src/DataverseDuck.Cli`, `tests/DataverseDuck.Tests`, and `spikes/*`

**Secondary:**
- MSBuild XML - `Directory.Build.props`, `Directory.Build.targets`, `src/DataverseDuck/build/DataverseDuck.targets`
- SQL (T-SQL dialect compiled to FetchXML) - queries authored by library consumers, parsed in `src/DataverseDuck/DataversePlanParser.cs`
- Markdown - documentation and ADRs (`docs/adr/*.md`, `README.md`)

## Runtime

**Environment:**
- .NET 10.0 (`net10.0` target framework in every `.csproj`)
- Installed SDK observed: `10.0.109` (via `dotnet --version`)
- No `global.json` present — SDK resolution is unpinned, relies on locally installed .NET 10 SDK

**Package Manager:**
- NuGet (standard SDK-style `.csproj` `PackageReference`)
- No `nuget.config`/`NuGet.Config` present at repo root — uses default NuGet feed configuration
- No committed lockfile (`packages.lock.json` not present) — versions pinned only via explicit `PackageReference` version attributes

## Frameworks

**Core:**
- Microsoft.NET.Sdk (SDK-style project, no ASP.NET/web framework — this is a library + CLI tool, not a service)
- `MarkMpn.Sql4Cds.Engine` 10.4.4 - compiles T-SQL to FetchXML against Dataverse (`src/DataverseDuck/Sql4CdsConnectionFactory.cs`, `src/DataverseDuck/Sql4CdsQuerySource.cs`)
- `DuckDB.NET.Data.Full` 1.5.5 - embedded analytical database, used via Appender API for bulk loads (`src/DataverseDuck/Schema/DuckDbBulkLoader.cs`)
- `Microsoft.Identity.Client` (MSAL) 4.79.0 - Entra ID confidential-client auth (`src/DataverseDuck/Configuration/DataverseCredential.cs`)
- `Microsoft.PowerPlatform.Dataverse.Client` (transitive via Sql4Cds/ServiceClient) - `ServiceClient`/`IOrganizationService` connection to Dataverse

**CLI-only:**
- `PrettyPrompt` 6.0.5 - interactive REPL editing/completion (`src/DataverseDuck.Cli/ReplLoop.cs`, `ReplSession.cs`)

**Testing:**
- `xunit` 2.9.3 + `xunit.runner.visualstudio` 3.1.4 - test framework (`tests/DataverseDuck.Tests/DataverseDuck.Tests.csproj`)
- `Microsoft.NET.Test.Sdk` 17.14.1 - test host
- `coverlet.collector` 6.0.4 - code coverage collection

**Build/Dev:**
- `dotnet pack -c Release -o out` builds distributable NuGet package + dotnet tool (test project and `spikes/` excluded via `IsPackable=false`)
- `Directory.Build.props`/`Directory.Build.targets` apply repo-wide MSBuild settings (version, license, NuGet audit, transitive dependency pin)
- Custom MSBuild target `DataverseDuckCheckGlobalization` (`src/DataverseDuck/build/DataverseDuck.targets`, packed as `buildTransitive`) fails consumer builds if `InvariantGlobalization=true`, because SQL 4 CDS needs ICU culture data

## Key Dependencies

**Critical:**
- `MarkMpn.Sql4Cds.Engine` 10.4.4 - core query compilation (T-SQL → FetchXML); the entire pushdown/folding design (`src/DataverseDuck/Plans/`) is built around its internals (note: `InternalsVisibleTo` grants `DataverseDuck.Tests` access to `DataverseDuck`'s internals, and the plan analyzer couples to Sql4Cds's own internal engine type names)
- `DuckDB.NET.Data.Full` 1.5.5 - local storage/join engine; Appender-based bulk load path measured at ~440k rows/sec (per README)
- `Microsoft.Identity.Client` 4.79.0 - MSAL confidential client for client-secret and certificate auth flows

**Infrastructure/transitive pin:**
- `System.Security.Cryptography.Pkcs` 10.0.11 - pinned explicitly in `Directory.Build.props` to patch GHSA-555c-2p6r-68mm (DoS) in a version `MarkMpn.Sql4Cds.Engine` transitively pulls in at a vulnerable 6.0.1; applied repo-wide (including spikes) via `Directory.Build.props` rather than per-project

## Configuration

**Environment:**
- Dotenv-style file loading: `.env` (gitignored, real secrets) vs `.env.example` (committed template) — parsed by `src/DataverseDuck/Configuration/DotEnvFile.cs`
- All config variables prefixed `DATAVERSE_` (e.g. `DATAVERSE_URL`, `DATAVERSE_TENANT_ID`, `DATAVERSE_CLIENT_ID`, `DATAVERSE_CLIENT_SECRET`, `DATAVERSE_CERT_PATH`, `DATAVERSE_CERT_PASSWORD`, `DATAVERSE_CERT_THUMBPRINT`)
- Named-profile support: any variable can be prefixed with a profile name (e.g. `DATAVERSE_TEST_URL`) selected via `--profile` CLI flag or `DATAVERSE_PROFILE` env var; unset profile values fall back to unprefixed defaults
- Exactly one credential type (secret, cert-file, cert-thumbprint) must be set — configuration validated and surfaced via `dvduck doctor` (`src/DataverseDuck/Diagnostics/EnvironmentDoctor.cs`)
- `.env` file existence noted only — contents never inspected (real secrets live there, gitignored)

**Build:**
- `Directory.Build.props` / `Directory.Build.targets` (repo root) — shared MSBuild properties for all projects
- `src/DataverseDuck/build/DataverseDuck.targets` — shipped inside the NuGet package (`buildTransitive`) to enforce `InvariantGlobalization=false` in consuming projects
- Per-project `.csproj` files set `TargetFramework`, `Nullable`, `ImplicitUsings`, and (for the CLI) `InvariantGlobalization=false` explicitly

## Platform Requirements

**Development:**
- .NET 10 SDK installed locally
- Linux-compatible (verified environment is Linux); note in CLI project that certificate store lookup (`DATAVERSE_CERT_THUMBPRINT`) is discouraged on Linux since the per-user certificate store isn't populated by default — prefer `DATAVERSE_CERT_PATH` (.pfx file)
- Entra ID app registration required for any live Dataverse testing (client secret or certificate)
- Access to a live Dataverse/Power Platform environment for integration verification (README documents which components are "verified live" vs. unit-tested only)

**Production/Distribution:**
- Distributed as two NuGet artifacts: `DataverseDuck` (library, `dotnet add package DataverseDuck`) and `DataverseDuck.Cli` (global dotnet tool, `dotnet tool install --global DataverseDuck.Cli`, command name `dvduck`)
- No containerization, no server/hosting target — this is a client library + CLI, not a deployed service
- No CI/CD pipeline files detected (no `.github/workflows/`) — builds/tests run manually or via external tooling not present in this repo

---

*Stack analysis: 2026-08-17*
