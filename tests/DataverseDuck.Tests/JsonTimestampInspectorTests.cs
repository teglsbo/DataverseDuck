using DataverseDuck;

namespace DataverseDuck.Tests;

public class JsonTimestampInspectorTests : IDisposable
{
    private readonly DuckDB.NET.Data.DuckDBConnection _connection =
        UtcTimestampPolicy.OpenConnection("Data Source=:memory:");

    private readonly List<string> _files = [];

    public void Dispose()
    {
        _connection.Dispose();
        foreach (var file in _files) File.Delete(file);
        GC.SuppressFinalize(this);
    }

    private string Write(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dvduck-ts-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        _files.Add(path);
        return path;
    }

    private DataverseCache NewCache(List<string>? log = null) =>
        new(_connection, new FakeQuerySource(new System.Data.DataTable()))
        {
            Log = log is null ? null : log.Add,
        };

    [Fact]
    public void Mixed_offset_notations_are_reported()
    {
        // The real-world case: two services logging the same field differently.
        var messages = new List<string>();
        var cache = NewCache(messages);

        var found = cache.RegisterJson("logs", Write("""
            [{"ts":"2026-08-16T22:00:00Z"},{"ts":"2026-08-16T23:00:00+02:00"}]
            """));

        var column = Assert.Single(found);
        Assert.Equal("ts", column.Column);
        Assert.True(column.HasMixedOffsets);
        Assert.Contains(messages, m => m.Contains("compares strings, not instants"));
    }

    [Fact]
    public void The_suggested_fix_produces_the_answer_string_comparison_got_wrong()
    {
        // Proves the warning is actionable, not just noise. Under string
        // ordering '...23:00:00+02:00' sorts last despite being an hour earlier.
        var cache = NewCache();

        var found = cache.RegisterJson("logs", Write("""
            [{"id":"a","ts":"2026-08-16T22:00:00Z"},{"id":"b","ts":"2026-08-16T23:00:00+02:00"}]
            """));

        using var wrong = cache.Query("SELECT id FROM logs ORDER BY ts LIMIT 1");
        Assert.True(wrong.Read());
        Assert.Equal("a", wrong.GetString(0));

        using var right = cache.Query($"SELECT id FROM logs ORDER BY {Assert.Single(found).Fix} LIMIT 1");
        Assert.True(right.Read());
        Assert.Equal("b", right.GetString(0));
    }

    [Fact]
    public void The_fix_yields_naive_utc()
    {
        var cache = NewCache();

        var found = cache.RegisterJson("logs", Write("""
            [{"ts":"2026-08-16T23:00:00+02:00"},{"ts":"2026-08-16T22:00:00Z"}]
            """));

        using var rows = cache.Query($"SELECT {Assert.Single(found).Fix} AS t FROM logs ORDER BY 1");

        Assert.True(rows.Read());
        Assert.Equal(new DateTime(2026, 8, 16, 21, 0, 0), rows.GetDateTime(0));
        Assert.True(rows.Read());
        Assert.Equal(new DateTime(2026, 8, 16, 22, 0, 0), rows.GetDateTime(0));
    }

    [Fact]
    public void A_uniform_column_is_typed_by_duckdb_and_not_reported()
    {
        var cache = NewCache();

        var found = cache.RegisterJson("logs", Write("""
            [{"ts":"2026-08-16T22:00:00+02:00"},{"ts":"2026-08-16T23:00:00+02:00"}]
            """));

        Assert.Empty(found);

        // And it really is a proper timestamp, reduced to UTC.
        using var rows = cache.Query("SELECT min(ts) FROM logs");
        Assert.True(rows.Read());
        Assert.Equal(new DateTime(2026, 8, 16, 20, 0, 0), rows.GetDateTime(0));
    }

    [Fact]
    public void Ordinary_text_is_not_reported()
    {
        var found = NewCache().RegisterJson("logs", Write("""
            [{"level":"error","message":"disk full"},{"level":"info","message":"ok"}]
            """));

        Assert.Empty(found);
    }

    [Fact]
    public void A_column_where_only_some_values_parse_is_not_reported()
    {
        // One parseable value should not brand a text column as a timestamp.
        var found = NewCache().RegisterJson("logs", Write("""
            [{"note":"2026-08-16T22:00:00Z"},{"note":"see ticket 4471"}]
            """));

        Assert.Empty(found);
    }

    [Fact]
    public void Nulls_do_not_hide_a_textual_timestamp()
    {
        var found = NewCache().RegisterJson("logs", Write("""
            [{"ts":"2026-08-16T22:00:00Z"},{"ts":null},{"ts":"2026-08-16T23:00:00+02:00"}]
            """));

        Assert.Equal("ts", Assert.Single(found).Column);
    }

    [Fact]
    public void An_all_null_column_is_not_reported()
    {
        var found = NewCache().RegisterJson("logs", Write("""
            [{"ts":null},{"ts":null}]
            """));

        Assert.Empty(found);
    }

    [Fact]
    public void A_single_notation_that_still_fails_inference_is_reported_without_blaming_mixing()
    {
        // Dates only: DuckDB may keep these as text, but there is no offset mix
        // to blame, so the message should not claim there is one.
        var found = NewCache().RegisterJson("logs", Write("""
            [{"ts":"2026-08-16T22:00:00.123456789Z"},{"ts":"2026-08-17T22:00:00.123456789Z"}]
            """));

        foreach (var column in found)
            Assert.False(column.HasMixedOffsets);
    }

    [Fact]
    public void Several_textual_columns_are_all_reported()
    {
        var found = NewCache().RegisterJson("logs", Write("""
            [{"started":"2026-08-16T22:00:00Z","ended":"2026-08-16T23:00:00Z","level":"error"},
             {"started":"2026-08-16T23:00:00+02:00","ended":"2026-08-17T01:00:00+02:00","level":"info"}]
            """));

        Assert.Equal(["ended", "started"], found.Select(f => f.Column).Order());
    }

    [Fact]
    public void A_column_name_needing_quoting_is_handled()
    {
        var found = NewCache().RegisterJson("logs", Write("""
            [{"event ts":"2026-08-16T22:00:00Z"},{"event ts":"2026-08-16T23:00:00+02:00"}]
            """));

        Assert.Equal("event ts", Assert.Single(found).Column);
    }
}
