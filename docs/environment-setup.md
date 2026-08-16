# Environment setup

Getting headless, app-registration access to Dataverse. Three portals are involved and
the steps are not obviously connected, which is why step 4 exists and why `dvduck doctor`
exists.

**The thing that catches everyone:** creating an app registration in Entra ID does *not*
give it access to Dataverse. Dataverse maintains its own user list, and the app
registration needs an **application user** record inside the environment, with a security
role. Miss either and you get an authentication error that says nothing about the cause.

Total time: about 20 minutes.

---

## 0. What permissions do you need in the tenant?

If you are a developer in a corporate tenant rather than its admin, this is the first
question, and the answer is better than people expect.

**You do not need admin consent.** This is the usual corporate blocker and it does not
apply here. Admin consent is only required to grant *API permissions*, and Dataverse S2S
requests none (see step 2). Since there is nothing to consent to, no Global Administrator,
Privileged Role Administrator or Cloud Application Administrator has to be involved.
Developers arriving from SharePoint or Graph assume otherwise and go asking for a consent
grant they never needed.

Four gates stand between you and a working setup. **All four are open by default**, and
all four can be closed by an admin — you cannot see which, so find out by trying:

| Gate | Default | If it is closed |
|---|---|---|
| Register an application (`usersCanRegisterApplications`) | Any member user may | Ask for the **Application Developer** role |
| Add a client secret to an app you own | Ownership is enough | An app management policy blocks or time-limits secrets; ask the admin |
| Sign up for the Power Apps Developer Plan (`allowedToSignUpEmailBasedSubscriptions`) | Self-service sign-up allowed | Ask the admin to enable it, or use your own tenant |
| Create a developer environment (`disableDeveloperEnvironmentCreationByNonAdminUsers`) | Licensed users may | Ask the admin, or use your own tenant |

**Application Developer** is the least-privileged role that unblocks the first gate. It is
worth naming it specifically when you ask: it only lets the holder create applications and
be added as their owner. It cannot manage apps it does not own and cannot grant consent —
unlike Cloud Application Administrator or Application Administrator, which are far broader
and are what an admin will otherwise reach for.

**Step 4 needs no admin either, if the environment is yours.** The creator of a developer
environment is automatically System Administrator in it, which is exactly the privilege
required to create the application user. In someone else's environment — a shared sandbox
— you need System Administrator there, and only its admin can give you that.

So the realistic best case is **zero admin involvement**, and the realistic minimal ask is
one role: Application Developer.

### If the tenant is locked down

Create your own free Entra tenant and sign up for the Developer Plan there. You are Global
Administrator of it, so every setting above is at its default and open.

Do not plan on the Microsoft 365 Developer Program: since its 2024 restriction it is open
only to Visual Studio Professional/Enterprise subscribers, partner-program members and
Premier/Unified Support customers — not to the general public.

There is also a governance question worth asking before you start: creating app
registrations and environments in an employer's tenant is a real footprint, and a separate
tenant avoids it entirely.

---

## 1. Get an environment

Two realistic options.

### Power Apps Developer Plan — free, indefinite

Best default. Sign up at <https://powerapps.microsoft.com/developerplan/>.

- Free, no expiry, up to 3 developer environments.
- Requires a **work or school account**. Personal Microsoft accounts are rejected.
  If you do not have one, create a Microsoft 365 Developer Program tenant first, or use
  the trial option below.
- **Does not include Dynamics 365 app tables** — no `opportunity`, `lead`, `incident`,
  `campaign`. Standard tables (`account`, `contact`, `systemuser`, `task`, `annotation`)
  and any custom tables are present.

### Dynamics 365 trial — 30 days, complete

<https://trials.dynamics.com>. Use this if your queries genuinely need the sales or
service tables. It expires, so treat it as a way to capture a metadata snapshot rather
than as a stable environment.

> There is no local Dataverse emulator. XrmMockup and FakeXrmEasy mock the
> `IOrganizationService` interface in-process, but neither serves the HTTP endpoint, and
> both still need real metadata to be useful. See ADR 0003.

Once created, note the **Environment URL** from
Power Platform Admin Center > Environments > *your environment*.
It looks like `https://yourorg.crm4.dynamics.com`.

### Which environment type?

The admin centre offers four (Danish tenants: *Udvikler*, *Prøveversion*, *Sandkasse*,
*Produktion*). Choose **Developer**.

| Type | Use it? |
|---|---|
| **Developer** | ✅ Free, consumes no tenant capacity, and its creator is automatically **System Administrator** — which is exactly the privilege step 4 needs |
| **Trial** | 30 days, then gone. Only if you need Dynamics 365 app tables |
| **Sandbox** | Consumes tenant Dataverse capacity and licences; needs a Power Platform admin |
| **Production** | The same, in your employer's production estate |

The System Administrator point is what makes Developer the only type that needs no admin
involvement at all. Sandbox and Production also cost capacity that is visibly billed.

Pick the region closest to you (Europe gives a `crm4` URL). **It cannot be changed later.**
Dataverse is provisioned automatically for a Developer environment; there is no separate
"add a database" step.

---

## 2. Register the application in Entra ID

<https://portal.azure.com> > **Microsoft Entra ID** > **App registrations** > **New registration**.

- Name: anything, e.g. `dvduck-cli`.
- Supported account types: **Accounts in this organizational directory only**.
- Redirect URI: leave blank. This is a daemon; there is no browser and no redirect.

From the **Overview** page copy two GUIDs — they are different and mixing them up is a
common failure:

| Portal label | Variable |
|---|---|
| Application (client) ID | `DATAVERSE_CLIENT_ID` |
| Directory (tenant) ID | `DATAVERSE_TENANT_ID` |

### Create a secret

**Certificates & secrets** > **Client secrets** > **New client secret**.

Copy the **Value** column immediately. It is shown once and is unrecoverable afterwards.
The **Secret ID** column is *not* the secret — it is an identifier, and using it produces
`AADSTS7000215`.

### API permissions

**None are required.** Microsoft's own S2S walkthrough states plainly that "Delegated
permissions are not required for this server-to-server scenario."[^s2s]

This is counterintuitive enough that plenty of guides tell you to add
**Dynamics CRM > user_impersonation** anyway. It is unnecessary: Dataverse does not
authorise service principals through Entra API permissions at all. It authorises them
through the application user and security role created in step 4. If you have already
added the permission, it is harmless — but it is not what grants access, so adding it
will not fix an access problem.

[^s2s]: [Use single-tenant server-to-server authentication](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/use-single-tenant-server-server-authentication), Microsoft Learn.

---

## 3. Set the environment variables

```bash
export DATAVERSE_URL="https://yourorg.crm4.dynamics.com"
export DATAVERSE_TENANT_ID="00000000-0000-0000-0000-000000000000"
export DATAVERSE_CLIENT_ID="00000000-0000-0000-0000-000000000000"
export DATAVERSE_CLIENT_SECRET="the Value you copied"
```

### Handling the secret

The three IDs are not sensitive. The secret is, and it is worth being deliberate about it,
because the ways it leaks are mundane rather than dramatic: shell history, a screenshot, a
paste into a chat window or an issue report.

Typing it inline as above puts it in your shell history. Prefer:

```bash
read -rs DATAVERSE_CLIENT_SECRET && export DATAVERSE_CLIENT_SECRET
```

`-s` hides the typing and `read` leaves no history entry. Or copy `.env.example` to `.env`
— gitignored — and source it:

```bash
cp .env.example .env      # then edit .env
set -a && . ./.env && set +a
```

**If a secret is ever exposed, rotate it rather than assessing the risk.** Deleting it in
**Certificates & secrets** and adding another takes under a minute, and an unrotated secret
is valid until its expiry regardless of who has seen it. There is no downside to rotating.

Never paste a secret into source, a commit message, an issue, or a conversation.

Check progress:

```bash
dotnet run --project src/DataverseDuck.Cli -- doctor
```

At this point **token acquisition should pass and WhoAmI should fail**. That is expected —
the app can authenticate to Entra ID, but Dataverse does not know it yet.

---

## 4. Create the application user in Dataverse

The step that is easy to miss and hard to diagnose.

Power Platform Admin Center > **Environments** > *your environment* > **Settings** >
**Users + permissions** > **Application users** > **+ New app user**.

1. **Add an app** — search for the application (client) ID from step 2.
2. **Business unit** — pick the root one, named after your organisation.
3. **Security roles** — click the pencil and assign one. Nothing works without this.

Role guidance:

| Role | Use |
|---|---|
| **System Customizer** | Good default while developing. Reads metadata and data, cannot change tenant settings. |
| **System Administrator** | Only if you hit privilege errors you cannot place. |
| Custom role | What Microsoft recommends for real use: read-only privileges on exactly the tables you query, stored in a solution so it travels with the app. |

Assigning the user but not a role produces a *different* error from not creating the user
at all, and `dvduck doctor` distinguishes them.

Two constraints worth knowing before you experiment:

- **One application user per app registration per environment.** You cannot create a
  second one for the same registration.
- **The username and primary email cannot be changed after creation.**

---

## 5. Verify

```bash
dotnet run --project src/DataverseDuck.Cli -- doctor account contact
```

Every check should pass:

```
[PASS] Configuration
[PASS] Token acquisition
[PASS] Token claims
[PASS] WhoAmI (application user)
[PASS] Read privileges
[PASS] Expected tables
[PASS] SQL 4 CDS engine
```

Naming tables after `doctor` checks they exist — worth doing on a Developer Plan
environment before assuming `opportunity` is available.

---

## 6. Capture a metadata snapshot

Now the environment can be left behind. Snapshot the metadata for the tables you query,
and development, tests and CI run offline from then on (ADR 0003):

```bash
dotnet run --project src/DataverseDuck.Cli -- capture account contact --out metadata/snapshot.bin
```

Snapshots are gitignored by default. They describe your schema, so treat them as
potentially sensitive if the environment is a customer's.

---

## Troubleshooting

`dvduck doctor` prints a remedy with each failure. The underlying causes:

| Symptom | Cause |
|---|---|
| `AADSTS7000215` invalid client secret | Copied the Secret ID rather than the Value, or the secret expired. |
| `AADSTS90002` tenant not found | `DATAVERSE_TENANT_ID` wrong — likely the application ID pasted twice. |
| `AADSTS700016` application not found | Registration is in a different tenant. |
| Token fine, WhoAmI 401 | **No application user.** Step 4. |
| Connects, but privilege errors | Application user has no security role. Step 4, part 3. |
| Audience mismatch | Token requested for a generic scope instead of the environment URL. |
| Table missing | Dynamics 365 app table on a Developer Plan environment. Step 1. |
| Cannot create the app registration | `usersCanRegisterApplications` is off. Ask for the Application Developer role. Step 0. |
| Secret creation blocked, or capped at a short lifetime | An app management policy is in force. Prefer a certificate where longevity matters. Step 0. |
| No **Application users** page in the environment | You are not System Administrator there. Automatic in an environment you created; otherwise ask its admin. Step 0. |

### Why not the TDS endpoint?

The TDS (SQL) endpoint would allow a normal SQL client to connect. It **does not support
service principal authentication** — only interactive Entra ID sign-in with MFA, or Entra
ID password auth. Given the app-registration requirement, it is unavailable to us
regardless of any other consideration. See ADR 0001.
