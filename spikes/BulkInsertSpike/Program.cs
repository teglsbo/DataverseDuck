using System.Diagnostics;
using System.Globalization;
using System.Text;
using DataverseDuck;
using DataverseDuck.Configuration;
using DataverseDuck.Diagnostics;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;

// Writes through SQL 4 CDS, which nothing else in this repository does: ADR 0001
// records the workload as read-only, so the engine's DML path has never been
// exercised here. This measures what an INSERT actually costs and confirms the
// engine batches it rather than issuing one request per row.
//
// Every row gets a deterministic id in a namespace nothing else uses:
//
//     dddddddd-0000-0000-0000-<12-digit index>
//
// That is what makes --delete exact. It removes the ids this spike would have
// created and nothing else, so a mistake here cannot take a real contact with it.
// Re-running --insert over existing ids fails rather than duplicating, which is
// the safer direction.

const string IdPrefix = "dddddddd-0000-0000-0000-";
const int DefaultCount = 1000;

var count = ArgValue("--count") is { } raw && int.TryParse(raw, out var parsed) ? parsed : DefaultCount;
var statementRows = ArgValue("--statement-rows") is { } sr && int.TryParse(sr, out var srv) ? srv : 250;
var batchSize = ArgValue("--batch-size") is { } bs && int.TryParse(bs, out var bsv) ? bsv : 100;
// Null means "ask the environment". x-ms-dop-hint describes the environment
// rather than one web server, which makes it the one service-protection header
// worth acting on -- see docs/service-protection.md. Design against the hint,
// not against the documented concurrency limit.
var maxDopOverride = ArgValue("--max-dop") is { } md && int.TryParse(md, out var mdv) ? mdv : (int?)null;
var bulkDelete = args.Contains("--bulk-delete");
// Affinity on concentrates the load on one web server, which is the only way its
// per-server budget counters describe what we did. It costs throughput; that is
// the point of the measurement.
var pinAffinity = args.Contains("--pin-affinity");
// thin = the primary key alone, the least a create can carry. wide = ten columns
// including a long description. Reads are dominated by column width
// (docs/large-tables.md); whether writes are is untested.
// A delete must check every relationship pointing at the row, and cascade the ones
// configured to. Measured from this environment's metadata: contact has 52 inbound
// 1:N relationships of which 39 cascade; annotation has 5 and 1. If that dominates
// delete cost, the two entities should differ by far more than their column counts.
// Custom tables are addressed by logical name; the id column is <logicalname>id and
// the primary name column is <prefix>_name, which is how the SDK creates them.
var entity = ArgValue("--entity")?.Trim().ToLowerInvariant() ?? "contact";
var isCustomEntity = entity.Contains('_');

// One arbitrary statement, for the DDL this study needs: SQL 4 CDS compiles
// CREATE TABLE / DROP TABLE into Dataverse entity metadata operations, so a
// purpose-built test table needs no SDK detour.
var exec = ArgValue("--exec");
// SQL 4 CDS drives writes through ExecuteMultiple (InsertNode.ExecuteMultiple, by
// reflection). Dataverse also registers CreateMultiple / DeleteMultiple, which are
// purpose-built bulk messages rather than a batch of individual requests. This mode
// goes straight to those through the SDK, with the same batch and parallelism
// semantics, so the two paths are comparable.
var native = args.Contains("--native");
// A purpose-built table is the only low-cascade control available: annotation still
// carries 5 inbound relationships and note-attachment machinery, and every stock
// table differs from contact in more than one dimension at once. SQL 4 CDS cannot
// create one -- its CREATE TABLE is for temp tables -- so this goes through the SDK.
var createEntity = ArgValue("--create-entity");
var dropEntity = ArgValue("--drop-entity");
var extraColumns = ArgValue("--extra-columns") is { } ec && int.TryParse(ec, out var ecv) ? ecv : 0;
// Elastic tables are the only place DeleteMultiple is supported, so the one
// interesting bulk-delete comparison lives here. Different storage engine
// (Cosmos-backed), so absolute numbers do not transfer to standard tables -- but
// ExecuteMultiple vs DeleteMultiple on the same rows does.
var elastic = args.Contains("--elastic");
// Omit the primary key and let Dataverse generate it. The received wisdom is that
// client-supplied random GUIDs fragment the clustered index and cost insert
// throughput; every other measurement in this spike used explicit random GUIDs, so
// if that is true they are all the pessimistic case. This tests it.
var generatedIds = args.Contains("--generated-ids");

var shape = ArgValue("--columns")?.Trim().ToLowerInvariant() ?? "normal";
if (shape is not ("thin" or "normal" or "wide"))
{
    Console.Error.WriteLine("--columns must be thin, normal or wide.");
    return 2;
}
var budgetSamples = ArgValue("--budget-samples") is { } bsm && int.TryParse(bsm, out var bsmv) ? bsmv : 0;
var deleting = args.Contains("--delete");
var dryRun = args.Contains("--dry-run");

if (count is < 1 or > 100_000)
{
    Console.Error.WriteLine("--count must be between 1 and 100000.");
    return 2;
}

DotEnvFile.LoadFromCurrentDirectory();

if (!DataverseOptions.TryLoadFromEnvironment(out var options, out var error))
{
    Console.Error.WriteLine(error);
    return 2;
}

if (exec is not null)
{
    Console.WriteLine($"Executing against {options.EnvironmentUrl}:");
    Console.WriteLine($"  {exec}");
}
else
{
    Console.WriteLine($"{(deleting ? "Deleting" : "Inserting")} {count:N0} {entity} row(s) in {options.EnvironmentUrl}");
}
Console.WriteLine($"  id namespace     {IdPrefix}<index>");
Console.WriteLine($"  entity           {entity}"
    + (entity == "contact" ? "  (52 inbound FKs, 39 cascading)" : "  (5 inbound FKs, 1 cascading)"));
Console.WriteLine($"  columns          {shape}");
Console.WriteLine($"  primary key      {(generatedIds ? "generated by Dataverse" : "supplied by the client (random GUID)")}");
Console.WriteLine($"  rows/statement   {statementRows}");
Console.WriteLine($"  BatchSize        {batchSize}");
if (deleting) Console.WriteLine($"  UseBulkDelete    {bulkDelete}");
Console.WriteLine();

var ids = Enumerable.Range(1, count)
    .Select(i => $"{IdPrefix}{i.ToString("D12", CultureInfo.InvariantCulture)}")
    .ToList();

var statements = exec is not null
    ? [exec]
    : deleting ? DeleteStatements(ids) : InsertStatements(ids);

if (dryRun)
{
    var first = statements.First();
    Console.WriteLine($"--dry-run: {statements.Count} statement(s). The first, truncated:");
    Console.WriteLine(first.Length > 600 ? first[..600] + " ..." : first);
    return 0;
}

// The credential decides how to connect, so this works under a client secret, a
// certificate, or a cached device-code sign-in alike.
using var service = options.CreateServiceClient();

if (!service.IsReady)
{
    Console.Error.WriteLine($"Connection failed: {service.LastError}");
    return 1;
}

// Raises the SDK's per-request timeout and retry ceiling; a batched write can
// exceed the four-minute default the same way a wide FetchXML page can.
Sql4CdsConnectionFactory.ConfigureForBulkExport(service);

// The SDK pins to one web server by default, which serializes concurrent
// requests behind that server. Turning affinity off lets the farm spread them,
// which is the point of a degree of parallelism above one. The cost is that the
// per-server budget counters in `dvduck doctor` stop describing the servers
// actually doing the work -- throughput bought with observability.
service.EnableAffinityCookie = !pinAffinity;

// Probed after the connection is proven but before any write, so the hint comes
// from the environment we are about to load rather than from a guess.
int maxDop;
if (maxDopOverride is { } forced)
{
    maxDop = forced;
    Console.WriteLine($"  MaxDegreeOfParallelism {maxDop} (from --max-dop)");
}
else
{
    var budget = await ServiceProtectionBudget.ProbeAsync(options);
    maxDop = budget.RecommendedParallelism ?? 1;
    Console.WriteLine($"  MaxDegreeOfParallelism {maxDop} "
        + (budget.RecommendedParallelism is null
            ? $"(no {ServiceProtectionBudget.ParallelismHintHeader}; defaulting to serial)"
            : $"(from {ServiceProtectionBudget.ParallelismHintHeader})"));
    if (budget.ServerAffinity is { } affinity)
    {
        Console.WriteLine($"  probe answered by   {affinity[..Math.Min(16, affinity.Length)]}…");
    }
}

Console.WriteLine();

if (createEntity is not null || dropEntity is not null)
{
    if (dropEntity is not null)
    {
        Console.WriteLine($"Deleting entity {dropEntity} ...");
        service.Execute(new DeleteEntityRequest { LogicalName = dropEntity });
        Console.WriteLine("  done.");
        return 0;
    }

    var schemaName = createEntity!;
    var prefix = schemaName[..schemaName.IndexOf('_')];

    // Labels must be in a language the organization has installed. Hard-coding 1033
    // failed here with "The language code 1033 is not a valid language for this
    // organization" -- and only on CreateAttributeRequest: CreateEntityRequest
    // accepted the same label. Read the base language rather than assume English.
    var orgQuery = new Microsoft.Xrm.Sdk.Query.QueryExpression("organization")
    {
        ColumnSet = new Microsoft.Xrm.Sdk.Query.ColumnSet("languagecode"),
        TopCount = 1,
    };
    var lcid = service.RetrieveMultiple(orgQuery).Entities
        .Select(e => e.GetAttributeValue<int?>("languagecode"))
        .FirstOrDefault() ?? 1033;

    Console.WriteLine($"Creating {(elastic ? "ELASTIC" : "standard")} entity {schemaName} with "
        + $"{extraColumns} extra column(s), labels in LCID {lcid} ...");

    var entityExists = ArgValue("--columns-only") is not null;
    if (!entityExists) service.Execute(new CreateEntityRequest
    {
        Entity = new EntityMetadata
        {
            SchemaName = schemaName,
            DisplayName = new Microsoft.Xrm.Sdk.Label("dvduck load test", lcid),
            DisplayCollectionName = new Microsoft.Xrm.Sdk.Label("dvduck load tests", lcid),
            OwnershipType = OwnershipTypes.UserOwned,
            IsActivity = false,
            TableType = elastic ? "Elastic" : null,
            // An elastic table is provisioned through the virtual-entity path
            // internally, and chart creation fails there: "Cannot create charts for
            // virtual Entity". Disable them explicitly rather than letting the
            // default attempt fail the whole create.
            CanCreateCharts = elastic ? new Microsoft.Xrm.Sdk.BooleanManagedProperty(false) : null,
            CanCreateViews = elastic ? new Microsoft.Xrm.Sdk.BooleanManagedProperty(false) : null,
            CanCreateForms = elastic ? new Microsoft.Xrm.Sdk.BooleanManagedProperty(false) : null,
        },
        PrimaryAttribute = new StringAttributeMetadata
        {
            SchemaName = $"{prefix}_name",
            RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None),
            MaxLength = 100,
            FormatName = StringFormatName.Text,
            DisplayName = new Microsoft.Xrm.Sdk.Label("Name", lcid),
        },
    });

    for (var i = 1; i <= extraColumns; i++)
    {
        service.Execute(new CreateAttributeRequest
        {
            EntityName = schemaName.ToLowerInvariant(),
            Attribute = new StringAttributeMetadata
            {
                SchemaName = $"{prefix}_c{i}",
                RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None),
                MaxLength = 250,
                FormatName = StringFormatName.Text,
                DisplayName = new Microsoft.Xrm.Sdk.Label($"C{i}", lcid),
            },
        });
    }

    Console.WriteLine($"  created. Remove it again with --drop-entity {schemaName.ToLowerInvariant()}");
    return 0;
}

if (native)
{
    Console.WriteLine($"  path             native SDK ({(deleting ? "DeleteMultipleRequest" : "CreateMultipleRequest")})");
    Console.WriteLine();

    var chunks = Chunk(ids, batchSize);

    // One client per worker. A single shared ServiceClient serializes concurrent
    // requests -- spikes/ThrottleSpike measured ~3.2 requests/second that way -- so
    // sharing one across Parallel.ForEachAsync measures the client, not the API.
    // SQL 4 CDS clones internally (BaseDmlNode.UseParallelConnections); match it.
    var clients = new List<ServiceClient> { service };
    for (var i = 1; i < maxDop; i++) clients.Add(service.Clone());
    Console.WriteLine($"  clients          {clients.Count} (one per worker)");
    Console.WriteLine();

    var sw = Stopwatch.StartNew();
    var done = 0;
    var failures = new List<string>();

    await Parallel.ForEachAsync(
        Enumerable.Range(0, clients.Count),
        new ParallelOptions { MaxDegreeOfParallelism = maxDop },
        async (worker, token) =>
        {
            await Task.Yield();
            var client = clients[worker];

            // Round-robin so every worker gets an even share regardless of count.
            for (var index = worker; index < chunks.Count; index += clients.Count)
            {
                var chunk = chunks[index];
                try
                {
                if (deleting)
                {
                    // DeleteMultiple is registered in the org but has no typed request in
                    // Microsoft.PowerPlatform.Dataverse.Client 1.2.2, unlike CreateMultiple.
                    // Untyped it is.
                    var refs = new Microsoft.Xrm.Sdk.EntityReferenceCollection(
                        chunk.Select(id => new Microsoft.Xrm.Sdk.EntityReference(entity, Guid.Parse(id))).ToList());
                    var request = new Microsoft.Xrm.Sdk.OrganizationRequest("DeleteMultiple");
                    request["Targets"] = refs;
                    client.Execute(request);
                }
                else
                {
                    var rows = new Microsoft.Xrm.Sdk.EntityCollection(
                        chunk.Select(id =>
                        {
                            var e = generatedIds
                                ? new Microsoft.Xrm.Sdk.Entity(entity)
                                : new Microsoft.Xrm.Sdk.Entity(entity, Guid.Parse(id));
                            if (entity == "contact") { e["firstname"] = "Load"; e["lastname"] = "Bulk"; }
                            else if (entity == "annotation") { e["subject"] = "Load Bulk"; }
                            else { e[$"{entity[..entity.IndexOf('_')]}_name"] = "Load Bulk"; }
                            return e;
                        }).ToList())
                    { EntityName = entity };
                    client.Execute(new CreateMultipleRequest { Targets = rows });
                }

                    Interlocked.Add(ref done, chunk.Count);
                }
                catch (Exception ex)
                {
                    lock (failures) failures.Add(ex.Message);
                }
            }
        });

    foreach (var extra in clients.Skip(1)) extra.Dispose();

    sw.Stop();
    var secs = Math.Max(sw.Elapsed.TotalSeconds, 0.001);
    Console.WriteLine($"{done:N0} row(s) in {secs:F1}s ({done / secs:F0} rows/s), "
        + $"{chunks.Count} request(s) = {chunks.Count / secs:F2} req/s, "
        + $"{secs / Math.Max(chunks.Count, 1) * 1000:F0} ms/request.");

    if (failures.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine($"*** {failures.Count} request(s) failed. First distinct message(s):");
        foreach (var f in failures.Distinct().Take(3)) Console.WriteLine($"*** {f}");
    }

    return failures.Count > 0 ? 1 : 0;
}

using var connection = Sql4CdsConnectionFactory.Create(service, applicationName: "dvduck-bulkinsert");
connection.BatchSize = batchSize;
connection.MaxDegreeOfParallelism = maxDop;
Console.WriteLine($"  affinity cookie     {(pinAffinity ? "ON (pinned to one web server)" : "off (spread across the farm)")}");
// A degree of parallelism can only be spent on separate requests. With one
// statement of N rows and BatchSize N there is a single ExecuteMultiple, so
// MaxDOP has nothing to parallelise however high it is set.
var batchesPerStatement = (int)Math.Ceiling(Math.Min(statementRows, (double)count) / batchSize);
if (maxDop > 1 && batchesPerStatement < maxDop)
{
    Console.WriteLine($"  note: {batchesPerStatement} batch(es) per statement, so at most "
        + $"{batchesPerStatement} of the {maxDop} parallel slots can be used. Lower --batch-size "
        + $"to below {(int)Math.Ceiling(Math.Min(statementRows, (double)count) / maxDop)} to use them all.");
}
// Submits an asynchronous Bulk Delete job instead of ExecuteMultiple. Different
// mechanism, different plan node (BulkDeleteJobNode), and it returns as soon as
// the job is queued rather than when the rows are gone.
connection.UseBulkDelete = bulkDelete;
// A DELETE here always carries an explicit WHERE; refusing an unqualified one is
// cheap insurance against a future edit dropping the predicate.
connection.BlockDeleteWithoutWhere = true;
connection.BlockUpdateWithoutWhere = true;

// The engine absorbs service protection faults itself -- BaseDmlNode counts them
// internally, with no public accessor -- so the only way to see whether a run was
// throttled is to listen to what it says while working. Anything mentioning a
// retry or a busy server means the limit was reached and handled, which is the
// difference between "no 429 surfaced" and "no 429 happened".
var engineMessages = new List<string>();
var throttleSeen = false;
connection.InfoMessage += (_, e) =>
{
    var text = e.Message?.Message;
    if (!string.IsNullOrWhiteSpace(text)) engineMessages.Add(text);
    if (text is not null && text.Contains("service protection", StringComparison.OrdinalIgnoreCase))
        throttleSeen = true;
};
connection.Progress += (_, e) =>
{
    if (!string.IsNullOrWhiteSpace(e.Message)) engineMessages.Add(e.Message);
    if (e.Message is not null && e.Message.Contains("service protection", StringComparison.OrdinalIgnoreCase))
        throttleSeen = true;
};

var stopwatch = Stopwatch.StartNew();
var affected = 0;
var rowsAtLastReport = 0;
var secondsAtLastReport = 0.0;

for (var i = 0; i < statements.Count; i++)
{
    using var command = connection.CreateCommand();
    command.CommandText = statements[i];
    command.CommandTimeout = (int)Sql4CdsConnectionFactory.BulkExportTimeout.TotalSeconds;

    try
    {
        affected += command.ExecuteNonQuery();
    }
    catch (Exception ex)
    {
        // Loudly, and on stdout as well as stderr. A previous version of this spike
        // reported a failed run as a blank line, and a delete of rows that had never
        // been inserted as a 243 rows/s success. A measurement harness that renders
        // failure as silence is worse than none.
        Console.WriteLine();
        Console.WriteLine($"*** FAILED at statement {i + 1}/{statements.Count} after {affected:N0} row(s)");
        Console.WriteLine($"*** {ex.Message}");
        Console.Error.WriteLine($"Statement {i + 1}/{statements.Count} failed after {affected:N0} row(s): {ex.Message}");
        Console.Error.WriteLine(DataverseThrottling.Explain(ex) ?? "Not a service protection fault.");
        return 1;
    }

    // Per-statement rate, not just the running total. The point of a long run is to
    // watch the rate fall as the service protection reservoir drains and then settle:
    // the settled value is the sustainable rate, with no arithmetic needed.
    var elapsed = stopwatch.Elapsed.TotalSeconds;
    var statementRowsDone = affected - rowsAtLastReport;
    var statementSeconds = elapsed - secondsAtLastReport;
    Console.WriteLine($"  {affected,7:N0}/{count:N0} rows  {elapsed,7:F1}s total  "
        + $"this statement: {statementRowsDone:N0} rows in {statementSeconds:F1}s "
        + $"= {statementRowsDone / Math.Max(statementSeconds, 0.001):F0} rows/s"
        + (throttleSeen ? "  [throttled]" : ""));
    // Per statement, not cumulative. Left sticky, a single stall marked every later
    // statement as throttled even when they ran clean, which made a recovery look
    // like continued clamping.
    throttleSeen = false;
    rowsAtLastReport = affected;
    secondsAtLastReport = elapsed;
}

stopwatch.Stop();
Console.WriteLine();
Console.WriteLine();
var seconds = Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
// Requests, not statements: each statement is split into ceil(rows / BatchSize)
// ExecuteMultiple calls, which is the unit the service protection limits count.
var requests = statements.Sum(_ => (int)Math.Ceiling(Math.Min(statementRows, (double)count) / batchSize));
Console.WriteLine($"{affected:N0} row(s) in {seconds:F1}s "
    + $"({affected / seconds:F0} rows/s), "
    + $"{statements.Count} statement(s), ~{requests} request(s) "
    + $"= {requests / seconds:F2} req/s, {seconds / Math.Max(requests, 1) * 1000:F0} ms/request, "
    + $"{seconds / affected * 1000:F1} ms/row.");

// Deliberately narrow. An earlier version matched bare "429" and "limit", which
// caught row numbers in ordinary progress text -- "Inserting 58 - 429 of 2,500" was
// reported as throttling. The engine's real notice is unambiguous and says
// "service protection", so match that and the pause it announces, nothing looser.
var throttleWords = new[] { "service protection", "paused until", "retry-after" };
var throttleNotes = engineMessages
    .Where(m => throttleWords.Any(w => m.Contains(w, StringComparison.OrdinalIgnoreCase)))
    .ToList();

Console.WriteLine();
if (throttleNotes.Count > 0)
{
    Console.WriteLine($"Engine reported {throttleNotes.Count} message(s) suggesting the service "
        + "protection limit was reached and absorbed:");
    foreach (var note in throttleNotes.Distinct().Take(10))
    {
        Console.WriteLine($"  {note}");
    }
    Console.WriteLine("  -> the run was throttled; the throughput above is a rate-limited rate.");
}
else
{
    Console.WriteLine($"Engine reported no retry or throttling messages "
        + $"({engineMessages.Count} message(s) total).");
}

if (budgetSamples > 0)
{
    // Limits are per user PER WEB SERVER, so one reading describes one server.
    // Sample several times with a fresh client each time to land on different
    // servers, and report the lowest: with affinity pinned, the server that took
    // the load should stand out.
    Console.WriteLine();
    Console.WriteLine($"Budget after the run, {budgetSamples} sample(s) "
        + "(limits are per user per web server, so these are different servers):");

    var lowestTime = TimeSpan.MaxValue;
    var lowestRequests = int.MaxValue;

    for (var sample = 0; sample < budgetSamples; sample++)
    {
        using var probeClient = new HttpClient();
        var probe = await ServiceProtectionBudget.ProbeAsync(options, probeClient);
        var server = probe.ServerAffinity is { } id ? id[..Math.Min(8, id.Length)] : "unknown";
        Console.WriteLine($"  server {server}…  "
            + $"requests={probe.BurstRemaining?.ToString("N0") ?? "?"}  "
            + $"execution={probe.TimeRemaining?.TotalSeconds.ToString("F0") ?? "?"}s");

        if (probe.TimeRemaining is { } t && t < lowestTime) lowestTime = t;
        if (probe.BurstRemaining is { } r && r < lowestRequests) lowestRequests = r;
    }

    Console.WriteLine();
    if (lowestTime != TimeSpan.MaxValue)
    {
        var consumed = 1200 - lowestTime.TotalSeconds;
        Console.WriteLine($"  lowest execution-time remaining: {lowestTime.TotalSeconds:F0}s of 1200s "
            + $"({consumed:F0}s drawn down)");

        // Refusing to draw a conclusion is the point. These counters are per web
        // server, so with affinity off they describe servers that did none of the
        // work -- and they will happily report a full budget while the engine is
        // being throttled, which is exactly what happened when this was first run.
        // The engine's own messages are authoritative; the headers are not.
        if (throttleNotes.Count > 0)
        {
            Console.WriteLine("  NOT SUSTAINABLE: the engine reported being throttled during this run, "
                + "whatever these counters say.");
        }
        else if (!pinAffinity)
        {
            Console.WriteLine("  no verdict: affinity is off, so this reading is from a server that did "
                + "not take the load. Re-run with --pin-affinity to measure a burn rate.");
        }
        else
        {
            // 20 minutes per 5-minute sliding window replenishes at 4 s/s.
            Console.WriteLine($"  over {seconds:F1}s of wall clock that is {consumed / seconds:F2} s/s "
                + $"consumed, against ~4.00 s/s replenishment"
                + (consumed / seconds > 4 ? "  -- NOT SUSTAINABLE if repeated" : "  -- sustainable"));
        }
    }

    if (lowestRequests != int.MaxValue)
    {
        Console.WriteLine($"  lowest request budget remaining:  {lowestRequests:N0}");
    }
}

// SQL 4 CDS reports the rows a statement addressed, not the rows it changed: a
// DELETE naming 4,000 absent ids reports 4,000. So verify against the table rather
// than trusting the count, or a no-op reads as a fast success.
if (exec is null && !generatedIds) try
{
    using var verify = connection.CreateCommand();
    verify.CommandText = $"SELECT count(*) FROM {entity} WHERE {entity}id IN ("
        + string.Join(", ", ids.Take(Math.Min(ids.Count, 500)).Select(id => $"'{id}'")) + ")";
    var present = Convert.ToInt64(verify.ExecuteScalar(), CultureInfo.InvariantCulture);
    var sampled = Math.Min(ids.Count, 500);
    var expected = deleting ? 0 : sampled;
    Console.WriteLine();
    Console.WriteLine($"Verified against the table: {present:N0} of the first {sampled:N0} id(s) present, "
        + $"expected {expected:N0}."
        + (present == expected ? "  OK" : "  *** MISMATCH -- the run did not do what it reported"));
}
catch (Exception ex)
{
    Console.WriteLine($"Could not verify row counts: {ex.Message}");
}

if (!deleting)
{
    Console.WriteLine();
    Console.WriteLine($"To remove exactly these rows again:");
    Console.WriteLine($"  dotnet run --project spikes/BulkInsertSpike -- --delete --count {count}");
}

return 0;

List<string> InsertStatements(List<string> allIds)
{
    var result = new List<string>();

    // 'Bulk' as the surname keeps these obvious in the UI and greppable in an
    // export, so a leftover row is recognisable rather than mysterious. The thin
    // shape has no surname to carry it, which is the trade for measuring the
    // minimum a create can be.
    var idColumn = entity + "id";
    var customPrefix = isCustomEntity ? entity[..entity.IndexOf('_')] : "";
    var columnList = (entity, shape) switch
    {
        (_, "thin") => idColumn,
        _ when isCustomEntity => $"{idColumn}, {customPrefix}_name"
            + string.Concat(Enumerable.Range(1, extraColumns).Select(i => $", {customPrefix}_c{i}")),
        ("annotation", "wide") => $"{idColumn}, subject, notetext",
        ("annotation", _) => $"{idColumn}, subject",
        (_, "wide") => $"{idColumn}, firstname, lastname, emailaddress1, jobtitle, description, "
                       + "telephone1, address1_line1, address1_city, address1_postalcode",
        _ => $"{idColumn}, firstname, lastname",
    };

    var filler = new string('x', 200);

    foreach (var chunk in Chunk(allIds, statementRows))
    {
        // With generated ids the leading id column and its value both come out, so
        // the statement is the same shape minus the key.
        var effectiveColumns = generatedIds
            ? string.Join(", ", columnList.Split(", ").Skip(1))
            : columnList;
        if (generatedIds && string.IsNullOrWhiteSpace(effectiveColumns))
        {
            throw new InvalidOperationException(
                "--generated-ids needs at least one non-key column; use --columns normal, not thin.");
        }

        var sql = new StringBuilder($"INSERT INTO {entity} ({effectiveColumns}) VALUES ");
        for (var i = 0; i < chunk.Count; i++)
        {
            var index = allIds.IndexOf(chunk[i]) + 1;
            if (i > 0) sql.Append(", ");
            var tuple = (entity, shape) switch
            {
                (_, "thin") => string.Create(CultureInfo.InvariantCulture, $"('{chunk[i]}')"),
                _ when isCustomEntity =>
                    "('" + chunk[i] + "', 'Load" + index.ToString(CultureInfo.InvariantCulture) + " Bulk'"
                    + string.Concat(Enumerable.Range(1, extraColumns).Select(_ => ", '" + filler + "'"))
                    + ")",
                ("annotation", "wide") => string.Create(CultureInfo.InvariantCulture,
                    $"('{chunk[i]}', 'Load{index} Bulk', '{filler}')"),
                ("annotation", _) => string.Create(CultureInfo.InvariantCulture,
                    $"('{chunk[i]}', 'Load{index} Bulk')"),
                (_, "wide") => string.Create(CultureInfo.InvariantCulture,
                    $"('{chunk[i]}', 'Load{index}', 'Bulk', 'load{index}@example.invalid', "
                    + $"'Loader {index}', '{filler}', '+45 00 00 00 {index % 100:00}', "
                    + $"'{index} Test Street', 'Copenhagen', '{1000 + index % 9000}')"),
                _ => string.Create(CultureInfo.InvariantCulture, $"('{chunk[i]}', 'Load{index}', 'Bulk')"),
            };

            // Strip the first value, which is the key.
            sql.Append(generatedIds
                ? "(" + string.Join(", ", tuple.TrimStart('(').TrimEnd(')').Split(", ").Skip(1)) + ")"
                : tuple);
        }

        result.Add(sql.ToString());
    }

    return result;
}

List<string> DeleteStatements(List<string> allIds)
{
    return Chunk(allIds, statementRows)
        .Select(chunk =>
            $"DELETE FROM {entity} WHERE {entity}id IN ("
            + string.Join(", ", chunk.Select(id => $"'{id}'"))
            + ")")
        .ToList();
}

static List<List<string>> Chunk(List<string> source, int size)
{
    var result = new List<List<string>>();
    for (var offset = 0; offset < source.Count; offset += size)
        result.Add(source.Skip(offset).Take(size).ToList());
    return result;
}

string? ArgValue(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
