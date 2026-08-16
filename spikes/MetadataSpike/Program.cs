using System.Reflection;
using System.ServiceModel;
using MarkMpn.Sql4Cds.Engine;
using MarkMpn.Sql4Cds.Engine.ExecutionPlan;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;

// Spike B: compile SQL -> FetchXML with NO tenant, using injected metadata.
// Proves offline dev/test is possible and shows exactly what pushes down.

var accounts = Entity("account", "accountid", 1,
    Attr("accountid", AttributeTypeCode.Uniqueidentifier),
    Attr("name", AttributeTypeCode.String),
    Attr("statecode", AttributeTypeCode.State),
    Attr("revenue", AttributeTypeCode.Money));

var contacts = Entity("contact", "contactid", 2,
    Attr("contactid", AttributeTypeCode.Uniqueidentifier),
    Attr("fullname", AttributeTypeCode.String),
    Attr("emailaddress1", AttributeTypeCode.String),
    Attr("createdon", AttributeTypeCode.DateTime),
    Attr("parentcustomerid", AttributeTypeCode.Lookup));

var ds = new DataSource
{
    Name = "offline",
    Connection = new NullOrganizationService(),
    Metadata = new OfflineMetadataCache(accounts, contacts),
    TableSizeCache = new FakeTableSizeCache(),
    MessageCache = new EmptyMessageCache(),
};

using var conn = new Sql4CdsConnection(new Dictionary<string, DataSource> { ["offline"] = ds });
conn.UseTDSEndpoint = false;
conn.ApplicationName = "DataverseDuck.Spike";

var queries = new (string Label, string Sql)[]
{
    ("simple filter",
        "SELECT a.accountid, a.name FROM account a WHERE a.statecode = 0"),
    ("2-table join + filter",
        "SELECT a.accountid, a.name, c.emailaddress1 " +
        "FROM account a INNER JOIN contact c ON a.accountid = c.parentcustomerid " +
        "WHERE a.statecode = 0 AND c.createdon > '2026-01-01'"),
    ("aggregate",
        "SELECT a.name, COUNT(*) AS n FROM account a " +
        "INNER JOIN contact c ON a.accountid = c.parentcustomerid GROUP BY a.name"),
    ("column comparison (expect in-memory)",
        "SELECT a.name FROM account a WHERE a.name > a.accountid"),
};

foreach (var (label, sql) in queries)
{
    Console.WriteLine($"\n=== {label} ===");
    try
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var plan in cmd.GeneratePlan(false))
            Walk(plan, 1);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  ERROR {ex.GetType().Name}: {Flat(ex.Message)}");
        Console.WriteLine(ex.InnerException?.StackTrace ?? ex.StackTrace);
    }
}

// Walk the plan tree, surfacing FetchXML and flagging in-memory operators.
static void Walk(object node, int depth)
{
    var t = node.GetType();
    var indent = new string(' ', depth * 2);
    var name = t.Name.Replace("Node", "");
    var warn = name is "NestedLoop" or "HashJoin" or "MergeJoin" or "Filter" or "Sort" or "HashMatchAggregate"
        ? "   <-- IN MEMORY" : "";
    Console.WriteLine($"{indent}{name}{warn}");

    var fx = t.GetProperty("FetchXmlString")?.GetValue(node) as string;
    if (fx is not null)
        foreach (var line in fx.Split('\n').Where(l => l.Trim().Length > 0))
            Console.WriteLine($"{indent}  | {line.TrimEnd()}");

    if (t.GetMethod("GetSources")?.Invoke(node, null) is System.Collections.IEnumerable kids)
        foreach (var k in kids) Walk(k, depth + 1);
}

static string Flat(string s) => s.ReplaceLineEndings(" ");

// ---- offline metadata plumbing -------------------------------------------

static EntityMetadata Entity(string logicalName, string primaryId, int otc, params AttributeMetadata[] attrs)
{
    var e = new EntityMetadata { LogicalName = logicalName, SchemaName = logicalName };
    Set(e, "PrimaryIdAttribute", primaryId);
    Set(e, "PrimaryNameAttribute", attrs.Any(a => a.LogicalName == "name") ? "name" : null);
    Set(e, "EntitySetName", logicalName + "s");
    Set(e, "ObjectTypeCode", otc);
    Set(e, "Attributes", attrs);
    Set(e, "ManyToOneRelationships", Array.Empty<OneToManyRelationshipMetadata>());
    Set(e, "OneToManyRelationships", Array.Empty<OneToManyRelationshipMetadata>());
    Set(e, "ManyToManyRelationships", Array.Empty<ManyToManyRelationshipMetadata>());
    return e;
}

static AttributeMetadata Attr(string logicalName, AttributeTypeCode type)
{
    AttributeMetadata a = type switch
    {
        AttributeTypeCode.Uniqueidentifier => new UniqueIdentifierAttributeMetadata(),
        AttributeTypeCode.String           => new StringAttributeMetadata { MaxLength = 100 },
        AttributeTypeCode.DateTime         => new DateTimeAttributeMetadata(),
        AttributeTypeCode.Money            => new MoneyAttributeMetadata(),
        AttributeTypeCode.State            => new StateAttributeMetadata(),
        AttributeTypeCode.Lookup           => new LookupAttributeMetadata(),
        _                                  => new StringAttributeMetadata(),
    };
    a.LogicalName = logicalName;
    a.SchemaName = logicalName;
    Set(a, "AttributeType", type);
    Set(a, "IsValidForRead", true);
    Set(a, "IsLogical", false);
    Set(a, "IsPrimaryId", type == AttributeTypeCode.Uniqueidentifier && logicalName.EndsWith("id") && !logicalName.StartsWith("parent"));
    Set(a, "IsPrimaryName", logicalName is "name" or "fullname");
    Set(a, "ColumnNumber", System.Threading.Interlocked.Increment(ref ColumnCounter.Value));
    Set(a, "IsValidForCreate", true);
    Set(a, "IsValidForUpdate", true);
    if (a is LookupAttributeMetadata l) Set(l, "Targets", new[] { "account" });
    return a;
}

// EntityMetadata exposes private setters; the platform normally populates them.
static void Set(object target, string prop, object? value)
{
    var p = target.GetType().GetProperty(prop,
        BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
    if (p?.SetMethod is null) { Console.Error.WriteLine($"  (cannot set {prop})"); return; }
    p.SetMethod.Invoke(target, new[] { value });
}

sealed class OfflineMetadataCache(params EntityMetadata[] entities) : IAttributeMetadataCache
{
    readonly Dictionary<string, EntityMetadata> _byName =
        entities.ToDictionary(e => e.LogicalName, StringComparer.OrdinalIgnoreCase);

    public EntityMetadata this[string name] =>
        name is not null && _byName.TryGetValue(name, out var e)
            ? e : throw new FaultException($"Unknown entity '{name ?? "(null)"}'");
    public EntityMetadata this[int otc] =>
        _byName.Values.FirstOrDefault(e => e.ObjectTypeCode == otc) ?? throw new FaultException($"Unknown otc {otc}");
    public string[] RecycleBinEntities => Array.Empty<string>();
    public bool TryGetValue(string logicalName, out EntityMetadata metadata) => _byName.TryGetValue(logicalName, out metadata!);
    public bool TryGetMinimalData(string logicalName, out EntityMetadata metadata) => TryGetValue(logicalName, out metadata);
    public IEnumerable<EntityMetadata> GetAllEntities() => _byName.Values;
    public string[] TryGetRecycleBinEntities() => Array.Empty<string>();
}

// Deliberately asymmetric: contact is the big table, to observe join ordering.
sealed class FakeTableSizeCache : ITableSizeCache
{
    public int this[string logicalName] => logicalName == "contact" ? 2_000_000 : 5_000;
}

sealed class EmptyMessageCache : IMessageCache
{
    public IEnumerable<Message> GetAllMessages(bool throwOnError) => Array.Empty<Message>();
    public bool TryGetValue(string name, out Message message) { message = null!; return false; }
    public bool IsMessageAvailable(string entityLogicalName, string messageName) => false;
}

sealed class NullOrganizationService : IOrganizationService
{
    const string Msg = "offline spike: no live connection";
    public void Associate(string n, Guid i, Relationship r, EntityReferenceCollection e) => throw new NotSupportedException(Msg);
    public Guid Create(Entity entity) => throw new NotSupportedException(Msg);
    public void Delete(string n, Guid id) => throw new NotSupportedException(Msg);
    public void Disassociate(string n, Guid i, Relationship r, EntityReferenceCollection e) => throw new NotSupportedException(Msg);
    public OrganizationResponse Execute(OrganizationRequest request) => throw new NotSupportedException(Msg + ": " + request.RequestName);
    public Entity Retrieve(string n, Guid id, Microsoft.Xrm.Sdk.Query.ColumnSet c) => throw new NotSupportedException(Msg);
    public void Update(Entity entity) => throw new NotSupportedException(Msg);
    public EntityCollection RetrieveMultiple(Microsoft.Xrm.Sdk.Query.QueryBase query)
    {
        var name = (query as Microsoft.Xrm.Sdk.Query.QueryExpression)?.EntityName
                ?? (query as Microsoft.Xrm.Sdk.Query.FetchExpression)?.Query;
        Console.WriteLine($"  [fake RetrieveMultiple] {Flatten(name)}");

        // SQL 4 CDS reads collation/locale from the organization row at startup.
        var org = new Entity("organization", Guid.NewGuid());
        org["organizationid"] = org.Id;
        org["localeid"] = 1033;
        org["collation"] = "SQL_Latin1_General_CP1_CI_AI";
        return new EntityCollection(new List<Entity> { org }) { EntityName = "organization" };
    }

    static string Flatten(string? s) =>
        s is null ? "(null)" : (s.Length > 120 ? s[..120] + "..." : s).ReplaceLineEndings(" ");
}


static class ColumnCounter { public static int Value; }
