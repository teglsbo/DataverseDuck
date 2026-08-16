using System.Data.Common;
using Microsoft.Xrm.Sdk;

namespace DataverseDuck.Schema;

/// <summary>
/// Turns a SQL 4 CDS result set into a DuckDB table definition, and converts
/// values into something the DuckDB appender accepts.
///
/// The CLR types listed here are not guesses: they were read out of the engine's
/// own <c>SqlTypeConverter.SqlToNetType</c>, which is the mapping the data reader
/// applies. Two consequences drive the design:
///
/// <list type="bullet">
/// <item>Lookups surface as <see cref="EntityReference"/>, not <see cref="Guid"/>,
/// unless the connection sets <c>ReturnEntityReferenceAsGuid</c>. The appender
/// cannot store one, so it is decomposed.</item>
/// <item>A <c>datetimeoffset</c> column surfaces as <see cref="DateTimeOffset"/>.
/// Storing it as-is would put timezone-aware data into the cache, which ADR 0002
/// bans, so it is normalised to a naive UTC instant.</item>
/// </list>
///
/// Note that <c>OptionSetValue</c> and <c>Money</c> never appear: the engine folds
/// them to <see cref="int"/> and <see cref="decimal"/> before the reader sees them.
/// They are handled anyway, for values arriving straight from the SDK.
/// </summary>
public sealed class DataverseSchemaMapper
{
    /// <summary>
    /// Suffix for the companion column holding a polymorphic lookup's target table.
    /// </summary>
    public const string LookupTargetSuffix = "_entitytype";

    /// <summary>
    /// Precision and scale for decimals. Dataverse money is 4 decimal places and
    /// decimal columns allow up to 10; 38 digits is DuckDB's maximum precision.
    /// </summary>
    public string DecimalType { get; init; } = "DECIMAL(38,10)";

    /// <summary>
    /// Emit a companion "_entitytype" column for lookup columns, so polymorphic
    /// lookups can be disambiguated. Costs a column per lookup.
    /// </summary>
    public bool IncludeLookupTargetTable { get; init; } = true;

    /// <summary>
    /// Builds a table mapping from an open reader's schema.
    /// </summary>
    public TableMapping MapReader(DbDataReader reader, string tableName)
    {
        ArgumentNullException.ThrowIfNull(reader);
        DuckDbIdentifier.Validate(tableName);

        var columns = new List<ColumnMapping>(reader.FieldCount);

        for (var i = 0; i < reader.FieldCount; i++)
            columns.AddRange(MapColumn(reader.GetName(i), reader.GetFieldType(i), i));

        return new TableMapping(tableName, columns);
    }

    /// <summary>
    /// Maps one source column. Returns more than one mapping for a lookup when
    /// <see cref="IncludeLookupTargetTable"/> is set.
    /// </summary>
    public IEnumerable<ColumnMapping> MapColumn(string name, Type clrType, int ordinal)
    {
        DuckDbIdentifier.Validate(name);
        ArgumentNullException.ThrowIfNull(clrType);

        var underlying = Nullable.GetUnderlyingType(clrType) ?? clrType;

        if (underlying == typeof(EntityReference))
        {
            yield return new ColumnMapping(name, "UUID", ordinal, ColumnKind.LookupId, underlying);

            if (IncludeLookupTargetTable)
                yield return new ColumnMapping(
                    name + LookupTargetSuffix, "VARCHAR", ordinal, ColumnKind.LookupTargetTable, underlying);

            yield break;
        }

        yield return new ColumnMapping(name, ToDuckDbType(underlying), ordinal, ColumnKind.Scalar, underlying);
    }

    /// <summary>
    /// The DuckDB type for a CLR type produced by the reader.
    /// </summary>
    public string ToDuckDbType(Type clrType)
    {
        ArgumentNullException.ThrowIfNull(clrType);
        var t = Nullable.GetUnderlyingType(clrType) ?? clrType;

        if (t == typeof(bool)) return "BOOLEAN";
        if (t == typeof(byte)) return "UTINYINT";
        if (t == typeof(sbyte)) return "TINYINT";
        if (t == typeof(short)) return "SMALLINT";
        if (t == typeof(int)) return "INTEGER";
        if (t == typeof(long)) return "BIGINT";
        if (t == typeof(float)) return "FLOAT";
        if (t == typeof(double)) return "DOUBLE";
        if (t == typeof(decimal)) return DecimalType;
        if (t == typeof(Guid)) return "UUID";
        if (t == typeof(string)) return "VARCHAR";
        if (t == typeof(byte[])) return "BLOB";
        if (t == typeof(TimeSpan)) return "TIME";

        // Both land in a naive TIMESTAMP holding UTC. See ADR 0002: TIMESTAMPTZ
        // comparisons are session-dependent, so it must not enter the cache.
        if (t == typeof(DateTime) || t == typeof(DateTimeOffset)) return "TIMESTAMP";

        if (t == typeof(EntityReference)) return "UUID";
        if (t == typeof(OptionSetValue)) return "INTEGER";
        if (t == typeof(Money)) return DecimalType;
        if (t == typeof(object)) return "VARCHAR";

        throw new NotSupportedException(
            $"No DuckDB type mapping for '{t.FullName}'. Add one to {nameof(DataverseSchemaMapper)}.");
    }

    /// <summary>
    /// Converts a reader value into something the DuckDB appender accepts.
    ///
    /// The appender takes plain CLR types only, and it does not interpret
    /// <see cref="DateTimeKind"/>, so anything carrying an offset must be
    /// resolved here rather than left to the database.
    /// </summary>
    public static object? ConvertValue(object? value, ColumnKind kind)
    {
        if (value is null || value is DBNull)
            return null;

        switch (kind)
        {
            case ColumnKind.LookupId:
                return value switch
                {
                    EntityReference reference => reference.Id,
                    Guid guid => guid,
                    _ => throw new InvalidOperationException(
                        $"Expected a lookup value but got '{value.GetType().FullName}'."),
                };

            case ColumnKind.LookupTargetTable:
                return value switch
                {
                    EntityReference reference => reference.LogicalName,
                    // A bare Guid carries no target table; the column is simply unknown.
                    Guid => null,
                    _ => throw new InvalidOperationException(
                        $"Expected a lookup value but got '{value.GetType().FullName}'."),
                };

            default:
                return ConvertScalar(value);
        }
    }

    private static object? ConvertScalar(object value) => value switch
    {
        // Resolve to a real UTC instant. Keeping the offset would smuggle
        // timezone-aware data into the cache.
        DateTimeOffset offset => offset.UtcDateTime,

        DateTime dateTime => UtcTimestampPolicy.ToUtcInstant(dateTime),

        OptionSetValue option => option.Value,
        Money money => money.Value,
        EntityReference reference => reference.Id,

        // The appender has no concept of these; store the label.
        EntityCollection collection => collection.EntityName,

        _ => value,
    };
}
