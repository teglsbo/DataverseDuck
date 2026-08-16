using System.ServiceModel;
using MarkMpn.Sql4Cds.Engine;
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
            // what the live platform returns.
            throw new FaultException(
                $"Entity '{name ?? "(null)"}' is not in the metadata snapshot. " +
                $"Re-capture the snapshot including this table. " +
                $"Known: {string.Join(", ", _snapshot.LogicalNames.Order())}");
        }
    }

    public EntityMetadata this[int otc]
    {
        get
        {
            var match = _snapshot.Entities.FirstOrDefault(e => e.ObjectTypeCode == otc);
            return match ?? throw new FaultException(
                $"No entity with object type code {otc} in the metadata snapshot.");
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
