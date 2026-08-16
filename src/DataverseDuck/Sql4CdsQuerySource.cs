using System.Data.Common;
using DataverseDuck.Plans;
using MarkMpn.Sql4Cds.Engine;

namespace DataverseDuck;

/// <summary>
/// Runs SQL against Dataverse through SQL 4 CDS.
/// </summary>
/// <remarks>
/// Does not own the connection: the caller decides its lifetime, since one
/// connection is normally reused across many cached tables.
/// </remarks>
public sealed class Sql4CdsQuerySource(
    Sql4CdsConnection connection,
    ExecutionPlanAnalyzer? analyzer = null) : IDataverseQuerySource
{
    private readonly Sql4CdsConnection _connection =
        connection ?? throw new ArgumentNullException(nameof(connection));

    private readonly ExecutionPlanAnalyzer _analyzer = analyzer ?? new ExecutionPlanAnalyzer();

    public PlanAnalysis? Analyze(string sql)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return _analyzer.Analyze(command);
    }

    public T Query<T>(string sql, Func<DbDataReader, T> read)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        ArgumentNullException.ThrowIfNull(read);

        using var command = _connection.CreateCommand();
        command.CommandText = sql;

        using var reader = command.ExecuteReader();
        return read(reader);
    }
}
