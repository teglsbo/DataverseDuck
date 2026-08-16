using System.Runtime.Serialization;
using System.Xml;
using Microsoft.Xrm.Sdk.Metadata;

namespace DataverseDuck.Metadata;

/// <summary>
/// A serialized copy of the Dataverse entity metadata needed to compile SQL.
///
/// SQL 4 CDS resolves columns, types and relationships through
/// IAttributeMetadataCache. Synthesising that metadata by hand does not work:
/// the query optimiser reaches into fields that only the platform populates.
/// So capture the real thing once, then replay it offline for development,
/// tests and CI.
///
/// EntityMetadata is a DataContract type, so DataContractSerializer preserves
/// the private setters that reflection cannot reasonably reconstruct.
/// </summary>
public sealed class MetadataSnapshot
{
    private readonly Dictionary<string, EntityMetadata> _entities;

    public MetadataSnapshot(IEnumerable<EntityMetadata> entities)
    {
        ArgumentNullException.ThrowIfNull(entities);
        _entities = entities.ToDictionary(e => e.LogicalName, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Entity logical names contained in the snapshot.</summary>
    public IReadOnlyCollection<string> LogicalNames => _entities.Keys;

    public IReadOnlyCollection<EntityMetadata> Entities => _entities.Values;

    public bool TryGet(string logicalName, out EntityMetadata metadata) =>
        _entities.TryGetValue(logicalName, out metadata!);

    /// <summary>
    /// Serializer configured for the metadata graph. MaxItemsInObjectGraph is
    /// raised because one entity with all attributes and relationships easily
    /// exceeds the default 65536 object limit.
    /// </summary>
    private static DataContractSerializer CreateSerializer() =>
        new(typeof(EntityMetadata[]), new DataContractSerializerSettings
        {
            MaxItemsInObjectGraph = int.MaxValue,
        });

    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        using var stream = File.Create(path);
        Save(stream);
    }

    public void Save(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var writer = XmlDictionaryWriter.CreateBinaryWriter(stream, null, null, ownsStream: false);
        CreateSerializer().WriteObject(writer, _entities.Values.ToArray());
        writer.Flush();
    }

    public static MetadataSnapshot Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Metadata snapshot '{path}' not found. Capture one from a live environment first.", path);

        using var stream = File.OpenRead(path);
        return Load(stream);
    }

    public static MetadataSnapshot Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var reader = XmlDictionaryReader.CreateBinaryReader(stream, XmlDictionaryReaderQuotas.Max);
        var entities = (EntityMetadata[]?)CreateSerializer().ReadObject(reader)
            ?? throw new InvalidDataException("Metadata snapshot was empty or unreadable.");

        return new MetadataSnapshot(entities);
    }
}
