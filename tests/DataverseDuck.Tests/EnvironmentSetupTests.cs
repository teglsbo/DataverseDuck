using System.Text;
using System.Text.Json;
using DataverseDuck.Configuration;
using DataverseDuck.Diagnostics;

namespace DataverseDuck.Tests;

public class DataverseOptionsTests
{
    private const string ValidClientId = "11111111-2222-3333-4444-555555555555";
    private const string ValidTenantId = "99999999-8888-7777-6666-555555555555";

    [Fact]
    public void Valid_settings_are_accepted()
    {
        Assert.True(DataverseOptions.TryCreate(
            "https://contoso.crm4.dynamics.com", ValidClientId, "secret", ValidTenantId,
            out var options, out var error));

        Assert.Null(error);
        Assert.Equal("https://contoso.crm4.dynamics.com/", options.EnvironmentUrl.ToString());
        Assert.Equal(ValidTenantId, options.TenantId);
    }

    [Fact]
    public void Scope_is_derived_from_the_environment_not_a_generic_audience()
    {
        // A Dataverse token is audience-bound to the org. Requesting a generic
        // audience authenticates but is rejected by Dataverse.
        Assert.True(DataverseOptions.TryCreate(
            "https://contoso.crm4.dynamics.com", ValidClientId, "secret", null,
            out var options, out _));

        Assert.Equal("https://contoso.crm4.dynamics.com/.default", options.Scope);
    }

    [Fact]
    public void Trailing_path_is_stripped_so_the_audience_stays_valid()
    {
        Assert.True(DataverseOptions.TryCreate(
            "https://contoso.crm4.dynamics.com/main.aspx?pagetype=entitylist",
            ValidClientId, "secret", null, out var options, out _));

        Assert.Equal("https://contoso.crm4.dynamics.com/", options.EnvironmentUrl.ToString());
        Assert.Equal("https://contoso.crm4.dynamics.com/.default", options.Scope);
    }

    [Fact]
    public void Missing_tenant_falls_back_to_the_organizations_authority()
    {
        Assert.True(DataverseOptions.TryCreate(
            "https://contoso.crm4.dynamics.com", ValidClientId, "secret", null, out var options, out _));

        Assert.Equal("https://login.microsoftonline.com/organizations", options.Authority);
    }

    [Fact]
    public void Tenant_is_used_in_the_authority_when_supplied()
    {
        Assert.True(DataverseOptions.TryCreate(
            "https://contoso.crm4.dynamics.com", ValidClientId, "secret", ValidTenantId, out var options, out _));

        Assert.Equal($"https://login.microsoftonline.com/{ValidTenantId}", options.Authority);
    }

    [Theory]
    [InlineData(null, ValidClientId, "secret", "DATAVERSE_URL")]
    [InlineData("https://contoso.crm4.dynamics.com", null, "secret", "DATAVERSE_CLIENT_ID")]
    [InlineData("https://contoso.crm4.dynamics.com", ValidClientId, null, "DATAVERSE_CLIENT_SECRET")]
    public void Missing_values_are_named_in_the_error(string? url, string? clientId, string? secret, string expected)
    {
        Assert.False(DataverseOptions.TryCreate(url, clientId, secret, null, out var options, out var error));

        Assert.Null(options);
        Assert.Contains(expected, error);
    }

    [Fact]
    public void Http_urls_are_rejected()
    {
        Assert.False(DataverseOptions.TryCreate(
            "http://contoso.crm4.dynamics.com", ValidClientId, "secret", null, out _, out var error));

        Assert.Contains("https", error);
    }

    [Fact]
    public void A_client_id_that_is_not_a_guid_is_rejected_with_a_specific_message()
    {
        // Easy mistake: pasting the app *name* or the object ID.
        Assert.False(DataverseOptions.TryCreate(
            "https://contoso.crm4.dynamics.com", "my-app", "secret", null, out _, out var error));

        Assert.Contains("GUID", error);
    }

    [Fact]
    public void A_tenant_id_that_is_not_a_guid_is_rejected()
    {
        Assert.False(DataverseOptions.TryCreate(
            "https://contoso.crm4.dynamics.com", ValidClientId, "secret", "contoso.onmicrosoft.com",
            out _, out var error));

        Assert.Contains(DataverseOptions.TenantIdVariable, error);
    }

    [Fact]
    public void Connection_string_carries_the_secret_and_requests_a_fresh_instance()
    {
        Assert.True(DataverseOptions.TryCreate(
            "https://contoso.crm4.dynamics.com", ValidClientId, "s3cret", null, out var options, out _));

        var credential = Assert.IsType<ClientSecretCredential>(options.Credential);
        var connectionString = credential.ToConnectionString(options);
        Assert.Contains("AuthType=ClientSecret", connectionString);
        Assert.Contains("s3cret", connectionString);
        Assert.Contains("RequireNewInstance=true", connectionString);
    }
}

public class AccessTokenClaimsTests
{
    private static string BuildToken(object payload)
    {
        static string Segment(string json) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        return $"{Segment("""{"alg":"RS256","typ":"JWT"}""")}." +
               $"{Segment(JsonSerializer.Serialize(payload))}.signature";
    }

    [Fact]
    public void Decodes_the_claims_that_matter_for_diagnosis()
    {
        var token = BuildToken(new
        {
            aud = "https://contoso.crm4.dynamics.com",
            tid = "99999999-8888-7777-6666-555555555555",
            appid = "11111111-2222-3333-4444-555555555555",
            oid = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
            roles = new[] { "System.Administrator" },
            exp = 1_800_000_000L,
        });

        var claims = AccessTokenClaims.TryDecode(token);

        Assert.NotNull(claims);
        Assert.Equal("https://contoso.crm4.dynamics.com", claims.Audience);
        Assert.Equal("11111111-2222-3333-4444-555555555555", claims.ApplicationId);
        Assert.Equal(["System.Administrator"], claims.Roles);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_800_000_000L), claims.ExpiresOn);
        Assert.True(claims.IsApplicationOnly);
    }

    [Fact]
    public void Falls_back_to_azp_when_appid_is_absent()
    {
        var claims = AccessTokenClaims.TryDecode(BuildToken(new { azp = "the-app" }));

        Assert.Equal("the-app", claims!.ApplicationId);
    }

    [Fact]
    public void A_token_carrying_a_user_is_not_application_only()
    {
        var claims = AccessTokenClaims.TryDecode(BuildToken(new { upn = "someone@contoso.com" }));

        Assert.False(claims!.IsApplicationOnly);
    }

    [Fact]
    public void Audience_matching_ignores_a_trailing_slash()
    {
        // Entra ID is inconsistent about the trailing slash on the aud claim,
        // so matching on it directly produces false alarms.
        var claims = AccessTokenClaims.TryDecode(
            BuildToken(new { aud = "https://contoso.crm4.dynamics.com/" }));

        Assert.True(claims!.MatchesEnvironment(new Uri("https://contoso.crm4.dynamics.com")));
    }

    [Fact]
    public void A_generic_audience_is_detected_as_a_mismatch()
    {
        // The classic wrong-audience mistake: this token is valid but Dataverse rejects it.
        var claims = AccessTokenClaims.TryDecode(
            BuildToken(new { aud = "https://database.windows.net/" }));

        Assert.False(claims!.MatchesEnvironment(new Uri("https://contoso.crm4.dynamics.com")));
    }

    [Fact]
    public void A_token_for_a_different_environment_is_a_mismatch()
    {
        var claims = AccessTokenClaims.TryDecode(
            BuildToken(new { aud = "https://fabrikam.crm4.dynamics.com" }));

        Assert.False(claims!.MatchesEnvironment(new Uri("https://contoso.crm4.dynamics.com")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("only.two")]
    public void Malformed_tokens_return_null_rather_than_throwing(string? token)
    {
        // A malformed token is itself a diagnosis; the doctor should keep going.
        Assert.Null(AccessTokenClaims.TryDecode(token));
    }

    [Fact]
    public void Payloads_needing_base64url_padding_still_decode()
    {
        // JWT segments drop '=' padding, and the required amount varies by length.
        foreach (var name in new[] { "a", "ab", "abc", "abcd", "abcde" })
        {
            var claims = AccessTokenClaims.TryDecode(BuildToken(new { aud = name }));
            Assert.Equal(name, claims?.Audience);
        }
    }

    [Fact]
    public void Secrets_are_masked_without_hiding_which_secret_it_is()
    {
        var masked = AccessTokenClaims.Mask("super-secret-value");

        Assert.DoesNotContain("secret-val", masked);
        Assert.StartsWith("supe", masked);
        Assert.EndsWith("ue", masked);
    }

    [Fact]
    public void Short_secrets_are_masked_entirely()
    {
        Assert.Equal("*****", AccessTokenClaims.Mask("short"));
        Assert.Equal("(not set)", AccessTokenClaims.Mask(null));
    }
}
