using System.Data.Common;
using System.Diagnostics;
using DataverseDuck.Plans;
using DataverseDuck.Schema;
using DuckDB.NET.Data;

namespace DataverseDuck;

/// <param name="TableName">DuckDB table the rows landed in.</param>
/// <param name="RowCount">Rows written.</param>
/// <param name="Mapping">Column shape that was created.</param>
/// <param name="Plan">Plan analysis, or null if the source could not produce one.</param>
/// <param name="Elapsed">Wall-clock time for the whole load.</param>
public sealed record CacheResult(
    string TableName,
    long RowCount,
    TableMapping Mapping,
    PlanAnalysis? Plan,
    TimeSpan Elapsed)
{
    /// <summary>
    /// Distinct keys a <c>{{ }}</c> query supplied, or null if there was none.
    /// Non-null means the table holds a subset of the Dataverse table.
    /// </summary>
    public long? KeyCount { get; init; }

    public override string ToString() =>
        $"{TableName}: {RowCount:N0} rows in {Elapsed.TotalSeconds:F1}s" +
        (Plan is { FullyFolded: false } ? $" ({Plan.Problems.Count()} operation(s) ran locally)" : string.Empty);
}

/// <summary>
/// Pulls Dataverse tables into a local DuckDB database so they can be joined
/// against JSON logs and state documents.
///
/// This is the whole point of the project in one class. The pieces it composes
/// each guard a specific failure:
///
/// <list type="bullet">
/// <item><see cref="ExecutionPlanAnalyzer"/> refuses queries that would drag a
/// large table across the network to join it locally (ADR 0004).</item>
/// <item><see cref="DataverseSchemaMapper"/> decomposes lookups and normalises
/// timestamps to naive UTC (ADR 0002).</item>
/// <item><see cref="UtcTimestampPolicy"/> pins the DuckDB session to UTC so
/// comparisons do not depend on the host timezone.</item>
/// </list>
/// </summary>
public sealed class DataverseCache
{
    private readonly IDataverseQuerySource _source;

    /// <param name="connection">
    /// Open DuckDB connection. Use <see cref="UtcTimestampPolicy.OpenConnection"/>
    /// so the session timezone is pinned; the constructor verifies this.
    /// </param>
    /// <param name="source">Where Dataverse rows come from.</param>
    public DataverseCache(DuckDBConnection connection, IDataverseQuerySource source)
    {
        Connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _source = source ?? throw new ArgumentNullException(nameof(source));

        // Cheap to check, and a non-UTC session silently changes query results
        // rather than failing, so it is worth catching at construction.
        UtcTimestampPolicy.ConfigureConnection(connection);
    }

    /// <summary>The DuckDB connection holding the cache. Query it directly.</summary>
    public DuckDBConnection Connection { get; }

    /// <summary>What to do when a query will not run entirely inside Dataverse.</summary>
    public FoldingPolicy FoldingPolicy { get; init; } = FoldingPolicy.Warn;

    /// <summary>Controls lookup decomposition and decimal precision.</summary>
    public DataverseSchemaMapper Mapper { get; init; } = new();

    /// <summary>Receives plan warnings and progress. Defaults to discarding them.</summary>
    public Action<string>? Log { get; init; }

    /// <summary>Controls how local key sets are pushed into Dataverse.</summary>
    public KeySetPushdown Pushdown { get; init; } = new();

    /// <summary>
    /// Runs a query against Dataverse and stores the result as a DuckDB table.
    /// </summary>
    /// <param name="sql">
    /// T-SQL, compiled to FetchXML by SQL 4 CDS.
    ///
    /// May embed one DuckDB query in <c>{{ }}</c> to fetch only the rows a
    /// local table or JSON file refers to:
    ///
    /// <code>
    /// SELECT contactid, fullname FROM contact
    /// WHERE contactid IN {{SELECT DISTINCT customer_id FROM logs WHERE channel = 'webchat'}}
    /// </code>
    ///
    /// The inner query runs locally; its results are inlined as literals and
    /// sent in batches, so a large table is filtered server-side rather than
    /// pulled across and joined here.
    ///
    /// Each batch runs the outer statement in full and appends its rows into
    /// <paramref name="tableName"/>, so a <c>{{ }}</c>-bearing statement must be a
    /// plain row-returning SELECT: DISTINCT, TOP, and GROUP BY would each apply
    /// per batch rather than across the full key set (see
    /// <see cref="KeySetPushdown.ValidateBatchable"/>). Apply those to the cached
    /// result afterwards instead.
    /// </param>
    /// <param name="tableName">Destination table. Replaced if it exists.</param>
    /// <exception cref="PlanNotFoldedException">
    /// The plan does more local work than <see cref="FoldingPolicy"/> permits.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A <c>{{ }}</c>-bearing statement has DISTINCT, TOP, or GROUP BY at its
    /// outermost level.
    /// </exception>
    public CacheResult Cache(string sql, string tableName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        DuckDbIdentifier.Validate(tableName);

        if (KeySetPushdown.FindKeyQuery(sql) is not { } keyQuery)
            return CacheOne(sql, tableName, plan: null, cancellationToken);

        KeySetPushdown.ValidateBatchable(keyQuery);
        return CacheMatching(keyQuery, sql, tableName, cancellationToken);
    }

    /// <summary>
    /// Fetches only the Dataverse rows whose key appears in a local result set.
    ///
    /// DuckDB decides what is needed; Dataverse does the filtering. Neither
    /// side sends the other more than it must.
    /// </summary>
    private CacheResult CacheMatching(
        LocalKeyQuery keyQuery,
        string originalSql,
        string tableName,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var keys = Pushdown.ReadKeys(Connection, keyQuery.Sql);

        Log?.Invoke($"{tableName}: {keys.Count:N0} distinct key(s) from the local query");

        var loader = new DuckDbBulkLoader(Connection);
        using var transaction = Connection.BeginTransaction();

        TableMapping? mapping = null;
        var rows = 0L;
        PlanAnalysis? firstPlan = null;

        if (keys.Count == 0)
        {
            // Nothing local refers to Dataverse, so there is nothing to fetch.
            // Still create the table: an empty table joins to nothing, whereas
            // a missing one turns the user's next query into an error.
            Log?.Invoke($"{tableName}: no keys, so Dataverse was not queried");
        }

        foreach (var batch in Pushdown.Batch(keys))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batchSql = keyQuery.Expand(batch);
            var plan = _source.Analyze(batchSql);
            firstPlan ??= plan;

            if (plan is not null)
            {
                if (!plan.FullyFolded)
                    Log?.Invoke(plan.Describe());

                if (ExecutionPlanAnalyzer.IsRejectedBy(plan, FoldingPolicy))
                    throw new PlanNotFoldedException(plan);
            }

            rows += Fetch(batchSql, reader =>
            {
                if (mapping is null)
                {
                    mapping = Mapper.MapReader(reader, tableName);
                    loader.CreateTable(mapping, transaction);
                }

                return loader.LoadInto(reader, mapping, null, cancellationToken);
            });

            Log?.Invoke($"{tableName}: {rows:N0} rows after {Math.Min(rows, batch.Count)} of {keys.Count:N0} keys");
        }

        if (mapping is null)
        {
            // No batch ran, so the schema is unknown. Ask Dataverse for the
            // shape without asking for any rows.
            Fetch(keyQuery.Expand([Guid.Empty]), reader =>
            {
                mapping = Mapper.MapReader(reader, tableName);
                loader.CreateTable(mapping, transaction);
                return 0L;
            });
        }

        stopwatch.Stop();

        var result = new CacheResult(tableName, rows, mapping!, firstPlan, stopwatch.Elapsed)
        {
            KeyCount = keys.Count,
        };

        RecordManifest(result, originalSql, transaction);
        transaction.Commit();

        return result;
    }

    private CacheResult CacheOne(
        string sql,
        string tableName,
        PlanAnalysis? plan,
        CancellationToken cancellationToken)
    {
        plan ??= _source.Analyze(sql);

        if (plan is not null)
        {
            if (!plan.FullyFolded)
                Log?.Invoke(plan.Describe());

            if (ExecutionPlanAnalyzer.IsRejectedBy(plan, FoldingPolicy))
                throw new PlanNotFoldedException(plan);
        }

        var stopwatch = Stopwatch.StartNew();
        var loader = new DuckDbBulkLoader(Connection);

        // The load owns the transaction here rather than delegating it to
        // DuckDbBulkLoader.Load, so the manifest row commits with the rows it
        // describes. Same atomicity guarantee, one level up.
        using var transaction = Connection.BeginTransaction();

        var loaded = Fetch(sql, reader =>
        {
            var mapping = Mapper.MapReader(reader, tableName);
            loader.CreateTable(mapping, transaction);

            var rows = loader.LoadInto(
                reader, mapping,
                progress: written => Log?.Invoke($"{tableName}: {written:N0} rows"),
                cancellationToken);

            return new LoadResult(mapping, rows);
        });

        stopwatch.Stop();

        var result = new CacheResult(tableName, loaded.RowCount, loaded.Mapping, plan, stopwatch.Elapsed);

        RecordManifest(result, sql, transaction);
        transaction.Commit();

        return result;
    }

    /// <summary>
    /// Writes what this table is into the cache file, inside the load's own
    /// transaction so the record cannot outlive a rollback.
    /// </summary>
    private void RecordManifest(CacheResult result, string sourceSql, DuckDBTransaction transaction) =>
        CacheManifest.Record(
            Connection,
            new CacheEntry(
                result.TableName,
                PlanStepKind.Dataverse,
                sourceSql,
                result.RowCount,
                result.KeyCount,
                DateTime.UtcNow,
                result.Elapsed),
            transaction);

    /// <summary>
    /// Runs one Dataverse query, translating service protection faults into
    /// something that says which limit was hit and what to do about it.
    /// </summary>
    private T Fetch<T>(string sql, Func<DbDataReader, T> read)
    {
        try
        {
            return _source.Query(sql, read);
        }
        catch (Exception e) when (DataverseThrottling.Explain(e) is { } explanation)
        {
            // The load rolled back, so the previous table is intact. Say which
            // limit was hit, because the three have different remedies and the
            // raw fault only carries a number.
            throw new DataverseThrottledException(explanation, e);
        }
    }

    /// <summary>
    /// Exposes a JSON file as a queryable view.
    ///
    /// Uses <c>read_json_auto</c>, which applies timezone offsets correctly and
    /// yields naive <c>TIMESTAMP</c> values — unlike casting the strings, which
    /// discards the offset silently (ADR 0002).
    /// </summary>
    /// <param name="viewName">View to create.</param>
    /// <param name="path">File path or glob, e.g. <c>logs/*.json</c>.</param>
    /// <returns>
    /// Timestamp columns that stayed textual, which compare as strings rather
    /// than instants. Also reported via <see cref="Log"/>.
    /// </returns>
    public IReadOnlyList<TextualTimestamp> RegisterJson(string viewName, string path)
    {
        DuckDbIdentifier.Validate(viewName);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using (var command = Connection.CreateCommand())
        {
            command.CommandText =
                $"CREATE OR REPLACE VIEW {DuckDbIdentifier.Quote(viewName)} AS " +
                $"SELECT * FROM read_json_auto({QuoteLiteral(path)})";
            command.ExecuteNonQuery();
        }

        var textual = JsonTimestampInspector.Inspect(Connection, viewName);

        foreach (var column in textual)
            Log?.Invoke(column.Describe());

        // A view is persisted in the .duckdb file too, and reading a reused
        // cache offline gives no other clue which path it points at.
        CacheManifest.Record(
            Connection,
            new CacheEntry(viewName, PlanStepKind.Json, path, null, null, DateTime.UtcNow, TimeSpan.Zero));

        return textual;
    }

    /// <summary>
    /// Runs a query against the cache, joining cached Dataverse tables and any
    /// registered JSON views.
    /// </summary>
    public DuckDBDataReader Query(string sql)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        var command = Connection.CreateCommand();
        command.CommandText = sql;
        return (DuckDBDataReader)command.ExecuteReader();
    }

    /// <summary>
    /// Quotes a string literal, doubling embedded single quotes.
    ///
    /// File paths are frequently user-supplied and this one is concatenated
    /// into SQL, so it is an injection point.
    /// </summary>
    private static string QuoteLiteral(string value) => $"'{value.Replace("'", "''")}'";
}
