# 9. Record what each cached table is, in the cache itself

Date: 2026-08-16

## Status

Accepted.

## Context

A `.duckdb` cache is meant to be reused. You fetch once, then query offline,
refetch selectively, and keep the file between sessions. That is the whole
reason `--db` exists.

But a cache file is just tables. Nothing in it says:

- when `crm_contact` was fetched, so nothing says whether it is stale;
- what statement produced it, so nothing says which columns or filters applied;
- and — the one that gives wrong answers — that it holds **only the contacts
  some JSON file referred to**.

That last point is specific to this project. Key-set pushdown (ADR 0006) is the
mechanism that makes large tables usable: `{{ }}` fetches the rows a local query
asked for and no others. The result is a table called `crm_contact` that has the
right name, the right columns, and a *subset* of the rows.

A partial copy is indistinguishable from a complete one by inspection. Nothing
about `SELECT count(*) FROM crm_contact` warns you. So the failure mode is not
an error — it is a plausible number that is quietly wrong, computed from a table
that was never meant to answer that question.

## Decision

Write a `dvduck_manifest` table into the cache recording, per entry: the name,
whether it came from Dataverse or JSON, the source statement or path **as
written**, the row count, the distinct key count if `{{ }}` supplied one, the
load time in naive UTC, and the elapsed time.

Three things make it worth having rather than decorative:

**It is written inside the load's own transaction.** A manifest written after a
commit would survive a load that rolled back and describe rows that are gone —
which reads as a *successful* load and is worse than having no manifest at all.
This forced a small refactor: `CacheOne` previously delegated its transaction to
`DuckDbBulkLoader.Load`, so it had no transaction to join. It now opens its own
and composes `CreateTable` + `LoadInto`, which is what the `{{ }}` path already
did. Same atomicity guarantee, one level up.

**The key count is the partial marker.** `KeyCount is not null` means the table
is a subset, and `dvduck tables` says so in words rather than leaving the reader
to infer it from a number.

**JSON views are recorded too.** A DuckDB view persists in the file, so a reused
cache carries a `logs` view pointing at a path that may no longer exist, or may
now hold different data. The path is the only thing that explains it.

`dvduck tables --db <path>` reads it back. A manifest with no reader is a
write-only feature.

## Consequences

The manifest is descriptive, not enforced. It records that `crm_contact` is
partial; it does not stop you querying it as though it were whole. Enforcement
would mean intercepting queries, which is DuckDB's job and not one it exposes.
Making the fact *visible* is the honest limit of what this can do.

Caches written before this change have no manifest. `Read` returns empty and
`dvduck tables` says so plainly rather than implying the file is empty.

The stored source is the statement as written, `{{ }}` and all — not the
expanded SQL with key literals inlined. The expansion is per batch and can be
hundreds of kilobytes of GUIDs; the original is what a human wrote and what they
would edit to refetch.

No primary key on the table: DuckDB rejects deleting and reinserting the same
key within one transaction, which is exactly what re-caching a table does. An
explicit `DELETE` before `INSERT` expresses the same intent and works where the
load actually happens.
