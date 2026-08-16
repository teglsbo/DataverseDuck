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

Early. The foundations are built and tested; nothing has run against a real tenant yet.

| Component | State |
|---|---|
| `UtcTimestampPolicy` | ✅ Built, 18 tests |
| `Sql4CdsConnectionFactory` | ✅ Built |
| `MetadataSnapshot` / `SnapshotMetadataCache` / `MetadataCapture` | ✅ Built, 12 tests |
| `dvduck doctor` / `dvduck capture` CLI | ✅ Built, 25 tests |
| `DataverseSchemaMapper` (reader → DDL) | ✅ Built, 26 tests |
| `DuckDbBulkLoader` (reader → Appender) | ✅ Built, 10 tests |
| `ExecutionPlanAnalyzer` (folding guard) | ✅ Built, 27 tests |
| `DataverseThrottling` (429 explanation) | ✅ Built, unverified against a tenant |
| Cache manifest (`dvduck tables`) | ✅ Built, 8 tests |
| Verified against a live environment | ❌ **Blocked on a tenant** |

## Quickstart

Requires .NET 10.

```bash
dotnet test          # 224 tests, no tenant required
```

### Connect to a real environment

Setting up headless access takes about 20 minutes and spans three portals.
Follow [docs/environment-setup.md](docs/environment-setup.md), then verify:

```bash
export DATAVERSE_URL="https://yourorg.crm4.dynamics.com"
export DATAVERSE_TENANT_ID=...  DATAVERSE_CLIENT_ID=...  DATAVERSE_CLIENT_SECRET=...

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

### Use the library

```csharp
using DataverseDuck;

// Service principal: no interactive login.
using var crm = Sql4CdsConnectionFactory.CreateWithClientSecret(
    new Uri("https://yourorg.crm.dynamics.com"), clientId, clientSecret);

// Opens DuckDB with the session pinned to UTC.
using var duck = UtcTimestampPolicy.OpenConnection("Data Source=cache.duckdb");
```

Then join the two worlds:

```sql
SELECT a.name, j.level, count(*) AS n
FROM read_json_auto('logs/*.json') j
JOIN crm_accounts a ON a.accountid = CAST(j.account_id AS UUID)
GROUP BY a.name, j.level;
```

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
tests/DataverseDuck.Tests/  118 tests, no tenant required
spikes/                     Throwaway experiments that produced the evidence
docs/environment-setup.md   Getting headless access to Dataverse
docs/sql4cds-behaviour.md   Measured engine defaults and type mapping
docs/adr/                   Architecture decision records
```

## Spikes

`spikes/` holds the throwaway programs used to measure behaviour rather than assume it.
They are kept because the ADRs cite them as evidence. They are not part of the build.

| Spike | Question | Outcome |
|---|---|---|
| `AppenderSpike` | Which CLR types does the DuckDB Appender accept? | 16/16 types, ~440k rows/sec |
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
