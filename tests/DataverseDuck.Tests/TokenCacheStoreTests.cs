using DataverseDuck.Configuration;
using Microsoft.Identity.Client;

namespace DataverseDuck.Tests;

public class TokenCacheStoreTests : IDisposable
{
    private readonly List<string> _directories = [];

    public void Dispose()
    {
        foreach (var directory in _directories.Where(Directory.Exists))
        {
            Directory.Delete(directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dvduck-cache-{Guid.NewGuid():N}");
        _directories.Add(path);
        return path;
    }

    private static Func<string, string?> Reader(params (string Name, string? Value)[] variables) =>
        name => variables.FirstOrDefault(v => v.Name == name).Value;

    // --- ResolvePath -------------------------------------------------------

    [Fact]
    public void ResolvePath_prefers_the_explicit_override()
    {
        var path = TokenCacheStore.ResolvePath(
            Reader((TokenCacheStore.PathVariable, "/tmp/somewhere/else.cache"),
                   ("XDG_DATA_HOME", "/tmp/ignored")));

        Assert.Equal("/tmp/somewhere/else.cache", path);
    }

    [Fact]
    public void ResolvePath_trims_the_override()
    {
        var path = TokenCacheStore.ResolvePath(Reader((TokenCacheStore.PathVariable, "  /tmp/padded.cache  ")));

        Assert.Equal("/tmp/padded.cache", path);
    }

    [Fact]
    public void ResolvePath_makes_a_relative_override_absolute()
    {
        // A relative path would otherwise resolve against whatever directory the
        // process happened to start in, so the same setting would name different
        // files across runs.
        var path = TokenCacheStore.ResolvePath(Reader((TokenCacheStore.PathVariable, "relative.cache")));

        Assert.True(Path.IsPathRooted(path));
        Assert.EndsWith("relative.cache", path);
    }

    [Fact]
    public void ResolvePath_uses_XDG_DATA_HOME_when_no_override()
    {
        var path = TokenCacheStore.ResolvePath(Reader(("XDG_DATA_HOME", "/tmp/xdg-data")));

        Assert.Equal(Path.Combine("/tmp/xdg-data", "dvduck", "msal.cache"), path);
    }

    [Fact]
    public void ResolvePath_falls_back_to_the_user_profile()
    {
        var path = TokenCacheStore.ResolvePath(Reader());

        Assert.Equal(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "share", "dvduck", "msal.cache"),
            path);
    }

    [Fact]
    public void ResolvePath_ignores_a_blank_override()
    {
        // An exported-but-empty variable is a common shape in shell scripts and
        // should not resolve the cache to the current directory.
        var path = TokenCacheStore.ResolvePath(
            Reader((TokenCacheStore.PathVariable, "   "), ("XDG_DATA_HOME", "/tmp/xdg-blank")));

        Assert.Equal(Path.Combine("/tmp/xdg-blank", "dvduck", "msal.cache"), path);
    }

    // --- IsEnabled ---------------------------------------------------------

    [Fact]
    public void IsEnabled_defaults_to_on()
    {
        Assert.True(TokenCacheStore.IsEnabled(Reader()));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("FALSE")]
    [InlineData("no")]
    [InlineData("No")]
    [InlineData("  0  ")]
    public void IsEnabled_recognises_the_negatives(string value)
    {
        Assert.False(TokenCacheStore.IsEnabled(Reader((TokenCacheStore.EnabledVariable, value))));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("yes")]
    [InlineData("flase")]
    public void IsEnabled_treats_anything_else_as_on(string value)
    {
        // A typo should fail towards the documented default rather than quietly
        // disabling persistence the caller asked for.
        Assert.True(TokenCacheStore.IsEnabled(Reader((TokenCacheStore.EnabledVariable, value))));
    }

    // --- AttachAsync -------------------------------------------------------

    private static IPublicClientApplication BuildApp() =>
        PublicClientApplicationBuilder
            .Create("51f81489-12ee-4a9e-aaae-a2591f45987d")
            .WithAuthority("https://login.microsoftonline.com/organizations")
            .WithRedirectUri("https://login.microsoftonline.com/common/oauth2/nativeclient")
            .Build();

    [Fact]
    public async Task AttachAsync_creates_the_directory_and_reports_the_path()
    {
        var path = Path.Combine(TempDirectory(), "msal.cache");

        var store = await TokenCacheStore.AttachAsync(BuildApp(), path);

        Assert.Equal(path, store.Path);
        Assert.True(Directory.Exists(Path.GetDirectoryName(path)));
    }

    [Fact]
    public async Task AttachAsync_restricts_the_directory_to_its_owner()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = TempDirectory();
        await TokenCacheStore.AttachAsync(BuildApp(), Path.Combine(directory, "msal.cache"));

        var mode = File.GetUnixFileMode(directory);
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            mode);
    }

    [Fact]
    public async Task AttachAsync_falls_back_to_memory_when_the_path_cannot_be_created()
    {
        // A file standing where the directory needs to be: creating it fails, and
        // that must degrade to a prompt rather than taking the process down.
        var blocker = Path.Combine(TempDirectory(), "blocker");
        Directory.CreateDirectory(Path.GetDirectoryName(blocker)!);
        await File.WriteAllTextAsync(blocker, "not a directory");

        var store = await TokenCacheStore.AttachAsync(BuildApp(), Path.Combine(blocker, "msal.cache"));

        Assert.Null(store.Path);
        Assert.False(store.Encrypted);
        Assert.NotNull(store.FallbackReason);
    }

    [Fact]
    public async Task AttachAsync_describes_an_unencrypted_cache_as_unencrypted()
    {
        // No keyring is reachable from the test host, so this is the fallback path
        // -- the same one a container takes, and the one whose wording matters most.
        var path = Path.Combine(TempDirectory(), "msal.cache");
        var store = await TokenCacheStore.AttachAsync(BuildApp(), path);

        if (store.Encrypted)
        {
            Assert.Contains("encrypted", store.Describe(), StringComparison.Ordinal);
            Assert.DoesNotContain("UNENCRYPTED", store.Describe(), StringComparison.Ordinal);
            return;
        }

        Assert.Contains("UNENCRYPTED", store.Describe(), StringComparison.Ordinal);
        Assert.Contains(path, store.Describe(), StringComparison.Ordinal);
    }

    // --- Disabled ----------------------------------------------------------

    [Fact]
    public void Disabled_has_no_path_and_says_so()
    {
        Assert.Null(TokenCacheStore.Disabled.Path);
        Assert.False(TokenCacheStore.Disabled.Encrypted);
        Assert.Null(TokenCacheStore.Disabled.FallbackReason);
        Assert.Contains("in memory only", TokenCacheStore.Disabled.Describe(), StringComparison.Ordinal);
    }
}
