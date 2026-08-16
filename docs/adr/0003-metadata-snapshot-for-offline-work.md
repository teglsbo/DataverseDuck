# ADR 0003 — Capture Dataverse metadata to a snapshot for offline work

- **Status:** Accepted
- **Date:** 2026-08-16

## Context

SQL 4 CDS cannot compile T-SQL without entity metadata: it resolves columns, types,
lookups and relationships through `IAttributeMetadataCache` in order to build FetchXML.
That means metadata is required at *compile* time, not just at execution time.

We want development and CI to work without a live Dataverse environment — for speed, for
offline work, and because a tenant is not yet available.

## Options considered

### A. Synthesise `EntityMetadata` in code

Construct `EntityMetadata` and `AttributeMetadata` objects by hand for the tables under test.

**Attempted and rejected.** `spikes/MetadataSpike` built account and contact entities with
logical names, primary keys, attribute types and lookup targets. Each fix revealed another
requirement deeper in the optimiser:

- `FetchXmlScan.SortAttributes` threw `Nullable object must have a value` — needs `ColumnNumber`.
- Then `DataSource.LoadDefaultCollation` issued a `RetrieveMultiple` for the `organization`
  row at connection construction, requiring `localeid` and `collation`.
- Then `FetchXmlScan.AddAttribute` performed entity lookups with a **null** key while
  resolving virtual and lookup attributes.
- Then `DataTypeComparer.Equals` threw `NullReferenceException` on incomplete
  `DataTypeReference` values while validating join comparisons.

These are platform-populated internals. Reflection-poking twenty-odd private setters to
satisfy a query optimiser is not a foundation to build tests on, and it would silently
drift from real behaviour.

### B. Use XrmMockup or FakeXrmEasy

Both are in-memory Dataverse mocks operating at the `IOrganizationService` layer, which is
the right layer — SQL 4 CDS accepts an `IOrganizationService`.

- **XrmMockup** (MIT, actively maintained) already solves this problem the same way: its
  companion `MetadataGenerator` exports metadata from a real instance to a local file.
  That is a strong signal the snapshot approach is correct.
- **FakeXrmEasy** v2+ requires a commercial licence for business use.

Neither removes the need for real metadata; XrmMockup explicitly depends on capturing it.
Adding a dependency to obtain a file we can capture in ~60 lines is not worthwhile yet,
though XrmMockup remains a sensible later addition for simulating *data* and plugins.

### C. Capture real metadata once and replay it

Retrieve `EntityMetadata` from a live environment, serialize it, and reload it offline.

## Decision

**Capture real metadata and serialize it with `DataContractSerializer`.**

`EntityMetadata` is a `DataContract` type — the SDK itself uses this mechanism to
deserialize server responses — so the serializer populates the private setters that
defeated option A.

- `MetadataCapture.Capture` issues `RetrieveEntityRequest` per named entity with
  `EntityFilters.All`, deliberately not `RetrieveAllEntitiesRequest`, which is tens of
  megabytes and slow when the query surface touches a handful of tables.
- `MetadataSnapshot.Save`/`Load` use a binary `XmlDictionaryWriter` with
  `MaxItemsInObjectGraph = int.MaxValue`, since one entity with all attributes and
  relationships exceeds the default 65,536-object limit.
- `SnapshotMetadataCache` implements `IAttributeMetadataCache` over a snapshot and throws
  `FaultException` for unknown entities, matching platform behaviour, listing the entities
  it does have.

Round-trip fidelity is verified by tests covering private-setter properties
(`PrimaryIdAttribute`, `ObjectTypeCode`, `EntitySetName`), attribute subtypes and flags
(`UniqueIdentifierAttributeMetadata`, `MaxLength`, `IsPrimaryId`), and `DateTimeBehavior`
— which ADR 0002 depends on for correct UTC handling.

## Consequences

**Positive**

- Query compilation, tests and CI run with no tenant and no network.
- Snapshots are small, reviewable artefacts scoped to the tables actually used.
- Fidelity is real rather than approximated, because the metadata *is* real.

**Negative**

- **A live environment is required once, before offline work can begin.** This moves
  environment setup onto the critical path rather than a parallel track.
- Snapshots go stale as schema changes. A re-capture command and a drift check are needed.
- Snapshots may embed schema details about a customer environment. They are gitignored
  (`metadata/*.bin`) by default; treat them as potentially sensitive.
- Capture is currently unverified against a real environment — the tests exercise the
  serialization mechanism and the request shape with a fake `IOrganizationService`, not a
  real metadata graph, which is far larger and more interconnected.

## Follow-ups

- Verify against a real environment as soon as one exists, especially graph size and
  serialization time.
- Add a `capture` CLI command and a staleness check.
- Reconsider XrmMockup for simulating data and plugin behaviour once metadata is solved.
