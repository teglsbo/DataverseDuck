using MarkMpn.Sql4Cds.Engine;
using MarkMpn.Sql4Cds.Engine.ExecutionPlan;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace DataverseDuck.Metadata;

/// <summary>
/// An <see cref="IOrganizationService"/> that answers nothing except the one
/// call SQL 4 CDS makes at connection start-up: an <c>organization</c> row, so
/// it can read the locale and collation. Everything else throws, because a
/// snapshot-only connection has no tenant to talk to.
/// </summary>
/// <remarks>
/// Promoted from <c>spikes/MetadataSpike/Program.cs</c>, which used this to
/// prove offline SQL 4 CDS compilation was possible at all. See ADR 0003.
/// </remarks>
public sealed class SnapshotOnlyOrganizationService : IOrganizationService
{
    private const string Message =
        "This connection was built from a metadata snapshot only (--snapshot), " +
        "with no live Dataverse connection. It can compile and run queries " +
        "against a previously cached --db, but it cannot fetch rows from Dataverse.";

    private bool _answeredNameProbe;
    private bool _answeredLocaleIdProbe;

    public Guid Create(Entity entity) => throw new NotSupportedException(Message);

    public Entity Retrieve(string entityName, Guid id, ColumnSet columnSet) =>
        throw new NotSupportedException(Message);

    public void Update(Entity entity) => throw new NotSupportedException(Message);

    public void Delete(string entityName, Guid id) => throw new NotSupportedException(Message);

    public void Associate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) =>
        throw new NotSupportedException(Message);

    public void Disassociate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) =>
        throw new NotSupportedException(Message);

    public OrganizationResponse Execute(OrganizationRequest request)
    {
        if (request is RetrieveVersionRequest)
            return new RetrieveVersionResponse { Results = { ["Version"] = "9.2.0.0" } };

        throw new NotSupportedException($"{Message} ({request.RequestName})");
    }

    public EntityCollection RetrieveMultiple(QueryBase query)
    {
        if (query is not QueryExpression queryExpression
            || !string.Equals(queryExpression.EntityName, "organization", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException(Message);

        var column = StartupLocaleLookupColumn(queryExpression);
        if (column is null || !TryConsumeStartupProbe(column))
            throw new NotSupportedException(Message);

        // SQL 4 CDS reads collation/locale from the organization row when it
        // builds a DataSource. Anything else asked of "organization" would be
        // a real query and is refused like everything else here.
        var org = new Entity("organization", Guid.NewGuid());
        org["organizationid"] = org.Id;
        org["name"] = "offline";
        org["localeid"] = 1033;
        org["collation"] = "SQL_Latin1_General_CP1_CI_AI";
        return new EntityCollection([org]) { EntityName = "organization" };
    }

    /// <summary>
    /// The startup projection shape alone cannot tell SQL 4 CDS's own probe
    /// apart from an ordinary user query happening to ask for the same single
    /// column -- both look identical. What does distinguish them: SQL 4 CDS
    /// asks for each of "name" and "localeid" at most once per connection
    /// (cached in <c>DataSource.Name</c> and <c>DataSource.DefaultCollation</c>
    /// respectively), so only the first request for each column is answered;
    /// any repeat is a real query and is refused like everything else here.
    /// </summary>
    private bool TryConsumeStartupProbe(string column)
    {
        if (string.Equals(column, "name", StringComparison.OrdinalIgnoreCase))
        {
            if (_answeredNameProbe)
                return false;

            _answeredNameProbe = true;
            return true;
        }

        if (_answeredLocaleIdProbe)
            return false;

        _answeredLocaleIdProbe = true;
        return true;
    }

    /// <summary>
    /// SQL 4 CDS's own start-up probes -- <c>DataSource</c>'s constructor and
    /// <c>LoadDefaultCollation</c> -- each read the organization row
    /// unfiltered, asking for exactly one of these two columns and no other
    /// shape: no WHERE, ORDER BY, JOIN, SELECT *, or TopCount, and never both
    /// columns together (verified against the real MarkMpn.Sql4Cds.Engine
    /// DLL, which ships no source or docs). Returns which column matched, or
    /// null if the shape does not match either probe at all.
    /// </summary>
    private static string? StartupLocaleLookupColumn(QueryExpression query)
    {
        if (query.ColumnSet.AllColumns
            || query.TopCount is > 1
            || query.Criteria.Conditions.Count != 0
            || query.Criteria.Filters.Count != 0
            || query.Orders.Count != 0
            || query.LinkEntities.Count != 0)
        {
            return null;
        }

        var columns = query.ColumnSet.Columns;
        if (columns.Count != 1)
            return null;

        if (string.Equals(columns[0], "name", StringComparison.OrdinalIgnoreCase))
            return "name";

        if (string.Equals(columns[0], "localeid", StringComparison.OrdinalIgnoreCase))
            return "localeid";

        return null;
    }
}

/// <summary>
/// Reports every table as an arbitrary, fixed row count. SQL 4 CDS uses this
/// only to weigh join order; a snapshot-only connection has no real counts to
/// offer, and join order does not affect correctness, only local plan shape.
/// </summary>
public sealed class SnapshotTableSizeCache : ITableSizeCache
{
    public int this[string logicalName] => 1_000;
}

/// <summary>
/// Reports no custom messages/actions. A snapshot captures entity metadata,
/// not the message catalogue, so a snapshot-only connection cannot resolve
/// custom actions -- only ordinary CRUD-shaped SQL against captured tables.
/// </summary>
public sealed class SnapshotMessageCache : IMessageCache
{
    public IEnumerable<Message> GetAllMessages(bool throwOnError) => [];

    public bool TryGetValue(string name, out Message message)
    {
        message = null!;
        return false;
    }

    public bool IsMessageAvailable(string entityLogicalName, string messageName) => false;
}
