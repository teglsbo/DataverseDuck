# Service protection limits

Dataverse protects itself by limiting what a single user may consume. Exceed a limit and
requests fail with HTTP 429 and a `Retry-After` header. This document records what the
limits are, what this library does about them, and what happened when we tried to reach
them.

## The three limits

Per user, per **web server**, over a five minute sliding window:

| Limit | Documented default | Measured on our environment |
|---|---|---|
| Number of requests | 6,000 | **8,000** |
| Combined execution time | 20 minutes | 20 minutes |
| Concurrent requests | 52 | not reached |

The documentation warns that limits vary between environments, and ours proves it: the
burst counter starts at 8,000, not 6,000. Do not hard code either number.

## Reading the budget

The environment reports its state in Web API response headers:

```
x-ms-dop-hint: 4
x-ms-ratelimit-burst-remaining-xrm-requests: 7999
x-ms-ratelimit-time-remaining-xrm-requests: 1,200.00
```

`dvduck doctor` prints these as an `[INFO]` block, and `ServiceProtectionBudget.ProbeAsync`
exposes them to code:

```
[INFO] Service protection budget
       Recommended parallelism: 4
       Requests remaining:      7,997
       Execution time left:     1,200s
```

`ServerAffinity` is available on the record but not printed by `doctor`.

Three things about those headers are worth knowing before you use them.

**`time-remaining` is not an integer.** It arrives as `1,200.00` — grouping separator, two
decimals. Our first parser used `int.TryParse`, which rejected it, and a rejected value
was indistinguishable from a missing header, so the budget silently reported execution
time as permanently unavailable. It is now parsed as an invariant `decimal`. Two tests
guard that.

**The two counters describe one web server, not the environment.** A client that does not
pin its affinity is spread across the farm, so a counter it read a moment ago may belong
to a server it never touches again. `ServiceProtectionBudget.ServerAffinity` reports which
server answered, taken from the `ARRAffinity` cookie, so you can tell whether two readings
are comparable at all.

**`x-ms-dop-hint` is the exception** — it describes the environment, and it is the only one
of the three worth acting on. Ours says **4**, against a concurrency limit of 52. Design
against the hint, not the limit.

The documentation is explicit that the counters are diagnostic:

> Don't depend on these values to control how many requests you send. They're intended for
> debugging purposes.

**None of these headers reach the SDK.** `Microsoft.PowerPlatform.Dataverse.Client` 1.2.2
contains the string `Retry-After` and neither `ratelimit` nor `dop` — verified by reading
the managed string literals out of the assembly. Our data path is SQL 4 CDS over SOAP, so
the budget is invisible along it. The probe uses the Web API separately, which is why it
is a `doctor` reading rather than something reported per query.

## When you are throttled

`DataverseThrottling` turns the fault into an explanation: which limit was hit, how long
to wait, and what to change. It handles the three service protection codes and the
concurrency code, and surfaces `Retry-After` where Dataverse supplied one.

The most common remedy is to lower `MaxDegreeOfParallelism`, and the recommended
parallelism above is a better starting point than a guess.

## We could not provoke a 429

Three attempts against a live environment, none of which throttled. In short:

- The SDK cloned per worker is fast but spreads across servers — **820 requests moved the
  observed counter by 2**.
- A single shared SDK client keeps affinity but serializes, about 3.2 requests/second.
  `ServiceClient` gives concurrency **or** affinity, never both.
- A raw `HttpClient` with a pinned cookie achieves both, but 16-way parallelism burned
  execution time at ~1.6 s/s against a window that replenishes at ~4 s/s. The budget
  settled at half full and stayed there.

The practical conclusion is reassuring for users of this library: from a single ordinary
.NET client, these limits are hard to reach at all. It also means `DataverseThrottling`
remains tested against constructed faults rather than a real one, which the README says
plainly.

The full write-up, including the arithmetic and the one untested hypothesis, is in
[spikes/ThrottleSpike/README.md](../spikes/ThrottleSpike/README.md).

## Official documentation

- [Service protection API limits](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/api-limits)
- [Send parallel requests](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/send-parallel-requests)
