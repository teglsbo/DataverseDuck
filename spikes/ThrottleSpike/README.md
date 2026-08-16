# ThrottleSpike

**Question:** can we provoke a real service protection 429 against a live environment, so
that `DataverseThrottling` is tested against a fault Dataverse actually sent rather than
one we constructed in a unit test?

**Answer: no.** Three attempts, none of which throttled. The reasons they failed are the
finding, and they are more useful than the 429 would have been.

Only the third attempt survives in `Program.cs`; the first two were overwritten as the
experiment moved on and are recorded here.

## What the limits are

Three limits apply per user, per web server, over a five minute sliding window:

| Limit | Documented | Measured here |
|---|---|---|
| Requests | 6,000 | **8,000** |
| Execution time | 20 minutes | 20 minutes (`1,200.00` seconds) |
| Concurrent requests | 52 | not reached |

The environment reports the two counters and a recommended parallelism in Web API
response headers. See `ServiceProtectionBudget` and [docs/service-protection.md](../../docs/service-protection.md).

## Attempt 1 — SDK, `Clone()` per task

`ServiceClient.Clone()` for each worker, ramping 60 → 400 concurrent `WhoAmI` calls.

400 requests completed in **3.0 seconds** (133/s). Nothing throttled.

Probing the budget afterwards explained why: **820 requests moved the observed burst
counter by 2.** Each clone authenticates separately and lands on a different web server,
so the load was diluted across the farm. The per-server caveat in the documentation is
not a footnote — it is the dominant effect.

## Attempt 2 — SDK, one shared client

Keep a single `ServiceClient` so every request keeps the same server affinity.

400 requests took **125.5 seconds** — about 3.2/s. The requests were **serialized**.

So `ServiceClient` gives you concurrency *or* affinity, never both. That alone puts the
52-request concurrency limit close to out of reach from an ordinary single .NET client,
which is the practical reason our users are unlikely to see a 429 from this library.

## Attempt 3 — HttpClient with a pinned cookie

To get both, drop the SDK: `HttpClient` with a `CookieContainer`, which preserves the
`ARRAffinity` cookie across parallel requests, `MaxConnectionsPerServer = 32`,
concurrency 16, reading the headers each round.

Target the execution-time limit rather than the request count, because it is the cheaper
of the two to exhaust: `solutioncomponents?$top=5000` costs about 5.5 seconds of server
execution per request.

40 rounds, 640 requests, 356 seconds of wall clock. The execution-time budget fell from
1,164s to about 594s and then **stopped falling**, oscillating around that level:

```
594 → 620 → 599 → 609 → 594
```

That is equilibrium, not progress. We burned roughly 570 seconds of execution over 356
seconds of wall clock, about **1.6 s/s**. The five minute sliding window forgives 1,200
seconds every 300 seconds, about **4 s/s**. Consumption was well under replenishment, so
the budget settled at half full and could never reach zero.

**The untested hypothesis:** to exhaust the window, sustained concurrency has to exceed
roughly 4× the per-request duration — about 40 or more concurrent 5-second requests
against a single pinned server, versus the 16 used here. Not attempted, because at that
point the run stops being a measurement and becomes a denial of service against our own
environment for five minutes.

## What this leaves

`DataverseThrottling.FromErrorCode` has still never seen a live fault. `Program.cs`
converts the hex error code from the response body into the signed integer the SDK would
surface and feeds it through that path, so the wiring is ready — that branch simply never
executed.

The failed attempts are worth more than a green checkmark would have been:

- The budget headers are per server, so an unpinned client dilutes its own load and the
  counters it reads describe a server it may never touch again.
- The SDK will not give you affinity and concurrency together.
- The recommended parallelism this environment reports is **4**, an order of magnitude
  below the concurrency limit. That, not the limits, is the number to design against.

## Running it

Needs a populated `.env` at the repository root. It is deliberately aggressive against a
single server; do not point it at anything anyone depends on.

```bash
dotnet run --project spikes/ThrottleSpike
```
