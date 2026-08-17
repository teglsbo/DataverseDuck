# 8. Let a query name its own sources

Date: 2026-08-16

## Status

Accepted.

## Context

ADR 0006 established that we choose which Dataverse rows to fetch, using a
`{{ }}` marker whose embedded DuckDB query supplies the keys. ADR 0007 recorded
why that stays hand-rolled rather than becoming automatic pushdown.

Neither addressed how a user *writes* the resulting work. The first form was one
flag per action:

```
--json  logs=webchat/*.json
--cache crm_account="... {{SELECT ... FROM logs}}"
--cache crm_contact="... {{SELECT accountid FROM crm_account}}"
--run   "SELECT ..."
```

That works, and it is honest about each `--cache` being a network round trip.
But the dependency order — JSON before the caches, `crm_account` before
`crm_contact` — lives entirely in the order the flags happen to appear, where
nothing states it and nothing checks it.

This is not hypothetical. While writing the help text for `{{ }}` we found that
the CLI registered JSON views *after* running the caches, which meant a `{{ }}`
could never have read a JSON file — the feature's main use. The bug was invisible
because the ordering was implicit.

## Options considered

### Keep the flags and document the order

Free, and preserves the property that each flag visibly costs a round trip. But
it leaves the same class of bug available to users: a `{{ }}` naming something
registered later fails as a bare "table not found", pointing at DuckDB rather
than at the ordering mistake.

### A real DuckDB CTE, with DuckDB driving the fetch

The ideal:

```sql
WITH ids AS (SELECT customer_id FROM logs)
SELECT * FROM dataverse_contact WHERE contactid IN (SELECT * FROM ids)
```

Not possible. DuckDB would have to hand our scan either a table argument or the
filter. The C API exposes neither: `grep in_out` over `duckdb.h` returns nothing,
so table-in-out functions are unavailable, and filter access is the wall of
ADR 0007. A table function *can* read literal scalar arguments at bind time, so
`dataverse('SELECT ...')` as a real table function would work — but the keys from
a preceding CTE could not reach it, which is the entire point.

### A real table function, fetching per scan

`WITH x AS MATERIALIZED (SELECT * FROM dataverse('SELECT ...'))` would be a
genuine CTE. Rejected: it loses the persistent cache in the `.duckdb` file that
makes a re-run free, it risks refetching if referenced twice without
`MATERIALIZED`, and the transactional all-or-nothing load, throttle
classification and cancellation all currently wrap `Cache()` rather than a scan
callback.

### Sugar: a `WITH` block we parse ourselves

```sql
WITH logs        AS JSON ('webchat/*.json'),
     crm_account AS DATAVERSE (... {{SELECT ... FROM logs}}),
     crm_contact AS DATAVERSE (... {{SELECT accountid FROM crm_account}})
SELECT ...
```

DuckDB never sees the `JSON` or `DATAVERSE` entries; they desugar to exactly the
steps the flags ran.

## Decision

Adopt the `WITH` form as the primary way to write a query, via `--plan` and
`--plan-file`. Keep the flags working.

`JSON` is an entry kind rather than a separate flag, so the ordering that was
previously a documented rule — *JSON before caches* — is now something the user
writes down and the parser checks.

## Consequences

- The order is stated where it applies, and read top to bottom. Reviewing a query
  no longer means reconstructing dependencies from flag order.
- Because the whole chain is parsed at once, mistakes can be named precisely
  instead of failing later as "table not found":
  - reading an entry declared below — *"'crm_contact' reads 'logs' inside
    `{{ }}`, but 'logs' comes later"*
  - reading an ordinary CTE, which only exists once the final query runs
  - reading itself; defining a name twice
  These checks are impossible in the flag form, which sees one statement at a
  time.
- The cost is a small dialect. `DATAVERSE` and `JSON` are keywords DuckDB has
  never heard of, inside something that looks like SQL. A reader may reasonably
  expect CTE semantics — laziness, inlining, cheapness — where each entry is in
  fact an eager network transfer that materialises a table. The help text and
  README say so explicitly, and `MATERIALIZED` on such an entry is refused rather
  than silently accepted, because it is always materialised.
- Ordinary CTEs are passed through untouched with their `MATERIALIZED` hints, so
  the escape hatch to plain DuckDB stays open.
- The parser is a scanner, not a SQL parser. It balances parentheses while
  skipping string literals, doubled-quote escapes, quoted identifiers and both
  comment forms, and understands nothing else. That is enough to find entry
  boundaries and is unlikely to need to grow; if it ever does, that is the signal
  to stop and use a real parser.
- Two forms now exist for the same thing, which is a maintenance cost. Both share
  one execution path — the flags build the same ordered `PlanStep` list — so the
  duplication is in argument handling only.

## Evidence

- `src/DataverseDuck/DataversePlanParser.cs` — the scanner and the ordering
  checks.
- `tests/DataverseDuck.Tests/DataversePlanParserTests.cs` — 24 tests, including
  the cases that break a naive scanner (`'Bob (Robert'`, `'O''Brien (x)'`,
  `-- a stray ) in a comment`, `IN (0, (1))`), and `PlanEndToEndTests`, which
  runs a two-hop plan against a fake source and asserts 2 accounts then 4
  contacts.
- `grep -c "in_out\|table_in_out" duckdb.h` → 0; the C API has no table-in-out
  functions, so a CTE cannot feed a table function.
- ADR 0006 for `{{ }}`, ADR 0007 for why pushdown is not automatic.

## Addendum (2026-08-17): `--plan` renamed to `--query`

The flag was named after the internal type it builds (`PlanStep`/`PlanAnalysis`), which
reads like a dry run — "show me the plan" — to anyone who has not read the source. It is
not one: `--plan "SELECT ..."` fetches from Dataverse and executes, exactly as `--query`
does now.

`--query` and `--query-file` are the names going forward. `--plan` and `--plan-file` are
still accepted — each prints a one-line deprecation notice to stderr and then behaves
identically — so existing scripts keep working. Nothing else changes: this is a rename,
not a new option.
