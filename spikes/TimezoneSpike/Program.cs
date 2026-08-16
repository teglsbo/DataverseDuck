using DuckDB.NET.Data;

// Spike C: timezone semantics across the Dataverse -> DuckDB -> JSON seam.
// Dataverse stores UTC; JSON logs may carry offsets; DuckDB TIMESTAMP is naive
// while TIMESTAMPTZ renders in a session timezone. Mismatches corrupt joins silently.

using var conn = new DuckDBConnection("Data Source=:memory:");
conn.Open();

Console.WriteLine($"Host TZ: {TimeZoneInfo.Local.Id}  (offset {TimeZoneInfo.Local.GetUtcOffset(DateTime.Now)})");
Show(conn, "SELECT current_setting('TimeZone') AS duckdb_timezone");

// ---------------------------------------------------------------- 1
Console.WriteLine("\n[1] DateTimeKind into TIMESTAMP (naive) vs TIMESTAMPTZ");
Exec(conn, "CREATE TABLE kinds (label VARCHAR, naive TIMESTAMP, tz TIMESTAMPTZ)");

var instant = new DateTime(2026, 8, 16, 12, 0, 0);
var kinds = new (string Label, DateTime Value)[]
{
    ("Kind=Utc",         DateTime.SpecifyKind(instant, DateTimeKind.Utc)),
    ("Kind=Local",       DateTime.SpecifyKind(instant, DateTimeKind.Local)),
    ("Kind=Unspecified", DateTime.SpecifyKind(instant, DateTimeKind.Unspecified)),
};

using (var app = conn.CreateAppender("kinds"))
{
    foreach (var (label, value) in kinds)
    {
        var row = app.CreateRow();
        row.AppendValue(label);
        row.AppendValue(value);
        row.AppendValue(value);
        row.EndRow();
    }
}
Show(conn, "SELECT label, naive::VARCHAR AS naive, tz::VARCHAR AS tz FROM kinds");
Console.WriteLine("  -> If all three rows are identical, the Appender IGNORES DateTimeKind.");

// ---------------------------------------------------------------- 2
Console.WriteLine("\n[2] Does session TimeZone change what we read back?");
foreach (var tz in new[] { "UTC", "America/New_York", "Europe/Berlin" })
{
    Exec(conn, $"SET TimeZone = '{tz}'");
    Show(conn, $"SELECT '{tz}' AS session_tz, naive::VARCHAR AS naive, tz::VARCHAR AS tz " +
               "FROM kinds WHERE label = 'Kind=Utc'", indent: "    ");
}
Console.WriteLine("  -> naive is stable; TIMESTAMPTZ RE-RENDERS per session. Non-deterministic across machines.");

// ---------------------------------------------------------------- 3
Console.WriteLine("\n[3] read_json_auto inference on common log timestamp shapes");
Exec(conn, "SET TimeZone = 'UTC'");
File.WriteAllText("ts.json", """
{"id":1,"kind":"zulu","ts":"2026-08-16T12:00:00Z"}
{"id":2,"kind":"offset_plus2","ts":"2026-08-16T12:00:00+02:00"}
{"id":3,"kind":"naive","ts":"2026-08-16T12:00:00"}
{"id":4,"kind":"space_sep","ts":"2026-08-16 12:00:00"}
{"id":5,"kind":"epoch_ms","ts":1755345600000}
""");
Show(conn, "SELECT kind, ts, typeof(ts) AS inferred FROM read_json_auto('ts.json') ORDER BY id");
Console.WriteLine("  -> Mixed shapes collapse to one column type. Note what wins.");

// Force each shape to be read as its own type to expose the divergence.
Show(conn, """
    SELECT kind,
           TRY_CAST(ts AS TIMESTAMP)::VARCHAR   AS as_naive,
           TRY_CAST(ts AS TIMESTAMPTZ)::VARCHAR AS as_tz
    FROM read_json_auto('ts.json', columns = {id: 'INTEGER', kind: 'VARCHAR', ts: 'VARCHAR'})
    ORDER BY id
    """);

// ---------------------------------------------------------------- 4
// [4] and [5] originally claimed the trap was the session timezone and that
// "AT TIME ZONE 'UTC'" was the fix. Both were wrong, and the output printed
// directly under them said so. Rewritten to match measurement.
Console.WriteLine("\n[4] THE REAL TRAP: the JSON side's shape, not the session timezone");
Exec(conn, "SET TimeZone = 'UTC'");
Exec(conn, "CREATE TABLE crm (id INTEGER, createdon TIMESTAMP)");
using (var app = conn.CreateAppender("crm"))
{
    foreach (var id in new[] { 1, 2 })
    {
        var row = app.CreateRow();
        row.AppendValue(id);
        row.AppendValue(DateTime.SpecifyKind(new DateTime(2026, 8, 16, 10, 0, 0), DateTimeKind.Utc));
        row.EndRow();
    }
}

// (a) Uniform and offset-bearing: DuckDB normalises to UTC for us.
File.WriteAllText("evt_offset.json", "{\"id\":1,\"event_at\":\"2026-08-16T12:00:00+02:00\"}\n");
foreach (var tz in new[] { "UTC", "Europe/Berlin" })
{
    Exec(conn, $"SET TimeZone = '{tz}'");
    Show(conn, $"""
        SELECT '{tz}' AS session_tz,
               typeof(j.event_at)  AS inferred,
               j.event_at::VARCHAR AS json_value,
               (c.createdon = j.event_at) AS equal_raw
        FROM crm c JOIN read_json_auto('evt_offset.json') j ON c.id = j.id
        """, indent: "    ");
}
Console.WriteLine("  -> (a) Inferred as naive TIMESTAMP ALREADY CONVERTED to UTC. The join is");
Console.WriteLine("         correct and the session timezone does not change it. No ceremony needed.");

// (b) Uniform but carrying no offset: silently wrong, nothing warns.
Exec(conn, "SET TimeZone = 'UTC'");
File.WriteAllText("evt_naive.json", "{\"id\":1,\"event_at\":\"2026-08-16T12:00:00\"}\n");
Show(conn, """
    SELECT typeof(j.event_at)   AS inferred,
           j.event_at::VARCHAR  AS json_value,
           c.createdon::VARCHAR AS crm_utc,
           (c.createdon = j.event_at) AS equal_raw
    FROM crm c JOIN read_json_auto('evt_naive.json') j ON c.id = j.id
    """, indent: "    ");
Console.WriteLine("  -> (b) THE SILENT ONE. The same instant written as local wall-clock with no");
Console.WriteLine("         offset is taken at face value, so the join is off by the offset and");
Console.WriteLine("         simply MISSES. No error. Only the producer knows which zone it meant.");

// (c) Mixed shapes: inference is unstable, and a single trailing newline flips it.
var mixed = "{\"id\":1,\"event_at\":\"2026-08-16T10:00:00Z\"}\n" +
            "{\"id\":2,\"event_at\":\"2026-08-16T12:00:00+02:00\"}";
File.WriteAllText("evt_mixed_nonl.json", mixed);
File.WriteAllText("evt_mixed_nl.json", mixed + "\n");
foreach (var f in new[] { "evt_mixed_nonl.json", "evt_mixed_nl.json" })
{
    Show(conn, $"""
        SELECT '{f}' AS file, id, typeof(event_at) AS inferred, event_at::VARCHAR AS value
        FROM read_json_auto('{f}') ORDER BY id
        """, indent: "    ");
}
Console.WriteLine("  -> (c) Same two rows, same engine. WITHOUT a trailing newline the column is");
Console.WriteLine("         inferred TIMESTAMP and normalised to UTC; WITH one it stays VARCHAR and");
Console.WriteLine("         keeps the raw offsets. One byte of whitespace changes the column type,");
Console.WriteLine("         and therefore whether downstream casts are correct. Mechanism unknown;");
Console.WriteLine("         the lesson is that inference is not something to build on.");
Show(conn, """
    SELECT id, event_at::VARCHAR AS raw,
           TRY_CAST(event_at AS TIMESTAMP)::VARCHAR   AS cast_naive,
           TRY_CAST(event_at AS TIMESTAMPTZ)::VARCHAR AS cast_tz
    FROM read_json_auto('evt_mixed_nl.json', columns = {id: 'INTEGER', event_at: 'VARCHAR'})
    ORDER BY id
    """, indent: "    ");
Console.WriteLine("  -> When it IS VARCHAR, row 2 casts to TIMESTAMP as 12:00 -- the offset is");
Console.WriteLine("     DROPPED, not applied -- while TIMESTAMPTZ gives the correct 10:00.");

// ---------------------------------------------------------------- 5
Console.WriteLine("\n[5] THE FIX: pin the column to VARCHAR, then cast to TIMESTAMPTZ and strip to UTC");
Console.WriteLine("    Direction matters. AT TIME ZONE on a TIMESTAMPTZ yields a naive value, but on");
Console.WriteLine("    a naive TIMESTAMP it yields a TIMESTAMPTZ. Applying it to an already-naive");
Console.WriteLine("    value is what makes a query session-dependent -- the old bug in this spike.");
Show(conn, """
    SELECT typeof(event_at)                    AS input_type,
           typeof(event_at AT TIME ZONE 'UTC') AS after_at_time_zone
    FROM read_json_auto('evt_offset.json')
    """, indent: "    ");

Console.WriteLine("\n    Pinning the type with columns = {...} removes the dependence on inference,");
Console.WriteLine("    so the same query is correct for both files under any session timezone:");
const string pinned = "columns = {id: 'INTEGER', event_at: 'VARCHAR'}";
foreach (var f in new[] { "evt_mixed_nonl.json", "evt_mixed_nl.json" })
{
    foreach (var tz in new[] { "UTC", "Europe/Berlin", "America/New_York" })
    {
        Exec(conn, $"SET TimeZone = '{tz}'");
        Show(conn, $"""
            SELECT '{f}' AS file, '{tz}' AS session_tz, c.id,
                   c.createdon = (CAST(j.event_at AS TIMESTAMPTZ) AT TIME ZONE 'UTC') AS fixed
            FROM crm c
            JOIN read_json_auto('{f}', {pinned}) j
              ON c.id = j.id
            ORDER BY c.id
            """, indent: "    ");
    }
}
Console.WriteLine("  -> True for every row, both files, every session timezone.");

Console.WriteLine("\n    Case (b) has no syntactic fix: the zone is not in the data. It must be");
Console.WriteLine("    supplied as an explicit assumption about the producer.");
foreach (var tz in new[] { "UTC", "America/New_York" })
{
    Exec(conn, $"SET TimeZone = '{tz}'");
    Show(conn, $"""
        SELECT '{tz}' AS session_tz,
               ((j.event_at AT TIME ZONE 'Europe/Copenhagen') AT TIME ZONE 'UTC')::VARCHAR AS assumed_utc,
               c.createdon = ((j.event_at AT TIME ZONE 'Europe/Copenhagen') AT TIME ZONE 'UTC') AS fixed
        FROM crm c JOIN read_json_auto('evt_naive.json') j ON c.id = j.id
        """, indent: "    ");
}
Console.WriteLine("  -> Declaring the zone recovers the match and is session-independent, but it is");
Console.WriteLine("     only as correct as the assumption, which the file cannot confirm.");

// ---------------------------------------------------------------- 5b
Console.WriteLine("\n[5b] NOT every Dataverse datetime is an instant (see ADR 0010)");
Console.WriteLine("    All of the above assumes the CRM side is a UTC instant. Columns whose");
Console.WriteLine("    DateTimeBehavior is DateOnly or TimeZoneIndependent are wall-clock values:");
Console.WriteLine("    a birthdate of 1980-05-15 is that date everywhere, and shifting it by any");
Console.WriteLine("    offset is corruption, not normalisation. dvduck resolves this from attribute");
Console.WriteLine("    metadata and maps such columns to DATE or naive TIMESTAMP, never shifting them.");

// ---------------------------------------------------------------- 6
Console.WriteLine("\n[6] Round-trip fidelity: does a UTC DateTime survive intact?");
var original = new DateTime(2026, 8, 16, 12, 34, 56, 789, DateTimeKind.Utc);
Exec(conn, "CREATE TABLE rt (v TIMESTAMP)");
using (var app = conn.CreateAppender("rt"))
{
    var row = app.CreateRow();
    row.AppendValue(original);
    row.EndRow();
}
using (var cmd = conn.CreateCommand())
{
    cmd.CommandText = "SELECT v FROM rt";
    using var r = cmd.ExecuteReader();
    r.Read();
    var back = r.GetDateTime(0);
    Console.WriteLine($"    sent {original:O} (Kind={original.Kind})");
    Console.WriteLine($"    got  {back:O} (Kind={back.Kind})");
    Console.WriteLine($"    ticks equal: {original.Ticks == back.Ticks}   Kind preserved: {original.Kind == back.Kind}");
}

static void Show(DuckDBConnection c, string sql, string indent = "  ")
{
    using var cmd = c.CreateCommand();
    cmd.CommandText = sql;
    using var r = cmd.ExecuteReader();
    var cols = Enumerable.Range(0, r.FieldCount).Select(r.GetName).ToArray();
    Console.WriteLine(indent + string.Join(" | ", cols.Select(x => x.PadRight(22))));
    while (r.Read())
    {
        var vals = Enumerable.Range(0, r.FieldCount)
            .Select(i => (r.IsDBNull(i) ? "<NULL>" : r.GetValue(i)?.ToString() ?? "")!.PadRight(22));
        Console.WriteLine(indent + string.Join(" | ", vals));
    }
}

static void Exec(DuckDBConnection c, string sql)
{
    using var cmd = c.CreateCommand();
    cmd.CommandText = sql;
    cmd.ExecuteNonQuery();
}
