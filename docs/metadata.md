# Querying metadata

Dataverse exposes its own schema as tables, and SQL 4 CDS makes them queryable like any
others — so "what tables exist?" and "which columns are lookups?" are ordinary SQL, not a
separate API. No extra code, no special flags.

Every example below was run against a live environment and shows its real output.

> **These need a live connection.** Run against a captured snapshot they fail with
> `RetrieveMetadataChanges`: the engine answers metadata queries by calling the service and
> ignores the injected metadata cache. A snapshot serves *compilation*, not schema
> browsing (ADR 0003).

The tables are `metadata.entity`, `metadata.attribute`, `metadata.relationship_1_n`,
`_n_1`, `_n_n`, `metadata.alternate_key` and `metadata.value`.

## 1. How many tables are there?

```console
$ dvduck query --plan "
    WITH e AS DATAVERSE (SELECT logicalname FROM metadata.entity)
    SELECT count(*) AS tables FROM e"
  e: 872 rows in 0.3s
tables
872
```

872 in a stock environment with no custom tables. Whatever you are looking for is in
there somewhere, which is why the next query matters more than this one.

## 2. Find a table by name

```console
$ dvduck query --plan "
    WITH e AS DATAVERSE (SELECT logicalname, displayname FROM metadata.entity
                         WHERE logicalname LIKE '%account%')
    SELECT logicalname, displayname FROM e ORDER BY logicalname"
  [Warning] FilterNode: This predicate did not fold into the FetchXML filter...
logicalname                              displayname
account                                  Firma
powerpagecomponent_mspp_webrole_account
```

Two things worth noticing.

**`displayname` is localised.** `account` is `Firma` here because the environment is
Danish. Filter on `logicalname` — it is invariant. Display names are for humans.

**The folding guard fires.** `LIKE` does not translate into FetchXML, so all 872 rows are
fetched and filtered locally. On metadata that is fine: 872 rows is nothing, and the
whole query still runs in under half a second. The same warning on a data table means
something much more expensive, which is why it is never silenced.

## 3. What columns does a table have?

```console
$ dvduck query --plan "
    WITH a AS DATAVERSE (SELECT logicalname, attributetypename, requiredlevel
                         FROM metadata.attribute WHERE entitylogicalname = 'contact')
    SELECT * FROM a ORDER BY logicalname LIMIT 8"
  a: 311 rows in 0.3s
logicalname              attributetypename        requiredlevel
accountid                LookupType               None
accountidname            StringType               None
accountidyominame        StringType               None
accountrolecode          PicklistType             None
accountrolecodename      VirtualType              None
address1_addressid       UniqueidentifierType     None
address1_addresstypecode PicklistType             None
address1_addresstypecodename VirtualType          None
```

311 columns on a stock `contact`, and `account` has 215. That is the real argument
against `SELECT *`: most of those columns are derived. Note the pattern above —
`accountid` is the lookup, `accountidname` is its display text, and
`accountrolecodename` is the label for a picklist. Three columns for one field.

## 4. Which columns are lookups, and what do they point at?

```console
$ dvduck query --plan "
    WITH a AS DATAVERSE (SELECT logicalname, targets, attributetype
                         FROM metadata.attribute
                         WHERE entitylogicalname = 'contact' AND attributetype = 'Lookup')
    SELECT logicalname, targets FROM a ORDER BY logicalname LIMIT 8"
logicalname                targets
accountid                  account
createdby                  systemuser
createdbyexternalparty     externalparty
createdonbehalfby          systemuser
masterid                   contact
modifiedby                 systemuser
modifiedbyexternalparty    externalparty
modifiedonbehalfby         systemuser
```

This is the query to run before writing a join: `targets` tells you what a lookup
actually points to, so you can chain the next `DATAVERSE` entry with `{{ }}` against the
right table. `masterid` pointing back at `contact` is a self-reference — merged records.

## 5. Which datetime columns are wall-clock rather than instants?

```console
$ dvduck query --plan "
    WITH a AS DATAVERSE (SELECT entitylogicalname, logicalname, datetimebehavior, format
                         FROM metadata.attribute
                         WHERE entitylogicalname IN ('account','contact')
                           AND attributetype = 'DateTime')
    SELECT datetimebehavior, count(*) AS columns, min(logicalname) AS example
    FROM a GROUP BY datetimebehavior ORDER BY columns DESC"
datetimebehavior    columns    example
UserLocal           16         adx_identity_lastsuccessfullogin
DateOnly            2          anniversary
```

`datetimebehavior` is exactly what ADR 0010 resolves through attribute metadata to decide
whether a column is an instant or a wall-clock value. This query lets you audit it
yourself: the two `DateOnly` columns must never be shifted by an offset, and the 16
`UserLocal` ones are real instants.

Do not key off `format` instead — it looks equivalent and is not. Stock
`overriddencreatedon` is `Format=DateOnly` with `Behavior=UserLocal`, so filtering on
`format` would strip the time off a genuine timestamp.

## 6. Join entity to attribute — the widest custom tables

```console
$ dvduck query --plan "
    WITH e AS DATAVERSE (SELECT logicalname, iscustomentity FROM metadata.entity),
         a AS DATAVERSE (SELECT entitylogicalname FROM metadata.attribute
                         WHERE entitylogicalname IN {{SELECT logicalname FROM e
                                                     WHERE iscustomentity = true}})
    SELECT entitylogicalname, count(*) AS columns
    FROM a GROUP BY entitylogicalname ORDER BY columns DESC LIMIT 5"
  e: 872 rows in 0.3s
  a: 533 distinct key(s) from the local query
  a: 19,657 rows after 500 of 533 keys
  a: 20,952 rows after 33 of 533 keys
  a: 20,952 rows in 5.2s
entitylogicalname     columns
mspp_webformstep      178
mspp_entityform       144
msdyn_mobileapp       114
chat                  112
adx_portalcomment     98
```

Metadata composes with the rest of the plan syntax: the second entry uses `{{ }}` to push
533 table names from the first entry back into Dataverse, and the log shows the key set
being chunked at 500 per round trip. Nothing here is metadata-specific — it is the same
two-hop pattern used for data.

## 7. Export the schema as JSON

```console
$ dvduck query --plan "
    WITH columns AS DATAVERSE (SELECT entitylogicalname, logicalname, attributetypename
                               FROM metadata.attribute WHERE entitylogicalname = 'contact')
    COPY (SELECT * FROM columns ORDER BY logicalname)
    TO 'contact-columns.json' (FORMAT JSON, ARRAY true)"
```

```console
  (0 row(s))
$ python3 -c "import json; print(len(json.load(open('contact-columns.json'))))"
311
```

`COPY` works as the final statement of a plan, so `FORMAT JSON`, `FORMAT CSV` and
`FORMAT PARQUET` all write files directly. It returns no rows to the caller, so the
trailing `(0 row(s))` is expected — the 311 rows are in the file. To send results to another program instead,
use `--format json` and read stdout.

## Notes

- **Filters on metadata mostly do not fold.** Expect the folding-guard warning and a full
  fetch of the 872 entities or the attributes of the tables you named. It is cheap here.
  `--strict` still allows them: it rejects only findings the planner estimates at 10,000
  rows or more, and these estimate in the dozens. You get the warning, not a refusal.
- **`logicalname` is stable, `displayname` is not.** Display names are localised and
  renameable; logical names are what FetchXML and the cache use.
- Attribute rows carry far more than shown here — `isvalidforcreate`, `iscustomattribute`,
  `maxlength`, `optionset` and so on. `SELECT TOP 1 *` on `metadata.attribute` is a quick
  way to see the full shape.
