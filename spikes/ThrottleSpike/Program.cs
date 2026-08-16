using System.Diagnostics;
using DataverseDuck;
using DataverseDuck.Configuration;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk.Query;

// Spike: provoke a real service protection limit and check that
// DataverseThrottling classifies and explains it.
//
// DataverseThrottling was written from the documentation and unit tested with
// synthetic faults. That proves the mapping from an error code to a message; it
// does not prove Dataverse raises what we think it raises, or that the fault
// survives the SDK wrapping intact.
//
// Deliberately targets the CONCURRENCY limit (-2147015898), which clears in
// seconds, rather than the request-count limit (6,000 per 5 minutes), which
// would lock the environment out for the rest of the window for everyone.

DotEnvFile.LoadFromCurrentDirectory();

if (!DataverseOptions.TryLoadFromEnvironment(out var options, out var error))
{
    Console.Error.WriteLine(error);
    return 1;
}

using var client = new ServiceClient(options.ToConnectionString());

if (!client.IsReady)
{
    Console.Error.WriteLine($"Connection failed: {client.LastError}");
    return 1;
}

Console.WriteLine($"Connected to {options.EnvironmentUrl}");
Console.WriteLine("Documented limit: 52 concurrent requests per server, per app, per user.");
Console.WriteLine("Ramping concurrency until something is refused, or we give up.\n");

// A query that is cheap for the server, so that what we are testing is
// concurrency rather than execution time.
static QueryExpression Cheap() => new("account")
{
    ColumnSet = new ColumnSet("accountid"),
    TopCount = 1,
};

var kinds = new Dictionary<ThrottleKind, int>();
var otherFailures = new Dictionary<string, int>();
Exception? firstThrottle = null;

foreach (var concurrency in new[] { 60, 120, 240, 400 })
{
    var stopwatch = Stopwatch.StartNew();

    var results = await Task.WhenAll(Enumerable.Range(0, concurrency).Select(async _ =>
    {
        try
        {
            // Clone gives each task its own channel; without it the SDK
            // serialises calls and no concurrency reaches the server.
            using var connection = client.Clone();
            await Task.Run(() => connection.RetrieveMultiple(Cheap()));
            return (Kind: ThrottleKind.None, Error: (Exception?)null);
        }
        catch (Exception e)
        {
            return (Kind: DataverseThrottling.Classify(e), Error: e);
        }
    }));

    stopwatch.Stop();

    var throttled = 0;

    foreach (var (kind, exception) in results)
    {
        if (kind != ThrottleKind.None)
        {
            kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
            firstThrottle ??= exception;
            throttled++;
        }
        else if (exception is not null)
        {
            var name = exception.GetType().Name;
            otherFailures[name] = otherFailures.GetValueOrDefault(name) + 1;
        }
    }

    var ok = results.Count(r => r.Error is null);

    Console.WriteLine(
        $"concurrency {concurrency,4}: {ok,4} ok, {throttled,3} throttled, " +
        $"{results.Length - ok - throttled,3} other failures, {stopwatch.Elapsed.TotalSeconds:F1}s");

    if (throttled > 0)
        break;
}

Console.WriteLine();

if (firstThrottle is null)
{
    Console.WriteLine("Nothing was throttled. The limit is per front-end server and requests");
    Console.WriteLine("are load balanced, so a short burst from one client may never reach it.");

    if (otherFailures.Count > 0)
        Console.WriteLine($"Other failures seen: {string.Join(", ", otherFailures.Select(p => $"{p.Key} x{p.Value}"))}");

    return 0;
}

Console.WriteLine("Throttled. What DataverseThrottling makes of it:\n");

foreach (var (kind, count) in kinds)
    Console.WriteLine($"  {kind}: {count}");

Console.WriteLine();
Console.WriteLine($"  Classify:   {DataverseThrottling.Classify(firstThrottle)}");
Console.WriteLine($"  RetryAfter: {DataverseThrottling.RetryAfter(firstThrottle)?.ToString() ?? "<none supplied>"}");
Console.WriteLine($"  Explain:    {DataverseThrottling.Explain(firstThrottle)}");
Console.WriteLine();
Console.WriteLine($"  Raw type:   {firstThrottle.GetType().FullName}");
Console.WriteLine($"  Raw message: {firstThrottle.Message}");

return 0;
