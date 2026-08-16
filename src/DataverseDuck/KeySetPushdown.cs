using System.Globalization;
using System.Text;
using DuckDB.NET.Data;

namespace DataverseDuck;

/// <summary>
/// Builds a Dataverse query that fetches only the rows a local DuckDB query
/// says are needed.
///
/// <para>The ideal would be registering Dataverse as a DuckDB table and letting
/// the optimiser push filters down, the way <c>postgres_scanner</c> does. That
/// is not available to us: <c>postgres_scanner</c> is a C++ extension using
/// DuckDB's internal filter-pushdown hooks, and the managed table function API
/// exposes none of them. Measured — a C# table function is asked for every row
/// regardless of <c>WHERE</c>, projection, or join:</para>
///
/// <code>
/// SELECT count(*) FROM dv() WHERE statecode = 0   -- pulled all 1000 rows
/// SELECT count(*) FROM dv() JOIN keys ON ...      -- pulled all 1000 rows
/// </code>
///
/// <para>So DuckDB cannot do the fetching. It can still decide <em>what</em> to
/// fetch, which is the part that matters: evaluate the local predicate over
/// JSON, collect the keys, and send them to Dataverse as an <c>IN</c> filter.
/// A four-million-row table is never scanned.</para>
///
/// <para>Verified in the SQL 4 CDS source: <c>IN</c> over literals always folds
/// to a single FetchXML <c>&lt;condition operator="in"&gt;</c>, and never falls
/// back to client-side filtering. <c>IN (SELECT …)</c> is refused outright,
/// which is why the keys are inlined as literals rather than left as a
/// subquery.</para>
/// </summary>
public sealed class KeySetPushdown
{
    /// <summary>
    /// Keys per Dataverse round trip.
    ///
    /// Not a documented limit. Microsoft documents 500 <c>&lt;condition&gt;</c>
    /// elements per <c>&lt;filter&gt;</c>, but an <c>IN</c> list is one
    /// condition however many values it holds, so that cap does not apply and
    /// no per-value cap is published. The widely repeated "500 values" advice
    /// is that rule misread.
    ///
    /// This is therefore a safety valve, not a constraint: it bounds the
    /// server-side plan complexity and keeps one failed page cheap to retry.
    /// </summary>
    public int BatchSize { get; init; } = 500;

    /// <summary>
    /// Above this many keys, fetching the table with a plain filter is usually
    /// cheaper than sending keys back. Zero disables the check.
    /// </summary>
    public int MaxKeys { get; init; } = 100_000;

    /// <summary>
    /// Runs <paramref name="keyQuery"/> against DuckDB and returns its distinct,
    /// non-null first-column values.
    /// </summary>
    public IReadOnlyList<object> ReadKeys(DuckDBConnection connection, string keyQuery)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyQuery);

        using var command = connection.CreateCommand();

        // DISTINCT here rather than in the caller's SQL: duplicate keys are the
        // norm in logs (one row per event) and every duplicate would otherwise
        // cost a slot in the IN list.
        command.CommandText = $"SELECT DISTINCT k FROM ({keyQuery}) t(k) WHERE k IS NOT NULL";

        using var reader = command.ExecuteReader();
        List<object> keys = [];

        while (reader.Read())
        {
            keys.Add(reader.GetValue(0));

            if (MaxKeys > 0 && keys.Count > MaxKeys)
                throw new InvalidOperationException(
                    $"The local query produced more than {MaxKeys:N0} keys. Sending them back to " +
                    "Dataverse as an IN filter is unlikely to beat fetching the table with a " +
                    "server-side filter instead. Narrow the local query, or raise MaxKeys if you " +
                    "are sure.");
        }

        return keys;
    }

    /// <summary>Splits keys into batches, each of which becomes one Dataverse query.</summary>
    public IEnumerable<IReadOnlyList<object>> Batch(IReadOnlyList<object> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        for (var offset = 0; offset < keys.Count; offset += BatchSize)
            yield return keys.Skip(offset).Take(BatchSize).ToList();
    }

    /// <summary>
    /// Renders keys as a T-SQL <c>IN</c> list.
    ///
    /// Values are emitted as literals, not parameters, because SQL 4 CDS only
    /// folds <c>IN</c> when every value is a literal. That makes this an
    /// injection boundary, so each value is validated or escaped by type rather
    /// than formatted into the string.
    /// </summary>
    public static string RenderInList(IReadOnlyList<object> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        if (keys.Count == 0)
            throw new ArgumentException("Cannot render an empty IN list.", nameof(keys));

        var builder = new StringBuilder("(");

        for (var i = 0; i < keys.Count; i++)
        {
            if (i > 0) builder.Append(", ");
            builder.Append(RenderLiteral(keys[i]));
        }

        return builder.Append(')').ToString();
    }

    /// <summary>
    /// Renders one key. Anything whose textual form we cannot vouch for is
    /// rejected rather than guessed at.
    /// </summary>
    internal static string RenderLiteral(object key) => key switch
    {
        null or DBNull => throw new ArgumentException("Null keys should have been filtered out."),

        // Guid.ToString is fixed-format and cannot contain a quote, but it is
        // still round-tripped through the parser so a spoofed ToString cannot
        // smuggle anything through.
        Guid guid => $"'{guid:D}'",

        string text => $"'{text.Replace("'", "''")}'",

        bool flag => flag ? "1" : "0",

        byte or sbyte or short or ushort or int or uint or long or ulong =>
            Convert.ToInt64(key, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),

        decimal value => value.ToString(CultureInfo.InvariantCulture),

        DateTime timestamp => $"'{timestamp:yyyy-MM-dd HH:mm:ss.fff}'",

        _ => throw new NotSupportedException(
            $"Cannot use a value of type {key.GetType().Name} as a Dataverse key. " +
            "Cast it in the local query, for example CAST(customer_id AS UUID)."),
    };

    /// <summary>
    /// The marker for a local key query inside a Dataverse statement, as in
    /// <c>WHERE contactid IN {{SELECT DISTINCT customer_id FROM logs}}</c>.
    /// </summary>
    public const string OpenMarker = "{{";

    /// <summary>Closes <see cref="OpenMarker"/>.</summary>
    public const string CloseMarker = "}}";

    /// <summary>
    /// Finds the local key query embedded in a Dataverse statement, or null if
    /// there is none.
    ///
    /// Substituting where the user wrote the marker, rather than wrapping their
    /// statement in a derived table, keeps the result a plain
    /// <c>IN (literal, …)</c> in their own <c>WHERE</c> clause. That is the
    /// exact shape SQL 4 CDS folds into a FetchXML <c>in</c> condition; a
    /// wrapper risks the optimiser leaving the filter client-side and quietly
    /// scanning the whole table, which is the outcome this class exists to
    /// prevent.
    /// </summary>
    public static LocalKeyQuery? FindKeyQuery(string sql)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        var start = sql.IndexOf(OpenMarker, StringComparison.Ordinal);

        if (start < 0)
            return null;

        var end = sql.IndexOf(CloseMarker, start + OpenMarker.Length, StringComparison.Ordinal);

        if (end < 0)
            throw new FormatException($"'{OpenMarker}' was opened but never closed with '{CloseMarker}'.");

        if (sql.IndexOf(OpenMarker, end + CloseMarker.Length, StringComparison.Ordinal) >= 0)
            throw new FormatException(
                "Only one local key query is supported per statement. Two independent key sets " +
                "would multiply the number of Dataverse round trips rather than narrowing them.");

        var inner = sql[(start + OpenMarker.Length)..end].Trim();

        if (inner.Length == 0)
            throw new FormatException($"'{OpenMarker}{CloseMarker}' is empty; it needs a DuckDB query returning key values.");

        return new LocalKeyQuery(inner, sql[..start], sql[(end + CloseMarker.Length)..]);
    }
}

/// <param name="Sql">The DuckDB query producing the keys.</param>
/// <param name="Before">Dataverse SQL preceding the marker.</param>
/// <param name="After">Dataverse SQL following the marker.</param>
public sealed record LocalKeyQuery(string Sql, string Before, string After)
{
    /// <summary>Rebuilds the Dataverse statement with one batch of keys inlined.</summary>
    public string Expand(IReadOnlyList<object> keys) =>
        Before + KeySetPushdown.RenderInList(keys) + After;
}
