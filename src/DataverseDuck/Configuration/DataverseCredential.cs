using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DataverseDuck.Diagnostics;
using Microsoft.Identity.Client;
using Microsoft.PowerPlatform.Dataverse.Client;

namespace DataverseDuck.Configuration;

/// <summary>
/// How the application registration proves who it is. Either a client secret
/// or a certificate; there is no interactive sign-in anywhere in this library.
///
/// This exists because the two credentials have to satisfy three different
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

    /// <summary>Applies this credential to an MSAL confidential client.</summary>
    public abstract ConfidentialClientApplicationBuilder Apply(ConfidentialClientApplicationBuilder builder);

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

    public override ConfidentialClientApplicationBuilder Apply(ConfidentialClientApplicationBuilder builder) =>
        builder.WithClientSecret(Secret);

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

    public override ConfidentialClientApplicationBuilder Apply(ConfidentialClientApplicationBuilder builder) =>
        builder.WithCertificate(Certificate);

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
