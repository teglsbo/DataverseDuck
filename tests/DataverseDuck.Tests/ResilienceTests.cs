using System.Data;
using System.Data.Common;
using System.ServiceModel;
using DataverseDuck.Schema;
using Microsoft.Xrm.Sdk;

namespace DataverseDuck.Tests;

/// <summary>
/// Fails partway through, the way a dropped connection or a service protection
/// error that outlived the SDK's retries does.
/// </summary>
internal sealed class FailingReader(DataTable table, int failAfter, Exception failure) : DbDataReader
{
    private readonly DbDataReader _inner = table.CreateDataReader();
    private int _read;

    public override bool Read()
    {
        if (_read == failAfter) throw failure;
        _read++;
        return _inner.Read();
    }

    public override int FieldCount => _inner.FieldCount;
    public override string GetName(int i) => _inner.GetName(i);
    public override Type GetFieldType(int i) => _inner.GetFieldType(i);
    public override object GetValue(int i) => _inner.GetValue(i);
    public override int GetValues(object[] values) => _inner.GetValues(values);
    public override bool IsDBNull(int i) => _inner.IsDBNull(i);
    public override string GetDataTypeName(int i) => _inner.GetDataTypeName(i);
    public override int GetOrdinal(string name) => _inner.GetOrdinal(name);
    public override object this[int i] => _inner[i];
    public override object this[string name] => _inner[name];
    public override bool HasRows => _inner.HasRows;
    public override int Depth => 0;
    public override bool IsClosed => _inner.IsClosed;
    public override int RecordsAffected => 0;
    public override bool NextResult() => _inner.NextResult();
    public override System.Collections.IEnumerator GetEnumerator() => throw new NotSupportedException();
    public override bool GetBoolean(int i) => _inner.GetBoolean(i);
    public override byte GetByte(int i) => _inner.GetByte(i);
    public override long GetBytes(int i, long o, byte[]? b, int bo, int l) => _inner.GetBytes(i, o, b, bo, l);
    public override char GetChar(int i) => _inner.GetChar(i);
    public override long GetChars(int i, long o, char[]? b, int bo, int l) => _inner.GetChars(i, o, b, bo, l);
    public override DateTime GetDateTime(int i) => _inner.GetDateTime(i);
    public override decimal GetDecimal(int i) => _inner.GetDecimal(i);
    public override double GetDouble(int i) => _inner.GetDouble(i);
    public override float GetFloat(int i) => _inner.GetFloat(i);
    public override Guid GetGuid(int i) => _inner.GetGuid(i);
    public override short GetInt16(int i) => _inner.GetInt16(i);
    public override int GetInt32(int i) => _inner.GetInt32(i);
    public override long GetInt64(int i) => _inner.GetInt64(i);
    public override string GetString(int i) => _inner.GetString(i);
}

public class ResilienceTests : IDisposable
{
    private readonly DuckDB.NET.Data.DuckDBConnection _connection =
        UtcTimestampPolicy.OpenConnection("Data Source=:memory:");

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private static DataTable Rows(int count, string note = "row")
    {
        var table = new DataTable();
        table.Columns.Add("id", typeof(int));
        table.Columns.Add("note", typeof(string));

        for (var i = 0; i < count; i++)
            table.Rows.Add(i, note);

        return table;
    }

    private long Count(string table)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM {table}";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static FaultException<OrganizationServiceFault> Throttle(int errorCode, TimeSpan? retryAfter = null)
    {
        var fault = new OrganizationServiceFault { ErrorCode = errorCode };

        if (retryAfter is { } wait)
            fault.ErrorDetails.Add(DataverseThrottling.RetryAfterKey, wait);

        return new FaultException<OrganizationServiceFault>(fault, new FaultReason("throttled"));
    }

    [Fact]
    public void A_load_that_dies_partway_leaves_no_table_behind()
    {
        var loader = new DuckDbBulkLoader(_connection);

        Assert.Throws<TimeoutException>(() => loader.Load(
            new FailingReader(Rows(1000), 600, new TimeoutException("dropped")), "crm_account"));

        // The dangerous outcome would be a queryable table holding 600 rows.
        var missing = Assert.ThrowsAny<Exception>(() => Count("crm_account"));
        Assert.Contains("crm_account", missing.Message);
    }

    [Fact]
    public void A_failed_reload_does_not_destroy_the_previous_copy()
    {
        // The worse half of the bug: retrying a failed load used to leave you
        // with less than you started with.
        var loader = new DuckDbBulkLoader(_connection);

        loader.Load(Rows(300, "good").CreateDataReader(), "crm_account");
        Assert.Equal(300, Count("crm_account"));

        Assert.Throws<TimeoutException>(() => loader.Load(
            new FailingReader(Rows(1000, "partial"), 600, new TimeoutException("dropped")), "crm_account"));

        Assert.Equal(300, Count("crm_account"));

        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT note FROM crm_account";
        Assert.Equal("good", command.ExecuteScalar());
    }

    [Fact]
    public void A_successful_reload_still_replaces_the_previous_copy()
    {
        var loader = new DuckDbBulkLoader(_connection);

        loader.Load(Rows(300, "old").CreateDataReader(), "crm_account");
        loader.Load(Rows(5, "new").CreateDataReader(), "crm_account");

        Assert.Equal(5, Count("crm_account"));
    }

    [Fact]
    public void Cancellation_rolls_the_load_back()
    {
        using var cancellation = new CancellationTokenSource();
        var table = Rows(1000);

        var loader = new DuckDbBulkLoader(_connection) { ProgressInterval = 100 };

        Assert.ThrowsAny<OperationCanceledException>(() => loader.Load(
            table.CreateDataReader(), "crm_account",
            progress: _ => cancellation.Cancel(),
            cancellationToken: cancellation.Token));

        Assert.ThrowsAny<Exception>(() => Count("crm_account"));
    }

    [Theory]
    [InlineData(DataverseThrottling.RequestCountErrorCode, ThrottleKind.RequestCount)]
    [InlineData(DataverseThrottling.ExecutionTimeErrorCode, ThrottleKind.ExecutionTime)]
    [InlineData(DataverseThrottling.ConcurrencyErrorCode, ThrottleKind.Concurrency)]
    public void The_three_service_protection_limits_are_told_apart(int errorCode, ThrottleKind expected)
    {
        // They have different remedies, which is the only reason to distinguish them.
        Assert.Equal(expected, DataverseThrottling.Classify(Throttle(errorCode)));
    }

    [Fact]
    public void An_unrelated_fault_is_not_mistaken_for_throttling()
    {
        Assert.Equal(ThrottleKind.None, DataverseThrottling.Classify(Throttle(-2147220969)));
        Assert.Equal(ThrottleKind.None, DataverseThrottling.Classify(new TimeoutException()));
        Assert.Equal(ThrottleKind.None, DataverseThrottling.Classify(null));
        Assert.Null(DataverseThrottling.Explain(new InvalidOperationException()));
    }

    [Fact]
    public void A_throttle_wrapped_by_the_appender_loop_is_still_recognised()
    {
        // Faults raised mid-stream arrive wrapped, so classification must unwrap.
        var wrapped = new InvalidOperationException("load failed",
            new AggregateException(Throttle(DataverseThrottling.ExecutionTimeErrorCode)));

        Assert.Equal(ThrottleKind.ExecutionTime, DataverseThrottling.Classify(wrapped));
    }

    [Fact]
    public void The_servers_requested_wait_is_surfaced()
    {
        var fault = Throttle(DataverseThrottling.RequestCountErrorCode, TimeSpan.FromSeconds(42));

        Assert.Equal(TimeSpan.FromSeconds(42), DataverseThrottling.RetryAfter(fault));
        Assert.Contains("42s", DataverseThrottling.Explain(fault));
    }

    [Fact]
    public void A_missing_retry_after_is_not_invented()
    {
        var fault = Throttle(DataverseThrottling.RequestCountErrorCode);

        Assert.Null(DataverseThrottling.RetryAfter(fault));
        Assert.DoesNotContain("retrying", DataverseThrottling.Explain(fault)!);
    }

    [Fact]
    public void Each_limit_is_explained_with_its_own_remedy()
    {
        // A shared message would defeat the purpose of classifying them.
        var explanations = new[]
        {
            DataverseThrottling.RequestCountErrorCode,
            DataverseThrottling.ExecutionTimeErrorCode,
            DataverseThrottling.ConcurrencyErrorCode,
        }.Select(code => DataverseThrottling.Explain(Throttle(code))!).ToArray();

        Assert.All(explanations, e => Assert.False(string.IsNullOrWhiteSpace(e)));
        Assert.Equal(3, explanations.Distinct().Count());
        Assert.Contains("MaxDegreeOfParallelism",
            DataverseThrottling.Explain(Throttle(DataverseThrottling.ConcurrencyErrorCode)));
    }

    [Fact]
    public void A_throttled_cache_reports_the_limit_and_keeps_the_old_table()
    {
        var loader = new DuckDbBulkLoader(_connection);
        loader.Load(Rows(300, "good").CreateDataReader(), "crm_account");

        var cache = new DataverseCache(
            _connection,
            new ThrowingQuerySource(new FailingReader(
                Rows(1000, "partial"), 600,
                Throttle(DataverseThrottling.ExecutionTimeErrorCode, TimeSpan.FromSeconds(30)))));

        var error = Assert.Throws<DataverseThrottledException>(
            () => cache.Cache("SELECT id FROM account", "crm_account"));

        Assert.Equal(ThrottleKind.ExecutionTime, error.Kind);
        Assert.Equal(TimeSpan.FromSeconds(30), error.RetryAfter);
        Assert.Equal(300, Count("crm_account"));
    }

    [Fact]
    public void Bulk_export_settings_exceed_the_sdk_defaults()
    {
        // The SDK's four-minute per-request ceiling is the one most likely to
        // bite a wide page over a large table.
        Assert.True(Sql4CdsConnectionFactory.BulkExportTimeout > TimeSpan.FromMinutes(4));
        Assert.True(Sql4CdsConnectionFactory.BulkExportRetryCount > 0);
        Assert.True(Sql4CdsConnectionFactory.BulkExportRetryPause > TimeSpan.Zero);
    }
}

internal sealed class ThrowingQuerySource(DbDataReader reader) : IDataverseQuerySource
{
    public Plans.PlanAnalysis? Analyze(string sql) => null;

    public T Query<T>(string sql, Func<DbDataReader, T> read) => read(reader);
}
