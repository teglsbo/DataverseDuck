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

    /// <summary>
    /// Runs a query against Dataverse and stores the result as a DuckDB table.
    /// </summary>
    /// <param name="sql">T-SQL, compiled to FetchXML by SQL 4 CDS.</param>
    /// <param name="tableName">Destination table. Replaced if it exists.</param>
    /// <exception cref="PlanNotFoldedException">
    /// The plan does more local work than <see cref="FoldingPolicy"/> permits.
    /// </exception>
    public CacheResult Cache(string sql, string tableName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        DuckDbIdentifier.Validate(tableName);

        var plan = _source.Analyze(sql);

        if (plan is not null)
        {
            if (!plan.FullyFolded)
                Log?.Invoke(plan.Describe());

            if (ExecutionPlanAnalyzer.IsRejectedBy(plan, FoldingPolicy))
                throw new PlanNotFoldedException(plan);
        }

        var stopwatch = Stopwatch.StartNew();

        var result = _source.Query(sql, reader =>
            new DuckDbBulkLoader(Connection).Load(
                reader, tableName, Mapper,
                progress: rows => Log?.Invoke($"{tableName}: {rows:N0} rows"),
                cancellationToken));

        stopwatch.Stop();

        return new CacheResult(tableName, result.RowCount, result.Mapping, plan, stopwatch.Elapsed);
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
