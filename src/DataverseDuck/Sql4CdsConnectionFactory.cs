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
        ConfigureForBulkExport(client);

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

    /// <summary>
    /// Tunes the SDK client for long exports.
    ///
    /// We deliberately add no retry logic of our own. <c>ServiceClient</c> has
    /// paused for the server's <c>Retry-After</c> duration and resent the
    /// request since 2019; wrapping that in a second retry loop would make the
    /// total attempts the product of the two and the total wait far longer than
    /// the server asked for. We raise its limits instead of duplicating it.
    ///
    /// The timeout matters most. It is a <b>static</b> property defaulting to
    /// four minutes, which is per-request rather than per-export, but a single
    /// FetchXML page over a wide or heavily filtered large table can exceed it.
    /// </summary>
    public static void ConfigureForBulkExport(ServiceClient client)
    {
        ArgumentNullException.ThrowIfNull(client);

        // Static, so this affects every client in the process. That is the
        // intent for a CLI whose whole job is exporting.
        if (ServiceClient.MaxConnectionTimeout < BulkExportTimeout)
            ServiceClient.MaxConnectionTimeout = BulkExportTimeout;

        client.MaxRetryCount = BulkExportRetryCount;

        // Only a floor. When the server sends Retry-After the SDK honours that
        // instead, which is the value we actually want to obey.
        client.RetryPauseTime = BulkExportRetryPause;
    }

    /// <summary>Per-request ceiling. The SDK default of four minutes is too tight for wide pages.</summary>
    public static readonly TimeSpan BulkExportTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Retries per request, performed by the SDK rather than by us.</summary>
    public const int BulkExportRetryCount = 10;

    /// <summary>Floor between retries when the server does not specify one.</summary>
    public static readonly TimeSpan BulkExportRetryPause = TimeSpan.FromSeconds(5);
}
