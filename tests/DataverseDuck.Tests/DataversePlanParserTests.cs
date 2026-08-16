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

        Assert.Equal(["crm_account", "crm_contact"], plan.Steps.Select(s => s.Table));
        Assert.StartsWith("SELECT accountid", plan.Steps[0].Sql);
        Assert.Contains("{{SELECT accountid FROM crm_account}}", plan.Steps[1].Sql);
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
        Assert.Equal("crm_contact", plan.Steps[0].Table);
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
        Assert.Contains("'Bob (Robert'", plan.Steps[0].Sql);
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

        Assert.Contains("'O''Brien (x)'", plan.Steps[0].Sql);
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

        Assert.Contains("IN (0, (1))", plan.Steps[0].Sql);
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

        Assert.Contains("fetched later", error.Message);
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

        Assert.Contains("nothing to fetch", error.Message);
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
    public void AcceptsQuotedNames()
    {
        var plan = DataversePlanParser.Parse("""
            WITH "crm contact" AS DATAVERSE (SELECT contactid FROM contact)
            SELECT 1
            """);

        Assert.Equal("crm contact", plan.Steps[0].Table);
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

        var counts = plan.Steps.Select(step => cache.Cache(step.Sql, step.Table).RowCount).ToList();

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
