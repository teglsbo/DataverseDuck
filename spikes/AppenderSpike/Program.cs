using System.Data;
using DuckDB.NET.Data;

// Spike A: does the DuckDB.NET Appender accept the CLR types SQL 4 CDS emits?
// Probes each type independently so one failure doesn't mask the rest.

var probes = new (string Name, string DuckType, object? Value)[]
{
    ("Guid",           "UUID",          Guid.Parse("0bdd4472-981d-f111-8341-0022482aa957")),
    ("Guid_null",      "UUID",          null),
    ("string",         "VARCHAR",       "Fourth Coffee"),
    ("string_null",    "VARCHAR",       null),
    ("int",            "INTEGER",       42),
    ("long",           "BIGINT",        9_000_000_000L),
    ("short",          "SMALLINT",      (short)7),
    ("bool",           "BOOLEAN",       true),
    ("decimal",        "DECIMAL(19,4)", 12345.6789m),
    ("double",         "DOUBLE",        3.14159d),
    ("float",          "FLOAT",         2.5f),
    ("DateTime",       "TIMESTAMP",     new DateTime(2026, 8, 16, 7, 40, 5, DateTimeKind.Utc)),
    ("DateTime_null",  "TIMESTAMP",     null),
    ("DateOnly",       "DATE",          new DateOnly(2026, 8, 16)),
    ("bytes",          "BLOB",          new byte[] { 1, 2, 3 }),
    ("DateTimeOffset", "TIMESTAMPTZ",   new DateTimeOffset(2026, 8, 16, 7, 40, 5, TimeSpan.Zero)),
};

using var conn = new DuckDBConnection("Data Source=:memory:");
conn.Open();

int pass = 0, fail = 0;
foreach (var (name, duckType, value) in probes)
{
    var table = "t_" + name;
    try
    {
        Exec(conn, $"CREATE TABLE {table} (v {duckType})");

        using (var appender = conn.CreateAppender(table))
        {
            var row = appender.CreateRow();
            AppendDynamic(row, value);
            row.EndRow();
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT v FROM {table}";
        using var rdr = cmd.ExecuteReader();
        rdr.Read();
        var back = rdr.IsDBNull(0) ? "<NULL>" : rdr.GetValue(0)?.ToString();
        var clr = rdr.IsDBNull(0) ? "-" : rdr.GetFieldType(0).Name;
        Console.WriteLine($"  PASS  {name,-15} {duckType,-14} -> {back}  (read back as {clr})");
        pass++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  FAIL  {name,-15} {duckType,-14} -> {ex.GetType().Name}: {Trim(ex.Message)}");
        fail++;
    }
}

Console.WriteLine($"\nAppender type probe: {pass} passed, {fail} failed.");

// Second probe: streaming throughput, the path a DbDataReader load will take.
Exec(conn, "CREATE TABLE bulk (id UUID, name VARCHAR, revenue DECIMAL(19,4), createdon TIMESTAMP)");
const int rows = 200_000;
var sw = System.Diagnostics.Stopwatch.StartNew();
using (var appender = conn.CreateAppender("bulk"))
{
    for (int i = 0; i < rows; i++)
    {
        var row = appender.CreateRow();
        row.AppendValue(Guid.NewGuid());
        if (i % 7 == 0) row.AppendNullValue(); else row.AppendValue($"Account {i}");
        if (i % 11 == 0) row.AppendNullValue(); else row.AppendValue(i * 1.25m);
        row.AppendValue(new DateTime(2026, 1, 1).AddMinutes(i));
        row.EndRow();
    }
}
sw.Stop();
Console.WriteLine($"Bulk: {rows:N0} rows in {sw.ElapsedMilliseconds} ms " +
                  $"({rows / Math.Max(sw.Elapsed.TotalSeconds, 0.001):N0} rows/sec)");

using (var cmd = conn.CreateCommand())
{
    cmd.CommandText = "SELECT count(*), count(name), count(revenue) FROM bulk";
    using var r = cmd.ExecuteReader();
    r.Read();
    Console.WriteLine($"Verify: {r.GetInt64(0):N0} rows, {r.GetInt64(1):N0} non-null name, {r.GetInt64(2):N0} non-null revenue");
}

// Third probe: drive the appender purely from reader metadata, with no
// compile-time knowledge of column types. This is the SQL 4 CDS load path.
Console.WriteLine("\nSchema-driven load (simulating a DbDataReader from SQL 4 CDS):");
using (var src = conn.CreateCommand())
{
    src.CommandText = "SELECT id, name, revenue, createdon FROM bulk LIMIT 1000";
    using var reader = src.ExecuteReader();

    var ddl = BuildDdl("copied", reader);
    Console.WriteLine("  DDL: " + ddl);
    Exec(conn, ddl);

    var copied = 0;
    using (var appender = conn.CreateAppender("copied"))
    {
        while (reader.Read())
        {
            var row = appender.CreateRow();
            for (int i = 0; i < reader.FieldCount; i++)
                AppendDynamic(row, reader.IsDBNull(i) ? null : reader.GetValue(i));
            row.EndRow();
            copied++;
        }
    }
    Console.WriteLine($"  Copied {copied} rows via schema-driven path.");
}

// Fourth probe: the actual goal - join DuckDB-cached CRM rows against JSON.
File.WriteAllText("logs.json", """
{"account_id":"0bdd4472-981d-f111-8341-0022482aa957","level":"ERROR","msg":"timeout"}
{"account_id":"0bdd4472-981d-f111-8341-0022482aa957","level":"WARN","msg":"retry"}
""");
Exec(conn, "CREATE TABLE crm_accounts (accountid UUID, name VARCHAR)");
using (var appender = conn.CreateAppender("crm_accounts"))
{
    var row = appender.CreateRow();
    row.AppendValue(Guid.Parse("0bdd4472-981d-f111-8341-0022482aa957"));
    row.AppendValue("Fourth Coffee");
    row.EndRow();
}
using (var cmd = conn.CreateCommand())
{
    cmd.CommandText = """
        SELECT a.name, j.level, count(*) AS n
        FROM read_json_auto('logs.json') j
        JOIN crm_accounts a ON a.accountid = CAST(j.account_id AS UUID)
        GROUP BY a.name, j.level ORDER BY j.level
        """;
    using var r = cmd.ExecuteReader();
    Console.WriteLine("\nJSON x CRM join:");
    while (r.Read())
        Console.WriteLine($"  {r.GetString(0)} | {r.GetString(1)} | {r.GetInt64(2)}");
}

static string BuildDdl(string table, IDataReader reader)
{
    var cols = new List<string>();
    for (int i = 0; i < reader.FieldCount; i++)
        cols.Add($"\"{reader.GetName(i)}\" {MapType(reader.GetFieldType(i))}");
    return $"CREATE TABLE {table} ({string.Join(", ", cols)})";
}

static string MapType(Type t) => Nullable.GetUnderlyingType(t) is { } u ? MapType(u) : t switch
{
    _ when t == typeof(Guid)           => "UUID",
    _ when t == typeof(string)         => "VARCHAR",
    _ when t == typeof(bool)           => "BOOLEAN",
    _ when t == typeof(byte)           => "UTINYINT",
    _ when t == typeof(short)          => "SMALLINT",
    _ when t == typeof(int)            => "INTEGER",
    _ when t == typeof(long)           => "BIGINT",
    _ when t == typeof(decimal)        => "DECIMAL(19,4)",
    _ when t == typeof(double)         => "DOUBLE",
    _ when t == typeof(float)          => "FLOAT",
    _ when t == typeof(DateTime)       => "TIMESTAMP",
    _ when t == typeof(DateTimeOffset) => "TIMESTAMPTZ",
    _ when t == typeof(byte[])         => "BLOB",
    _                                  => "VARCHAR",
};

// The appender has no object overload, so dispatch on runtime type.
static void AppendDynamic(IDuckDBAppenderRow row, object? value)
{
    switch (value)
    {
        case null:             row.AppendNullValue(); break;
        case Guid v:           row.AppendValue(v); break;
        case string v:         row.AppendValue(v); break;
        case bool v:           row.AppendValue(v); break;
        case byte v:           row.AppendValue(v); break;
        case short v:          row.AppendValue(v); break;
        case int v:            row.AppendValue(v); break;
        case long v:           row.AppendValue(v); break;
        case decimal v:        row.AppendValue(v); break;
        case double v:         row.AppendValue(v); break;
        case float v:          row.AppendValue(v); break;
        case DateTime v:       row.AppendValue(v); break;
        case DateOnly v:       row.AppendValue(v); break;
        case DateTimeOffset v: row.AppendValue(v); break;
        case byte[] v:         row.AppendValue(v); break;
        default:               row.AppendValue(value.ToString()); break;
    }
}

static void Exec(DuckDBConnection c, string sql)
{
    using var cmd = c.CreateCommand();
    cmd.CommandText = sql;
    cmd.ExecuteNonQuery();
}

static string Trim(string s) => (s.Length > 90 ? s[..90] + "..." : s).ReplaceLineEndings(" ");
