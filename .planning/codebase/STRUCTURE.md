# Codebase Structure

**Analysis Date:** 2026-08-17

## Directory Layout

```
dataverse-duck/
├── DataverseDuck.slnx          # Solution file (new .slnx XML format), lists src/ and tests/ projects
├── Directory.Build.props       # Shared MSBuild properties for all projects (version, license, package tags)
├── Directory.Build.targets     # Shared MSBuild targets (enforces build-time guards, e.g. InvariantGlobalization check)
├── README.md                   # Project overview, architecture diagram, status table, quickstart
├── LICENSE                     # MIT
├── src/
│   ├── DataverseDuck/           # Library project (packable NuGet: "DataverseDuck")
│   │   ├── Configuration/       # Env-var driven settings and credential resolution
│   │   ├── Diagnostics/         # `doctor` command's checks
│   │   ├── Metadata/            # Entity metadata capture/snapshot for offline work
│   │   ├── Plans/               # Execution-plan folding analysis
│   │   ├── Schema/               # Reader → DuckDB DDL mapping and bulk loading
│   │   ├── build/               # MSBuild targets shipped inside the NuGet package
│   │   ├── *.cs                 # Root namespace: cache orchestrator, plan parser, pushdown, throttling, manifest
│   │   └── DataverseDuck.csproj
│   └── DataverseDuck.Cli/       # CLI project (packable dotnet tool: "dvduck")
│       ├── Program.cs           # Entry point + all command handlers
│       ├── ReplSession.cs       # Interactive REPL session state
│       ├── ReplLoop.cs          # REPL input loop (PrettyPrompt-based)
│       ├── ReplTable.cs         # REPL result table rendering
│       ├── ResultWriter.cs      # tsv/csv/json output formatting
│       ├── CaptureArguments.cs  # `capture` command argument parsing
│       └── DataverseDuck.Cli.csproj
├── tests/
│   └── DataverseDuck.Tests/     # xUnit test project, references both src projects
│       └── *.cs                 # One test file per production class/concern (see Testing note below)
├── spikes/                      # Standalone throwaway console apps for experiments, NOT part of the solution
│   ├── AppenderSpike/           # DuckDB Appender throughput measurement
│   ├── MetadataSpike/           # Entity metadata exploration
│   ├── PushdownSpike/           # Key-set pushdown prototyping
│   ├── SeedData/                # Generates sample/seed data
│   ├── ThrottleSpike/           # Provoking/observing service-protection throttling
│   └── TimezoneSpike/           # DateTime/timezone behavior experiments
├── docs/
│   ├── adr/                     # Architecture Decision Records, numbered 0001-0010 + README index
│   ├── environment-setup.md     # How to configure a real Dataverse tenant (Entra app reg, app user, role)
│   ├── large-tables.md          # Guidance on large-table handling / pushdown
│   ├── metadata.md              # Metadata snapshot documentation
│   ├── service-protection.md    # Dataverse service-protection limits explained
│   └── sql4cds-behaviour.md     # Notes on observed SQL 4 CDS engine behavior
└── metadata/
    └── snapshot.bin              # Default output location for `dvduck capture` (binary metadata snapshot)
```

## Directory Purposes

**`src/DataverseDuck/` (library root):**
- Purpose: all caching/query pipeline logic, published as the `DataverseDuck` NuGet package.
- Contains: orchestrator (`DataverseCache.cs`), plan parser (`DataversePlanParser.cs`), pushdown (`KeySetPushdown.cs`), throttling (`DataverseThrottling.cs`), manifest (`CacheManifest.cs`), connection factory (`Sql4CdsConnectionFactory.cs`), query source abstraction (`IDataverseQuerySource.cs`, `Sql4CdsQuerySource.cs`), timestamp policy (`UtcTimestampPolicy.cs`), JSON timestamp inspection (`JsonTimestampInspector.cs`).
- Key files: `src/DataverseDuck/DataverseCache.cs` (the composition root), `src/DataverseDuck/DataverseDuck.csproj`.

**`src/DataverseDuck/Configuration/`:**
- Purpose: resolves connection settings/credentials from environment variables (with profile prefixing) and `.env` files.
- Contains: `DataverseOptions.cs`, `DataverseCredential.cs`, `DotEnvFile.cs`.
- Key files: `src/DataverseDuck/Configuration/DataverseOptions.cs` (427 lines — the largest file in the library, defines every `DATAVERSE_*` env var name).

**`src/DataverseDuck/Diagnostics/`:**
- Purpose: implements the `doctor` command's step-by-step environment verification.
- Contains: `EnvironmentDoctor.cs`, `CheckResult.cs`, `AccessTokenClaims.cs`, `EnvironmentTenant.cs`, `ServiceProtectionBudget.cs`.
- Key files: `src/DataverseDuck/Diagnostics/EnvironmentDoctor.cs` (343 lines, orchestrates all checks in order).

**`src/DataverseDuck/Metadata/`:**
- Purpose: capture and snapshot Dataverse entity metadata so schema mapping can work offline (ADR 0003).
- Contains: `MetadataCapture.cs`, `MetadataSnapshot.cs`, `SnapshotMetadataCache.cs`.
- Key files: `src/DataverseDuck/Metadata/MetadataSnapshot.cs` (binary snapshot format written to `metadata/snapshot.bin` by default).

**`src/DataverseDuck/Plans/`:**
- Purpose: analyzes compiled query plans to detect operations that run locally instead of folding into Dataverse FetchXML (ADR 0004).
- Contains: `ExecutionPlanAnalyzer.cs`, `FoldingPolicy.cs`, `PlanAnalysis.cs`.
- Key files: `src/DataverseDuck/Plans/ExecutionPlanAnalyzer.cs` (220 lines).

**`src/DataverseDuck/Schema/`:**
- Purpose: maps Dataverse reader shapes to DuckDB table definitions and bulk-loads rows.
- Contains: `DataverseSchemaMapper.cs`, `ColumnMapping.cs`, `DuckDbBulkLoader.cs`.
- Key files: `src/DataverseDuck/Schema/DataverseSchemaMapper.cs` (329 lines — CLR type → DuckDB type mapping table).

**`src/DataverseDuck/build/`:**
- Purpose: MSBuild target packed into the NuGet package to fail consumer builds early if `InvariantGlobalization=true` (error `DVD001`).
- Contains: `DataverseDuck.targets`.

**`src/DataverseDuck.Cli/` (CLI):**
- Purpose: `dvduck` command-line tool — argument parsing, REPL, output formatting; thin wrapper over the library.
- Contains: `Program.cs` (780 lines, all command handlers + `Help()` text), `ReplSession.cs`, `ReplLoop.cs`, `ReplTable.cs`, `ResultWriter.cs`, `CaptureArguments.cs`.
- Key files: `src/DataverseDuck.Cli/Program.cs` (entry point `Main`).

**`tests/DataverseDuck.Tests/`:**
- Purpose: xUnit tests covering both projects; no live Dataverse tenant required (`dotnet test` runs standalone per README).
- Contains: one test file roughly per production concern (e.g. `SchemaMapperTests.cs`, `ExecutionPlanAnalyzerTests.cs`, `KeySetPushdownTests.cs`, `DataversePlanParserTests.cs`, `EnvironmentSetupTests.cs`, `ProfileTests.cs`, `ReplTests.cs`, `ResilienceTests.cs`).
- Key files: `tests/DataverseDuck.Tests/DataverseDuck.Tests.csproj` (references both `DataverseDuck` and `DataverseDuck.Cli` projects).

**`spikes/`:**
- Purpose: standalone, throwaway console apps used to measure/validate behavior before building it into the library (Appender throughput, metadata shape, pushdown feasibility, throttling behavior, timezone handling). Each is its own `.csproj`, not referenced by the solution's projects.
- Generated: build artifacts (`bin/`, `obj/`) present per spike but not source-controlled meaningfully; the spikes' `.cs` source files are the intentional content.
- Committed: yes, source files are committed; `bin/`/`obj/` are build output (should be gitignored).

**`docs/adr/`:**
- Purpose: Architecture Decision Records explaining *why* a design was chosen over alternatives (SQL 4 CDS vs TDS/OData, naive-UTC timestamps, metadata snapshots, folding guard, textual JSON timestamp detection, key-set pushdown, plan syntax, cache manifest, datetime behavior).
- Key files: `docs/adr/README.md` (index), `docs/adr/0001-sql4cds-over-tds-and-odata.md` through `docs/adr/0010-datetime-behaviour.md`.

**`docs/` (top-level files):**
- Purpose: operational/behavioral documentation not tied to a single ADR — environment setup walkthrough, large-table handling, metadata snapshot usage, service-protection limits, observed SQL 4 CDS quirks.
- Key files: `docs/environment-setup.md`, `docs/large-tables.md`, `docs/metadata.md`, `docs/service-protection.md`, `docs/sql4cds-behaviour.md`.

**`metadata/`:**
- Purpose: default output location for `dvduck capture`'s metadata snapshot.
- Generated: yes (`snapshot.bin` is produced by the `capture` command, not authored by hand).
- Committed: appears present in the repo as a checked-in example/default artifact — verify before treating as disposable.

## Key File Locations

**Entry Points:**
- `src/DataverseDuck.Cli/Program.cs`: CLI `Main`, command dispatch table (`doctor`/`capture`/`query`/`repl`/`tables`/`help`).
- `src/DataverseDuck/DataverseCache.cs`: library entry point for embedding (constructor takes `DuckDBConnection` + `IDataverseQuerySource`).

**Configuration:**
- `Directory.Build.props`: shared package metadata (version, authors, license, tags) for all projects.
- `Directory.Build.targets`: shared MSBuild targets/guards.
- `src/DataverseDuck/Configuration/DataverseOptions.cs`: all `DATAVERSE_*` environment variable names and profile resolution logic.
- `.env.example` (referenced in README, gitignored `.env` for actual secrets): local environment variable file, loaded by `src/DataverseDuck/Configuration/DotEnvFile.cs`.

**Core Logic:**
- `src/DataverseDuck/DataverseCache.cs`: caching orchestrator (fetch, map, load, record manifest).
- `src/DataverseDuck/DataversePlanParser.cs`: `WITH ... AS JSON/DATAVERSE` plan desugaring.
- `src/DataverseDuck/KeySetPushdown.cs`: `{{ }}` local-key-query extraction and batching.
- `src/DataverseDuck/Schema/DataverseSchemaMapper.cs`: reader-to-DDL type mapping.
- `src/DataverseDuck/Plans/ExecutionPlanAnalyzer.cs`: fold/no-fold plan analysis.

**Testing:**
- `tests/DataverseDuck.Tests/`: all test files, one per major class/concern; run via `dotnet test` at the repo root.

## Naming Conventions

**Files:**
- One public type per file, file name matches the type name exactly (standard C# convention), e.g. `DataverseCache.cs` contains `class DataverseCache` (plus small closely-related records like `CacheResult` in the same file).
- Test files are named `<ProductionClassName>Tests.cs`, e.g. `SchemaMapperTests.cs` for `DataverseSchemaMapper`, `KeySetPushdownTests.cs` for `KeySetPushdown`.
- ADR files are named `NNNN-kebab-case-title.md` under `docs/adr/`, sequential 4-digit prefix.

**Directories:**
- Subnamespace folders under `src/DataverseDuck/` (`Configuration/`, `Diagnostics/`, `Metadata/`, `Plans/`, `Schema/`) match the C# namespace exactly (`DataverseDuck.Configuration`, `DataverseDuck.Diagnostics`, etc.) — root-level files stay in the bare `DataverseDuck` namespace.
- Spike projects under `spikes/` are named `<Concern>Spike` (e.g. `AppenderSpike`, `PushdownSpike`, `ThrottleSpike`, `TimezoneSpike`) except `SeedData`, which generates fixtures rather than testing a concern.

## Where to Add New Code

**New library feature (e.g., a new caching mode or SQL extension):**
- Primary code: `src/DataverseDuck/` root namespace if it's cross-cutting (like `KeySetPushdown.cs`), or a new/existing subnamespace folder (`Configuration/`, `Diagnostics/`, `Metadata/`, `Plans/`, `Schema/`) if it fits an existing concern.
- Tests: `tests/DataverseDuck.Tests/<FeatureName>Tests.cs`, following the one-file-per-concern convention; add an `InternalsVisibleTo` entry in the relevant `.csproj` only if the feature needs to expose internals to tests (already done for `DataverseDuck.csproj` and `DataverseDuck.Cli.csproj`).

**New CLI command:**
- Implementation: add a case to the `switch` in `src/DataverseDuck.Cli/Program.cs` `Main`, plus a handler method following the pattern of `DoctorAsync`/`Capture`/`Query`/`Tables`; update the `Help()` method's usage text in the same file.
- Tests: `tests/DataverseDuck.Tests/` — see `CaptureArgumentsTests.cs`/`ReplTests.cs` for CLI-layer test patterns.

**New Dataverse type mapping or schema behavior:**
- Implementation: `src/DataverseDuck/Schema/DataverseSchemaMapper.cs` (type table) and `src/DataverseDuck/Schema/ColumnMapping.cs` (per-column representation).
- Tests: `tests/DataverseDuck.Tests/SchemaMapperTests.cs`, `tests/DataverseDuck.Tests/DateTimeBehaviorTests.cs`.
- Document the rationale as a new ADR in `docs/adr/` if it's a non-obvious design decision (established pattern: see `docs/adr/0010-datetime-behaviour.md`).

**New diagnostics check for `doctor`:**
- Implementation: add a private check method to `src/DataverseDuck/Diagnostics/EnvironmentDoctor.cs`, appended to the `checks` list in `RunAsync`, returning a `CheckResult` (`src/DataverseDuck/Diagnostics/CheckResult.cs`).
- Tests: `tests/DataverseDuck.Tests/EnvironmentSetupTests.cs`.

**Experimental/throwaway validation:**
- New spike: `spikes/<ConcernName>Spike/Program.cs`, its own `.csproj`, not referenced by `DataverseDuck.slnx`'s main src/tests folders (spikes are excluded from `dotnet pack` per `Directory.Build.props`).

**Utilities:**
- Shared helpers with no clear subnamespace home stay in the `src/DataverseDuck/` root (e.g. `UtcTimestampPolicy.cs`, `JsonTimestampInspector.cs`) rather than a generic `Utils/` folder — this codebase has no catch-all utility directory.

## Special Directories

**`spikes/`:**
- Purpose: exploratory console apps, not production code.
- Generated: partially — `bin/`/`obj/` under each spike are build output.
- Committed: source `.cs`/`.csproj` files are committed; excluded from the solution's pack/test targets via `Directory.Build.props` (`IsPackable` gating).

**`metadata/`:**
- Purpose: holds `snapshot.bin`, the default output of `dvduck capture`.
- Generated: yes.
- Committed: present in the repo tree — treat as a sample/default artifact rather than source.

**`bin/` / `obj/` (under every `.csproj` directory):**
- Purpose: standard .NET build output and intermediate files.
- Generated: yes, entirely.
- Committed: no (should be excluded via `.gitignore`; not part of source structure).

**`docs/adr/`:**
- Purpose: durable design-decision records, referenced from README and expected to be consulted before changing established behavior (e.g. UTC timestamp policy, folding guard, plan syntax).
- Generated: no, hand-written.
- Committed: yes.

---

*Structure analysis: 2026-08-17*
