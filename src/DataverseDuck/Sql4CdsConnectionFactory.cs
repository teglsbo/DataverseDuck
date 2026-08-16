using MarkMpn.Sql4Cds.Engine;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;

namespace DataverseDuck;

/// <summary>
/// Builds SQL 4 CDS connections with the settings this project requires.
/// </summary>
public static class Sql4CdsConnectionFactory
{
    /// <summary>
    /// Reported to Dataverse as the calling application, and used as the
    /// SQL 4 CDS telemetry application name.
    ///
    /// SQL 4 CDS ships anonymous Application Insights telemetry to a hardcoded
    /// instrumentation key. Query text is not sent on success, but it *is* sent
    /// on exception. Setting a deliberate name keeps that attribution clear
    /// rather than leaking a default or an assembly name.
    /// </summary>
    public const string DefaultApplicationName = "dvduck-cli";

    /// <summary>
    /// Creates a connection from an existing organization service.
    /// </summary>
    /// <param name="service">Authenticated service, typically a ServiceClient.</param>
    /// <param name="applicationName">Overrides <see cref="DefaultApplicationName"/>.</param>
    /// <param name="useTdsEndpoint">
    /// Left off by default. The Dataverse TDS endpoint does not support
    /// service principal authentication, and it needs port 1433/5558 open,
    /// so headless runs should go through the SDK path instead.
    /// </param>
    public static Sql4CdsConnection Create(
        IOrganizationService service,
        string? applicationName = null,
        bool useTdsEndpoint = false)
    {
        ArgumentNullException.ThrowIfNull(service);

        var connection = new Sql4CdsConnection(new[] { service });
        Configure(connection, applicationName, useTdsEndpoint);
        return connection;
    }

    /// <summary>
    /// Creates a connection using service principal (app registration) auth.
    /// No interactive login, no user identity.
    /// </summary>
    public static Sql4CdsConnection CreateWithClientSecret(
        Uri environmentUrl,
        string clientId,
        string clientSecret,
        string? applicationName = null,
        bool useTdsEndpoint = false)
    {
        ArgumentNullException.ThrowIfNull(environmentUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientSecret);

        var client = new ServiceClient(environmentUrl, clientId, clientSecret, useUniqueInstance: true);

        if (!client.IsReady)
            throw new InvalidOperationException(
                $"Dataverse connection to '{environmentUrl}' is not ready: {client.LastError}",
                client.LastException);

        return Create(client, applicationName, useTdsEndpoint);
    }

    private static void Configure(Sql4CdsConnection connection, string? applicationName, bool useTdsEndpoint)
    {
        connection.ApplicationName = string.IsNullOrWhiteSpace(applicationName)
            ? DefaultApplicationName
            : applicationName;

        // Measured default is true, so this must be set explicitly rather than
        // left alone: the TDS endpoint cannot authenticate a service principal.
        connection.UseTDSEndpoint = useTdsEndpoint;

        // Measured default is already false, but it is set explicitly because a
        // change here would silently shift every datetime out of UTC and break
        // the guarantee in ADR 0002. A wrong timestamp does not throw.
        connection.UseLocalTimeZone = false;
    }
}
