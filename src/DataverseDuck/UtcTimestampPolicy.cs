using DuckDB.NET.Data;
using Microsoft.Xrm.Sdk.Metadata;

namespace DataverseDuck;

/// <summary>
/// Timestamp handling rules for the Dataverse -> DuckDB -> JSON seam.
///
/// Every rule here was measured, not assumed. See spikes/TimezoneSpike.
/// The invariant: every timestamp stored in the cache is a naive DuckDB
/// TIMESTAMP whose value is UTC. TIMESTAMPTZ never enters the cache, because
/// comparing naive to tz-aware values is session-timezone dependent and
/// therefore non-deterministic across machines.
/// </summary>
public static class UtcTimestampPolicy
{
    /// <summary>
    /// Rule 1: pin the session to UTC. Without this, any accidental
    /// TIMESTAMPTZ comparison silently returns different answers on different
    /// machines (measured: True under UTC, False under Europe/Berlin).
    /// Call immediately after opening every connection.
    /// </summary>
    public static void ConfigureConnection(DuckDBConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SET TimeZone = 'UTC'";
        cmd.ExecuteNonQuery();
    }

    /// <summary>Opens a connection with the policy already applied.</summary>
    public static DuckDBConnection OpenConnection(string connectionString)
    {
        var connection = new DuckDBConnection(connectionString);
        connection.Open();
        ConfigureConnection(connection);
        return connection;
    }

    /// <summary>
    /// Rule 3: the DuckDB Appender ignores <see cref="DateTimeKind"/> entirely
    /// (measured: Utc, Local and Unspecified all store identical wall-clock
    /// values). A Local value would therefore be stored as though it were UTC.
    /// Convert it before it reaches the appender.
    /// </summary>
    /// <param name="value">Value as returned by SQL 4 CDS.</param>
    /// <param name="strict">
    /// When true, an <see cref="DateTimeKind.Unspecified"/> value throws rather
    /// than being assumed to be UTC. Use for sources you do not trust.
    /// </param>
    public static DateTime ToUtcInstant(DateTime value, bool strict = false) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        DateTimeKind.Unspecified when strict => throw new InvalidOperationException(
            $"Refusing to store DateTime '{value:O}' with Kind=Unspecified: its timezone is " +
            "ambiguous and the DuckDB appender would store it verbatim as UTC."),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    /// <summary>
    /// Rule 6 (Dataverse side): only <see cref="DateTimeBehavior.UserLocal"/>
    /// is a true UTC instant. DateOnly and TimeZoneIndependent are wall-clock
    /// values and must not be timezone-converted, or they are corrupted.
    /// </summary>
    public static DateTimeMapping MapDateTimeAttribute(DateTimeAttributeMetadata attribute)
    {
        ArgumentNullException.ThrowIfNull(attribute);

        var behavior = attribute.DateTimeBehavior?.Value;

        // Behaviour name comparison: DateTimeBehavior exposes static instances
        // rather than an enum, so compare the underlying string value.
        if (string.Equals(behavior, DateTimeBehavior.DateOnly.Value, StringComparison.OrdinalIgnoreCase))
            return new DateTimeMapping("DATE", ConvertToUtc: false);

        if (string.Equals(behavior, DateTimeBehavior.TimeZoneIndependent.Value, StringComparison.OrdinalIgnoreCase))
            return new DateTimeMapping("TIMESTAMP", ConvertToUtc: false);

        // UserLocal, or unspecified behaviour on older attributes: a UTC instant.
        return new DateTimeMapping("TIMESTAMP", ConvertToUtc: true);
    }

    /// <summary>
    /// Rule 4: reading a log timestamp out of JSON.
    ///
    /// Casting a string straight to TIMESTAMP silently discards the offset
    /// (measured: '2026-08-16T12:00:00+02:00' becomes 12:00:00, not 10:00:00).
    /// Going via TIMESTAMPTZ applies the offset, and AT TIME ZONE 'UTC' then
    /// converts back to a naive UTC TIMESTAMP matching the cache.
    ///
    /// Note AT TIME ZONE is direction-dependent: applied to a naive TIMESTAMP
    /// it produces a TIMESTAMPTZ instead. Only ever apply it to a TIMESTAMPTZ.
    /// </summary>
    /// <param name="columnExpression">A JSON column holding a timestamp string.</param>
    /// <param name="assumeUtcIfNoOffset">
    /// When the string carries no offset, TIMESTAMPTZ parsing adopts the session
    /// timezone. Because Rule 1 pins the session to UTC that is already correct,
    /// but this makes the intent explicit and survives a stray SET TimeZone.
    /// </param>
    public static string JsonTimestampToUtc(string columnExpression, bool assumeUtcIfNoOffset = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(columnExpression);

        return assumeUtcIfNoOffset
            ? $"(TRY_CAST({columnExpression} AS TIMESTAMPTZ) AT TIME ZONE 'UTC')"
            : $"TRY_CAST({columnExpression} AS TIMESTAMP)";
    }

    /// <summary>
    /// Rule 6 (JSON side): epoch milliseconds cannot be cast to TIMESTAMP
    /// (measured: TRY_CAST returns NULL, so rows vanish silently).
    ///
    /// epoch_ms already returns a naive TIMESTAMP in UTC, verified stable
    /// across session timezones. Deliberately no AT TIME ZONE here: applying
    /// it to an already-naive value would convert it *to* a TIMESTAMPTZ, which
    /// is the direction error this class exists to prevent.
    /// </summary>
    public static string JsonEpochMillisToUtc(string columnExpression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(columnExpression);
        return $"epoch_ms(TRY_CAST({columnExpression} AS BIGINT))";
    }

    /// <summary>
    /// Guard rail: fails loudly if any TIMESTAMPTZ column reached the cache,
    /// which would reintroduce session-dependent comparisons.
    /// </summary>
    public static void AssertNoTimestampTz(DuckDBConnection connection, string tableName)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT column_name, data_type FROM information_schema.columns " +
            "WHERE table_name = $table AND data_type ILIKE '%WITH TIME ZONE%'";
        var p = cmd.CreateParameter();
        p.ParameterName = "table";
        p.Value = tableName;
        cmd.Parameters.Add(p);

        var offenders = new List<string>();
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
                offenders.Add($"{reader.GetString(0)} ({reader.GetString(1)})");

        if (offenders.Count > 0)
            throw new InvalidOperationException(
                $"Table '{tableName}' contains timezone-aware columns, which make joins " +
                $"session-dependent: {string.Join(", ", offenders)}. Store naive TIMESTAMP in UTC instead.");
    }
}

/// <summary>How a Dataverse date/time attribute maps into the DuckDB cache.</summary>
/// <param name="DuckDbType">DuckDB column type.</param>
/// <param name="ConvertToUtc">
/// Whether the value is a real instant that should be normalised to UTC.
/// False for DateOnly and TimeZoneIndependent wall-clock values.
/// </param>
public readonly record struct DateTimeMapping(string DuckDbType, bool ConvertToUtc);
