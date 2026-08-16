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
Console.WriteLine("\n[4] THE BUG: joining naive CRM timestamps to offset-bearing JSON");
Exec(conn, "CREATE TABLE crm (id INTEGER, createdon TIMESTAMP)");
using (var app = conn.CreateAppender("crm"))
{
    var row = app.CreateRow();
    row.AppendValue(1);
    row.AppendValue(DateTime.SpecifyKind(new DateTime(2026, 8, 16, 10, 0, 0), DateTimeKind.Utc));
    row.EndRow();
}
File.WriteAllText("evt.json", """
{"id":1,"event_at":"2026-08-16T12:00:00+02:00"}
""");

foreach (var tz in new[] { "UTC", "Europe/Berlin" })
{
    Exec(conn, $"SET TimeZone = '{tz}'");
    Show(conn, $"""
        SELECT '{tz}' AS session_tz,
               c.createdon::VARCHAR AS crm_naive,
               j.event_at::VARCHAR  AS json_value,
               (c.createdon = j.event_at) AS equal_raw
        FROM crm c JOIN read_json_auto('evt.json') j ON c.id = j.id
        """, indent: "    ");
}
Console.WriteLine("  -> Same data, same query, different answer per session TZ. This is the trap.");

// ---------------------------------------------------------------- 5
Console.WriteLine("\n[5] THE FIX: normalise both sides to UTC-naive TIMESTAMP");
Exec(conn, "SET TimeZone = 'Europe/Berlin'");
Show(conn, """
    SELECT c.createdon::VARCHAR AS crm_utc,
           (j.event_at AT TIME ZONE 'UTC')::VARCHAR AS json_utc,
           c.createdon = (j.event_at AT TIME ZONE 'UTC') AS equal_normalised
    FROM crm c JOIN read_json_auto('evt.json') j ON c.id = j.id
    """, indent: "    ");
Exec(conn, "SET TimeZone = 'UTC'");
Show(conn, """
    SELECT c.createdon::VARCHAR AS crm_utc,
           (j.event_at AT TIME ZONE 'UTC')::VARCHAR AS json_utc,
           c.createdon = (j.event_at AT TIME ZONE 'UTC') AS equal_normalised
    FROM crm c JOIN read_json_auto('evt.json') j ON c.id = j.id
    """, indent: "    ");
Console.WriteLine("  -> Stable under both sessions. AT TIME ZONE 'UTC' converts TIMESTAMPTZ -> naive UTC.");

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
