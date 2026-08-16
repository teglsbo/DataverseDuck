using System.Data;
using System.Data.Common;
using DataverseDuck;
using DataverseDuck.Plans;

namespace DataverseDuck.Tests;

/// <summary>
/// Serves rows only for the keys a query actually asks for, so a test can
/// assert on what was fetched rather than merely what came back.
/// </summary>
internal sealed class KeyedQuerySource(IReadOnlyDictionary<Guid, string> contacts) : IDataverseQuerySource
{
    public List<string> ExecutedSql { get; } = [];

    public PlanAnalysis? Analyze(string sql) => null;

    /// <summary>Every key Dataverse was asked about, across all batches.</summary>
    public List<Guid> RequestedKeys { get; } = [];

    public T Query<T>(string sql, Func<DbDataReader, T> read)
    {
        ExecutedSql.Add(sql);

        var table = new DataTable();
        table.Columns.Add("contactid", typeof(Guid));
        table.Columns.Add("fullname", typeof(string));

        foreach (var key in ParseKeys(sql))
        {
            RequestedKeys.Add(key);

            if (contacts.TryGetValue(key, out var name))
                table.Rows.Add(key, name);
        }

        using var reader = table.CreateDataReader();
        return read(reader);
    }

    private static IEnumerable<Guid> ParseKeys(string sql)
    {
        var open = sql.LastIndexOf('(');
        var close = sql.LastIndexOf(')');

        if (open < 0 || close < open) yield break;

        foreach (var part in sql[(open + 1)..close].Split(','))
        {
            if (Guid.TryParse(part.Trim().Trim('\''), out var key))
                yield return key;
        }
    }
}

public class KeySetPushdownTests : IDisposable
{
    private readonly DuckDB.NET.Data.DuckDBConnection _connection =
        UtcTimestampPolicy.OpenConnection("Data Source=:memory:");

    private readonly List<string> _files = [];

    public void Dispose()
    {
        _connection.Dispose();
        foreach (var file in _files) File.Delete(file);
        GC.SuppressFinalize(this);
    }

    private string Write(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dvduck-k-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        _files.Add(path);
        return path;
    }

    private static Guid Key(int n) => Guid.Parse($"aaaaaaaa-0000-0000-0000-{n:D12}");

    [Fact]
    public void Only_the_contacts_the_logs_mention_are_fetched()
    {
        // The webchat question: a huge contact table, a handful of relevant ids.
        var contacts = Enumerable.Range(1, 5000).ToDictionary(Key, n => $"Contact {n}");
        var source = new KeyedQuerySource(contacts);

        var cache = new DataverseCache(_connection, source);

        cache.RegisterJson("logs", Write($$"""
            [{"customer_id":"{{Key(7)}}","channel":"webchat"},
             {"customer_id":"{{Key(7)}}","channel":"webchat"},
             {"customer_id":"{{Key(9)}}","channel":"webchat"},
             {"customer_id":"{{Key(11)}}","channel":"branch"}]
            """));

        var result = cache.Cache("""
            SELECT contactid, fullname FROM contact
            WHERE contactid IN {{SELECT CAST(customer_id AS UUID) FROM logs WHERE channel = 'webchat'}}
            """, "crm_contact");

        // Two distinct webchat contacts, out of 5,000 in Dataverse.
        Assert.Equal(2, result.RowCount);
        Assert.Equal([Key(7), Key(9)], source.RequestedKeys.Order());

        using var rows = cache.Query("""
            SELECT count(DISTINCT c.contactid)
            FROM logs l JOIN crm_contact c ON c.contactid = CAST(l.customer_id AS UUID)
            WHERE l.channel = 'webchat'
            """);

        Assert.True(rows.Read());
        Assert.Equal(2L, Convert.ToInt64(rows.GetValue(0)));
    }

    [Fact]
    public void Duplicate_keys_are_asked_for_once()
    {
        // Logs have one row per event, so duplicates are the norm; each one
        // would otherwise waste a slot in the IN list.
        var source = new KeyedQuerySource(new Dictionary<Guid, string> { [Key(1)] = "A" });
        var cache = new DataverseCache(_connection, source);

        cache.RegisterJson("logs", Write($$"""
            [{"id":"{{Key(1)}}"},{"id":"{{Key(1)}}"},{"id":"{{Key(1)}}"}]
            """));

        cache.Cache(
            "SELECT contactid, fullname FROM contact WHERE contactid IN {{SELECT CAST(id AS UUID) FROM logs}}",
            "crm_contact");

        Assert.Equal([Key(1)], source.RequestedKeys);
    }

    [Fact]
    public void Keys_are_sent_in_batches()
    {
        var contacts = Enumerable.Range(1, 1200).ToDictionary(Key, n => $"C{n}");
        var source = new KeyedQuerySource(contacts);

        var cache = new DataverseCache(_connection, source) { Pushdown = new KeySetPushdown { BatchSize = 500 } };

        using (var seed = _connection.CreateCommand())
        {
            seed.CommandText = "CREATE TABLE ids AS SELECT * FROM (VALUES " +
                string.Join(",", contacts.Keys.Select(k => $"('{k}'::UUID)")) + ") t(id)";
            seed.ExecuteNonQuery();
        }

        var result = cache.Cache(
            "SELECT contactid, fullname FROM contact WHERE contactid IN {{SELECT id FROM ids}}",
            "crm_contact");

        Assert.Equal(1200, result.RowCount);
        Assert.Equal(3, source.ExecutedSql.Count);
        Assert.Equal(1200, source.RequestedKeys.Distinct().Count());
    }

    [Fact]
    public void All_batches_commit_together_or_not_at_all()
    {
        var contacts = Enumerable.Range(1, 600).ToDictionary(Key, n => $"C{n}");
        var cache = new DataverseCache(_connection, new FailOnSecondBatchSource(contacts))
        {
            Pushdown = new KeySetPushdown { BatchSize = 500 },
        };

        using (var seed = _connection.CreateCommand())
        {
            seed.CommandText = "CREATE TABLE ids AS SELECT * FROM (VALUES " +
                string.Join(",", contacts.Keys.Select(k => $"('{k}'::UUID)")) + ") t(id)";
            seed.ExecuteNonQuery();
        }

        Assert.ThrowsAny<Exception>(() => cache.Cache(
            "SELECT contactid, fullname FROM contact WHERE contactid IN {{SELECT id FROM ids}}",
            "crm_contact"));

        // The first batch's 500 rows must not survive as a plausible-looking table.
        Assert.ThrowsAny<Exception>(() =>
        {
            using var check = _connection.CreateCommand();
            check.CommandText = "SELECT count(*) FROM crm_contact";
            check.ExecuteScalar();
        });
    }

    [Fact]
    public void No_keys_means_Dataverse_is_not_queried_at_all()
    {
        var source = new KeyedQuerySource(new Dictionary<Guid, string>());
        var cache = new DataverseCache(_connection, source);

        cache.RegisterJson("logs", Write("""[{"id":"aaaaaaaa-0000-0000-0000-000000000001","channel":"branch"}]"""));

        var result = cache.Cache(
            "SELECT contactid, fullname FROM contact WHERE contactid IN {{SELECT CAST(id AS UUID) FROM logs WHERE channel='webchat'}}",
            "crm_contact");

        Assert.Equal(0, result.RowCount);

        // One probe for the schema, no batches: an empty table still has to exist
        // or the user's next join becomes an error rather than an empty result.
        using var rows = cache.Query("SELECT count(*) FROM crm_contact");
        Assert.True(rows.Read());
        Assert.Equal(0L, Convert.ToInt64(rows.GetValue(0)));
    }

    [Fact]
    public void A_statement_without_a_marker_is_left_alone()
    {
        Assert.Null(KeySetPushdown.FindKeyQuery("SELECT contactid FROM contact WHERE statecode = 0"));
    }

    [Fact]
    public void The_marker_is_replaced_where_the_user_put_it()
    {
        // Substituting in place keeps it a plain IN over literals, which is the
        // only shape SQL 4 CDS folds into FetchXML.
        var found = KeySetPushdown.FindKeyQuery(
            "SELECT a FROM contact WHERE x IN {{SELECT id FROM logs}} AND statecode = 0");

        Assert.NotNull(found);
        Assert.Equal("SELECT id FROM logs", found.Sql);
        Assert.Equal(
            "SELECT a FROM contact WHERE x IN ('aaaaaaaa-0000-0000-0000-000000000001') AND statecode = 0",
            found.Expand([Key(1)]));
    }

    [Theory]
    [InlineData("SELECT a FROM t WHERE x IN {{SELECT id FROM logs")]
    [InlineData("SELECT a FROM t WHERE x IN {{}}")]
    [InlineData("SELECT a FROM t WHERE x IN {{SELECT a FROM p}} OR y IN {{SELECT b FROM q}}")]
    public void Malformed_markers_are_rejected(string sql)
    {
        Assert.Throws<FormatException>(() => KeySetPushdown.FindKeyQuery(sql));
    }

    [Fact]
    public void A_quote_in_a_key_cannot_break_out_of_the_in_list()
    {
        // Keys must be literals for SQL 4 CDS to fold the IN, so this is an
        // injection boundary rather than a parameterised query.
        var rendered = KeySetPushdown.RenderInList(["o'brien", "plain"]);

        Assert.Equal("('o''brien', 'plain')", rendered);
    }

    [Fact]
    public void Key_types_are_rendered_unambiguously()
    {
        Assert.Equal("(1, 2)", KeySetPushdown.RenderInList([1, 2L]));
        Assert.Equal("('aaaaaaaa-0000-0000-0000-000000000001')", KeySetPushdown.RenderInList([Key(1)]));
        Assert.Equal("(1, 0)", KeySetPushdown.RenderInList([true, false]));
    }

    [Fact]
    public void A_key_type_we_cannot_vouch_for_is_refused_rather_than_guessed()
    {
        var error = Assert.Throws<NotSupportedException>(
            () => KeySetPushdown.RenderInList([new object()]));

        Assert.Contains("CAST", error.Message);
    }

    [Fact]
    public void Nulls_in_the_local_key_query_are_dropped()
    {
        using var seed = _connection.CreateCommand();
        seed.CommandText = "CREATE TABLE ids AS SELECT * FROM (VALUES (1),(NULL),(2)) t(id)";
        seed.ExecuteNonQuery();

        var keys = new KeySetPushdown().ReadKeys(_connection, "SELECT id FROM ids");

        Assert.Equal([1, 2], keys.Select(Convert.ToInt32).Order());
    }

    [Fact]
    public void An_implausibly_large_key_set_is_refused_with_a_reason()
    {
        using var seed = _connection.CreateCommand();
        seed.CommandText = "CREATE TABLE ids AS SELECT range AS id FROM range(50)";
        seed.ExecuteNonQuery();

        var error = Assert.Throws<InvalidOperationException>(
            () => new KeySetPushdown { MaxKeys = 10 }.ReadKeys(_connection, "SELECT id FROM ids"));

        // Past some size, filtering server-side beats sending keys back.
        Assert.Contains("server-side filter", error.Message);
    }

    [Fact]
    public void Batching_covers_every_key_exactly_once()
    {
        var keys = Enumerable.Range(0, 1201).Cast<object>().ToList();
        var batches = new KeySetPushdown { BatchSize = 500 }.Batch(keys).ToList();

        Assert.Equal([500, 500, 201], batches.Select(b => b.Count));
        Assert.Equal(keys, batches.SelectMany(b => b));
    }
}

internal sealed class FailOnSecondBatchSource(IReadOnlyDictionary<Guid, string> contacts) : IDataverseQuerySource
{
    private int _calls;

    public PlanAnalysis? Analyze(string sql) => null;

    public T Query<T>(string sql, Func<DbDataReader, T> read)
    {
        if (++_calls == 2)
            throw new TimeoutException("dropped on the second batch");

        var table = new DataTable();
        table.Columns.Add("contactid", typeof(Guid));
        table.Columns.Add("fullname", typeof(string));

        foreach (var (key, name) in contacts.Take(500))
            table.Rows.Add(key, name);

        using var reader = table.CreateDataReader();
        return read(reader);
    }
}

/// <summary>
/// Serves two Dataverse tables, recording how many rows each query was asked
/// to consider, so a test can prove the narrowing is real.
/// </summary>
internal sealed class TwoTableSource : IDataverseQuerySource
{
    public required IReadOnlyDictionary<Guid, string> Accounts { get; init; }

    /// <summary>contact id -> (name, parent account id)</summary>
    public required IReadOnlyDictionary<Guid, (string Name, Guid Parent)> Contacts { get; init; }

    public List<string> ExecutedSql { get; } = [];

    public PlanAnalysis? Analyze(string sql) => null;

    public T Query<T>(string sql, Func<DbDataReader, T> read)
    {
        ExecutedSql.Add(sql);

        var keys = ParseKeys(sql).ToHashSet();
        var table = new DataTable();

        if (sql.Contains("FROM account", StringComparison.OrdinalIgnoreCase))
        {
            table.Columns.Add("accountid", typeof(Guid));
            table.Columns.Add("name", typeof(string));

            foreach (var key in keys.Where(Accounts.ContainsKey))
                table.Rows.Add(key, Accounts[key]);
        }
        else
        {
            table.Columns.Add("contactid", typeof(Guid));
            table.Columns.Add("fullname", typeof(string));
            table.Columns.Add("parentcustomerid", typeof(Guid));

            foreach (var (id, contact) in Contacts.Where(c => keys.Contains(c.Value.Parent)))
                table.Rows.Add(id, contact.Name, contact.Parent);
        }

        using var reader = table.CreateDataReader();
        return read(reader);
    }

    private static IEnumerable<Guid> ParseKeys(string sql)
    {
        var open = sql.LastIndexOf('(');
        var close = sql.LastIndexOf(')');
        if (open < 0 || close < open) yield break;

        foreach (var part in sql[(open + 1)..close].Split(','))
            if (Guid.TryParse(part.Trim().Trim('\''), out var key))
                yield return key;
    }
}

public class ChainedPushdownTests : IDisposable
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
    public void A_second_hop_can_key_off_the_table_the_first_hop_cached()
    {
        // The realistic shape: logs name accounts, but you want their contacts.
        // Because {{ }} runs against DuckDB, and a cached Dataverse table lives
        // in DuckDB, hops chain -- each one fetching only what the last found.
        var accounts = Enumerable.Range(1, 5000).ToDictionary(Acc, n => $"Account {n}");

        var contacts = Enumerable.Range(1, 5000)
            .SelectMany(n => new[] { (Con(n * 2), $"Contact {n}a", Acc(n)), (Con(n * 2 + 1), $"Contact {n}b", Acc(n)) })
            .ToDictionary(x => x.Item1, x => (x.Item2, x.Item3));

        var source = new TwoTableSource { Accounts = accounts, Contacts = contacts };
        var cache = new DataverseCache(_connection, source);

        _file = Path.Combine(Path.GetTempPath(), $"dvduck-chain-{Guid.NewGuid():N}.json");
        File.WriteAllText(_file, $$"""
            [{"account_id":"{{Acc(3)}}","channel":"webchat"},
             {"account_id":"{{Acc(8)}}","channel":"webchat"},
             {"account_id":"{{Acc(9)}}","channel":"branch"}]
            """);

        cache.RegisterJson("logs", _file);

        // Hop 1: only the accounts the logs mention.
        var hop1 = cache.Cache("""
            SELECT accountid, name FROM account
            WHERE accountid IN {{SELECT DISTINCT CAST(account_id AS UUID) FROM logs WHERE channel='webchat'}}
            """, "crm_account");

        Assert.Equal(2, hop1.RowCount);

        // Hop 2: only the contacts of those accounts, keyed off hop 1's table.
        var hop2 = cache.Cache("""
            SELECT contactid, fullname, parentcustomerid FROM contact
            WHERE parentcustomerid IN {{SELECT accountid FROM crm_account}}
            """, "crm_contact");

        Assert.Equal(4, hop2.RowCount);

        // 2 of 5,000 accounts and 4 of 10,000 contacts.
        using var rows = cache.Query("""
            SELECT a.name, count(DISTINCT c.contactid) AS contacts
            FROM crm_account a
            JOIN crm_contact c ON c.parentcustomerid = a.accountid
            GROUP BY a.name ORDER BY a.name
            """);

        Assert.True(rows.Read());
        Assert.Equal("Account 3", rows.GetString(0));
        Assert.Equal(2L, rows.GetInt64(1));
        Assert.True(rows.Read());
        Assert.Equal("Account 8", rows.GetString(0));
        Assert.False(rows.Read());
    }
}
