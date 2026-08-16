using DataverseDuck.Configuration;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

// Seeds a handful of accounts and contacts with fixed GUIDs so the JSON-to-
// Dataverse join can be verified against known data rather than an empty
// environment.
//
// Fixed GUIDs matter: the local JSON fixture refers to these ids, so a join
// that returns the expected rows proves the keys really travelled to Dataverse
// and back. Random ids would only prove that something was fetched.
//
// Safe to re-run: each record is created only if absent. Pass --delete to
// remove them again.

DotEnvFile.LoadFromCurrentDirectory();

if (!DataverseOptions.TryLoadFromEnvironment(out var options, out var error))
{
    Console.Error.WriteLine(error);
    return 2;
}

using var client = new ServiceClient(options.ToConnectionString());

if (!client.IsReady)
{
    Console.Error.WriteLine($"Connection failed: {client.LastError}");
    return 1;
}

var accounts = new (Guid Id, string Name)[]
{
    (Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001"), "Acme Nordics"),
    (Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002"), "Contoso Denmark"),
};

var contacts = new (Guid Id, string First, string Last, Guid Account)[]
{
    (Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), "Ann", "Webchat", accounts[0].Id),
    (Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002"), "Bob", "Branch", accounts[0].Id),
    (Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003"), "Cara", "Webchat", accounts[1].Id),
    (Guid.Parse("aaaaaaaa-0000-0000-0000-000000000004"), "Dan", "Neither", accounts[1].Id),
};

var deleting = args.Contains("--delete");

if (deleting)
{
    foreach (var (id, _, _, _) in contacts)
        Remove("contact", id);

    foreach (var (id, _) in accounts)
        Remove("account", id);

    Console.WriteLine("Removed the seed data.");
    return 0;
}

foreach (var (id, name) in accounts)
{
    var account = new Entity("account", id) { ["name"] = name };
    Upsert(account);
}

foreach (var (id, first, last, accountId) in contacts)
{
    var contact = new Entity("contact", id)
    {
        ["firstname"] = first,
        ["lastname"] = last,
        ["parentcustomerid"] = new EntityReference("account", accountId),
    };

    Upsert(contact);
}

Console.WriteLine($"Seeded {accounts.Length} account(s) and {contacts.Length} contact(s).");
return 0;

void Upsert(Entity entity)
{
    if (Exists(entity.LogicalName, entity.Id))
    {
        client.Update(entity);
        Console.WriteLine($"  updated {entity.LogicalName} {entity.Id}");
        return;
    }

    client.Create(entity);
    Console.WriteLine($"  created {entity.LogicalName} {entity.Id}");
}

bool Exists(string table, Guid id)
{
    var query = new QueryExpression(table)
    {
        ColumnSet = new ColumnSet(false),
        TopCount = 1,
    };

    query.Criteria.AddCondition($"{table}id", ConditionOperator.Equal, id);
    return client.RetrieveMultiple(query).Entities.Count > 0;
}

void Remove(string table, Guid id)
{
    if (!Exists(table, id))
    {
        return;
    }

    client.Delete(table, id);
    Console.WriteLine($"  deleted {table} {id}");
}
