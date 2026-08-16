using System.Globalization;
using System.Net.Http.Headers;
using DataverseDuck.Configuration;
using Microsoft.Identity.Client;

namespace DataverseDuck.Diagnostics;

/// <summary>
/// The service protection budget Dataverse reports, read from the Web API.
///
/// Dataverse enforces three limits: number of requests, combined execution
/// time, and concurrency. <see cref="DataverseThrottling"/> only ever learns
/// about them after one has been exceeded, because the SDK's SOAP channel
/// surfaces the fault but not the running totals. Those totals exist only as
/// Web API response headers, so reading them takes a separate HTTP call.
///
/// Two caveats, both from Microsoft's documentation, and both the reason the
/// remaining counts are described as indicative rather than authoritative:
///
/// 1. The limits are enforced per web server, and most environments have more
///    than one. This probe carries no affinity cookie, so it reaches an
///    arbitrary server -- not necessarily the one serving the SDK connection.
/// 2. The documentation says outright: "Don't depend on these values to
///    control how many requests you send. They're intended for debugging
///    purposes."
///
/// <see cref="RecommendedParallelism"/> does not share those caveats. It
/// describes the environment rather than one server, so it is worth acting on.
/// </summary>
public sealed record ServiceProtectionBudget
{
    /// <summary>Requests left for this connection in the sliding window.</summary>
    public const string BurstRemainingHeader = "x-ms-ratelimit-burst-remaining-xrm-requests";

    /// <summary>Combined execution time left for this user, in seconds.</summary>
    public const string TimeRemainingHeader = "x-ms-ratelimit-time-remaining-xrm-requests";

    /// <summary>The environment's recommended degree of parallelism.</summary>
    public const string ParallelismHintHeader = "x-ms-dop-hint";

    /// <summary>
    /// Azure's server affinity cookie. Opaque, but stable per web server, so
    /// comparing it across probes tells you whether two readings describe the
    /// same server's budget or two different ones.
    /// </summary>
    public const string AffinityCookieName = "ARRAffinity";

    /// <summary>
    /// Identifies which web server answered, or null if it did not say.
    ///
    /// This matters because the remaining counts are enforced per server. Two
    /// probes reporting different values mean nothing if this differs between
    /// them -- they are readings of two separate budgets, not a change in one.
    /// The value is Azure's opaque affinity token, useful only for comparison.
    /// </summary>
    public string? ServerAffinity { get; init; }

    /// <summary>
    /// Requests remaining. Microsoft documents a default ceiling of 6,000 per
    /// five minutes, but the header is authoritative and environments differ:
    /// the environment this was developed against reports 7,999 remaining on
    /// an idle connection, so its ceiling is 8,000. Null when not sent.
    /// </summary>
    public int? BurstRemaining { get; init; }

    /// <summary>
    /// Execution time remaining, against a documented default of 20 minutes
    /// per five minutes. Null when the server did not send the header.
    /// </summary>
    public TimeSpan? TimeRemaining { get; init; }

    /// <summary>
    /// How many requests this environment suggests running concurrently.
    /// Prefer this over a thread count derived from local CPU cores, which
    /// says nothing about the server's capacity.
    /// </summary>
    public int? RecommendedParallelism { get; init; }

    /// <summary>True when the server sent none of the three headers.</summary>
    public bool IsEmpty =>
        BurstRemaining is null && TimeRemaining is null && RecommendedParallelism is null;

    /// <summary>
    /// Reads the headers from an already-received response. Separated from the
    /// request so it can be tested without a network, and so a caller that
    /// already has a Web API response can reuse it.
    /// </summary>
    public static ServiceProtectionBudget FromHeaders(HttpResponseHeaders headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        return new ServiceProtectionBudget
        {
            BurstRemaining = ReadNumber(headers, BurstRemainingHeader) is { } burst
                ? (int)burst
                : null,
            TimeRemaining = ReadNumber(headers, TimeRemainingHeader) is { } seconds
                ? TimeSpan.FromSeconds((double)seconds)
                : null,
            RecommendedParallelism = ReadNumber(headers, ParallelismHintHeader) is { } parallelism
                ? (int)parallelism
                : null,
            ServerAffinity = ReadAffinity(headers),
        };
    }

    /// <summary>
    /// Asks the environment for its current budget using the cheapest call
    /// available. WhoAmI touches no business data and returns a fixed-size
    /// payload, so the probe costs about as little as a request can.
    /// </summary>
    public static async Task<ServiceProtectionBudget> ProbeAsync(
        DataverseOptions options,
        HttpClient? httpClient = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var application = ConfidentialClientApplicationBuilder
            .Create(options.ClientId)
            .WithClientSecret(options.ClientSecret)
            .WithAuthority(options.Authority)
            .Build();

        var token = await application
            .AcquireTokenForClient([options.Scope])
            .ExecuteAsync(cancellationToken);

        var owned = httpClient is null ? new HttpClient() : null;
        var client = httpClient ?? owned!;

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                new Uri(options.EnvironmentUrl, "api/data/v9.2/WhoAmI"));

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

            using var response = await client.SendAsync(request, cancellationToken);

            // Deliberately not throwing on a failure status. A 429 carries the
            // headers too, and is the moment they matter most.
            return FromHeaders(response.Headers);
        }
        finally
        {
            owned?.Dispose();
        }
    }

    /// <summary>
    /// A one-line summary for a log. Reports only what the server actually
    /// sent, so an absent header reads as absent rather than as zero.
    /// </summary>
    public override string ToString()
    {
        if (IsEmpty)
            return "service protection budget: not reported";

        var parts = new List<string>(3);

        if (BurstRemaining is { } burst)
            parts.Add($"{burst.ToString("N0", CultureInfo.InvariantCulture)} requests left");

        if (TimeRemaining is { } time)
            parts.Add($"{time.TotalSeconds.ToString("N0", CultureInfo.InvariantCulture)}s execution time left");

        if (RecommendedParallelism is { } parallelism)
            parts.Add($"recommended parallelism {parallelism.ToString(CultureInfo.InvariantCulture)}");

        return $"service protection budget: {string.Join(", ", parts)}";
    }

    /// <summary>
    /// Parses a header value.
    ///
    /// These are not all bare integers, which is worth stating because it is
    /// easy to assume otherwise and the failure is silent. A live environment
    /// returns <c>x-ms-dop-hint: 4</c> and
    /// <c>x-ms-ratelimit-burst-remaining-xrm-requests: 7999</c>, but
    /// <c>x-ms-ratelimit-time-remaining-xrm-requests: 1,200.00</c> -- a
    /// grouping separator and two decimal places. An integer parse rejects
    /// that, and since a rejected value is indistinguishable from an absent
    /// one, the budget would have reported the execution time as unavailable
    /// forever.
    ///
    /// Parsed as invariant, so the comma is a grouping separator and the stop
    /// is a decimal point regardless of the machine's locale.
    /// </summary>
    private static decimal? ReadNumber(HttpResponseHeaders headers, string name) =>
        headers.TryGetValues(name, out var values) &&
        decimal.TryParse(values.FirstOrDefault(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    private static string? ReadAffinity(HttpResponseHeaders headers)
    {
        if (!headers.TryGetValues("Set-Cookie", out var cookies))
            return null;

        // Dataverse sends several cookies and repeats this one, so scan by
        // name rather than assuming a position.
        foreach (var cookie in cookies)
        {
            var start = cookie.IndexOf(AffinityCookieName + "=", StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                continue;

            var valueStart = start + AffinityCookieName.Length + 1;
            var end = cookie.IndexOf(';', valueStart);

            return end < 0 ? cookie[valueStart..] : cookie[valueStart..end];
        }

        return null;
    }
}
