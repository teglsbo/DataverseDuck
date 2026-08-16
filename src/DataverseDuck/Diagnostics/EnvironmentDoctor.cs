using DataverseDuck.Configuration;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Identity.Client;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;

namespace DataverseDuck.Diagnostics;

/// <summary>
/// Verifies that a Dataverse environment is set up correctly for headless,
/// app-registration access.
///
/// The reason this exists rather than a checklist: the setup steps live in
/// three different portals, and when one is missed the resulting error is
/// almost always a bare authentication failure. In particular, an app
/// registration with a perfectly valid secret still cannot talk to Dataverse
/// until an *application user* has been created for it inside the environment
/// and given a security role. Those are separate steps, in a separate portal,
/// and both fail the same way.
/// </summary>
public sealed class EnvironmentDoctor(DataverseOptions options)
{
    private readonly DataverseOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>Tables to probe for existence. Empty means skip the check.</summary>
    public IReadOnlyList<string> ExpectedTables { get; init; } = [];

    public async Task<DoctorReport> RunAsync(CancellationToken cancellationToken = default)
    {
        var checks = new List<CheckResult>
        {
            CheckResult.Pass("Configuration", _options.Describe()),
        };

        checks.Add(await CheckEnvironmentTenantAsync(cancellationToken));

        var (tokenCheck, accessToken) = await AcquireTokenAsync(cancellationToken);
        checks.Add(tokenCheck);

        if (accessToken is null)
            return new DoctorReport(checks);

        checks.Add(InspectToken(accessToken));

        var (whoAmICheck, service) = ConnectAndIdentify();
        checks.Add(whoAmICheck);

        if (service is null)
            return new DoctorReport(checks);

        using (service)
        {
            checks.Add(CheckPrivileges(service));

            if (ExpectedTables.Count > 0)
                checks.Add(CheckTables(service));

            checks.Add(CheckQueryEngine(service));
        }

        return new DoctorReport(checks);
    }

    /// <summary>
    /// Asks the environment which tenant it authenticates against and compares
    /// it with the configured one.
    ///
    /// Needs no credentials, so it works when everything else fails -- which is
    /// exactly when it is useful. A mismatch here explains an "application not
    /// found in the directory" error that would otherwise look like a wrong
    /// client ID.
    /// </summary>
    private async Task<CheckResult> CheckEnvironmentTenantAsync(CancellationToken cancellationToken)
    {
        const string name = "Environment tenant";

        var actual = await EnvironmentTenant.DiscoverAsync(
            _options.EnvironmentUrl.ToString(), cancellationToken: cancellationToken);

        if (actual is null)
        {
            return CheckResult.Skip(name, "The environment did not return a readable authentication challenge.");
        }

        if (_options.TenantId is null)
        {
            return CheckResult.Pass(name, $"The environment belongs to tenant {actual}.");
        }

        if (string.Equals(actual, _options.TenantId, StringComparison.OrdinalIgnoreCase))
        {
            return CheckResult.Pass(name, $"The environment belongs to tenant {actual}, which matches.");
        }

        return CheckResult.Fail(
            name,
            $"The environment belongs to tenant {actual}, but {DataverseOptions.TenantIdVariable} " +
            $"is {_options.TenantId}.",
            $"The app registration must live in the same tenant as the environment. Either set " +
            $"{DataverseOptions.TenantIdVariable} to {actual} and register the application there, " +
            "or use an environment in the tenant you configured.");
    }

    private async Task<(CheckResult, string?)> AcquireTokenAsync(CancellationToken cancellationToken)
    {
        const string name = "Token acquisition";
        try
        {
            var app = _options.CreateConfidentialClient();

            var result = await app
                .AcquireTokenForClient([_options.Scope])
                .ExecuteAsync(cancellationToken);

            return (CheckResult.Pass(name, $"Acquired a token for {_options.Scope}, expires {result.ExpiresOn:u}."),
                result.AccessToken);
        }
        catch (MsalServiceException e)
        {
            return (CheckResult.Fail(name, $"{e.ErrorCode}: {FirstLine(e.Message)}", InterpretMsalError(e)), null);
        }
        catch (Exception e)
        {
            return (CheckResult.Fail(name, FirstLine(e.Message),
                "Check network access to login.microsoftonline.com."), null);
        }
    }

    private string InterpretMsalError(MsalServiceException e)
    {
        // These codes are the ones that actually show up during first-time setup.
        if (e.Message.Contains("AADSTS7000215"))
            return $"The client secret is wrong or expired. Generate a new one under " +
                   $"App registrations > your app > Certificates & secrets, and reset {DataverseOptions.ClientSecretVariable}. " +
                   "Note the portal shows the secret *Value* only once; the 'Secret ID' is not the secret.";

        if (e.Message.Contains("AADSTS90002"))
            return $"The tenant '{_options.TenantId ?? "(organizations)"}' does not exist. Copy the directory " +
                   $"(tenant) ID from the app registration Overview page in Entra ID, and set " +
                   $"{DataverseOptions.TenantIdVariable}. It is a different GUID from the application ID, " +
                   $"and from the environment and organization IDs shown in the Power Platform admin centre.";

        if (e.Message.Contains("AADSTS700016"))
            return $"The tenant exists, but application '{_options.ClientId}' was not found in it. " +
                   $"Two different mistakes produce this. Most often {DataverseOptions.ClientIdVariable} " +
                   "holds the *Object ID* rather than the *Application (client) ID* -- both are on the " +
                   "app registration Overview page, one above the other. Otherwise the registration is " +
                   "in a different tenant from the environment; the 'Environment tenant' check above " +
                   "says which tenant the environment expects.";

        if (e.Message.Contains("AADSTS500011") || e.Message.Contains("AADSTS650057"))
            return $"The resource principal for '{_options.Scope}' was not found in the tenant. " +
                   "Usually means the environment URL is wrong, or belongs to a different tenant. " +
                   "Copy it from Power Platform Admin Center > Environments > your environment > Environment URL.";

        return "Check the app registration ID, secret and tenant.";
    }

    private CheckResult InspectToken(string accessToken)
    {
        const string name = "Token claims";
        var claims = AccessTokenClaims.TryDecode(accessToken);

        if (claims is null)
            return CheckResult.Warn(name, "Could not decode the token payload.",
                "Not fatal by itself; continue and see whether the connection works.");

        var detail = $"aud={claims.Audience} appid={claims.ApplicationId} tid={claims.TenantId} " +
                     $"roles=[{string.Join(", ", claims.Roles)}]";

        if (!claims.MatchesEnvironment(_options.EnvironmentUrl))
            return CheckResult.Fail(name,
                $"Audience '{claims.Audience}' does not match environment '{_options.EnvironmentUrl.Host}'. {detail}",
                "The token is valid but Dataverse will reject it. A Dataverse token must be requested " +
                $"for the environment's own scope ('{_options.Scope}'), not a generic one such as " +
                "'https://database.windows.net/.default'.");

        if (!claims.IsApplicationOnly)
            return CheckResult.Warn(name, $"Token carries a user identity. {detail}",
                "Expected an app-only token from the client credentials flow. Harmless here, but it means " +
                "the run is not actually headless.");

        return CheckResult.Pass(name, detail);
    }

    private (CheckResult, ServiceClient?) ConnectAndIdentify()
    {
        const string name = "WhoAmI (application user)";
        try
        {
            var client = _options.CreateServiceClient();

            if (!client.IsReady)
            {
                var reason = client.LastError ?? client.LastException?.Message ?? "unknown";
                client.Dispose();
                return (CheckResult.Fail(name, FirstLine(reason), InterpretConnectionError(reason)), null);
            }

            var who = (WhoAmIResponse)client.Execute(new WhoAmIRequest());
            return (CheckResult.Pass(name,
                $"userId={who.UserId} businessUnitId={who.BusinessUnitId} organizationId={who.OrganizationId}"),
                client);
        }
        catch (Exception e)
        {
            return (CheckResult.Fail(name, FirstLine(e.Message), InterpretConnectionError(e.Message)), null);
        }
    }

    private string InterpretConnectionError(string message)
    {
        // The single most common first-run failure, and the least self-explanatory.
        if (message.Contains("0x80072560") ||
            message.Contains("not a member of the organization", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("does not exist in the organization", StringComparison.OrdinalIgnoreCase))
            return "The app registration authenticated, but has no *application user* in this environment. " +
                   "Create one: Power Platform Admin Center > Environments > your environment > Settings > " +
                   "Users + permissions > Application users > New app user, pick the app registration " +
                   $"'{_options.ClientId}', choose a business unit, and assign a security role. " +
                   "This is a separate step from creating the app registration in Entra ID.";

        if (message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase) || message.Contains("401"))
            return "Authentication reached Dataverse but was rejected. Usually a missing application user, " +
                   "or the environment URL points at a different environment than the one where the " +
                   "application user was created.";

        if (message.Contains("Forbidden", StringComparison.OrdinalIgnoreCase) || message.Contains("403"))
        {
            // Dataverse returns 403 both for "no application user" and for "user
            // with no role", and the two are fixed on different screens. The
            // 'Anonymous' scheme is the tell: Dataverse could not map the token
            // to any user at all, so there is nobody to assign a role to.
            var anonymous = message.Contains("Anonymous", StringComparison.OrdinalIgnoreCase);

            return anonymous
                ? "Dataverse could not map the token to any user -- the *application user* has not been " +
                  "created yet. Power Platform Admin Center > Environments > your environment > Settings > " +
                  "Users + permissions > Application users > + New app user, add the app registration " +
                  $"'{_options.ClientId}', pick the root business unit, and assign a security role " +
                  "(System Customizer is usually enough). This is a separate step from the Entra registration."
                : "The application user exists but is missing a security role, or its role lacks the " +
                  "privileges needed. Assign one (System Customizer is usually enough to read metadata " +
                  "and data) in Admin Center > Application users > Edit security roles.";
        }

        return "Verify the environment URL, and that an application user exists for this app registration.";
    }

    private static CheckResult CheckPrivileges(IOrganizationService service)
    {
        const string name = "Read privileges";
        try
        {
            // The organization row is readable by any role, and SQL 4 CDS reads it
            // at connection time for collation, so failing here fails everything later.
            var result = service.RetrieveMultiple(new QueryExpression("organization")
            {
                ColumnSet = new ColumnSet("name", "localeid"),
                TopCount = 1,
            });

            if (result.Entities.Count == 0)
                return CheckResult.Warn(name, "The organization table returned no rows.",
                    "Unexpected. SQL 4 CDS reads this row to determine collation, so queries may fail.");

            return CheckResult.Pass(name, $"Read organization '{result.Entities[0].GetAttributeValue<string>("name")}'.");
        }
        catch (Exception e)
        {
            return CheckResult.Fail(name, FirstLine(e.Message),
                "The application user has no security role, or one without read privileges. Assign " +
                "System Customizer (or a custom role with read on the tables you need) in " +
                "Admin Center > Application users > Edit security roles.");
        }
    }

    private CheckResult CheckTables(IOrganizationService service)
    {
        const string name = "Expected tables";
        var found = new List<string>();
        var missing = new List<string>();

        foreach (var table in ExpectedTables)
        {
            try
            {
                service.Execute(new RetrieveEntityRequest
                {
                    LogicalName = table,
                    EntityFilters = Microsoft.Xrm.Sdk.Metadata.EntityFilters.Entity,
                    RetrieveAsIfPublished = true,
                });
                found.Add(table);
            }
            catch
            {
                missing.Add(table);
            }
        }

        if (missing.Count == 0)
            return CheckResult.Pass(name, $"All present: {string.Join(", ", found)}.");

        return CheckResult.Warn(name,
            $"Missing: {string.Join(", ", missing)}. Present: {(found.Count == 0 ? "(none)" : string.Join(", ", found))}.",
            "If these are Dynamics 365 app tables (opportunity, lead, incident, campaign), a Power Apps " +
            "Developer Plan environment does not include them. Use a Dynamics 365 trial at " +
            "trials.dynamics.com instead, or install the relevant app into the environment.");
    }

    private static CheckResult CheckQueryEngine(IOrganizationService service)
    {
        const string name = "SQL 4 CDS engine";
        try
        {
            using var connection = Sql4CdsConnectionFactory.Create(service);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT TOP 1 name FROM account";

            using var reader = command.ExecuteReader();
            var rows = 0;
            while (reader.Read()) rows++;

            return CheckResult.Pass(name, $"Compiled and executed a query against account ({rows} row(s)).");
        }
        catch (Exception e)
        {
            return CheckResult.Fail(name, FirstLine(e.Message),
                "Authentication works but query compilation or execution failed. If the message mentions " +
                "an unknown table, the account table may not exist in this environment.");
        }
    }

    private static string FirstLine(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "(no message)";
        var line = value.Split('\n', '\r').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim() ?? value;
        return line.Length > 300 ? line[..300] + "..." : line;
    }
}
