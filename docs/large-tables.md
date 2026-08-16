# Large tables

Measured against a live Developer environment on 2026-08-16, using
`solutioncomponent` (56,161 rows), which is stock data present in any
environment. Numbers from one tenant on one day: treat the shapes as real and
the absolute values as indicative.

## What Microsoft documents

| Limit | Value | Notes |
|---|---|---|
| Requests per user | 6,000 per 5-minute sliding window | 429 with `Retry-After`, code `-2147015902` |
| Execution time per user | 20 minutes per 5-minute window | Code `-2147015903`. Usually the one that bites a bulk read |
| Concurrent requests | 52 or higher | Code `-2147015898` |
| Page size | 5,000 standard, 500 elastic | Default and maximum |
| Simple paging (no paging cookie) | 50,000 rows total | Some queries cannot use a cookie, e.g. sorting on a linked attribute |
| FetchXML conditions | 500 condition and link-entity elements | An `IN` list folds to **one** condition regardless of length |
| Aggregate queries | 50,000 records | Error `AggregateQueryRecordLimit` |

Sources: [API limits](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/api-limits),
[Page results](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/fetchxml/page-results),
[Filter rows](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/fetchxml/filter-rows),
[Aggregate data](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/fetchxml/aggregate-data).

Two things worth knowing about those limits:

**The same limits apply to service principals.** Microsoft's FAQ answers "are
limits applied differently for application users?" with "No." There is no
headroom to be had by using an app registration.

**Microsoft publishes no throughput figures.** Their guidance is explicitly to
measure: "don't try to calculate how many requests to send at a time... gradually
increase the rate you send requests until you begin to hit limits". Any
rows/second number you find online, including the ones below, is someone's
measurement of their own environment.

## What Microsoft recommends, and why we do not do it

For large reads Microsoft points at Azure Synapse Link or Fabric Link. Both
require infrastructure we do not have — a storage account, a Synapse or Fabric
workspace, and the tenant rights to provision them. The TDS (SQL) endpoint is
the other alternative and is ruled out separately: it does not support service
principal authentication (ADR 0001), and it has a fixed 5-minute timeout.

So everything here goes through `RetrieveMultiple` with FetchXML, and the
question is where that runs out.

## Measured: reading a whole table

| Query | Rows | Time | Rate |
|---|---|---|---|
| Full scan, 3 narrow columns | 56,161 | 7.1s | ~7,900 rows/s |
| Same again, warm | 56,161 | 6.7s | ~8,400 rows/s |
| `TOP 5000` (one page) | 5,000 | 0.5s | ~9,800 rows/s |
| Server-side filter, 1% selectivity | 582 | 0.3s | — |

Roughly **8,000 rows/second** for narrow columns, and paging is not the
bottleneck: a single 5,000-row page runs at about the same rate as a sustained
scan. Extrapolating at that rate, 1M rows is about two minutes and 10M rows is
about twenty — at which point the 20-minute execution budget per 5-minute window
is the wall, not patience.

Column width matters more than row count. These were three narrow columns;
`SELECT *` on `contact` is 311 attributes.

## Measured: how far key-set pushdown scales

Fetching specific rows by primary key, varying the number of keys in the `{{ }}`
list:

| Keys | Time | | Keys | Time |
|---:|---:|---|---:|---:|
| 1 | 0.2s | | 5,000 | 2.5s |
| 10 | 0.1s | | 10,000 | 7.3s |
| 100 | 0.1s | | 20,000 | 24.4s |
| 500 | 0.1s | | 40,000 | 66.5s |
| 1,000 | 0.2s | | 56,000 | 116.6s |
| 2,000 | 0.6s | | | |

Three findings.

**The 500-condition limit does not apply to an `IN` list.** SQL 4 CDS folds the
whole list into a single `<condition operator="in">`, so 56,000 keys is one
condition. Nothing failed at any size tested; 56,000 keys produced 2.1 MB of
literals and still ran. The transport is SOAP, so there is no URL length limit
either.

**Cost is superlinear.** Up to about 1,000 keys the filter is free — 1,000 keys
cost the same as 1. Beyond that, doubling the keys roughly triples the time.

**Chunking beats one large list.** 10,000 keys as a single `IN` takes 7.3s;
five requests of 2,000 keys take about 3s in total. The superlinear cost is
per-request, so splitting a large key set is strictly faster, and it also keeps
each request well inside the execution-time budget.

These numbers come from issuing the `IN` list directly. **`{{ }}` already chunks
for you** at `KeySetPushdown.BatchSize`, currently 500 keys per round trip, so a
single enormous request is not something a plan can produce by accident:

```console
  a: 533 distinct key(s) from the local query
  a: 19,657 rows after 500 of 533 keys
  a: 20,952 rows after 33 of 533 keys
```

500 sits inside the flat region below 1,000 keys where the filter is free, so it
is at least as fast as the 2,000 measured above while keeping any single failed
request cheap to retry.

## Where the crossover is

A full scan of this table costs 7s. Pushing 10,000 keys also costs 7s. So on
this table, pushdown stops paying at roughly **one key per five rows of table**.

As a rule of thumb, with a table of *N* rows and *K* keys:

- **K < N/5** — push the keys. This is the normal case and what `{{ }}` exists for.
- **K > N/5** — read the whole table and join locally. You are paying more to
  describe the subset than to fetch everything.
- **K > 10,000** — reconsider rather than tune: `{{ }}` chunks the requests
  already, so the cost here is the number of round trips, and a key set this
  large usually means the local query is not selective enough.

Note this ratio is about *cost*, not correctness, and it moves with column
width. A table with 300 wide columns scans far more slowly than
`solutioncomponent` does, which pushes the crossover higher and favours pushdown
more strongly.

## Observation not explained by the documentation

`SELECT COUNT(*) FROM solutioncomponent` returned 56,161 without error, despite
the documented 50,000-record aggregate limit. SQL 4 CDS appears to partition
aggregate queries to work around that limit. This was not investigated further;
do not rely on it without measuring, and treat 50,000 as the documented
behaviour.

## What this means for this project

Nothing here changes the design. Key-set pushdown (ADR 0006) is the right
mechanism, `{{ }}` remains explicit rather than automatic (ADR 0007), and the
folding guard (ADR 0004) already catches the case where a filter silently fails
to reach the server.

The practical ceiling for this tool is a few million rows per table for a full
cache, and effectively unlimited when the local data selects a small subset —
which is the case it was built for.
