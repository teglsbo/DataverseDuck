using System.Text.Json;

namespace DataverseDuck.Diagnostics;

/// <summary>
/// The handful of claims worth looking at when diagnosing a Dataverse
/// service principal connection.
///
/// This decodes without validating the signature. That is deliberate and safe
/// here: the token is one we just acquired for ourselves, and the goal is to
/// explain a failure, not to make a trust decision. Never use this to
/// authorise anything.
/// </summary>
public sealed record AccessTokenClaims
{
    /// <summary>Audience. For Dataverse this must be the environment URL.</summary>
    public string? Audience { get; init; }

    /// <summary>Directory (tenant) ID the token was issued from.</summary>
    public string? TenantId { get; init; }

    /// <summary>Application (client) ID the token was issued to.</summary>
    public string? ApplicationId { get; init; }

    /// <summary>Object ID of the service principal in the directory.</summary>
    public string? ObjectId { get; init; }

    /// <summary>Application roles granted to the app registration.</summary>
    public IReadOnlyList<string> Roles { get; init; } = [];

    public DateTimeOffset? ExpiresOn { get; init; }

    /// <summary>
    /// True when this is an app-only token. A token carrying a user identity
    /// means client credentials were not actually used.
    /// </summary>
    public bool IsApplicationOnly { get; init; }

    /// <summary>
    /// Decodes the payload of a JWT. Returns null for anything unparseable
    /// rather than throwing, since a malformed token is itself a diagnosis.
    /// </summary>
    public static AccessTokenClaims? TryDecode(string? accessToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            return null;

        var parts = accessToken.Split('.');
        if (parts.Length < 2)
            return null;

        JsonElement payload;
        try
        {
            payload = JsonDocument.Parse(Base64UrlDecode(parts[1])).RootElement;
        }
        catch (Exception e) when (e is FormatException or JsonException)
        {
            return null;
        }

        return new AccessTokenClaims
        {
            Audience = GetString(payload, "aud"),
            TenantId = GetString(payload, "tid"),
            ApplicationId = GetString(payload, "appid") ?? GetString(payload, "azp"),
            ObjectId = GetString(payload, "oid"),
            Roles = GetStringArray(payload, "roles"),
            ExpiresOn = payload.TryGetProperty("exp", out var exp) &&
                exp.TryGetInt64(out var seconds) &&
                seconds >= DateTimeOffset.MinValue.ToUnixTimeSeconds() &&
                seconds <= DateTimeOffset.MaxValue.ToUnixTimeSeconds()
                ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                : null,
            // An app-only token has no user principal name and no name claim.
            IsApplicationOnly =
                GetString(payload, "upn") is null &&
                GetString(payload, "unique_name") is null &&
                GetString(payload, "scp") is null,
        };
    }

    /// <summary>
    /// Checks the audience matches the environment. A mismatch authenticates
    /// successfully but is rejected by Dataverse, which is a confusing failure
    /// unless you look at the token.
    /// </summary>
    public bool MatchesEnvironment(Uri environmentUrl)
    {
        if (string.IsNullOrWhiteSpace(Audience))
            return false;

        var expected = environmentUrl.Host;
        return Uri.TryCreate(Audience, UriKind.Absolute, out var audienceUri)
            ? string.Equals(audienceUri.Host, expected, StringComparison.OrdinalIgnoreCase)
            : Audience.Contains(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IReadOnlyList<string> GetStringArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            return [];

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!)
            .ToArray();
    }

    private static byte[] Base64UrlDecode(string input)
    {
        var normalised = input.Replace('-', '+').Replace('_', '/');
        // JWT segments drop the '=' padding that Convert.FromBase64String requires.
        normalised = (normalised.Length % 4) switch
        {
            2 => normalised + "==",
            3 => normalised + "=",
            0 => normalised,
            _ => throw new FormatException("Invalid base64url segment length."),
        };
        return Convert.FromBase64String(normalised);
    }

    /// <summary>Redacts a secret for display, keeping just enough to identify it.</summary>
    public static string Mask(string? value) =>
        string.IsNullOrEmpty(value) ? "(not set)"
        : value.Length <= 8 ? new string('*', value.Length)
        : $"{value[..4]}{new string('*', 8)}{value[^2..]}";
}
