using System.Reflection;
using System.ServiceModel;
using DataverseDuck.Metadata;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Xunit;

namespace DataverseDuckTests;

/// <summary>
/// The whole offline story rests on one claim: EntityMetadata survives a
/// serialize/deserialize round trip with its platform-populated, private-setter
/// properties intact. If that breaks, offline development breaks.
/// </summary>
public class MetadataSnapshotTests
{
    /// <summary>
    /// Builds an EntityMetadata the way the platform would, using the SDK's own
    /// internal setter helper. This is the same mechanism the SDK uses when
    /// deserializing a server response.
    /// </summary>
    private static EntityMetadata BuildEntity(string logicalName, int objectTypeCode)
    {
        var entity = new EntityMetadata { LogicalName = logicalName, SchemaName = logicalName };

        SetPrivate(entity, nameof(EntityMetadata.PrimaryIdAttribute), logicalName + "id");
        SetPrivate(entity, nameof(EntityMetadata.PrimaryNameAttribute), "name");
        SetPrivate(entity, nameof(EntityMetadata.ObjectTypeCode), objectTypeCode);
        SetPrivate(entity, nameof(EntityMetadata.EntitySetName), logicalName + "s");

        var id = new UniqueIdentifierAttributeMetadata
        {
            LogicalName = logicalName + "id",
            SchemaName = logicalName + "Id",
        };
        SetPrivate(id, nameof(AttributeMetadata.IsPrimaryId), true);
        SetPrivate(id, nameof(AttributeMetadata.ColumnNumber), 1);

        var name = new StringAttributeMetadata { LogicalName = "name", SchemaName = "Name", MaxLength = 160 };
        SetPrivate(name, nameof(AttributeMetadata.IsPrimaryName), true);
        SetPrivate(name, nameof(AttributeMetadata.ColumnNumber), 2);

        var created = new DateTimeAttributeMetadata
        {
            LogicalName = "createdon",
            SchemaName = "CreatedOn",
            DateTimeBehavior = DateTimeBehavior.UserLocal,
        };
        SetPrivate(created, nameof(AttributeMetadata.ColumnNumber), 3);

        SetPrivate(entity, nameof(EntityMetadata.Attributes), new AttributeMetadata[] { id, name, created });

        return entity;
    }

    private static void SetPrivate(object target, string propertyName, object? value)
    {
        var property = target.GetType().GetProperty(propertyName,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);

        Assert.NotNull(property);
        Assert.NotNull(property!.SetMethod);
        property.SetMethod!.Invoke(target, [value]);
    }

    private static MetadataSnapshot RoundTrip(MetadataSnapshot snapshot)
    {
        using var buffer = new MemoryStream();
        snapshot.Save(buffer);
        buffer.Position = 0;
        return MetadataSnapshot.Load(buffer);
    }

    [Fact]
    public void Round_trip_preserves_private_setter_properties()
    {
        var original = new MetadataSnapshot([BuildEntity("account", 1)]);

        var restored = RoundTrip(original);

        Assert.True(restored.TryGet("account", out var entity));
        Assert.Equal("account", entity.LogicalName);
        Assert.Equal("accountid", entity.PrimaryIdAttribute);
        Assert.Equal("name", entity.PrimaryNameAttribute);
        Assert.Equal(1, entity.ObjectTypeCode);
        Assert.Equal("accounts", entity.EntitySetName);
    }

    [Fact]
    public void Round_trip_preserves_attribute_subtypes_and_flags()
    {
        var restored = RoundTrip(new MetadataSnapshot([BuildEntity("account", 1)]));
        Assert.True(restored.TryGet("account", out var entity));

        var id = Assert.Single(entity.Attributes.Where(a => a.LogicalName == "accountid"));
        Assert.IsType<UniqueIdentifierAttributeMetadata>(id);
        Assert.True(id.IsPrimaryId);
        Assert.Equal(1, id.ColumnNumber);

        var name = Assert.Single(entity.Attributes.Where(a => a.LogicalName == "name"));
        var stringAttribute = Assert.IsType<StringAttributeMetadata>(name);
        Assert.Equal(160, stringAttribute.MaxLength);
        Assert.True(name.IsPrimaryName);
    }

    [Fact]
    public void Round_trip_preserves_DateTimeBehavior_which_drives_utc_handling()
    {
        var restored = RoundTrip(new MetadataSnapshot([BuildEntity("account", 1)]));
        Assert.True(restored.TryGet("account", out var entity));

        var created = Assert.IsType<DateTimeAttributeMetadata>(
            entity.Attributes.Single(a => a.LogicalName == "createdon"));

        Assert.Equal(DateTimeBehavior.UserLocal.Value, created.DateTimeBehavior?.Value);

        // And the timestamp policy still reads it correctly after the round trip.
        var mapping = DataverseDuck.UtcTimestampPolicy.MapDateTimeAttribute(created);
        Assert.Equal("TIMESTAMP", mapping.DuckDbType);
        Assert.True(mapping.ConvertToUtc);
    }

    [Fact]
    public void Snapshot_survives_a_file_round_trip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dvduck-{Guid.NewGuid():N}", "metadata.bin");
        try
        {
            new MetadataSnapshot([BuildEntity("account", 1), BuildEntity("contact", 2)]).Save(path);

            Assert.True(File.Exists(path));
            var restored = MetadataSnapshot.Load(path);
            Assert.Equal(2, restored.LogicalNames.Count);
            Assert.True(restored.TryGet("contact", out var contact));
            Assert.Equal(2, contact.ObjectTypeCode);
        }
        finally
        {
            var dir = Path.GetDirectoryName(path);
            if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Loading_a_missing_snapshot_explains_how_to_create_one()
    {
        var ex = Assert.Throws<FileNotFoundException>(
            () => MetadataSnapshot.Load(Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.bin")));

        Assert.Contains("Capture one from a live environment", ex.Message);
    }

    [Fact]
    public void Cache_lookup_is_case_insensitive_like_the_platform()
    {
        var cache = new SnapshotMetadataCache(new MetadataSnapshot([BuildEntity("account", 1)]));

        Assert.Equal("account", cache["ACCOUNT"].LogicalName);
        Assert.True(cache.TryGetValue("Account", out _));
    }

    [Fact]
    public void Cache_lookup_by_object_type_code_works()
    {
        var cache = new SnapshotMetadataCache(
            new MetadataSnapshot([BuildEntity("account", 1), BuildEntity("contact", 2)]));

        Assert.Equal("contact", cache[2].LogicalName);
    }

    [Fact]
    public void Unknown_entity_names_what_is_available()
    {
        var cache = new SnapshotMetadataCache(new MetadataSnapshot([BuildEntity("account", 1)]));

        var ex = Assert.Throws<FaultException>(() => _ = cache["opportunity"]);
        Assert.Contains("opportunity", ex.Message);
        Assert.Contains("account", ex.Message);
    }

    [Fact]
    public void Null_entity_name_does_not_throw_from_TryGetValue()
    {
        var cache = new SnapshotMetadataCache(new MetadataSnapshot([BuildEntity("account", 1)]));

        Assert.False(cache.TryGetValue(null!, out _));
    }

    [Fact]
    public void Capture_requests_full_metadata_for_each_named_entity()
    {
        var service = new RecordingOrganizationService();

        var snapshot = MetadataCapture.Capture(service, ["account", "contact"]);

        Assert.Equal(2, snapshot.LogicalNames.Count);
        Assert.Equal(["account", "contact"], service.Requested);
        Assert.All(service.Filters, f => Assert.Equal(EntityFilters.All, f));
    }

    [Fact]
    public void Capture_deduplicates_names_case_insensitively()
    {
        var service = new RecordingOrganizationService();

        MetadataCapture.Capture(service, ["account", "ACCOUNT", " account "]);

        Assert.Single(service.Requested);
    }

    [Fact]
    public void Capture_rejects_an_empty_entity_list()
    {
        Assert.Throws<ArgumentException>(
            () => MetadataCapture.Capture(new RecordingOrganizationService(), []));
    }

    /// <summary>Records RetrieveEntityRequest calls and returns built metadata.</summary>
    private sealed class RecordingOrganizationService : IOrganizationService
    {
        public List<string> Requested { get; } = [];
        public List<EntityFilters> Filters { get; } = [];

        public OrganizationResponse Execute(OrganizationRequest request)
        {
            var retrieve = Assert.IsType<RetrieveEntityRequest>(request);
            Requested.Add(retrieve.LogicalName);
            Filters.Add(retrieve.EntityFilters);

            var response = new RetrieveEntityResponse();
            response.Results["EntityMetadata"] =
                BuildEntity(retrieve.LogicalName, Requested.Count);
            return response;
        }

        public void Associate(string n, Guid i, Relationship r, EntityReferenceCollection e) => throw new NotSupportedException();
        public Guid Create(Entity entity) => throw new NotSupportedException();
        public void Delete(string n, Guid id) => throw new NotSupportedException();
        public void Disassociate(string n, Guid i, Relationship r, EntityReferenceCollection e) => throw new NotSupportedException();
        public Entity Retrieve(string n, Guid id, Microsoft.Xrm.Sdk.Query.ColumnSet c) => throw new NotSupportedException();
        public EntityCollection RetrieveMultiple(Microsoft.Xrm.Sdk.Query.QueryBase query) => throw new NotSupportedException();
        public void Update(Entity entity) => throw new NotSupportedException();
    }
}
