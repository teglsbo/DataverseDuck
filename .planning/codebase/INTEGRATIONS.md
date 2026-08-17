# External Integrations

**Analysis Date:** 2026-08-17

## APIs & External Services

**Microsoft Dataverse / Power Platform:**
- Dataverse organization service (Web API / SDK, not the TDS endpoint) - the primary external data source
  - Client: `Microsoft.PowerPlatform.Dataverse.Client.ServiceClient` (`IOrganizationService`), constructed in `src/DataverseDuck/Configuration/DataverseCredential.cs`
  - Query layer: `MarkMpn.Sql4Cds.Engine` compiles T-SQL into FetchXML executed server-side (`src/DataverseDuck/Sql4CdsConnectionFactory.cs`, `src/DataverseDuck/Sql4CdsQuerySource.cs`)
  - TDS endpoint explicitly NOT used by default (`useTdsEndpoint: false` in `Sql4CdsConnectionFactory.Create`) — service principal auth is unsupported there and it requires opening ports 1433/5558; the SDK path is used instead
  - Auth: Entra ID app registration, resolved through `DATAVERSE_URL`, `DATAVERSE_TENANT_ID`, `DATAVERSE_CLIENT_ID` env vars
  - Rate limiting: Dataverse Service Protection API limit headers parsed and explained (`src/DataverseDuck/Diagnostics/ServiceProtectionBudget.cs`, `src/DataverseDuck/DataverseThrottling.cs` — handles HTTP 429 responses)
  - Metadata: table/column schema retrieved and cached (`src/DataverseDuck/Metadata/MetadataCapture.cs`, `MetadataSnapshot.cs`, `SnapshotMetadataCache.cs`)

**Entra ID (Azure AD):**
- App registration used for authentication only (no interactive/user sign-in anywhere in the library)
  - SDK: `Microsoft.Identity.Client` (MSAL) `ConfidentialClientApplicationBuilder`
  - Implementation: `src/DataverseDuck/Configuration/DataverseCredential.cs` (`ClientSecretCredential`, `CertificateCredential` subclasses)
  - Access token claims inspected for diagnostics: `src/DataverseDuck/Diagnostics/AccessTokenClaims.cs`, `EnvironmentTenant.cs`

**SQL 4 CDS telemetry (implicit, third-party):**
- `MarkMpn.Sql4Cds.Engine` ships anonymous Application Insights telemetry to a hardcoded instrumentation key (noted in `src/DataverseDuck/Sql4CdsConnectionFactory.cs`)
  - Query text is NOT sent on success, but IS sent on exception
  - Mitigation: an explicit application name (`dvduck-cli`, `Sql4CdsConnectionFactory.DefaultApplicationName`) is set for clearer attribution rather than leaving a default/assembly name
  - No opt-out mechanism implemented in this codebase — inherited third-party behavior to be aware of

## Data Storage

**Databases:**
- DuckDB (embedded, file-based analytical database) — local cache/join engine, not a hosted service
  - Client: `DuckDB.NET.Data.Full` 1.5.5
  - Bulk load path: `src/DataverseDuck/Schema/DuckDbBulkLoader.cs` uses the DuckDB Appender API (~440k rows/sec per README)
  - Schema mapping from Dataverse reader: `src/DataverseDuck/Schema/DataverseSchemaMapper.cs`, `ColumnMapping.cs`
  - Default cache file: `cache.duckdb` (per README architecture diagram); managed via `src/DataverseDuck/CacheManifest.cs`, `src/DataverseDuck/DataverseCache.cs`

**File Storage:**
- Local filesystem only — no cloud object storage
  - JSON log/state files read directly by DuckDB's native `read_json_auto('logs/*.json')`
  - Metadata snapshots serialized to disk (`docs/adr/snapshot.bin` example; `MetadataSnapshot`/`SnapshotMetadataCache`)
  - `.pfx` certificate files loaded from local paths (`DATAVERSE_CERT_PATH`)

**Caching:**
- DuckDB itself functions as the query cache layer (Dataverse rows streamed in once, then queried locally alongside JSON)
- Metadata snapshot cache for offline schema work — see ADR `docs/adr/0003-metadata-snapshot-for-offline-work.md`
- Cache manifest tracks cached tables — see ADR `docs/adr/0009-cache-manifest.md`, `src/DataverseDuck/CacheManifest.cs`

## Authentication & Identity

**Auth Provider:**
- Entra ID (Azure AD) via MSAL confidential client — no third-party identity provider, no interactive/user auth
  - Two supported credential types, mutually exclusive (configuring more than one is refused rather than resolved by precedence):
    1. Client secret (`DATAVERSE_CLIENT_SECRET`) — the only credential verified against a live environment
    2. X.509 certificate — three sub-options: file path + optional password (`DATAVERSE_CERT_PATH`, `DATAVERSE_CERT_PASSWORD`), or platform certificate store thumbprint (`DATAVERSE_CERT_THUMBPRINT`, discouraged on Linux)
  - Implementation: `src/DataverseDuck/Configuration/DataverseCredential.cs`
  - Diagnostics: `dvduck doctor` CLI command validates configuration end-to-end (`src/DataverseDuck/Diagnostics/EnvironmentDoctor.cs`, invoked from `src/DataverseDuck.Cli/Program.cs`)
  - Multi-environment support via named profiles (env var prefix pattern, e.g. `DATAVERSE_TEST_URL`), selected with `--profile` or `DATAVERSE_PROFILE`

## Monitoring & Observability

**Error Tracking:**
- None first-party. Third-party: `MarkMpn.Sql4Cds.Engine` sends exception details (including query text) to its own hardcoded Application Insights instance — not configurable or owned by this project

**Logs:**
- No structured logging framework detected (no Serilog/NLog/Microsoft.Extensions.Logging reference in any `.csproj`)
- Diagnostics surfaced via CLI output/exit codes (`dvduck doctor`) and `CheckResult` objects (`src/DataverseDuck/Diagnostics/CheckResult.cs`)
- Secrets/credentials never logged — `Describe()` methods on `DataverseCredential` subclasses explicitly mask secret values (`AccessTokenClaims.Mask`)

## CI/CD & Deployment

**Hosting:**
- None — this is a distributed library + CLI tool, not a hosted service
- Distribution via NuGet.org (implied by `dotnet pack`/`dotnet tool install --global` instructions in README): `DataverseDuck` (library) and `DataverseDuck.Cli` (dotnet tool, command `dvduck`)

**CI Pipeline:**
- None detected — no `.github/workflows/` directory or other CI config found in the repository

## Environment Configuration

**Required env vars (all prefixed `DATAVERSE_`, optionally profile-prefixed e.g. `DATAVERSE_TEST_URL`):**
- `DATAVERSE_URL` - Power Platform environment URL
- `DATAVERSE_TENANT_ID` - Entra ID tenant (directory) ID
- `DATAVERSE_CLIENT_ID` - Entra ID app registration client ID
- `DATAVERSE_CLIENT_SECRET` - client secret credential (mutually exclusive with cert options below)
- `DATAVERSE_CERT_PATH` / `DATAVERSE_CERT_PASSWORD` - certificate file credential
- `DATAVERSE_CERT_THUMBPRINT` - platform certificate store credential
- `DATAVERSE_PROFILE` - default profile name when `--profile` flag not passed

**Secrets location:**
- `.env` file at repo root (gitignored, present locally but never read/quoted by tooling in this analysis)
- `.env.example` (committed) documents variable names and format with placeholder/empty values only
- Loaded at runtime by `src/DataverseDuck/Configuration/DotEnvFile.cs`

## Webhooks & Callbacks

**Incoming:**
- None — this is a CLI/library, not a service; no HTTP listener present

**Outgoing:**
- None application-defined. Dataverse Web API calls (via `ServiceClient`/Sql4CdsConnection) and MSAL token requests to Entra ID are the only outbound network calls; SQL 4 CDS's own telemetry call (see APIs section) is outbound but not initiated by this project's code.

---

*Integration audit: 2026-08-17*
