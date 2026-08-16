using System.Data;
using DataverseDuck.Cli;

namespace DataverseDuck.Tests;

/// <summary>
/// The writer previously joined values with tabs and no escaping, so a
/// Dataverse text field containing a tab or a newline -- a multi-line
/// 'description' is enough -- silently added columns or split a row in two.
/// These cover RFC 4180 rule by rule, and the corruption case that started it.
/// </summary>
public class ResultWriterTests
{
    private static string Write(OutputFormat format, DataTable table)
    {
        using var reader = new DataTableReader(table);
        using var writer = new StringWriter();

        ResultWriter.Write(reader, writer, format);

        return writer.ToString();
    }

    private static DataTable OneColumn(params object?[] values)
    {
        var table = new DataTable();
        table.Columns.Add("value", typeof(string));

        foreach (var value in values)
            table.Rows.Add(value ?? DBNull.Value);

        return table;
    }

    // ---------------------------------------------------------------- RFC 4180

    [Fact]
    public void CsvLeavesOrdinaryFieldsUnquoted()
    {
        // Section 2.4: fields need not be quoted when they contain nothing special.
        Assert.Equal("value\r\nAcme\r\n", Write(OutputFormat.Csv, OneColumn("Acme")));
    }

    [Fact]
    public void CsvQuotesAFieldContainingAComma()
    {
        Assert.Equal("value\r\n\"Acme, Inc\"\r\n", Write(OutputFormat.Csv, OneColumn("Acme, Inc")));
    }

    [Fact]
    public void CsvDoublesAnEmbeddedQuoteAndEnclosesTheField()
    {
        // Section 2.7: a quote inside a quoted field is escaped by another quote.
        Assert.Equal("value\r\n\"say \"\"hi\"\"\"\r\n", Write(OutputFormat.Csv, OneColumn("say \"hi\"")));
    }

    [Fact]
    public void CsvQuotesAFieldContainingALineBreak()
    {
        // Section 2.6: line breaks are allowed only inside quotes. This is the
        // case that used to split one row into two.
        Assert.Equal("value\r\n\"line one\nline two\"\r\n",
            Write(OutputFormat.Csv, OneColumn("line one\nline two")));
    }

    [Fact]
    public void CsvQuotesAFieldContainingACarriageReturn()
    {
        Assert.Equal("value\r\n\"a\rb\"\r\n", Write(OutputFormat.Csv, OneColumn("a\rb")));
    }

    [Fact]
    public void CsvEndsEveryRecordWithCrLf()
    {
        // Section 2.1 requires CRLF, and it must not vary with the host platform.
        var csv = Write(OutputFormat.Csv, OneColumn("a", "b"));

        Assert.Equal("value\r\na\r\nb\r\n", csv);
        Assert.DoesNotContain("\n\n", csv);
    }

    [Fact]
    public void CsvQuotesAHeaderThatNeedsIt()
    {
        var table = new DataTable();
        table.Columns.Add("full,name", typeof(string));
        table.Rows.Add("x");

        Assert.StartsWith("\"full,name\"\r\n", Write(OutputFormat.Csv, table));
    }

    [Fact]
    public void CsvRoundTripsThroughAStrictReader()
    {
        // The real test of compliance: something else must be able to read it.
        var nasty = "quote \" comma , newline \n tab \t end";
        var csv = Write(OutputFormat.Csv, OneColumn(nasty));

        Assert.Equal([nasty], ParseCsv(csv).Skip(1).Select(r => r.Single()).ToArray());
    }

    // ---------------------------------------------------------------- TSV

    [Fact]
    public void TsvEscapesTabsRatherThanEmittingAnExtraColumn()
    {
        Assert.Contains(@"a\tb", Write(OutputFormat.Tsv, OneColumn("a\tb")));
    }

    [Fact]
    public void TsvEscapesNewlinesRatherThanSplittingTheRow()
    {
        var tsv = Write(OutputFormat.Tsv, OneColumn("line one\nline two"));

        Assert.Contains(@"line one\nline two", tsv);
        Assert.Equal(2, tsv.TrimEnd('\r', '\n').Split('\n').Length);
    }

    [Fact]
    public void TsvEscapesBackslashesSoTheEscapingIsReversible()
    {
        // Without this, a literal \t in the data would be indistinguishable
        // from an escaped tab.
        Assert.Contains(@"C:\\temp", Write(OutputFormat.Tsv, OneColumn(@"C:\temp")));
    }

    // ---------------------------------------------------------------- JSON

    [Fact]
    public void JsonDistinguishesNullFromEmptyString()
    {
        // The only format that can. TSV and CSV both render these identically.
        var json = Write(OutputFormat.Json, OneColumn(null, ""));

        Assert.Contains("\"value\": null", json);
        Assert.Contains("\"value\": \"\"", json);
    }

    [Fact]
    public void JsonKeepsNumbersUnquoted()
    {
        var table = new DataTable();
        table.Columns.Add("n", typeof(int));
        table.Rows.Add(42);

        Assert.Contains("\"n\": 42", Write(OutputFormat.Json, table));
    }

    [Fact]
    public void JsonKeepsBooleansUnquoted()
    {
        var table = new DataTable();
        table.Columns.Add("b", typeof(bool));
        table.Rows.Add(true);

        Assert.Contains("\"b\": true", Write(OutputFormat.Json, table));
    }

    [Fact]
    public void JsonEscapesQuotesAndNewlinesReadably()
    {
        // Correct JSON either way, but the default encoder emits \u0022 for a
        // quote, which is legible to a parser and to nobody else.
        var json = Write(OutputFormat.Json, OneColumn("a\"b\nc"));

        Assert.Contains(@"a\""b\nc", json);
        Assert.DoesNotContain(@"\u0022", json);
        Assert.Equal(1, CountTopLevelRows(json));
    }

    [Fact]
    public void JsonLeavesNonAsciiTextAlone()
    {
        // The tenant this was built against is Danish. 'Åse' must not become
        // '\u00C5se' just because the default encoder is cautious about HTML.
        var json = Write(OutputFormat.Json, OneColumn("Åse Ø Ærø"));

        Assert.Contains("Åse Ø Ærø", json);
        Assert.Equal("Åse Ø Ærø",
            System.Text.Json.JsonDocument.Parse(json).RootElement[0].GetProperty("value").GetString());
    }

    [Fact]
    public void JsonEmitsAnEmptyArrayForNoRows()
    {
        Assert.Equal("[\n]\n".Replace("\n", Environment.NewLine),
            Write(OutputFormat.Json, OneColumn()));
    }

    [Fact]
    public void JsonSeparatesRowsWithCommas()
    {
        var json = Write(OutputFormat.Json, OneColumn("a", "b"));

        Assert.Equal(2, CountTopLevelRows(json));
        Assert.Contains("},", json);
    }

    // ---------------------------------------------------------------- format flag

    [Theory]
    [InlineData("csv", OutputFormat.Csv)]
    [InlineData("CSV", OutputFormat.Csv)]
    [InlineData(" json ", OutputFormat.Json)]
    [InlineData("tsv", OutputFormat.Tsv)]
    public void ParsesFormatNamesLeniently(string text, OutputFormat expected)
    {
        Assert.True(ResultWriter.TryParseFormat(text, out var format, out _));
        Assert.Equal(expected, format);
    }

    [Fact]
    public void RejectsAnUnknownFormatAndSaysWhatIsAvailable()
    {
        Assert.False(ResultWriter.TryParseFormat("xml", out _, out var error));
        Assert.Contains("tsv, csv, json", error);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>A deliberately strict RFC 4180 reader, written here so the test
    /// does not depend on the same assumptions as the writer.</summary>
    private static List<List<string>> ParseCsv(string text)
    {
        List<List<string>> records = [];
        List<string> record = [];
        var field = new System.Text.StringBuilder();
        var quoted = false;
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];

            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i += 2;
                        continue;
                    }

                    quoted = false;
                    i++;
                    continue;
                }

                field.Append(c);
                i++;
                continue;
            }

            switch (c)
            {
                case '"':
                    quoted = true;
                    i++;
                    break;

                case ',':
                    record.Add(field.ToString());
                    field.Clear();
                    i++;
                    break;

                case '\r' when i + 1 < text.Length && text[i + 1] == '\n':
                    record.Add(field.ToString());
                    field.Clear();
                    records.Add(record);
                    record = [];
                    i += 2;
                    break;

                default:
                    field.Append(c);
                    i++;
                    break;
            }
        }

        if (field.Length > 0 || record.Count > 0)
        {
            record.Add(field.ToString());
            records.Add(record);
        }

        return records;
    }

    private static int CountTopLevelRows(string json) =>
        System.Text.Json.JsonDocument.Parse(json).RootElement.GetArrayLength();
}
