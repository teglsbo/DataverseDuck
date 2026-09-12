# PushdownSpike

Measures what DuckDB's automatic join-filter pushdown actually hands a table function, and
pairs it with what the missing filter costs when the scan is a network round trip.

`Program.cs` is the measurement. This README is the argument it supports, assembled so it
can be posted upstream without re-deriving anything. See ADR 0007 for the decision it
informs and ADR 0012 for why the target moved.

## Why this exists

DuckDB's maintainers have twice reasoned about filter pushdown from the cost model of a
local scan. On [#20613](https://github.com/duckdb/duckdb/issues/20613), Mytherin:

> all that would save in this case is the additional `FILTER` that is added to the plan.
> Nothing prevents the scan from actually resolving the filter in the current
> implementation. […] Have you done any benchmarks that show the added `FILTER` is
> significant […]? If not […] I would not put a particularly high priority on this.

That is correct for a local scan, where an unresolved filter costs one cheap operator over
data already in memory. It is the wrong model for a scan whose rows arrive over a
rate-limited HTTP API, where an unresolved filter costs the entire table.

Nobody has answered it with numbers. Three PRs offered the API
([#8163](https://github.com/duckdb/duckdb/pull/8163),
[#14591](https://github.com/duckdb/duckdb/pull/14591),
[#19093](https://github.com/duckdb/duckdb/pull/19093)) and none carried a cost measurement
from a real remote source. That is the gap this fills.

## What changed in DuckDB v2.0

**The cliff is real on 2.0 too, but it is hidden, and the first attempt to measure it here
got the wrong answer.** Recorded because the wrong answer is easy to reach.

Reading `EXPLAIN ANALYZE` against v2.0 (`v2.0-cyanoptera`, built from source) reports the
same thing at every size:

    join keys | threshold | Dynamic Filters
           10 |        50 | optional: i IN PRF(v)
           60 |        50 | optional: i IN PRF(v)
           60 |      1000 | optional: i IN PRF(v)
       20,000 |   100,000 | optional: i IN PRF(v)

From that it looks as though 2.0 replaced the exact key set with a **prefix range filter**
at all sizes. It has not. `EXPLAIN`'s rendering is lossy: when the engine pushes both an
exact `IN` list and a prefix range filter for the same column they are ANDed together, and
only the second appears in the plan text. The rendering cannot be used to tell whether a
key set is present.

Reading the filters through the C API instead — which required patching DuckDB, see below —
gives the real behaviour, and it matches ADR 0007's 1.5.5 finding:

    join keys | threshold | exact key set reaches the scan?
           10 |        50 | yes
           60 |        50 | no -- dropped, probe filter only, silently
           60 |      1000 | yes

So `dynamic_or_filter_threshold` still governs the key set on 2.0. The cliff was never
removed; it became harder to observe.

### What a prefix range filter is, and why it does not help here

`PrefixRangeFilter` is "runtime prefix-range filter state used by join pushdown and
internal tablefilter functions". It is initialised from the build side's minimum, maximum
and a bit budget, keys are inserted, and it is then probed in bulk:
`LookupKeys(Vector &keys, SelectionVector &result_sel, idx_t count)`. Effectively a bitmap
over the key range — exact while the span fits the budget, lossy above it.

It is **probe-only**. You hand it keys you already hold and it reports which might match.
There is no enumeration, so a scan cannot turn one into a `WHERE ... IN (...)`. For a local
scan that is fine and often better than a list. For a scan that must name the rows it wants
before issuing a request, it is worth nothing.

### The blocker was not the threshold

Instrumenting the engine showed the key set being pushed correctly all along
(`pushing IN list with 60 values`). The reason a table function could not see it is
separate and more interesting: `CreateOptionalFilterExpression` stores the predicate in
`OptionalFilterFunctionData` — **bind data** — and puts only the column in the function's
argument list. The v2 expression API walks arguments, so a consumer receives a
`BOUND_FUNCTION` whose single child is a column reference, with the values invisible inside.

That blind spot is in the **shipped** v2 surface, not only in anything added here: every
runtime filter the engine pushes uses one of these wrappers, so
`table_function_filter_pushdown_get_filter` — merged 2026-09-03 and tagged `stable` — cannot
read a wrapped predicate either.

Both halves are fixed in the patch described below, and the behaviour above is asserted in
DuckDB's own test suite.

## Half one: what DuckDB pushes (v1.5.5)

`dotnet run --project spikes/PushdownSpike`, in-memory, no tenant required. A 200,000-row
table keyed by random GUIDs, joined against a sampled key set, reading `Dynamic Filters:`
out of `EXPLAIN ANALYZE`. Measured on **DuckDB 1.5.5**:

    join keys | pushed filter carries an enumerable key set?
    ----------|-----------------------------------------------
           10 | YES - exact IN list
           50 | YES - exact IN list
           51 | NO  - min/max GUID range only
          100 | NO  - min/max GUID range only
         1000 | NO  - min/max GUID range only

    after SET dynamic_or_filter_threshold = 100000

         1000 | YES - exact IN list
        20000 | YES - exact IN list

The cliff is `dynamic_or_filter_threshold`, default **50**. Above it the exact `IN` list is
replaced by a min/max range over the key column. Over random GUIDs that range matches
essentially every row in the table, so the filter conveys nothing.

Two properties matter as much as the cliff itself. It is **silent** — no warning, no
profiling event, no `EXPLAIN` flag distinguishing "an IN list was discarded because of the
threshold" from "only a range was ever available". And it is **absolute** — 50 keys
regardless of whether the table behind the scan holds 5,000 rows or 50 million.

## Half two: what the missing filter costs

Measured against a live Dataverse environment on 2026-08-16 using `solutioncomponent`
(56,161 rows, three narrow columns), recorded in [docs/large-tables.md](../../docs/large-tables.md).

Sustained read rate is **~8,000 rows/second**, page size is 5,000 rows, and fetching rows
by key is flat below about 1,000 keys — 1,000 keys costs the same as 1.

For a join of 1,000 local keys against that table, the two branches of the cliff are:

| | Filter reaching the scan | Rows transferred | Time | Requests |
|---|---|---|---|---|
| 50 keys | exact `IN` list | matching only | **0.2s** | 1 |
| 51 keys | min/max GUID range | all 56,161 | **7.1s** | 12 |

Same query, same data, a 35x difference in time and 12x in requests, decided by a default
of 50.

Extrapolating at the measured rate, holding the key set at 1,000:

| Table rows | With `IN` list | With range only | Ratio | Requests |
|---:|---:|---:|---:|---:|
| 56,161 | 0.2s | 7.1s | 35x | 1 → 12 |
| 1,000,000 | 0.2s | ~125s | ~600x | 1 → 200 |
| 10,000,000 | 0.2s | ~21min | ~6,000x | 1 → 2,000 |

At the bottom row it stops being a performance question. Dataverse allows a user **20
minutes of combined execution time per 5-minute sliding window** and roughly 6,000 requests
in the same window (8,000 measured on our environment; the documentation warns the number
varies). A 10M-row transfer exhausts the execution budget on its own and spends a third of
the request budget doing it — and that budget is per user, so it takes out every other
client sharing the app registration. The query does not run slowly; it does not complete,
and it throttles its neighbours.

## The pairing

DuckDB abandons the exact key set at **50 keys, absolute**.

The measured break-even on the remote side is **K = N/5** — push the keys while they number
fewer than a fifth of the table's rows, because past that you are paying more to describe
the subset than to fetch everything.

Those two numbers are not close:

| Table rows | Break-even keys (N/5) | DuckDB stops pushing at | Gives up early by |
|---:|---:|---:|---:|
| 56,161 | 11,232 | 50 | 225x |
| 1,000,000 | 200,000 | 50 | 4,000x |

The default is not merely conservative for a remote scan; it is on the wrong side of the
break-even by two to four orders of magnitude, and it gets worse as the table grows —
because the threshold is absolute and the break-even scales with N.

One objection worth pre-empting: that a large `IN` list is itself too expensive to ship.
Not on this transport. SQL 4 CDS folds an `IN` list into a single FetchXML
`<condition operator="in">` regardless of length, so the 500-condition limit does not
apply; 56,000 keys produced 2.1 MB of literals and still executed, and SOAP imposes no URL
length limit. The list is cheap to send and the cost is superlinear only past ~1,000 keys,
which chunking handles.

## What we are asking for

**On 1.x**, two things in this order, matching ADR 0007's sequencing. On 2.x the second is
partly answered and the first is no longer sufficient -- see the v2.0 section above:

1. **A per-table-function way to opt out of the threshold**, or a cost hint that says this
   scan is remote. `TableFunction` today has `filter_pushdown`, `filter_prune`,
   `sampling_pushdown`, `projection_pushdown` and several callbacks, but no `is_remote`,
   `remote_cost` or latency field — verified absence, not an unsearched gap.
   `CanUseInFilter` consults only the global setting.
2. **Access to the resulting filter from a C-ABI binding.** DuckDB v2.0 exposes
   optimiser-time predicates as expressions
   ([#25340](https://github.com/duckdb/duckdb/pull/25340)), which is real progress, but
   hash-join dynamic filters are a runtime artefact and do not reach that hook. The
   `TableFilterSet` handed to `init_global` still has no accessor. See ADR 0012.

Raising the threshold without (2) does nothing, and (2) without (1) delivers a range
instead of a key set above 50 keys. Neither is useful alone.

## Caveats on these numbers

One tenant, one day, one table, narrow columns. Microsoft publishes no throughput figures
and explicitly advises measuring rather than calculating, so treat the shapes as real and
the absolute values as indicative. Column width moves the scan rate substantially —
`solutioncomponent` was three columns, where `contact` has 311 — and a wider table scans
more slowly, which pushes the break-even higher and favours pushdown more strongly than
the table above shows.

The DuckDB half is a single version, 1.5.5, and v2.0 already behaves differently in a way
that invalidates its central claim. Re-run `Program.cs` before citing it against any other
version, and read the v2.0 section first.
