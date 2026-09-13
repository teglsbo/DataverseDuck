using System.ServiceModel;
using MarkMpn.Sql4Cds.Engine;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;

namespace DataverseDuck.Metadata;

/// <summary>
/// Serves SQL 4 CDS from a captured <see cref="MetadataSnapshot"/> instead of a
/// live connection, so query compilation works offline and in CI.
/// </summary>
public sealed class SnapshotMetadataCache : IAttributeMetadataCache
{
    private readonly MetadataSnapshot _snapshot;
    private readonly string[] _recycleBinEntities;

    /// <summary>
    /// A distinguishing, non-zero, non-throttling error code. The live
    /// AttributeMetadataCache leaves <c>Detail.ErrorCode</c> at its default of 0
    /// when it wraps an unrelated failure (auth, transport, provider), so
    /// DataverseSchemaMapper treats ErrorCode 0 as "not a genuine unknown-entity
    /// fault" and propagates it instead of falling back. This cache's own fault
    /// must therefore also avoid 0 to be recognised the same way offline.
    /// </summary>
    private const int UnknownEntityErrorCode = -1;

    public SnapshotMetadataCache(MetadataSnapshot snapshot, string[]? recycleBinEntities = null)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _recycleBinEntities = recycleBinEntities ?? [];
    }

    public static SnapshotMetadataCache FromFile(string path) => new(MetadataSnapshot.Load(path));

    public EntityMetadata this[string name]
    {
        get
        {
            if (name is not null && _snapshot.TryGet(name, out var metadata))
                return metadata;

            // SQL 4 CDS expects a FaultException for unknown entities, matching
            // what the live platform returns. The generic form -- and the
            // "not found" wording -- matter too: DataverseSchemaMapper only
            // recognizes a FaultException<OrganizationServiceFault> with a
            // non-zero, non-throttling ErrorCode and a "not found"/"does not
            // exist" message as an unknown entity, the same shape
            // AttributeMetadataCache's fault takes.
            var message =
                $"Entity '{name ?? "(null)"}' was not found: it is not in the metadata snapshot. " +
                $"Re-capture the snapshot including this table. " +
                $"Known: {string.Join(", ", _snapshot.LogicalNames.Order())}";

            throw new FaultException<OrganizationServiceFault>(
                new OrganizationServiceFault { Message = message, ErrorCode = UnknownEntityErrorCode },
                new FaultReason(message));
        }
    }

    public EntityMetadata this[int otc]
    {
        get
        {
            var match = _snapshot.Entities.FirstOrDefault(e => e.ObjectTypeCode == otc);
            if (match is not null)
                return match;

            var message = $"No entity with object type code {otc} was found in the metadata snapshot.";
            throw new FaultException<OrganizationServiceFault>(
                new OrganizationServiceFault { Message = message, ErrorCode = UnknownEntityErrorCode },
                new FaultReason(message));
        }
    }

    public string[] RecycleBinEntities => _recycleBinEntities;

    public bool TryGetValue(string logicalName, out EntityMetadata metadata)
    {
        if (logicalName is null)
        {
            metadata = null!;
            return false;
        }

        return _snapshot.TryGet(logicalName, out metadata);
    }

    public bool TryGetMinimalData(string logicalName, out EntityMetadata metadata) =>
        TryGetValue(logicalName, out metadata);

    public IEnumerable<EntityMetadata> GetAllEntities() => _snapshot.Entities;

    public string[] TryGetRecycleBinEntities() => _recycleBinEntities;
}
