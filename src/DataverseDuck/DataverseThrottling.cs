using System.ServiceModel;
using Microsoft.Xrm.Sdk;

namespace DataverseDuck;

/// <summary>Which service protection limit was hit.</summary>
public enum ThrottleKind
{
    /// <summary>Not a service protection error.</summary>
    None,

    /// <summary>More than 6,000 requests in the 300-second sliding window.</summary>
    RequestCount,

    /// <summary>More than 20 minutes of combined execution time in the window.</summary>
    ExecutionTime,

    /// <summary>More than ~52 requests in flight at once.</summary>
    Concurrency,
}

/// <summary>
/// Recognises Dataverse service protection limit errors.
///
/// This deliberately does <b>not</b> retry. <c>ServiceClient</c> already pauses
/// for the <c>Retry-After</c> duration and resends, and has done since 2019.
/// Adding another retry loop on top would multiply the wait and make the
/// effective attempt count the product of the two, not the sum.
///
/// What is missing is not retrying but *explaining*: when a load fails despite
/// the SDK's retries, the raw fault says which numeric limit was hit and
/// nothing about what to do. The three limits have genuinely different fixes,
/// so the distinction is worth surfacing.
///
/// Limits are per user, per web server, over a 300-second sliding window, and
/// Microsoft documents them as defaults that vary by environment.
/// See <see href="https://learn.microsoft.com/power-apps/developer/data-platform/api-limits"/>.
/// </summary>
public static class DataverseThrottling
{
    /// <summary>Too many requests: 6,000 per 300s.</summary>
    public const int RequestCountErrorCode = -2147015902;

    /// <summary>Too much combined execution time: 1,200,000 ms per 300s.</summary>
    public const int ExecutionTimeErrorCode = -2147015903;

    /// <summary>Too many in flight at once: ~52.</summary>
    public const int ConcurrencyErrorCode = -2147015898;

    /// <summary>Key under which the SDK returns the wait duration.</summary>
    public const string RetryAfterKey = "Retry-After";

    /// <summary>
    /// Classifies an exception, unwrapping aggregates and inner exceptions
    /// because a fault raised inside the appender loop arrives wrapped.
    /// </summary>
    public static ThrottleKind Classify(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    var nested = Classify(inner);
                    if (nested != ThrottleKind.None)
                        return nested;
                }
            }

            if (current is FaultException<OrganizationServiceFault> { Detail: not null } fault)
            {
                var kind = FromErrorCode(fault.Detail.ErrorCode);
                if (kind != ThrottleKind.None)
                    return kind;
            }
        }

        return ThrottleKind.None;
    }

    /// <summary>Maps a Dataverse error code to a limit, or <see cref="ThrottleKind.None"/>.</summary>
    public static ThrottleKind FromErrorCode(int errorCode) => errorCode switch
    {
        RequestCountErrorCode => ThrottleKind.RequestCount,
        ExecutionTimeErrorCode => ThrottleKind.ExecutionTime,
        ConcurrencyErrorCode => ThrottleKind.Concurrency,
        _ => ThrottleKind.None,
    };

    /// <summary>
    /// Extracts the server's requested wait, if present. The SDK supplies it as
    /// a <see cref="TimeSpan"/> in <c>ErrorDetails</c>, unlike the Web API which
    /// uses a <c>Retry-After</c> header in seconds.
    /// </summary>
    public static TimeSpan? RetryAfter(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is FaultException<OrganizationServiceFault> { Detail: not null } fault &&
                fault.Detail.ErrorDetails.TryGetValue(RetryAfterKey, out var value))
            {
                return value switch
                {
                    TimeSpan span => span,
                    int seconds => TimeSpan.FromSeconds(seconds),
                    string text when int.TryParse(text, out var parsed) => TimeSpan.FromSeconds(parsed),
                    _ => (TimeSpan?)null,
                };
            }
        }

        return null;
    }

    /// <summary>
    /// Says what the user can actually do about it. Each limit has a different
    /// remedy, which is the reason for distinguishing them at all.
    /// </summary>
    public static string? Explain(Exception? exception)
    {
        var kind = Classify(exception);

        if (kind == ThrottleKind.None)
            return null;

        var wait = RetryAfter(exception) is { } retry
            ? $" The server asked for {retry.TotalSeconds:F0}s before retrying."
            : string.Empty;

        return kind switch
        {
            ThrottleKind.RequestCount =>
                "Dataverse throttled this: over 6,000 requests in 5 minutes. The SDK already " +
                "retried and still could not finish. Narrow the query so fewer pages are " +
                "fetched, or select fewer columns so each page carries more rows." + wait,

            ThrottleKind.ExecutionTime =>
                "Dataverse throttled this: over 20 minutes of combined server execution time " +
                "in a 5-minute window. This usually means the query is expensive rather than " +
                "merely large. Add a filter Dataverse can index, or split the load by date " +
                "range and cache each slice." + wait,

            ThrottleKind.Concurrency =>
                "Dataverse throttled this: too many concurrent requests (limit around 52). " +
                "Lower MaxDegreeOfParallelism on the SQL 4 CDS connection, or stop running " +
                "several caches at once." + wait,

            _ => null,
        };
    }
}

/// <summary>
/// A service protection limit stopped the load, after the SDK's own retries.
///
/// The DuckDB table is unchanged: loads run in a transaction, so nothing
/// partial is left behind and the previous copy, if any, still stands.
/// </summary>
public sealed class DataverseThrottledException(string message, Exception inner)
    : Exception(message, inner)
{
    /// <summary>Which limit was hit.</summary>
    public ThrottleKind Kind => DataverseThrottling.Classify(InnerException);

    /// <summary>How long the server asked us to wait, if it said.</summary>
    public TimeSpan? RetryAfter => DataverseThrottling.RetryAfter(InnerException);
}
