# Testing Patterns

**Analysis Date:** 2026-08-17

## Test Framework

**Runner:**
- xUnit 2.9.3 (`tests/DataverseDuck.Tests/DataverseDuck.Tests.csproj`), with `xunit.runner.visualstudio` 3.1.4 and `Microsoft.NET.Test.Sdk` 17.14.1.
- Target framework `net10.0`, same as the main library.
- Global usings simplify test files: `<Using Include="Xunit" />` is set in the `.csproj`, so test files never write `using Xunit;` explicitly.
- Single test project for the whole solution: `tests/DataverseDuck.Tests/`, referencing both `src/DataverseDuck/DataverseDuck.csproj` and `src/DataverseDuck.Cli/DataverseDuck.Cli.csproj` directly (no separate unit/integration test assemblies).

**Assertion Library:**
- xUnit's built-in `Assert` class exclusively. No FluentAssertions, Shouldly, or similar. No mocking framework (`Moq`, `NSubstitute`, `FakeItEasy`) is referenced anywhere — mocks are done by hand (see Mocking section).

**Coverage tooling:**
- `coverlet.collector` 6.0.4 is referenced but coverage is not enforced by any script/CI config in the repo (no `.github/workflows` exist).

**Run Commands:**
```bash
dotnet test                 # runs the whole suite; README states 364 tests, no live Dataverse tenant required
dotnet test --filter "FullyQualifiedName~CacheManifestTests"   # run a single class
dotnet test --collect:"XPlat Code Coverage"                    # coverage via coverlet.collector
```
- `README.md` documents the expected count (`dotnet test # 364 tests, no tenant required`, line ~110 and ~476) — treat a materially different count after a change as a signal that tests were accidentally skipped/added without intention.

## Test File Organization

**Location:**
- Flat directory, not co-located with source: all tests live directly in `tests/DataverseDuck.Tests/` regardless of which `src/` subfolder the class-under-test lives in (e.g. `src/DataverseDuck/Schema/DataverseSchemaMapper.cs` → `tests/DataverseDuck.Tests/SchemaMapperTests.cs`, no `Schema/` subfolder in the test project).

**Naming:**
- Test file = `{ClassUnderTest}Tests.cs`, e.g. `CacheManifestTests.cs`, `DataverseCacheTests.cs`, `KeySetPushdownTests.cs`.
- Some files hold multiple related test classes covering different facets of one area, e.g. `tests/DataverseDuck.Tests/ReplTests.cs` contains `ReplStatementTests` and `ReplCompletionTests`; `tests/DataverseDuck.Tests/KeySetPushdownTests.cs` contains `KeySetPushdownTests` and `ChainedPushdownTests`; `tests/DataverseDuck.Tests/DataversePlanParserTests.cs` contains `PlanEndToEndTests`.
- Some test files reflect a *behavior*, not a class name: `tests/DataverseDuck.Tests/ResilienceTests.cs` and `tests/DataverseDuck.Tests/DateTimeBehaviorTests.cs` group tests around a cross-cutting concern (partial-load safety, timezone behavior) rather than one class.

**Structure:**
```
tests/DataverseDuck.Tests/
├── DataverseDuck.Tests.csproj
├── CacheManifestTests.cs
├── CaptureArgumentsTests.cs
├── DataverseCacheTests.cs
├── DataversePlanParserTests.cs
├── DateTimeBehaviorTests.cs
├── DotEnvFileTests.cs
├── EnvironmentSetupTests.cs
├── ExecutionPlanAnalyzerTests.cs
├── JsonTimestampInspectorTests.cs
├── KeySetPushdownTests.cs
├── MetadataSnapshotTests.cs
├── ProfileTests.cs
├── ReplTests.cs
├── ResilienceTests.cs
├── ResultWriterTests.cs
├── SchemaMapperTests.cs
├── ServiceProtectionBudgetTests.cs
└── UtcTimestampPolicyTests.cs
```

## Test Structure

**Suite organization:** one `public class {Name}Tests` per concern, `[Fact]` for a single scenario, `[Theory]`/`[InlineData]` for parameterized variants of the same assertion.

```csharp
// tests/DataverseDuck.Tests/CacheManifestTests.cs
/// <summary>
/// The manifest exists so a reused .duckdb file can be trusted. These tests
/// hold it to that: it must say what a table is, must not survive a load that
/// rolled back, and must mark a {{ }} table as partial.
/// </summary>
public class CacheManifestTests
{
    private static readonly Guid AcmeId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void Records_what_a_cached_table_is()
    {
        using var connection = UtcTimestampPolicy.OpenConnection("Data Source=:memory:");
        var cache = new DataverseCache(connection, new AllAccountsSource());

        cache.Cache("SELECT accountid, name FROM account", "crm_account");

        var entry = Assert.Single(CacheManifest.Read(connection));
        Assert.Equal("crm_account", entry.Name);
        Assert.False(entry.IsPartial);
    }
}
```

**Class-level doc comments:** every test class carries a `<summary>` stating the invariant/risk the file protects — read it before adding a test to understand what NOT to duplicate. This is a hard convention; new test classes should follow it.

**Theory pattern:**
```csharp
// tests/DataverseDuck.Tests/UtcTimestampPolicyTests.cs
[Theory]
[InlineData("UTC")]
[InlineData("Europe/Berlin")]
[InlineData("America/New_York")]
public void JsonTimestampToUtc_is_stable_regardless_of_session_timezone(string tz)
{
    // ...
}
```
- 24 `[Theory]` tests vs 283 `[Fact]` tests across the suite — prefer `[Fact]` unless testing the same assertion against several concrete input values (timezones, statement variants, error codes).

**Setup/Teardown:**
- No shared base class or `[ClassFixture]`/`IClassFixture<T>` framework fixture is used anywhere in the suite.
- Classes needing cleanup implement `IDisposable` directly and dispose a connection or delete a temp file in `Dispose()`: `public class ResilienceTests : IDisposable` (`tests/DataverseDuck.Tests/ResilienceTests.cs:57`), also `DotEnvFileTests`, `JsonTimestampInspectorTests`, `KeySetPushdownTests`, `ProfileTests`, `DataversePlanParserTests` (its `PlanEndToEndTests` nested class).
```csharp
public class ResilienceTests : IDisposable
{
    private readonly DuckDB.NET.Data.DuckDBConnection _connection =
        UtcTimestampPolicy.OpenConnection("Data Source=:memory:");

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }
}
```
- For tests needing a scratch file rather than a class-scoped resource, `try`/`finally` inside the test method is used instead of `IDisposable` (see Fixtures section).

## Mocking

**No mocking framework is used.** Every "mock" is a small hand-written class implementing the relevant interface, defined `internal sealed class ... : IDataverseQuerySource` either as a private nested class inside the test class or as a top-level `internal` class in the same file.

```csharp
// tests/DataverseDuck.Tests/DataverseCacheTests.cs
internal sealed class FakeQuerySource(DataTable table, PlanAnalysis? plan = null) : IDataverseQuerySource
{
    // implements just enough of IDataverseQuerySource to drive the scenario
}
```

```csharp
// tests/DataverseDuck.Tests/CacheManifestTests.cs (private nested fakes)
private sealed class AllAccountsSource : IDataverseQuerySource { /* ... */ }
private sealed class ThrowingSource : IDataverseQuerySource { /* always throws, to test rollback */ }
```

```csharp
// tests/DataverseDuck.Tests/ResilienceTests.cs — a fake that fails partway through enumeration
internal sealed class FailingReader(DataTable table, int failAfter, Exception failure) : DbDataReader
{
    public override bool Read()
    {
        if (_read == failAfter) throw failure;
        _read++;
        return _inner.Read();
    }
    // ... delegates every other member to an in-memory DataTable reader
}
```

Other fakes seen: `KeyedQuerySource`, `FailOnSecondBatchSource`, `TwoTableSource` (`tests/DataverseDuck.Tests/KeySetPushdownTests.cs`), `ThrowingQuerySource` (`tests/DataverseDuck.Tests/ResilienceTests.cs`).

**What to fake:** the seam is always a small first-party interface (`IDataverseQuerySource`) or an abstract framework base class that's cheap to subclass (`DbDataReader`). Never fake DuckDB itself — an in-memory `DuckDBConnection` (`Data Source=:memory:`) is used as a real dependency in nearly every test (see `UtcTimestampPolicy.OpenConnection` calls throughout).

**What NOT to mock:** DuckDB connections and `System.Data.DataTable`/`DbDataReader` plumbing are used for real, in-memory, rather than mocked — the codebase treats DuckDB-in-memory as fast and reliable enough to be a real test dependency, not a mock target. Follow this: add a new `IDataverseQuerySource` fake for Dataverse-side seams, but exercise the real `DuckDBConnection`.

## Fixtures and Factories

**In-memory DuckDB connection** is the standard fixture, created directly in each test (or in a field for `IDisposable` classes) via the shared helper:
```csharp
using var connection = UtcTimestampPolicy.OpenConnection("Data Source=:memory:");
```
`UtcTimestampPolicy.OpenConnection` (`src/DataverseDuck/UtcTimestampPolicy.cs`) centralizes DuckDB connection setup (timezone/session config) so tests get production-identical connection behavior.

**Temp files:** created inline with a GUID-suffixed name under `Path.GetTempPath()`, cleaned up via `try`/`finally` or `IDisposable.Dispose()`:
```csharp
var path = Path.Combine(Path.GetTempPath(), $"manifest-{Guid.NewGuid():N}.json");
File.WriteAllText(path, """[{"id": 1}]""");
try
{
    // ... test body
}
finally
{
    File.Delete(path);
}
```
Seen consistently across `CacheManifestTests.cs`, `DataverseCacheTests.cs`, `DataversePlanParserTests.cs`, `DotEnvFileTests.cs`, `JsonTimestampInspectorTests.cs`, `KeySetPushdownTests.cs`, `MetadataSnapshotTests.cs`. Naming convention for the temp file prefix mirrors the feature under test (`dvduck-`, `dvduck-plan-`, `dvduck-chain-`, `manifest-`, `dotenv-`).

**Test data:** built inline per test using small `Dictionary<Guid, ...>` literals or a `DataTable` builder helper method local to the test class, e.g. `Rows(int count, string note = "row")` in `tests/DataverseDuck.Tests/ResilienceTests.cs` builds an in-memory `DataTable`. No external fixture files, no JSON/XML golden files, no test data builders shared across test classes — everything is self-contained per file.

**Fixed GUIDs:** well-known GUIDs are declared as `private static readonly Guid` constants at the top of the test class for readability (`AcmeId`, `ContosoId` in `tests/DataverseDuck.Tests/CacheManifestTests.cs`), rather than `Guid.NewGuid()`, when the test asserts on specific identity.

## Coverage

**Requirements:** No coverage threshold is enforced anywhere (no CI config exists at all — no `.github/workflows/`). Coverage is opt-in via coverlet.

**View Coverage:**
```bash
dotnet test --collect:"XPlat Code Coverage"
# then inspect the generated coverage.cobertura.xml under TestResults/
```

## Test Types

**Unit tests:** the overwhelming majority. Each test exercises one class/static method directly, with fakes for the Dataverse-facing interface and a real in-memory DuckDB connection for the storage-facing side.

**Integration-style tests:** a few files exercise multi-step flows end-to-end within the process — `PlanEndToEndTests` (nested in `tests/DataverseDuck.Tests/DataversePlanParserTests.cs`) parses a full `WITH ... DATAVERSE (...)` plan and runs it against the fake source and a real DuckDB connection. `tests/DataverseDuck.Tests/ChainedPushdownTests` (in `KeySetPushdownTests.cs`) chains multiple pushdown steps together. These are still fully in-process and require no live tenant.

**Live/manual verification:** README (`README.md`) documents a separate tier of manual verification against a real Dataverse tenant (`✅ Built, N tests, verified live` annotations in the feature table, lines ~41-56) — this is NOT automated and NOT part of `dotnet test`; it's tracked informally in README prose, not in code. When a feature needs live verification, note it in README rather than adding a live-tenant test to the suite (the suite is explicitly "no tenant required").

**E2E tests:** none — there is no browser/HTTP end-to-end layer; the CLI (`dvduck`) is tested via its statement-parsing/session logic directly (`ReplStatementTests`, `ReplCompletionTests` in `tests/DataverseDuck.Tests/ReplTests.cs`) using `PrettyPrompt`'s `IPromptCallbacks` test seam rather than spawning the actual process.

## Common Patterns

**Testing thrown exceptions:**
```csharp
Assert.ThrowsAny<Exception>(() =>
    cache.Cache("SELECT accountid FROM account", "crm_account"));

Assert.Throws<TimeoutException>(() => loader.Load(
    new FailingReader(Rows(1000), 600, new TimeoutException("dropped")), "crm_account"));
```
- `Assert.Throws<TSpecific>` when the exact exception type matters; `Assert.ThrowsAny<Exception>` when only "it failed" matters and the specific wrapping type is incidental (e.g. `tests/DataverseDuck.Tests/CacheManifestTests.cs` — the point is that no manifest row is written, not which exception type DuckDB raises for a missing table).

**Behavioral assertions over structural ones:** assertions frequently check a downstream *consequence* rather than a direct return value, with a comment stating the risk being guarded against:
```csharp
// The dangerous outcome would be a queryable table holding 600 rows.
var missing = Assert.ThrowsAny<Exception>(() => Count("crm_account"));
Assert.Contains("crm_account", missing.Message);
```

**Single-row assertions:** `Assert.Single(collection)` / `Assert.Single(collection, predicate)` is the standard way to assert "exactly one matching row exists" against `CacheManifest.Read(connection)` and similar query results.

**Parameterized statement recognition tests:** for parser/grammar-like logic, list multiple `[InlineData]` variants covering case sensitivity, whitespace, and near-miss inputs that must NOT match (`tests/DataverseDuck.Tests/ReplTests.cs` — `APlanIsRecognised`, `PlainSqlIsNotAPlan`, `AKeywordInTheBodyDoesNotMakeAPlan`).

---

*Testing analysis: 2026-08-17*
