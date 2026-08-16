using System.Buffers;
using System.Data.Common;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace DataverseDuck.Cli;

public enum OutputFormat
{
    /// <summary>Tab separated, with C-style escapes. The default: greppable and pasteable.</summary>
    Tsv,

    /// <summary>RFC 4180 comma separated values.</summary>
    Csv,

    /// <summary>A JSON array of objects.</summary>
    Json,
}

/// <summary>
/// Writes a result set.
///
/// The original writer joined values with tabs and nothing else, which is fine
/// until a Dataverse text field contains a tab or a newline — a multi-line
/// <c>description</c> is enough. Then the row silently gains columns or splits
/// in two, and the output still looks like a table. Every format here is
/// lossless: the value that went in can be recovered from the text that came
/// out.
/// </summary>
public static class ResultWriter
{
    /// <summary>
    /// The default encoder escapes a quote as \u0022 and every non-ASCII
    /// character as \uXXXX, so 'Åse "AA" Ø' becomes unreadable. That caution
    /// exists for JSON embedded in HTML; this output goes to a file or a pipe,
    /// so the relaxed encoder is both correct and legible. Danish text stays
    /// Danish.
    /// </summary>
    private static readonly JsonSerializerOptions JsonText =
        new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly SearchValues<char> TsvSpecials = SearchValues.Create("\\\t\n\r");
    private static readonly SearchValues<char> CsvSpecials = SearchValues.Create(",\"\n\r");

    public static bool TryParseFormat(string? text, out OutputFormat format, out string? error)
    {
        error = null;

        switch (text?.Trim().ToLowerInvariant())
        {
            case "tsv": format = OutputFormat.Tsv; return true;
            case "csv": format = OutputFormat.Csv; return true;
            case "json": format = OutputFormat.Json; return true;
            default:
                format = OutputFormat.Tsv;
                error = $"Unknown format '{text}'. Use one of: tsv, csv, json.";
                return false;
        }
    }

    /// <summary>
    /// Excel on non-English Windows reads a BOM-less UTF-8 CSV as the ANSI code
    /// page, so 'Åse' arrives as 'Ãse'. A BOM is the only signal it reliably
    /// takes. Nothing else wants one: RFC 8259 forbids it for JSON, and it
    /// breaks 'grep ^id' and any reader not opening with utf-8-sig.
    /// </summary>
    public const char ByteOrderMark = '\uFEFF';

    /// <summary>Writes <paramref name="reader"/> and returns the row count.</summary>
    public static long Write(
        DbDataReader reader, TextWriter writer, OutputFormat format, bool byteOrderMark = false)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);

        if (byteOrderMark)
        {
            if (format == OutputFormat.Json)
                throw new ArgumentException(
                    "JSON must not start with a byte order mark (RFC 8259 section 8.1).",
                    nameof(byteOrderMark));

            writer.Write(ByteOrderMark);
        }

        return format switch
        {
            OutputFormat.Csv => WriteCsv(reader, writer),
            OutputFormat.Json => WriteJson(reader, writer),
            _ => WriteTsv(reader, writer),
        };
    }

    /// <summary>
    /// Tab separated. There is no escape mechanism in the tab-separated-values
    /// media type — a field simply may not contain a tab or a newline — so
    /// rather than emit something ambiguous we escape with backslashes, the
    /// same convention Postgres uses for <c>COPY ... TO</c> in text mode.
    /// </summary>
    private static long WriteTsv(DbDataReader reader, TextWriter writer)
    {
        writer.WriteLine(string.Join('\t',
            Enumerable.Range(0, reader.FieldCount).Select(i => EscapeTsv(reader.GetName(i)))));

        var rows = 0L;

        while (reader.Read())
        {
            writer.WriteLine(string.Join('\t',
                Enumerable.Range(0, reader.FieldCount).Select(i => EscapeTsv(Format(reader, i)))));
            rows++;
        }

        return rows;
    }

    private static string EscapeTsv(string value)
    {
        if (value.AsSpan().IndexOfAny(TsvSpecials) < 0)
            return value;

        var builder = new System.Text.StringBuilder(value.Length + 8);

        foreach (var c in value)
        {
            _ = c switch
            {
                '\\' => builder.Append(@"\\"),
                '\t' => builder.Append(@"\t"),
                '\n' => builder.Append(@"\n"),
                '\r' => builder.Append(@"\r"),
                _ => builder.Append(c),
            };
        }

        return builder.ToString();
    }

    /// <summary>
    /// RFC 4180. Fields containing a comma, a double quote, CR or LF are
    /// enclosed in double quotes, and an embedded quote is doubled. Records end
    /// with CRLF, which section 2.1 requires; readers that want LF cope with
    /// CRLF, but a strict reader given LF may not.
    /// </summary>
    private static long WriteCsv(DbDataReader reader, TextWriter writer)
    {
        // Set explicitly rather than relying on the platform, so the output does
        // not change between Linux and Windows.
        writer.Write(string.Join(',',
            Enumerable.Range(0, reader.FieldCount).Select(i => EscapeCsv(reader.GetName(i)))));
        writer.Write("\r\n");

        var rows = 0L;

        while (reader.Read())
        {
            writer.Write(string.Join(',',
                Enumerable.Range(0, reader.FieldCount).Select(i => EscapeCsv(Format(reader, i)))));
            writer.Write("\r\n");
            rows++;
        }

        return rows;
    }

    private static string EscapeCsv(string value) =>
        value.AsSpan().IndexOfAny(CsvSpecials) >= 0
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;

    /// <summary>
    /// A JSON array of objects. This is the only format that can distinguish
    /// NULL from an empty string, and it keeps numbers and booleans as JSON
    /// types rather than quoting them.
    /// </summary>
    private static long WriteJson(DbDataReader reader, TextWriter writer)
    {
        var names = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
        var rows = 0L;

        writer.WriteLine("[");

        while (reader.Read())
        {
            if (rows > 0)
                writer.WriteLine(",");

            writer.Write("  {");

            for (var i = 0; i < names.Length; i++)
            {
                if (i > 0)
                    writer.Write(", ");

                writer.Write(JsonSerializer.Serialize(names[i], JsonText));
                writer.Write(": ");
                writer.Write(JsonValue(reader, i));
            }

            writer.Write("}");
            rows++;
        }

        if (rows > 0)
            writer.WriteLine();

        writer.WriteLine("]");

        return rows;
    }

    private static string JsonValue(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return "null";

        return reader.GetValue(ordinal) switch
        {
            bool b => b ? "true" : "false",

            // Serialized through the invariant culture by ToString("R")-like
            // paths inside the switch below; written unquoted so they stay
            // numbers to a JSON consumer.
            byte or sbyte or short or ushort or int or uint or long or ulong
                => Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture)!,

            decimal d => d.ToString(CultureInfo.InvariantCulture),
            double dbl => dbl.ToString("R", CultureInfo.InvariantCulture),
            float f => f.ToString("R", CultureInfo.InvariantCulture),

            _ => JsonSerializer.Serialize(Format(reader, ordinal), JsonText),
        };
    }

    /// <summary>
    /// Renders one value as text. Timestamps are round-tripped in ISO 8601 so
    /// the naive-UTC convention survives being copied elsewhere.
    /// </summary>
    internal static string Format(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? string.Empty
            : reader.GetValue(ordinal) switch
            {
                DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),

                // DuckDB returns these for DATE and TIME columns. Without an
                // explicit format they fall through to ToString() and pick up
                // the current culture, which turned a birthdate into
                // '05/15/1980' -- unsortable, and ambiguous with 15/05.
                DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                TimeOnly time => time.ToString("HH:mm:ss", CultureInfo.InvariantCulture),

                byte[] b => Convert.ToHexString(b),

                // Numbers must not pick up a culture either: a decimal comma
                // inside a CSV field is worse than merely ugly.
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),

                var v => v.ToString() ?? string.Empty,
            };
}
