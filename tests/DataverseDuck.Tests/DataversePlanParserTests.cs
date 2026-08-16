using DataverseDuck;

namespace DataverseDuck.Tests;

public class DataversePlanParserTests
{
    [Fact]
    public void ReadsEntriesInWrittenOrder()
    {
        var plan = DataversePlanParser.Parse("""
            WITH crm_account AS DATAVERSE (
                SELECT accountid, name FROM account
                WHERE accountid IN {{SELECT DISTINCT CAST(account_id AS UUID) FROM logs}}
            ),
            crm_contact AS DATAVERSE (
                SELECT contactid, fullname FROM contact
                WHERE parentcustomerid IN {{SELECT accountid FROM crm_account}}
            )
            SELECT count(*) FROM crm_contact
            """);

        Assert.Equal(["crm_account", "crm_contact"], plan.Steps.Select(s => s.Name));
        Assert.StartsWith("SELECT accountid", plan.Steps[0].Body);
        Assert.Contains("{{SELECT accountid FROM crm_account}}", plan.Steps[1].Body);
        Assert.Equal("SELECT count(*) FROM crm_contact", plan.FinalSql);
    }

    [Fact]
    public void LeavesOrdinaryCtesForDuckDb()
    {
        var plan = DataversePlanParser.Parse("""
            WITH crm_contact AS DATAVERSE (SELECT contactid FROM contact),
                 busy AS (SELECT customer_id FROM logs GROUP BY 1 HAVING count(*) > 5)
            SELECT * FROM busy JOIN crm_contact USING (contactid)
            """);

        Assert.Single(plan.Steps);
        Assert.Equal("crm_contact", plan.Steps[0].Name);
        Assert.StartsWith("WITH busy AS (SELECT customer_id FROM logs", plan.FinalSql);
        Assert.EndsWith("SELECT * FROM busy JOIN crm_contact USING (contactid)", plan.FinalSql);
    }

    [Fact]
    public void KeepsMaterializedHintOnOrdinaryCtes()
    {
        var plan = DataversePlanParser.Parse("""
            WITH crm_contact AS DATAVERSE (SELECT contactid FROM contact),
                 busy AS MATERIALIZED (SELECT 1)
            SELECT 1
            """);

        Assert.Contains("busy AS MATERIALIZED (SELECT 1)", plan.FinalSql);
    }

    [Fact]
    public void ParenthesesInsideStringLiteralsDoNotEndTheEntry()
    {
        var plan = DataversePlanParser.Parse("""
            WITH crm_contact AS DATAVERSE (
                SELECT contactid FROM contact WHERE fullname = 'Bob (Robert'
            )
            SELECT * FROM crm_contact
            """);

        Assert.Single(plan.Steps);
        Assert.Contains("'Bob (Robert'", plan.Steps[0].Body);
        Assert.Equal("SELECT * FROM crm_contact", plan.FinalSql);
    }

    [Fact]
    public void DoubledQuotesAreAnEscapeNotAnEnding()
    {
        var plan = DataversePlanParser.Parse("""
            WITH crm_contact AS DATAVERSE (
                SELECT contactid FROM contact WHERE fullname = 'O''Brien (x)'
            )
            SELECT 1
            """);

        Assert.Contains("'O''Brien (x)'", plan.Steps[0].Body);
    }

    [Fact]
    public void CommentsDoNotUnbalanceTheScan()
    {
        var plan = DataversePlanParser.Parse("""
            WITH crm_contact AS DATAVERSE (
                -- a stray ) in a comment
                SELECT contactid FROM contact /* and ( here */
            )
            SELECT 1
            """);

        Assert.Single(plan.Steps);
        Assert.Equal("SELECT 1", plan.FinalSql);
    }

    [Fact]
    public void NestedParenthesesAreBalanced()
    {
        var plan = DataversePlanParser.Parse("""
            WITH crm_contact AS DATAVERSE (
                SELECT contactid FROM contact WHERE statecode IN (0, (1))
            )
            SELECT 1
            """);

        Assert.Contains("IN (0, (1))", plan.Steps[0].Body);
    }

    [Fact]
    public void PlainQueryPassesStraightThrough()
    {
        var plan = DataversePlanParser.Parse("SELECT 1");

        Assert.Empty(plan.Steps);
        Assert.Equal("SELECT 1", plan.FinalSql);
    }

    [Fact]
    public void RefusesReadingAnEntryFetchedLater()
    {
        var error = Assert.Throws<FormatException>(() => DataversePlanParser.Parse("""
            WITH crm_contact AS DATAVERSE (
                SELECT contactid FROM contact WHERE parentcustomerid IN {{SELECT accountid FROM crm_account}}
            ),
            crm_account AS DATAVERSE (SELECT accountid FROM account)
            SELECT 1
            """));

        Assert.Contains("comes later", error.Message);
        Assert.Contains("crm_account", error.Message);
    }

    [Fact]
    public void RefusesReadingAnOrdinaryCte()
    {
        var error = Assert.Throws<FormatException>(() => DataversePlanParser.Parse("""
            WITH busy AS (SELECT customer_id FROM logs),
                 crm_contact AS DATAVERSE (
                     SELECT contactid FROM contact WHERE contactid IN {{SELECT customer_id FROM busy}}
                 )
            SELECT 1
            """));

        Assert.Contains("ordinary CTE", error.Message);
        Assert.Contains("busy", error.Message);
    }

    [Fact]
    public void RefusesSelfReference()
    {
        var error = Assert.Throws<FormatException>(() => DataversePlanParser.Parse("""
            WITH crm_contact AS DATAVERSE (
                SELECT contactid FROM contact WHERE contactid IN {{SELECT contactid FROM crm_contact}}
            )
            SELECT 1
            """));

        Assert.Contains("itself", error.Message);
    }

    [Fact]
    public void RefusesRecursive()
    {
        var error = Assert.Throws<FormatException>(() =>
            DataversePlanParser.Parse("WITH RECURSIVE t AS DATAVERSE (SELECT 1) SELECT 1"));

        Assert.Contains("RECURSIVE", error.Message);
    }

    [Fact]
    public void RefusesAWithBlockThatFetchesNothing()
    {
        var error = Assert.Throws<FormatException>(() =>
            DataversePlanParser.Parse("WITH a AS (SELECT 1) SELECT * FROM a"));

        Assert.Contains("nothing to bring in", error.Message);
    }

    [Fact]
    public void RefusesMaterializedOnADataverseEntry()
    {
        var error = Assert.Throws<FormatException>(() =>
            DataversePlanParser.Parse("WITH a AS DATAVERSE MATERIALIZED (SELECT 1) SELECT 1"));

        Assert.Contains("always materialised", error.Message);
    }

    [Fact]
    public void RefusesAWithBlockWithNoQueryAfterIt()
    {
        var error = Assert.Throws<FormatException>(() =>
            DataversePlanParser.Parse("WITH a AS DATAVERSE (SELECT 1)"));

        Assert.Contains("not followed by a query", error.Message);
    }

    [Fact]
    public void ReportsUnbalancedParentheses()
    {
        Assert.Throws<FormatException>(() =>
            DataversePlanParser.Parse("WITH a AS DATAVERSE (SELECT 1 SELECT 2"));
    }


    [Fact]
    public void ReadsJsonEntries()
    {
        var plan = DataversePlanParser.Parse("""
            WITH logs AS JSON ('webchat/*.json'),
                 crm_contact AS DATAVERSE (
                     SELECT contactid FROM contact
                     WHERE contactid IN {{SELECT customer_id FROM logs}}
                 )
            SELECT 1
            """);

        Assert.Equal(2, plan.Steps.Count);
        Assert.Equal(PlanStepKind.Json, plan.Steps[0].Kind);
        Assert.Equal("logs", plan.Steps[0].Name);
        Assert.Equal("webchat/*.json", plan.Steps[0].Body);
        Assert.Equal(PlanStepKind.Dataverse, plan.Steps[1].Kind);
    }

    [Fact]
    public void JsonOnlyPlansAreAllowed()
    {
        var plan = DataversePlanParser.Parse("WITH logs AS JSON ('a.json') SELECT * FROM logs");

        Assert.Single(plan.Steps);
        Assert.Equal("SELECT * FROM logs", plan.FinalSql);
    }

    [Fact]
    public void JsonPathKeepsDoubledQuoteAsEscape()
    {
        var plan = DataversePlanParser.Parse("WITH j AS JSON ('it''s/*.json') SELECT 1");

        Assert.Equal("it's/*.json", plan.Steps[0].Body);
    }

    [Fact]
    public void RefusesJsonEntryThatIsNotOneQuotedPath()
    {
        var error = Assert.Throws<FormatException>(() =>
            DataversePlanParser.Parse("WITH j AS JSON (SELECT 1) SELECT 1"));

        Assert.Contains("one quoted path or glob", error.Message);
    }

    [Fact]
    public void RefusesJsonEntryWithTwoLiterals()
    {
        var error = Assert.Throws<FormatException>(() =>
            DataversePlanParser.Parse("WITH j AS JSON ('a.json', 'b.json') SELECT 1"));

        Assert.Contains("exactly one quoted path", error.Message);
    }

    [Fact]
    public void RefusesAnEntryDefinedTwice()
    {
        var error = Assert.Throws<FormatException>(() =>
            DataversePlanParser.Parse("WITH j AS JSON ('a.json'), j AS JSON ('b.json') SELECT 1"));

        Assert.Contains("defined twice", error.Message);
    }

    [Fact]
    public void RefusesReadingAJsonEntryDeclaredLater()
    {
        var error = Assert.Throws<FormatException>(() => DataversePlanParser.Parse("""
            WITH crm_contact AS DATAVERSE (
                SELECT contactid FROM contact WHERE contactid IN {{SELECT customer_id FROM logs}}
            ),
            logs AS JSON ('webchat/*.json')
            SELECT 1
            """));

        Assert.Contains("comes later", error.Message);
        Assert.Contains("logs", error.Message);
    }

    [Fact]
    public void AcceptsQuotedNames()
    {
        var plan = DataversePlanParser.Parse("""
            WITH "crm contact" AS DATAVERSE (SELECT contactid FROM contact)
            SELECT 1
            """);

        Assert.Equal("crm contact", plan.Steps[0].Name);
    }

    /// <summary>
    /// A structural failure is only detected at the end of input, which is
    /// nowhere near the mistake. On a real multi-line plan the message alone
    /// does not locate it, so the position and the offending line are the
    /// whole value of the error.
    /// </summary>
    [Fact]
    public void PointsAnUnclosedParenAtTheParenThatOpenedIt()
    {
        var error = Assert.Throws<FormatException>(() => DataversePlanParser.Parse("""
            WITH logs AS JSON ('logs.json'),
                 accts AS DATAVERSE (SELECT accountid FROM account
            SELECT * FROM logs
            """));

        Assert.Contains("line 2, column 25", error.Message);
        Assert.Contains("accts AS DATAVERSE (SELECT accountid FROM account", error.Message);
        Assert.Contains("^", error.Message);
    }

    [Fact]
    public void PutsTheCaretUnderTheOffendingCharacter()
    {
        var error = Assert.Throws<FormatException>(() => DataversePlanParser.Parse("""
            WITH logs AS JSON ('logs.json'),
                 accts DATAVERSE (SELECT accountid FROM account)
            SELECT 1
            """));

        var lines = error.Message.Split('\n');
        var source = Array.Find(lines, l => l.StartsWith("  2 | ", StringComparison.Ordinal))!;
        var caret = lines[Array.IndexOf(lines, source) + 1];

        // The caret must land on the token the parser rejected, not merely
        // somewhere on the line.
        Assert.Equal('D', source[caret.IndexOf('^')]);
    }

    [Fact]
    public void SaysWhenAnUnterminatedLiteralIsProbablyAnEarlierQuote()
    {
        var error = Assert.Throws<FormatException>(() => DataversePlanParser.Parse("""
            WITH logs AS JSON ('logs.json'),
                 accts AS DATAVERSE (SELECT name FROM account WHERE name = 'Acme),
                 more AS JSON ('x.json')
            SELECT 1
            """));

        // Quotes pair left to right, so the literal that fails to close is not
        // the one that was mistyped. Claiming otherwise would send the reader
        // to a line that is fine.
        Assert.Contains("an earlier quote is probably unclosed", error.Message);
    }

    [Fact]
    public void ReportsTheFirstLineAsLineOne()
    {
        var error = Assert.Throws<FormatException>(
            () => DataversePlanParser.Parse("WITH logs AS JSON ('a.json' SELECT 1"));

        Assert.Contains("line 1, column 19", error.Message);
    }
}

/// <summary>
/// The sugar has to produce exactly what the hand-written caches produce, or it
/// is worse than not having it.
/// </summary>
public class PlanEndToEndTests : IDisposable
{
    private readonly DuckDB.NET.Data.DuckDBConnection _connection =
        UtcTimestampPolicy.OpenConnection("Data Source=:memory:");

    private string? _file;

    public void Dispose()
    {
        _connection.Dispose();
        if (_file is not null) File.Delete(_file);
        GC.SuppressFinalize(this);
    }

    private static Guid Acc(int n) => Guid.Parse($"acc00000-0000-0000-0000-{n:D12}");
    private static Guid Con(int n) => Guid.Parse($"c0000000-0000-0000-0000-{n:D12}");

    [Fact]
    public void One_plan_chains_two_hops_and_fetches_only_what_the_logs_name()
    {
        var accounts = Enumerable.Range(1, 5000).ToDictionary(Acc, n => $"Account {n}");
        var contacts = Enumerable.Range(1, 5000)
            .SelectMany(n => new[] { (Con(n * 2), $"Contact {n}a", Acc(n)), (Con(n * 2 + 1), $"Contact {n}b", Acc(n)) })
            .ToDictionary(x => x.Item1, x => (x.Item2, x.Item3));

        var source = new TwoTableSource { Accounts = accounts, Contacts = contacts };
        var cache = new DataverseCache(_connection, source);

        _file = Path.Combine(Path.GetTempPath(), $"dvduck-plan-{Guid.NewGuid():N}.json");
        File.WriteAllText(_file, $$"""
            [{"account_id":"{{Acc(3)}}","channel":"webchat"},
             {"account_id":"{{Acc(8)}}","channel":"webchat"},
             {"account_id":"{{Acc(9)}}","channel":"branch"}]
            """);

        cache.RegisterJson("logs", _file);

        var plan = DataversePlanParser.Parse("""
            WITH crm_account AS DATAVERSE (
                SELECT accountid, name FROM account
                WHERE accountid IN {{SELECT DISTINCT CAST(account_id AS UUID) FROM logs WHERE channel='webchat'}}
            ),
            crm_contact AS DATAVERSE (
                SELECT contactid, fullname, parentcustomerid FROM contact
                WHERE parentcustomerid IN {{SELECT accountid FROM crm_account}}
            )
            SELECT a.name, count(DISTINCT c.contactid) AS contacts
            FROM crm_account a
            JOIN crm_contact c ON c.parentcustomerid = a.accountid
            GROUP BY a.name ORDER BY a.name
            """);

        var counts = plan.Steps.Select(step => cache.Cache(step.Body, step.Name).RowCount).ToList();

        Assert.Equal([2, 4], counts);

        using var rows = cache.Query(plan.FinalSql);

        Assert.True(rows.Read());
        Assert.Equal("Account 3", rows.GetString(0));
        Assert.Equal(2L, rows.GetInt64(1));
        Assert.True(rows.Read());
        Assert.Equal("Account 8", rows.GetString(0));
        Assert.False(rows.Read());
    }
}
