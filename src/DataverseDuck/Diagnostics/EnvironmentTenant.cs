using System.Text.RegularExpressions;

namespace DataverseDuck.Diagnostics;

/// <summary>
/// Asks a Dataverse environment which Entra tenant it belongs to.
///
/// An unauthenticated request to the Web API returns 401 with a
/// <c>WWW-Authenticate</c> header naming the authority to sign in against, and
/// that authority URL contains the tenant GUID. No credentials are needed to
/// read it.
///
/// This settles a question that is otherwise guesswork. When a token request
/// fails with "application not found in the directory", the cause is either a
/// wrong client ID or an app registered in a different tenant from the
/// environment -- and those have completely different fixes. Comparing the two
/// tenants tells you which, instead of asking the user to check both.
/// </summary>
public static partial class EnvironmentTenant
{
    /// <summary>
    /// Reads the tenant GUID the environment authenticates against, or null if
    /// the challenge could not be read.
    /// </summary>
    public static async Task<string?> DiscoverAsync(
        string environmentUrl,
        HttpClient? client = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentUrl);

        var owned = client is null;
        client ??= new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        try
        {
            var url = new Uri(new Uri(environmentUrl), "/api/data/v9.2/WhoAmI");
            using var response = await client.GetAsync(url, cancellationToken);

            // Any 401 carries the challenge; a 200 would mean the endpoint is
            // not the one we think it is.
            if (!response.Headers.TryGetValues("WWW-Authenticate", out var values))
            {
                return null;
            }

            return values.Select(Extract).FirstOrDefault(guid => guid is not null);
        }
        catch (Exception e) when (e is HttpRequestException or UriFormatException)
        {
            // Discovery is a diagnostic aid, not a gate. If the network says no,
            // the caller still has its own error to report.
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // An HttpClient-internal timeout (e.g. the default Timeout above
            // elapsing), not the caller asking to stop -- same diagnostic
            // fallback as any other network failure. A caller-requested
            // cancellation is left to propagate rather than being swallowed here.
            return null;
        }
        finally
        {
            if (owned)
            {
                client.Dispose();
            }
        }
    }

    /// <summary>Pulls the tenant GUID out of an authorization_uri in the challenge.</summary>
    internal static string? Extract(string header) =>
        AuthorityPattern().Match(header) is { Success: true } match
            ? match.Groups["tenant"].Value
            : null;

    [GeneratedRegex(
        @"login\.microsoftonline\.com/(?<tenant>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})",
        RegexOptions.IgnoreCase)]
    private static partial Regex AuthorityPattern();
}
