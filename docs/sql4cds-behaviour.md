# SQL 4 CDS engine behaviour (measured)

Facts about `MarkMpn.Sql4Cds.Engine` 10.4.4 that are not in its documentation, obtained by
reflecting over the assembly and constructing a real connection against a stub
`IOrganizationService`. They are recorded because several of them are load-bearing, and
two would cause silent data corruption if they changed.

## Connection defaults

Read from a live `Sql4CdsConnection`, not from documentation:

| Property | Default | Why it matters |
|---|---|---|
| `UseTDSEndpoint` | **`true`** | Must be forced off. The TDS endpoint cannot authenticate a service principal (ADR 0001), so leaving the default would break every headless run. |
| `UseLocalTimeZone` | `false` | Correct for us, but set explicitly: flipping it shifts every datetime out of UTC without an error (ADR 0002). |
| `ReturnEntityReferenceAsGuid` | `false` | Lookups therefore arrive as `EntityReference`, which the DuckDB appender cannot store. |
| `QuotedIdentifiers` | `true` | |
| `BatchSize` | `100` | |
| `MaxDegreeOfParallelism` | `10` | Relevant to service protection limits: 10 concurrent requests against a ~52 concurrent cap. |
| `ColumnOrdering` | `Strict` | |

`Sql4CdsConnectionFactory` sets the first three explicitly rather than relying on any of
them, because all three fail silently rather than loudly.

## Type mapping

The reader surfaces CLR types via `SqlTypeConverter.SqlToNetType`. The full mapping:

| Engine type | CLR type surfaced |
|---|---|
| `SqlString`, `SqlXml` | `String` |
| `SqlByte` | `Byte` |
| `SqlInt16` / `SqlInt32` / `SqlInt64` | `Int16` / `Int32` / `Int64` |
| `SqlBoolean` | `Boolean` |
| `SqlDecimal`, `SqlMoney` | `Decimal` |
| `SqlSingle` / `SqlDouble` | `Single` / `Double` |
| `SqlGuid` | `Guid` |
| `SqlBinary` | `Byte[]` |
| `SqlDateTime`, `SqlDate`, `SqlDateTime2`, `SqlSmallDateTime` | `DateTime` |
| **`SqlDateTimeOffset`** | **`DateTimeOffset`** |
| `SqlTime` | `TimeSpan` |
| **`SqlEntityReference`** | **`EntityReference`** |

Two entries drive `DataverseSchemaMapper`:

- **`DateTimeOffset`** is the one type that carries a timezone into the pipeline. It is
  normalised to a naive UTC `DateTime` before reaching the appender, because ADR 0002
  bans timezone-aware values from the cache.
- **`EntityReference`** has no appender overload. It is decomposed into a `UUID` id column
  plus an optional `_entitytype` companion column. The companion is not decoration:
  polymorphic lookups (`customerid`, `ownerid`, `regardingobjectid`) can point at more
  than one table, so the id alone does not identify a row.

**`OptionSetValue` and `Money` never reach the reader** — the engine folds them to `Int32`
and `Decimal` internally. The mapper handles them anyway, for values arriving directly
from `IOrganizationService` rather than through SQL.

## Connection construction

Constructing a `Sql4CdsConnection` issues `RetrieveVersion` before anything else, and
reads the `organization` row for collation. Any stub `IOrganizationService` used in tests
must serve both, or construction throws before a query is ever compiled.

## Reproducing

These were obtained by loading the assembly and invoking
`SqlTypeConverter.SqlToNetType(Type)`, which is public and static. Re-run that probe after
a package upgrade; the mapper's tests pin the resulting behaviour, so a change will fail
the suite rather than corrupt the cache.
