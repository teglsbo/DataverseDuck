# ADR 0014 — Refuse DML at the connection, and unbatchable key queries at the plan

- **Status:** Accepted
- **Date:** 2026-09-12

## Context

Every `DATAVERSE (...)` source in a plan (ADR 0008) is SQL text handed to SQL 4 CDS
largely unmodified — that is the whole point of using it (ADR 0001). SQL 4 CDS compiles
INSERT/UPDATE/DELETE just as readily as SELECT. dvduck has no feature that writes to
Dataverse — `doctor`, `capture`, `query`, `repl`, and `tables` all read — so a DML
statement reaching a `Sql4CdsConnection` this project created is always a mistake: a
malformed source, a copy-pasted statement, or a hostile one. Nothing currently stops it
from running.

Separately, a `{{ }}` key query (ADR 0006) narrows a `DATAVERSE (...)` source to the keys
a local query names, but `DataverseCache.CacheMatching` fetches those keys in batches and
runs the outer statement **once per batch**, appending every batch's rows into the same
table. DISTINCT, TOP, and GROUP BY each look like ordinary, harmless SQL, but applied
per-batch instead of across the full key set they give a plausible, wrong answer rather
than an error — the query still runs and still returns rows.

Both are the same shape of problem: a caller's SQL text may describe an operation this
project's execution model cannot honour correctly (or, for DML, should never honour at
all), and nothing between the caller and SQL 4 CDS is checking for it.

## Options considered

**Parse every `DATAVERSE (...)` body with a T-SQL parser and reject non-SELECT
statements.** Works, but re-derives — with our own SQL text handling — a classification
SQL 4 CDS's own compiler already makes more reliably: what counts as a write, including
forms a hand-written check is likely to miss (a CTE in front of the write, `UPDATE ...
FROM`, and so on). It is also something a caller has to remember to invoke before every
query; nothing forces it.

**Ask SQL 4 CDS not to execute DML at all.** `Sql4CdsConnection` exposes
`PreInsert`/`PreUpdate`/`PreDelete` events (`ConfirmDmlStatementEventArgs`, a
`CancelEventArgs`) that its own `InsertNode`/`UpdateNode`/`DeleteNode` fire immediately
before performing the write — after the engine, not us, has already decided the
statement is DML, however it was phrased. Setting `Cancel = true` makes the node throw the
engine's own `QueryExecutionException` ("INSERT/UPDATE/DELETE cancelled by user") instead
of proceeding. This is upstream, XrmToolBox's SQL 4 CDS plugin uses the same hook to
implement its own confirm-before-writing prompts.

For the batching hazard, there is no engine-level equivalent to ask for: SQL 4 CDS has no
concept of "this statement will be run once per batch of an external key set" — that
behaviour is entirely `DataverseCache`'s. Only a T-SQL parse of the statement's outermost
shape can tell whether it declares DISTINCT/TOP/GROUP BY.

## Decision

**Two separate guards, at the two separate levels the two problems actually live at.**

1. `Sql4CdsConnectionFactory.Configure` subscribes to `PreInsert`/`PreUpdate`/`PreDelete`
   on every connection it builds and unconditionally cancels each one. This runs exactly
   once, for every connection this project creates, regardless of which entry point
   (`query`, `repl`, `capture`, `doctor`) or which shape the write takes.
2. `KeySetPushdown.ValidateBatchable` parses a `{{ }}`-bearing statement's outermost
   shape with the same T-SQL grammar SQL 4 CDS compiles against
   (`Microsoft.SqlServer.TransactSql.ScriptDom`, already a transitive dependency — pinned
   to the same version) and throws if it is DISTINCT, TOP, or GROUP BY at the top level.
   `DataverseCache.Cache` calls it as soon as `KeySetPushdown.FindKeyQuery` finds a key
   query, before any batch runs.

The two are not interchangeable: the connection-level block cannot see "this is inside a
`{{ }}` key query", and the parser-level check cannot see "this connection should never
write" (a parser has no way to distinguish a legitimate SELECT-only tool from one that
means to support writes one day).

## Consequences

**Positive**

- DML is refused by the same component that decides what DML *is*. A write phrased in a
  way our own SQL text handling has not been taught to recognise is refused exactly like
  one that is.
- The block lives in the one place every connection this project creates passes through
  (`Sql4CdsConnectionFactory.Configure`), not in a caller's per-query checklist.
- The batching check reuses `KeySetPushdown.FindKeyQuery`'s own definition of "this is a
  keyed statement" rather than re-deriving it, and fires before the first batch runs
  rather than after silently producing a wrong result.
- Neither check does its own SQL string scanning for the constructs it cares about; both
  ask a real parser, so a DISTINCT that appears inside a string literal, a comment, or a
  column alias is not mistaken for the clause.

**Negative**

- `ValidateBatchable` does not catch a bare aggregate with no GROUP BY (e.g. `COUNT(*)`)
  run against a `{{ }}` source — that needs walking the select list for an aggregate
  function name, which `UniqueRowFilter`/`TopRowFilter`/`GroupByClause` do not expose for
  free the way the other three do.
- The DML block is unconditional, with no escape hatch. That is deliberate — dvduck has
  no consumer that should ever want it disabled — but it does mean this project cannot
  grow a write feature without first reworking `Sql4CdsConnectionFactory.Configure`.
- Adds a direct dependency on `Microsoft.SqlServer.TransactSql.ScriptDom`, already pulled
  in transitively by `MarkMpn.Sql4Cds.Engine`. Pinning it to the same version keeps both
  parses on identical grammar, at the cost of one more version to track through Sql4Cds
  upgrades (`NuGetAudit`, per `Directory.Build.props`, already covers it for CVEs).

## Evidence

Read directly from `MarkMpn.Sql4Cds.Engine`'s source
(github.com/MarkMpn/Sql4Cds, `MarkMpn.Sql4Cds.Engine/Ado/Sql4CdsConnection.cs` and
`ExecutionPlan/InsertNode.cs` et al.):

- `Sql4CdsConnection.PreInsert`/`PreUpdate`/`PreDelete` are public events of type
  `EventHandler<ConfirmDmlStatementEventArgs>`; `ConfirmDmlStatementEventArgs` derives
  from `System.ComponentModel.CancelEventArgs`.
- `InsertNode.Execute` (and the equivalent in `UpdateNode`/`DeleteNode`) calls
  `context.Options.ConfirmInsert(confirmArgs)` and, if `confirmArgs.Cancel` is set,
  throws `QueryExecutionException` before issuing any Dataverse request.
- `MarkMpn.Sql4Cds.Engine` 10.4.4's own nuspec depends on
  `Microsoft.SqlServer.TransactSql.ScriptDom` 170.64.0.

`spikes/BulkInsertSpike` confirms the write path this ADR blocks is not theoretical: it
is SQL 4 CDS's own `InsertNode`/`DeleteNode`, driving `ExecuteMultiple` against a live
environment, and it works — 215 rows/s peak on `INSERT`, throttling and all. Before that
spike, nothing in this repository had ever exercised DML through this connection type;
ADR 0001 chose SQL 4 CDS for reads, and the write path had simply never been asked to run.
That a `DATAVERSE (...)` source can, unblocked, mutate a real environment (and did, in
that spike, to rows purpose-built to be safely reversible) is the concrete version of the
mistake this ADR closes off by default.
