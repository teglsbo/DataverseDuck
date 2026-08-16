# 6. Let DuckDB choose the rows, but do the fetching ourselves

Date: 2026-08-16

## Status

Accepted.

## Context

The natural architecture is to register Dataverse as a table inside DuckDB and
let the optimiser push filters down, exactly as `postgres_scanner` and
`mysql_scanner` do. Then this would just work:

```sql
SELECT count(DISTINCT c.contactid)
FROM logs l JOIN dataverse.contact c ON c.contactid = l.customer_id
WHERE l.channel = 'webchat'
```

No explicit caching step, no manual query splitting. It is the right instinct
and it is how the mature DuckDB connectors behave.

It is not available to us. To be precise about why, because the obvious reading
("DuckDB cannot push down predicates") is wrong:

| Layer | Row predicate pushdown? |
|---|---|
| DuckDB engine (C++) | **Yes.** `TableFilterSet filters`, `pushdown_complex_filter`, `pushdown_expression`, `supports_pushdown` — `table_function.hpp` |
| DuckDB C API (`duckdb.h`) | **No.** The only exported pushdown symbol is `duckdb_table_function_supports_projection_pushdown` |
| DuckDB.NET (binds the C API) | **No**, inherits the gap |

DuckDB itself pushes predicates down extensively — that is how `parquet_scan`
and `postgres_scanner` avoid reading what they do not need. The limitation is
the **C extension API**, which every .NET table function must go through.
Grepping `duckdb.h` for exported symbols matching `filter|pushdown` returns
exactly one, and it is about projections. DuckDB's own `table_function-c.cpp`
receives the filter set into its internal init struct
(`optional_ptr<TableFilterSet> filters`) and then exposes no accessor for it.

So the capability exists one layer below us and is not reachable from managed
code. What DuckDB.NET 1.5.5 does offer was measured rather than assumed:

| Query against a C# table function | What DuckDB requested |
|---|---|
| `SELECT count(*)` | column `contactid` |
| `SELECT count(fullname)` | column `fullname` |
| `SELECT count(fullname) … WHERE statecode = 0` | columns `statecode`, `fullname` |
| `SELECT contactid, statecode` | columns `contactid`, `statecode` |

So DuckDB **does** perform projection pushdown, and it is precise: it includes
the columns a predicate needs, not merely the ones in the select list.

But that is the whole of it. DuckDB reports *which columns* the predicate
touches, never *the value it compares against*. `WHERE statecode = 0` arrives as
"statecode is required" and nothing more. Searching the assembly for any filter,
predicate or constraint type returns nothing.

The consequence is decisive. A managed table function is asked for every row, no
matter the `WHERE`, the join, or the projection — confirmed by counting rows
pulled from the source. Registering a four-million-row Dataverse table as a
DuckDB table and joining it to three JSON keys would fetch four million rows.
That is far worse than the explicit fetch it would replace.

Two further findings closed off the alternatives:

- The projection information does exist, but only through
  `TableFunction`'s `dataFactory` constructor, which is `internal`. It was
  reachable by reflection for the measurement above; that is not shippable.
- SQL 4 CDS temp tables are held **client-side** (GitHub issue #712,
  `IndexSpoolNode.cs`), so `WHERE EXISTS (SELECT … FROM #keys)` performs a
  local nested-loop join — the opposite of pushdown.

## Decision

DuckDB decides *what* to fetch; we do the fetching.

A Dataverse statement may embed one DuckDB query in `{{ }}`:

```sql
SELECT contactid, fullname FROM contact
WHERE contactid IN {{SELECT DISTINCT customer_id FROM logs WHERE channel = 'webchat'}}
```

The inner query runs locally against JSON and cached tables. Its distinct
non-null results are inlined as literals, in batches, and each batch is sent to
Dataverse as an ordinary `IN` filter.

Substitution happens exactly where the marker was written rather than by
wrapping the statement in a derived table. This is deliberate: verified in
`BaseDataNode.cs`, SQL 4 CDS folds `IN` into a FetchXML `<condition
operator="in">` only when every value is a literal, and refuses to fold
`IN (SELECT …)` at all. Keeping the marker in the user's own `WHERE` clause
guarantees the shape that folds. A wrapper risks the optimiser evaluating the
filter client-side and scanning the whole table — the exact failure this exists
to prevent.

Batches are 500 keys. This is a safety valve, not a documented limit: Microsoft
documents 500 `<condition>` elements per `<filter>`, but an `IN` list is a
single condition however many values it holds, and no per-value cap is
published. The widely repeated "500 values" advice is that rule misread. SQL 4
CDS does no batching of its own, so unbatched it would send all 50,000 values in
one request.

All batches commit as one transaction, so a failure part-way leaves no
half-populated table (ADR 0005's sibling concern).

## Consequences

- The motivating query fetches 2 contacts out of 5,000 rather than all 5,000.
- The `{{ }}` marker is visible syntax. The user must know their access pattern.
  That is worse ergonomics than a real optimiser and better than silently
  transferring a large table.
- One key query per statement. Two independent key sets would multiply round
  trips rather than narrowing them, so it is rejected outright.
- An empty key set queries Dataverse for schema only and creates an empty table,
  because a missing table turns the user's next join into an error rather than
  an empty result.
- Keys must be literals for folding, so rendering them is an injection boundary.
  Values are escaped or validated by type, and unrecognised types are refused
  rather than formatted.
- The unlock is not a DuckDB.NET change but a **C API** one: DuckDB would have to
  export an accessor for the `TableFilterSet` it already plumbs into
  `CTableInternalInitInfo`. If that lands, simple predicates could fold
  automatically and this becomes redundant for them. It would still be needed
  for the semi-join case, which requires join keys to reach the scan, not just
  constant comparisons.
- The other route is a genuine C++ DuckDB extension, which gets the full
  `pushdown_complex_filter` surface. That means doing MSAL authentication and
  Dataverse SDK calls from native code, which is a different project.
