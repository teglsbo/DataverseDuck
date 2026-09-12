# 13. Persist the device-code token cache to disk

Date: 2026-09-08

## Status

Accepted.

## Context

`DeviceCodeCredential` built a `PublicClientApplication` and left MSAL's token
cache at its default, which lives in the process. The consequence was that every
invocation of `dvduck` prompted for a new device code. Measured: two consecutive
`dvduck doctor` runs seconds apart issued codes `GLELQFE4J` and `CDL8YF5WC`, and
no cache file existed anywhere under `$HOME`.

That made device-code a mode for a single interactive command and nothing else. A
REPL restart, a script, or an agent driving the CLI each needed a human at a
browser, which is the opposite of what the mode is for on a headless machine — and
headless is exactly where it was documented as the convenient option, since it
needs no app registration.

It also made one documented option inoperative. `DATAVERSE_USERNAME` is described
in the README and in `DeviceCodeCredential`'s own XML comment as selecting "which
cached sign-in to reuse **across runs** … when more than one has signed in on this
machine before". With an in-process cache, `GetAccountsAsync()` returns nothing in a
fresh process, so the option could never have had that effect.

The forces that make this more than a one-line change:

- **A persisted MSAL cache holds a refresh token.** That is a credential: it is
  exchangeable for access tokens, without a prompt, for as long as the tenant
  honours it. Persisting it is a security decision, not a convenience one.
- **Encryption is unavailable precisely where persistence matters most.** MSAL's
  extensions encrypt the cache with the platform secret store — login keyring on
  Linux, Keychain on macOS, DPAPI on Windows. A container or an SSH session
  usually has no keyring, and that is the environment where re-prompting hurts.
- **The blast radius is a person's access, not an application's.** Device-code
  authenticates a user, so a leaked cache carries whatever that user can reach in
  Dataverse — typically far more than a purpose-scoped application user.

## Options considered

### A. Leave the cache in memory

The status quo.

**Rejected.** It makes the mode unusable for anything but a single command, and
leaves `DATAVERSE_USERNAME` documenting behaviour that cannot occur.

### B. Persist, but only when the platform can encrypt it

Attach the cache when a keyring, Keychain or DPAPI answers; otherwise stay in
memory.

**Rejected, and this was the closest call.** It is the safer default, and it
disables the feature exactly where it was asked for. A headless Linux box has no
keyring, so the outcome would be that persistence works on developer laptops and
silently does not work on the machines that need it — the worst shape for a
feature, because it appears to work until it is relied upon.

### C. Persist, encrypted where possible, unencrypted with owner-only permissions otherwise

**Adopted.** Encrypted via the platform store when one answers. Otherwise a
plaintext file at mode `600` in a directory at `700`. This is the same trade the
Azure CLI and the GitHub CLI make for their own refresh tokens, so it is a
familiar exposure rather than a novel one, and it is reported rather than assumed:
`dvduck doctor` prints a `[WARN] Token cache` line naming the file and saying the
token is in the clear, with the remedy.

`DATAVERSE_TOKEN_CACHE_PERSIST=0` restores option A for anyone whose policy
forbids a token on that disk, without also forcing them off device-code.

### D. Encrypt the cache ourselves

Derive a key and encrypt the file independently of the platform store.

**Rejected.** The key has to live somewhere, and every answer to where is either
the platform secret store — already tried, by definition unavailable in this
branch — or another file beside the cache, which protects against nothing. Rolling
this by hand would buy the appearance of encryption and no property worth having.

### E. Recommend app-only credentials instead and change nothing

**Rejected as the whole answer, adopted as advice.** A client secret or a
certificate is the correct choice for unattended access, and the documentation now
says so at the point where the cache is described. But it does not address the
interactive user re-authenticating on every command, which is the complaint, and
it requires an app registration that device-code deliberately avoids.

## Decision

Persist the device-code token cache, on by default, via
`Microsoft.Identity.Client.Extensions.Msal` (pinned to 4.79.0, matching the
existing `Microsoft.Identity.Client`).

- Location: `$XDG_DATA_HOME/dvduck/msal.cache`, defaulting to
  `~/.local/share/dvduck/msal.cache`. Data rather than cache, deliberately: losing
  it costs a sign-in, which is more than something named "cache" should cost.
  `DATAVERSE_TOKEN_CACHE` overrides it.
- Encrypted by the platform secret store where one answers; otherwise a plaintext
  file at `600` in a directory at `700`.
- `MsalCacheHelper.CreateAsync` succeeds even where the backing store does not
  work, so `VerifyPersistence()` is called before the result is trusted.
- Failure to persist is never fatal. It degrades to memory, and a prompt is a
  working outcome.
- Attached for device-code only. The secret and certificate credentials
  re-acquire silently from something already on disk, so caching their tokens
  would add exposure and save nothing.
- `dvduck doctor` reports which of the three outcomes occurred.

## Consequences

**Positive**

- One sign-in carries subsequent runs until the refresh token expires, which is
  what makes the mode usable from a script, a restarted REPL, or an agent.
- `DATAVERSE_USERNAME` now does what it has always claimed to do.
- The exposure is stated in `doctor` output rather than left to be discovered,
  and it is one command to turn off.

**Negative, and why it is accepted**

- **There is now a credential on disk that there was not before.** On a machine
  with no keyring it is in the clear. Mode `600` stops another unprivileged user
  reading it and stops nothing else: anything running as that user, or with root,
  can take it, and it carries the signed-in person's Dataverse access rather than
  an application's. This is the cost of option C over option B and it is real.
  The mitigations are the permissions, the `doctor` warning, the opt-out, and the
  documented preference for app-only credentials when nobody is at the keyboard.
- A shared cache file means a second profile against a different tenant writes into
  the same file. MSAL keys entries by account so this is correct, and
  `DATAVERSE_USERNAME` selects between them, but the file is not per-profile and
  deleting it signs every profile out.
- One more package dependency, in the authentication path.

## Evidence

- Two consecutive `dvduck doctor` runs before the change issued different device
  codes; `find $HOME` for an MSAL cache returned nothing. After the change, the
  second run acquired a token with no prompt and
  `~/.local/share/dvduck/msal.cache` existed at `-rw-------` inside a `drwx------`
  directory.
- `dvduck doctor` on the same machine reports
  `[WARN] Token cache — persisted UNENCRYPTED, owner-only permissions`, the
  keyring being unavailable in this container.
- `DeviceCodeCredential`'s XML comment before the change: "caches the resulting
  token in memory for the process lifetime via MSAL's own silent-token-first
  behaviour".
- `TokenCacheStoreTests` — 22 tests covering path resolution, the enable/disable
  variable, directory permissions, the describe wording, and the degrade-to-memory
  path when the location cannot be created.
- ADR 0003 — the metadata snapshot, the other mechanism for working without a
  live connection.
