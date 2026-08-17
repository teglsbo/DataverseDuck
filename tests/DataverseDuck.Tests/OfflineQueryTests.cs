using System.Reflection;
using DataverseDuck;
using DataverseDuck.Metadata;
using Microsoft.Xrm.Sdk.Metadata;
using Xunit;

namespace DataverseDuckTests;

/// <summary>
/// Proves the offline path promised by ADR 0003 and wired up in
/// <c>dvduck query --snapshot</c>: SQL 4 CDS can compile and run a query
/// against captured metadata with no live Dataverse connection at all, as
/// long as the query only touches an already-cached table.
/// </summary>
public class OfflineQueryTests
{
    private static EntityMetadata BuildEntity(string logicalName, int objectTypeCode)
    {
        var entity = new EntityMetadata { LogicalName = logicalName, SchemaName = logicalName };

        SetPrivate(entity, nameof(EntityMetadata.PrimaryIdAttribute), logicalName + "id");
        SetPrivate(entity, nameof(EntityMetadata.PrimaryNameAttribute), "name");
        SetPrivate(entity, nameof(EntityMetadata.ObjectTypeCode), objectTypeCode);
        SetPrivate(entity, nameof(EntityMetadata.EntitySetName), logicalName + "s");

        var id = new UniqueIdentifierAttributeMetadata { LogicalName = logicalName + "id", SchemaName = logicalName + "Id" };
        SetPrivate(id, nameof(AttributeMetadata.IsPrimaryId), true);
        SetPrivate(id, nameof(AttributeMetadata.ColumnNumber), 1);
        SetPrivate(id, nameof(AttributeMetadata.EntityLogicalName), logicalName);

        var name = new StringAttributeMetadata { LogicalName = "name", SchemaName = "Name", MaxLength = 160 };
        SetPrivate(name, nameof(AttributeMetadata.IsPrimaryName), true);
        SetPrivate(name, nameof(AttributeMetadata.ColumnNumber), 2);
        SetPrivate(name, nameof(AttributeMetadata.EntityLogicalName), logicalName);

        SetPrivate(entity, nameof(EntityMetadata.Attributes), new AttributeMetadata[] { id, name });
        SetPrivate(entity, nameof(EntityMetadata.ManyToOneRelationships), Array.Empty<OneToManyRelationshipMetadata>());
        SetPrivate(entity, nameof(EntityMetadata.OneToManyRelationships), Array.Empty<OneToManyRelationshipMetadata>());
        SetPrivate(entity, nameof(EntityMetadata.ManyToManyRelationships), Array.Empty<ManyToManyRelationshipMetadata>());

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

    [Fact]
    public void CanCompileAndAnalyseAQueryWithNoLiveConnection()
    {
        var snapshot = new MetadataSnapshot([BuildEntity("account", 1)]);
        var cache = new SnapshotMetadataCache(snapshot);

        using var connection = Sql4CdsConnectionFactory.CreateOffline(cache);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT accountid, name FROM account WHERE name = 'Contoso'";

        var plans = command.GeneratePlan(false);

        Assert.NotEmpty(plans);
    }

    [Fact]
    public void ThrowsWhenTheQueryNeedsALiveFetch()
    {
        var snapshot = new MetadataSnapshot([BuildEntity("account", 1)]);
        var cache = new SnapshotMetadataCache(snapshot);

        using var connection = Sql4CdsConnectionFactory.CreateOffline(cache);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT accountid FROM account";

        // Compiling is offline-safe; actually reading rows is not, because
        // there is no tenant behind SnapshotOnlyOrganizationService. SQL 4 CDS
        // wraps the NotSupportedException from the RetrieveMultiple call in
        // its own exception type, and the wrapping only surfaces on the first
        // Read() rather than on ExecuteReader() itself.
        using var reader = command.ExecuteReader();
        var ex = Assert.Throws<MarkMpn.Sql4Cds.Engine.Sql4CdsException>(() => reader.Read());
        Assert.IsType<NotSupportedException>(ex.InnerException?.InnerException);
    }
}
