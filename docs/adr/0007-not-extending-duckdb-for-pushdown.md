# 7. Do not extend DuckDB to push down our predicates — yet

Date: 2026-08-16

## Status

Accepted.

## Context

ADR 0006 chose an explicit `{{ }}` key-set marker because DuckDB would not hand
our scan the rows it actually needed. That ADR's closing paragraphs implied the
missing capability was join-key propagation. Measurement showed that was wrong,
and the correction is recorded at the end of ADR 0006. The engine already does
the hard part.

This ADR answers the question that follows: **could we extend DuckDB so that
WHERE predicates and join predicates reach us properly?**

Three facts frame it.

**First, WHERE and join predicates arrive by the same path.** Static optimiser
filters and hash-join dynamic filters are merged into one `TableFilterSet` by
`DynamicTableFilterSet::GetFinalTableFilters`, and that set is handed to
`init_global` before the scan produces a row. One fix unlocks both. There is no
separate mechanism to build for joins.

**Second, the timing is already right for a remote scan.**
`PhysicalHashJoin::Finalize` runs after the build side is fully consumed, and
only then is the probe-side scan initialised. So the keys are known *before* we
would issue an HTTP request. This is the property that would make the whole idea
work, and it is not something we would have to negotiate for.

**Third, the payload is already the right shape — conditionally.** Measured on
DuckDB 1.5.5 (`spikes/PushdownSpike`):

    join keys | filter delivered to the scan
    ----------|--------------------------------------------------
           10 | optional: contactid IN (<10 exact GUIDs>) AND >=min AND <=max
           50 | exact IN list
           51 | min/max GUID range only
         1000 | min/max GUID range only

The cliff at 51 is `dynamic_or_filter_threshold`, default 50. Setting it to
100000 restores exact IN lists at 1,000 and 20,000 keys.

So the engine builds precisely the artefact ADR 0006 builds by hand. What stops
us using it is a gap in the C boundary, plus a heuristic tuned for a cost model
that is the inverse of ours.

### Where the gap actually is

`src/main/capi/table_function-c.cpp` on current `main`:

```cpp
struct CTableInternalInitInfo {
	const CTableBindData &bind_data;
	CTableInitData &init_data;
	const vector<column_t> &column_ids;   // line 51
	optional_ptr<TableFilterSet> filters; // line 52
	bool success;
	string error;
};
```

`filters` is populated at lines 127 and 143 and then never read again. Its
neighbour `column_ids` gets two accessors in the same file —
`duckdb_init_get_column_count` and `duckdb_init_get_column_index`. `filters`
gets none. The data is present, one line away from an existing precedent for
exposing it.

Both C headers confirm the consequence: `duckdb.h` and `duckdb_extension.h` each
export exactly one pushdown symbol,
`duckdb_table_function_supports_projection_pushdown`. No C-ABI binding —
DuckDB.NET included — can read a filter.

## Options considered

### A. Patch DuckDB to expose filters through the C API

Add accessors mirroring the `column_ids` precedent: an opt-in
`duckdb_table_function_set_filter_pushdown`, a `duckdb_init_get_filter`, an
opaque `duckdb_table_filter` handle with a type discriminator, and per-type
readers for constant comparisons, IN lists, conjunctions, null checks and the
optional wrapper. Roughly 16–20 new symbols plus an enum — comparable in size to
the `expression` module added to the C API after v1.4.0.

Versioning is not an obstacle. The extension API is semver'd
(`DUCKDB_EXTENSION_API_VERSION` currently 1.5.6) and generated from a YAML spec,
so additive functions are a routine minor bump rather than an ABI break.

This is the architecturally correct fix. It unlocks WHERE *and* join predicates
for every language binding at once, and it would let this project stay in C#
with SQL 4 CDS intact — which is the whole reason the question was asked.

Two problems. The C++ work is plumbing, but the design is not: on current `main`
the concrete filter types have been renamed `LEGACY_*` and optimiser-generated
filters are becoming `EXPRESSION_FILTER`, which has no stable structured
representation to expose. A scoped PR would have to expose the structured types
and tell callers to skip expression filters — defensible, but a real design
decision made on someone else's roadmap.

And it does not exist. Every released DuckDB lacks it.

### B. Rely on upstream appetite

Rejected as a plan, though it informs the others.

There is exactly one relevant upstream issue: **#20613**, "TableFunction
filter_pushdown ability to fully pushdown disjoint sets", filed 2026-01-20 by
Rusty Conover, still open and labelled *under review*. Mytherin's only reply,
two days later:

> "we can think about adding a new callback that allows table filters more
> control over pushdown for sure. However, all that would save in this case is
> the additional `FILTER` that is added to the plan. Nothing prevents the scan
> from actually resolving the filter in the current implementation. […] Have you
> done any benchmarks that show the added `FILTER` is significant […]? If not […]
> I would not put a particularly high priority on this."

That reply is reasoning about local scans, where an unresolved filter costs one
cheap operator. For a remote scan an unresolved filter costs the entire table
over the network. The maintainers are not hostile; the remote-scan cost model
simply is not in view. No activity since January 2026, no PR.

Searching `duckdb-rs` issues for the same request returns nothing, even though
every Rust extension author hits this exact wall. Nobody has made the case.

So the channel is open and unattended. That is an argument for filing a PR one
day, not for waiting on one.

### C. Make the join threshold controllable per table function

`TableFunction` has `filter_pushdown`, `filter_prune`, `sampling_pushdown`,
`projection_pushdown` and several pushdown callbacks. It has **no** `is_remote`,
`remote_cost` or latency field — a verified absence, not an unsearched gap.
`CanUseInFilter` consults only the global setting, though the hash join can
already reach the scan operator when it runs.

A per-function override is perhaps fifty lines. It is the easiest change to
justify to a maintainer, because it needs no new API surface and the argument is
purely about cost: for an HTTP round trip, a 20,000-value IN list is a large win
where for a local scan it is overhead.

But on its own it does nothing for us. Raising the threshold only matters if you
can *read* the resulting IN list, which needs Route A. Route C is a foothold,
not a solution.

### D. Write a C++ extension and take the filters directly

Available today, and `erpl_web` proves the surrounding pieces work: it is a C++
extension in the community registry with a `crm_read` for Dataverse, real
`filter_pushdown = true`, and client-credentials auth built the same way ours is.

Rejected here for the reason ADR 0001 chose SQL 4 CDS: it would trade a mature
T-SQL-to-FetchXML compiler for hand-written filter translation. `erpl_web`
illustrates the cost precisely — its translator handles constant comparisons,
null checks, conjunctions and the optional wrapper, but has no `IN_FILTER` case.
Since `OPTIONAL_FILTER` delegates to its child, and the child of a join-derived
filter *is* the `InFilter`, a small join against `crm_read` falls through to
`default:` and throws `std::runtime_error`. Their pushdown is sound for
`WHERE statecode = 0` and unproven against joins — which is the shape of our
workload.

### E. Keep the explicit key set

The status quo from ADR 0006.

## Decision

Do not extend DuckDB now. Keep the `{{ }}` key-set marker as the mechanism that
gets rows fetched selectively.

Record the patch shape above so the decision can be revisited cheaply rather
than re-researched. If this project ever needs pushdown to be automatic, the
order is C then A: file the threshold override first as a small, purely
cost-motivated change that builds maintainer understanding of the remote-scan
problem, then propose the filter accessors with that context established.

## Consequences

- We keep visible syntax where a mature connector would need none. That cost was
  already accepted in ADR 0006 and has not changed.
- We also keep determinism, which is worth more here than it first appears. The
  automatic mechanism is gated on a tunable heuristic and degrades **silently**:
  above the threshold the exact IN list is replaced by a min/max range, which
  over random GUIDs matches essentially every row. There is no warning, no
  profiling event and no `EXPLAIN` flag distinguishing "an IN list was discarded
  because of the threshold" from "only a range was ever possible". A user would
  discover it as an unexplained full table transfer. `{{ }}` cannot fail that
  way.
- Staying on the C ABI means projection pushdown remains the only automatic
  narrowing we get. That is real and precise — DuckDB requests exactly the
  columns needed, including ones only a predicate touches — but it narrows
  columns, never rows.
- We are not blocked on anyone. Nothing here waits for an upstream release.
- The main risk is drift: `EXPRESSION_FILTER` is replacing the structured filter
  types on `main`, so the Route A design sketched above will age. If it is ever
  attempted, re-read the source rather than trusting this ADR's shape.

## Evidence

- `spikes/PushdownSpike` — measures the threshold cliff and the rescue on
  DuckDB 1.5.5, by reading `Dynamic Filters` out of `EXPLAIN ANALYZE`.
- `duckdb/duckdb:src/main/capi/table_function-c.cpp` — `CTableInternalInitInfo`
  holds `filters` (line 52) unused; `column_ids` (line 51) has accessors at
  lines 446–463.
- `duckdb/duckdb:src/include/duckdb.h` and `src/include/duckdb_extension.h` —
  grep for `filter|pushdown` returns only
  `duckdb_table_function_supports_projection_pushdown` in both.
- `duckdb/duckdb:src/include/duckdb/main/settings.hpp` —
  `DynamicOrFilterThresholdSetting`, `DefaultValue = "50"`.
- `duckdb/duckdb:src/execution/operator/join/physical_hash_join.cpp` —
  `CanUseInFilter` reads only the global setting; `PushInFilter` builds
  `OptionalFilter(InFilter(values))`.
- `duckdb/duckdb:src/include/duckdb/function/table_function.hpp` — no remote or
  cost hint field exists.
- [duckdb/duckdb#20613](https://github.com/duckdb/duckdb/issues/20613) — the one
  upstream request, open and under review, replied to 2026-01-22.
- `DataZooDE/erpl-web:src/odata_predicate_pushdown_helper.cpp` —
  `TranslateFilter` has no `IN_FILTER` case; `OPTIONAL_FILTER` delegates to its
  child; `default:` throws.
- `DataZooDE/erpl-web:src/dataverse_secret.cpp` — `client_credentials` is the
  default and only provider; scope is `environment_url + "/.default"`.
