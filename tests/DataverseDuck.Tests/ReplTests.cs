using System.Data;
using System.Text;
using DataverseDuck;
using DataverseDuck.Cli;

namespace DataverseDuck.Tests;

/// <summary>
/// The REPL has three pieces that can be wrong quietly: deciding whether a
/// statement is a fetch plan or plain SQL, deciding when the user has finished
/// typing, and rendering rows. Each is covered here.
/// </summary>
public class ReplStatementTests
{
    [Theory]
    [InlineData("WITH c AS DATAVERSE (SELECT contactid FROM contact) SELECT * FROM c")]
    [InlineData("with c as dataverse (SELECT contactid FROM contact)")]
    [InlineData("WITH logs AS JSON ('log.json')")]
    [InlineData("  \n WITH c AS DATAVERSE (SELECT 1)")]
    public void APlanIsRecognised(string statement)
    {
        Assert.True(ReplSession.LooksLikeAPlan(statement));
    }

    [Theory]
    [InlineData("SELECT * FROM c")]
    [InlineData("SELECT count(*) FROM contact")]
    public void PlainSqlIsNotAPlan(string statement)
    {
        Assert.False(ReplSession.LooksLikeAPlan(statement));
    }

    /// <summary>
    /// A plain CTE is ordinary DuckDB SQL and must run locally. Matching on
    /// 'WITH' alone would have sent it to the planner, which would reject it.
    /// </summary>
    [Fact]
    public void APlainCteIsNotAPlan()
    {
        Assert.False(ReplSession.LooksLikeAPlan(
            "WITH recent AS (SELECT * FROM logs WHERE ts > now()) SELECT * FROM recent"));
    }

    /// <summary>
    /// The binding keyword only counts after an AS. A column, alias or string
    /// that happens to say 'json' is not a request to fetch anything.
    /// </summary>
    [Theory]
    [InlineData("WITH x AS (SELECT json FROM t) SELECT * FROM x")]
    [InlineData("WITH x AS (SELECT * FROM t WHERE kind = 'dataverse') SELECT * FROM x")]
    public void AKeywordInTheBodyDoesNotMakeAPlan(string statement)
    {
        Assert.False(ReplSession.LooksLikeAPlan(statement));
    }
}

public class ReplAccumulateTests
{
    private static (bool Complete, string Statement) Feed(StringBuilder buffer, string line)
    {
        var complete = ReplLoop.Accumulate(buffer, line, out var statement);
        return (complete, statement);
    }

    [Fact]
    public void ASemicolonEndsAStatement()
    {
        var buffer = new StringBuilder();

        var result = Feed(buffer, "SELECT 1;");

        Assert.True(result.Complete);
        Assert.Contains("SELECT 1;", result.Statement);
        Assert.Equal(0, buffer.Length);
    }

    [Fact]
    public void AStatementContinuesUntilTheSemicolon()
    {
        var buffer = new StringBuilder();

        Assert.False(Feed(buffer, "SELECT").Complete);
        Assert.False(Feed(buffer, "  1 AS x").Complete);

        var result = Feed(buffer, "FROM t;");

        Assert.True(result.Complete);
        Assert.Contains("SELECT", result.Statement);
        Assert.Contains("1 AS x", result.Statement);
        Assert.Contains("FROM t;", result.Statement);
    }

    /// <summary>
    /// Requiring '.tables;' would be a needless surprise, so a meta command
    /// completes on its own line.
    /// </summary>
    [Fact]
    public void AMetaCommandCompletesWithoutASemicolon()
    {
        var buffer = new StringBuilder();

        var result = Feed(buffer, "  .tables  ");

        Assert.True(result.Complete);
        Assert.Equal(".tables", result.Statement);
    }

    /// <summary>
    /// Mid-statement a leading dot is data, not a command: treating it as one
    /// would run the fragment above it and discard the rest.
    /// </summary>
    [Fact]
    public void ADotIsNotAMetaCommandInsideAStatement()
    {
        var buffer = new StringBuilder();

        Assert.False(Feed(buffer, "SELECT").Complete);

        var result = Feed(buffer, ".5 AS x;");

        Assert.True(result.Complete);
        Assert.Contains("SELECT", result.Statement);
        Assert.Contains(".5 AS x;", result.Statement);
    }

    [Fact]
    public void ABlankLineDoesNotCompleteAnything()
    {
        var buffer = new StringBuilder();

        Assert.False(Feed(buffer, "").Complete);
    }
}

public class ReplTableTests
{
    private static string Render(DataTable table, int limit = 50)
    {
        using var reader = new DataTableReader(table);
        using var writer = new StringWriter();

        ReplTable.Write(reader, writer, limit);

        return writer.ToString();
    }

    private static DataTable Table(string column, params object?[] values)
    {
        var table = new DataTable();
        table.Columns.Add(column, typeof(string));

        foreach (var value in values)
            table.Rows.Add(value ?? DBNull.Value);

        return table;
    }

    [Fact]
    public void ColumnsAreWidenedToTheLongestValue()
    {
        var output = Render(Table("name", "Bob", "Cornelius"));

        Assert.Contains("---------", output);
        Assert.Contains("Cornelius", output);
    }

    /// <summary>
    /// NULL and the empty string look identical once padded, and the
    /// difference matters often enough in Dataverse data to be worth showing.
    /// </summary>
    [Fact]
    public void NullIsDistinguishableFromEmpty()
    {
        var output = Render(Table("value", null, ""));

        Assert.Contains("NULL", output);
    }

    /// <summary>
    /// Copying a line out of the terminal should not bring trailing spaces.
    /// </summary>
    [Fact]
    public void TheLastColumnIsNotPadded()
    {
        var table = new DataTable();
        table.Columns.Add("a", typeof(string));
        table.Columns.Add("b", typeof(string));
        table.Rows.Add("x", "y");
        table.Rows.Add("x", "longer");

        using var reader = new DataTableReader(table);
        using var writer = new StringWriter();
        ReplTable.Write(reader, writer, 50);

        foreach (var line in writer.ToString().Split(Environment.NewLine))
            Assert.Equal(line.TrimEnd(), line);
    }

    /// <summary>
    /// A 4,000 character JSON column wraps a terminal into uselessness, so the
    /// REPL is allowed to cut a cell short. ResultWriter never may.
    /// </summary>
    [Fact]
    public void ALongCellIsTruncated()
    {
        var output = Render(Table("value", new string('x', 200)));

        Assert.Contains("…", output);
        Assert.DoesNotContain(new string('x', 200), output);
    }

    /// <summary>
    /// A newline inside a value would otherwise split one row into two, which
    /// silently misreports the row count to the reader.
    /// </summary>
    [Fact]
    public void ANewlineInAValueDoesNotSplitTheRow()
    {
        var output = Render(Table("value", "first\nsecond"));

        var lines = output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(3, lines.Length);
    }

    [Fact]
    public void TheLimitStopsReadingAndIsReported()
    {
        var table = Table("value", "a", "b", "c", "d");

        using var reader = new DataTableReader(table);
        using var writer = new StringWriter();

        var (rows, truncated) = ReplTable.Write(reader, writer, limit: 2);

        Assert.Equal(2, rows);
        Assert.True(truncated);
        Assert.DoesNotContain("c", writer.ToString());
    }

    [Fact]
    public void AFullResultIsNotReportedAsTruncated()
    {
        var table = Table("value", "a", "b");

        using var reader = new DataTableReader(table);
        using var writer = new StringWriter();

        var (rows, truncated) = ReplTable.Write(reader, writer, limit: 2);

        Assert.Equal(2, rows);
        Assert.False(truncated);
    }
}

/// <summary>
/// The REPL fetches so the user can poke at the result afterwards, so a plan
/// with no trailing query is the natural move there. Everywhere else a plan
/// without a query produces nothing and is a mistake worth reporting.
/// </summary>
public class ReplPlanParsingTests
{
    private const string NoQuery = "WITH c AS DATAVERSE (SELECT contactid FROM contact)";

    [Fact]
    public void TheReplAcceptsAPlanWithNoFinalQuery()
    {
        var plan = DataversePlanParser.Parse(NoQuery, requireFinalQuery: false);

        Assert.Single(plan.Steps);
        Assert.Equal(string.Empty, plan.FinalSql);
    }

    [Fact]
    public void EveryOtherCallerStillRequiresAFinalQuery()
    {
        Assert.Throws<FormatException>(() => DataversePlanParser.Parse(NoQuery));
    }

    /// <summary>
    /// A plain CTE has nowhere to live without a query, so dropping the
    /// requirement must not quietly accept one.
    /// </summary>
    [Fact]
    public void APlainCteIsStillRefusedWithoutAFinalQuery()
    {
        Assert.Throws<FormatException>(() =>
            DataversePlanParser.Parse("WITH x AS (SELECT 1)", requireFinalQuery: false));
    }
}
