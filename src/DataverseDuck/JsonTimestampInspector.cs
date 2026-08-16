using DataverseDuck.Schema;
using DuckDB.NET.Data;

namespace DataverseDuck;

/// <param name="Column">The column that stayed textual.</param>
/// <param name="Sample">A value from it, for the diagnostic message.</param>
/// <param name="HasMixedOffsets">
/// True when the column mixes offset notations (<c>Z</c> and <c>+02:00</c>),
/// which is the usual reason DuckDB gave up on it.
/// </param>
public sealed record TextualTimestamp(string Column, string Sample, bool HasMixedOffsets)
{
    /// <summary>An expression that converts the column to naive UTC correctly.</summary>
    public string Fix => UtcTimestampPolicy.JsonTimestampToUtc(DuckDbIdentifier.Quote(Column));

    public string Describe() =>
        $"Column '{Column}' holds timestamps as text (e.g. {Sample})" +
        (HasMixedOffsets ? " because the file mixes offset notations." : ".") +
        $" Comparing or sorting it compares strings, not instants, which gives wrong" +
        $" answers across offsets. Use: {Fix}";
}

/// <summary>
/// Finds timestamp columns that DuckDB failed to type.
///
/// <c>read_json_auto</c> normally converts ISO 8601 strings to naive UTC
/// <c>TIMESTAMP</c> correctly. But if a column mixes offset notations — say
/// <c>2026-08-16T22:00:00Z</c> in one record and <c>2026-08-16T23:00:00+02:00</c>
/// in another, which happens whenever logs come from more than one service —
/// inference falls back to <c>VARCHAR</c> silently.
///
/// From then on <c>MIN</c>, <c>ORDER BY</c> and <c>&lt;</c> compare strings.
/// That is not merely imprecise, it is wrong: <c>'…23:00:00+02:00'</c> sorts
/// after <c>'…22:00:00Z'</c> even though it is an hour earlier. Nothing errors.
///
/// So we look for it and say so.
/// </summary>
public static class JsonTimestampInspector
{
    /// <summary>How many rows to sample. Enough to catch a mix, cheap enough to always run.</summary>
    private const int SampleSize = 200;

    /// <summary>
    /// Returns textual columns in <paramref name="viewName"/> whose values look
    /// like timestamps.
    /// </summary>
    public static IReadOnlyList<TextualTimestamp> Inspect(DuckDBConnection connection, string viewName)
    {
        ArgumentNullException.ThrowIfNull(connection);
        DuckDbIdentifier.Validate(viewName);

        var quoted = DuckDbIdentifier.Quote(viewName);
        List<TextualTimestamp> found = [];

        foreach (var column in TextColumns(connection, quoted))
        {
            var identifier = DuckDbIdentifier.Quote(column);

            using var command = connection.CreateCommand();

            // A column counts as a timestamp only if every non-null sampled value
            // parses. One stray value would otherwise flag ordinary text columns.
            command.CommandText = $"""
                SELECT count(*),
                       count(TRY_CAST({identifier} AS TIMESTAMPTZ)),
                       count(DISTINCT {identifier} LIKE '%Z'),
                       min({identifier})
                FROM (SELECT {identifier} FROM {quoted}
                      WHERE {identifier} IS NOT NULL LIMIT {SampleSize})
                """;

            using var reader = command.ExecuteReader();

            if (!reader.Read() || reader.IsDBNull(3))
                continue;

            var total = reader.GetInt64(0);
            var parsed = reader.GetInt64(1);

            if (total == 0 || parsed != total)
                continue;

            found.Add(new TextualTimestamp(column, reader.GetString(3), reader.GetInt64(2) > 1));
        }

        return found;
    }

    private static List<string> TextColumns(DuckDBConnection connection, string quotedView)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT column_name, column_type FROM (DESCRIBE SELECT * FROM {quotedView})";

        using var reader = command.ExecuteReader();
        List<string> columns = [];

        while (reader.Read())
        {
            if (reader.GetString(1) == "VARCHAR")
                columns.Add(reader.GetString(0));
        }

        return columns;
    }
}
