<!-- refreshed: 2026-08-17 -->
# Architecture

**Analysis Date:** 2026-08-17

## System Overview

```text
┌───────────────────────────────────────────────────────────────────────────┐
│                          CLI Layer (DataverseDuck.Cli)                     │
│  `src/DataverseDuck.Cli/Program.cs` — dispatches: doctor | capture | query │
│  | repl | tables                                                           │
│  `ReplSession.cs`, `ReplLoop.cs`, `ReplTable.cs`, `ResultWriter.cs`         │
│  `CaptureArguments.cs`                                                     │
└───────────────────────────────┬─────────────────────────────────────────┘
                                 │ constructs / drives
                                 ▼
┌───────────────────────────────────────────────────────────────────────────┐
│                    Library Root (DataverseDuck namespace)                  │
│  `src/DataverseDuck/DataverseCache.cs` — orchestrator, "the whole point"   │
│  `DataversePlanParser.cs` — parses the `WITH ... AS JSON/DATAVERSE` sugar   │
│  `KeySetPushdown.cs` — extracts `{{ }}` local key queries, batches them     │
│  `DataverseThrottling.cs` — explains 429 / service-protection faults        │
│  `CacheManifest.cs` — records what a cache table/view is and where from     │
│  `Sql4CdsQuerySource.cs` / `IDataverseQuerySource.cs` — Dataverse access    │
│  `Sql4CdsConnectionFactory.cs` — builds authenticated SQL 4 CDS connections │
│  `UtcTimestampPolicy.cs` — pins DuckDB session to UTC                       │
│  `JsonTimestampInspector.cs` — flags textual (unparsed) JSON timestamps     │
└───┬──────────────┬──────────────────┬──────────────────┬─────────────────┘
    │               │                  │                   │
    ▼               ▼                  ▼                   ▼
┌─────────┐   ┌────────────┐   ┌──────────────┐   ┌──────────────────┐
│Configuration│  │Diagnostics │   │   Schema     │   │     Plans        │
│`Configuration/│ `Diagnostics/│  │`Schema/`     │   │`Plans/`          │
│DataverseOptions│EnvironmentDoctor│DataverseSchemaMapper│ExecutionPlanAnalyzer│
│DataverseCredential│AccessTokenClaims│DuckDbBulkLoader│FoldingPolicy    │
│DotEnvFile`     │ServiceProtectionBudget`│ColumnMapping`│PlanAnalysis` │
└─────────┘   └────────────┘   └──────────────┘   └──────────────────┘
    │                                    │
    │                                    ▼
    │                          ┌──────────────────────┐
    │                          │   Metadata            │
    │                          │`Metadata/MetadataCapture.cs`
    │                          │`MetadataSnapshot.cs`  │
    │                          │`SnapshotMetadataCache.cs` (offline schema)
    │                          └──────────────────────┘
    │
    ▼
┌───────────────────────────────────────────────────────────────────────────┐
│         External Systems: Entra ID (MSAL) → Dataverse (SQL 4 CDS)          │
│         Local: DuckDB file (`cache.duckdb`), JSON files (`read_json_auto`) │
└───────────────────────────────────────────────────────────────────────────┘
```

## Component Responsibilities

| Component | Responsibility | File |
|-----------|----------------|------|
| CLI dispatcher | Parses argv, routes to `doctor`/`capture`/`query`/`repl`/`tables` | `src/DataverseDuck.Cli/Program.cs` |
| REPL session | Interactive loop: parses statements, tracks fetched tables, completion | `src/DataverseDuck.Cli/ReplSession.cs`, `src/DataverseDuck.Cli/ReplLoop.cs` |
| Result formatting | Renders query output as tsv/csv/json/table | `src/DataverseDuck.Cli/ResultWriter.cs`, `src/DataverseDuck.Cli/ReplTable.cs` |
| Cache orchestrator | Fetches Dataverse rows into DuckDB tables, registers JSON views, records manifest | `src/DataverseDuck/DataverseCache.cs` |
| Plan parser | Desugars `WITH ... AS JSON(...) / AS DATAVERSE(...)` into ordered fetch steps | `src/DataverseDuck/DataversePlanParser.cs` |
| Key-set pushdown | Finds `{{ local subquery }}`, batches distinct keys, expands them into Dataverse SQL | `src/DataverseDuck/KeySetPushdown.cs` |
| Query source abstraction | Executes SQL against Dataverse (or a test double) | `src/DataverseDuck/IDataverseQuerySource.cs`, `src/DataverseDuck/Sql4CdsQuerySource.cs` |
| Connection factory | Builds `Sql4CdsConnection` with correct TDS/timezone/telemetry settings | `src/DataverseDuck/Sql4CdsConnectionFactory.cs` |
| Schema mapping | Maps `DbDataReader` columns/types to DuckDB DDL, decomposes lookups, normalises datetime | `src/DataverseDuck/Schema/DataverseSchemaMapper.cs`, `src/DataverseDuck/Schema/ColumnMapping.cs` |
| Bulk loader | Streams rows into DuckDB via the Appender API | `src/DataverseDuck/Schema/DuckDbBulkLoader.cs` |
| Execution plan analysis | Detects operations that would run locally instead of folding into FetchXML | `src/DataverseDuck/Plans/ExecutionPlanAnalyzer.cs`, `src/DataverseDuck/Plans/FoldingPolicy.cs`, `src/DataverseDuck/Plans/PlanAnalysis.cs` |
| Cache manifest | Persists table/view provenance (source SQL, row count, fetch time) in the DuckDB file itself | `src/DataverseDuck/CacheManifest.cs` |
| Throttling explanation | Translates Dataverse service-protection faults into actionable messages | `src/DataverseDuck/DataverseThrottling.cs` |
| Timestamp policy | Forces UTC session timezone; naive-UTC storage convention (ADR 0002) | `src/DataverseDuck/UtcTimestampPolicy.cs` |
| JSON timestamp inspection | Flags JSON columns whose timestamps stayed textual after `read_json_auto` | `src/DataverseDuck/JsonTimestampInspector.cs` |
| Configuration | Environment-variable-driven options, profile resolution, credential selection | `src/DataverseDuck/Configuration/DataverseOptions.cs`, `src/DataverseDuck/Configuration/DataverseCredential.cs`, `src/DataverseDuck/Configuration/DotEnvFile.cs` |
| Diagnostics | `doctor` checks: token acquisition, WhoAmI, privileges, table existence, query engine | `src/DataverseDuck/Diagnostics/EnvironmentDoctor.cs`, `src/DataverseDuck/Diagnostics/AccessTokenClaims.cs`, `src/DataverseDuck/Diagnostics/ServiceProtectionBudget.cs`, `src/DataverseDuck/Diagnostics/EnvironmentTenant.cs` |
| Offline metadata | Snapshot of entity metadata for `query --snapshot`-style offline schema mapping | `src/DataverseDuck/Metadata/MetadataCapture.cs`, `src/DataverseDuck/Metadata/MetadataSnapshot.cs`, `src/DataverseDuck/Metadata/SnapshotMetadataCache.cs` |

## Pattern Overview

**Overall:** Pipeline / ETL-style orchestrator over a small library, fronted by a thin CLI. Not layered MVC or DI-container style — it is a single-purpose data-movement tool composed from cohesive, single-responsibility classes wired together explicitly (manual constructor injection, no framework container).

**Key Characteristics:**
- One class (`DataverseCache`) is the composition root for the caching pipeline; everything else is a collaborator it owns or is handed.
- Abstraction boundary (`IDataverseQuerySource`) exists specifically to let the whole pipeline run in tests without a live Dataverse tenant — a fake reader over in-memory rows substitutes for `Sql4CdsQuerySource`.
- SQL compilation/pushdown is delegated entirely to SQL 4 CDS (T-SQL → FetchXML); this codebase does not implement a query planner itself, only a *folding guard* (`ExecutionPlanAnalyzer`) that inspects the plan SQL 4 CDS produces and refuses to run ones that would drag full tables across the wire.
- DuckDB is both the local query engine and the persistence layer (a `.duckdb` file doubles as cache and manifest store).
- The CLI is a routing/formatting shell; all business logic lives in the library project (`src/DataverseDuck`) so it is independently packaged and testable.

## Layers

**CLI (`src/DataverseDuck.Cli/`):**
- Purpose: argument parsing, environment/profile resolution wiring, console I/O, output formatting (tsv/csv/json), REPL loop.
- Location: `src/DataverseDuck.Cli/Program.cs` (780 lines, all command handlers), `ReplSession.cs`, `ReplLoop.cs`, `ReplTable.cs`, `ResultWriter.cs`, `CaptureArguments.cs`.
- Contains: `static class Program` with one method per command (`DoctorAsync`, `Capture`, `Query`, `ReplAsync`, `Tables`), each building library types directly.
- Depends on: the library project (`src/DataverseDuck`) via `ProjectReference`; `PrettyPrompt` for the REPL renderer.
- Used by: end users via `dotnet run` or the packaged `dvduck` global tool.

**Library core (`src/DataverseDuck/*.cs`, root namespace):**
- Purpose: the caching/query pipeline itself — plan parsing, key-set pushdown, throttling translation, manifest bookkeeping, connection setup.
- Location: `src/DataverseDuck/DataverseCache.cs`, `DataversePlanParser.cs`, `KeySetPushdown.cs`, `DataverseThrottling.cs`, `CacheManifest.cs`, `Sql4CdsQuerySource.cs`, `IDataverseQuerySource.cs`, `Sql4CdsConnectionFactory.cs`, `UtcTimestampPolicy.cs`, `JsonTimestampInspector.cs`.
- Depends on: `MarkMpn.Sql4Cds.Engine` (query compilation), `DuckDB.NET.Data.Full` (storage/query), `Microsoft.PowerPlatform.Dataverse.Client` + `Microsoft.Identity.Client` (auth/transport), and the `Configuration`/`Schema`/`Plans`/`Diagnostics`/`Metadata` subnamespaces.
- Used by: the CLI project and the test project directly (no service layer indirection).

**Configuration (`src/DataverseDuck/Configuration/`):**
- Purpose: resolve connection settings and credentials from environment variables, with profile-prefix fallback and `.env` file loading.
- Location: `DataverseOptions.cs` (427 lines — env var names, profile resolution, `Describe()` for diagnostics), `DataverseCredential.cs` (secret vs certificate, mutually exclusive), `DotEnvFile.cs` (loads `.env` without overriding real env vars).
- Depends on: `Microsoft.Identity.Client`, `Microsoft.PowerPlatform.Dataverse.Client` (types only, no network calls itself).
- Used by: CLI command handlers and `Sql4CdsConnectionFactory`/`EnvironmentDoctor`.

**Diagnostics (`src/DataverseDuck/Diagnostics/`):**
- Purpose: the `doctor` command's checks — token acquisition, tenant match, WhoAmI, privileges, table existence, query engine sanity, service-protection budget.
- Location: `EnvironmentDoctor.cs` (343 lines, orchestrates checks in order, short-circuits on failure), `AccessTokenClaims.cs`, `CheckResult.cs`, `EnvironmentTenant.cs`, `ServiceProtectionBudget.cs`.
- Depends on: `Microsoft.Identity.Client` (MSAL), `Microsoft.PowerPlatform.Dataverse.Client`, `Microsoft.Xrm.Sdk*`.
- Used by: CLI `doctor` command.

**Schema (`src/DataverseDuck/Schema/`):**
- Purpose: map a Dataverse `DbDataReader` shape to DuckDB DDL and load rows via the Appender.
- Location: `DataverseSchemaMapper.cs` (329 lines — CLR-type-to-DuckDB-type mapping, lookup decomposition, datetime normalisation), `ColumnMapping.cs`, `DuckDbBulkLoader.cs` (Appender-based bulk insert, ~440k rows/sec per README).
- Depends on: `MarkMpn.Sql4Cds.Engine` (type info), `Microsoft.Xrm.Sdk` (`EntityReference`), `DuckDB.NET.Data.Full` (Appender API).
- Used by: `DataverseCache`.

**Plans (`src/DataverseDuck/Plans/`):**
- Purpose: inspect the query plan SQL 4 CDS compiles and decide whether it "folds" fully into FetchXML, or partly runs locally (ADR 0004).
- Location: `ExecutionPlanAnalyzer.cs` (220 lines), `FoldingPolicy.cs` (warn/strict/silent modes), `PlanAnalysis.cs` (result record + `Describe()`).
- Depends on: `MarkMpn.Sql4Cds.Engine` internal plan node types (via `InternalsVisibleTo` for tests, not exposed publicly).
- Used by: `DataverseCache`, `Sql4CdsQuerySource`.

**Metadata (`src/DataverseDuck/Metadata/`):**
- Purpose: capture and snapshot entity metadata so schema mapping can run offline without a live tenant (ADR 0003).
- Location: `MetadataCapture.cs`, `MetadataSnapshot.cs`, `SnapshotMetadataCache.cs`.
- Depends on: `Microsoft.Xrm.Sdk.Metadata`.
- Used by: CLI `capture` command; `DataverseSchemaMapper` optionally consumes a snapshot cache instead of live metadata calls.

## Data Flow

### Primary Request Path (`dvduck query --plan "WITH ..."`)

1. CLI parses argv, loads `.env`, resolves `DataverseOptions` (profile-aware) — `src/DataverseDuck.Cli/Program.cs`.
2. `DataversePlanParser.Parse` splits the `WITH` block into ordered `PlanStep`s (`JSON` or `DATAVERSE`) plus the final SQL — `src/DataverseDuck/DataversePlanParser.cs`.
3. For each step, in written order:
   - `JSON` step → `DataverseCache.RegisterJson` creates a DuckDB view over `read_json_auto(glob)` and flags textual timestamps — `src/DataverseDuck/DataverseCache.cs:RegisterJson`.
   - `DATAVERSE` step → `DataverseCache.Cache` runs, checking for an embedded `{{ }}` key query via `KeySetPushdown.FindKeyQuery` — `src/DataverseDuck/DataverseCache.cs:Cache`.
4. If a key query is present: `KeySetPushdown.ReadKeys` executes it against the already-cached DuckDB tables/views, batches the distinct keys, and expands each batch into literal-embedded Dataverse SQL — `src/DataverseDuck/KeySetPushdown.cs`.
5. Each batch (or the whole query, if no pushdown) is analyzed via `IDataverseQuerySource.Analyze` → `ExecutionPlanAnalyzer`; a plan that doesn't fully fold is logged, and rejected outright if `FoldingPolicy.Strict` — `src/DataverseDuck/Plans/ExecutionPlanAnalyzer.cs`.
6. `IDataverseQuerySource.Query` executes the SQL through `Sql4CdsConnection` (T-SQL compiled to FetchXML), returning a `DbDataReader` — `src/DataverseDuck/Sql4CdsQuerySource.cs`.
7. `DataverseSchemaMapper.MapReader` inspects the reader's first row to build a `TableMapping` (DDL, lookup decomposition, datetime normalisation) — `src/DataverseDuck/Schema/DataverseSchemaMapper.cs`.
8. `DuckDbBulkLoader.CreateTable` + `LoadInto` stream rows into the DuckDB Appender, all inside one transaction — `src/DataverseDuck/Schema/DuckDbBulkLoader.cs`.
9. `CacheManifest.Record` writes provenance (source SQL, row/key counts, timestamp, elapsed) into the same transaction — `src/DataverseDuck/CacheManifest.cs`.
10. After all plan steps complete, the final SQL runs directly against DuckDB (joining cached tables and JSON views) and `ResultWriter` formats output — `src/DataverseDuck/DataverseCache.cs:Query`, `src/DataverseDuck.Cli/ResultWriter.cs`.

### Doctor Flow (`dvduck doctor`)

1. `EnvironmentDoctor.RunAsync` builds a checklist, starting with a config description (secrets redacted) — `src/DataverseDuck/Diagnostics/EnvironmentDoctor.cs`.
2. Checks tenant match, acquires an MSAL token, inspects its claims, connects with `ServiceClient` and calls WhoAmI, checks privileges, optionally checks named tables exist, checks the query engine, and short-circuits on the first hard failure.
3. Each check returns a `CheckResult` (pass/fail + remedy text) — `src/DataverseDuck/Diagnostics/CheckResult.cs`.

### REPL Flow (`dvduck repl`)

1. `ReplSession` maintains one live `DataverseCache`-backed connection across multiple statements, so tables are fetched once and queried many times — `src/DataverseDuck.Cli/ReplSession.cs`.
2. `ReplLoop` drives `PrettyPrompt` for input, history, and completion; falls back to plain line reading when the terminal reports no size — `src/DataverseDuck.Cli/ReplLoop.cs`.
3. `DataversePlanParser.Parse(sql, requireFinalQuery: false)` allows a bare `WITH` block (fetch-only) as a distinct REPL statement from the query that reads it.

**State Management:**
- All durable state lives in the DuckDB file itself (cached tables, JSON views, and the manifest table written by `CacheManifest`) — there is no separate application database or in-memory session store beyond the open `DuckDBConnection` and the CLI process's own variables.
- Credentials/configuration are read once per process invocation from environment variables / `.env`; no config file caching across runs.

## Key Abstractions

**`IDataverseQuerySource`:**
- Purpose: decouples the caching pipeline from a live Dataverse connection so tests exercise the same code path with an in-memory reader.
- Examples: `src/DataverseDuck/IDataverseQuerySource.cs` (interface), `src/DataverseDuck/Sql4CdsQuerySource.cs` (real implementation).
- Pattern: strategy/seam interface with exactly one production implementation; test doubles live in `tests/DataverseDuck.Tests/`.

**`PlanStep` / `DataversePlan`:**
- Purpose: represents the desugared form of a `WITH ... AS JSON/DATAVERSE` block as an ordered list of fetch operations plus a final query.
- Examples: `src/DataverseDuck/DataversePlanParser.cs`.
- Pattern: immutable record types (`PlanStep`, `DataversePlan`) produced by a hand-written recursive-descent-style parser that balances parens/quotes without a full SQL grammar.

**`CacheResult` / `CacheEntry` / manifest:**
- Purpose: every cached table or registered view records its own provenance (source SQL, kind, row/key count, timestamp) inside the same DuckDB file, so a reopened cache is self-describing.
- Examples: `src/DataverseDuck/DataverseCache.cs` (`CacheResult` record), `src/DataverseDuck/CacheManifest.cs` (`CacheEntry`, `PlanStepKind`).
- Pattern: write-through metadata table, committed in the same transaction as the data it describes.

**`PlanAnalysis` / `FoldingPolicy`:**
- Purpose: represents whether a compiled query plan runs entirely inside Dataverse ("folded") or does some work locally, and what to do about it (warn vs. reject).
- Examples: `src/DataverseDuck/Plans/PlanAnalysis.cs`, `src/DataverseDuck/Plans/FoldingPolicy.cs`, `src/DataverseDuck/Plans/ExecutionPlanAnalyzer.cs`.
- Pattern: guard/policy object consulted at each fetch boundary, not a global setting enforced elsewhere.

**`DataverseOptions` / `DataverseCredential`:**
- Purpose: typed, profile-aware view over environment variables; refuses ambiguous credential configuration (both secret and certificate set) rather than picking one.
- Examples: `src/DataverseDuck/Configuration/DataverseOptions.cs`, `src/DataverseDuck/Configuration/DataverseCredential.cs`.
- Pattern: static factory (`DataverseOptions.FromEnvironment(...)`-style) over `Environment.GetEnvironmentVariable`, with a `Describe()` method for safe (secret-redacted) diagnostic output.

## Entry Points

**CLI process entry:**
- Location: `src/DataverseDuck.Cli/Program.cs` (`Main`)
- Triggers: `dotnet run --project src/DataverseDuck.Cli -- <command>` or the packaged `dvduck` tool.
- Responsibilities: console encoding setup, `.env` loading, profile flag extraction, command dispatch to `DoctorAsync`/`Capture`/`Query`/`ReplAsync`/`Tables`/`Help`.

**Library entry (embedding as a package):**
- Location: `src/DataverseDuck/DataverseCache.cs` (constructor takes an open `DuckDBConnection` + `IDataverseQuerySource`).
- Triggers: consumer code that adds the `DataverseDuck` NuGet package directly (per README `dotnet add package DataverseDuck`).
- Responsibilities: same caching pipeline as the CLI, usable without going through argv parsing.

**Build-time guard:**
- Location: `src/DataverseDuck/build/DataverseDuck.targets` (packed as `buildTransitive/DataverseDuck.targets`), enforced via `Directory.Build.targets`.
- Triggers: any consumer's `dotnet build`.
- Responsibilities: fails the build with error `DVD001` if `InvariantGlobalization=true`, because SQL 4 CDS throws an unhelpful `TypeInitializationException` under that setting.

## Architectural Constraints

- **Threading:** Single-threaded, synchronous pipeline per invocation. Async is used only for MSAL token acquisition and Dataverse WhoAmI calls in `EnvironmentDoctor` (`RunAsync`) and the REPL's async command loop (`ReplAsync`) — bulk loading, schema mapping, and plan analysis are all synchronous.
- **Global state:** `ServiceClient.MaxConnectionTimeout` and related retry settings are static and mutated process-wide by `Sql4CdsConnectionFactory.ConfigureForBulkExport` (`src/DataverseDuck/Sql4CdsConnectionFactory.cs`) — deliberate for a CLI whose only job is exporting, but a hazard if this library is embedded alongside other Dataverse SDK usage in the same process.
- **DuckDB session timezone:** `UtcTimestampPolicy.ConfigureConnection` must run before any query executes on a `DuckDBConnection`; `DataverseCache`'s constructor enforces this. Bypassing the constructor (e.g., raw DuckDB calls) can silently produce wrong timestamp comparisons.
- **SQL injection surface:** `DataverseCache.RegisterJson` and `KeySetPushdown` concatenate user-supplied paths/keys into SQL text (see `QuoteLiteral` in `src/DataverseDuck/DataverseCache.cs`); the mitigation is literal-quoting, not parameterization, because DuckDB's JSON reader and value embedding need string SQL.
- **No retry logic of its own:** `Sql4CdsConnectionFactory.ConfigureForBulkExport` explicitly avoids adding retry wrapping on top of the SDK's built-in `Retry-After` handling, to avoid multiplying wait times (documented rationale in the file itself).

## Anti-Patterns

### God-orchestrator class carrying transaction lifecycle

**What happens:** `DataverseCache` (`src/DataverseDuck/DataverseCache.cs`, 346 lines) owns query execution, schema mapping invocation, bulk loading, manifest recording, and DuckDB transaction boundaries all in `CacheOne`/`CacheMatching`.
**Why it's wrong:** Any change to how a table is cached (e.g., adding a new provenance field, changing transaction granularity) requires touching this one class in multiple places, and the two near-duplicate methods (`CacheOne` vs `CacheMatching`) share most of their structure but are not factored into a common template.
**Do this instead:** When extending caching behavior, prefer adding a policy object (as already done for `FoldingPolicy`, `Pushdown`) over adding more branches to `CacheOne`/`CacheMatching`; if a third caching mode is ever needed, extract the shared "analyze → fetch → map → load → record" sequence into a helper both call.

### String-concatenated SQL for view/path registration

**What happens:** `DataverseCache.RegisterJson` and `KeySetPushdown` build SQL via string interpolation with a hand-rolled `QuoteLiteral` escaper (`src/DataverseDuck/DataverseCache.cs`), rather than DuckDB parameter binding.
**Why it's wrong:** Any future change to `QuoteLiteral` or a missed call site reintroduces an injection point; DuckDB.NET does support parameterized queries for values (though not for identifiers like view names or `read_json_auto` globs).
**Do this instead:** Continue quoting identifiers via `DuckDbIdentifier.Validate`/`Quote` (already used for table/view names) and keep literal values passed through `QuoteLiteral` centralized — do not add new ad hoc string concatenation for SQL elsewhere without routing through these two helpers.

## Error Handling

**Strategy:** Domain-specific exception types wrap underlying SDK/engine exceptions with actionable messages; `DataverseCache.Fetch` (in `src/DataverseDuck/DataverseCache.cs`) is the single point that translates Dataverse faults into `DataverseThrottledException` via `DataverseThrottling.Explain`.

**Patterns:**
- Guard-clause style validation at method entry (`ArgumentException.ThrowIfNullOrWhiteSpace`, `ArgumentNullException.ThrowIfNull`) throughout, e.g. `src/DataverseDuck/DataverseCache.cs`, `src/DataverseDuck/Sql4CdsQuerySource.cs`.
- Custom exceptions carry structured context rather than just a message: `PlanNotFoldedException` carries the `PlanAnalysis`; `DataverseThrottledException` carries the throttling explanation and inner exception — both defined near their throwing sites in `src/DataverseDuck/DataverseCache.cs` and `src/DataverseDuck/DataverseThrottling.cs`.
- Transactional rollback safety: caching failures roll back the DuckDB transaction so a partially-loaded table never replaces a good one (see comments in `DataverseCache.CacheOne`/`CacheMatching`).
- CLI-level errors are caught at the command-handler level in `Program.cs` and printed to `Console.Error` with a non-zero exit code (2 for argument errors) rather than propagating a stack trace to the user.

## Cross-Cutting Concerns

**Logging:** No logging framework — `DataverseCache.Log` is a simple `Action<string>?` delegate the caller supplies (CLI wires it to `Console.WriteLine`/stderr as appropriate); diagnostics (`EnvironmentDoctor`) return structured `CheckResult`s rather than logging directly.

**Validation:** Defensive argument validation at public API boundaries (`ArgumentException.ThrowIfNullOrWhiteSpace`, `DuckDbIdentifier.Validate` for identifiers used in generated SQL) rather than a centralized validation layer.

**Authentication:** Entra ID (MSAL) client-credentials flow only — secret or certificate, mutually exclusive, resolved by `DataverseCredential`/`DataverseOptions` (`src/DataverseDuck/Configuration/`). No interactive/user auth path exists anywhere in the codebase.

---

*Architecture analysis: 2026-08-17*
