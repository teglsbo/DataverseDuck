# ADR 0002 — Store every timestamp as a naive UTC `TIMESTAMP`

- **Status:** Accepted
- **Date:** 2026-08-16

## Context

Three systems meet in one query, each with different timestamp defaults:

- **Dataverse** stores UTC, but attributes carry a `DateTimeBehavior` that changes the
  meaning: `UserLocal` is a real instant, while `DateOnly` and `TimeZoneIndependent`
  are wall-clock values.
- **DuckDB** has naive `TIMESTAMP` and timezone-aware `TIMESTAMPTZ`. The latter renders
  according to a session `TimeZone` setting that defaults to the **host** timezone.
- **JSON logs** carry whatever the emitting service produced: `Z`, a numeric offset, a
  naive string, or epoch milliseconds.

Every mismatch here fails silently. Nothing throws; results are just wrong by a few hours.

## Measurements

All from `spikes/TimezoneSpike`, run on DuckDB 1.5.5 via DuckDB.NET 1.5.5.

1. **The Appender ignores `DateTimeKind`.** `Utc`, `Local` and `Unspecified` all stored
   identical wall-clock values. No conversion occurs, so a `Local` value is stored as
   though it were UTC.
2. **Round-trip loses `Kind`.** Ticks are preserved exactly; `Utc` returns as `Unspecified`.
3. **`read_json_auto` does the right thing natively.** It infers `TIMESTAMP` and applies
   the offset: `"2026-08-16T12:00:00+02:00"` → `2026-08-16 10:00:00`.
4. **But casting the string yourself silently drops the offset:**

   ```sql
   TRY_CAST('2026-08-16T12:00:00+02:00' AS TIMESTAMP)   -- 12:00:00  WRONG
   TRY_CAST('2026-08-16T12:00:00+02:00' AS TIMESTAMPTZ) -- 10:00:00+00  correct
   ```

5. **`AT TIME ZONE` is direction-dependent on its input type:**
   - `TIMESTAMP AT TIME ZONE 'UTC'` → `TIMESTAMPTZ` (interprets naive *as* UTC)
   - `TIMESTAMPTZ AT TIME ZONE 'UTC'` → `TIMESTAMP` (converts *into* UTC)

   Applied to the wrong type it shifts the value with no error.

6. **Comparing naive to timezone-aware is session-dependent** — the core hazard:

   | session `TimeZone` | `TIMESTAMP '10:00' = TIMESTAMPTZ '12:00+02'` |
   |---|---|
   | `UTC` | `True` |
   | `Europe/Berlin` | `False` |
   | `America/New_York` | `False` |

7. **Mixed-shape JSON destroys inference.** A field holding both ISO strings and epoch
   millis degrades the whole column to `JSON`, and `TRY_CAST(1755345600000 AS TIMESTAMP)`
   returns `NULL` — rows vanish silently.
8. **`epoch_ms()` already returns a naive UTC `TIMESTAMP`**, stable across sessions.
9. **Inference is not stable enough to build on.** A field mixing `"...Z"` with
   `"...+02:00"` may infer as either `TIMESTAMP` (offsets applied, measurement 3) or
   `VARCHAR` (offsets raw, and then measurement 4 applies) — and **a single trailing
   newline on the file flips which**, on the same engine and the same two rows:

   | file | inferred | value of row 2 |
   |---|---|---|
   | no trailing newline | `TIMESTAMP` | `2026-08-16 10:00:00` (correct) |
   | one trailing newline | `VARCHAR` | `2026-08-16T12:00:00+02:00` (raw) |

   The mechanism was not determined. The consequence is that a query relying on the
   inferred type can be correct for one log file and silently wrong for the next.

   This is why the mitigation is the runtime detector of ADR 0005 rather than a rule
   about how to write queries: the type cannot be predicted from the file, so it is
   *observed* after loading and reported, with the `TIMESTAMPTZ` cast offered as the
   fix. Measurement 3 is therefore true but must not be relied on.

   Where a query is written by hand against a log file, pinning the column with
   `read_json_auto(path, columns = {ts: 'VARCHAR'})` and casting through `TIMESTAMPTZ`
   is correct under every session timezone and both file variants (verified).

   **Addendum (2026-08-18) — the mechanism, found.** Reproduced directly against
   DuckDB 1.5.5's own source (`extension/json/json_reader.cpp`,
   `json_functions/json_structure.cpp`). `read_json_auto` samples the file in
   `STANDARD_VECTOR_SIZE`-row (2048) buffer batches and narrows a per-column
   "candidate type" list (`RefineCandidateTypesString` /
   `JSONStructureNode::EliminateCandidateTypes`) as more sampled values are seen;
   the final type is whatever's left in the list once sampling stops — if every
   candidate is eliminated, VARCHAR is what remains. It is a moving boundary
   condition in that elimination, not something that depends on any single row's
   content:

   | rows in the file | no trailing newline | one trailing newline |
   |---|---|---|
   | 1 | `TIMESTAMP` | `TIMESTAMP` |
   | 2 (one `Z`, one `+02:00`) | `TIMESTAMP` | `VARCHAR` |
   | 3+ | `VARCHAR` | `VARCHAR` |

   So the trailing newline only matters at the exact n=2 boundary this project's
   spike happened to test; add a third row and both variants already agree on
   `VARCHAR` regardless of newline. The newline changes where DuckDB's internal
   read buffer is split relative to the two JSON objects during that one sampling
   pass, which is enough to change the order candidate types are eliminated in —
   but the destination (VARCHAR, once mixed offset shapes are present at all) is
   the same either way for any realistic file. This is a DuckDB engine sniffing
   quirk, not a bug in this project's code, and there is nothing here to fix:
   it only reinforces that inference must not be trusted, which is exactly what
   ADR 0005's runtime detector (`JsonTimestampInspector`) already assumes and
   guards — it flags any textual column that still looks like a timestamp after
   `read_json_auto`, regardless of *why* inference failed.

## Decision

**Every timestamp in the cache is a naive DuckDB `TIMESTAMP` whose value is UTC.
`TIMESTAMPTZ` never enters the cache.** Enforced by `UtcTimestampPolicy`:

1. `SET TimeZone = 'UTC'` on every connection open — `UtcTimestampPolicy.OpenConnection`.
   This makes any accidental naive/aware comparison deterministic.
2. `ToUtcInstant` normalises `DateTimeKind` before the value reaches the Appender, with a
   `strict` mode that rejects `Unspecified` rather than guessing.
3. `JsonTimestampToUtc` routes JSON strings through `TIMESTAMPTZ` and back, so offsets are
   applied rather than discarded.
4. `JsonEpochMillisToUtc` uses `epoch_ms` and deliberately applies **no** `AT TIME ZONE`.
5. `MapDateTimeAttribute` honours `DateTimeBehavior`: `DateOnly` → `DATE`,
   `TimeZoneIndependent` → `TIMESTAMP` with no conversion, `UserLocal` → `TIMESTAMP` in UTC.
6. `AssertNoTimestampTz` fails loudly if a timezone-aware column reaches a cache table.

## Consequences

- Results are reproducible across machines and CI regardless of host timezone.
- Callers must use the policy helpers rather than hand-written casts. The guard rail
  catches the structural mistake; it cannot catch every ad-hoc expression.
- Presenting timestamps in a local timezone is a rendering concern, applied at the edge.
- Each rule is pinned by a test, so a DuckDB upgrade that changes semantics fails loudly.

## Note

While implementing rule 4, `JsonEpochMillisToUtc` was written applying `AT TIME ZONE 'UTC'`
to the output of `epoch_ms()` — which is already naive UTC — thereby converting it *to* a
`TIMESTAMPTZ`. That is exactly the direction error described in measurement 5, made by the
author of the rule, and it was caught only because a test asserted the rendered value.
This is the argument for the guard rail and the tests, not just the documentation.
