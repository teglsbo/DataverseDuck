using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DataverseDuck.Diagnostics;
using Microsoft.Identity.Client;
using Microsoft.PowerPlatform.Dataverse.Client;

namespace DataverseDuck.Configuration;

/// <summary>
/// How the application proves who it is: a client secret, a certificate, or --
/// for a human running the CLI at a keyboard rather than a headless service --
/// an interactive device-code sign-in.
///
/// The first two are app-only (MSAL confidential client, no user, no MFA
/// prompt possible even if the tenant requires it). Device-code is the only
/// credential here that authenticates as a *person*, which is also the only
/// case where MFA can apply at all, since MFA is a user-identity concept and
/// the app registration flows have no user in them.
///
/// This exists because the credentials have to satisfy three different
/// consumers -- MSAL, <see cref="ServiceClient"/>, and the diagnostics that
/// describe the configuration back to a human -- and only one of those accepts
/// a connection string. A certificate loaded from a PFX file cannot be
/// expressed as one at all, so construction is a method here rather than a
/// string built by the caller.
/// </summary>
public abstract class DataverseCredential
{
    /// <summary>
    /// A description safe to print or log. Implementations must never reveal
    /// the secret or the private key.
    /// </summary>
    public abstract string Describe();

    /// <summary>
    /// Acquires a Web API access token for <see cref="DataverseOptions.Scope"/>.
    /// Confidential credentials do this silently; <see cref="DeviceCodeCredential"/>
    /// may prompt on a cold cache.
    /// </summary>
    public abstract Task<AuthenticationResult> AcquireTokenAsync(
        DataverseOptions options, CancellationToken cancellationToken);

    /// <summary>Builds a connected <see cref="ServiceClient"/> for the given environment.</summary>
    public abstract ServiceClient CreateServiceClient(DataverseOptions options);
}

/// <summary>
/// The secret from Entra ID &gt; App registrations &gt; Certificates &amp; secrets.
/// Simplest to set up and the only credential this project has verified against
/// a live environment.
/// </summary>
public sealed class ClientSecretCredential(string secret) : DataverseCredential
{
    public string Secret { get; } = secret ?? throw new ArgumentNullException(nameof(secret));

    public override string Describe() => $"secret={AccessTokenClaims.Mask(Secret)}";

    public override async Task<AuthenticationResult> AcquireTokenAsync(
        DataverseOptions options, CancellationToken cancellationToken)
    {
        var app = ConfidentialClientApplicationBuilder.Create(options.ClientId)
            .WithClientSecret(Secret)
            .WithAuthority(options.Authority)
            .Build();

        return await app.AcquireTokenForClient([options.Scope]).ExecuteAsync(cancellationToken);
    }

    public override ServiceClient CreateServiceClient(DataverseOptions options) =>
        new(ToConnectionString(options));

    /// <summary>
    /// Never log this: it carries the secret. Kept separate from
    /// <see cref="CreateServiceClient"/> so tests can assert on it.
    /// </summary>
    internal string ToConnectionString(DataverseOptions options) =>
        $"AuthType=ClientSecret;Url={options.EnvironmentUrl};ClientId={options.ClientId};" +
        $"ClientSecret={Secret};RequireNewInstance=true";
}

/// <summary>
/// An X.509 certificate whose public key is registered on the application.
/// Preferred over a secret where the platform can protect the private key,
/// and the only option where policy forbids secrets outright.
/// </summary>
public sealed class CertificateCredential(X509Certificate2 certificate, string source) : DataverseCredential
{
    public X509Certificate2 Certificate { get; } = certificate ?? throw new ArgumentNullException(nameof(certificate));

    /// <summary>Where the certificate came from, for diagnostics. Not a secret.</summary>
    public string Source { get; } = source;

    public override string Describe() =>
        $"certificate={Certificate.Thumbprint} subject='{Certificate.Subject}' " +
        $"expires={Certificate.NotAfter:yyyy-MM-dd} from={Source}";

    public override async Task<AuthenticationResult> AcquireTokenAsync(
        DataverseOptions options, CancellationToken cancellationToken)
    {
        var app = ConfidentialClientApplicationBuilder.Create(options.ClientId)
            .WithCertificate(Certificate)
            .WithAuthority(options.Authority)
            .Build();

        return await app.AcquireTokenForClient([options.Scope]).ExecuteAsync(cancellationToken);
    }

    /// <summary>
    /// A certificate cannot travel in a connection string unless it is already
    /// in the platform certificate store, so this uses the constructor that
    /// accepts one directly. The thumbprint argument is null precisely because
    /// we have the certificate in hand and do not want the SDK to go looking
    /// for it a second time.
    /// </summary>
    public override ServiceClient CreateServiceClient(DataverseOptions options) =>
        new(Certificate,
            StoreName.My,
            certificateThumbPrint: null,
            instanceUrl: options.EnvironmentUrl,
            useUniqueInstance: true,
            orgDetail: null,
            clientId: options.ClientId,
            redirectUri: null);

    /// <summary>
    /// Loads a PFX or PEM certificate from disk. Portable across platforms,
    /// unlike the certificate store, which on Linux is a per-user directory
    /// that nothing else populates.
    /// </summary>
    public static bool TryLoadFromFile(
        string path,
        string? password,
        [NotNullWhen(true)] out CertificateCredential? credential,
        [NotNullWhen(false)] out string? error)
    {
        credential = null;

        if (!File.Exists(path))
        {
            error = $"No certificate file at '{path}'.";
            return false;
        }

        X509Certificate2 certificate;
        try
        {
            certificate = X509CertificateLoader.LoadPkcs12FromFile(
                path,
                password,
                X509KeyStorageFlags.EphemeralKeySet);
        }
        catch (CryptographicException e)
        {
            // Overwhelmingly a wrong or missing password, and the platform
            // message for that case says only 'The specified network password
            // is not correct', which sends people looking in the wrong place.
            error = $"Could not read the certificate at '{path}': {e.Message} " +
                    "If it is password protected, set the password variable.";
            return false;
        }

        if (!certificate.HasPrivateKey)
        {
            error = $"The certificate at '{path}' has no private key. Dataverse needs the " +
                    "private half to sign the token; the public .cer is what you upload to " +
                    "the app registration, not what you authenticate with.";
            return false;
        }

        credential = new CertificateCredential(certificate, path);
        error = null;
        return true;
    }

    /// <summary>
    /// Finds a certificate by thumbprint in the current user's store, then the
    /// machine store. Expired certificates are returned rather than filtered
    /// out, so that the failure says 'expired' instead of 'not found'.
    /// </summary>
    public static bool TryLoadFromStore(
        string thumbprint,
        [NotNullWhen(true)] out CertificateCredential? credential,
        [NotNullWhen(false)] out string? error)
    {
        credential = null;

        // Copy-pasting a thumbprint out of a certificate dialog brings spaces,
        // and out of Entra brings lowercase. Neither matches the store.
        var normalized = thumbprint.Replace(" ", string.Empty).Trim().ToUpperInvariant();

        foreach (var location in (ReadOnlySpan<StoreLocation>)[StoreLocation.CurrentUser, StoreLocation.LocalMachine])
        {
            using var store = new X509Store(StoreName.My, location);

            try
            {
                store.Open(OpenFlags.ReadOnly);
            }
            catch (CryptographicException)
            {
                // The store need not exist, which is the normal case on Linux.
                continue;
            }

            var found = store.Certificates.Find(X509FindType.FindByThumbprint, normalized, validOnly: false);
            if (found.Count == 0)
                continue;

            var certificate = found[0];

            if (!certificate.HasPrivateKey)
            {
                error = $"The certificate {normalized} in the {location} store has no private key.";
                return false;
            }

            credential = new CertificateCredential(certificate, $"{location}/My store");
            error = null;
            return true;
        }

        error = $"No certificate with thumbprint {normalized} in the CurrentUser or LocalMachine " +
                "'My' store. On Linux that store is a per-user directory that nothing populates " +
                "by default, so a certificate file is usually the better option there.";
        return false;
    }
}

/// <summary>
/// Interactive sign-in as a human, via MSAL's device-code flow: the CLI prints
/// a URL and a short code, the user opens the URL on any device, enters the
/// code, and completes the sign-in there -- including any MFA challenge the
/// tenant requires. This is the only credential in this file that can ever
/// see an MFA prompt, because MFA is a property of a *user* session and the
/// other two credentials authenticate the application itself, with no user
/// in the flow at all.
///
/// Uses a <see cref="PublicClientApplication"/> (not confidential -- there is
/// no secret to protect), and caches the resulting token in memory for the
/// process lifetime via MSAL's own silent-token-first behaviour, so a session
/// that acquires more than one token is not prompted twice.
/// </summary>
public sealed class DeviceCodeCredential(string? username = null, Func<DeviceCodeResult, Task>? onCodeReady = null)
    : DataverseCredential
{
    private IPublicClientApplication? _app;

    /// <summary>
    /// Optional. Device-code itself has no field for a username -- you type
    /// whatever account you sign in with at the browser prompt -- so this is
    /// used only to pick the right cached account across repeated runs when
    /// more than one has signed in on this machine before. Never sent as, or
    /// treated as, a credential -- device-code proves identity by completing
    /// the sign-in, not by naming an account.
    /// </summary>
    public string? Username { get; } = string.IsNullOrWhiteSpace(username) ? null : username.Trim();

    public override string Describe() =>
        Username is null
            ? "device-code (interactive sign-in, human user)"
            : $"device-code (interactive sign-in, cached account={Username})";

    public override async Task<AuthenticationResult> AcquireTokenAsync(
        DataverseOptions options, CancellationToken cancellationToken)
    {
        var app = GetOrBuildApp(options);

        var accounts = await app.GetAccountsAsync();
        var existing = Username is null
            ? accounts.FirstOrDefault()
            : accounts.FirstOrDefault(a => string.Equals(a.Username, Username, StringComparison.OrdinalIgnoreCase))
                ?? accounts.FirstOrDefault();

        if (existing is not null)
        {
            try
            {
                return await app.AcquireTokenSilent([options.Scope], existing)
                    .ExecuteAsync(cancellationToken);
            }
            catch (MsalUiRequiredException)
            {
                // Silent renewal failed (expired refresh token, revoked
                // consent, changed MFA policy, ...) -- fall through to a
                // fresh interactive prompt rather than failing outright.
            }
        }

        return await app
            .AcquireTokenWithDeviceCode([options.Scope], DefaultCallback(onCodeReady))
            .ExecuteAsync(cancellationToken);
    }

    private static Func<DeviceCodeResult, Task> DefaultCallback(Func<DeviceCodeResult, Task>? onCodeReady) =>
        onCodeReady ?? (deviceCode =>
        {
            Console.WriteLine(deviceCode.Message);
            return Task.CompletedTask;
        });

    private IPublicClientApplication GetOrBuildApp(DataverseOptions options) =>
        _app ??= PublicClientApplicationBuilder.Create(options.ClientId)
            .WithAuthority(options.Authority)
            // The well-known redirect URI for device-code and other flows with
            // no browser to redirect back to; MSAL requires one be set anyway.
            .WithRedirectUri("https://login.microsoftonline.com/common/oauth2/nativeclient")
            .Build();

    /// <summary>
    /// There is no connection string shape for an interactive, per-user token,
    /// and the SDK's <see cref="ServiceClient"/> constructors are all built
    /// around either a connection string or a credential it manages itself.
    /// The one exception -- <c>ServiceClient(Uri, Func&lt;string, Task&lt;string&gt;&gt;, bool, ILogger)</c>
    /// -- accepts an arbitrary async token provider, which is exactly this
    /// credential's <see cref="AcquireTokenAsync"/> wrapped to return a bare
    /// access token string instead of the full MSAL result.
    /// </summary>
    public override ServiceClient CreateServiceClient(DataverseOptions options) =>
        new(
            instanceUrl: options.EnvironmentUrl,
            tokenProviderFunction: async _ => (await AcquireTokenAsync(options, CancellationToken.None)).AccessToken,
            useUniqueInstance: true,
            logger: null!);
}
