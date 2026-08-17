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

    /// <summary>Initialises the cache from a snapshot, optionally specifying recycle-bin-capable entities.</summary>
    /// <param name="snapshot">The captured metadata to serve.</param>
    /// <param name="recycleBinEntities">Entity logical names that support the recycle bin. Pass null for none.</param>
    public SnapshotMetadataCache(MetadataSnapshot snapshot, string[]? recycleBinEntities = null)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _recycleBinEntities = recycleBinEntities ?? [];
    }

    /// <summary>Convenience factory that loads a snapshot file and wraps it.</summary>
    public static SnapshotMetadataCache FromFile(string path) => new(MetadataSnapshot.Load(path));

    /// <summary>Looks up an entity by logical name. Throws <see cref="System.ServiceModel.FaultException"/> for unknown entities, matching the live platform.</summary>
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

    /// <summary>Looks up an entity by object type code. Throws <see cref="System.ServiceModel.FaultException"/> for unknown codes.</summary>
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

    /// <summary>Entity logical names that the platform considers recycle-bin-capable.</summary>
    public string[] RecycleBinEntities => _recycleBinEntities;

    /// <summary>
    /// Looks up metadata without throwing. Returns false when the entity was not captured.
    /// SQL 4 CDS calls this before the indexer to avoid an exception in normal lookup paths.
    /// </summary>
    public bool TryGetValue(string logicalName, out EntityMetadata metadata)
    {
        if (logicalName is null)
        {
            metadata = null!;
            return false;
        }

        return _snapshot.TryGet(logicalName, out metadata);
    }

    /// <summary>
    /// Alias for <see cref="TryGetValue"/> required by the interface. A snapshot
    /// always has full attribute data; there is no lighter-weight copy to return.
    /// </summary>
    public bool TryGetMinimalData(string logicalName, out EntityMetadata metadata) =>
        TryGetValue(logicalName, out metadata);

    /// <summary>All entities in the snapshot, in capture order.</summary>
    public IEnumerable<EntityMetadata> GetAllEntities() => _snapshot.Entities;

    /// <summary>Entities that support the recycle bin. Returns the same value as <see cref="RecycleBinEntities"/>.</summary>
    public string[] TryGetRecycleBinEntities() => _recycleBinEntities;
}
