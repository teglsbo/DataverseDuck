using System.Data.Common;
using DataverseDuck.Plans;

namespace DataverseDuck;

/// <summary>
/// Something that can run SQL against Dataverse.
///
/// This interface exists so the caching pipeline can be exercised end to end
/// without a tenant. The real implementation compiles through SQL 4 CDS; tests
/// substitute a reader over in-memory rows. Everything downstream — schema
/// mapping, the folding guard, the appender — is identical either way.
/// </summary>
public interface IDataverseQuerySource
{
    /// <summary>
    /// Compiles the statement and reports what will run locally, without
    /// executing it. Returns null if the source cannot produce a plan.
    /// </summary>
    PlanAnalysis? Analyze(string sql);

    /// <summary>As <see cref="Analyze(string)"/>, but cancellable while compiling.</summary>
    PlanAnalysis? Analyze(string sql, CancellationToken cancellationToken) => Analyze(sql);

    /// <summary>
    /// Executes the statement and hands the reader to <paramref name="read"/>.
    ///
    /// Scoped rather than returning the reader, so the implementation can
    /// dispose the underlying command deterministically. The reader must not be
    /// retained beyond the callback.
    /// </summary>
    T Query<T>(string sql, Func<DbDataReader, T> read);

    /// <summary>As <see cref="Query{T}(string, Func{DbDataReader, T})"/>, but cancellable while executing.</summary>
    T Query<T>(string sql, Func<DbDataReader, T> read, CancellationToken cancellationToken) => Query(sql, read);
}
