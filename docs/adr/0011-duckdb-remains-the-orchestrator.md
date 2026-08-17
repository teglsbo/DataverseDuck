# 11. DuckDB remains the orchestrator; do not invert the stack under SQL 4 CDS

Date: 2026-08-17

## Status

Accepted.

## Context

ADR 0001 chose SQL 4 CDS to get Dataverse rows into DuckDB. The question raised here is
the inverse of that one: instead of DuckDB on top issuing queries that pull from
Dataverse (via SQL 4 CDS) and from local JSON (via `read_json_auto`), could **SQL 4 CDS
be the orchestrator**, with DuckDB — or local JSON directly — registered underneath it
as a second data source? That would mean writing an `IOrganizationService` backed by
the local JSON folders and letting `Sql4CdsConnection` join it against the real
Dataverse connection in one T-SQL statement, with no DuckDB `read_json_auto` step at
all.

## Investigation

Inspected `MarkMpn.Sql4Cds.Engine.dll` (10.4.4) directly by reflection rather than
relying on documentation.

**`Sql4CdsConnection` does support multiple named data sources.** One constructor takes
`IDictionary<string, DataSource>`, which is how the engine supports multi-org T-SQL
(`org1.contact JOIN org2.account`). So a second, non-Dataverse `DataSource` is not
rejected outright by the connection API.

**But every `DataSource` must be a real Dataverse connection.** `DataSource`'s
constructors take only `IOrganizationService` (plus `IAttributeMetadataCache`,
`ITableSizeCache`, `IMessageCache` — all Dataverse-metadata-shaped interfaces). There is
no constructor or property that accepts a file path, a generic `IDataReader`, or any
non-Dataverse table provider.

**The execution plan confirms this is structural, not an oversight.** Enumerating
`MarkMpn.Sql4Cds.Engine.ExecutionPlan` types, the only row-producing leaf node backed by
an external store is `FetchXmlScan`. (`ConstantScanNode`, `MetadataSource`,
`OptionSetSource` and `TableScanNode` serve literals, Dataverse metadata/optionset
introspection, and internal temp results — none of them is a generic external-provider
hook.) `HashJoinNode` and `MergeJoinNode` exist and would in principle join two
`DataSource`s' results, but each side of that join is still produced by compiling to
FetchXml and executing it through `IOrganizationService`.

**Consequence:** making local JSON a `DataSource` means implementing an
`IOrganizationService` that can interpret arbitrary FetchXml — `<filter>`,
`<link-entity>`, aggregates, paging, `<order>` — against JSON records, plus synthesizing
`EntityMetadata`/`AttributeMetadata` for JSON shapes that have no natural encoding for
Dataverse-only types (`OptionSetValue`, `EntityReference`, `Money`).

## Options considered

### A. Implement `IOrganizationService` over local JSON folders (invert the stack)

Register local JSON as a second `DataSource`; let SQL 4 CDS's own plan builder and join
nodes orchestrate everything, dropping DuckDB's `read_json_auto` join entirely.

**Rejected.** This is not a thin adapter — it is a FetchXml execution engine written
from scratch, duplicating a large slice of Dataverse's own query semantics purely as a
compatibility shim, to obtain a capability (SQL over JSON with joins, filters,
aggregation) that DuckDB already provides natively and for free. It also introduces a
correctness risk that does not exist today: SQL 4 CDS's folding decisions (ADR 0004)
depend on `EntityMetadata` being accurate, and synthetic metadata for arbitrary JSON
shapes is exactly the kind of input likely to trip an undetected fallback to full
in-memory execution — the same failure mode ADR 0001 already flags as SQL 4 CDS's
weakness, but now on data we do not even need to fetch remotely.

### B. Keep DuckDB as orchestrator, SQL 4 CDS as a Dataverse-only producer (status quo)

DuckDB issues the query, executes `read_json_auto` locally, and receives Dataverse rows
through a `Sql4CdsConnection` / `DbDataReader` streamed into the Appender (ADR 0001).

**Adopted.** DuckDB is the general-purpose query engine in this design; SQL 4 CDS is a
narrow, single-purpose FetchXml compiler for exactly one source. An orchestrator should
be the general engine, not the narrow one. This also matches ADR 0007's direction of
travel: that ADR already examined "make the pushdown smarter" and chose to keep pushing
in *DuckDB's* direction (dynamic filter timing on the probe side of a hash join) rather
than pulling orchestration into SQL 4 CDS.

## Decision

**Keep the current stacking.** DuckDB remains the orchestrator and query engine. SQL 4
CDS remains a narrow Dataverse-only row producer, invoked the way ADR 0001 describes.
Do not implement an `IOrganizationService` over local files.

## Consequences

**Positive**

- No new execution engine to build, test, or keep correct against SQL 4 CDS's own
  evolving fold rules.
- Local JSON keeps using DuckDB's native, fast, well-tested `read_json_auto` and
  Appender path instead of a hand-rolled FetchXml interpreter.
- The one place SQL 4 CDS's "silently falls back to full retrieval" failure mode
  (ADR 0001) can bite is exactly where it already does today — real Dataverse tables —
  not newly introduced onto local data that never needed fetching in the first place.

**Negative, and why it is accepted**

- DuckDB's predicate/join-key pushdown into the FetchXml side remains bounded by what
  ADR 0006 and ADR 0007 already measured (key-set pushdown via the `{{ }}` marker, not
  a fully automatic mechanism). Inverting the stack would not have removed this
  limitation — it would have moved the same limitation into a new, hand-built layer.

## Evidence

- Reflection over `MarkMpn.Sql4Cds.Engine.dll` (10.4.4, resolved via
  `spikes/MetadataSpike/bin/Debug/net10.0/`):
  - `DataSource` constructors accept only `IOrganizationService` (+ Dataverse metadata
    cache interfaces) — no file/table-provider constructor exists.
  - `Sql4CdsConnection(IDictionary<string, DataSource>)` confirms multi-data-source
    support exists at the connection level.
  - `MarkMpn.Sql4Cds.Engine.ExecutionPlan` enumerated types: the only external-store
    scan node is `FetchXmlScan`; `ConstantScanNode`, `MetadataSource`, `OptionSetSource`,
    `TableScanNode` are not generic external-provider hooks.
- ADR 0001 — original choice of SQL 4 CDS and its known in-memory-fallback weakness.
- ADR 0004 — execution plan folding guard; depends on accurate `EntityMetadata`.
- ADR 0007 — prior examination of where to invest in smarter pushdown; chose DuckDB's
  side, not SQL 4 CDS's.
