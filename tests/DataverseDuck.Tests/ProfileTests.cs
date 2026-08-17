using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DataverseDuck.Configuration;

namespace DataverseDuck.Tests;

/// <summary>
/// These tests mutate process-wide environment variables, so they must not run
/// beside anything else that reads them.
/// </summary>
[CollectionDefinition("environment variables", DisableParallelization = true)]
public class EnvironmentVariableCollection;

[Collection("environment variables")]
public class ProfileTests : IDisposable
{
    private const string ValidClientId = "11111111-2222-3333-4444-555555555555";
    private const string OtherClientId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private const string ValidTenantId = "99999999-8888-7777-6666-555555555555";

    private readonly Dictionary<string, string?> _saved = [];

    /// <summary>
    /// Snapshots and clears every DATAVERSE_ variable, so that a developer who
    /// has a real environment exported does not get different results from CI.
    /// </summary>
    public ProfileTests()
    {
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var name = (string)entry.Key;
            if (!name.StartsWith("DATAVERSE_", StringComparison.Ordinal))
                continue;

            _saved[name] = (string?)entry.Value;
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    public void Dispose()
    {
        foreach (var (name, value) in _saved)
            Environment.SetEnvironmentVariable(name, value);

        foreach (var name in _set.Where(name => !_saved.ContainsKey(name)))
            Environment.SetEnvironmentVariable(name, null);

        GC.SuppressFinalize(this);
    }

    private readonly List<string> _set = [];

    private void Set(string name, string? value)
    {
        _set.Add(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    [Fact]
    public void The_unprefixed_variables_are_the_default_profile()
    {
        Set(DataverseOptions.UrlVariable, "https://default.crm4.dynamics.com");
        Set(DataverseOptions.ClientIdVariable, ValidClientId);
        Set(DataverseOptions.ClientSecretVariable, "secret");

        Assert.True(DataverseOptions.TryLoadFromEnvironment(out var options, out var error));

        Assert.Null(error);
        Assert.Null(options.Profile);
        Assert.Equal("https://default.crm4.dynamics.com/", options.EnvironmentUrl.ToString());
    }

    [Fact]
    public void A_profile_reads_its_own_prefixed_variables()
    {
        Set(DataverseOptions.UrlVariable, "https://default.crm4.dynamics.com");
        Set(DataverseOptions.ClientIdVariable, ValidClientId);
        Set(DataverseOptions.ClientSecretVariable, "secret");

        Set("DATAVERSE_PROD_URL", "https://prod.crm4.dynamics.com");
        Set("DATAVERSE_PROD_CLIENT_ID", OtherClientId);
        Set("DATAVERSE_PROD_CLIENT_SECRET", "prod-secret");

        Assert.True(DataverseOptions.TryLoadFromEnvironment("prod", out var options, out _));

        Assert.Equal("prod", options.Profile);
        Assert.Equal("https://prod.crm4.dynamics.com/", options.EnvironmentUrl.ToString());
        Assert.Equal(OtherClientId, options.ClientId);
    }

    [Fact]
    public void A_profile_inherits_what_it_does_not_override()
    {
        // The case this feature exists for: several environments behind one app
        // registration, where only the URL differs.
        Set(DataverseOptions.UrlVariable, "https://default.crm4.dynamics.com");
        Set(DataverseOptions.ClientIdVariable, ValidClientId);
        Set(DataverseOptions.ClientSecretVariable, "shared-secret");
        Set(DataverseOptions.TenantIdVariable, ValidTenantId);

        Set("DATAVERSE_TEST_URL", "https://test.crm4.dynamics.com");

        Assert.True(DataverseOptions.TryLoadFromEnvironment("test", out var options, out _));

        Assert.Equal("https://test.crm4.dynamics.com/", options.EnvironmentUrl.ToString());
        Assert.Equal(ValidClientId, options.ClientId);
        Assert.Equal(ValidTenantId, options.TenantId);
        Assert.Equal("shared-secret", Assert.IsType<ClientSecretCredential>(options.Credential).Secret);
    }

    [Fact]
    public void Profile_names_are_case_insensitive()
    {
        Set("DATAVERSE_PROD_URL", "https://prod.crm4.dynamics.com");
        Set("DATAVERSE_PROD_CLIENT_ID", ValidClientId);
        Set("DATAVERSE_PROD_CLIENT_SECRET", "secret");

        Assert.True(DataverseOptions.TryLoadFromEnvironment("PrOd", out var options, out _));
        Assert.Equal("https://prod.crm4.dynamics.com/", options.EnvironmentUrl.ToString());
    }

    [Fact]
    public void A_hyphen_in_a_profile_name_becomes_an_underscore()
    {
        Set("DATAVERSE_WEST_EU_URL", "https://west.crm4.dynamics.com");
        Set("DATAVERSE_WEST_EU_CLIENT_ID", ValidClientId);
        Set("DATAVERSE_WEST_EU_CLIENT_SECRET", "secret");

        Assert.True(DataverseOptions.TryLoadFromEnvironment("west-eu", out var options, out _));
        Assert.Equal("https://west.crm4.dynamics.com/", options.EnvironmentUrl.ToString());
    }

    [Fact]
    public void The_profile_variable_selects_a_profile_when_none_is_named()
    {
        Set(DataverseOptions.ProfileVariable, "prod");
        Set("DATAVERSE_PROD_URL", "https://prod.crm4.dynamics.com");
        Set("DATAVERSE_PROD_CLIENT_ID", ValidClientId);
        Set("DATAVERSE_PROD_CLIENT_SECRET", "secret");

        Assert.True(DataverseOptions.TryLoadFromEnvironment(out var options, out _));
        Assert.Equal("prod", options.Profile);
    }

    [Fact]
    public void An_explicit_profile_beats_the_profile_variable()
    {
        Set(DataverseOptions.ProfileVariable, "prod");
        Set("DATAVERSE_PROD_URL", "https://prod.crm4.dynamics.com");
        Set("DATAVERSE_TEST_URL", "https://test.crm4.dynamics.com");
        Set(DataverseOptions.ClientIdVariable, ValidClientId);
        Set(DataverseOptions.ClientSecretVariable, "secret");

        Assert.True(DataverseOptions.TryLoadFromEnvironment("test", out var options, out _));
        Assert.Equal("https://test.crm4.dynamics.com/", options.EnvironmentUrl.ToString());
    }

    [Fact]
    public void A_missing_variable_is_reported_under_its_profile_name()
    {
        // Naming the unprefixed variable here would send the reader to set a
        // variable that would not be read for the profile they asked for.
        Set("DATAVERSE_PROD_CLIENT_ID", ValidClientId);
        Set("DATAVERSE_PROD_CLIENT_SECRET", "secret");

        Assert.False(DataverseOptions.TryLoadFromEnvironment("prod", out _, out var error));
        Assert.Contains("DATAVERSE_PROD_URL", error);
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("dots.are.out")]
    [InlineData("slash/es")]
    public void A_profile_name_that_is_not_a_variable_name_is_refused(string profile)
    {
        Assert.False(DataverseOptions.TryLoadFromEnvironment(profile, out _, out var error));
        Assert.Contains(profile, error);
    }

    [Fact]
    public void A_profile_name_starting_with_a_digit_is_refused()
    {
        Assert.False(DataverseOptions.TryLoadFromEnvironment("2nd", out _, out var error));
        Assert.Contains("2ND", error);
    }

    [Fact]
    public void A_profile_with_no_variables_of_its_own_is_refused()
    {
        // The whole point: a typo must not quietly hand back the default
        // environment, because the command then succeeds against the wrong
        // tenant, which is worse than any error.
        Set(DataverseOptions.UrlVariable, "https://default.crm4.dynamics.com");
        Set(DataverseOptions.ClientIdVariable, ValidClientId);
        Set(DataverseOptions.ClientSecretVariable, "secret");

        Assert.False(DataverseOptions.TryLoadFromEnvironment("prodd", out _, out var error));
        Assert.Contains("prodd", error);
    }

    [Fact]
    public void A_mistyped_profile_is_answered_with_the_profiles_that_exist()
    {
        Set("DATAVERSE_PROD_URL", "https://prod.crm4.dynamics.com");
        Set("DATAVERSE_TEST_URL", "https://test.crm4.dynamics.com");

        Assert.False(DataverseOptions.TryLoadFromEnvironment("prodd", out _, out var error));

        Assert.Contains("PROD", error);
        Assert.Contains("TEST", error);
    }

    [Fact]
    public void With_no_profiles_at_all_the_advice_is_to_omit_the_profile()
    {
        Set(DataverseOptions.UrlVariable, "https://default.crm4.dynamics.com");
        Set(DataverseOptions.ClientIdVariable, ValidClientId);
        Set(DataverseOptions.ClientSecretVariable, "secret");

        Assert.False(DataverseOptions.TryLoadFromEnvironment("prod", out _, out var error));
        Assert.Contains("omit the profile", error);
    }

    [Fact]
    public void One_overridden_variable_is_enough_to_make_a_profile_real()
    {
        // Overriding only the certificate password is odd but legitimate, and
        // the existence rule must not be narrower than the inheritance rule.
        Set(DataverseOptions.UrlVariable, "https://default.crm4.dynamics.com");
        Set(DataverseOptions.ClientIdVariable, ValidClientId);
        Set(DataverseOptions.ClientSecretVariable, "secret");
        Set("DATAVERSE_TEST_TENANT_ID", ValidTenantId);

        Assert.True(DataverseOptions.TryLoadFromEnvironment("test", out var options, out _));
        Assert.Equal(ValidTenantId, options.TenantId);
    }

    [Fact]
    public void A_default_variable_is_not_mistaken_for_a_profile()
    {
        // DATAVERSE_CLIENT_SECRET must not be read as profile 'CLIENT', nor
        // DATAVERSE_CERT_PATH as profile 'CERT'.
        Set(DataverseOptions.UrlVariable, "https://default.crm4.dynamics.com");
        Set(DataverseOptions.ClientIdVariable, ValidClientId);
        Set(DataverseOptions.ClientSecretVariable, "secret");
        Set(DataverseOptions.CertificatePathVariable, "/some/app.pfx");
        Set(DataverseOptions.TenantIdVariable, ValidTenantId);

        Assert.Empty(DataverseOptions.KnownProfiles());
    }

    [Fact]
    public void Profiles_are_discovered_from_any_of_their_variables()
    {
        Set("DATAVERSE_PROD_URL", "https://prod.crm4.dynamics.com");
        Set("DATAVERSE_WEST_EU_CLIENT_SECRET", "secret");
        Set("DATAVERSE_ACC_CERT_THUMBPRINT", new string('A', 40));

        Assert.Equal(["ACC", "PROD", "WEST_EU"], DataverseOptions.KnownProfiles());
    }

    [Fact]
    public void Variable_names_are_reported_for_the_profile_in_use()
    {
        Assert.Equal("DATAVERSE_URL", DataverseOptions.VariableName(DataverseOptions.UrlVariable, null));
        Assert.Equal("DATAVERSE_PROD_URL", DataverseOptions.VariableName(DataverseOptions.UrlVariable, "prod"));
        Assert.Equal(
            "DATAVERSE_PROD_CLIENT_SECRET",
            DataverseOptions.VariableName(DataverseOptions.ClientSecretVariable, "prod"));
    }

    [Fact]
    public void The_description_names_the_profile_and_hides_the_secret()
    {
        Set("DATAVERSE_PROD_URL", "https://prod.crm4.dynamics.com");
        Set("DATAVERSE_PROD_CLIENT_ID", ValidClientId);
        Set("DATAVERSE_PROD_CLIENT_SECRET", "hunter2-is-the-secret");

        Assert.True(DataverseOptions.TryLoadFromEnvironment("prod", out var options, out _));

        var described = options.Describe();
        Assert.Contains("profile=prod", described);
        Assert.DoesNotContain("hunter2-is-the-secret", described);
    }
}

[Collection("environment variables")]
public class CredentialTests : IDisposable
{
    private const string ValidClientId = "11111111-2222-3333-4444-555555555555";
    private const string Url = "https://contoso.crm4.dynamics.com";

    private readonly List<string> _files = [];

    public void Dispose()
    {
        foreach (var file in _files.Where(File.Exists))
            File.Delete(file);

        GC.SuppressFinalize(this);
    }

    private static bool TryCreate(
        string? secret,
        string? certificatePath,
        string? certificatePassword,
        string? thumbprint,
        out DataverseOptions? options,
        out string? error) =>
        DataverseOptions.TryCreate(
            Url, ValidClientId, secret, null,
            certificatePath, certificatePassword, thumbprint, null, null, null,
            out options, out error);

    /// <summary>Self-signed, generated here so the test needs nothing external.</summary>
    private string WritePfx(string? password, bool withPrivateKey = true)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=dataverse-duck-test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        var path = Path.Combine(Path.GetTempPath(), $"dvduck-{Guid.NewGuid():N}.pfx");
        _files.Add(path);

        if (withPrivateKey)
        {
            File.WriteAllBytes(path, certificate.Export(X509ContentType.Pkcs12, password));
        }
        else
        {
            // What you upload to the app registration, which people do reach for
            // by mistake. Still a PKCS#12 container, just without the private half.
            using var publicOnly = X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Cert));
            File.WriteAllBytes(path, publicOnly.Export(X509ContentType.Pkcs12, password));
        }

        return path;
    }

    [Fact]
    public void A_client_secret_produces_a_secret_credential()
    {
        Assert.True(TryCreate("secret", null, null, null, out var options, out _));
        Assert.IsType<ClientSecretCredential>(options!.Credential);
    }

    [Fact]
    public void No_credential_at_all_is_refused_and_all_three_are_offered()
    {
        Assert.False(TryCreate(null, null, null, null, out _, out var error));

        Assert.Contains(DataverseOptions.ClientSecretVariable, error);
        Assert.Contains(DataverseOptions.CertificatePathVariable, error);
        Assert.Contains(DataverseOptions.CertificateThumbprintVariable, error);
    }

    [Fact]
    public void Two_credentials_are_refused_rather_than_ranked()
    {
        // A leftover secret quietly beating a certificate someone had just
        // switched to would be almost invisible.
        var path = WritePfx(null);

        Assert.False(TryCreate("secret", path, null, null, out _, out var error));

        Assert.Contains(DataverseOptions.ClientSecretVariable, error);
        Assert.Contains(DataverseOptions.CertificatePathVariable, error);
    }

    [Fact]
    public void A_certificate_file_produces_a_certificate_credential()
    {
        var path = WritePfx(null);

        Assert.True(TryCreate(null, path, null, null, out var options, out var error));

        Assert.Null(error);
        var credential = Assert.IsType<CertificateCredential>(options!.Credential);
        Assert.True(credential.Certificate.HasPrivateKey);
        Assert.Equal("CN=dataverse-duck-test", credential.Certificate.Subject);
    }

    [Fact]
    public void A_password_protected_certificate_file_is_read_with_its_password()
    {
        var path = WritePfx("correct horse");

        Assert.True(TryCreate(null, path, "correct horse", null, out var options, out var error));

        Assert.Null(error);
        Assert.IsType<CertificateCredential>(options!.Credential);
    }

    [Fact]
    public void A_wrong_certificate_password_says_to_check_the_password()
    {
        var path = WritePfx("correct horse");

        Assert.False(TryCreate(null, path, "wrong", null, out _, out var error));

        // The platform message for this is 'the specified network password is
        // not correct', which sends people looking in entirely the wrong place.
        Assert.Contains("password", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_certificate_without_a_private_key_is_refused()
    {
        var path = WritePfx(null, withPrivateKey: false);

        Assert.False(TryCreate(null, path, null, null, out _, out var error));

        Assert.Contains("private key", error);
        Assert.Contains(".cer", error);
    }

    [Fact]
    public void A_missing_certificate_file_names_the_path()
    {
        var path = Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.pfx");

        Assert.False(TryCreate(null, path, null, null, out _, out var error));
        Assert.Contains(path, error);
    }

    [Fact]
    public void An_unknown_thumbprint_explains_the_store_on_linux()
    {
        Assert.False(TryCreate(null, null, null, new string('A', 40), out _, out var error));

        Assert.Contains("CurrentUser", error);
        Assert.Contains(new string('A', 40), error);
    }

    [Fact]
    public void A_thumbprint_is_normalised_before_it_is_looked_up()
    {
        // Copying out of a certificate dialog brings spaces; out of Entra,
        // lowercase. Neither matches what the store holds.
        Assert.False(TryCreate(null, null, null, "ab cd ef", out _, out var error));
        Assert.Contains("ABCDEF", error);
    }

    [Fact]
    public void A_certificate_is_described_without_revealing_the_key()
    {
        var path = WritePfx(null);

        Assert.True(TryCreate(null, path, null, null, out var options, out _));

        var described = options!.Describe();
        Assert.Contains("certificate=", described);
        Assert.Contains("CN=dataverse-duck-test", described);
        Assert.DoesNotContain("PRIVATE KEY", described);
    }
}
