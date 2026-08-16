# 10. Read datetime behaviour from metadata, not from the value

Date: 2026-08-16

## Status

Accepted.

## Context

ADR 0002 established that every timestamp in the cache is a naive `TIMESTAMP`
holding UTC. That rule is right for almost every Dataverse datetime, and wrong
for two kinds of them.

A Dataverse datetime attribute has a `DateTimeBehavior`:

| Behaviour | Meaning | Is it an instant? |
|---|---|---|
| `UserLocal` | A moment in time, stored UTC | Yes |
| `DateOnly` | A calendar date | No |
| `TimeZoneIndependent` | A wall-clock reading | No |

A birthdate of 1980-05-15 is that date everywhere. Shifting it by an offset
makes it the 14th for anyone west of UTC. The same applies to an appointment
time that means 09:00 wherever it is read.

Three things were measured against a live environment before deciding anything.

**The reader cannot tell them apart.** A `DateOnly` birthdate, a `UserLocal`
timestamp and a `DateAndTime` value all arrive as `DateTime` with
`Kind=Unspecified`. `GetFieldType` says `System.DateTime` for all of them. The
distinction exists only in metadata.

**`Format` is not `DateTimeBehavior`.** The obvious shortcut is to read
`Format`, which is right in front of you on the same attribute. It is wrong:
stock `account.lastusedincampaign` and `contact.overriddencreatedon` are
`Format=DateOnly` with `Behavior=UserLocal` — real instants that merely display
as dates. Keying off `Format` would strip the time from a genuine timestamp.

**The user's timezone does not affect what the engine returns.** Changing the
application user's `timezonecode` from 92 (UTC) to 105 (Copenhagen) and
re-reading the same row returned the same `23:30`. ADR 0002 assumed this; it is
now measured. Had it been false, every cached timestamp would have depended on
a setting nothing in this project controls.

## Decision

Resolve each datetime column's originating attribute and map by behaviour:

- `DateOnly` becomes DuckDB `DATE`
- `TimeZoneIndependent` becomes `TIMESTAMP`, stored verbatim
- `UserLocal`, and anything unresolved, becomes `TIMESTAMP` normalised to UTC

The origin comes from the reader's schema table, which reports `BaseTableName`
and `BaseColumnName` per column. This survives joins and aliases: a column
selected as `a.createdon AS acct_created` still reports `account`/`createdon`.

Wall-clock columns are marked `ColumnKind.WallClock` so no conversion is applied
at append time, in either direction.

When metadata is unavailable the column is treated as an instant. That is the
pre-existing behaviour and is correct for the large majority of columns.

## Consequences

A `DATE` column cannot be compared directly against a `TIMESTAMP` without a
cast. This is the point: the two were never comparable, and the previous
representation only made it look as though they were.

`dvduck` prints `DateOnly` and `TimeOnly` in ISO form. DuckDB returns those CLR
types for `DATE` and `TIME`, and without an explicit format they picked up the
current culture — a birthdate printed as `05/15/1980`, unsortable and ambiguous
with `15/05`.

The mapper now needs an `IAttributeMetadataCache`. It is optional, so the
library still works without one.

**A live metadata cache loads lazily, and its `TryGetValue` reports false for an
entity it has not fetched yet** rather than for one that does not exist. Using
it meant metadata was silently skipped on every first call, so `birthdate`
cached as `TIMESTAMP` against a real tenant while every offline test passed. The
indexer, which fetches on demand, is used instead. This is covered by a
regression test that fails against the `TryGetValue` version.

This rule was written and tested in `UtcTimestampPolicy.MapDateTimeAttribute`
when ADR 0002 was accepted, and then never called by anything. Being tested is
not the same as being reachable.
