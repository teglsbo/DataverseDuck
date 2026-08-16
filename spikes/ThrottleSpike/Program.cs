using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using DataverseDuck;
using DataverseDuck.Configuration;
using DataverseDuck.Diagnostics;
using Microsoft.Identity.Client;

// Spike: provoke a real service protection limit, and check that
// DataverseThrottling recognises what comes back.
//
// DataverseThrottling was written from documentation and unit tested against
// synthetic faults. That proves the mapping from an error code to a message. It
// does not prove Dataverse raises what we think it raises.
//
// Targets the EXECUTION TIME limit (1,200 seconds of server time per five
// minutes) rather than the request count limit (8,000 requests on this
// environment). Both are enforced per web server and both recover on the same
// five minute sliding window, but exhausting execution time takes a couple of
// hundred requests instead of several thousand.
//
// Two earlier attempts through the SDK failed to provoke anything, and why
// they failed is itself a finding. See README.md in this directory.

var options = Load();
if (options is null)
    return 1;

// Cookies are what make this work. Dataverse returns an ARRAffinity cookie
// identifying the web server that answered, and honouring it sends every
// subsequent request back to that same server. Since the limits are enforced
// per server, concentrating load is the only way to reach one without
// exhausting the entire environment's budget.
using var handler = new HttpClientHandler
{
    CookieContainer = new CookieContainer(),
    UseCookies = true,
    MaxConnectionsPerServer = 32,
};

using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(2) };

http.DefaultRequestHeaders.Authorization =
    new AuthenticationHeaderValue("Bearer", await AcquireTokenAsync(options));

// Expensive by design: a full page of a wide system table. Measured at about
// five seconds per call on this environment.
var heavy = new Uri(options.EnvironmentUrl, "api/data/v9.2/solutioncomponents?$top=5000");

Console.WriteLine($"Environment: {options.EnvironmentUrl}");
Console.WriteLine("Limit targeted: execution time, 1,200s per web server per 5 minutes.\n");

// Warm up, both to establish the affinity cookie and to read the starting budget.
using (var warmup = await http.GetAsync(new Uri(options.EnvironmentUrl, "api/data/v9.2/WhoAmI")))
{
    var start = ServiceProtectionBudget.FromHeaders(warmup.Headers);
    Console.WriteLine($"Pinned to server {Short(start.ServerAffinity)}");
    Console.WriteLine($"Starting budget: {start}\n");
}

const int Concurrency = 16;
const int MaxRounds = 40;

var stopwatch = Stopwatch.StartNew();
HttpResponseMessage? throttled = null;
var sent = 0;

for (var round = 1; round <= MaxRounds && throttled is null; round++)
{
    var responses = await Task.WhenAll(Enumerable.Range(0, Concurrency).Select(async _ =>
    {
        try
        {
            return await http.GetAsync(heavy);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"  request failed: {e.GetType().Name}: {e.Message}");
            return null;
        }
    }));

    sent += Concurrency;

    ServiceProtectionBudget? latest = null;

    foreach (var response in responses)
    {
        if (response is null)
            continue;

        if (response.StatusCode == HttpStatusCode.TooManyRequests && throttled is null)
        {
            throttled = response;
            continue;
        }

        latest ??= ServiceProtectionBudget.FromHeaders(response.Headers);
        response.Dispose();
    }

    Console.WriteLine(
        $"round {round,2}: {sent,4} sent, {stopwatch.Elapsed.TotalSeconds,5:F0}s elapsed | " +
        (latest is null ? "no budget reported" : Describe(latest)));
}

stopwatch.Stop();
Console.WriteLine();

if (throttled is null)
{
    Console.WriteLine($"Not throttled after {sent} requests in {stopwatch.Elapsed.TotalSeconds:F0}s.");
    return 0;
}

Console.WriteLine($"Throttled after {sent} requests in {stopwatch.Elapsed.TotalSeconds:F0}s.\n");

var body = await throttled.Content.ReadAsStringAsync();
var budget = ServiceProtectionBudget.FromHeaders(throttled.Headers);

Console.WriteLine($"  HTTP status:  {(int)throttled.StatusCode} {throttled.StatusCode}");
Console.WriteLine($"  Server:       {Short(budget.ServerAffinity)}");
Console.WriteLine($"  Retry-After:  {throttled.Headers.RetryAfter?.ToString() ?? "<not sent>"}");
Console.WriteLine($"  Budget:       {budget}");

// The Web API reports the code in hex; the SDK reports the same number as a
// signed integer. Converting one to the other is what lets us check that the
// code Dataverse actually sends is one DataverseThrottling knows about.
var hex = TryReadErrorField(body, "code");

Console.WriteLine($"  Error code:   {hex ?? "<none>"}");

if (hex is not null &&
    hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
    uint.TryParse(hex[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var unsigned))
{
    Console.WriteLine();
    Console.WriteLine($"  as signed int: {unchecked((int)unsigned)}");
    Console.WriteLine($"  DataverseThrottling.FromErrorCode => {DataverseThrottling.FromErrorCode(unchecked((int)unsigned))}");
}

Console.WriteLine();
Console.WriteLine($"  Message: {TryReadErrorField(body, "message")}");

throttled.Dispose();
return 0;

static string Describe(ServiceProtectionBudget budget) =>
    $"{budget.BurstRemaining?.ToString("N0", CultureInfo.InvariantCulture) ?? "?"} requests, " +
    $"{budget.TimeRemaining?.TotalSeconds.ToString("N0", CultureInfo.InvariantCulture) ?? "?"}s execution left " +
    $"[{Short(budget.ServerAffinity)}]";

static string Short(string? affinity) =>
    affinity is null ? "unknown" : affinity[..Math.Min(8, affinity.Length)];

static string? TryReadErrorField(string body, string field)
{
    try
    {
        return JsonDocument.Parse(body).RootElement
            .TryGetProperty("error", out var error) && error.TryGetProperty(field, out var value)
            ? value.GetString()
            : null;
    }
    catch (JsonException)
    {
        return null;
    }
}

static DataverseOptions? Load()
{
    DotEnvFile.LoadFromCurrentDirectory();

    if (DataverseOptions.TryLoadFromEnvironment(out var options, out var error))
        return options;

    Console.Error.WriteLine(error);
    return null;
}

static async Task<string> AcquireTokenAsync(DataverseOptions options)
{
    var application = ConfidentialClientApplicationBuilder
        .Create(options.ClientId)
        .WithClientSecret(options.ClientSecret)
        .WithAuthority(options.Authority)
        .Build();

    var result = await application.AcquireTokenForClient([options.Scope]).ExecuteAsync();
    return result.AccessToken;
}
