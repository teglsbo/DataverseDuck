using DataverseDuck;
using DuckDB.NET.Data;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Xunit;

namespace DataverseDuckTests;

/// <summary>
/// Each test pins one behaviour measured in spikes/TimezoneSpike, so a future
/// DuckDB or SQL 4 CDS upgrade that changes the semantics fails here loudly
/// rather than silently shifting timestamps in production.
/// </summary>
public class UtcTimestampPolicyTests
{
    private static DuckDBConnection Open() => UtcTimestampPolicy.OpenConnection("Data Source=:memory:");

    private static T Scalar<T>(DuckDBConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return (T)Convert.ChangeType(cmd.ExecuteScalar()!, typeof(T))!;
    }

    private static void Exec(DuckDBConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void ConfigureConnection_pins_session_to_utc()
    {
        using var conn = Open();
        Assert.Equal("UTC", Scalar<string>(conn, "SELECT current_setting('TimeZone')"));
    }

    [Fact]
    public void Appender_ignores_DateTimeKind_which_is_why_ToUtcInstant_exists()
    {
        using var conn = Open();
        Exec(conn, "CREATE TABLE k (label VARCHAR, v TIMESTAMP)");

        var wall = new DateTime(2026, 8, 16, 12, 0, 0);
        using (var appender = conn.CreateAppender("k"))
        {
            foreach (var kind in new[] { DateTimeKind.Utc, DateTimeKind.Local, DateTimeKind.Unspecified })
            {
                var row = appender.CreateRow();
                row.AppendValue(kind.ToString());
                row.AppendValue(DateTime.SpecifyKind(wall, kind));
                row.EndRow();
            }
        }

        // All three land on the same value: the appender performs no conversion.
        Assert.Equal(1, Scalar<int>(conn, "SELECT count(DISTINCT v) FROM k"));
    }

    [Fact]
    public void ToUtcInstant_converts_local_and_preserves_utc()
    {
        var utc = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(utc, UtcTimestampPolicy.ToUtcInstant(utc));

        var local = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Local);
        var converted = UtcTimestampPolicy.ToUtcInstant(local);
        Assert.Equal(DateTimeKind.Utc, converted.Kind);
        Assert.Equal(local.ToUniversalTime(), converted);
    }

    [Fact]
    public void ToUtcInstant_strict_rejects_unspecified_kind()
    {
        var ambiguous = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Unspecified);

        Assert.Equal(DateTimeKind.Utc, UtcTimestampPolicy.ToUtcInstant(ambiguous).Kind);
        Assert.Throws<InvalidOperationException>(() => UtcTimestampPolicy.ToUtcInstant(ambiguous, strict: true));
    }

    [Fact]
    public void Casting_a_string_to_TIMESTAMP_silently_drops_the_offset()
    {
        using var conn = Open();

        // The naive cast loses the +02:00 and yields the wrong instant.
        Assert.Equal("2026-08-16 12:00:00",
            Scalar<string>(conn, "SELECT TRY_CAST('2026-08-16T12:00:00+02:00' AS TIMESTAMP)::VARCHAR"));

        // The policy expression applies the offset and returns naive UTC.
        var expr = UtcTimestampPolicy.JsonTimestampToUtc("'2026-08-16T12:00:00+02:00'");
        Assert.Equal("2026-08-16 10:00:00", Scalar<string>(conn, $"SELECT ({expr})::VARCHAR"));
    }

    [Fact]
    public void JsonTimestampToUtc_yields_naive_timestamp_not_timestamptz()
    {
        using var conn = Open();
        var expr = UtcTimestampPolicy.JsonTimestampToUtc("'2026-08-16T12:00:00+02:00'");
        Assert.Equal("TIMESTAMP", Scalar<string>(conn, $"SELECT typeof({expr})"));
    }

    [Theory]
    [InlineData("UTC")]
    [InlineData("Europe/Berlin")]
    [InlineData("America/New_York")]
    public void JsonTimestampToUtc_is_stable_regardless_of_session_timezone(string tz)
    {
        using var conn = Open();
        Exec(conn, $"SET TimeZone = '{tz}'");

        var expr = UtcTimestampPolicy.JsonTimestampToUtc("'2026-08-16T12:00:00+02:00'");
        Assert.Equal("2026-08-16 10:00:00", Scalar<string>(conn, $"SELECT ({expr})::VARCHAR"));
    }

    [Fact]
    public void Naive_to_timestamptz_comparison_is_session_dependent_hence_the_ban()
    {
        const string sql = "SELECT TIMESTAMP '2026-08-16 10:00:00' = TIMESTAMPTZ '2026-08-16T12:00:00+02:00'";

        using var conn = Open();
        Assert.True(Scalar<bool>(conn, sql));

        Exec(conn, "SET TimeZone = 'Europe/Berlin'");
        Assert.False(Scalar<bool>(conn, sql));
    }

    [Theory]
    [InlineData("UTC")]
    [InlineData("Europe/Berlin")]
    public void JsonEpochMillisToUtc_recovers_values_that_a_plain_cast_loses(string tz)
    {
        using var conn = Open();
        Exec(conn, $"SET TimeZone = '{tz}'");

        Assert.True(Scalar<bool>(conn, "SELECT TRY_CAST(1755345600000 AS TIMESTAMP) IS NULL"));

        var expr = UtcTimestampPolicy.JsonEpochMillisToUtc("1755345600000");
        Assert.Equal("TIMESTAMP", Scalar<string>(conn, $"SELECT typeof({expr})"));
        Assert.Equal("2025-08-16 12:00:00", Scalar<string>(conn, $"SELECT ({expr})::VARCHAR"));
    }

    [Fact]
    public void AssertNoTimestampTz_passes_for_naive_and_throws_for_tz_aware()
    {
        using var conn = Open();
        Exec(conn, "CREATE TABLE good (id INTEGER, createdon TIMESTAMP)");
        Exec(conn, "CREATE TABLE bad  (id INTEGER, createdon TIMESTAMPTZ)");

        UtcTimestampPolicy.AssertNoTimestampTz(conn, "good");

        var ex = Assert.Throws<InvalidOperationException>(
            () => UtcTimestampPolicy.AssertNoTimestampTz(conn, "bad"));
        Assert.Contains("createdon", ex.Message);
    }

    [Fact]
    public void Round_trip_preserves_the_instant_but_not_the_kind()
    {
        using var conn = Open();
        Exec(conn, "CREATE TABLE rt (v TIMESTAMP)");

        var original = new DateTime(2026, 8, 16, 12, 34, 56, 789, DateTimeKind.Utc);
        using (var appender = conn.CreateAppender("rt"))
        {
            var row = appender.CreateRow();
            row.AppendValue(UtcTimestampPolicy.ToUtcInstant(original));
            row.EndRow();
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT v FROM rt";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());

        var roundTripped = reader.GetDateTime(0);
        Assert.Equal(original.Ticks, roundTripped.Ticks);
        Assert.Equal(DateTimeKind.Unspecified, roundTripped.Kind);
    }

    [Fact]
    public void DateOnly_behaviour_maps_to_DATE_without_conversion()
    {
        var attribute = new DateTimeAttributeMetadata
        {
            DateTimeBehavior = DateTimeBehavior.DateOnly,
        };

        var mapping = UtcTimestampPolicy.MapDateTimeAttribute(attribute);
        Assert.Equal("DATE", mapping.DuckDbType);
        Assert.False(mapping.ConvertToUtc);
    }

    [Fact]
    public void TimeZoneIndependent_behaviour_is_wall_clock_and_must_not_convert()
    {
        var attribute = new DateTimeAttributeMetadata
        {
            DateTimeBehavior = DateTimeBehavior.TimeZoneIndependent,
        };

        var mapping = UtcTimestampPolicy.MapDateTimeAttribute(attribute);
        Assert.Equal("TIMESTAMP", mapping.DuckDbType);
        Assert.False(mapping.ConvertToUtc);
    }

    [Fact]
    public void UserLocal_behaviour_is_a_real_instant_and_converts_to_utc()
    {
        var attribute = new DateTimeAttributeMetadata
        {
            DateTimeBehavior = DateTimeBehavior.UserLocal,
        };

        var mapping = UtcTimestampPolicy.MapDateTimeAttribute(attribute);
        Assert.Equal("TIMESTAMP", mapping.DuckDbType);
        Assert.True(mapping.ConvertToUtc);
    }

    [Fact]
    public void Application_name_is_set_for_telemetry_attribution()
    {
        Assert.Equal("dvduck-cli", Sql4CdsConnectionFactory.DefaultApplicationName);
    }
}
