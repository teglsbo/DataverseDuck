using System.Data;
using System.Data.Common;
using DataverseDuck;
using DataverseDuck.Plans;

namespace DataverseDuck.Tests;

/// <summary>
/// The manifest exists so a reused .duckdb file can be trusted. These tests
/// hold it to that: it must say what a table is, must not survive a load that
/// rolled back, and must mark a {{ }} table as partial.
/// </summary>
public class CacheManifestTests
{
    private static readonly Guid AcmeId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ContosoId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static TwoTableSource Source() => new()
    {
        Accounts = new Dictionary<Guid, string>
        {
            [AcmeId] = "Acme",
            [ContosoId] = "Contoso",
        },
        Contacts = new Dictionary<Guid, (string, Guid)>
        {
            [Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001")] = ("Ann", AcmeId),
            [Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002")] = ("Bob", ContosoId),
        },
    };

    [Fact]
    public void Records_what_a_cached_table_is()
    {
        using var connection = UtcTimestampPolicy.OpenConnection("Data Source=:memory:");
        var cache = new DataverseCache(connection, new AllAccountsSource());

        cache.Cache("SELECT accountid, name FROM account", "crm_account");

        var entry = Assert.Single(CacheManifest.Read(connection));

        Assert.Equal("crm_account", entry.Name);
        Assert.Equal(PlanStepKind.Dataverse, entry.Kind);
        Assert.Equal("SELECT accountid, name FROM account", entry.Source);
        Assert.Equal(2, entry.RowCount);
        Assert.Null(entry.KeyCount);
        Assert.False(entry.IsPartial);
    }

    [Fact]
    public void Marks_a_key_filtered_table_as_partial()
    {
        using var connection = UtcTimestampPolicy.OpenConnection("Data Source=:memory:");
        var cache = new DataverseCache(connection, Source());

        Execute(connection, "CREATE TABLE seen (id UUID)");
        Execute(connection, $"INSERT INTO seen VALUES ('{AcmeId}')");

        cache.Cache(
            "SELECT accountid, name FROM account WHERE accountid IN {{SELECT id FROM seen}}",
            "crm_account");

        var entry = Assert.Single(CacheManifest.Read(connection), e => e.Name == "crm_account");

        // The distinction the manifest exists for: this table looks exactly
        // like a full copy of account, but holds one row on purpose.
        Assert.True(entry.IsPartial);
        Assert.Equal(1, entry.KeyCount);
        Assert.Equal(1, entry.RowCount);
        Assert.Contains("{{", entry.Source);
    }

    [Fact]
    public void Records_a_json_view_with_its_path()
    {
        var path = Path.Combine(Path.GetTempPath(), $"manifest-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """[{"id": 1}]""");

        try
        {
            using var connection = UtcTimestampPolicy.OpenConnection("Data Source=:memory:");
            var cache = new DataverseCache(connection, Source());

            cache.RegisterJson("logs", path);

            var entry = Assert.Single(CacheManifest.Read(connection));

            Assert.Equal(PlanStepKind.Json, entry.Kind);
            Assert.Equal(path, entry.Source);
            Assert.Null(entry.RowCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Replaces_the_entry_when_a_table_is_refetched()
    {
        using var connection = UtcTimestampPolicy.OpenConnection("Data Source=:memory:");
        var cache = new DataverseCache(connection, new AllAccountsSource());

        cache.Cache("SELECT accountid, name FROM account", "crm_account");
        cache.Cache("SELECT accountid FROM account", "crm_account");

        var entry = Assert.Single(CacheManifest.Read(connection));
        Assert.Equal("SELECT accountid FROM account", entry.Source);
    }

    [Fact]
    public void Does_not_record_a_load_that_failed()
    {
        using var connection = UtcTimestampPolicy.OpenConnection("Data Source=:memory:");
        var cache = new DataverseCache(connection, new ThrowingSource());

        Assert.ThrowsAny<Exception>(() =>
            cache.Cache("SELECT accountid FROM account", "crm_account"));

        // A manifest row describing rows that were rolled back would be worse
        // than no manifest at all: it would read as a successful load.
        Assert.Empty(CacheManifest.Read(connection));
    }

    [Fact]
    public void Survives_being_written_to_a_file_and_reopened()
    {
        var path = Path.Combine(Path.GetTempPath(), $"manifest-{Guid.NewGuid():N}.duckdb");

        try
        {
            using (var connection = UtcTimestampPolicy.OpenConnection($"Data Source={path}"))
            {
                new DataverseCache(connection, new AllAccountsSource())
                    .Cache("SELECT accountid, name FROM account", "crm_account");
            }

            using var reopened = UtcTimestampPolicy.OpenConnection($"Data Source={path}");
            var entry = Assert.Single(CacheManifest.Read(reopened));

            Assert.Equal("crm_account", entry.Name);
            Assert.Equal(2, entry.RowCount);
            Assert.True(entry.Age < TimeSpan.FromMinutes(1));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void Reads_as_empty_when_the_cache_has_no_manifest()
    {
        using var connection = UtcTimestampPolicy.OpenConnection("Data Source=:memory:");

        Assert.False(CacheManifest.Exists(connection));
        Assert.Empty(CacheManifest.Read(connection));
    }

    [Theory]
    [InlineData(30, "30s")]
    [InlineData(90, "2m")]
    [InlineData(7200, "2h")]
    [InlineData(172800, "2d")]
    public void Describes_age_at_the_scale_that_matters(int seconds, string expected) =>
        Assert.Equal(expected, CacheEntry.Humanise(TimeSpan.FromSeconds(seconds)));

    private static void Execute(DuckDB.NET.Data.DuckDBConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>Returns every account regardless of filter, for full-fetch tests.</summary>
    private sealed class AllAccountsSource : IDataverseQuerySource
    {
        public PlanAnalysis? Analyze(string sql) => null;

        public T Query<T>(string sql, Func<DbDataReader, T> read)
        {
            var table = new DataTable();
            table.Columns.Add("accountid", typeof(Guid));
            table.Columns.Add("name", typeof(string));
            table.Rows.Add(AcmeId, "Acme");
            table.Rows.Add(ContosoId, "Contoso");

            using var reader = table.CreateDataReader();
            return read(reader);
        }
    }

    private sealed class ThrowingSource : IDataverseQuerySource
    {
        public PlanAnalysis? Analyze(string sql) => null;

        public T Query<T>(string sql, Func<DbDataReader, T> read)
        {
            var table = new DataTable();
            table.Columns.Add("accountid", typeof(Guid));
            table.Rows.Add(Guid.NewGuid());

            using var reader = table.CreateDataReader();
            read(reader);

            throw new InvalidOperationException("connection dropped mid-load");
        }
    }
}
