# BulkInsertSpike

Measures what writing through SQL 4 CDS costs. Nothing else in this repository writes —
ADR 0001 records the workload as read-only, so the engine's DML path had never been
exercised here, and `docs/large-tables.md` measures reads only.

```bash
dotnet run --project spikes/BulkInsertSpike -- --count 8000 --batch-size 1000
dotnet run --project spikes/BulkInsertSpike -- --delete --count 8000
```

Every row gets a deterministic id in a namespace nothing else uses —
`dddddddd-0000-0000-0000-<12-digit index>`, surname `Bulk` — which is what makes `--delete`
exact. It removes the ids this spike would have created and nothing else, so a mistake here
cannot take a real contact with it. `--dry-run` prints the first statement without executing.

Measured against a live Developer environment on 2026-09-08, inserting and deleting
`contact` rows with three columns. One tenant, one day: treat the shapes as real and the
absolute values as indicative.

## The headline

There are two numbers per operation, and quoting the wrong one is the easiest mistake to
make here.

| Operation | Peak (rested) | Sustained (30,000 rows) | Sustained / peak |
|---|---:|---:|---:|
| INSERT | **215 rows/s** | **154 rows/s** | 72% |
| DELETE | **34 rows/s** | **18 rows/s** | 53% |
| *READ, for scale* | *~8,000 rows/s* | — | — |

**Peak** is what a short run achieves against a full service protection reservoir: 8,000
rows in one wave. **Sustained** is the 30,000-row figure, by which point the reservoir is
spent and the rate is whatever replenishment allows.

Writes are **40-50x slower than reads**, so bulk seeding is a different problem from bulk
querying rather than a variation on it. Deletes are 6x slower than inserts at peak and 8.5x
at sustained: the gap widens because deletes are the operation that provokes throttling.

### The sustained delete is a sawtooth, not a rate

Per-statement rates across the 30,000-row delete, 2,500 rows each:

    14, 20, 41, 15, 16, 36, 20, 9, 41, 32, 9, 41 rows/s   [throttled throughout]

The engine emitted **300** notices of the form *"4 threads paused until 05:06 due to service
protection limits"*, dropping from 8 workers to 6 and back. So 18 rows/s is an average over
an oscillation between a throttled floor near **9 rows/s** and unthrottled bursts near
**41 rows/s** -- not a steady rate anything actually runs at. Plan capacity from the average;
do not expect the instantaneous rate to resemble it.

Note what this means for measurement: **once the reservoir is drained, peak is no longer
measurable.** A short run in that state reports neither figure, just whatever credit
happened to be left. Measuring peak again requires ~5 minutes fully idle first, since the
1,200s reservoir refills at 4 s/s.

Deletes cost about 3x an insert per row at small volume, and 8.5x at sustained volume,
because they are what provokes throttling — see the throttling section below before quoting
any delete number.

## The mechanism matters more than the tuning

Everything below about `BatchSize` and `MaxDOP` tunes SQL 4 CDS's write path, which goes
through **`ExecuteMultiple`** -- a batch of individual requests
(`InsertNode.ExecuteMultiple`, by reflection). Dataverse also registers purpose-built bulk
messages, and they are a different order of thing. Measured back to back, 8,000 rows,
`BatchSize` 250, 8 cloned clients:

| Operation | Path | Table | rows/s | Throttled |
|---|---|---|---:|---|
| INSERT | SQL 4 CDS (`ExecuteMultiple`) | `contact` | 127 | no |
| INSERT | native `CreateMultiple` | `contact` | **216** | no |
| INSERT | native `CreateMultiple` | elastic | **661** | no |
| DELETE | SQL 4 CDS (`ExecuteMultiple`) | `contact` | 20 | yes |
| DELETE | SQL 4 CDS (`ExecuteMultiple`) | elastic | 62 | yes |
| DELETE | native `DeleteMultiple` | elastic | **695** | no |
| DELETE | native `DeleteMultiple` | `contact` | *unsupported* | — |

Three things fall out of that.

**`CreateMultiple` is ~1.7x SQL 4 CDS for inserts** on the same table. Some of the gap is
T-SQL parsing, planning and metadata resolution that SQL 4 CDS does and a direct message
call does not, so treat 1.7x as the ceiling on what switching would buy rather than a
like-for-like API comparison.

**`DeleteMultiple` is ~11x `ExecuteMultiple`** on the same elastic table, and it did not
throttle where `ExecuteMultiple` did -- same 32 requests, far less execution time consumed.
But it **only works on elastic tables**: on `contact` every request fails with

    The 'DeleteMultiple' method does not support entities of type 'contact'.

So for a standard table there is no faster synchronous path than what SQL 4 CDS already
does. That is worth stating plainly, because it means the delete numbers below are not a
limitation of this project's choice of engine.

**Deletes are not inherently slower than inserts.** On elastic with the right message they
are the same speed: 695 rows/s deleting against 661 inserting. The 3-6x gap measured
everywhere else in this document is an artefact of `ExecuteMultiple` plus SQL-backed
storage plus cascade checks, not a property of deletion.

### If bulk writes ever matter

In order of leverage, largest first. Note that this ordering is **not** what it looked like
halfway through measuring: the table you write to matters more than the storage engine, and
both matter more than any amount of batch tuning.

| Table | INSERT via `CreateMultiple` | DELETE, best available |
|---|---:|---:|
| custom standard, 33 attributes | **932 rows/s** | 93 peak / 61 sustained (`ExecuteMultiple`) |
| elastic | 661 rows/s | **695 rows/s** (`DeleteMultiple`) |
| `contact`, 311 attributes | 216 rows/s | 34 peak / 18 sustained (`ExecuteMultiple`) |

1. **Write to a simple table.** A custom standard table with 33 attributes inserts at 932
   rows/s and deletes at 93 -- against `contact`'s 216 and 34. That is 4x on inserts and
   2.7x on deletes for the same API and the same tuning, and it beats the elastic table on
   inserts. Most of `contact`'s cost is `contact`: 311 attributes, plugins, duplicate
   detection, 39 cascading relationships.
2. **Use the `*Multiple` messages directly** rather than SQL 4 CDS: 1.7x on `contact`
   inserts, and 11x for deletes on an elastic table. SQL 4 CDS remains the right tool for
   *reading* -- that is what ADR 0001 chose it for, and nothing here contradicts that.
3. **Use an elastic table** only if you need bulk *deletes* at speed. It is the only way to
   reach `DeleteMultiple` (695 rows/s, and it does not throttle), but it is *slower* than a
   simple standard table for inserts, and it costs cascading relationships, transactions and
   query richness.
4. **Then** tune `BatchSize` and `MaxDOP` as below -- worth roughly 2-3x, against the 4-11x
   available above.

### Sustained deletes: simple tables degrade far later

Both runs 30,000 rows, `BatchSize` 125, `MaxDOP` 8, SQL 4 CDS:

| Table | Unthrottled run | First stall | Overall |
|---|---|---|---:|
| custom standard | 89-101 rows/s for ~14,500 rows | one 176s stall at ~17,000 | **61 rows/s** |
| `contact` | throttled from the first statement | immediate | **18 rows/s** |

So a simple table is 3.4x better sustained, and the difference is mostly *when* throttling
starts rather than how hard it bites: the custom table held near its peak for half the run
before the reservoir emptied, then recovered to around 80 rows/s.

## Fast deletes: the practical answer

Deletes are the hard direction, so this is the summary a reader usually wants. Everything
here is measured; the sections below give the runs behind each number.

**1. The table you delete from dominates.** 93 rows/s on a 33-attribute custom table with no
cascading relationships, against 34 on `contact` (311 attributes, 39 cascading). Same API,
same tuning, **2.7x**. If you control the schema, nothing else you do matters this much.

**2. `DeleteMultiple` is 11x `ExecuteMultiple` -- on elastic tables only.** 695 rows/s
against 62 on the *same* elastic table, and it did not throttle where `ExecuteMultiple` did.
On a standard table every request fails:

    The 'DeleteMultiple' method does not support entities of type 'contact'.

So for a standard table there is no faster synchronous path than the one SQL 4 CDS already
takes. That is a platform limit, not a limitation of this project.

**3. Small batches, high parallelism -- the opposite of inserts.** A single 1,000-row batch
took 129s (8 rows/s); ten batches of 100 took 50s (20 rows/s). `BatchSize` 125 with
`MaxDOP` 8 was best. Large `ExecuteMultiple` delete batches appear to serialize server-side
rather than amortise the per-request cost.

**4. Deletes provoke throttling; inserts do not.** 8,000-row inserts never throttled once.
`contact` deletes throttled every time. Deletes draw the 1,200s execution-time reservoir
down at two to three times its 4 s/s replenishment.

**5. Sustained is about half of peak, and the interesting difference is *when* it bites.**
`contact` went 34 -> 18 rows/s and was throttled from the first statement. The simple table
went 93 -> 61 rows/s but held near 93 for 14,500 rows before its first stall. Simple tables
win mostly by degrading later.

**6. Payload is irrelevant to deletes.** 88 vs 93 rows/s with nine extra 250-character
columns. Trimming columns only helps inserts (~23%); for deletes it buys nothing, which
makes sense -- you are removing a row, not writing fields.

**7. Async `BulkDelete` is asynchronous, not fast.** It returns in 3.4s having *queued* a
job, which then drains over several minutes. Correct when completion time does not matter;
wrong whenever the next step needs the rows gone.

**8. Turn affinity off, then stop trusting the headers.** `EnableAffinityCookie = false` is
required for `MaxDOP > 1` to mean anything, because a pinned `ServiceClient` serializes.
But the service protection counters are per user *per web server*, so once requests spread
they describe servers doing none of your work -- during a throttled run they reported the
full 1,200s remaining. Subscribe to `Sql4CdsConnection.InfoMessage` and `Progress` instead;
the engine says it outright:

    Deleting Kontaktpersoner 5,657 - 5,782 of 8,000 (6 threads) (4 threads paused until 21:59 due to service protection limits)

### Recipe

```bash
# Standard table, the best available:
--delete --batch-size 125 --max-dop 8          # ~93 rows/s simple table, ~34 on contact

# Elastic table, if bulk deletes are a requirement:
--native --delete --batch-size 250 --max-dop 8 # ~695 rows/s, no throttling
```

## Two findings about how you address rows

### Client-supplied primary keys cost nothing measurable

Every other number in this document was produced with **client-supplied random GUIDs**, so
if the usual warning about fragmenting the clustered index were true at this scale, they
would all be pessimistic. Tested directly, 8,000 rows into an empty table, wiped between:

| Path | Key | rows/s |
|---|---|---:|
| SQL 4 CDS | client random GUID | 323 |
| SQL 4 CDS | generated by Dataverse | 329 |
| native `CreateMultiple` | client random GUID | 668 |
| native `CreateMultiple` | generated by Dataverse | 732 |

Both gaps are inside run-to-run variance -- native inserts on this table ranged 471-932
rows/s across the day -- so there is no measurable penalty. Deterministic ids are therefore
free, and they are what makes this spike's `--delete` exact and its collisions loud.

Two real caveats, neither about speed:

- **Not every table allows it.** In this environment 802 tables have `isvalidforcreate` set
  on their primary id and **88 do not** -- mostly N:N intersect tables
  (`adx_kbarticle_kbarticle`, `appaction_appactionrule_classicrules`) and system
  configuration tables (`appconfig`, `applicationroles`).
- **Uniqueness becomes yours to guarantee, and a collision fails the whole batch.** Five
  leftover probe rows killed two batches of 250 during a 30,000-row seed:
  `Cannot insert duplicate key`.

And a limit on the test: 8,000 rows into an empty table cannot show a cumulative effect. If
index fragmentation matters it will matter over millions of rows and months, not here.

### A set-based delete costs 20x fewer requests

The wipes between those runs used `DELETE FROM t WHERE new_name LIKE 'Load%'` rather than a
list of ids, and the difference in *requests* is far larger than the difference in speed:

| Delete form | Requests for 8,000 rows | rows/s |
|---|---:|---:|
| `WHERE <id> IN (...)`, batched at 125 | 64 | 93 |
| `WHERE <column> LIKE '...'` | **3** | 72 |

**23% slower, ~20x cheaper against the limits.** Requests and execution time are the
constrained resources, not wall clock, so a set-based predicate is usually the better trade
-- and it is likely why the id-list deletes throttled repeatedly while these never did.

Prefer a predicate the server can evaluate over an enumerated key set whenever the rows can
be described rather than listed. The `{{ }}` key-set mechanism exists for the opposite
case, where only the client knows which rows are wanted.

## Two levers, and they only pay together

`BatchSize` sets how many rows go into one `ExecuteMultiple`; `MaxDegreeOfParallelism` sets
how many of those run at once. Both are SQL 4 CDS settings — see below — and the mistake
worth avoiding is spending one without the other.

| Rows | BatchSize | Batches | MaxDOP | Time | rows/s |
|---:|---:|---:|---:|---:|---:|
| 1,000 | 1000 | 1 | 8 | 15.0s | 66 |
| 1,000 | 125 | 8 | 8 | 10.9s | 92 |
| 8,000 | 1000 | 8 | 8 | **37.2s** | **215** |

The first row is the trap: with one batch there is a single request, so however high `MaxDOP`
is set there is nothing to parallelise. The second row parallelises but pays per-request
overhead (~1.2s) eight times over on batches of 125. The third row has both, and is 2.3x the
best of the other two.

**The rule: batch size ~= rows / MaxDOP, capped at 1,000**, which is Dataverse's
`ExecuteMultiple` ceiling. The spike prints a note when the configuration cannot spend the
parallelism it was given.

## Insert scaling

| Rows | BatchSize | MaxDOP | Time | rows/s |
|---:|---:|---:|---:|---:|
| 1,000 | 100 | 1 | 28.9s | 35 |
| 1,000 | 1000 | 1 | 17.0s | 59 |
| 1,000 | 125 | 4 | 11.5s | 87 |
| 1,000 | 125 | 8 | 10.9s | 92 |
| 8,000 | 1000 | 8 | 37.2s | 215 |

Per-row cost falls from 28.9 ms to 4.7 ms across that range. Raising `MaxDOP` from 4 to 8
moved inserts only 87 -> 92 rows/s, inside run-to-run variance: a few batches of 1,000
already saturate the insert path, so extra concurrency buys little.

## Delete scaling

| Rows | BatchSize | MaxDOP | Time | rows/s |
|---:|---:|---:|---:|---:|
| 1,000 | 1000 | 1 | 129.2s | 8 |
| 1,000 | 100 | 1 | 50.0s | 20 |
| 1,000 | 250 | 4 | 49.7s | 20 |
| 1,000 | 125 | 4 | 36.6s | 27 |
| 1,000 | 125 | 8 | 29.6-31.6s | 32-34 |

Deletes behave differently in both directions. A single 1,000-row batch is *three times
worse* than ten of 100 — where inserts amortise, deletes appear to serialize inside a large
`ExecuteMultiple`, presumably because each row does more server-side work (cascade and
relationship checks).

And unlike inserts they keep gaining from parallelism. The clean comparison is at a fixed
`BatchSize` of 125, where only `MaxDOP` varies: **27 rows/s at 4, 33 at 8**. Run-to-run
variance on deletes is about +/-2s, so that gain is outside the noise where the equivalent
insert gain (87 -> 92) is not.

The serial figure in the table above used `BatchSize` 100 rather than 125, so it is not part
of that comparison — `MaxDOP` 1 at 125 was never run. The trend across 4 and 8 is real; a
clean 1 -> 4 -> 8 curve at one batch size is not yet measured.

**Above 8 is unmeasured.** There was no plateau at 8, so higher may well help, but a DOP 16
run has not completed. Do not read the table as an optimum.

## `UseBulkDelete` is asynchronous, not fast

`connection.UseBulkDelete = true` takes a different path entirely (`BulkDeleteJobNode`):
it submits a Bulk Delete job and returns once it is **queued**.

```
1 row(s) in 3.4s        <- the job submission, not 1,000 deletions
```

The rows were still all present a minute later. The job appeared in `asyncoperation` as
`SQL 4 CDS Kontaktpersoner Bulk Delete Job` (named from the org's display language) with
`statecode = 2` / `statuscode = 20` — Locked, In Progress — and drained over roughly five
minutes.

So it is fire-and-forget for volumes where completion time does not matter, and the wrong
tool whenever the next step needs the rows gone. Its 3.4s is not comparable with the
synchronous numbers above and should not be put in the same table.

## SQL 4 CDS owns the parallelism

This spike sets one property. Everything else is the engine:

```
Sql4CdsConnection.MaxDegreeOfParallelism    settable on the connection
InsertNode.MaxDOP                           the value reaches the plan node
BaseDmlNode.UseParallelConnections()        returns IDisposable -- the engine's scope
BaseDmlNode.ServiceProtectionLimitHits      the engine counts its own 429s
```

There is no `Task.Run`, `Parallel.ForEach` or thread in `Program.cs`; the statement loop is
sequential. That the engine tracks its own service-protection hits is worth knowing: it
manages backpressure as well as concurrency, which is why `DataverseThrottling` advises
lowering `MaxDegreeOfParallelism` rather than adding retries.

## Affinity must be off for parallelism to mean anything

`ServiceClient` pins to one web server by default, which serializes concurrent requests
behind that server — the effect `spikes/ThrottleSpike/README.md` measured as *"a single
shared SDK client keeps affinity but serializes… `ServiceClient` gives concurrency **or**
affinity, never both."* This spike sets:

```csharp
service.EnableAffinityCookie = false;
```

The cost is observability, not correctness: with requests spread across the farm, the
per-server counters `dvduck doctor` reports describe servers that are not doing the work.
`x-ms-dop-hint` is unaffected, since it describes the environment.

## On the dop hint

`MaxDegreeOfParallelism` defaults to whatever `x-ms-dop-hint` reports, read through
`ServiceProtectionBudget.ProbeAsync` — one cheap `WhoAmI` — rather than guessed.
`--max-dop N` overrides it and the output says which was used.

This environment hints **4**. At 1,000 rows, 8 measured better for deletes (27 -> 33 rows/s)
and indistinguishable for inserts. At 8,000 rows, 8 was throttled.

So the hint is vindicated rather than beaten: exceeding it wins on small runs and gets
clamped on large ones, which is what a recommendation from the environment is for.
`docs/service-protection.md` says to design against the hint, and that advice stands —
this spike found the edge it was protecting.

**A 429 was provoked, and it changes how to read every delete figure above.**
This supersedes `spikes/ThrottleSpike/README.md`, which concluded these limits were hard to
reach from a single ordinary .NET client. They are — from a raw client issuing reads. A bulk
**delete** through SQL 4 CDS at `MaxDOP` 8 reaches them readily:

```
Deleting Kontaktpersoner 5,353 - 5,781 of 8,000 (8 threads) (1 threads paused until 21:59 due to service protection limits)
Deleting Kontaktpersoner 5,476 - 5,715 of 8,000 (7 threads) (2 threads paused until 21:59 ...)
Deleting Kontaktpersoner 5,657 - 5,782 of 8,000 (6 threads) (4 threads paused until 21:59 ...)
```

The engine degrades gracefully rather than failing: it parks workers until the retry window
passes — 8 threads down to 6, up to 4 paused — and completes. A caller sees only a slow run.

Consequences:

- **The 8,000-row delete figure (412.9s, 19 rows/s) is a rate-limited rate, not a cost.**
  Deletes do not inherently get worse with volume; they hit the limit, and inserts do not.
  The 33 rows/s at 1,000 rows is probably an unthrottled rate, and the two are therefore not
  comparable.
- **The 8,000-row insert is genuinely unthrottled.** Two runs, 37.2s and 41.9s (215 and 191
  rows/s), and the engine reported no retry or throttling messages in either.
- **`DataverseThrottling` is still untested against a real fault**, because the engine
  absorbs these before anything reaches our code. Reaching them is no longer the hard part;
  seeing them at our layer is.

### How to tell, and why the headers cannot

The engine counts service protection hits internally (`BaseDmlNode.ServiceProtectionLimitHits`)
with no public accessor, so the only way to observe throttling is to subscribe to
`Sql4CdsConnection.InfoMessage` and `Progress` and read what it says while working. This
spike does, and flags any message mentioning a retry, a pause or a limit.

The response headers are worse than useless for this. During the throttled run above,
`--budget-samples 4` reported:

```
server 668da569…  requests=7,999  execution=1200s
lowest execution-time remaining: 1200s of 1200s (0s drawn down)
```

A full budget, while the engine was being throttled. The counters are per user *per web
server*, affinity is off, and all four samples landed on a server that had done none of the
work. An earlier version of this spike drew a `-- sustainable` verdict from exactly that
reading; it now refuses to, and prints the engine's finding instead. **Trust the engine's
messages, not the headers** — unless affinity is pinned, in which case the headers describe
one server that did take the load and a burn rate can be computed.

## Practical recipe

Tuning only, and worth 2-3x. The 4-11x is in the choices above it: which table, which
message, which storage type. See "The mechanism matters more than the tuning".

```bash
# Insert: batch as large as Dataverse allows (1,000), and enough batches to
# fill MaxDOP -- batch size ~= rows / MaxDOP, capped at 1,000.
--count 8000 --batch-size 1000 --max-dop 8

# Delete: the opposite. Small batches, high parallelism.
--delete --count 8000 --batch-size 125 --max-dop 8

# Faster still, if you can go around SQL 4 CDS (1.7x insert, 11x elastic delete):
--native --count 8000 --batch-size 250 --max-dop 8
```

The single most common mistake is spending parallelism you have not got: with one
statement of N rows and `BatchSize` N there is one request, so `MaxDOP` does nothing
however high it is set. The spike prints a note when the configuration cannot spend its
parallelism -- a warning added after making that mistake three times.

## Caveats

Three narrow columns on `contact`, in one Developer environment, on one day. Wider rows,
plugins, or business rules on the table would all change these numbers, and a production
environment with real plugin logic on `contact` create would likely change them a lot —
`BypassCustomPlugins` exists on the connection for exactly that reason and was not used
here. Microsoft publishes no write-throughput figures and advises measuring; so does this.
