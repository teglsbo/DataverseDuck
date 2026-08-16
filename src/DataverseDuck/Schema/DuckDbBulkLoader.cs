using System.Data.Common;
using DuckDB.NET.Data;

namespace DataverseDuck.Schema;

/// <summary>
/// Streams a result set into a DuckDB table using the appender.
///
/// Rows are never buffered: the reader is pulled one row at a time and pushed
/// straight into the appender, so memory stays flat regardless of table size.
/// That matters because the whole point of pushing joins into Dataverse is to
/// avoid materialising large tables locally.
/// </summary>
public sealed class DuckDbBulkLoader(DuckDBConnection connection)
{
    private readonly DuckDBConnection _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    /// <summary>Rows between progress callbacks.</summary>
    public int ProgressInterval { get; init; } = 10_000;

    /// <summary>
    /// Creates the table from the reader's schema and loads every row.
    /// </summary>
    /// <returns>The mapping used, and how many rows were written.</returns>
    public LoadResult Load(
        DbDataReader reader,
        string tableName,
        DataverseSchemaMapper? mapper = null,
        Action<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);

        mapper ??= new DataverseSchemaMapper();
        var mapping = mapper.MapReader(reader, tableName);

        using (var create = _connection.CreateCommand())
        {
            create.CommandText = mapping.ToCreateTableSql();
            create.ExecuteNonQuery();
        }

        var rows = LoadInto(reader, mapping, progress, cancellationToken);
        return new LoadResult(mapping, rows);
    }

    /// <summary>
    /// Loads into an existing table using a known mapping.
    /// </summary>
    public long LoadInto(
        DbDataReader reader,
        TableMapping mapping,
        Action<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(mapping);

        var rows = 0L;

        // GetValues writes DBNull.Value for null columns, never a null reference,
        // so the buffer is non-nullable. ConvertValue maps DBNull to null.
        var buffer = new object[reader.FieldCount];

        using var appender = _connection.CreateAppender(mapping.TableName);

        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();

            reader.GetValues(buffer);
            var row = appender.CreateRow();

            foreach (var column in mapping.Columns)
            {
                var raw = column.SourceColumn < buffer.Length ? buffer[column.SourceColumn] : null;
                Append(row, DataverseSchemaMapper.ConvertValue(raw, column.Kind));
            }

            row.EndRow();
            rows++;

            if (progress is not null && ProgressInterval > 0 && rows % ProgressInterval == 0)
                progress(rows);
        }

        return rows;
    }

    /// <summary>
    /// Appends a converted value. Typed rather than reflective, because the
    /// appender's overload resolution is compile-time and a boxed object would
    /// silently pick the wrong one.
    /// </summary>
    private static void Append(IDuckDBAppenderRow row, object? value)
    {
        switch (value)
        {
            case null:
                row.AppendNullValue();
                break;
            case bool v: row.AppendValue(v); break;
            case byte v: row.AppendValue(v); break;
            case sbyte v: row.AppendValue(v); break;
            case short v: row.AppendValue(v); break;
            case int v: row.AppendValue(v); break;
            case long v: row.AppendValue(v); break;
            case float v: row.AppendValue(v); break;
            case double v: row.AppendValue(v); break;
            case decimal v: row.AppendValue(v); break;
            case Guid v: row.AppendValue(v); break;
            case string v: row.AppendValue(v); break;
            case byte[] v: row.AppendValue(v); break;
            case DateTime v: row.AppendValue(v); break;
            default:
                throw new NotSupportedException(
                    $"The DuckDB appender has no overload for '{value.GetType().FullName}'. " +
                    $"Convert it in {nameof(DataverseSchemaMapper)}.{nameof(DataverseSchemaMapper.ConvertValue)} first.");
        }
    }
}

/// <param name="Mapping">The table shape that was created.</param>
/// <param name="RowCount">Rows written.</param>
public sealed record LoadResult(TableMapping Mapping, long RowCount);
