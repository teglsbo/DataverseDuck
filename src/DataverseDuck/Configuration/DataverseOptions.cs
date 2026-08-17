using System.Diagnostics.CodeAnalysis;
using System.Text;
using Microsoft.Identity.Client;
using Microsoft.PowerPlatform.Dataverse.Client;

namespace DataverseDuck.Configuration;

/// <summary>
/// Connection settings for a Dataverse environment, authenticated as an
/// application registration (service principal). There is no interactive
/// sign-in and no user identity anywhere in this flow.
///
/// Settings come from environment variables, optionally under a named profile
/// so that one machine can address several environments.
/// </summary>
public sealed class DataverseOptions
{
    public const string UrlVariable = "DATAVERSE_URL";
    public const string TenantIdVariable = "DATAVERSE_TENANT_ID";
    public const string ClientIdVariable = "DATAVERSE_CLIENT_ID";
    public const string ClientSecretVariable = "DATAVERSE_CLIENT_SECRET";
    public const string CertificatePathVariable = "DATAVERSE_CERT_PATH";
    public const string CertificatePasswordVariable = "DATAVERSE_CERT_PASSWORD";
    public const string CertificateThumbprintVariable = "DATAVERSE_CERT_THUMBPRINT";

    /// <summary>
    /// Set to 'devicecode' to sign in interactively as a human instead of as
    /// the application. This is the one auth mode where MFA can apply, since
    /// it is the only one with a user in the flow at all.
    /// </summary>
    public const string AuthModeVariable = "DATAVERSE_AUTH_MODE";

    public const string DeviceCodeAuthMode = "devicecode";

    /// <summary>
    /// Optional login hint for device-code sign-in: pre-fills the account at
    /// the sign-in prompt and disambiguates cached accounts on repeated runs.
    /// Not a credential in itself -- device-code proves identity by
    /// completing the sign-in, not by naming an account.
    /// </summary>
    public const string UsernameVariable = "DATAVERSE_USERNAME";

    /// <summary>Selects a profile when no name is passed explicitly.</summary>
    public const string ProfileVariable = "DATAVERSE_PROFILE";

    private const string Prefix = "DATAVERSE_";

    public required Uri EnvironmentUrl { get; init; }
    public required string ClientId { get; init; }

    /// <summary>How the application proves who it is.</summary>
    public required DataverseCredential Credential { get; init; }

    /// <summary>
    /// The profile these settings came from, or null for the unprefixed
    /// default. Reported by diagnostics so that a surprising result is
    /// traceable to the environment it actually came from.
    /// </summary>
    public string? Profile { get; init; }

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

    /// <summary>A one line summary safe to print. Contains no secret.</summary>
    public string Describe() =>
        $"profile={Profile ?? "(default)"} url={EnvironmentUrl} clientId={ClientId} " +
        $"tenant={TenantId ?? "(organizations)"} {Credential.Describe()}";

    /// <summary>Builds a connected client using whichever credential is configured.</summary>
    public ServiceClient CreateServiceClient() => Credential.CreateServiceClient(this);

    /// <summary>
    /// Acquires a Web API access token for the configured credential, for the
    /// Web API paths (doctor, budget probing) that do not go through the SDK.
    /// </summary>
    public Task<AuthenticationResult> AcquireTokenAsync(CancellationToken cancellationToken = default) =>
        Credential.AcquireTokenAsync(this, cancellationToken);

    /// <summary>Reads settings from environment variables for the default profile.</summary>
    public static bool TryLoadFromEnvironment(
        [NotNullWhen(true)] out DataverseOptions? options,
        [NotNullWhen(false)] out string? error) =>
        TryLoadFromEnvironment(null, out options, out error);

    /// <summary>
    /// Reads settings from environment variables, optionally under a profile.
    ///
    /// A profile named 'prod' is read from DATAVERSE_PROD_URL,
    /// DATAVERSE_PROD_CLIENT_ID and so on. Any value the profile does not set
    /// falls back to the unprefixed variable, so several environments sharing
    /// one app registration need only override the URL.
    ///
    /// When no profile is named here, <see cref="ProfileVariable"/> is
    /// consulted, and failing that the unprefixed variables are used on their
    /// own. A single-environment setup therefore behaves exactly as it did
    /// before profiles existed.
    ///
    /// Returns false with an explanation rather than throwing, because a
    /// missing variable is the most common first-run problem and deserves a
    /// readable message.
    /// </summary>
    public static bool TryLoadFromEnvironment(
        string? profile,
        [NotNullWhen(true)] out DataverseOptions? options,
        [NotNullWhen(false)] out string? error)
    {
        options = null;

        if (string.IsNullOrWhiteSpace(profile))
            profile = Environment.GetEnvironmentVariable(ProfileVariable);

        if (string.IsNullOrWhiteSpace(profile))
            profile = null;

        if (profile is not null)
        {
            if (!TryNormalizeProfile(profile, out var normalized, out error))
                return false;

            if (!ProfileExists(normalized))
            {
                // Falling back to the unprefixed variables is the point of
                // profiles, but a profile that overrides nothing is not one
                // that inherits everything -- it is a name nobody ever set.
                // Silently handing back the default environment is the worst
                // outcome here, because the operation then succeeds against
                // the wrong tenant.
                var known = KnownProfiles();

                error = $"No variables are set for profile '{profile}'. Expected at least one of " +
                        $"{Prefix}{normalized}_URL, {Prefix}{normalized}_CLIENT_ID, " +
                        $"{Prefix}{normalized}_CLIENT_SECRET or the certificate equivalents. " +
                        (known.Count == 0
                            ? "No profiles are configured at all; omit the profile to use the " +
                              "unprefixed variables."
                            : $"Configured profiles: {string.Join(", ", known)}.");
                return false;
            }
        }

        string? Read(string variable) => ReadForProfile(variable, profile);

        return TryCreate(
            Read(UrlVariable),
            Read(ClientIdVariable),
            Read(ClientSecretVariable),
            Read(TenantIdVariable),
            Read(CertificatePathVariable),
            Read(CertificatePasswordVariable),
            Read(CertificateThumbprintVariable),
            Read(AuthModeVariable),
            Read(UsernameVariable),
            profile,
            out options,
            out error);
    }

    /// <summary>
    /// The variables a profile may override. Order is not significant; this is
    /// also what profile discovery matches names against.
    /// </summary>
    private static readonly string[] Suffixes =
    [
        UrlVariable, TenantIdVariable, ClientIdVariable, ClientSecretVariable,
        CertificatePathVariable, CertificatePasswordVariable, CertificateThumbprintVariable,
        AuthModeVariable, UsernameVariable,
    ];

    private static bool ProfileExists(string normalized) =>
        Suffixes.Any(suffix => !string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable(
                string.Concat(Prefix, normalized, "_", suffix.AsSpan(Prefix.Length)))));

    /// <summary>
    /// Every profile that has at least one variable set, so that a mistyped
    /// name can be answered with the names that do exist.
    ///
    /// Note that no default variable can be mistaken for a profiled one:
    /// DATAVERSE_CLIENT_SECRET would need a suffix of 'SECRET' to look like
    /// profile 'CLIENT', and no such suffix exists. The same holds for
    /// DATAVERSE_CERT_PATH and DATAVERSE_TENANT_ID.
    /// </summary>
    public static IReadOnlyList<string> KnownProfiles()
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);

        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var name = (string)entry.Key;

            if (!name.StartsWith(Prefix, StringComparison.Ordinal))
                continue;

            foreach (var suffix in Suffixes)
            {
                var tail = suffix.AsSpan(Prefix.Length);

                if (name.Length <= Prefix.Length + tail.Length + 1)
                    continue;

                if (!name.AsSpan(name.Length - tail.Length).SequenceEqual(tail))
                    continue;

                if (name[name.Length - tail.Length - 1] != '_')
                    continue;

                found.Add(name[Prefix.Length..(name.Length - tail.Length - 1)]);
                break;
            }
        }

        return [.. found];
    }

    /// <summary>
    /// The profile-qualified name of a variable, for error messages that have
    /// to tell the reader which variable to actually set.
    /// </summary>
    public static string VariableName(string variable, string? profile)
    {
        if (profile is null || !TryNormalizeProfile(profile, out var normalized, out _))
            return variable;

        return string.Concat(Prefix, normalized, "_", variable.AsSpan(Prefix.Length));
    }

    private static string? ReadForProfile(string variable, string? profile)
    {
        if (profile is not null)
        {
            var scoped = Environment.GetEnvironmentVariable(VariableName(variable, profile));
            if (!string.IsNullOrWhiteSpace(scoped))
                return scoped;
        }

        return Environment.GetEnvironmentVariable(variable);
    }

    /// <summary>
    /// Profile names become part of an environment variable name, so they are
    /// upper-cased and anything that cannot appear in one is rejected rather
    /// than quietly rewritten. A name that became a different name would read
    /// a different environment, which is the failure here worth being loud
    /// about.
    /// </summary>
    private static bool TryNormalizeProfile(
        string profile,
        [NotNullWhen(true)] out string? normalized,
        [NotNullWhen(false)] out string? error)
    {
        normalized = null;
        var trimmed = profile.Trim();

        if (trimmed.Length == 0)
        {
            error = "The profile name is empty.";
            return false;
        }

        var builder = new StringBuilder(trimmed.Length);
        foreach (var c in trimmed)
        {
            if (char.IsAsciiLetterOrDigit(c))
                builder.Append(char.ToUpperInvariant(c));
            else if (c is '-' or '_')
                builder.Append('_');
            else
            {
                error = $"Profile '{profile}' cannot be used: a profile name becomes part of an " +
                        "environment variable name, so it may contain only letters, digits, " +
                        $"hyphens and underscores. '{c}' is not one of those.";
                return false;
            }
        }

        normalized = builder.ToString();

        if (char.IsAsciiDigit(normalized[0]))
        {
            error = $"Profile '{profile}' cannot be used: it would produce the variable " +
                    $"'{Prefix}{normalized}_URL', and not every shell allows a name whose first " +
                    "character after the prefix is a digit.";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Microsoft's own published sample application ID for interactive/OAuth
    /// scenarios (used in Microsoft's XRM tooling connection-string docs and
    /// widely reused by tools like XrmToolBox). Pre-registered as a public
    /// client with the native redirect URI in every tenant, which is what
    /// lets device-code sign-in work with nothing more than a URL and a
    /// username -- no app registration step of your own required.
    /// </summary>
    public const string WellKnownDeviceCodeClientId = "51f81489-12ee-4a9e-aaae-a2591f45987d";

    public static bool TryCreate(
        string? url,
        string? clientId,
        string? clientSecret,
        string? tenantId,
        [NotNullWhen(true)] out DataverseOptions? options,
        [NotNullWhen(false)] out string? error) =>
        TryCreate(url, clientId, clientSecret, tenantId, null, null, null, null, null, null, out options, out error);

    public static bool TryCreate(
        string? url,
        string? clientId,
        string? clientSecret,
        string? tenantId,
        string? certificatePath,
        string? certificatePassword,
        string? certificateThumbprint,
        string? authMode,
        string? username,
        string? profile,
        [NotNullWhen(true)] out DataverseOptions? options,
        [NotNullWhen(false)] out string? error)
    {
        options = null;

        string Named(string variable) => VariableName(variable, profile);

        var isDeviceCode = string.Equals(authMode?.Trim(), DeviceCodeAuthMode, StringComparison.OrdinalIgnoreCase);

        // Device-code is the one mode that can run with nothing but a URL:
        // Microsoft's well-known sample client ID is already a registered
        // public client in every tenant, so there is no app registration to
        // create first.
        if (isDeviceCode && string.IsNullOrWhiteSpace(clientId))
            clientId = WellKnownDeviceCodeClientId;

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(url)) missing.Add(Named(UrlVariable));
        if (string.IsNullOrWhiteSpace(clientId)) missing.Add(Named(ClientIdVariable));

        if (missing.Count > 0)
        {
            error = $"Missing required environment variable(s): {string.Join(", ", missing)}.";
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps)
        {
            error = $"{Named(UrlVariable)} must be an absolute https URL, for example " +
                    $"'https://contoso.crm4.dynamics.com'. Got: '{url}'.";
            return false;
        }

        if (!Guid.TryParse(clientId, out _))
        {
            error = $"{Named(ClientIdVariable)} must be the application (client) ID GUID, not the " +
                    "application name and not the object ID.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(tenantId) && !Guid.TryParse(tenantId, out _))
        {
            error = $"{Named(TenantIdVariable)}, when set, must be the directory (tenant) ID GUID.";
            return false;
        }

        if (!TryResolveCredential(
                clientSecret, certificatePath, certificatePassword, certificateThumbprint, authMode, username,
                profile, out var credential, out error))
            return false;

        options = new DataverseOptions
        {
            // A trailing path would corrupt the audience, so keep scheme + host only.
            EnvironmentUrl = new Uri(parsed.GetLeftPart(UriPartial.Authority)),
            ClientId = clientId!,
            Credential = credential,
            TenantId = string.IsNullOrWhiteSpace(tenantId) ? null : tenantId,
            Profile = profile,
        };
        error = null;
        return true;
    }

    /// <summary>
    /// Exactly one credential must be configured. Two is refused rather than
    /// resolved by precedence: a leftover secret quietly winning over a
    /// certificate someone had just switched to would be very hard to see.
    /// Device-code is the exception: it needs no secret material, so it is
    /// selected by an explicit opt-in (<see cref="AuthModeVariable"/>) rather
    /// than by a value being present, and is likewise refused alongside any
    /// of the other three -- an app that could authenticate either as itself
    /// or as whoever is at the keyboard is not a choice this should make
    /// silently.
    /// </summary>
    private static bool TryResolveCredential(
        string? clientSecret,
        string? certificatePath,
        string? certificatePassword,
        string? certificateThumbprint,
        string? authMode,
        string? username,
        string? profile,
        [NotNullWhen(true)] out DataverseCredential? credential,
        [NotNullWhen(false)] out string? error)
    {
        credential = null;

        string Named(string variable) => VariableName(variable, profile);

        var isDeviceCode = string.Equals(authMode?.Trim(), DeviceCodeAuthMode, StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(authMode) && !isDeviceCode)
        {
            error = $"{Named(AuthModeVariable)}='{authMode}' is not recognized. The only supported " +
                    $"value is '{DeviceCodeAuthMode}'; leave it unset to authenticate with a secret " +
                    "or certificate instead.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(username) && !isDeviceCode)
        {
            error = $"{Named(UsernameVariable)} only applies to device-code sign-in. Set " +
                    $"{Named(AuthModeVariable)}={DeviceCodeAuthMode} to use it, or unset it.";
            return false;
        }

        var configured = new List<string>();
        if (!string.IsNullOrWhiteSpace(clientSecret)) configured.Add(Named(ClientSecretVariable));
        if (!string.IsNullOrWhiteSpace(certificatePath)) configured.Add(Named(CertificatePathVariable));
        if (!string.IsNullOrWhiteSpace(certificateThumbprint)) configured.Add(Named(CertificateThumbprintVariable));
        if (isDeviceCode) configured.Add(Named(AuthModeVariable));

        if (configured.Count == 0)
        {
            error = $"No credential is configured. Set {Named(ClientSecretVariable)} for a client " +
                    $"secret, {Named(CertificatePathVariable)} for a certificate file, " +
                    $"{Named(CertificateThumbprintVariable)} for one in the platform certificate " +
                    $"store, or {Named(AuthModeVariable)}={DeviceCodeAuthMode} for interactive sign-in.";
            return false;
        }

        if (configured.Count > 1)
        {
            error = $"More than one credential is configured ({string.Join(", ", configured)}). " +
                    "Set exactly one, so that which of them authenticates is never in doubt.";
            return false;
        }

        if (isDeviceCode)
        {
            credential = new DeviceCodeCredential(username);
            error = null;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(clientSecret))
        {
            credential = new ClientSecretCredential(clientSecret);
            error = null;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(certificatePath))
        {
            if (!CertificateCredential.TryLoadFromFile(
                    certificatePath, certificatePassword, out var fromFile, out error))
                return false;

            credential = fromFile;
            return true;
        }

        if (!CertificateCredential.TryLoadFromStore(certificateThumbprint!, out var fromStore, out error))
            return false;

        credential = fromStore;
        return true;
    }
}
