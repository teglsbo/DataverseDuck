using DuckDB.NET.Data;

namespace DataverseDuck;

/// <summary>One row of the manifest: what a table or view in the cache is.</summary>
/// <param name="Name">The DuckDB table or view.</param>
/// <param name="Kind">Whether it came from Dataverse or a JSON file.</param>
/// <param name="Source">The statement or path that produced it, exactly as written.</param>
/// <param name="RowCount">Rows written, or null for a JSON view (which is not materialised).</param>
/// <param name="KeyCount">
/// Distinct keys the <c>{{ }}</c> query supplied, or null if the statement had none.
/// Non-null is the marker that this table is a <em>subset</em> of the Dataverse table.
/// </param>
/// <param name="LoadedAt">Naive UTC, matching every other timestamp in the cache.</param>
/// <param name="Elapsed">Wall-clock time for the load.</param>
public sealed record CacheEntry(
    string Name,
    PlanStepKind Kind,
    string Source,
    long? RowCount,
    long? KeyCount,
    DateTime LoadedAt,
    TimeSpan Elapsed)
{
    /// <summary>
    /// True when the table holds only the rows some local query asked for, so
    /// it must not be read as if it were the whole Dataverse table.
    /// </summary>
    public bool IsPartial => KeyCount is not null;

    /// <summary>How long ago this was loaded, relative to now.</summary>
    public TimeSpan Age => DateTime.UtcNow - LoadedAt;

    /// <summary>A one-line summary for the CLI.</summary>
    public string Describe()
    {
        var what = Kind == PlanStepKind.Json
            ? "view"
            : FormattableString.Invariant($"{RowCount ?? 0:N0} rows") + (IsPartial ? FormattableString.Invariant($" matching {KeyCount:N0} key(s)") : string.Empty);

        return $"{Name}  {what}, loaded {Humanise(Age)} ago";
    }

    /// <summary>
    /// Ages are read to decide whether to refetch, so minutes-vs-days is the
    /// distinction that matters, not seconds.
    /// </summary>
    internal static string Humanise(TimeSpan age) => age switch
    {
        { TotalSeconds: < 0 } => "0s",
        { TotalMinutes: < 1 } => $"{age.TotalSeconds:F0}s",
        { TotalHours: < 1 } => $"{age.TotalMinutes:F0}m",
        { TotalDays: < 1 } => $"{age.TotalHours:F0}h",
        _ => $"{age.TotalDays:F0}d",
    };
}

/// <summary>
/// Records what each cached table is, in the cache file itself.
///
/// A <c>.duckdb</c> file is meant to be reused — queried offline, refetched
/// selectively, kept between sessions. Without this, the file is a set of
/// tables with no provenance: nothing says when <c>crm_contact</c> was loaded,
/// what statement produced it, or — the dangerous one — that <c>{{ }}</c>
/// means it holds only the contacts some JSON file referred to at the time.
/// A partial copy is indistinguishable from a complete one by inspection, and
/// reading it as complete gives wrong answers rather than errors.
///
/// The manifest is written inside the load's own transaction, so it can never
/// describe rows that were rolled back.
/// </summary>
public static class CacheManifest
{
    /// <summary>The manifest table. Prefixed so it cannot collide with a Dataverse table name.</summary>
    public const string TableName = "dvduck_manifest";

    private const string CreateSql = $"""
        CREATE TABLE IF NOT EXISTS {TableName} (
            name       VARCHAR NOT NULL,
            kind       VARCHAR NOT NULL,
            source     VARCHAR NOT NULL,
            row_count  BIGINT,
            key_count  BIGINT,
            loaded_at  TIMESTAMP NOT NULL,
            elapsed_ms BIGINT NOT NULL
        )
        """;

    /// <summary>
    /// Records one entry, replacing any previous entry for the same name.
    /// </summary>
    /// <param name="transaction">
    /// The transaction the rows were loaded in. Passing it is what makes the
    /// manifest and the data commit or roll back together; a manifest written
    /// outside the load would survive a failure and claim rows that are gone.
    /// </param>
    public static void Record(DuckDBConnection connection, CacheEntry entry, DuckDBTransaction? transaction = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(entry);

        Execute(connection, transaction, CreateSql);

        // No primary key, because DuckDB rejects deleting and reinserting the
        // same key inside one transaction. An explicit delete says the same
        // thing and works where the load actually happens.
        //
        // Matched case-insensitively: a plan can name the same source with different
        // casing across runs (DuckDB table names aren't case-normalised), and without
        // this a re-load leaves the old-cased entry behind instead of replacing it.
        Execute(connection, transaction, $"DELETE FROM {TableName} WHERE lower(name) = lower($name)",
            ("name", entry.Name));

        Execute(connection, transaction,
            $"""
            INSERT INTO {TableName} (name, kind, source, row_count, key_count, loaded_at, elapsed_ms)
            VALUES ($name, $kind, $source, $row_count, $key_count, $loaded_at, $elapsed_ms)
            """,
            ("name", entry.Name),
            ("kind", entry.Kind.ToString().ToLowerInvariant()),
            ("source", entry.Source),
            ("row_count", entry.RowCount),
            ("key_count", entry.KeyCount),
            ("loaded_at", entry.LoadedAt),
            ("elapsed_ms", (long)entry.Elapsed.TotalMilliseconds));
    }

    /// <summary>
    /// Reads the manifest, newest first. Empty if the cache has none — an
    /// in-memory run, or a file written before manifests existed.
    /// </summary>
    public static IReadOnlyList<CacheEntry> Read(DuckDBConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (!Exists(connection))
            return [];

        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT name, kind, source, row_count, key_count, loaded_at, elapsed_ms " +
            $"FROM {TableName} ORDER BY loaded_at DESC, name";

        var entries = new List<CacheEntry>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            entries.Add(new CacheEntry(
                reader.GetString(0),
                Enum.TryParse<PlanStepKind>(reader.GetString(1), ignoreCase: true, out var kind)
                    ? kind
                    : PlanStepKind.Dataverse,
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetInt64(4),
                reader.GetDateTime(5),
                TimeSpan.FromMilliseconds(reader.GetInt64(6))));
        }

        return entries;
    }

    /// <summary>Whether this cache carries a manifest at all.</summary>
    public static bool Exists(DuckDBConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT count(*) FROM information_schema.tables WHERE table_name = $table";
        Bind(command, ("table", TableName));
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }

    private static void Execute(
        DuckDBConnection connection,
        DuckDBTransaction? transaction,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        Bind(command, parameters);
        command.ExecuteNonQuery();
    }

    private static void Bind(DuckDBCommand command, params (string Name, object? Value)[] parameters)
    {
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
    }
}
