# ADR 0004 — Analyse the execution plan and refuse queries that do not fold

- **Status:** Accepted
- **Date:** 2026-08-16

## Context

SQL 4 CDS compiles T-SQL into FetchXML so that joins and filters execute inside Dataverse.
Where it cannot, it **silently falls back** to fetching each table separately and joining
in the client process.

The fallback is not a bug and does not produce an error. The query returns correct
results. That is precisely what makes it dangerous: correctness is preserved while the
performance characteristics change by orders of magnitude.

ADR 0001 chose SQL 4 CDS partly *because* it pushes more work server-side than the
alternatives. But it has the same failure mode as every other option, and our stated
constraint is that **some Dataverse tables are very large**. A plan that quietly decides to
stream two million rows across the network to join them locally will look like a hang, and
will trip service protection limits (6000 requests / 5 minutes) on the way.

A join fails to fold for reasons that are not obvious from the SQL:

- Dataverse `link-entity` requires a **relationship** between the tables. A join on a
  non-relationship column cannot fold, however sensible it looks in T-SQL.
- Joins across data sources — for us, Dataverse to local JSON — can never fold.
- Non-equality join predicates have no FetchXML equivalent.
- A function applied to a column, or a comparison between two columns, blocks filter
  folding.

None of these are visible at the call site.

## Decision

**Inspect the compiled plan before executing, classify any work that will happen locally,
and let the caller refuse the query.**

`Sql4CdsCommand.GeneratePlan(compileForExecution: false)` compiles without executing, so
this is a pre-flight check rather than a post-mortem.

`ExecutionPlanAnalyzer` walks the plan tree and classifies each node:

| Operation | Meaning |
|---|---|
| `ServerSideScan` | Ran as FetchXML inside Dataverse. What we want. |
| `ClientSideJoin` | The join did not fold. Both sides are fetched. |
| `ClientSideFilter` | Rows are fetched, then discarded locally. |
| `ClientSideSpool` | Rows buffered in memory. |
| `ClientSideSort` / `ClientSideAggregate` | Ordering or grouping applied locally. |

**Severity scales with the estimated row count.** A client-side join over 200 rows is
irrelevant; the same plan over 2,000,000 rows is the failure this project exists to avoid.
Findings above `LargeRowThreshold` (default 10,000) are `Critical`, below it `Warning`.

`FoldingPolicy` then decides what to do:

| Policy | Behaviour |
|---|---|
| `Allow` | Analyse only. |
| `Warn` | Log local work, run anyway. Default. |
| `RejectCritical` | Throw when local work exceeds the threshold. Recommended unattended. |
| `RequireFullFolding` | Throw on any local work. Strict. |

Findings carry the *reason* folding is likely to have failed, not just the fact, because
"this join did not fold" is not actionable on its own.

## Detection signals

Two, with different reliability, and the difference matters:

1. **`IFetchXmlExecutionPlanNode` is public.** Server-side scans are identified by
   interface. Stable.
2. **The concrete join, filter and spool node types are `internal`.** They are identified
   by type name and base-type chain. This is genuinely fragile.

Signal 2 has a nasty failure mode: if a package upgrade renames `BaseJoinNode`, detection
stops matching, every plan looks perfectly folded, and the safety check silently becomes a
no-op. The worst possible outcome for a guard.

So `EngineTypeNameContractTests` reflects over the shipped assembly and asserts that the
names and hierarchy still hold, that no new concrete join node has appeared outside
`BaseJoinNode`, and that `GeneratePlan` still takes `compileForExecution`. **An upgrade
that breaks detection fails the build rather than silently disarming the guard.**

## Consequences

**Positive**

- The dominant failure mode is caught before it costs anything.
- Warnings are quantitative — row estimates, not just node names.
- `RejectCritical` makes unattended runs safe to leave alone.

**Negative**

- Row counts are the optimiser's *estimates*. Dataverse statistics can be wrong, so a
  large table may be under-estimated and slip past the threshold. Estimates are a filter,
  not a guarantee.
- Coupling to internal type names is a maintenance cost at every upgrade. Accepted because
  the contract tests make it a loud, cheap failure.
- Classification by name substring (`Spool`, `Aggregate`) is coarse and may mis-label an
  unfamiliar node. It errs toward warning, which is the safe direction.
- `GeneratePlan` costs a compilation round trip, including metadata retrieval.

## Evidence

Obtained by reflecting over `MarkMpn.Sql4Cds.Engine` 10.4.4 — see
[`../sql4cds-behaviour.md`](../sql4cds-behaviour.md):

- `IFetchXmlExecutionPlanNode` (public) exposes `FetchXmlString`.
- `IDataExecutionPlanNode` (public) exposes `EstimatedRowsOut` and `RowsOut`.
- `HashJoinNode`, `MergeJoinNode` and `NestedLoopNode` are the only concrete types deriving
  from `BaseJoinNode`; all are internal.
- `Sql4CdsCommand.GeneratePlan(bool compileForExecution)` compiles without executing.
