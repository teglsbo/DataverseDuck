using System.Net.Http.Headers;
using DataverseDuck.Diagnostics;

namespace DataverseDuck.Tests;

public class ServiceProtectionBudgetTests
{
    private static HttpResponseHeaders Headers(params (string Name, string Value)[] values)
    {
        var response = new HttpResponseMessage();

        foreach (var (name, value) in values)
            response.Headers.TryAddWithoutValidation(name, value);

        return response.Headers;
    }

    [Fact]
    public void Reads_all_three_headers()
    {
        // Values and formats copied from a live response, including the
        // grouped and decimalised execution time.
        var budget = ServiceProtectionBudget.FromHeaders(Headers(
            (ServiceProtectionBudget.BurstRemainingHeader, "7999"),
            (ServiceProtectionBudget.TimeRemainingHeader, "1,200.00"),
            (ServiceProtectionBudget.ParallelismHintHeader, "4")));

        Assert.Equal(7999, budget.BurstRemaining);
        Assert.Equal(TimeSpan.FromSeconds(1200), budget.TimeRemaining);
        Assert.Equal(4, budget.RecommendedParallelism);
        Assert.False(budget.IsEmpty);
    }

    [Fact]
    public void Time_remaining_survives_grouping_and_decimals()
    {
        // The header is not a bare integer. An integer parse rejects this and
        // the rejection is indistinguishable from the header being absent, so
        // the budget would silently report execution time as never available.
        var budget = ServiceProtectionBudget.FromHeaders(Headers(
            (ServiceProtectionBudget.TimeRemainingHeader, "1,200.00")));

        Assert.Equal(TimeSpan.FromMinutes(20), budget.TimeRemaining);
    }

    [Fact]
    public void Header_numbers_are_read_as_invariant()
    {
        // Under da-DK the comma is a decimal separator, which would turn
        // 1,200.00 into something quite different or reject it outright.
        var original = Thread.CurrentThread.CurrentCulture;

        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("da-DK");

            var budget = ServiceProtectionBudget.FromHeaders(Headers(
                (ServiceProtectionBudget.TimeRemainingHeader, "1,200.00")));

            Assert.Equal(TimeSpan.FromMinutes(20), budget.TimeRemaining);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }

    [Fact]
    public void Time_remaining_is_interpreted_as_seconds()
    {
        var budget = ServiceProtectionBudget.FromHeaders(Headers(
            (ServiceProtectionBudget.TimeRemainingHeader, "600")));

        Assert.Equal(TimeSpan.FromMinutes(10), budget.TimeRemaining);
    }

    [Fact]
    public void Reads_the_server_affinity_cookie()
    {
        // Dataverse sends several cookies, and repeats this one.
        var budget = ServiceProtectionBudget.FromHeaders(Headers(
            ("Set-Cookie", "ReqClientId=cf4dcad4-4f68-49b3-bd30-2845e7a47367; path=/; secure; HttpOnly"),
            ("Set-Cookie", "ARRAffinity=668da569856debcf; path=/; secure; HttpOnly"),
            ("Set-Cookie", "orgId=e8fb0497-cd97-f111-9969-000d3ab7305c; path=/; secure; HttpOnly")));

        Assert.Equal("668da569856debcf", budget.ServerAffinity);
    }

    [Fact]
    public void Server_affinity_is_null_when_no_cookie_is_sent()
    {
        Assert.Null(ServiceProtectionBudget.FromHeaders(Headers()).ServerAffinity);
    }

    [Fact]
    public void Missing_headers_are_null_rather_than_zero()
    {
        // Zero would read as "no budget left", which is the opposite of
        // "the server did not say".
        var budget = ServiceProtectionBudget.FromHeaders(Headers());

        Assert.Null(budget.BurstRemaining);
        Assert.Null(budget.TimeRemaining);
        Assert.Null(budget.RecommendedParallelism);
        Assert.True(budget.IsEmpty);
    }

    [Fact]
    public void Zero_remaining_is_distinguished_from_absent()
    {
        var budget = ServiceProtectionBudget.FromHeaders(Headers(
            (ServiceProtectionBudget.BurstRemainingHeader, "0")));

        Assert.Equal(0, budget.BurstRemaining);
        Assert.False(budget.IsEmpty);
    }

    [Fact]
    public void Unparseable_values_are_ignored()
    {
        var budget = ServiceProtectionBudget.FromHeaders(Headers(
            (ServiceProtectionBudget.BurstRemainingHeader, "not-a-number"),
            (ServiceProtectionBudget.ParallelismHintHeader, "8")));

        Assert.Null(budget.BurstRemaining);
        Assert.Equal(8, budget.RecommendedParallelism);
    }

    [Fact]
    public void Header_names_are_matched_case_insensitively()
    {
        // HTTP field names are case insensitive per RFC 9110 section 5.1, and
        // nothing guarantees the casing Microsoft's documentation uses.
        var budget = ServiceProtectionBudget.FromHeaders(Headers(
            ("X-MS-DOP-HINT", "6")));

        Assert.Equal(6, budget.RecommendedParallelism);
    }

    [Fact]
    public void Summary_reports_only_what_the_server_sent()
    {
        var budget = ServiceProtectionBudget.FromHeaders(Headers(
            (ServiceProtectionBudget.ParallelismHintHeader, "4")));

        var summary = budget.ToString();

        Assert.Contains("recommended parallelism 4", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("requests left", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Summary_says_so_when_nothing_was_reported()
    {
        Assert.Equal(
            "service protection budget: not reported",
            ServiceProtectionBudget.FromHeaders(Headers()).ToString());
    }

    [Fact]
    public void Summary_does_not_depend_on_the_current_culture()
    {
        var original = Thread.CurrentThread.CurrentCulture;

        try
        {
            // da-DK groups with a full stop, so a culture-sensitive format
            // would render 5.998 and read as a decimal.
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("da-DK");

            var summary = ServiceProtectionBudget.FromHeaders(Headers(
                (ServiceProtectionBudget.BurstRemainingHeader, "5998"))).ToString();

            Assert.Contains("5,998 requests left", summary, StringComparison.Ordinal);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }
}
