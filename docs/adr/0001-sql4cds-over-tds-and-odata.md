# ADR 0001 — Use SQL 4 CDS rather than the TDS endpoint or a DuckDB OData extension

- **Status:** Accepted
- **Date:** 2026-08-16

## Context

We need to join Dataverse tables against local JSON logs and state documents in one SQL
query, from C#. Two constraints shape the decision:

1. **Authentication must be an Entra ID app registration** (MSAL client credentials).
   No interactive user, no user licence.
2. **Several Dataverse tables are involved and some are large** (millions of rows), so
   the join and filter must execute server-side. Pulling whole tables is not viable.

DuckDB handles the JSON side natively via `read_json_auto`, so the only real question is
how Dataverse rows enter the query.

## Options considered

### A. Dataverse TDS endpoint + the `mssql` DuckDB extension

`ATTACH` Dataverse as if it were SQL Server and let DuckDB push down joins.

**Rejected — authentication is a hard blocker.** Microsoft Learn's TDS endpoint
documentation (`dataverse-sql-query`, ms.date 2026-06-01) lists only two supported
flows: *Microsoft Entra ID – Universal* (MFA) and *Microsoft Entra ID – Password*.
Service principal / client credentials is not documented and not supported. The
`mssql` extension is also untested against Dataverse: a repository search for
"dataverse" or "dynamics" returns zero issues, it targets SQL Server 2019+, and its
Azure AD path requests the `https://database.windows.net/` audience whereas Dataverse
TDS expects a Dataverse-audience token.

Also worth noting: TDS has a fixed 5-minute query timeout, dropping to 2 minutes for
queries containing `SELECT *` or nested joins, and excludes several column types.

### B. `erpl_web` DuckDB community extension

A community extension with purpose-built `crm_read`, `crm_describe` and
`crm_show_entities` functions, and client-credentials support via a `dataverse` secret.
Elegant: everything stays in DuckDB SQL with no C# glue.

**Rejected as the primary path — OData has no join primitive.** Its pushdown covers
`$filter`, `$select`, `$top` and `$skip` only. There is no join or `$expand` pushdown,
and the Dataverse surface is three single-entity read functions. A two-table join
therefore issues two independent REST scans and joins locally, so a selective filter on
one table cannot constrain the other. Against a 2M-row table that is roughly 400
sequential paged requests under a 6000-request / 5-minute service protection limit.

Secondary concerns: BSL 1.1 licence (not OSI open source), ~850 downloads/week, and
Dataverse is one of roughly eight backends rather than the focus.

**Still a good fit** where every table is small, or where each table is independently
filtered, and zero C# glue matters more than throughput.

### C. Dataverse Web API `?sql=` query option

Genuinely supported, GA, and authenticates like the rest of the Web API — so service
principals work. Supports `INNER`/`LEFT JOIN` across multiple tables, `WHERE`,
`GROUP BY`, `DISTINCT` and `ORDER BY`.

**Kept as a fallback.** Constraints are real: no `SELECT *`, no `TOP` or
`OFFSET…FETCH`, no subqueries, no `HAVING`, no functions on column values, and a
50,000-row cap on aggregate queries. Paging is via `Prefer: odata.maxpagesize` and
`@odata.nextLink`.

### D. Azure Synapse Link / Link to Microsoft Fabric → Delta Parquet → DuckDB

Dataverse continuously exports to ADLS Gen2 or OneLake as Delta Parquet. DuckDB reads it
with the `azure` and `delta` extensions, authenticating with a service principal
(`CREATE SECRET … PROVIDER service_principal`). Bypasses the API and its limits entirely.

**Adopted for genuinely large tables**, as a complement rather than a replacement. It
requires provisioning and introduces sync latency, so it is not the default for every table.

### E. Write a custom DuckDB extension

Rejected. DuckDB extensions are C++/C/Rust; the community CI has no C# path. This would
be justified only to get OData predicate pushdown that no existing option provides —
which is exactly what SQL 4 CDS already does via FetchXML.

## Decision

**Use SQL 4 CDS (`MarkMpn.Sql4Cds.Engine`) as the primary path**, with
`UseTDSEndpoint = false`, streaming its `DbDataReader` into DuckDB through the Appender.

It parses T-SQL with `Microsoft.SqlServer.TransactSql.ScriptDom` and compiles what it can
into FetchXML, so joins become `link-entity` elements evaluated inside Dataverse. It is
MIT licensed, actively maintained, exposes a real ADO.NET surface
(`Sql4CdsConnection` / `Sql4CdsCommand` / `Sql4CdsDataReader`) with a working
`GetSchemaTable()`, and accepts an `IOrganizationService` built from client credentials.

For tables too large to query through the API at all, use option D and join the Parquet
in the same DuckDB statement.

## Consequences

**Positive**

- Service principal authentication works, satisfying the hard constraint.
- Joins and filters execute server-side; only results cross the wire.
- `GetSchemaTable()` and `GetFieldType()` drive DuckDB DDL generation automatically.
- Accepting an `IOrganizationService` means tests can inject a fake.

**Negative, and how we handle it**

- **SQL 4 CDS also falls back to in-memory execution** when it cannot translate a
  construct, and must then retrieve *all* rows first — the same failure mode we rejected
  `erpl_web` for. The difference is that it pushes far more down and reports what it did.
  *Mitigation:* log the execution plan on every query and warn when a join or filter did
  not fold into the `FetchXmlScan`.
- Service protection limits (6000 requests / 5 minutes, 20 minutes execution, ~52
  concurrent) still apply. *Mitigation:* honour `Retry-After`; route the largest tables
  to option D.
- Anonymous Application Insights telemetry, including query text on exceptions.
  *Mitigation:* `ApplicationName = "dvduck-cli"`; flagged in the README.
- No transaction support. Irrelevant for a read-only workload.
- The engine pulled a vulnerable `System.Security.Cryptography.Pkcs 6.0.1`
  (GHSA-555c-2p6r-68mm). *Mitigation:* pinned to 10.0.11.

## Evidence

- `spikes/MetadataSpike` — confirmed `DataSource` accepts injectable
  `IOrganizationService`, `IAttributeMetadataCache`, `ITableSizeCache` and
  `IMessageCache`, so offline compilation is possible.
- `erpl-web` README — pushdown limited to `$filter`, `$select`, `$top`, `$skip`.
- Microsoft Learn `dataverse-sql-query` (2026-06-01) — TDS auth matrix.
- Microsoft Learn `webapi/query/sql` (2026-06-01) — Web API SQL capabilities and limits.
