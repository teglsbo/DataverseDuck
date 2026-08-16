using System.Data.Common;
using System.Text;

namespace DataverseDuck.Cli;

/// <summary>
/// Aligned columns, for reading on a terminal. Deliberately separate from
/// <see cref="ResultWriter"/>, which exists to produce output another program
/// will parse and must never lose or pad a value. This one is allowed to
/// truncate, because a 4,000 character JSON column wraps a terminal into
/// uselessness.
/// </summary>
internal static class ReplTable
{
    /// <summary>Longest a single cell may be before it is cut short.</summary>
    private const int MaxCellWidth = 40;

    /// <summary>
    /// Rows are buffered because column widths cannot be known before the last
    /// row has been seen. That is fine at the default limit and is the reason
    /// there is a default limit.
    /// </summary>
    public static (long Rows, bool Truncated) Write(DbDataReader reader, TextWriter writer, int limit)
    {
        var headers = new string[reader.FieldCount];
        for (var i = 0; i < reader.FieldCount; i++)
            headers[i] = reader.GetName(i);

        var rows = new List<string[]>();
        var truncated = false;

        while (reader.Read())
        {
            if (limit > 0 && rows.Count >= limit)
            {
                truncated = true;
                break;
            }

            var row = new string[reader.FieldCount];

            for (var i = 0; i < reader.FieldCount; i++)
                row[i] = Cell(reader, i);

            rows.Add(row);
        }

        if (headers.Length == 0)
            return (rows.Count, truncated);

        var widths = new int[headers.Length];

        for (var i = 0; i < headers.Length; i++)
        {
            widths[i] = headers[i].Length;

            foreach (var row in rows)
                widths[i] = Math.Max(widths[i], row[i].Length);
        }

        WriteRow(writer, headers, widths);
        writer.WriteLine(string.Join("  ", widths.Select(width => new string('-', width))));

        foreach (var row in rows)
            WriteRow(writer, row, widths);

        return (rows.Count, truncated);
    }

    private static void WriteRow(TextWriter writer, string[] values, int[] widths)
    {
        var line = new StringBuilder();

        for (var i = 0; i < values.Length; i++)
        {
            if (i > 0)
                line.Append("  ");

            line.Append(values[i]);

            // Nothing is padded after the last column, so that copying a line
            // out of the terminal does not bring trailing spaces with it.
            if (i < values.Length - 1)
                line.Append(' ', widths[i] - values[i].Length);
        }

        writer.WriteLine(line.ToString());
    }

    private static string Cell(DbDataReader reader, int ordinal)
    {
        // NULL and the empty string look identical once padded, and the
        // difference matters often enough in Dataverse data to be worth
        // showing.
        if (reader.IsDBNull(ordinal))
            return "NULL";

        var text = ResultWriter.Format(reader, ordinal).ReplaceLineEndings(" ");

        return text.Length > MaxCellWidth
            ? string.Concat(text.AsSpan(0, MaxCellWidth - 1), "…")
            : text;
    }
}
