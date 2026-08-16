using System.Diagnostics.CodeAnalysis;

namespace DataverseDuck.Configuration;

/// <summary>
/// Connection settings for a Dataverse environment, authenticated as an
/// application registration (service principal). There is no interactive
/// sign-in and no user identity anywhere in this flow.
/// </summary>
public sealed class DataverseOptions
{
    public const string UrlVariable = "DATAVERSE_URL";
    public const string TenantIdVariable = "DATAVERSE_TENANT_ID";
    public const string ClientIdVariable = "DATAVERSE_CLIENT_ID";
    public const string ClientSecretVariable = "DATAVERSE_CLIENT_SECRET";

    public required Uri EnvironmentUrl { get; init; }
    public required string ClientId { get; init; }
    public required string ClientSecret { get; init; }

    /// <summary>
    /// Optional. When absent, MSAL is pointed at the 'organizations' authority,
    /// which works but produces a less specific error if the app registration
    /// lives in a different tenant than expected.
    /// </summary>
    public string? TenantId { get; init; }

    /// <summary>
    /// The scope for a Dataverse Web API token.
    ///
    /// Deliberately derived from the environment URL rather than a generic
    /// audience. A Dataverse token is audience-bound to the organization, so a
    /// token acquired for 'https://database.windows.net/' is rejected even
    /// though it authenticates successfully.
    /// </summary>
    public string Scope => $"{EnvironmentUrl.GetLeftPart(UriPartial.Authority)}/.default";

    /// <summary>
    /// The authority MSAL should authenticate against.
    /// </summary>
    public string Authority =>
        $"https://login.microsoftonline.com/{(string.IsNullOrWhiteSpace(TenantId) ? "organizations" : TenantId)}";

    /// <summary>
    /// Reads settings from environment variables. Returns false with an
    /// explanation rather than throwing, because a missing variable is the
    /// most common first-run problem and deserves a readable message.
    /// </summary>
    public static bool TryLoadFromEnvironment(
        [NotNullWhen(true)] out DataverseOptions? options,
        [NotNullWhen(false)] out string? error) =>
        TryCreate(
            Environment.GetEnvironmentVariable(UrlVariable),
            Environment.GetEnvironmentVariable(ClientIdVariable),
            Environment.GetEnvironmentVariable(ClientSecretVariable),
            Environment.GetEnvironmentVariable(TenantIdVariable),
            out options,
            out error);

    public static bool TryCreate(
        string? url,
        string? clientId,
        string? clientSecret,
        string? tenantId,
        [NotNullWhen(true)] out DataverseOptions? options,
        [NotNullWhen(false)] out string? error)
    {
        options = null;

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(url)) missing.Add(UrlVariable);
        if (string.IsNullOrWhiteSpace(clientId)) missing.Add(ClientIdVariable);
        if (string.IsNullOrWhiteSpace(clientSecret)) missing.Add(ClientSecretVariable);

        if (missing.Count > 0)
        {
            error = $"Missing required environment variable(s): {string.Join(", ", missing)}.";
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps)
        {
            error = $"{UrlVariable} must be an absolute https URL, for example " +
                    $"'https://contoso.crm4.dynamics.com'. Got: '{url}'.";
            return false;
        }

        if (!Guid.TryParse(clientId, out _))
        {
            error = $"{ClientIdVariable} must be the application (client) ID GUID, not the " +
                    "application name and not the object ID.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(tenantId) && !Guid.TryParse(tenantId, out _))
        {
            error = $"{TenantIdVariable}, when set, must be the directory (tenant) ID GUID.";
            return false;
        }

        options = new DataverseOptions
        {
            // A trailing path would corrupt the audience, so keep scheme + host only.
            EnvironmentUrl = new Uri(parsed.GetLeftPart(UriPartial.Authority)),
            ClientId = clientId!,
            ClientSecret = clientSecret!,
            TenantId = string.IsNullOrWhiteSpace(tenantId) ? null : tenantId,
        };
        error = null;
        return true;
    }

    /// <summary>
    /// Connection string for <c>ServiceClient</c>. Never log this: it carries the secret.
    /// </summary>
    public string ToConnectionString() =>
        $"AuthType=ClientSecret;Url={EnvironmentUrl};ClientId={ClientId};ClientSecret={ClientSecret};RequireNewInstance=true";
}
