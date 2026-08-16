using System.Data;
using System.Data.Common;
using DataverseDuck;
using DataverseDuck.Plans;
using DataverseDuck.Schema;
using Microsoft.Xrm.Sdk;

namespace DataverseDuck.Tests;

/// <summary>
/// Stands in for Dataverse. Produces the CLR types SQL 4 CDS produces, so
/// everything downstream of the source is the real code path.
/// </summary>
internal sealed class FakeQuerySource(DataTable table, PlanAnalysis? plan = null) : IDataverseQuerySource
{
    public List<string> ExecutedSql { get; } = [];
    public bool Disposed { get; private set; }

    public PlanAnalysis? Analyze(string sql) => plan;

    public T Query<T>(string sql, Func<DbDataReader, T> read)
    {
        ExecutedSql.Add(sql);
        using var reader = table.CreateDataReader();
        var result = read(reader);
        Disposed = true;
        return result;
    }
}

public class DataverseCacheTests
{
    private static DataTable Accounts()
    {
        var table = new DataTable();
        table.Columns.Add("accountid", typeof(Guid));
        table.Columns.Add("name", typeof(string));
        table.Columns.Add("revenue", typeof(decimal));
        table.Columns.Add("createdon", typeof(DateTime));
        table.Columns.Add("primarycontactid", typeof(EntityReference));

        table.Rows.Add(
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), "Fourth Coffee", 5000m,
            new DateTime(2026, 8, 1, 9, 0, 0, DateTimeKind.Utc),
            new EntityReference("contact", Guid.NewGuid()));

        table.Rows.Add(
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002"), "Contoso Ltd", 250m,
            new DateTime(2026, 8, 2, 9, 0, 0, DateTimeKind.Utc),
            DBNull.Value);

        return table;
    }

    private static DataverseCache NewCache(
        IDataverseQuerySource source,
        FoldingPolicy policy = FoldingPolicy.Warn,
        Action<string>? log = null) =>
        new(UtcTimestampPolicy.OpenConnection("Data Source=:memory:"), source)
        {
            FoldingPolicy = policy,
            Log = log,
        };

    private static string WriteJson(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dvduck-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Json_logs_join_cached_dataverse_rows()
    {
        // The goal of the whole project, exercised end to end: everything from
        // the query source down is production code.
        var source = new FakeQuerySource(Accounts());
        var cache = NewCache(source);

        var result = cache.Cache("SELECT accountid, name FROM account", "crm_account");
        Assert.Equal(2, result.RowCount);

        var json = WriteJson("""
            [{"account_id":"aaaaaaaa-0000-0000-0000-000000000001","level":"error","ts":"2026-08-16T12:00:00+02:00"},
             {"account_id":"aaaaaaaa-0000-0000-0000-000000000001","level":"error","ts":"2026-08-16T13:00:00+02:00"},
             {"account_id":"aaaaaaaa-0000-0000-0000-000000000001","level":"info","ts":"2026-08-16T14:00:00+02:00"},
             {"account_id":"aaaaaaaa-0000-0000-0000-000000000002","level":"error","ts":"2026-08-16T15:00:00+02:00"}]
            """);

        try
        {
            cache.RegisterJson("logs", json);

            using var rows = cache.Query("""
                SELECT a.name, count(*) AS errors, min(l.ts) AS first_error
                FROM logs l
                JOIN crm_account a ON a.accountid = CAST(l.account_id AS UUID)
                WHERE l.level = 'error'
                GROUP BY a.name
                ORDER BY errors DESC, a.name
                """);

            Assert.True(rows.Read());
            Assert.Equal("Fourth Coffee", rows.GetString(0));
            Assert.Equal(2L, rows.GetInt64(1));

            // read_json_auto applies the offset: 12:00+02:00 is 10:00 UTC.
            Assert.Equal(new DateTime(2026, 8, 16, 10, 0, 0), rows.GetDateTime(2));

            Assert.True(rows.Read());
            Assert.Equal("Contoso Ltd", rows.GetString(0));
            Assert.Equal(1L, rows.GetInt64(1));

            Assert.False(rows.Read());
        }
        finally
        {
            File.Delete(json);
        }
    }

    [Fact]
    public void A_critical_plan_is_refused_before_any_rows_are_fetched()
    {
        // The point of pre-flight analysis: nothing should be executed.
        var plan = new PlanAnalysis(
            [new PlanFinding(PlanOperation.ClientSideJoin, PlanSeverity.Critical, "HashJoinNode", 2_000_000,
                "did not fold")],
            [], 2_000_000);

        var source = new FakeQuerySource(Accounts(), plan);
        var cache = NewCache(source, FoldingPolicy.RejectCritical);

        var error = Assert.Throws<PlanNotFoldedException>(
            () => cache.Cache("SELECT ... a huge join", "crm_account"));

        Assert.Contains("HashJoinNode", error.Message);
        Assert.Empty(source.ExecutedSql);
    }

    [Fact]
    public void A_warning_plan_still_runs_but_is_logged()
    {
        var plan = new PlanAnalysis(
            [new PlanFinding(PlanOperation.ClientSideFilter, PlanSeverity.Warning, "FilterNode", 40, "no fold")],
            [], 40);

        var messages = new List<string>();
        var source = new FakeQuerySource(Accounts(), plan);
        var cache = NewCache(source, FoldingPolicy.Warn, messages.Add);

        var result = cache.Cache("SELECT accountid FROM account", "crm_account");

        Assert.Equal(2, result.RowCount);
        Assert.Contains(messages, m => m.Contains("FilterNode"));
        Assert.Same(plan, result.Plan);
    }

    [Fact]
    public void A_fully_folded_plan_logs_nothing()
    {
        var plan = new PlanAnalysis(
            [new PlanFinding(PlanOperation.ServerSideScan, PlanSeverity.Info, "FetchXmlScan", 2, "fetchxml")],
            ["<fetch />"], 2);

        var messages = new List<string>();
        NewCache(new FakeQuerySource(Accounts(), plan), FoldingPolicy.Warn, messages.Add)
            .Cache("SELECT accountid FROM account", "crm_account");

        Assert.Empty(messages);
    }

    [Fact]
    public void A_source_that_cannot_produce_a_plan_is_not_blocked()
    {
        var result = NewCache(new FakeQuerySource(Accounts()), FoldingPolicy.RequireFullFolding)
            .Cache("SELECT accountid FROM account", "crm_account");

        // No plan is not the same as a bad plan.
        Assert.Null(result.Plan);
        Assert.Equal(2, result.RowCount);
    }

    [Fact]
    public void The_result_reports_what_happened()
    {
        var result = NewCache(new FakeQuerySource(Accounts()))
            .Cache("SELECT accountid FROM account", "crm_account");

        Assert.Equal("crm_account", result.TableName);
        Assert.Equal(2, result.RowCount);
        Assert.Contains("crm_account", result.Mapping.TableName);
        Assert.True(result.Elapsed >= TimeSpan.Zero);
        Assert.Contains("2 rows", result.ToString());
    }

    [Fact]
    public void The_reader_is_released_once_the_load_finishes()
    {
        var source = new FakeQuerySource(Accounts());
        NewCache(source).Cache("SELECT accountid FROM account", "crm_account");

        // Scoped Query rather than a returned reader, so this is deterministic.
        Assert.True(source.Disposed);
    }

    [Fact]
    public void Caching_twice_replaces_rather_than_appends()
    {
        var cache = NewCache(new FakeQuerySource(Accounts()));

        cache.Cache("SELECT accountid FROM account", "crm_account");
        var second = cache.Cache("SELECT accountid FROM account", "crm_account");

        Assert.Equal(2, second.RowCount);

        using var rows = cache.Query("SELECT count(*) FROM crm_account");
        Assert.True(rows.Read());
        Assert.Equal(2L, Convert.ToInt64(rows.GetValue(0)));
    }

    [Fact]
    public void A_json_path_containing_a_quote_cannot_inject_sql()
    {
        var cache = NewCache(new FakeQuerySource(Accounts()));
        cache.Cache("SELECT accountid FROM account", "crm_account");

        // The literal is escaped, so this fails to find a file rather than
        // executing the injected statement.
        Assert.ThrowsAny<Exception>(
            () => cache.RegisterJson("bad", "x'); DROP TABLE crm_account; --"));

        using var rows = cache.Query("SELECT count(*) FROM crm_account");
        Assert.True(rows.Read());
        Assert.Equal(2L, Convert.ToInt64(rows.GetValue(0)));
    }

    [Fact]
    public void A_json_glob_spanning_several_files_is_supported()
    {
        var directory = Directory.CreateTempSubdirectory("dvduck");

        try
        {
            File.WriteAllText(Path.Combine(directory.FullName, "a.json"),
                """[{"account_id":"aaaaaaaa-0000-0000-0000-000000000001","level":"error"}]""");
            File.WriteAllText(Path.Combine(directory.FullName, "b.json"),
                """[{"account_id":"aaaaaaaa-0000-0000-0000-000000000002","level":"error"}]""");

            var cache = NewCache(new FakeQuerySource(Accounts()));
            cache.Cache("SELECT accountid, name FROM account", "crm_account");
            cache.RegisterJson("logs", Path.Combine(directory.FullName, "*.json"));

            using var rows = cache.Query("""
                SELECT count(*) FROM logs l
                JOIN crm_account a ON a.accountid = CAST(l.account_id AS UUID)
                """);

            Assert.True(rows.Read());
            Assert.Equal(2L, Convert.ToInt64(rows.GetValue(0)));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Lookups_are_joinable_after_caching()
    {
        var cache = NewCache(new FakeQuerySource(Accounts()));
        cache.Cache("SELECT accountid, primarycontactid FROM account", "crm_account");

        using var rows = cache.Query("""
            SELECT count(*) FROM crm_account
            WHERE primarycontactid IS NOT NULL AND primarycontactid_entitytype = 'contact'
            """);

        Assert.True(rows.Read());
        Assert.Equal(1L, Convert.ToInt64(rows.GetValue(0)));
    }

    [Fact]
    public void Null_arguments_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(
            () => new DataverseCache(null!, new FakeQuerySource(Accounts())));

        Assert.Throws<ArgumentNullException>(
            () => new DataverseCache(UtcTimestampPolicy.OpenConnection("Data Source=:memory:"), null!));
    }
}
