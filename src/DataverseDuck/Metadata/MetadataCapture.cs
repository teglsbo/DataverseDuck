using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;

namespace DataverseDuck.Metadata;

/// <summary>
/// Captures entity metadata from a live environment so it can be replayed offline.
///
/// Deliberately capture-by-name rather than RetrieveAllEntitiesRequest: a full
/// pull is tens of megabytes and slow, and the query surface only ever touches
/// a handful of tables.
/// </summary>
public static class MetadataCapture
{
    /// <summary>
    /// Retrieves full metadata for the named entities.
    /// </summary>
    /// <param name="service">Authenticated organization service.</param>
    /// <param name="logicalNames">Entity logical names, for example "account".</param>
    /// <param name="progress">Optional per-entity progress callback.</param>
    public static MetadataSnapshot Capture(
        IOrganizationService service,
        IEnumerable<string> logicalNames,
        Action<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(logicalNames);

        var names = logicalNames
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (names.Length == 0)
            throw new ArgumentException("At least one entity logical name is required.", nameof(logicalNames));

        var captured = new List<EntityMetadata>(names.Length);

        foreach (var name in names)
        {
            progress?.Invoke(name);

            var response = (RetrieveEntityResponse)service.Execute(new RetrieveEntityRequest
            {
                LogicalName = name,
                // All: attributes, relationships and privileges. SQL 4 CDS needs
                // relationships to fold joins into FetchXML link-entities.
                EntityFilters = EntityFilters.All,
                RetrieveAsIfPublished = true,
            });

            captured.Add(response.EntityMetadata);
        }

        return new MetadataSnapshot(captured);
    }

    /// <summary>
    /// Captures metadata and writes it to disk in one step.
    /// </summary>
    public static MetadataSnapshot CaptureToFile(
        IOrganizationService service,
        IEnumerable<string> logicalNames,
        string path,
        Action<string>? progress = null)
    {
        var snapshot = Capture(service, logicalNames, progress);
        snapshot.Save(path);
        return snapshot;
    }
}
