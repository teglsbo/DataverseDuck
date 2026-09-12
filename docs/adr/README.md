# Architecture decision records

Records of decisions that were expensive to make and would be expensive to reverse.
Each one states what was decided, what was rejected, and — importantly — the evidence.

Where a decision rests on how a library actually behaves rather than how it is
documented, the ADR cites a spike in [`../../spikes`](../../spikes) that measured it.
Measured facts that are not themselves decisions live in
[`../sql4cds-behaviour.md`](../sql4cds-behaviour.md).

| ADR | Decision | Status |
|---|---|---|
| [0001](0001-sql4cds-over-tds-and-odata.md) | Use SQL 4 CDS rather than the TDS endpoint or a DuckDB OData extension | Accepted |
| [0002](0002-utc-naive-timestamps.md) | Store every timestamp as a naive UTC `TIMESTAMP` | Accepted |
| [0003](0003-metadata-snapshot-for-offline-work.md) | Capture Dataverse metadata to a snapshot for offline work | Accepted |
| [0004](0004-execution-plan-folding-guard.md) | Analyse the execution plan and refuse queries that do not fold | Accepted |
| [0005](0005-detect-textual-json-timestamps.md) | Detect timestamp columns that JSON inference left as text | Accepted |
| [0006](0006-key-set-pushdown.md) | Let DuckDB choose the rows, but do the fetching ourselves | Accepted |
| [0007](0007-not-extending-duckdb-for-pushdown.md) | Do not extend DuckDB to push down our predicates — yet | Accepted |
| [0008](0008-plan-syntax.md) | Let a query name its own sources | Accepted |
| [0009](0009-cache-manifest.md) | Record what each cached table is, in the cache itself | Accepted |
| [0010](0010-datetime-behaviour.md) | Read datetime behaviour from metadata, not from the value | Accepted |
| [0011](0011-duckdb-remains-the-orchestrator.md) | DuckDB remains the orchestrator; do not invert the stack under SQL 4 CDS | Accepted |
| [0012](0012-duckdb-v2-api-window.md) | DuckDB v2.0 exposes optimiser-time filters, not ADR 0007's init-time filter set | Proposed |
| [0013](0013-persist-the-device-code-token-cache.md) | Persist the device-code token cache to disk | Accepted |

## Why there is no CHANGELOG

Git history is the changelog. Commits are written to be read: one logical change each,
with the reasoning in the body rather than the diff. `git log --oneline` gives the
summary a `CHANGELOG.md` would, without the duplication and the drift.

A hand-written changelog earns its place when there are external consumers who need
release-level notes. If this ever ships as a package, add one then and generate it from
the history.

## Writing a new ADR

Copy the shape of an existing one:

- **Context** — the forces at play, including constraints that are not negotiable.
- **Options considered** — including the ones rejected, and *why*. This is the part
  future readers actually need.
- **Decision** — what was chosen.
- **Consequences** — both directions. Be honest about what the decision costs.
- **Evidence** — links to spikes, documentation with dates, and measurements.

Number sequentially. Do not edit an accepted ADR to reflect a new decision; write a new
one that supersedes it, and mark the old one `Superseded by ADR NNNN`.
