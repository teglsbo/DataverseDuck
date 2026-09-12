# DataverseDuck

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
| Named profiles / certificate credentials | ✅ Built, 31 tests; certificate path unverified live |
| Device-code (interactive/MFA-capable) sign-in | ✅ Built, 9 tests, verified live |
| `dvduck repl` (fetch once, query many) | ✅ Built, 38 tests, verified live; key handling untested |
| `DataverseThrottling` (429 explanation) | ✅ Built, 9 tests; could not be provoked live, see below |
| `ServiceProtectionBudget` (limit headers) | ✅ Built, 13 tests, verified live |
| Cache manifest (`dvduck tables`) | ✅ Built, 8 tests |
| Verified against a live environment | ✅ All 8 doctor checks pass; two-hop JSON-to-Dataverse join verified |
| Device-code sign-in verified live | ✅ All 8 doctor checks pass under `DATAVERSE_AUTH_MODE=devicecode` |

### Open

- [ ] **Verify certificate authentication against a live environment.** Implemented and
      unit tested against a generated certificate, but this project's tenant
      authenticates with a secret, so the SDK's certificate constructor has never
      actually run against Dataverse.
- [x] **Cache storage version — documented as a one-way upgrade, deliberately not pinned.**
      A `--db` file carries DuckDB's storage version, and nothing here sets
      `STORAGE_VERSION`, so it is whatever the linked DuckDB writes. Across the v1-to-v2
      boundary that is a real break: v1.5 writes header version 64 and reads 64–68, v2.0
      writes 69 and reads 64–69, so a v2-written cache cannot be opened by a v1.5 client.
      Pinning was considered and rejected — it would freeze every cache at an old format to
      protect a case that deleting the cache also solves, and DuckDB's own open error
      already names both versions and links its storage page. Documented under
      [Know what is in a cache](#a-cache-upgrades-one-way) instead. See
      [ADR 0012](docs/adr/0012-duckdb-v2-api-window.md#when-v20-ships).
- [x] **Popup non-narrowing observation — root-caused, not our bug.** Confirmed with a
      scripted pty against a real snapshot: this is PrettyPrompt v6's own documented
      design ("Completion list contains also non-matching items (below matching ones)"
      per its changelog), not an issue in this codebase. It only re-invokes our
      `GetCompletionItemsAsync` while the typed text still extends what was typed when
      the window opened, and its internal `SlidingArrayWindow` always fills the
      configured window height (9 rows) with the best-ranked items from that one
      candidate list, padding with non-matching leftovers rather than shrinking —
      exactly like Visual Studio's IntelliSense. Tab still accepts the right item.
      Left as-is.
- [x] **`dvduck query --snapshot`.** `query --snapshot <path>` now builds an offline
      SQL 4 CDS connection from a captured metadata snapshot, with no live Dataverse
      connection at all. This lets a query that only touches an already-cached `--db`
      (and JSON) resolve column types and relationships correctly (the datetime/lookup
      mapping ADR 0002 depends on) without a tenant. It still refuses combination with
      a `DATAVERSE (...)` step, which needs a real fetch (ADR 0003).
- [x] **Package metadata.** `RepositoryUrl`/`PackageProjectUrl` now point at the GitHub
      repo, so `dotnet pack` embeds the commit SHA into the nuspec. The version stays
      pinned at 0.1.0 deliberately: neither package has been published yet (nothing on
      nuget.org consumes it), so there is no compatibility promise to keep by bumping it.
- [x] **Explained: `COUNT(*)` returned 56,161** despite the documented 50,000 aggregate
      limit. SQL 4 CDS retries a rejected aggregate as `PartitionedAggregateNode`,
      splitting the query into `createdon` date ranges under 50,000 rows each and summing
      client-side, silently. The limit is real and enforced per partition; see
      [docs/large-tables.md](docs/large-tables.md#explained-count-returning-more-than-50000).
- [x] **Explained: a trailing newline flips `read_json_auto` type inference**
      (ADR 0002, measurement 9). Reproduced against DuckDB's own sniffing code: with a
      2-row file mixing timestamp shapes, the newline shifts where DuckDB's read buffer
      splits during sampling, changing the order candidate types get eliminated in. Add
      a 3rd row and both variants already agree on `VARCHAR` regardless of newline — it's
      a narrow boundary condition, not a bug to fix. Inference still isn't safe to rely
      on either way, which is exactly what ADR 0005's runtime detector already assumes.

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
dotnet test          # 432 tests, no tenant required
```

### Do you need a Dataverse connection at all?

Most work does not, and the credential you want depends on whether a human is present.
Decide here before following the setup below.

| You want to | Command | Credential |
|---|---|---|
| Build, test, change code | `dotnet build`, `dotnet test` | none |
| Query already-cached tables | `dvduck query --db cache.duckdb …` | none, as long as no `DATAVERSE (…)` step runs |
| Resolve column types offline | `dvduck query --snapshot snap.json …` | none |
| See what a cache holds | `dvduck tables --db cache.duckdb` | none |
| Fetch rows from Dataverse | any `DATAVERSE (…)` step | yes |
| Query `metadata.entity` and friends | a `DATAVERSE (…)` step | yes — a snapshot is *not* enough |

And if you do need one:

| Situation | Credential | Prompts? |
|---|---|---|
| Unattended: script, CI, agent | `DATAVERSE_CLIENT_SECRET` or `DATAVERSE_CERT_PATH` | never |
| A person at a keyboard, no app registration | `DATAVERSE_AUTH_MODE=devicecode` | once, then remembered |

Device-code requires someone to open a URL and type a code; nothing automated can complete
it. It is remembered afterwards (see [The sign-in is remembered](#the-sign-in-is-remembered)),
but the first sign-in, and any renewal after the refresh token lapses, needs a human. Choose
a secret or a certificate if there will not be one.

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

Authenticate with a client secret (`DATAVERSE_CLIENT_SECRET`) or a certificate
(`DATAVERSE_CERT_PATH`, or `DATAVERSE_CERT_THUMBPRINT` for the platform store). Setting
two is refused rather than ranked, so a leftover secret cannot quietly win over a
certificate you just switched to.

Set `DATAVERSE_AUTH_MODE=devicecode` instead to sign in interactively as yourself rather
than as the application. This is the only mode where MFA applies at all: the secret and
certificate flows have no user in them, so a tenant's MFA policy simply doesn't come up.
No `DATAVERSE_CLIENT_ID` or app registration is required either — it defaults to
Microsoft's own well-known public sample app, already registered in every tenant, so
`DATAVERSE_URL` plus this one flag is enough to run. Device-code prints a URL and short
code — no browser or GUI is needed on the machine running `dvduck`, so it works the same
in a container or over SSH as it does locally; you (or anyone) can complete the sign-in
from any device with a browser. `DATAVERSE_USERNAME` optionally picks which cached
sign-in to reuse across runs. See
[docs/environment-setup.md](docs/environment-setup.md#device-code-sign-in) for details,
including using your own app registration where the well-known one isn't allowed.

#### The sign-in is remembered

You sign in once, not once per command. The device-code token cache is written to
`$XDG_DATA_HOME/dvduck/msal.cache` (by default `~/.local/share/dvduck/msal.cache`), and
later runs renew silently from it until the tenant expires the refresh token.
`DATAVERSE_TOKEN_CACHE` moves the file; `DATAVERSE_TOKEN_CACHE_PERSIST=0` keeps the cache
in memory as it was before, so that every run prompts again.

**The file is a credential.** It holds a refresh token, exchangeable for access tokens
without a prompt for as long as it stays valid, carrying whatever access the person who
signed in has. Where the platform offers a secret store — login keyring, Keychain, DPAPI —
the cache is encrypted with it. A container or an SSH session usually has no keyring, and
the fallback is a plaintext file with owner-only permissions: the same trade `az` and `gh`
make, but a trade. `dvduck doctor` says which of the two you got:

```console
[WARN] Token cache
       persisted UNENCRYPTED, owner-only permissions: /home/you/.local/share/dvduck/msal.cache
```

Delete the file to sign out. For genuinely unattended access prefer a client secret or a
certificate — those authenticate the *application*, so nothing on disk carries a person's
session, and they never prompt. Note that device-code still falls back to an interactive
prompt when silent renewal fails, which will block a script rather than failing it.
See [ADR 0013](docs/adr/0013-persist-the-device-code-token-cache.md).

For more than one environment, prefix any variable with a profile name:

```bash
export DATAVERSE_PROD_URL="https://yourorg.crm4.dynamics.com"
dvduck doctor --profile prod
```

Anything the profile does not set falls back to the unprefixed variable, so environments
sharing one app registration need only override the URL. A profile that sets *nothing* is
rejected rather than silently resolved to the default environment, so a mistyped name
fails instead of quietly querying the wrong tenant; the error lists the profiles that do
exist.

`--url`, `--client-id`, `--tenant-id`, `--auth-mode`, and `--username` override the
matching variable for a single run instead of setting it in the environment. There's no
`--client-secret` flag on purpose -- that belongs in `.env`, not shell history.

### Ask a question

A query names its own sources in one `WITH` block. How many contacts had webchat messages:

```bash
dvduck query --query "
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

Use `--query-file plan.sql` to keep the SQL in a file. `--db cache.duckdb` persists the
fetched tables so a re-run costs nothing.

`--query` is the only form. It runs the statement — the name says so now, rather than
implying a dry run — but it was originally called `--plan` and is still accepted with a
deprecation notice, alongside `--plan-file`. An earlier `--json` / `--cache` / `--run` flag
form was removed entirely because it left the dependency order implicit in the order the
flags happened to appear; those flags now print the equivalent query rather than a bare
"unknown option".

### Ask about the schema

Dataverse metadata is queryable as ordinary tables — `metadata.entity`,
`metadata.attribute`, `metadata.relationship_1_n` (and `_n_1`, `_n_n`, `alternate_key`,
`value`). Nothing special is needed to use them: a `DATAVERSE` entry is T-SQL, so they
cache into DuckDB like any other table and can be searched, joined and exported.

```bash
dvduck query --db schema.duckdb --query "
  WITH columns AS DATAVERSE (SELECT entitylogicalname, logicalname, attributetypename
                             FROM metadata.attribute WHERE entitylogicalname = 'contact')
  COPY (SELECT * FROM columns ORDER BY logicalname)
  TO 'contact-columns.json' (FORMAT JSON, ARRAY true)"
```

This is also the quickest way to see how wide these tables are: `account` has 215
attributes and `contact` 311, counting derived ones like `accountidname`. `SELECT *` is
rarely what you want.

See [docs/metadata.md](docs/metadata.md) for worked examples from "how many tables are
there" up to joining `entity` to `attribute`, including how to find what a lookup points
at and how to audit which datetime columns are wall-clock.

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

#### A cache upgrades one way

A `--db` file is a DuckDB database, so it carries DuckDB's storage version, and **opening
one with a newer DuckDB can upgrade it irreversibly**. Newer DuckDB reads older files;
older DuckDB cannot read newer ones. Nothing here pins the version — a cache is written in
whatever format the linked DuckDB writes by default.

Concretely, across the v1-to-v2 boundary: DuckDB v1.5 writes storage header version 64 and
reads 64 through 68. DuckDB v2.0 writes 69 and reads 64 through 69. So a v2-linked `dvduck`
opens every existing cache, and a cache it has written is one version past what a v1.5
client can read. That client fails at open with DuckDB's own error:

```
Trying to read a database file with version number 69, but we can only read versions
between 64 and 68.
The database file was created with a newer version of DuckDB.
```

The error names both versions and links DuckDB's storage page, so it needs no help from
us. Treat it as the expected outcome of a one-way upgrade, not a fault.

The practical rule: **a cache shared between machines is only as portable as the oldest
`dvduck` that must read it.** If that matters, keep the cache disposable — it is a cache,
and `--db` can always be deleted and refetched — or standardise the `dvduck` version
across the machines that share one. Caches are not an archive format.

### Fetch once, ask many questions

`dvduck query` is one round trip per invocation, which makes exploring expensive: every
rephrasing of the question re-downloads the table. `dvduck repl` keeps the DuckDB
connection open instead.

```console
$ dvduck repl
dvduck REPL. :memory: database. '.help' for commands, '.quit' to leave.
Completing 2 Dataverse tables from the snapshot.

dvduck> WITH c AS DATAVERSE (SELECT contactid, fullname FROM contact);
Connecting to https://org13bc90fb.crm4.dynamics.com/...
Caching c...
  c: 4 rows in 0.2s

dvduck> SELECT count(*) FROM c;
count_star()
------------
4
(1 row)

dvduck> SELECT fullname FROM c ORDER BY fullname LIMIT 3;
```

A `WITH ... AS DATAVERSE` block may end without a query — it fetches and stops, which is
the natural move when the point is to poke at the result afterwards. Everything else is
plain DuckDB SQL over what is already cached, so the second question costs nothing.

Dataverse is not contacted until the first `DATAVERSE` block, so a session over an
existing `--db` file never opens a connection at all. Completion comes from
`information_schema` for cached tables and from the metadata snapshot for Dataverse
logical names, which means it works offline. Names you were typing are offered before
names that merely contain what you typed, so `name` finds `fullname` without burying it.

`.tables` `.schema` `.format` `.limit` `.help` `.quit`. Output is aligned columns by
default, capped at 50 rows and 40 characters per cell — this view is for reading, not for
piping, and `.format csv` switches to the escaping writer when you want the latter.
Ctrl-C cancels the running statement, not the session.

Input can also be piped, which is how scripts use it and the only way the REPL is tested:

```console
$ printf 'WITH c AS DATAVERSE (SELECT contactid FROM contact);\nSELECT count(*) FROM c;\n' | dvduck repl
```

### How results are printed

`--format tsv` (the default), `csv` or `json`, to **stdout**. Progress lines and the
trailing `(2 row(s))` go to **stderr**, so `dvduck query ... > out.csv` gives a clean file.

All three escape their own delimiters, so a `description` containing a tab, a comma or a
newline cannot silently add a column or split a row:

```console
$ dvduck query --format csv --query "WITH d AS JSON ('nasty.json') SELECT * FROM d"
id,name,note
1,"Acme, Inc","line one
line two"
2,"He said ""hi""",Åse Ø Ærø
```

- **csv** is RFC 4180: fields containing `,` `"` CR or LF are quoted, embedded quotes are
  doubled, and records end with CRLF as section 2.1 requires. Verified by round-tripping
  hostile values through Python's `csv` reader, not only through our own writer.
- **tsv** has no escape mechanism in its media type, so values are escaped with
  backslashes (`\t`, `\n`, `\r`, `\\`) the way Postgres `COPY ... TO` does in text mode.
  Lossless and reversible.
- **json** is the only format that distinguishes NULL from an empty string, and it keeps
  numbers and booleans unquoted. Non-ASCII stays literal, so Danish text is readable
  rather than `\u00C5`.

Common to all: timestamps as `yyyy-MM-dd HH:mm:ss` (naive UTC, per ADR 0002), dates as
`yyyy-MM-dd`, `byte[]` as hex, all numbers formatted invariantly so a decimal comma can
never appear inside a CSV field.

#### Byte order marks

Output is UTF-8 **without** a BOM, and `--bom` adds one for `csv` and `tsv`.

Opt-in rather than opt-out, because the evidence points that way: the Unicode Standard
says a BOM is *"neither required nor recommended for UTF-8"*, RFC 8259 §8.1 says
implementations **must not** add one to JSON (so `--bom --format json` is refused), and
DuckDB, PostgreSQL, MySQL, pandas, Python's `csv`, Google Sheets and PowerShell 7 all
write none. PowerShell moved *away* from BOMs in 6.0, as did .NET Core's `StreamWriter`.

The exception is the one that matters here: **Excel on non-English Windows reads a
BOM-less UTF-8 CSV as the ANSI code page**, so `Åse Ø Ærø` opens as `Ã…se Ã˜ Ã†rÃ¸`.
If a human will double-click the file, pass `--bom`. If a program will read it, don't —
a BOM makes `grep '^id'` match nothing and turns the first header into `\ufeffid` for any
reader that doesn't open with `utf-8-sig`.

For files, DuckDB can also write directly — `COPY (...) TO 'out.parquet' (FORMAT PARQUET)`
works as the final statement of a plan, as does `FORMAT JSON` or `FORMAT CSV`.

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

## Service protection limits

Dataverse limits requests, execution time and concurrency per user, per **web server**,
over a five minute window. `dvduck doctor` reads the environment's own counters and prints
them:

```
[INFO] Service protection budget
       Recommended parallelism: 4
       Requests remaining:      7,997
       Execution time left:     1,200s
```

Design against the recommended parallelism, not the limits. Ours reports **4**, against a
documented concurrency limit of 52. The two counters describe a single web server, so they
are only comparable between readings that hit the same one — `ServiceProtectionBudget`
reports which server answered so you can tell.

`DataverseThrottling` turns a 429 into the limit that was hit, the wait Dataverse asked
for, and what to change. It has **never been tested against a real fault**, because three
attempts to provoke one all failed: the SDK gives you concurrency or server affinity but
never both, and a client that has both still burns execution time more slowly than the
window replenishes it. From an ordinary single .NET client these limits are hard to reach
at all — which is good news, but it is not a green checkmark, and it is recorded as such.

See [docs/service-protection.md](docs/service-protection.md).

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

The CLI reads a captured snapshot back too, via `dvduck query --snapshot`, so offline
development doesn't require writing code against the library:

```bash
dvduck query --db cache.duckdb --snapshot metadata/snapshot.bin \
  --query "SELECT * FROM account"
```

This builds a `Sql4CdsConnection` with no live `IOrganizationService` at all — enough to
resolve column types and relationships against an already-cached `--db` (and JSON), but
a plan with a `DATAVERSE (...)` step is rejected up front since that needs a real fetch.

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
  Repl*.cs                  Interactive session, statement loop and terminal table
tests/DataverseDuck.Tests/  406 tests, no tenant required
spikes/                     Throwaway experiments that produced the evidence
docs/environment-setup.md   Getting headless access to Dataverse
docs/large-tables.md        Measured limits, and where key-set pushdown stops paying
docs/metadata.md            Querying the schema itself, simple to complex
docs/sql4cds-behaviour.md   Measured engine defaults and type mapping
docs/adr/                   Architecture decision records
Directory.Build.props       Shared package metadata; nothing packs unless it opts in
.github/workflows/ci.yml    Build and test on every push/PR
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
| `ThrottleSpike` | Can we provoke a real 429? | No, three ways — and why is the finding |
| `BulkInsertSpike` | What does writing through SQL 4 CDS cost? | ~215 rows/s in, ~33 out; batching and DOP only pay together |

## Licensing and telemetry notes

- **SQL 4 CDS** (`MarkMpn.Sql4Cds.Engine`) is MIT.
- It ships anonymous Application Insights telemetry to a hardcoded key. Query text is
  **not** sent on success but **is** sent on exception. This project sets
  `ApplicationName = "dvduck-cli"` so that attribution is deliberate. Review before use
  in a sensitive environment.
- The Dataverse **TDS endpoint is not used**: it does not support service principal
  authentication. See [ADR 0001](docs/adr/0001-sql4cds-over-tds-and-odata.md).
