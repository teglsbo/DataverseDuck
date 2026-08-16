using System.Text.RegularExpressions;
using DuckDB.NET.Data;

// Does DuckDB's automatic join-filter pushdown spare a remote scan from fetching
// everything? Research said it pushes an exact IN list only while the build side stays
// under 'dynamic_or_filter_threshold' (default 50) and degrades to a min/max range
// above it -- worthless for GUID keys. Measure both claims against the real engine.

using var connection = new DuckDBConnection("Data Source=:memory:");
connection.Open();

void Execute(string sql)
{
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    command.ExecuteNonQuery();
}

string Scalar(string sql)
{
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    return command.ExecuteScalar()?.ToString() ?? "<null>";
}

Console.WriteLine("[1] Is the threshold real, and what is it by default?");
Console.WriteLine($"    duckdb version              = {Scalar("SELECT version()")}");
Console.WriteLine($"    dynamic_or_filter_threshold = {Scalar("SELECT current_setting('dynamic_or_filter_threshold')")}");

// A stand-in for the large Dataverse table: contacts keyed by random GUIDs, exactly
// the key shape that makes a min/max range useless.
Execute("CREATE TABLE contact AS SELECT uuid() AS contactid, 'Contact ' || i AS fullname FROM range(200000) t(i)");

// What DuckDB hands the scan is printed under 'Dynamic Filters' in the analyzed plan.
// An enumerable IN list is renderable into FetchXML or OData; a bare range is not.
string DynamicFilters(int keys)
{
    Execute($"CREATE OR REPLACE TABLE logline AS SELECT contactid FROM contact USING SAMPLE {keys} ROWS");

    using var command = connection.CreateCommand();
    command.CommandText = "EXPLAIN ANALYZE SELECT count(*) FROM contact c JOIN logline l ON l.contactid = c.contactid";
    using var reader = command.ExecuteReader();
    var plan = "";
    while (reader.Read())
    {
        plan = reader.GetString(reader.FieldCount - 1);
    }

    // Strip the box-drawing frame so the filter text reads as one line.
    var flat = Regex.Replace(plan, @"[\u2500-\u257F\s]+", " ");
    var start = flat.IndexOf("Dynamic Filters:", StringComparison.Ordinal);
    if (start < 0) return "<none pushed>";

    var text = flat[(start + "Dynamic Filters:".Length)..];
    var end = text.IndexOf("Projections", StringComparison.Ordinal);
    return (end < 0 ? text : text[..end]).Trim();
}

void Report(int keys)
{
    var filters = DynamicFilters(keys);
    var hasInList = filters.Contains(" IN (", StringComparison.Ordinal);
    var hasRange = filters.Contains(">=", StringComparison.Ordinal);
    var verdict = hasInList
        ? "YES - exact IN list, renderable as FetchXML/OData"
        : hasRange
            ? "NO  - min/max GUID range only, matches ~everything"
            : $"NO  - {filters}";
    Console.WriteLine($"    {keys,9} | {verdict}");
}

Console.WriteLine();
Console.WriteLine("[2] What reaches the scan as the join grows?");
Console.WriteLine();
Console.WriteLine("    join keys | pushed filter carries an enumerable key set?");
Console.WriteLine("    ----------|-----------------------------------------------");
foreach (var keys in new[] { 10, 50, 51, 100, 1000 })
{
    Report(keys);
}

Console.WriteLine();
Console.WriteLine("[3] Does raising the threshold rescue the large-join case?");
Execute("SET dynamic_or_filter_threshold = 100000");
Console.WriteLine($"    dynamic_or_filter_threshold = {Scalar("SELECT current_setting('dynamic_or_filter_threshold')")}");
Console.WriteLine();
Console.WriteLine("    join keys | pushed filter carries an enumerable key set?");
Console.WriteLine("    ----------|-----------------------------------------------");
foreach (var keys in new[] { 51, 1000, 20000 })
{
    Report(keys);
}
