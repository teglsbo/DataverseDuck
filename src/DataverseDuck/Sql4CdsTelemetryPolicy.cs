using System.Reflection;
using MarkMpn.Sql4Cds.Engine;
using Microsoft.ApplicationInsights.Extensibility;

namespace DataverseDuck;

/// <summary>
/// Disables SQL 4 CDS's own, built-in Application Insights telemetry.
///
/// <see cref="Sql4CdsConnectionFactory.DefaultApplicationName"/>'s doc comment already
/// notes what this closes off: SQL 4 CDS ships anonymous telemetry to a hardcoded
/// instrumentation key, and while query text is not sent on success, it <i>is</i> sent on
/// exception -- which can be a <c>DATAVERSE (...)</c> body containing whatever a caller wrote,
/// including anything the plan's own author would not want leaving this machine.
///
/// There is no public switch for this on <see cref="Sql4CdsConnection"/>; the telemetry
/// client is a private field. Reached by reflection, and this throws rather than
/// continuing quietly if that field or its shape is ever renamed -- disabling telemetry is
/// the whole point, so silently failing to disable it is worse than refusing to start.
/// </summary>
internal static class Sql4CdsTelemetryPolicy
{
    public static void Disable(Sql4CdsConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var telemetryField = typeof(Sql4CdsConnection).GetField("_ai", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "SQL 4 CDS telemetry field was not found; refusing to continue without disabling upstream telemetry.");
        var telemetryClient = telemetryField.GetValue(connection)
            ?? throw new InvalidOperationException(
                "SQL 4 CDS telemetry client was not found; refusing to continue without disabling upstream telemetry.");
        var configuration = telemetryClient.GetType()
            .GetProperty("TelemetryConfiguration", BindingFlags.Instance | BindingFlags.Public)?.GetValue(telemetryClient) as TelemetryConfiguration
            ?? throw new InvalidOperationException(
                "SQL 4 CDS telemetry configuration was not found; refusing to continue without disabling upstream telemetry.");

        // Application Insights 3.x resolves this lazily from the OpenTelemetry service
        // provider on first use. Resolving it now, while the provider is still alive,
        // caches it on the client so later Track* calls (e.g. SQL 4 CDS's own
        // TrackException on plan-generation failures) use the cached logger instead of
        // re-resolving from the now-disposed provider and throwing ObjectDisposedException.
        _ = telemetryClient.GetType()
            .GetProperty("Logger", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(telemetryClient)
            ?? throw new InvalidOperationException(
                "SQL 4 CDS telemetry logger was not found; refusing to continue without disabling upstream telemetry.");

        Disable(configuration);
    }

    internal static void Disable(TelemetryConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        try
        {
            // In Application Insights 3.x the TelemetryClient constructor builds its
            // OpenTelemetry pipeline, making every configuration setting immutable.
            // TelemetryConfiguration no longer exposes the v2 telemetry channel or
            // processor pipeline. Its supported teardown API disposes the OpenTelemetry
            // SDK and, with it, every provider, processor, and exporter.
            configuration.Dispose();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "SQL 4 CDS telemetry configuration could not be stopped; refusing to continue with upstream telemetry enabled.",
                ex);
        }
    }
}
