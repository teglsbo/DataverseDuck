# ADR 0003 — Capture Dataverse metadata to a snapshot for offline work

- **Status:** Accepted
- **Date:** 2026-08-16

## Context

SQL 4 CDS cannot compile T-SQL without entity metadata: it resolves columns, types,
lookups and relationships through `IAttributeMetadataCache` in order to build FetchXML.
That means metadata is required at *compile* time, not just at execution time.

We want development and CI to work without a live Dataverse environment — for speed, for
offline work, and (when this was written) because no tenant was available yet.

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
- **The CLI can write a snapshot but cannot read one back.** `dvduck capture` produces the
  file and `MetadataSnapshot.Load` consumes it, but `dvduck query` has no `--snapshot`
  option, so the offline path this ADR exists to enable is reachable only from the library.
  Note also that `metadata.*` schema queries fail against a snapshot regardless (ADR 0010
  notes the engine calls the service for those), so a snapshot serves compilation, not
  schema browsing.
- Snapshots must be re-captured after schema changes; there is still no drift check.

## Verification against a real environment

Done, and it found a bug. Capturing `account` and `contact` from a live environment:

| | |
|---|---|
| Entities / size | 2 entities, 2,070.9 KB |
| Capture time | 3.7 s wall clock, cold |
| Attributes | `account` 215, `contact` 311 |
| Survives round trip | `PrimaryIdAttribute`, `PrimaryNameAttribute`, and critically `DateTimeBehavior` (`birthdate` → `DateOnly`) |

`DateTimeBehavior` surviving matters: it is what ADR 0010 resolves wall-clock columns
from, so the offline path maps `birthdate` to `DATE` exactly as the live path does.

The bug: `dvduck capture account contact` captured **only `contact`**, reporting
"1 table(s)" without complaint. The argument filter excluded `outIndex` and `outIndex + 1`,
but with no `--out` present `outIndex` was `-1`, so `-1 + 1` selected the first real
argument. Silent partial capture — the failure mode this ADR's "Negative" section warns
about, produced by the tool itself. Fixed by extracting `CaptureArguments`, which is
tested; the CLI had had no tests at all, which is why an off-by-one in argument parsing
reached a live run.

## Follow-ups
- Add a `capture` CLI command and a staleness check.
- Reconsider XrmMockup for simulating data and plugin behaviour once metadata is solved.
