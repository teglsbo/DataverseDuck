using System.Data.Common;
using System.ServiceModel;
using MarkMpn.Sql4Cds.Engine;
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
    /// Entity metadata, used to read each datetime column's
    /// <see cref="Microsoft.Xrm.Sdk.Metadata.DateTimeAttributeMetadata.DateTimeBehavior"/>.
    ///
    /// Optional. Without it every datetime is treated as a UTC instant, which
    /// is right for the great majority of columns but wrong for DateOnly and
    /// TimeZoneIndependent ones. The reader cannot tell them apart on its own:
    /// all three arrive as <see cref="DateTime"/> with <c>Kind=Unspecified</c>
    /// (measured), so the distinction only exists in metadata.
    /// </summary>
    public IAttributeMetadataCache? Metadata { get; init; }

    /// <summary>
    /// Builds a table mapping from an open reader's schema.
    /// </summary>
    public TableMapping MapReader(DbDataReader reader, string tableName)
    {
        ArgumentNullException.ThrowIfNull(reader);
        DuckDbIdentifier.Validate(tableName);

        var columns = new List<ColumnMapping>(reader.FieldCount);
        var sources = ReadColumnSources(reader);

        for (var i = 0; i < reader.FieldCount; i++)
            columns.AddRange(MapColumn(reader.GetName(i), reader.GetFieldType(i), i, sources.GetValueOrDefault(i)));

        return new TableMapping(tableName, columns);
    }

    /// <summary>
    /// Reads each column's originating table and attribute from the reader's
    /// schema table. Aliases and joins are handled by the engine: a column
    /// selected as <c>a.createdon AS acct_created</c> still reports
    /// <c>account</c>/<c>createdon</c> (measured).
    ///
    /// Returns nothing when metadata is unavailable, or when the provider
    /// declines to produce a schema table -- a computed column has no source
    /// attribute, and that is not an error.
    /// </summary>
    private Dictionary<int, (string Table, string Column)> ReadColumnSources(DbDataReader reader)
    {
        var sources = new Dictionary<int, (string, string)>();

        if (Metadata is null)
            return sources;

        System.Data.DataTable? schema;

        try
        {
            schema = reader.GetSchemaTable();
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException)
        {
            return sources;
        }

        if (schema is null)
            return sources;

        foreach (System.Data.DataRow row in schema.Rows)
        {
            if (row["ColumnOrdinal"] is not int ordinal) continue;
            if (row["BaseTableName"] is not string table || string.IsNullOrEmpty(table)) continue;
            if (row["BaseColumnName"] is not string column || string.IsNullOrEmpty(column)) continue;

            sources[ordinal] = (table, column);
        }

        return sources;
    }

    /// <summary>
    /// Maps one source column. Returns more than one mapping for a lookup when
    /// <see cref="IncludeLookupTargetTable"/> is set.
    /// </summary>
    public IEnumerable<ColumnMapping> MapColumn(string name, Type clrType, int ordinal) =>
        MapColumn(name, clrType, ordinal, null);

    /// <summary>
    /// Maps one source column, consulting metadata for the originating
    /// attribute when it is known.
    /// </summary>
    public IEnumerable<ColumnMapping> MapColumn(
        string name, Type clrType, int ordinal, (string Table, string Column)? source)
    {
        DuckDbIdentifier.Validate(name);
        ArgumentNullException.ThrowIfNull(clrType);

        var underlying = Nullable.GetUnderlyingType(clrType) ?? clrType;

        if (underlying == typeof(EntityReference) || underlying == typeof(SqlEntityReference))
        {
            yield return new ColumnMapping(name, "UUID", ordinal, ColumnKind.LookupId, underlying);

            if (IncludeLookupTargetTable)
                yield return new ColumnMapping(
                    name + LookupTargetSuffix, "VARCHAR", ordinal, ColumnKind.LookupTargetTable, underlying);

            yield break;
        }

        if (underlying == typeof(DateTime) || underlying == typeof(DateTimeOffset))
        {
            var mapping = ResolveDateTimeMapping(source);

            yield return new ColumnMapping(
                name,
                mapping.DuckDbType,
                ordinal,
                mapping.ConvertToUtc ? ColumnKind.Scalar : ColumnKind.WallClock,
                underlying);

            yield break;
        }

        yield return new ColumnMapping(name, ToDuckDbType(underlying), ordinal, ColumnKind.Scalar, underlying);
    }

    /// <summary>
    /// Applies rule 6: only UserLocal is a true UTC instant. Falls back to
    /// treating the column as an instant when the attribute cannot be resolved,
    /// which is both the common case and the safe one -- the great majority of
    /// Dataverse datetimes are UserLocal.
    /// </summary>
    private DateTimeMapping ResolveDateTimeMapping((string Table, string Column)? source)
    {
        var instant = new DateTimeMapping("TIMESTAMP", ConvertToUtc: true);

        if (Metadata is null || source is null)
            return instant;

        var (table, column) = source.Value;

        Microsoft.Xrm.Sdk.Metadata.EntityMetadata entity;

        try
        {
            // Deliberately the indexer, not TryGetValue. A live
            // AttributeMetadataCache loads lazily, so TryGetValue reports false
            // for an entity that simply has not been fetched yet (measured);
            // using it here silently skipped metadata on every first call. The
            // indexer fetches on demand and throws only when the entity really
            // is unknown.
            entity = Metadata[table];
        }
        catch (FaultException<OrganizationServiceFault> fault) when (IsUnknownEntityFault(fault))
        {
            // AttributeMetadataCache reports an unknown entity this way. The
            // column may come from something with no entity behind it, and
            // treating it as an instant is the same answer as having no
            // metadata at all. Anything else -- authentication, transport,
            // provider failures -- is a real error and must propagate rather
            // than being silently mapped as if metadata were simply absent.
            return instant;
        }

        var attribute = entity?.Attributes?
            .OfType<Microsoft.Xrm.Sdk.Metadata.DateTimeAttributeMetadata>()
            .FirstOrDefault(a => string.Equals(a.LogicalName, column, StringComparison.OrdinalIgnoreCase));

        return attribute is null ? instant : UtcTimestampPolicy.MapDateTimeAttribute(attribute);
    }

    /// <summary>
    /// AttributeMetadataCache rethrows a genuine Dataverse fault (including
    /// "entity not found") unchanged, but wraps every other exception --
    /// authentication, transport, provider failures -- into a fresh
    /// <see cref="FaultException{OrganizationServiceFault}"/> whose
    /// <c>Detail.ErrorCode</c> is left at its default of 0 (verified against
    /// the real MarkMpn.Sql4Cds.Engine DLL, which ships no source or docs).
    /// ErrorCode 0 and the known service-protection throttling codes rule out
    /// those wrapped/unrelated failures, but a genuine, non-throttling fault
    /// can still be access-denied or a server/plugin failure rather than an
    /// unknown entity -- both have a real, non-zero ErrorCode too. Those also
    /// have to be excluded, and no documented error code distinguishes
    /// "unknown entity" from them (RetrieveEntityRequest's failure codes are
    /// server-internal and unpublished). The remaining, best available
    /// signal is that a genuine "no such entity" fault's message says so --
    /// unlike an access-denied or server-error message, which does not.
    /// </summary>
    private static bool IsUnknownEntityFault(FaultException<OrganizationServiceFault> fault) =>
        fault.Detail is { ErrorCode: not 0 } detail &&
        DataverseThrottling.FromErrorCode(detail.ErrorCode) == ThrottleKind.None &&
        (fault.Message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
         fault.Message.Contains("does not exist", StringComparison.OrdinalIgnoreCase));

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
        if (t == typeof(SqlEntityReference)) return "UUID";
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

                    // A struct, so a null lookup arrives as a non-null value
                    // with IsNull set rather than as DBNull. Reading .Id
                    // without checking would store Guid.Empty and turn an
                    // absent lookup into one pointing at nothing.
                    SqlEntityReference sql => sql.IsNull ? null : sql.Id,

                    Guid guid => guid,
                    _ => throw new InvalidOperationException(
                        $"Expected a lookup value but got '{value.GetType().FullName}'."),
                };

            case ColumnKind.LookupTargetTable:
                return value switch
                {
                    EntityReference reference => reference.LogicalName,
                    SqlEntityReference sql => sql.IsNull ? null : sql.LogicalName,
                    // A bare Guid carries no target table; the column is simply unknown.
                    Guid => null,
                    _ => throw new InvalidOperationException(
                        $"Expected a lookup value but got '{value.GetType().FullName}'."),
                };

            case ColumnKind.WallClock:
                return ConvertWallClock(value);

            default:
                return ConvertScalar(value);
        }
    }

    /// <summary>
    /// A wall-clock value is stored exactly as it arrived. No timezone
    /// conversion, in either direction: a DateOnly birthdate or a
    /// TimeZoneIndependent appointment time means the same reading everywhere,
    /// and shifting it by an offset changes what it says.
    /// </summary>
    private static object? ConvertWallClock(object value) => value switch
    {
        // The offset is dropped rather than applied. A wall-clock attribute
        // should not carry one; if it somehow does, the reading is what was
        // meant and the offset is noise.
        DateTimeOffset offset => offset.DateTime,

        DateTime dateTime => DateTime.SpecifyKind(dateTime, DateTimeKind.Unspecified),

        _ => value,
    };

    private static object? ConvertScalar(object value) => value switch
    {
        // Resolve to a real UTC instant. Keeping the offset would smuggle
        // timezone-aware data into the cache.
        DateTimeOffset offset => offset.UtcDateTime,

        DateTime dateTime => UtcTimestampPolicy.ToUtcInstant(dateTime),

        OptionSetValue option => option.Value,
        Money money => money.Value,
        EntityReference reference => reference.Id,
        SqlEntityReference sql => sql.IsNull ? null : sql.Id,

        // The appender has no concept of these; store the label.
        EntityCollection collection => collection.EntityName,

        _ => value,
    };
}
