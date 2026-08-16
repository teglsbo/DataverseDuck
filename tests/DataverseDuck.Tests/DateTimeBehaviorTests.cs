using DataverseDuck;
using DataverseDuck.Schema;
using System.ServiceModel;
using MarkMpn.Sql4Cds.Engine;
using Microsoft.Xrm.Sdk.Metadata;

namespace DataverseDuck.Tests;

/// <summary>
/// Dataverse datetime attributes come in three behaviours and only one of them
/// is an instant. The reader cannot tell them apart -- measured against a live
/// environment, a DateOnly birthdate, a TimeZoneIndependent value and a
/// UserLocal timestamp all arrive as DateTime with Kind=Unspecified. The
/// distinction exists only in metadata, so these tests drive it from there.
/// </summary>
public class DateTimeBehaviorTests
{
    private static EntityMetadata Contact() =>
        FakeMetadata.Entity("contact",
            FakeMetadata.DateTime("birthdate", DateTimeBehavior.DateOnly, DateTimeFormat.DateOnly),
            FakeMetadata.DateTime("appointmenttime", DateTimeBehavior.TimeZoneIndependent, DateTimeFormat.DateAndTime),
            FakeMetadata.DateTime("createdon", DateTimeBehavior.UserLocal, DateTimeFormat.DateAndTime),
            FakeMetadata.DateTime("lastusedincampaign", DateTimeBehavior.UserLocal, DateTimeFormat.DateOnly));

    private static DataverseSchemaMapper Mapper(IAttributeMetadataCache metadata) =>
        new() { Metadata = metadata };

    [Theory]
    [InlineData("birthdate", "DATE", ColumnKind.WallClock)]
    [InlineData("appointmenttime", "TIMESTAMP", ColumnKind.WallClock)]
    [InlineData("createdon", "TIMESTAMP", ColumnKind.Scalar)]
    public void Behaviour_decides_the_type_and_whether_time_may_be_shifted(
        string column, string expectedType, ColumnKind expectedKind)
    {
        var mapper = Mapper(new FakeMetadata(Contact()));

        var mapping = Assert.Single(mapper.MapColumn(column, typeof(DateTime), 0, ("contact", column)));

        Assert.Equal(expectedType, mapping.DuckDbType);
        Assert.Equal(expectedKind, mapping.Kind);
    }

    [Fact]
    public void Format_is_not_behaviour()
    {
        // lastusedincampaign is Format=DateOnly but Behavior=UserLocal: a real
        // instant that merely displays as a date. Keying off Format -- the
        // obvious choice -- would strip the time from a genuine timestamp.
        // Both stock Dataverse attributes with this shape were found in a live
        // environment, so this is not a hypothetical combination.
        var mapper = Mapper(new FakeMetadata(Contact()));

        var mapping = Assert.Single(
            mapper.MapColumn("lastusedincampaign", typeof(DateTime), 0, ("contact", "lastusedincampaign")));

        Assert.Equal("TIMESTAMP", mapping.DuckDbType);
        Assert.Equal(ColumnKind.Scalar, mapping.Kind);
    }

    [Fact]
    public void A_wall_clock_value_is_never_shifted()
    {
        // The reading is the data. 1980-05-15 is that date everywhere; moving
        // it by an offset makes it the 14th for anyone west of UTC.
        var birthdate = new DateTime(1980, 5, 15, 0, 0, 0, DateTimeKind.Utc);

        var stored = Assert.IsType<DateTime>(
            DataverseSchemaMapper.ConvertValue(birthdate, ColumnKind.WallClock));

        Assert.Equal(new DateTime(1980, 5, 15), stored);
        Assert.Equal(DateTimeKind.Unspecified, stored.Kind);
    }

    [Fact]
    public void A_wall_clock_value_keeps_its_reading_even_with_an_offset()
    {
        // Contrast with ColumnKind.Scalar, where the offset is applied.
        var value = new DateTimeOffset(2026, 8, 16, 9, 0, 0, TimeSpan.FromHours(2));

        Assert.Equal(
            new DateTime(2026, 8, 16, 9, 0, 0),
            DataverseSchemaMapper.ConvertValue(value, ColumnKind.WallClock));

        Assert.Equal(
            new DateTime(2026, 8, 16, 7, 0, 0),
            DataverseSchemaMapper.ConvertValue(value, ColumnKind.Scalar));
    }

    [Fact]
    public void A_lazily_loaded_metadata_cache_is_still_consulted()
    {
        // Regression. The live AttributeMetadataCache returns false from
        // TryGetValue for an entity it has not fetched yet, and only reports
        // true after the indexer has loaded it. Using TryGetValue here meant
        // metadata was silently skipped on every first call, so birthdate
        // cached as TIMESTAMP against a real tenant while every offline test
        // passed.
        var metadata = new FakeMetadata(Contact()) { AnswerTryGetValueBeforeLoad = false };

        var mapping = Assert.Single(
            Mapper(metadata).MapColumn("birthdate", typeof(DateTime), 0, ("contact", "birthdate")));

        Assert.Equal("DATE", mapping.DuckDbType);
    }

    [Fact]
    public void An_unresolvable_table_falls_back_to_an_instant()
    {
        // A computed column has no entity behind it. That is not an error, and
        // an instant is the same answer as having no metadata at all.
        var mapping = Assert.Single(
            Mapper(new FakeMetadata(Contact()))
                .MapColumn("whenever", typeof(DateTime), 0, ("no_such_table", "whenever")));

        Assert.Equal("TIMESTAMP", mapping.DuckDbType);
        Assert.Equal(ColumnKind.Scalar, mapping.Kind);
    }

    [Fact]
    public void Without_metadata_every_datetime_is_an_instant()
    {
        var mapping = Assert.Single(new DataverseSchemaMapper().MapColumn("birthdate", typeof(DateTime), 0));

        Assert.Equal("TIMESTAMP", mapping.DuckDbType);
        Assert.Equal(ColumnKind.Scalar, mapping.Kind);
    }
}

internal sealed class FakeMetadata(params EntityMetadata[] entities) : IAttributeMetadataCache
{
    private readonly HashSet<string> _loaded = [];

    /// <summary>
    /// When false, mimics the live cache: TryGetValue reports false until the
    /// indexer has loaded the entity.
    /// </summary>
    public bool AnswerTryGetValueBeforeLoad { get; init; } = true;

    public EntityMetadata this[string name]
    {
        get
        {
            var entity = entities.FirstOrDefault(e =>
                string.Equals(e.LogicalName, name, StringComparison.OrdinalIgnoreCase))
                ?? throw new FaultException<Microsoft.Xrm.Sdk.OrganizationServiceFault>(
                    new Microsoft.Xrm.Sdk.OrganizationServiceFault(), new FaultReason($"No entity {name}."));

            _loaded.Add(entity.LogicalName);
            return entity;
        }
    }

    public EntityMetadata this[int otc] => entities.First(e => e.ObjectTypeCode == otc);

    public string[] RecycleBinEntities => [];

    public bool TryGetValue(string logicalName, out EntityMetadata metadata)
    {
        metadata = entities.FirstOrDefault(e =>
            string.Equals(e.LogicalName, logicalName, StringComparison.OrdinalIgnoreCase))!;

        if (metadata is null)
            return false;

        return AnswerTryGetValueBeforeLoad || _loaded.Contains(metadata.LogicalName);
    }

    public bool TryGetMinimalData(string logicalName, out EntityMetadata metadata) =>
        TryGetValue(logicalName, out metadata);

    public IEnumerable<EntityMetadata> GetAllEntities() => entities;

    public string[] TryGetRecycleBinEntities() => [];

    public static EntityMetadata Entity(string logicalName, params AttributeMetadata[] attributes)
    {
        var entity = new EntityMetadata { LogicalName = logicalName };
        SetProperty(entity, nameof(EntityMetadata.Attributes), attributes);
        return entity;
    }

    public static DateTimeAttributeMetadata DateTime(
        string logicalName, DateTimeBehavior behavior, DateTimeFormat format)
    {
        var attribute = new DateTimeAttributeMetadata { LogicalName = logicalName, Format = format };
        SetProperty(attribute, nameof(DateTimeAttributeMetadata.DateTimeBehavior), behavior);
        return attribute;
    }

    /// <summary>
    /// The SDK metadata classes expose these as read-only, because normally
    /// they only ever arrive deserialised from the service.
    /// </summary>
    private static void SetProperty(object target, string name, object value) =>
        target.GetType()
            .GetProperty(name)!
            .SetValue(target, value, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null, null, null);
}
