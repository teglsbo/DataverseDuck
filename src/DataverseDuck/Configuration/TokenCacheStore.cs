using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace DataverseDuck.Configuration;

/// <summary>
/// Where a device-code sign-in is remembered between runs.
///
/// MSAL's default token cache lives in the process that created it, so without
/// this every invocation of <c>dvduck</c> prompts for a new device code even
/// seconds after a successful sign-in. That is fine for one interactive query
/// and useless for anything else -- a script, a REPL restart, or an agent
/// driving the CLI. Persisting the cache means a person signs in once and the
/// refresh token carries subsequent runs until the tenant expires it.
///
/// **What is on disk is a credential.** The cache holds a refresh token, which
/// is exchangeable for access tokens without a further prompt for as long as it
/// remains valid. Where the platform offers a secret store -- the login keyring
/// on Linux, Keychain on macOS, DPAPI on Windows -- the cache is encrypted with
/// it. On a headless Linux box, which is where persistence matters most, there
/// is usually no keyring available, and the fallback is a plaintext file with
/// owner-only permissions. That is the same trade the Azure CLI and GitHub CLI
/// make, but it is a trade: treat the file as you would a password, and prefer a
/// client secret or a certificate for genuinely unattended access, since those
/// authenticate the application rather than carrying a person's session.
///
/// <see cref="Describe"/> reports which of the two happened, so
/// <c>dvduck doctor</c> can say it out loud rather than leaving it to be
/// discovered.
/// </summary>
public sealed class TokenCacheStore
{
    /// <summary>Overrides where the cache is written. Mainly for tests and for
    /// keeping a cache beside a profile rather than in one shared location.</summary>
    public const string PathVariable = "DATAVERSE_TOKEN_CACHE";

    /// <summary>Set to <c>0</c>, <c>false</c> or <c>no</c> to keep the cache in
    /// memory, restoring the pre-persistence behaviour. Provided because "do not
    /// write a refresh token to this disk" is a legitimate position and should not
    /// require unsetting the auth mode as well.</summary>
    public const string EnabledVariable = "DATAVERSE_TOKEN_CACHE_PERSIST";

    private const string CacheFileName = "msal.cache";
    private const string KeyringSchema = "dk.teglsbo.dvduck.tokencache";
    private const string KeyringCollection = "default";

    private TokenCacheStore(string? path, bool encrypted, string? fallbackReason)
    {
        Path = path;
        Encrypted = encrypted;
        FallbackReason = fallbackReason;
    }

    /// <summary>The cache file, or <c>null</c> when the cache stays in memory.</summary>
    public string? Path { get; }

    /// <summary>Whether a platform secret store protects the file.</summary>
    public bool Encrypted { get; }

    /// <summary>Why encryption was unavailable, when it was.</summary>
    public string? FallbackReason { get; }

    /// <summary>The cache is in memory and a sign-in will not outlive the process.</summary>
    public static TokenCacheStore Disabled { get; } = new(null, false, null);

    /// <summary>
    /// Resolves the cache location. An explicit <see cref="PathVariable"/> wins;
    /// otherwise the file sits under <c>$XDG_DATA_HOME/dvduck</c>, defaulting to
    /// <c>~/.local/share/dvduck</c>. Data rather than cache, deliberately: losing
    /// it costs a sign-in, which is more than a cache is allowed to cost.
    /// </summary>
    public static string ResolvePath(Func<string, string?> read)
    {
        var explicitPath = read(PathVariable);
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return System.IO.Path.GetFullPath(explicitPath.Trim());
        }

        var dataHome = read("XDG_DATA_HOME");
        var baseDir = string.IsNullOrWhiteSpace(dataHome)
            ? System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share")
            : dataHome.Trim();

        return System.IO.Path.Combine(baseDir, "dvduck", CacheFileName);
    }

    /// <summary>
    /// Whether persistence is switched on. Defaults to on; the variable exists to
    /// turn it off. Anything other than a recognised negative is treated as on, so
    /// a typo fails towards the documented default rather than silently disabling
    /// a feature the caller expected.
    /// </summary>
    public static bool IsEnabled(Func<string, string?> read)
    {
        var raw = read(EnabledVariable)?.Trim();
        return string.IsNullOrEmpty(raw)
            || !(raw.Equals("0", StringComparison.Ordinal)
                || raw.Equals("false", StringComparison.OrdinalIgnoreCase)
                || raw.Equals("no", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Attaches a persistent cache to <paramref name="app"/>, falling back to an
    /// unencrypted file when no platform secret store answers, and to memory only
    /// when the file itself cannot be written. Never throws: failing to persist a
    /// cache is a degradation, not an error, and a sign-in prompt is a working
    /// outcome.
    /// </summary>
    public static async Task<TokenCacheStore> AttachAsync(
        IPublicClientApplication app, string path, CancellationToken cancellationToken = default)
    {
        var directory = System.IO.Path.GetDirectoryName(path);
        try
        {
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
                RestrictToOwner(directory, isDirectory: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new TokenCacheStore(null, false, $"could not create {directory}: {ex.Message}");
        }

        var fileName = System.IO.Path.GetFileName(path);
        var builder = new StorageCreationPropertiesBuilder(fileName, directory!)
            .WithLinuxKeyring(KeyringSchema, KeyringCollection, "dvduck token cache",
                new KeyValuePair<string, string>("product", "dvduck"),
                new KeyValuePair<string, string>("component", "tokencache"))
            .WithMacKeyChain(KeyringSchema, "tokencache");

        var encrypted = await TryCreateAsync(app, builder.Build(), cancellationToken);
        if (encrypted is null)
        {
            RestrictToOwner(path, isDirectory: false);
            return new TokenCacheStore(path, encrypted: true, fallbackReason: null);
        }

        // No keyring, no Keychain, no DPAPI -- the common case in a container.
        var plaintext = await TryCreateAsync(
            app,
            new StorageCreationPropertiesBuilder(fileName, directory!)
                .WithUnprotectedFile()
                .Build(),
            cancellationToken);

        if (plaintext is not null)
        {
            return new TokenCacheStore(null, false, plaintext);
        }

        RestrictToOwner(path, isDirectory: false);
        return new TokenCacheStore(path, encrypted: false, fallbackReason: encrypted);
    }

    /// <summary>Registers the cache and confirms it actually round-trips.
    /// Returns null on success, or the reason it did not work.</summary>
    private static async Task<string?> TryCreateAsync(
        IPublicClientApplication app, StorageCreationProperties properties, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var helper = await MsalCacheHelper.CreateAsync(properties);
            // CreateAsync succeeds even where the backing store does not work, so
            // ask before trusting it.
            helper.VerifyPersistence();
            helper.RegisterCache(app.UserTokenCache);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Owner-only permissions. MSAL's extensions set these on the file already;
    /// this covers the directory, the unprotected-file path, and any file left
    /// behind by an older version. A no-op on Windows, where the ACL inherited
    /// from the user profile is the equivalent.
    /// </summary>
    private static void RestrictToOwner(string target, bool isDirectory)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var mode = isDirectory
                ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                : UnixFileMode.UserRead | UnixFileMode.UserWrite;

            if (isDirectory ? Directory.Exists(target) : File.Exists(target))
            {
                File.SetUnixFileMode(target, mode);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Best effort: a cache that works with loose permissions still beats
            // no cache, and the description below reports what protection exists.
        }
    }

    /// <summary>One line for <c>dvduck doctor</c>.</summary>
    public string Describe() => Path is null
        ? FallbackReason is null
            ? "in memory only; a sign-in will not outlive this process"
            : $"in memory only ({FallbackReason})"
        : Encrypted
            ? $"persisted, encrypted by the platform secret store: {Path}"
            : $"persisted UNENCRYPTED, owner-only permissions: {Path}";
}
