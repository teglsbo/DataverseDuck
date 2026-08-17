# Coding Conventions

**Analysis Date:** 2026-08-17

## Language & Project Baseline

- C# on **.NET 10** (`<TargetFramework>net10.0</TargetFramework>` in every `.csproj`, e.g. `src/DataverseDuck/DataverseDuck.csproj`, `src/DataverseDuck.Cli/DataverseDuck.Cli.csproj`, `tests/DataverseDuck.Tests/DataverseDuck.Tests.csproj`).
- `<ImplicitUsings>enable</ImplicitUsings>` and `<Nullable>enable</Nullable>` are set on every project — write nullable-aware code (`string?`, `is null`/`is not null` checks, null-forgiving only when justified).
- No `.editorconfig` is present in the repo. There is no `dotnet format` / analyzer ruleset checked in — conventions are enforced by review/consistency, not tooling. Do not assume analyzer-driven style rules; follow the patterns documented below by reading nearby code.
- Shared MSBuild metadata lives in `Directory.Build.props` and `Directory.Build.targets` (repo root) — package metadata, `IsPackable=false` default (only `DataverseDuck` and `DataverseDuck.Cli` opt in), and a pinned transitive `System.Security.Cryptography.Pkcs` version with a comment explaining the CVE it patches. Follow this pattern (comment above any pinned/unusual dependency explaining *why*) when adding similar overrides.

## Naming Patterns

**Namespaces:**
- File-scoped namespace declarations everywhere: `namespace DataverseDuck;` (e.g. `src/DataverseDuck/CacheManifest.cs:3`). Never use block-scoped `namespace X { }`.
- Root library namespace is `DataverseDuck`; subfolders map 1:1 to sub-namespaces (`DataverseDuck.Configuration`, `DataverseDuck.Diagnostics`, `DataverseDuck.Metadata`, `DataverseDuck.Plans`, `DataverseDuck.Schema`). CLI project uses `DataverseDuck.Cli` (set explicitly via `<RootNamespace>` in `src/DataverseDuck.Cli/DataverseDuck.Cli.csproj`).
- Test namespace is flat: `DataverseDuck.Tests` for every test file regardless of which folder the class-under-test lives in.

**Types:**
- PascalCase for classes, records, interfaces, enums, methods, properties.
- Interfaces prefixed `I` (`IDataverseQuerySource` in `src/DataverseDuck/IDataverseQuerySource.cs`).
- Most public types are `public sealed class` or `public sealed record` — seal by default unless a type is explicitly designed for inheritance. See `src/DataverseDuck/CacheManifest.cs` (`public sealed record CacheEntry(...)`), `src/DataverseDuck/DataverseThrottling.cs` (`public sealed class DataverseThrottledException(...)`).
- Static utility classes are `public static class` (e.g. `CacheManifest` in `src/DataverseDuck/CacheManifest.cs`).

**Fields & Locals:**
- Private instance fields: `_camelCase` (e.g. `_connection`, `_source`, `_options`, `_entities` — see `src/DataverseDuck/DataverseCache.cs`, `src/DataverseDuck/Diagnostics/EnvironmentDoctor.cs:25`).
- `const` fields are PascalCase (`public const string TableName = "dvduck_manifest";` in `src/DataverseDuck/CacheManifest.cs`).
- Local variables and lambda parameters are camelCase.

**Test Methods:**
- Test method names are full English sentences in `Pascal_Case_With_Underscores_As_Spaces`, first letter capitalized, describing behavior/outcome, not "MethodName_Scenario_Result" style: `Records_what_a_cached_table_is`, `Marks_a_key_filtered_table_as_partial`, `Does_not_record_a_load_that_failed` (`tests/DataverseDuck.Tests/CacheManifestTests.cs`). `A_load_that_dies_partway_leaves_no_table_behind` (`tests/DataverseDuck.Tests/ResilienceTests.cs`).
- Test class names mirror the class under test plus `Tests`: `CacheManifestTests`, `DataverseCacheTests`, `SchemaMapperTests`, `KeySetPushdownTests`.

## Code Style

**Formatting:**
- No formatter config committed (no `.editorconfig`, no `omnisharp.json`). Match surrounding file style exactly: 4-space indent, braces on new line for types/methods, opening brace on same line for control-flow blocks is inconsistent — check the specific file being edited before assuming a rule.
- Expression-bodied members are used heavily for simple getters/computed properties: `public bool IsPartial => KeyCount is not null;` (`src/DataverseDuck/CacheManifest.cs`).
- Switch expressions (not statements) are the preferred way to branch on classification logic: see `Humanise` in `src/DataverseDuck/CacheManifest.cs` and `Classify`-style methods in `src/DataverseDuck/DataverseThrottling.cs`.
- Primary constructors are used for small classes that just capture dependencies, including internal test fakes: `internal sealed class FailingReader(DataTable table, int failAfter, Exception failure) : DbDataReader` (`tests/DataverseDuck.Tests/ResilienceTests.cs`), `public sealed class EnvironmentDoctor(DataverseOptions options)` (`src/DataverseDuck/Diagnostics/EnvironmentDoctor.cs`), `public sealed class DataverseThrottledException(string message, Exception inner) : Exception(message, inner)` (`src/DataverseDuck/DataverseThrottling.cs:160`).
- Raw string literals (`"""..."""`) are used for embedded SQL and JSON fixtures: `private const string CreateSql = $"""..."""` (`src/DataverseDuck/CacheManifest.cs`), `File.WriteAllText(path, """[{"id": 1}]""");` (`tests/DataverseDuck.Tests/CacheManifestTests.cs`).
- Collection expressions (`[]`) are used for empty/default collections: `public IReadOnlyList<string> ExpectedTables { get; init; } = [];` (`src/DataverseDuck/Diagnostics/EnvironmentDoctor.cs`).

**Linting:**
- No ESLint/Roslyn analyzer package is referenced in any `.csproj`. `NuGetAudit` is enabled globally (`Directory.Build.props`) with `<NuGetAuditMode>all</NuGetAuditMode>` — this audits transitive packages, not just direct ones, and is the one automated "quality gate" in the repo. When adding a dependency, check `dotnet list package --vulnerable --include-transitive` and pin/patch transitive vulnerabilities the way `System.Security.Cryptography.Pkcs` is pinned in `Directory.Build.props`.

## Documentation Comments

- XML doc comments (`///`) are pervasive and are written as **prose explaining "why", not just "what"** — this is a strong, deliberate convention. Example: `src/DataverseDuck/CacheManifest.cs` has a multi-paragraph `<summary>` on the `CacheManifest` class explaining the risk of partial-vs-complete tables being indistinguishable by inspection. Same on `src/DataverseDuck/Diagnostics/EnvironmentDoctor.cs`.
- Record components document each parameter individually via `<param>` tags, including non-obvious semantics (e.g. `KeyCount` doc explains that non-null is "the marker that this table is a *subset*" — `src/DataverseDuck/CacheManifest.cs`).
- Test classes carry a `<summary>` explaining *why the test file exists* / what invariant it protects, not what methods it contains: see the `<summary>` on `CacheManifestTests` (`tests/DataverseDuck.Tests/CacheManifestTests.cs:8-12`) and `ResilienceTests` (`tests/DataverseDuck.Tests/ResilienceTests.cs:9-11`).
- Inline `//` comments inside test bodies explain the *intent* of an assertion when it isn't obvious from the assertion itself, e.g. `// The distinction the manifest exists for: this table looks exactly like a full copy of account, but holds one row on purpose.` (`tests/DataverseDuck.Tests/CacheManifestTests.cs`). Follow this pattern: comment on *why this specific assertion matters*, not on mechanics.
- When adding a new public type or non-trivial method, write the "why" first in the summary — the codebase treats comments as design-rationale documentation, closer to lightweight ADRs than boilerplate.

## Error Handling

**Guard clauses:**
- Null/argument validation is done inline at the point of assignment using `??` combined with `throw`, not separate `if` blocks: `_options = options ?? throw new ArgumentNullException(nameof(options));` (`src/DataverseDuck/Diagnostics/EnvironmentDoctor.cs:25`), `Connection = connection ?? throw new ArgumentNullException(nameof(connection));` (`src/DataverseDuck/DataverseCache.cs:59`). Use this exact pattern for constructor argument validation.

**Exception types (in order of frequency in `src/`):**
- `FormatException` — malformed input/query syntax (14 uses), primarily in `src/DataverseDuck/DataversePlanParser.cs` and `src/DataverseDuck/KeySetPushdown.cs` for the custom `WITH` plan DSL.
- `InvalidOperationException` — invalid state/sequencing errors (10 uses), e.g. `src/DataverseDuck.Cli/ReplSession.cs`, `src/DataverseDuck/Sql4CdsConnectionFactory.cs:64`.
- `ArgumentNullException` / `ArgumentException` — constructor/parameter validation (13 uses combined), always with `nameof(...)`.
- Custom domain exceptions are defined where the failure is a first-class concept the caller needs to inspect/catch specifically:
  - `PlanNotFoldedException` (`src/DataverseDuck/Plans/ExecutionPlanAnalyzer.cs:157`) — thrown when a query plan cannot be safely pushed down.
  - `DataverseThrottledException` (`src/DataverseDuck/DataverseThrottling.cs:160`) — wraps a Dataverse 429/throttle fault, exposes `Kind` and `RetryAfter` as derived properties rather than storing them, computed from `InnerException` on access.
- Follow this pattern when introducing a new failure mode that callers need to branch on: define a `sealed class FooException(...) : Exception(...)` with a primary constructor, and put semantic accessors (`Kind`, `RetryAfter`-style) as properties computed from the wrapped exception, not stored fields.

**No generic catch-and-swallow:** grep across `src/` shows no `catch { }` empty-block patterns; exceptions propagate unless a method's whole purpose is to interpret one (e.g. `DataverseThrottling.Classify`).

## Function & Type Design

**Records vs classes:**
- Use `record` for immutable data-carrying types with value semantics (`CacheEntry` in `src/DataverseDuck/CacheManifest.cs`, plan/analysis result types in `src/DataverseDuck/Plans/PlanAnalysis.cs`).
- Use `sealed class` for services/behavior-carrying types (readers, loaders, factories, analyzers) — these dominate the codebase (46 `record` files vs but most substantive logic lives in `sealed class` — check counts: 16 files with `sealed class` markers at repo scan time).
- Primary constructors are the default for dependency-holding classes; use a full constructor body only when validation logic is non-trivial (multi-line) or multiple fields need cross-validation.

**Method size & structure:**
- Public API methods (e.g. `EnvironmentDoctor.RunAsync` in `src/DataverseDuck/Diagnostics/EnvironmentDoctor.cs`) are kept short by delegating each logical check to a private method returning a `CheckResult`, assembled into a list — prefer decomposing multi-step workflows into named private steps over one long method.
- Early-return guard style is preferred over nested conditionals: `if (actual is null) { return CheckResult.Skip(...); }` before the "happy path" continues (`src/DataverseDuck/Diagnostics/EnvironmentDoctor.cs`).

**Async:**
- Async methods are suffixed `Async` and accept `CancellationToken cancellationToken = default` as the last parameter (`RunAsync`, `CheckEnvironmentTenantAsync` in `src/DataverseDuck/Diagnostics/EnvironmentDoctor.cs`).

## Module Design

**File organization:**
- One primary public type per file, file named after the type (`CacheManifest.cs` contains `CacheEntry` + `CacheManifest`, both tightly coupled — acceptable to co-locate a small closely related record with its owning static class in the same file).
- Folders group by concern, not by type-kind: `Configuration/`, `Diagnostics/`, `Metadata/`, `Plans/`, `Schema/` under `src/DataverseDuck/` (see `README.md` "Repository layout" section for the authoritative description).

**Internals sharing:**
- Cross-project internal access uses `[InternalsVisibleTo("DataverseDuck.Tests")]` in both `src/DataverseDuck/DataverseDuck.csproj` and `src/DataverseDuck.Cli/DataverseDuck.Cli.csproj`, with a comment explaining why (`// The plan analyzer couples to internal engine type names; tests pin them.`). Prefer `internal` over `public` for implementation types that tests need but consumers should not, and document why in a comment above the `InternalsVisibleTo` entry if adding a new reason.

**No DI container:** Dependencies are passed via constructor parameters directly (no `Microsoft.Extensions.DependencyInjection` container is referenced anywhere in `src/`). Wiring happens explicitly in `src/DataverseDuck.Cli/Program.cs`.

---

*Convention analysis: 2026-08-17*
