# 5. Detect timestamp columns that JSON inference left as text

Date: 2026-08-16

## Status

Accepted. Extends [ADR 0002](0002-utc-naive-timestamps.md).

## Context

ADR 0002 established that `read_json_auto` handles ISO 8601 offsets correctly,
producing naive UTC `TIMESTAMP` values, and that this is why we prefer it to
casting the strings ourselves.

That is true only when a column's values share one offset notation. Measured:

| File contents | Inferred type |
|---|---|
| `+02:00` in every record | `TIMESTAMP` (offset applied) |
| `Z` in every record | `TIMESTAMP` (offset applied) |
| `Z` in one record, `+02:00` in another | **`VARCHAR`** |

Nothing warns. The column simply stays text, and every subsequent `MIN`,
`MAX`, `ORDER BY`, `<` and `BETWEEN` silently becomes a string comparison.

This is not a loss of precision, it is a wrong answer:

```
id     ts                          correct UTC
late   2026-08-16T23:00:00+02:00   21:00
early  2026-08-16T22:00:00Z        22:00
```

`ORDER BY ts` returns `early` first, though it is an hour *later*. The query
succeeds and the result looks reasonable.

Mixed notation is not a corner case. It is what happens whenever a log file is
assembled from more than one service, library or language runtime — which is
the normal case for the log-plus-state joins this project exists to serve.

Two plausible repairs both fail:

- `TRY_CAST(ts AS TIMESTAMP)` — **discards the offset** (ADR 0002, rule 4).
  Turns 23:00+02:00 into 23:00 UTC. Measured, silent.
- Rewriting the JSON before loading — pushes the problem onto the user and
  onto whatever wrote the logs, which we do not control.

## Decision

`DataverseCache.RegisterJson` inspects every `VARCHAR` column of the new view
and reports those whose sampled values all parse as timestamps.

The report names the column, quotes a sample value, says whether mixed offsets
are to blame, and carries the corrective expression
(`TRY_CAST(col AS TIMESTAMPTZ) AT TIME ZONE 'UTC'`) so the fix can be pasted or
interpolated directly.

Detection requires *all* sampled non-null values to parse, so a free-text field
that happens to contain one date is not flagged. Sampling is capped at 200 rows
so it can run unconditionally.

We warn rather than rewrite. Silently redefining the user's column would swap
one invisible behaviour for another, and a view whose columns do not match the
file would be its own surprise.

## Consequences

- The failure becomes visible at the moment the file is registered, before a
  wrong answer can be produced and acted on.
- Warnings cost a `DESCRIBE` and one aggregate per text column per file.
- A file where *no* column parses cleanly is silent, as it should be.
- If DuckDB later infers mixed-offset columns as `TIMESTAMPTZ`, this detection
  goes quiet on its own. `JsonTimestampInspectorTests` would then fail, which
  is the signal to revisit — not a silent change of behaviour.
