# 12. DuckDB v2.0 exposes optimiser-time filters, not ADR 0007's init-time filter set

Date: 2026-09-05

## Status

Proposed.

Does **not** supersede ADR 0007. Its decision and its analysis both stand. This ADR exists
because DuckDB v2.0 adds a filter pushdown C API that looks, from the specification alone,
like the capability ADR 0007 wanted — and is not. Recorded so that a future reader who
sees `duckdb_v2_table_function_filter_pushdown_*` in the headers does not conclude the
question is settled.

## Context

ADR 0007 asked whether DuckDB could be extended so that WHERE and join predicates reach a
table function, and located the gap precisely: `CTableInternalInitInfo` carries a
`TableFilterSet` at `init_global` that no C API accessor exposes.

Its argument for why that one gap mattered:

> **First, WHERE and join predicates arrive by the same path.** Static optimiser filters
> and hash-join dynamic filters are merged into one `TableFilterSet` by
> `DynamicTableFilterSet::GetFinalTableFilters`, and that set is handed to `init_global`
> before the scan produces a row. One fix unlocks both.

> **Second, the timing is already right for a remote scan.**
> `PhysicalHashJoin::Finalize` runs after the build side is fully consumed, and only then
> is the probe-side scan initialised. So the keys are known *before* we would issue an
> HTTP request.

DuckDB v2.0, on the `v2.0-cyanoptera` branch, ships seven
`table_function_filter_pushdown_*` functions tagged
`[ "stable", "v2.0.0", "2026-09-03" ]`. The question this ADR answers is whether they are
that fix.

They are not. They are a different hook, at a different point in the query lifecycle,
carrying a strictly smaller set of predicates.

## Investigation

**Branch matters.** `main` lags `v2.0-cyanoptera` for `api_spec/v2`: on `main` there is no
`function/table.yaml` at all and `properties.yaml` still shows `0x05 TABLE (reserved)`.
Reading `main` yields the opposite conclusion to the correct one, in both directions.
Verify against the release branch.

**What the specification says.** `api_spec/v2/function/table.yaml` (1,355 lines) describes
a callback

> invoked while the query is optimized, after the bind callback and before any init
> callback […] Each predicate is a bound expression the callback can inspect with the
> `expression` functions

with per-predicate acceptance: the engine stops applying what the callback accepts and
keeps applying the rest above the scan.

"Before any init callback" is the tell. ADR 0007's mechanism delivers at `init_global`.

**What the implementation does.** `src/main/capi/v2/capi_v2_func_table.cpp` settles it at
line 489:

```cpp
if (info.filter_pushdown_cb) {
    function.pushdown_complex_filter = CV2TableFilterPushdown;
}
function.projection_pushdown = info.projection_pushdown;
```

The C API callback is wired to **`TableFunction::pushdown_complex_filter`** — DuckDB's
long-standing optimiser-time hook — with the matching signature (line 416):

```cpp
static auto CV2TableFilterPushdown(ClientContext &context, LogicalGet &get,
                                   FunctionData *bind_data_p,
                                   vector<unique_ptr<Expression>> &filters) -> void
```

and the source's own comment above it:

> The optimizer hands over the predicates it would otherwise evaluate above the scan;
> whatever the callback accepts is removed from the list, and the engine keeps applying
> the rest itself.

The handle's fields say the same (lines 153–156): `in_filters` is "the predicates the
optimizer offers", and `in_column_ids` is "the scan's column list **at optimization
time**".

Three consequences follow, and the third is the important one.

1. **These are static predicates only.** `pushdown_complex_filter` runs in the
   `FilterPushdown` optimiser pass over a `LogicalGet`. Hash-join dynamic filters do not
   exist yet at that point — they are produced at runtime by
   `PhysicalHashJoin::Finalize`, which is exactly the timing ADR 0007 identified as the
   property that would make a remote scan work.

2. **`filter_pushdown` is never set.** Line 491 sets `projection_pushdown` and nothing
   else. Without `function.filter_pushdown = true` no `TableFilterSet` is built for a
   v2 C API table function at all, so the `init_global` path is not merely unexposed —
   for these functions it is not populated.

3. **ADR 0007's gap is therefore still open in v2.** `CTableInternalInitInfo::filters`
   remains without an accessor. Issue #20613 remains the relevant upstream request; it is
   still open, still labelled *under review*, still one comment, last touched 2026-01-22.
   The v2 work did not come through it and did not address it.

**What v2 does give.** Genuine, useful, and not what we need: full bound expression trees
for static predicates, inspectable through a new `expression` module, with partial
acceptance — a callback may accept the two predicates it understands and leave the third
to the engine. For a scan whose selectivity comes from literal `WHERE` clauses this is a
real capability that v1 could not offer any C-ABI binding.

**A maintainer states the same distinction.** On issue #19818, which asks precisely how
`filter_pushdown` and `pushdown_complex_filter` differ, samansmink answers:

> I think once you set `filter_pushdown` your TableFunction is expected to indeed handle
> all pushed down table filters. For a more finegrained implementation you should use
> `pushdown_complex_filter`

So the two are alternative mechanisms with different contracts, and v2 chose the
fine-grained one. This is not an oversight in the v2 port; it is the documented use of
that hook.

**And the authors say it is not finished.** PR #25340, which added all of this, describes
the feature as "expression claim-based filter pushdown" and adds:

> Filter pushdown has been a long requested feature, and we will continue to refine this
> API to make it more powerful as we hopefully rework expressions more soon.

with the PR opening "nothing is set in stone - there will for sure be more follow-up PRs,
polishes, and features added that are currently blocked on core-work". The surface is
stable-tagged but explicitly expected to grow.

**The threshold cliff survives into v2.0, and a patch against the release branch resolves
both halves of ADR 0007's Route A/C.** This was worth establishing by building it, because
reading the plan text gives the wrong answer.

`EXPLAIN ANALYZE` on v2.0 reports `optional: i IN PRF(v)` at every key count and every
threshold, which reads as though the exact key set had been replaced everywhere by a
prefix range filter. It has not. When both an exact `IN` list and a prefix range filter are
pushed for one column they are ANDed, and only the latter appears in the plan text.

Read through the C API instead, the behaviour matches what ADR 0007 measured on 1.5.5:

    join keys | threshold | exact key set reaches the scan?
           10 |        50 | yes
           60 |        50 | no -- dropped silently, probe filter only
           60 |      1000 | yes

A prefix range filter is a bitmap over the key range, probed in bulk
(`LookupKeys(Vector &keys, SelectionVector &, idx_t)`). It answers "might this value be
present" for keys the caller already holds. Sound for a local scan; worth nothing to a scan
that must name the rows it wants before issuing a request.

**The real obstacle was not filter selection.** Instrumenting the engine showed the key set
being pushed correctly throughout. A table function could not read it because
`CreateOptionalFilterExpression` keeps the predicate in `OptionalFilterFunctionData` — bind
data — exposing only the column as the function's argument. The v2 expression API walks
arguments, so a consumer sees a `BOUND_FUNCTION` over a column reference and nothing else.

That blind spot is in the shipped surface: `table_function_filter_pushdown_get_filter`,
merged 2026-09-03 as `stable`, cannot read a wrapped predicate either, and every runtime
filter uses one of these wrappers.

**A working patch exists**, against `v2.0-cyanoptera`, in
`spikes/PushdownSpike/README.md` and the accompanying diff. It adds a
`join_filter_pushdown` opt-in distinct from `filter_pushdown`, a per-function
`join_filter_in_threshold`, init-time filter accessors, and exposure of wrapped predicates
through the expression API. All 446 `[capi_v2]` tests pass, including new cases asserting
the table above. It also fixes two incidental bugs found on the way: `plan_get.cpp` drops
`dynamic_filters` entirely when a function does not enable projection pushdown, and the
`unstable` lifecycle tier — which no symbol had ever used — does not compile, because the
generator guards public declarations but not the internal api struct.

**None of which changes this ADR's decision.** The patch is unmerged work against an
unreleased branch; DuckDB.NET does not expose the v2 API; and a C# extension would still
face the objections in option B. What it changes is the *evidence*: the upstream request is
now demonstrable rather than argued, which is a materially better position than ADR 0007
had.

## Options considered

### A. Record the correction; change nothing

**Adopted.** See Decision.

### B. Build a C# extension against the v2 API and drop `{{ }}`

The accessors are C ABI, so a binding could read them, and `DuckDB.ExtensionKit` already
demonstrates C# over the C API via Native AOT.

**Assumed reachable.** This ADR takes it as given that DuckDB.NET will track v2 and expose
the new surface. That is an assumption, not a verified plan, and it is deliberately the
*generous* one: it removes reachability from the argument so the rejection below rests
only on substance. If DuckDB.NET does not follow, option B is simply unavailable and
nothing here changes.

**Rejected, on two independent grounds.**

It does not deliver the case `{{ }}` exists for. Our selectivity comes from keys in a
local JSON table joined against a Dataverse scan; those arrive as hash-join dynamic
filters, which this hook does not see. An extension built on it would push down literal
`WHERE` clauses and still transfer whole tables for every join.

And the translation objection from ADR 0001 E and ADR 0007 D is now larger, not smaller.
v2 hands over bound expression trees rather than the structured comparison/IN/conjunction
types ADR 0007 costed, and acceptance is a promise: the engine stops applying an accepted
predicate, so a translator that mishandles one returns wrong rows rather than throwing.
`erpl_web` throwing on an unhandled `IN_FILTER` is the benign version of that failure.

That risk is not hypothetical, and it did not take long to appear. Issue #25391, filed
2026-09-05 — one day after #25340 merged — reports that v2 hands a callback `b > 1 AND
b < 4` and `b >= 1 AND b <= 4` as the *same* `DUCKDB_V2_EXPRESSION_TYPE_COMPARE_BETWEEN`
node, with strict and inclusive bounds indistinguishable. In the reporter's words, "any
code reading the callback picks would be wrong for one of the forms". A translator that
accepted such a predicate would return wrong rows, silently, with the engine no longer
checking. That is the precise failure this option is rejected to avoid, demonstrated
against the shipped API within a day of it landing.

### C. Pursue ADR 0007's Route A / Route C upstream

**ADR 0007 concluded the channel was "open and unattended" and that "nobody has made the
case".** Both were true as far as that ADR looked. The fuller record says something more
useful, and it should replace the earlier reading rather than sit beside it.

Route A was attempted three times, by three unconnected people, over three years. Each
attempt offered the structured-`TableFilter` shape ADR 0007 sketched. None was rejected on
merit; all three stalled on the same unresolved internal design question.

- **#8163** (2023, Kayrnt). Ran straight into the obvious obstacle: `TableFilterSet` is a
  C++ class, not a C struct, so exposing it means a mirror struct plus lifetime
  management. Mytherin closed it — "this needs some more thought as it is not functional
  in its current state. If you want to pick this up again I would recommend drafting a
  proposal so we can discuss what this should look like." The author's closing note is the
  part worth remembering: "I switched back to C++ for my extension because I think that
  it's a lot of work to use C bindings for some use cases."
- **#14591** (2024, prashanthellina). Thirteen symbols with tests —
  `duckdb_init_get_table_filters`, `duckdb_table_filters_get_filter`,
  `duckdb_table_filter_get_type` / `_get_constant` / `_get_child` — against ADR 0007's
  estimate of sixteen to twenty. Maxxen's first response was **positive**: "at first
  glance looks good!", with one caveat, that a function signalling `filter_pushdown` must
  handle *every* filter type, and that functions should be able to declare or reject the
  ones they support, "since we want the C-API to be stable I think we should make sure
  that the interface added by this PR can be extended to accommodate that". The author
  asked for API feedback before re-issuing against `feature`. It never arrived. Eighteen
  months later joseph-isaacs asked "would be great to get this moving again, anything I
  can do to help" and got no reply. The stale bot closed it in April 2026.
- **#19093** (2025, halgari). taniabogatsch answered plainly: "we're still undecided on
  how to expose the filters in the C API. Your proposal, which mimics the current C++ API,
  is the most straightforward possibility. However, we've talked about refactoring the C++
  table filters to become generic DuckDB expressions a few times […] If we decide to go
  with that solution, table filters in the C API should reflect that and also use
  expressions." She cross-posted to #14591: "Ideally, filters (on the C API side) should
  be 'just' expressions."

**v2 is the resolution of that question, built in-house.** #25340 ships expression-based
predicates with per-predicate `accept` — taniabogatsch's expressions, and Maxxen's
declare-what-you-support, together. The two objections that blocked three PRs were both
answered; the PRs were overtaken rather than merged.

So the accurate correction to ADR 0007 is not that the case went unmade. It is that the
case was made three times, competently and with working code, and was blocked by an
internal design question the maintainers had not settled. That is a different problem with
a different remedy, and it is now settled.

**The part still genuinely unasked is ours.** Every one of those attempts, and #25163,
framed the request as "expose table filters to table functions". The
`TableFilterSet`-at-`init_global` path — where hash-join dynamic filters arrive merged, and
the only path that would serve this project — was incidental to the ask rather than the
point of it. Nobody has argued for *dynamic* filters reaching a remote scan before the
request goes out. #20613 is the closest anyone came, and Mytherin's reply there reasons
from local scans — "nothing prevents the scan from actually resolving the filter […] I
would not put a particularly high priority on this" — which is cost scepticism about a
different question, not a refusal of this one.

**The ask itself has changed, and that is the main thing to carry forward.** On 1.x it was
"raise the threshold so the exact key set survives". On 2.0 that request is meaningless:
the threshold no longer selects the shape. The 2.x ask is "let a scan decline probe-shaped
runtime filters and receive the key set", which is a design question about where DuckDB's
runtime filters are heading rather than a knob to expose.

An attempt was made to answer it in code, against the release branch: a per-function
threshold override plus suppression of the prefix range filter and the deferred bloom
filter. The engine does rebuild the `IN` list under that override — `CanUseInFilter` and
`PushInFilter` both succeed — but a probe-shaped filter still reaches the scan from a path
not yet identified, and `TableFilterSet::PushFilter` ANDs rather than replaces, so the list
is absent rather than overwritten. Whoever picks this up should start there.

**Deferred, with a clearer target than ADR 0007 had.** If this is pursued, the live thread
is #25163 and the follow-up work #25340 promises, not #20613. The contribution that would
be genuinely new is not another patch — three of those already exist and a fourth adds
nothing — but evidence, and it comes in two halves of which we hold only one.

`spikes/PushdownSpike` supplies the **mechanism** half, and supplies it well: against
DuckDB 1.5.5 it shows the exact `IN` list surviving to 50 keys, collapsing to a bare
min/max GUID range at 51, and being restored at 1,000 and 20,000 keys once
`dynamic_or_filter_threshold` is raised to 100000. Over random GUIDs that range matches
essentially every row, and the degradation is silent — no warning, no `EXPLAIN` flag. That
is the part of Mytherin's reply on #20613 that is answerable with a measurement.

It does **not** supply the **cost** half on its own. The spike measures the shape of the
filter that reaches the scan, in memory, on synthetic data; it never issues a request or
times one. That half comes from `docs/large-tables.md`, measured against a live environment
on 2026-08-16.

The two are now paired in `spikes/PushdownSpike/README.md`, and the pairing is sharper than
either half suggested. DuckDB abandons the exact key set at **50 keys, absolute**. The
measured remote break-even is **K = N/5** — 11,232 keys on the 56,161-row table actually
tested, 200,000 on a table of a million rows. The default is not conservative for a remote
scan; it sits two to four orders of magnitude on the wrong side of the break-even, and the
gap widens with table size because the threshold is absolute while the break-even scales
with N. At ten million rows the un-pushed branch exhausts Dataverse's entire 20-minute
execution budget per five-minute window and spends a third of the request budget, so the
query does not complete and takes its neighbours on the same app registration down with it.

That write-up is the contribution worth making, and it is now written rather than
described. Post it to #25163 whether or not this project ever depends on the outcome.

### D. Adopt v2 for the existing data path

**Not now, but this is the option the DuckDB.NET assumption actually bears on.** Taking it
as given that DuckDB.NET tracks v2, the question stops being *whether* this codebase meets
v2 and becomes *when*, on someone else's release schedule rather than ours.

That makes one thing worth auditing before it is urgent: `DuckDbBulkLoader` depends on the
Appender at ~440k rows/sec, and the v2 tree's appender story has not been examined here.
v2 symbols are namespaced `duckdb_v2_`, which suggests coexistence with v1 rather than
replacement — an inference from a naming convention, not a compatibility guarantee. If
that inference holds, a DuckDB.NET v2 release is additive and this codebase can stay on
the v1 surface indefinitely. If it does not, the Appender path is the migration's critical
item and worth knowing about early.

Nothing to do today. Re-read when DuckDB.NET announces v2 support, and audit the Appender
first.

## Decision

Change nothing. Keep `{{ }}`, keep SQL 4 CDS, keep ADR 0007 in force.

Record that ADR 0007's analysis survives v2.0 intact, including the parts most at risk:

- Its **gap** is still real. `TableFilterSet` at `init_global` is still unexposed, and for
  v2 C API functions is not even populated.
- Its **timing argument** is still the crux, and v2's hook sits on the wrong side of it.
- Its **determinism argument** is untouched, and the silence is worse than recorded. Above
  the threshold the key set is dropped and a probe-shaped filter remains, which a scan
  cannot distinguish from a query that never offered keys — and the plan text shows the
  same string either way, so `EXPLAIN` will not reveal it. `{{ }}` cannot fail that way.
- Its **drift warning** — "re-read the source rather than trusting this ADR's shape" —
  earned its keep. Reading v2's specification alone gives the wrong answer; reading
  `main` instead of the release branch gives a different wrong answer. Both were made
  while writing this ADR before the implementation settled it.

No spike is needed to answer the dynamic-filter question. `pushdown_complex_filter` is an
optimiser hook and hash-join filters are a runtime artefact; the source states which one
the callback receives. A spike would only be worth running to test the reverse claim —
that some future v2 revision also wires `filter_pushdown` — which nothing currently
suggests.

### When v2.0 ships

Everything above was read from a release branch before GA. Re-check it against the tag,
in this order, and stop as soon as an answer makes the rest moot.

**1. Has the wiring changed?** This ADR's whole conclusion rests on two lines in
`src/main/capi/v2/capi_v2_func_table.cpp`.

    grep -n 'pushdown_complex_filter\|filter_pushdown' src/main/capi/v2/capi_v2_func_table.cpp

Expected: the callback is bound to `pushdown_complex_filter`, and `function.filter_pushdown`
is assigned nowhere. If instead `function.filter_pushdown = true` appears, or
`api_spec/v2/function/table.yaml` has gained an accessor reachable from the init callbacks,
then dynamic filters may reach a C API table function and **option B needs re-examining
properly** — that is the one outcome that makes this ADR stale rather than merely dated.

**2. Did ADR 0007's literal gap close on the v1 side?** Independent of v2:

    grep -c filter src/include/duckdb.h        # expected: 0
    grep -n 'duckdb_init_get_' src/include/duckdb.h

Expected: still `bind_data`, `column_count`, `column_index`, `extra_info`. A
`duckdb_init_get_filter` appearing would reopen ADR 0007 directly.

**3. Did the upstream threads move?** Check **#25163** first — it is Route A as asked for
by another remote-scan extension author, open and uncommented since 2026-08-30. Then the
follow-up PRs #25340 promises. #20613 is a different question and dormant since
2026-01-22. Also worth a look: **#25391**, the strict-versus-inclusive `BETWEEN`
ambiguity; if that is still open, the v2 filter surface is not yet safe to translate
from.

**4. The cache storage version — decided, and the only item here that touches users.**
v2 ships a new default storage format; `src/storage/storage_info.cpp` sets
`DEFAULT_STORAGE_VERSION_INFO = StorageVersion::V2_0_0`, with `"latest"` mapping to the
same. The concrete boundary, from `VERSION_NUMBER` on each release branch:

    v1.5  writes header version 64, reads 64-68
    v2.0  writes header version 69, reads 64-69

So v2 opens every existing cache, and a v2-written cache is exactly one past what a v1.5
client can read. That client fails at open in
`single_file_block_manager.cpp`, with DuckDB's own message naming both versions, saying the
file "was created with a newer version of DuckDB", and linking its storage page.

**Decision: do not pin, document the one-way upgrade.** Pinning `STORAGE_VERSION` would
freeze every cache at an old format to protect a case that deleting the cache also solves —
a `--db` is a cache, not an archive — and DuckDB's error is already self-explanatory, so a
custom check would add nothing. Recorded in the README under "A cache upgrades one way".

The residual rule, worth restating at GA because it is the part users hit: a cache shared
between machines is only as portable as the oldest `dvduck` that must read it. Keep such
caches disposable, or standardise the `dvduck` version across the machines sharing one.

**5. Audit the Appender before migrating.** Per option D, `DuckDbBulkLoader` is the
critical item, and the audit is worth doing before DuckDB.NET forces the timing.

**Checked and clear:** the completed lambda-syntax transition, the other named breaking
change, does not touch this codebase — no `list_transform`, `list_filter`, `list_reduce`,
`array_transform` or lambda syntax appears anywhere in `src`, `tests` or `docs`.

## Consequences

**Positive**

- ADR 0007 does not need revisiting on its merits, and the temptation to revisit it on a
  misread of the v2 headers is now documented against.
- The upstream case is cheaper to make than it was: v2 supplies precedent for exposing
  filters to C API table functions, so Route A is no longer a novel proposal.
- Static predicate pushdown becoming available to C-ABI bindings is worth knowing if this
  project's query shape ever changes — for a workload driven by literal `WHERE` clauses
  rather than join keys, the calculus would be different.

**Negative, and why it is accepted**

- We remain on `{{ }}`, with the visible syntax cost ADR 0006 accepted, for at least
  another major DuckDB version.
- This ADR reasons from source rather than from a running binary. The wiring at line 489
  is unambiguous, but the claim that no dynamic filter can reach an optimiser pass is
  inference from DuckDB's execution model — well founded, and not directly measured here.
- Two documents must now be read together to understand one decision. The convention in
  this directory forbids the alternative.

## Evidence

All v2 paths are on `v2.0-cyanoptera` (head `e3946f232`, 2026-09-04). `main`
(`154c2c8d5`, same day) lags for `api_spec/v2` and must not be used for this question.

- `duckdb/duckdb@v2.0-cyanoptera:src/main/capi/v2/capi_v2_func_table.cpp`
  - line 489 — `function.pushdown_complex_filter = CV2TableFilterPushdown;`
  - line 491 — `function.projection_pushdown = info.projection_pushdown;`, and
    `function.filter_pushdown` is never assigned anywhere in the file.
  - lines 414–417 — the hook's comment and its
    `(ClientContext&, LogicalGet&, FunctionData*, vector<unique_ptr<Expression>>&)`
    signature.
  - lines 153–156 — `in_filters` "the predicates the optimizer offers";
    `in_column_ids` "the scan's column list at optimization time".
  - lines 440–445 — accepted predicates removed, remainder returned to the engine.
- `duckdb/duckdb@v2.0-cyanoptera:api_spec/v2/function/table.yaml` — 1,355 lines; seven
  `table_function_filter_pushdown_*` entries, each
  `lifecycle: [ "stable", "v2.0.0", "2026-09-03" ]`; `get_filter` returns type
  `expression`; the callback contract quoted above; no occurrence of `dynamic`, `join`,
  `runtime` or `probe`; `table_function_init_global_*` exposes column count and index
  only.
- `duckdb/duckdb@main:api_spec/v2/function/` — `aggregate`, `properties`, `scalar`,
  `signature` only; `properties.yaml` shows `0x05 TABLE (reserved)` with no keys.
  Recorded to document the trap.
- `duckdb/duckdb` recursive trees for both branches (neither truncated) — no path under
  `api_spec/` matching storage, attach or a catalog-provider module in v1 or v2.
- `duckdb/duckdb@main:api_spec/v1/catalog/catalog.yaml` — `client_context_get_catalog`,
  `catalog_get_type_name`, `catalog_get_entry`, `catalog_entry_get_type`,
  `catalog_entry_get_name`, plus destructors. Lookup only.
- `duckdb/duckdb@main:api_spec/v2/schema/schema.yaml` — "An ordered list of (name, type)
  fields: a row schema […] describing a bound statement's output (result columns) and
  input (parameters)". Not a database schema.
- `duckdb/duckdb@main:src/include/duckdb.h`, `duckdb_extension.h` — extension API 1.5.6;
  `grep -ci filter` on `duckdb.h` returns 0. The v1 surface is unchanged; nothing in this
  codebase is affected today.
- GitHub branches API — `main`, `v1.1-eatoni`, `v1.2-histrionicus`, `v1.3-ossivalis`,
  `v1.4-andium`, `v1.5-variegata`, `v2.0-cyanoptera`. Latest release v1.5.5 (2026-07-22);
  no 2.0 tag.
- [duckdb/duckdb#25340](https://github.com/duckdb/duckdb/pull/25340) — "C-API-V2: Part 3",
  Maxxen, merged 2026-09-04; the PR that added this surface. Body: "expression claim-based
  filter pushdown […] we will continue to refine this API to make it more powerful";
  "nothing is set in stone". Parts 1 and 2 are #24702 and #25184.
- [duckdb/duckdb#19818](https://github.com/duckdb/duckdb/issues/19818) — rustyconover
  (Airport), 2025-11-17. samansmink: setting `filter_pushdown` means handling all pushed
  table filters; "for a more finegrained implementation you should use
  `pushdown_complex_filter`".
- [duckdb/duckdb#25163](https://github.com/duckdb/duckdb/issues/25163) — open, 2026-08-30,
  zero comments. Route A requested by an Athena extension author on the same remote-scan
  grounds, with measured bytes-scanned figures.
- [duckdb/duckdb#8163](https://github.com/duckdb/duckdb/pull/8163) — 2023-07-05, Kayrnt;
  closed unmerged 2023-11-08 by Mytherin asking for a proposal first.
- [duckdb/duckdb#14591](https://github.com/duckdb/duckdb/pull/14591) — 2024-10-28, thirteen
  C API symbols implementing Route A, with tests; Maxxen's "at first glance looks good!"
  and the every-filter-type caveat; `needs maintainer approval`, then `stale`, closed
  unmerged 2026-04-18 with the author's request for API feedback never answered.
- [duckdb/duckdb#19093](https://github.com/duckdb/duckdb/pull/19093) — 2025-09-22, halgari;
  closed unmerged. taniabogatsch, 2025-10-01, on the undecided question and the preference
  for filters as expressions — the design v2 went on to implement.
- [duckdb/duckdb#25391](https://github.com/duckdb/duckdb/issues/25391) — open, filed
  2026-09-05, one day after #25340 merged: v2 exposes `> a AND < b` and `>= a AND <= b` as
  the same `COMPARE_BETWEEN` node, so strict and inclusive bounds are indistinguishable.
- [duckdb/duckdb#20613](https://github.com/duckdb/duckdb/issues/20613) — re-checked
  2026-09-05: open, *under review*, 1 comment, last updated 2026-01-22. Not the thread the
  v2 work came through.
- [A Preview of DuckDB v2.0](https://duckdb.org/2026/08/17/duckdb-20-highlights),
  2026-08-17 — versioned YAML spec, per-symbol lifecycle tags, GA targeted autumn 2026.
- `spikes/PushdownSpike` — the threshold cliff measurements, still the evidence Route C
  would need.
- ADR 0001, ADR 0006, ADR 0007 — the positions this ADR confirms rather than narrows.
