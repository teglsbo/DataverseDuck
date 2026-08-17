# Codebase Concerns

**Analysis Date:** 2026-08-17

This document is derived from the project's own tracked "Open" items (`README.md`), ADR
caveats (`docs/adr/`), the throttling/large-table spikes (`spikes/ThrottleSpike`,
`spikes/PushdownSpike`), and direct inspection of `src/` and `tests/`. The project is
unusually self-documenting about its own gaps — most items below are acknowledged in the
repo itself rather than inferred.

## Tech Debt

**Unverified certificate authentication path:**
- Issue: Certificate-based auth (`DATAVERSE_CERT_PATH` / `DATAVERSE_CERT_THUMBPRINT`) is
  implemented and unit tested against a generated certificate, but has never run against
  a real Dataverse tenant — the project's own tenant only has a client secret configured.
- Files: `src/DataverseDuck/Sql4CdsConnectionFactory.cs`, `src/DataverseDuck/Configuration/DataverseCredential.cs`
- Impact: The SDK's certificate constructor path is entirely unverified live; any
  environment-specific quirk (cert store access, chain validation, thumbprint lookup)
  would surface only in production use.
- Fix approach: Verify against a tenant configured with a certificate credential, or add
  an integration test harness that can accept one via CI secrets.

**Package naming inconsistency:**
- Issue: Repository name (`dataverse-duck`), CLI command (`dvduck`), and package IDs
  (`DataverseDuck`, `DataverseDuck.Cli`) are all different and not yet finalized.
- Files: `src/DataverseDuck/DataverseDuck.csproj`, `src/DataverseDuck.Cli/DataverseDuck.Cli.csproj`
- Impact: `PackageId` and `ToolCommandName` are difficult to change after a first NuGet
  release; committing to the current names now locks them in.
- Fix approach: Decide the final name before first publish; document the decision as an ADR.

**`dvduck query --snapshot` not implemented:**
- Issue: `dvduck capture` can write a metadata snapshot to disk, but `dvduck query` has no
  flag to read one back — only the library API can use a snapshot for offline work.
- Files: `src/DataverseDuck.Cli/Program.cs`, `src/DataverseDuck/Metadata/SnapshotMetadataCache.cs`
- Impact: Offline development against a snapshot requires writing C# code rather than
  using the CLI, contradicting the offline-development goal in ADR 0003.
- Fix approach: Add a `--snapshot <path>` option to `query` that wires a
  `SnapshotMetadataCache` into the same path `capture` already exercises.

**Package metadata incomplete:**
- Issue: No `RepositoryUrl` set, and version is pinned at `0.1.0`.
- Files: `Directory.Build.props`
- Impact: Published NuGet packages would lack source-link/repository metadata; version
  has not moved since initial development.
- Fix approach: Add `RepositoryUrl`/`PackageProjectUrl` to `Directory.Build.props` before
  first publish; establish a versioning policy.

**Metadata queries ignore injected cache when using a snapshot:**
- Issue: `metadata.entity` / `metadata.attribute` / relationship queries call the live
  service (`RetrieveMetadataChanges`) even when a metadata snapshot cache is present,
  rather than answering from the snapshot.
- Files: `src/DataverseDuck/Metadata/SnapshotMetadataCache.cs`, `src/DataverseDuck/Metadata/MetadataSnapshot.cs`
- Impact: Documented in `README.md` ("These queries need a live connection... the engine
  answers metadata queries by calling the service and ignores the injected metadata
  cache"). Breaks the offline story specifically for schema exploration.
- Fix approach: This is an SQL 4 CDS engine limitation, not something fixable locally
  without upstream changes or a query-rewriting shim; document as a known limitation if
  no fix is planned (partially done already — surface prominently near `--snapshot` work
  above).

## Known Bugs

**`COUNT(*)` exceeds the documented 50,000 aggregate limit:**
- Symptoms: A `COUNT(*)` against `solutioncomponent` returned 56,161 rows, despite
  Microsoft's documented `AggregateQueryRecordLimit` of 50,000.
- Files: Referenced in `README.md` ("Open" section) and `docs/large-tables.md`
- Trigger: Any aggregate query over a table exceeding 50,000 rows.
- Workaround: None currently; unclear whether the limit does not apply to `COUNT` or is
  simply unenforced in this environment. Needs a second environment or Microsoft support
  ticket to confirm which.

**Trailing newline flips `read_json_auto` type inference:**
- Symptoms: A trailing newline character in a JSON file changes how DuckDB's
  `read_json_auto` infers column types, per ADR 0002 measurement 9.
- Files: `docs/adr/0002-utc-naive-timestamps.md` (measurement 9), any code path reading
  JSON logs via `read_json_auto`
- Trigger: JSON log/state files with a trailing newline.
- Workaround: None implemented; root cause is a DuckDB behavior, not this project's code.
  Worth a regression test if it starts affecting real log files.

**`DataverseThrottling.FromErrorCode` has never observed a live 429:**
- Symptoms: Not a bug per se, but the 429-handling path (`DataverseThrottling`) has only
  ever been exercised by constructed unit-test faults. Three live attempts to provoke a
  real service-protection throttle in `spikes/ThrottleSpike/Program.cs` all failed —
  either the load was diluted across web servers (unpinned client) or serialized (single
  shared client), so a genuine 429 was never observed end-to-end.
- Files: `src/DataverseDuck/DataverseThrottling.cs`, `spikes/ThrottleSpike/Program.cs`,
  `spikes/ThrottleSpike/README.md`
- Trigger: Sustained high concurrency against a single pinned Dataverse web server
  (untested hypothesis: >40 concurrent ~5s requests needed).
- Workaround: Error-code parsing is unit tested against constructed SOAP fault text; the
  live wiring (hex error code → signed integer → `FromErrorCode`) has not executed against
  a real fault.

## Security Considerations

**`.env` file handling relies on user discipline:**
- Risk: Client secret / certificate paths are read from a `.env` file
  (`src/DataverseDuck/Configuration/DotEnvFile.cs`). The file is gitignored and the README
  instructs `chmod 600`, but nothing in code enforces file permissions or warns if the
  file is world-readable.
- Files: `src/DataverseDuck/Configuration/DotEnvFile.cs`, `src/DataverseDuck/Configuration/DataverseCredential.cs`
- Current mitigation: Documentation only (`README.md` Quickstart: "chmod 600 it").
- Recommendations: Consider a `doctor` check that warns if `.env` permissions are looser
  than `600` on POSIX systems.

**Two credential types set simultaneously are refused rather than ranked:**
- Risk: If both `DATAVERSE_CLIENT_SECRET` and a certificate variable are set, the library
  refuses to guess which should win — this is a deliberate, good design choice, not a gap.
- Files: `src/DataverseDuck/Configuration/DataverseCredential.cs`
- Current mitigation: Explicit rejection with a clear error (by design, per README).
- Recommendations: None — flagged here only because it's a security-relevant design
  decision worth preserving in any refactor.

**`InvariantGlobalization` foot-gun caught at build time, not runtime:**
- Risk: Setting `InvariantGlobalization=true` in a consuming project causes SQL 4 CDS to
  throw a `TypeInitializationException` with a message that names nothing relevant,
  costing a full live debugging session to trace (per README).
- Files: `src/DataverseDuck/build/` (MSBuild target, error `DVD001`), `src/DataverseDuck/DataverseDuck.csproj`
- Current mitigation: A build target now fails the consumer's build early with a named
  error (`DVD001`) instead of surfacing as a cryptic runtime crash.
- Recommendations: None — already mitigated; documented here as evidence of a
  previously-fragile integration point now hardened.

## Performance Bottlenecks

**Key-set pushdown becomes superlinear above ~1,000 keys:**
- Problem: Pushing a `{{ }}` key list into a Dataverse `IN` filter is fast up to ~1,000
  keys but scales worse than linearly beyond that — 10,000 keys costs about the same as
  scanning the entire 56,000-row table it was meant to avoid fetching.
- Files: `src/DataverseDuck/KeySetPushdown.cs`, `docs/large-tables.md` (measured table)
- Cause: FetchXML folds an `IN` list into a single condition regardless of length (not
  the documented 500-condition limit), but per-condition cost apparently grows with list
  size server-side.
- Improvement path: `docs/large-tables.md` recommends chunking above ~10,000 keys into
  batches of ~2,000, which measured more than 2x faster than one large `IN`. This chunking
  is not implemented in `KeySetPushdown.cs` — it is a documented recommendation only.

**~8,000 rows/second ceiling on full table scans:**
- Problem: Reading a full table via `RetrieveMultiple`/FetchXML tops out around 8,000
  rows/sec for narrow columns (measured against `solutioncomponent`, 56,161 rows).
- Files: `src/DataverseDuck/Sql4CdsQuerySource.cs`, `docs/large-tables.md`
- Cause: Inherent to the FetchXML/SOAP data path — the TDS endpoint (which might be
  faster) is unavailable because it doesn't support service-principal auth (ADR 0001),
  and Synapse/Fabric Link require infrastructure this project doesn't have.
- Improvement path: None available without additional Azure infrastructure. At ~8,000
  rows/s, 10M rows would take ~20 minutes, colliding directly with the 20-minute
  per-5-minute execution-time service protection limit — effectively a hard ceiling on
  single-query table size documented in `docs/large-tables.md`.

**Service protection budget headers are per-web-server, not per-environment:**
- Problem: `x-ms-ratelimit-*` headers describe the specific web server that answered, not
  the whole environment. A client that doesn't pin affinity spreads load across the farm,
  making the counters read as if plenty of budget remains when the real bottleneck is
  elsewhere.
- Files: `src/DataverseDuck/Diagnostics/ServiceProtectionBudget.cs`
- Cause: Documented Dataverse behavior, confirmed experimentally in
  `spikes/ThrottleSpike/Program.cs` (Attempt 1: 820 requests moved the burst counter by 2).
- Improvement path: `ServiceProtectionBudget.ServerAffinity` (from the `ARRAffinity`
  cookie) lets callers tell whether two readings are comparable, but nothing in
  `dvduck doctor` currently warns when affinity is unpinned during a bulk read.

## Fragile Areas

**Broad `catch (Exception e)` blocks without further filtering:**
- Files: `src/DataverseDuck.Cli/ReplSession.cs:95,164,331`, `src/DataverseDuck.Cli/Program.cs:312,519,723`,
  `src/DataverseDuck/Diagnostics/EnvironmentDoctor.cs:124,207,271,329`
- Why fragile: Unfiltered `catch (Exception e)` blocks in CLI/REPL and doctor-check code
  can silently swallow unrelated failures (e.g. `OutOfMemoryException`,
  programming errors) alongside the expected ones (network/auth faults), making bugs in
  those catch bodies harder to notice. Most other catches in the library layer use
  exception filters (`when (e is ...)`) to be precise — the CLI layer is less disciplined.
- Safe modification: When touching these blocks, prefer adding an exception filter
  (`when (e is SomeSpecificException)`) matching the pattern already used in
  `src/DataverseDuck/Schema/DataverseSchemaMapper.cs:99` and
  `src/DataverseDuck/DataverseCache.cs:277`.
- Test coverage: `tests/DataverseDuck.Tests/ReplTests.cs` and `EnvironmentSetupTests.cs`
  cover the happy/expected-error paths but not arbitrary unexpected exceptions inside
  these catch blocks.

**REPL interactive-only code paths are effectively untested:**
- Files: `src/DataverseDuck.Cli/ReplLoop.cs`, `src/DataverseDuck.Cli/ReplSession.cs`
- Why fragile: Per `README.md`, the piped input path is covered by tests and live use, and
  completion is driven through PrettyPrompt's `IPromptCallbacks` in tests — but history,
  multi-line editing, and how the completion window actually renders on a real terminal
  are untested. The renderer's fallback to line-reading when a terminal reports no size is
  also implemented but not exercised by an actual terminal.
- Safe modification: Manual smoke-testing against a real terminal is required before any
  change to prompt rendering, history, or completion-window layout; automated tests
  cannot catch regressions here.
- Test coverage: `tests/DataverseDuck.Tests/ReplTests.cs` (436 lines) covers piped
  input/output extensively but explicitly does not cover keyboard-driven interaction.

**Large procedural files with high cyclomatic surface:**
- Files: `src/DataverseDuck.Cli/Program.cs` (780 lines — CLI command dispatch, arg
  parsing, and all subcommand bodies), `src/DataverseDuck/DataversePlanParser.cs` (571
  lines — the entire `WITH ... AS DATAVERSE/JSON` plan-syntax parser)
- Why fragile: Both files are large single-responsibility-per-file but internally dense —
  `Program.cs` mixes argument parsing, credential resolution, and command execution for
  every subcommand (`doctor`, `capture`, `query`, `repl`, `tables`) in one file, and
  `DataversePlanParser.cs` implements a hand-written recursive-descent-style parser for
  the plan DSL (ADR 0008) with no formal grammar tooling (e.g. no parser generator).
- Safe modification: Changes to plan syntax should be cross-checked against
  `docs/adr/0008-plan-syntax.md` and the corresponding 429-line
  `tests/DataverseDuck.Tests/DataversePlanParserTests.cs`; changes to CLI argument handling
  should be checked against `tests/DataverseDuck.Tests/CaptureArgumentsTests.cs`.
- Test coverage: Both files have substantial dedicated test files, so risk is in
  future extension (new flags, new plan syntax) rather than current gaps.

## Scaling Limits

**~20-minute execution-time wall per 5-minute sliding window:**
- Current capacity: At the measured ~8,000 rows/sec scan rate, roughly 10M rows takes
  about 20 minutes — exactly the per-user, per-server execution-time budget Dataverse
  enforces every 5 minutes.
- Limit: Any single query pulling substantially more than ~10M narrow-column rows (fewer
  for wide tables — `contact` has 311 attributes) will likely hit
  `-2147015903` (execution time exceeded) before completing.
- Scaling path: `docs/large-tables.md` and ADR 0001 already rule out Synapse Link, Fabric
  Link, and the TDS endpoint as options (missing infrastructure / no service-principal
  auth support). The only scaling levers within this project are narrower column
  selection and key-set pushdown (which itself degrades above ~10,000 keys — see
  Performance Bottlenecks above) or chunked/resumable reads, which are not implemented.

**Per-user request and concurrency budget is shared across all callers of one app registration:**
- Current capacity: 6,000–8,000 requests / 20 min execution / 52 concurrent requests per
  user per web server, per Microsoft's FAQ confirmed to apply identically to service
  principals (no headroom from using an app registration).
- Limit: Any other process (CI job, another `dvduck` invocation, a Power Automate flow)
  authenticating as the *same* app registration draws from the *same* budget.
- Scaling path: Not addressed in code — would require either a dedicated app registration
  per workload or explicit coordination between callers sharing one identity. Worth a
  documentation note if multiple concurrent `dvduck` users are expected against one tenant.

## Dependencies at Risk

**`MarkMpn.Sql4Cds.Engine` is a load-bearing third-party dependency with undocumented internals:**
- Risk: The entire T-SQL → FetchXML translation layer, and the metadata-cache bypass
  described above, live inside this closed-scope dependency. Its behavior around
  `InvariantGlobalization` required reverse-engineering (a live debugging session) rather
  than being documented.
- Files: `src/DataverseDuck/DataverseDuck.csproj` (`MarkMpn.Sql4Cds.Engine` 10.4.4),
  `src/DataverseDuck/build/` (the `DVD001` guard target)
- Impact: Upstream version bumps could silently change parsing, folding, or metadata
  behavior without this project's control; ADR 0004 (`execution-plan-folding-guard`)
  exists specifically to detect when SQL 4 CDS silently falls back to client-side
  evaluation instead of pushing a filter to Dataverse.
- Migration plan: None — this is the foundational architectural choice (ADR 0001).
  Mitigation is defensive: `ExecutionPlanAnalyzer`/`FoldingPolicy` detect regressions in
  folding behavior after a dependency upgrade, per `docs/adr/0004-execution-plan-folding-guard.md`.

**`Microsoft.PowerPlatform.Dataverse.Client` does not surface service-protection headers:**
- Risk: Verified by reading managed string literals out of the 1.2.2 assembly — it
  contains `Retry-After` but neither `ratelimit` nor `dop-hint`. This project's SOAP-based
  data path (via SQL 4 CDS) cannot observe these headers at all; the probe uses a separate
  Web API call.
- Files: `src/DataverseDuck/Diagnostics/ServiceProtectionBudget.cs`, `docs/service-protection.md`
- Impact: Budget information is only available as a point-in-time `doctor` reading, never
  attached to the actual query path that consumes the budget.
- Migration plan: None short of an SDK change upstream; documented as an accepted
  limitation.

## Test Coverage Gaps

**Certificate authentication (live):**
- What's not tested: The certificate-credential path has never executed against a real
  Dataverse tenant (see Tech Debt above).
- Files: `src/DataverseDuck/Sql4CdsConnectionFactory.cs`
- Risk: Any environment-specific certificate-store or chain-validation issue would only
  surface in a user's production environment, not in CI.
- Priority: Medium — feature is implemented and unit tested, only the live path is unverified.

**Interactive REPL (keyboard-driven):**
- What's not tested: History, multi-line editing, completion window rendering, and the
  terminal-size-fallback path in the REPL are not covered by automated tests (see Fragile
  Areas above).
- Files: `src/DataverseDuck.Cli/ReplLoop.cs`, `src/DataverseDuck.Cli/ReplSession.cs`
- Risk: Regressions in these paths would only be caught by manual testing, which is easy
  to skip under time pressure.
- Priority: Medium.

**Files with no dedicated test file:**
- What's not tested directly (though some are exercised indirectly through other tests):
  `AccessTokenClaims`, `CheckResult`, `ColumnMapping`, `DataverseCredential`,
  `DataverseOptions`, `DataverseSchemaMapper` (mapper logic is covered by
  `SchemaMapperTests.cs` — naming mismatch only), `DataverseThrottling`,
  `DuckDbBulkLoader`, `EnvironmentDoctor`, `EnvironmentTenant`, `FoldingPolicy`,
  `IDataverseQuerySource`, `MetadataCapture`, `PlanAnalysis`, `SnapshotMetadataCache`,
  `Sql4CdsConnectionFactory`, `Sql4CdsQuerySource`.
- Files: `src/DataverseDuck/**/*.cs` vs `tests/DataverseDuck.Tests/*.cs` (see file listing)
- Risk: Note many of these are covered indirectly by feature-oriented test files (e.g.
  `DataverseCredential` via `ProfileTests.cs`/`EnvironmentSetupTests.cs`,
  `DataverseThrottling` via `ResilienceTests.cs`, `FoldingPolicy`/`PlanAnalysis` via
  `ExecutionPlanAnalyzerTests.cs`) — this is a naming-convention gap more than a true
  coverage gap. `Sql4CdsQuerySource`, `Sql4CdsConnectionFactory`, and `IDataverseQuerySource`
  are the ones most plausibly under-tested directly, since they require a live connection
  and are largely thin wrappers exercised only in integration/live scenarios.
- Priority: Low — cross-check before assuming a real gap; verify via test file contents
  rather than filename matching alone.

**`DataverseThrottling.FromErrorCode` against a genuine live fault:**
- What's not tested: The full path from a real Dataverse 429 response to
  `DataverseThrottling`'s explanation has never executed (see Known Bugs above) — only
  unit tests with constructed fault text, and manual error-code injection in
  `spikes/ThrottleSpike/Program.cs`.
- Files: `src/DataverseDuck/DataverseThrottling.cs`, `tests/DataverseDuck.Tests/ResilienceTests.cs`
- Risk: A subtle mismatch between the constructed test fault and a real SOAP fault's
  shape (message format, error code encoding) would not be caught until a user hits it.
- Priority: Low — three deliberate live attempts failed to provoke the fault, and forcing
  it further risks denial-of-service against a real environment (explicitly called out in
  `spikes/ThrottleSpike/README.md`).

**Aggregate query limit discrepancy is unexplained, not just untested:**
- What's not tested: Whether `COUNT(*)` is actually exempt from the 50,000-row aggregate
  limit, or whether the limit is simply unenforced in this tenant.
- Files: `docs/large-tables.md`, `README.md` ("Open" section)
- Risk: Code or documentation that assumes the 50,000 limit is authoritative (e.g. any
  future chunking logic keyed off that number) could be built on a wrong assumption.
- Priority: Low-Medium — worth confirming against a second Dataverse environment before
  relying on the number anywhere in code.

---

*Concerns audit: 2026-08-17*
