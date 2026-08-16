namespace DataverseDuck.Schema;

/// <summary>
/// How a source column is represented in DuckDB.
/// </summary>
public enum ColumnKind
{
    /// <summary>Direct mapping of a scalar value.</summary>
    Scalar,

    /// <summary>The id of a lookup, stored as UUID so it can be joined on.</summary>
    LookupId,

    /// <summary>
    /// The target table of a lookup. Only meaningful for polymorphic lookups
    /// (customerid, ownerid, regardingobjectid), where the id alone is ambiguous.
    /// </summary>
    LookupTargetTable,
}

/// <summary>
/// One column in a DuckDB cache table.
/// </summary>
/// <param name="Name">Column name in DuckDB.</param>
/// <param name="DuckDbType">DuckDB type, e.g. <c>VARCHAR</c>.</param>
/// <param name="SourceColumn">Ordinal in the source reader.</param>
/// <param name="Kind">How the value is derived.</param>
/// <param name="SourceType">CLR type the reader produces.</param>
public sealed record ColumnMapping(
    string Name,
    string DuckDbType,
    int SourceColumn,
    ColumnKind Kind,
    Type SourceType)
{
    /// <summary>The column definition as it appears in a CREATE TABLE statement.</summary>
    public string ToDdl() => $"{DuckDbIdentifier.Quote(Name)} {DuckDbType}";
}

/// <summary>
/// The full shape of a cache table.
/// </summary>
public sealed record TableMapping(string TableName, IReadOnlyList<ColumnMapping> Columns)
{
    /// <summary>
    /// CREATE TABLE for this mapping. Column names are quoted, so reserved words
    /// and Dataverse's mixed-case logical names are safe.
    /// </summary>
    public string ToCreateTableSql(bool orReplace = true)
    {
        var verb = orReplace ? "CREATE OR REPLACE TABLE" : "CREATE TABLE";
        var columns = string.Join(",\n  ", Columns.Select(c => c.ToDdl()));
        return $"{verb} {DuckDbIdentifier.Quote(TableName)} (\n  {columns}\n)";
    }

    /// <summary>Columns that come straight from the reader, in reader order.</summary>
    public IEnumerable<ColumnMapping> SourceColumns =>
        Columns.Where(c => c.Kind != ColumnKind.LookupTargetTable);
}

/// <summary>
/// Quoting for DuckDB identifiers.
/// </summary>
public static class DuckDbIdentifier
{
    /// <summary>
    /// Quotes an identifier, doubling any embedded quote.
    ///
    /// Always used when building DDL. Dataverse logical names are well behaved,
    /// but table names can come from user input, and an unquoted identifier is
    /// an injection point in a string-built CREATE TABLE.
    /// </summary>
    public static string Quote(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        return $"\"{identifier.Replace("\"", "\"\"")}\"";
    }

    /// <summary>
    /// Rejects identifiers that cannot safely round-trip, rather than mangling them.
    /// </summary>
    public static string Validate(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);

        if (identifier.Contains('\0'))
            throw new ArgumentException("Identifier contains a null character.", nameof(identifier));

        return identifier;
    }
}
