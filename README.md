# dataverse-duck

Query Microsoft Dataverse tables and local JSON logs/state in a single SQL statement,
from C#, using [DuckDB](https://duckdb.org) and [SQL 4 CDS](https://github.com/MarkMpn/Sql4Cds).

## The problem

We need to join **JSON log files** and **JSON state documents** against **Dataverse
tables** — several tables, some of them large — authenticating with an **Entra ID app
registration** (MSAL client credentials, no interactive user).

DuckDB reads JSON natively, so that half is free. The work is getting Dataverse rows
into the same query without dragging whole tables across a rate-limited API.

## Approach

```
Entra app reg ──MSAL client credentials──► ServiceClient (IOrganizationService)
                                                  │
                                       Sql4CdsConnection (UseTDSEndpoint = false)
                                       T-SQL ──► FetchXML (server-side join + filter)
                                                  │ DbDataReader
                                          schema mapper ──► DuckDB DDL
                                            Appender ──► cache.duckdb
                                                  │
       read_json_auto('logs/*.json') ─────────────┴──► one DuckDB SQL query
```

SQL 4 CDS compiles T-SQL into FetchXML so joins and filters execute **inside Dataverse**.
Only the result set crosses the wire, then lands in DuckDB via the Appender
(measured at ~440k rows/sec) and joins against JSON locally.

See [`docs/adr`](docs/adr) for why this design and not the alternatives.

## Status

Working end to end against a real Dataverse environment.

| Component | State |
|---|---|
| `UtcTimestampPolicy` | ✅ Built, 18 tests |
| `DateTimeBehavior` mapping (DATE vs instant) | ✅ Built, 9 tests, verified live |
| `Sql4CdsConnectionFactory` | ✅ Built |
| `MetadataSnapshot` / `SnapshotMetadataCache` / `MetadataCapture` | ✅ Built, 12 tests |
| `dvduck doctor` / `dvduck capture` CLI | ✅ Built, 25 tests |
| `DataverseSchemaMapper` (reader → DDL) | ✅ Built, 31 tests |
| `DuckDbBulkLoader` (reader → Appender) | ✅ Built, 10 tests |
| `ExecutionPlanAnalyzer` (folding guard) | ✅ Built, 27 tests |
| `DataversePlanParser` (the `WITH` form) | ✅ Built, 28 tests |
| `DataverseCache` / key-set pushdown | ✅ Built, 28 tests |
| `.env` loading, `dvduck doctor` remedies | ✅ Built, 17 tests |
| `DataverseThrottling` (429 explanation) | ✅ Built, not yet provoked on a tenant |
| Cache manifest (`dvduck tables`) | ✅ Built, 8 tests |
| Verified against a live environment | ✅ All 8 doctor checks pass; two-hop JSON-to-Dataverse join verified |

## Installing

The command line tool:

```bash
dotnet tool install --global DataverseDuck.Cli
dvduck help
```

The library, for use from your own code:

```bash
dotnet add package DataverseDuck
```

Both are MIT. `dotnet pack -c Release -o out` builds them from source; the test project
and the spikes are excluded.

The library package carries a build target that **fails your build** if
`InvariantGlobalization` is `true`, with error `DVD001`. That property makes SQL 4 CDS
throw a `TypeInitializationException` naming nothing relevant, and it cost a live
debugging session to trace, so consumers are told at build time instead of at runtime.

## Quickstart

Requires .NET 10.

```bash
dotnet test          # 243 tests, no tenant required
```

### Connect to a real environment

Setting up headless access takes about 20 minutes and spans three portals.
Follow [docs/environment-setup.md](docs/environment-setup.md), then verify:

```bash
cp .env.example .env     # then fill it in; it is gitignored, chmod 600 it
dotnet run --project src/DataverseDuck.Cli -- doctor account contact
```

`doctor` exists because Dataverse setup failures are opaque — a missing application user
and a missing security role both surface as a bare authentication error, but the fixes are
in different places. It checks each link in the chain and tells you which one broke.

### Ask a question

A query names its own sources in one `WITH` block. How many contacts had webchat messages:

```bash
dvduck query --plan "
  WITH logs AS JSON ('webchat/*.json'),
       crm_contact AS DATAVERSE (
           SELECT contactid, fullname FROM contact
           WHERE contactid IN {{SELECT CAST(customer_id AS UUID)
                                FROM logs WHERE channel = 'webchat'}}
       )
  SELECT count(DISTINCT c.contactid)
  FROM logs l JOIN crm_contact c ON c.contactid = CAST(l.customer_id AS UUID)
  WHERE l.channel = 'webchat'"
```

Entries run top to bottom, before the final query. `JSON` registers a file or glob as a
view; `DATAVERSE` is **one round trip** that materialises a real table.

The `{{ }}` is the important part. It is an ordinary DuckDB query, evaluated locally
first, whose results are inlined as literals so Dataverse does the filtering. Without it
you would transfer the whole `contact` table to find the handful of rows your logs mention.

Because each entry can read everything above it, and a cached Dataverse table is just a
DuckDB table by then, narrowing **chains**:

```sql
WITH logs        AS JSON ('webchat/*.json'),
     crm_account AS DATAVERSE (SELECT accountid, name FROM account
                               WHERE accountid IN {{SELECT DISTINCT CAST(account_id AS UUID)
                                                    FROM logs}}),
     crm_contact AS DATAVERSE (SELECT contactid, fullname, parentcustomerid FROM contact
                               WHERE parentcustomerid IN {{SELECT accountid FROM crm_account}})
SELECT a.name, count(DISTINCT c.contactid)
FROM crm_account a JOIN crm_contact c ON c.parentcustomerid = a.accountid
GROUP BY a.name;
```

In the test that covers this, that fetches 2 of 5,000 accounts and then 4 of 10,000
contacts. Ordinary CTEs may sit in the same `WITH`; they are left to DuckDB and run with
the final query, so a `{{ }}` cannot read them — the parser says so rather than letting it
fail as "table not found".

Use `--plan-file plan.sql` to keep the SQL in a file. `--db cache.duckdb` persists the
fetched tables so a re-run costs nothing.

`--plan` is the only form. An earlier `--json` / `--cache` / `--run` flag form was removed
because it left the dependency order implicit in the order the flags happened to appear;
those flags now print the equivalent plan rather than a bare "unknown option".

### Ask about the schema

Dataverse metadata is queryable as ordinary tables — `metadata.entity`,
`metadata.attribute`, `metadata.relationship_1_n` (and `_n_1`, `_n_n`, `alternate_key`,
`value`). Nothing special is needed to use them: a `DATAVERSE` entry is T-SQL, so they
cache into DuckDB like any other table and can be searched, joined and exported.

```bash
dvduck query --db schema.duckdb --plan "
  WITH columns AS DATAVERSE (SELECT entitylogicalname, logicalname, attributetypename
                             FROM metadata.attribute WHERE entitylogicalname = 'contact')
  COPY (SELECT * FROM columns ORDER BY logicalname)
  TO 'contact-columns.json' (FORMAT JSON, ARRAY true)"
```

This is also the quickest way to see how wide these tables are: `account` has 215
attributes and `contact` 311, counting derived ones like `accountidname`. `SELECT *` is
rarely what you want.

**These queries need a live connection.** Run against a captured snapshot they fail with
`RetrieveMetadataChanges`: the engine answers metadata queries by calling the service and
ignores the injected metadata cache, even though the snapshot holds the same information.

### Know what is in a cache

A `{{ }}` table has the right name and the right columns and only *some* of the rows.
Nothing about it looks partial, so counting it as if it were the whole table gives a
plausible number that is wrong. Every load records what it was:

```console
$ dvduck tables --db cache.duckdb
crm_contact  4 rows matching 2 key(s), loaded 3h ago
    SELECT contactid, fullname, parentcustomerid FROM contact
    WHERE parentcustomerid IN {{SELECT accountid FROM crm_account}}
    Partial: only the rows those keys matched. Do not read it as the whole table.

logs  view, loaded 3h ago
    webchat/*.json
```

The manifest is written inside the load's own transaction, so it can never describe rows
that were rolled back (ADR 0009).

### How results are printed

Tab-separated to **stdout**: a header row, then the data. Timestamps print as
`yyyy-MM-dd HH:mm:ss` (naive UTC, per ADR 0002), `byte[]` as hex, NULL as an empty field.
Progress lines and the trailing `(2 row(s))` go to **stderr**, so `dvduck query ... > out.tsv`
gives a clean file.

TSV has no escaping, so a value containing a tab or a newline will break the layout, and
an empty string is indistinguishable from NULL. When either matters, let DuckDB write the
file instead — `COPY (...) TO 'out.json' (FORMAT JSON, ARRAY true)` works as the final
statement of a plan, as does `FORMAT CSV` or `FORMAT PARQUET`.

### Use the library

```csharp
using DataverseDuck;

// Service principal: no interactive login.
using var crm = Sql4CdsConnectionFactory.CreateWithClientSecret(
    new Uri("https://yourorg.crm.dynamics.com"), clientId, clientSecret);

// Opens DuckDB with the session pinned to UTC.
using var duck = UtcTimestampPolicy.OpenConnection("Data Source=cache.duckdb");
```

**Do not set `InvariantGlobalization` to `true`** in a project that uses this library.
SQL 4 CDS builds SQL Server collations in a static constructor that needs real culture
data; without it the engine fails to initialise with a `TypeInitializationException`
naming none of this. The package enforces this at build time (`DVD001`).

Then join the two worlds:

```sql
SELECT a.name, j.level, count(*) AS n
FROM read_json_auto('logs/*.json') j
JOIN crm_accounts a ON a.accountid = CAST(j.account_id AS UUID)
GROUP BY a.name, j.level;
```

## Large tables

Everything goes through `RetrieveMultiple` with FetchXML: Synapse Link and Fabric Link
need infrastructure we do not have, and the TDS endpoint does not support service
principal authentication (ADR 0001).

Measured against a live environment: about **8,000 rows/second** for narrow columns, and
key-set pushdown is free up to roughly 1,000 keys but superlinear above that. Pushing
10,000 keys costs the same as scanning a 56,000-row table, so pushdown stops paying at
about one key per five rows. Above 10,000 keys, chunked requests of ~2,000 are more than
twice as fast as one large `IN`.

The documented 500-condition FetchXML limit does not apply to an `IN` list: SQL 4 CDS
folds it into a single condition, and 56,000 keys in one query still ran.

See [docs/large-tables.md](docs/large-tables.md) for the numbers and the official limits.

## The folding guard

SQL 4 CDS pushes joins and filters into Dataverse as FetchXML where it can, and **silently
falls back** to fetching both tables and joining locally where it cannot. Results stay
correct; the query just starts moving millions of rows across the network.

Because the plan can be compiled without executing it, this is caught before it costs
anything:

```csharp
var analyzer = new ExecutionPlanAnalyzer { LargeRowThreshold = 10_000 };
analyzer.Enforce(command, FoldingPolicy.RejectCritical, log: Console.Error.WriteLine);
```

Severity scales with estimated rows: a client-side join over 200 rows is irrelevant, the
same plan over 2,000,000 rows is the failure this project exists to avoid. See
[ADR 0004](docs/adr/0004-execution-plan-folding-guard.md).

## Timestamps

Timestamp handling is the sharpest edge in this project, because Dataverse, DuckDB and
JSON logs each have different defaults. Every rule in `UtcTimestampPolicy` was measured,
not assumed — see [ADR 0002](docs/adr/0002-utc-naive-timestamps.md).

The short version: **every timestamp in the cache is a naive `TIMESTAMP` holding UTC**,
and the DuckDB session is pinned to UTC. Never let a `TIMESTAMPTZ` into the cache.

With one exception, and it needs metadata to see. Only Dataverse's `UserLocal` behaviour
is an instant. `DateOnly` and `TimeZoneIndependent` are wall-clock readings: a birthdate
of 1980-05-15 is that date everywhere, and shifting it by an offset makes it the 14th for
anyone west of UTC. Those become `DATE` and unconverted `TIMESTAMP`.

The reader cannot tell the three apart — measured live, all arrive as `DateTime` with
`Kind=Unspecified` — so the mapper resolves each column's originating attribute from the
reader's schema table. Give it metadata to get this; without it every datetime is treated
as an instant, which is right for the large majority of columns. Note `Format` is *not*
behaviour: stock attributes exist that are `Format=DateOnly` with `Behavior=UserLocal`.
See [ADR 0010](docs/adr/0010-datetime-behaviour.md).

## Offline development

SQL 4 CDS needs entity metadata to compile SQL. Synthesising it by hand does not work.
Capture it once from a live environment, then replay it offline:

```csharp
// Online, once:
MetadataCapture.CaptureToFile(service, ["account", "contact"], "metadata/snapshot.bin");

// Offline, thereafter:
var cache = SnapshotMetadataCache.FromFile("metadata/snapshot.bin");
```

See [ADR 0003](docs/adr/0003-metadata-snapshot-for-offline-work.md).

## Repository layout

```
src/DataverseDuck/          Library
  UtcTimestampPolicy.cs     Timestamp rules and guard rails
  Sql4CdsConnectionFactory.cs
  Configuration/            Connection settings and validation
  Schema/                   Reader -> DuckDB DDL, and the bulk loader
  Plans/                    Execution plan analysis and the folding guard
  Diagnostics/              Environment checks behind 'dvduck doctor'
  Metadata/                 Snapshot capture, storage and offline cache
src/DataverseDuck.Cli/      'dvduck' command line tool
tests/DataverseDuck.Tests/  243 tests, no tenant required
spikes/                     Throwaway experiments that produced the evidence
docs/environment-setup.md   Getting headless access to Dataverse
docs/large-tables.md        Measured limits, and where key-set pushdown stops paying
docs/sql4cds-behaviour.md   Measured engine defaults and type mapping
docs/adr/                   Architecture decision records
Directory.Build.props       Shared package metadata; nothing packs unless it opts in
```

## Spikes

`spikes/` holds the throwaway programs used to measure behaviour rather than assume it.
They are kept because the ADRs cite them as evidence. They are not part of the build.

| Spike | Question | Outcome |
|---|---|---|
| `AppenderSpike` | Which CLR types does the DuckDB Appender accept? | 16/16 types, ~440k rows/sec |
| `PushdownSpike` | Does DuckDB push join keys into a table function? | Only as a range above ~50 keys (ADR 0007) |
| `SeedData` | Give a live environment known rows to join against | 2 accounts, 4 contacts, fixed GUIDs |
| `MetadataSpike` | Can SQL 4 CDS run without a live connection? | Yes, but metadata must be captured, not synthesised |
| `TimezoneSpike` | How do timestamps behave across the seam? | Six rules, now enforced in code |

## Licensing and telemetry notes

- **SQL 4 CDS** (`MarkMpn.Sql4Cds.Engine`) is MIT.
- It ships anonymous Application Insights telemetry to a hardcoded key. Query text is
  **not** sent on success but **is** sent on exception. This project sets
  `ApplicationName = "dvduck-cli"` so that attribution is deliberate. Review before use
  in a sensitive environment.
- The Dataverse **TDS endpoint is not used**: it does not support service principal
  authentication. See [ADR 0001](docs/adr/0001-sql4cds-over-tds-and-odata.md).
