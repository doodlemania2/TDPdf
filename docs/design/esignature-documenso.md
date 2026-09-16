# E-Signature: native Documenso integration — design

Status: **design approved with amendments, build blocked.** See "Decisions taken" below.
Tracked by TDPdf issue #176. Written 2026-09-15 for the 2.0.0 train.

## Decisions taken (Derek, 2026-09-15)

| Question | Decision |
|---|---|
| Token storage | **DPAPI, `CurrentUser` scope.** Approved as a deliberate reversal of the 1.24.0.0 removal: that removal was about a fleet-provisioned app secret being hidden from the machine's owner; this is the user's own credential held on their behalf. |
| Auth model | **Per-user token** for now. Derek's note: *"Maybe we could extend it to support better? It's open source after all"* — so **scoped tokens are to be contributed to our Documenso fork** rather than worked around in TDPdf. TDPdf's client is a token in a header either way, so it does not block on that work. |
| Validation | **Ship the signing surface dark** (Derek: *"We may want to ship sign dark"*), consistent with how telemetry is already inert until policy-provisioned. **No local PAdES validation** — no cryptographic code is added to TDPdf in 2.0.0; evidence comes from the server's certificate and audit trail, and TDPdf asserts no validity of its own. |
| Dev instance | **Wait for the hosted instance.** No local Docker instance. This is what blocks the build. |

## What blocks the build

1. The hosted Documenso instance must be deployed and configured.
2. The **hostname must be settled first** — Documenso embeds its own address in the PAdES `location` field of every signature and in every emailed signing link, so changing it after the first real signature leaves dead audit-trail URLs and broken links in inboxes. Tracked as step 0 of TRACK 2 in Derek's task inbox.

The SSL.com certificate is **not** a blocker for building: it changes only whether Adobe shows a green check or a yellow warning, and arrives as a container configuration change with no TDPdf release.

---

# TDPdf ↔ Documenso — Sign Document integration design

**Status:** design for review. No code written.
**Target:** TDPdf 2.0.0, branch `release/2.0.0`.
**Scope:** native integration with a self-hosted Documenso instance — auth, send, sign, status, validate.

---

## 0. Executive summary

| Ask | Recommendation |
|---|---|
| **auth** | Build. API-token only (Documenso has no API OAuth). Themed paste-token dialog + browser handoff to Documenso's own token page. Token stored via **DPAPI `CurrentUser`** in `%LOCALAPPDATA%\TDPdf\documenso.cred`, never in `user.config`. |
| **send** | Build. `POST /api/v2/envelope/create` (multipart) → `recipient/create-many` → `field/create-many` → `envelope/distribute`. |
| **status** | Build, **by polling**. Webhooks are unusable from a desktop app. On-demand refresh only; no background poller in 2.0.0. |
| **sign** (self-sign) | Build as a **browser handoff**. Documenso's `field.sign` is internal tRPC with no public REST path — there is no supported way to sign programmatically. TDPdf opens the recipient's `signingUrl` in the default browser. |
| **validate** | **Do not build local PAdES verification in 2.0.0.** Build a server-backed "Verify with Documenso" (certificate PDF + audit log) plus a deliberately *informational-only* local read of signer identity. Rationale in §7. |

Two non-obvious findings that shape the design:

1. **TDPdf actively destroys digital signatures on save.** `ScrubDeadSignatures` / `ScrubSigFieldValues` in `/Volumes/Data/repos/TDPdf/MainWindow.Files.cs:1963-1990` strip `/V` from every `/FT /Sig` field and remove the catalog's `/Perms` on every save, by design (a TDPdf save rewrites the whole file, so any existing `/ByteRange` digest is void). A Documenso-completed PDF opened and saved in TDPdf therefore silently loses its signature. This must be guarded, not discovered later — see §5.5.
2. **The Documenso API token is a full-account credential with no scoping**, so — unlike the OTLP token — it must **not** be pushed by Intune policy. Policy carries the *base URL only*. §3 explains the asymmetry.

---

## 1. API findings

### 1.1 Versions

- **v2 is the current API.** Base path `/api/v2`, served from the same origin as the web app (`NEXT_PUBLIC_WEBAPP_URL`). It "fully launched with v2.0.0 (released 10 Nov 2025)"; "API v1 remains stable but will no longer receive new feature updates." ([changelog](https://documenso.com/changelog), [authentication docs](https://docs.documenso.com/docs/developers/getting-started/authentication))
- v1 (`/api/v1`) still exists and is documented as deprecated / legacy-migration only.
- **Decision: target v2.** It is the only version getting the envelope model, the certificate/audit-log download routes, and the `version=signed|original|pending` download switch. Consequence: **the self-hosted instance must be Documenso ≥ 2.0.0.**
- v2's core resource is the **envelope** (one envelope = one or more PDF "envelope items" + recipients + fields). "Documents (called *envelopes* in the API) are the core resource." ([documents docs](https://docs.documenso.com/docs/developers/api/documents))
- **There is no official .NET/C# SDK** — only TypeScript, Python and Go. ([SDK listing](https://github.com/documenso)) We hand-roll a thin client, which is also what the single-file GPLv3 build wants (no new dependency, no notices churn).

### 1.2 Endpoints we will use

All paths below are relative to `{BaseUrl}/api/v2`. Method/path pairs verified against both the docs and the route `meta` in source (`packages/trpc/server/envelope-router/*.types.ts`).

| Purpose | Method | Path | Notes |
|---|---|---|---|
| Create envelope (upload PDF) | `POST` | `/envelope/create` | `contentTypes: ['multipart/form-data']`; parts are **`payload`** (JSON: `title`, `type`, `externalId`, `visibility`, `recipients[]`, `meta`, …) and **`files`** (repeatable). Recipients *and their fields* may be supplied in this one call; each field carries an `identifier` linking it to an uploaded file by name or index. Auto-scans PDFs for `{{signature, r1}}`-style text placeholders and creates fields there. ([source](https://raw.githubusercontent.com/documenso/documenso/main/packages/trpc/server/envelope-router/create-envelope.types.ts), [docs](https://docs.documenso.com/docs/developers/api/documents)) |
| Get one envelope | `GET` | `/envelope/{envelopeId}` | Returns status, recipients, fields, and the **envelope items with their ids** — which is how you get an `envelopeItemId` for download. |
| List envelopes | `GET` | `/envelope` | `page`, `perPage`, `type`, `status`, `source`, `folderId`, `orderByColumn`, `orderByDirection`. Also doubles as our cheap token-validation probe. |
| Get many by id | `POST` | `/envelope/get-many` | `ids.type` ∈ {envelopeId, documentId, templateId}, `ids.ids` 1–20. Good for refreshing a tracked list in one call. |
| Update envelope | `POST` | `/envelope/update` | DRAFT only. |
| Add recipients | `POST` | `/envelope/recipient/create-many` | Roles: `SIGNER`, `APPROVER`, `VIEWER`, `CC`, `ASSISTANT`. Fields incl. `signingOrder`, `token`, `readStatus`, `signingStatus` (`NOT_SIGNED`/`SIGNED`/`REJECTED`), `sendStatus`, `signedAt`. ([docs](https://docs.documenso.com/docs/developers/api/recipients)) |
| Update / delete recipients | `POST` | `/envelope/recipient/update-many`, `/envelope/recipient/delete` | Only while the envelope is not completed. |
| Add fields | `POST` | `/envelope/field/create-many` | 11 types: `SIGNATURE`, `FREE_SIGNATURE`, `INITIALS`, `NAME`, `EMAIL`, `DATE`, `TEXT`, `NUMBER`, `RADIO`, `CHECKBOX`, `DROPDOWN`. Geometry is **percentage based**: `page` 1-indexed, `pageX`/`pageY`/`width`/`height` all 0–100, origin top-left. **Field *values* cannot be prefilled at creation**, and nothing can be modified after the envelope is sent. ([docs](https://docs.documenso.com/docs/developers/api/fields)) |
| Send for signing | `POST` | `/envelope/distribute` | `{ envelopeId, meta? }`. **Response returns each recipient with `signingUrl` (from `formatSigningLink(recipient.token)`) and the raw `token`.** ([source](https://raw.githubusercontent.com/documenso/documenso/main/packages/trpc/server/envelope-router/distribute-envelope.types.ts)) This is the hinge for self-signing. |
| Download a file | `GET` | `/envelope/item/{envelopeItemId}/download` | `version` ∈ `original` \| `signed` (default) \| `pending`. Response `Content-Type: application/pdf`, body is raw bytes. `pending` renders currently-inserted fields and is explicitly **not** a final executed document. ([source](https://raw.githubusercontent.com/documenso/documenso/main/packages/trpc/server/envelope-router/download-envelope-item.types.ts)) |
| Download signing certificate | `GET` | `/envelope/{envelopeId}/certificate/download` | `Content-Type: application/pdf`. "Download the signing certificate for a completed document as a PDF." ([source](https://raw.githubusercontent.com/documenso/documenso/main/packages/trpc/server/envelope-router/download-envelope-certificate-pdf.types.ts)) |
| Download audit log PDF / list audit entries | `GET` | audit-log routes (`auditLog.find`, `auditLog.downloadPdf`) | Confirmed present in the router; exact REST paths to be read off the live instance's OpenAPI at implementation time (they follow the same `/envelope/{envelopeId}/...` shape). |
| Cancel / delete | `POST` | `/envelope/cancel`, `/envelope/delete` | Completed envelopes cannot be deleted. |

Full route inventory (48 routes) confirmed from [`envelope-router/router.ts`](https://raw.githubusercontent.com/documenso/documenso/main/packages/trpc/server/envelope-router/router.ts).

### 1.3 Authentication

- **API tokens only. There is no OAuth or OIDC for the API.** Documenso's OIDC and "Sign in with Microsoft" support (v1.5.5, v2.0.0) is for *user sign-in to the web app*, not for API authorization. ([changelog](https://documenso.com/changelog))
- Header format is a **bare token, no `Bearer` scheme**:
  ```
  Authorization: api_xxxxxxxxxxxxxxxx
  ```
  ([authentication docs](https://docs.documenso.com/developers/public-api/authentication)) — this matters in C#: `HttpRequestMessage.Headers.Authorization` wants a scheme + parameter, so we must use `Headers.TryAddWithoutValidation("Authorization", token)`.
- **Tokens are created only in the Documenso web UI** (avatar → Settings → API Tokens → Create token). There is *no* programmatic token-creation endpoint. Expiry choices: never / 7 days / 1 month / 3 months / 6 months / 1 year. The value is shown once.
- **Tokens are unscoped**: "API tokens have full access to your account… There is currently no way to create tokens with limited scopes or permissions." See also [documenso#2098](https://github.com/documenso/documenso/issues/2098) (open request for per-document scoped keys).
- No documented rate limits for self-hosted. Treat as unknown; be conservative (see §5.4).

**This single fact — UI-only, unscoped, full-account tokens — determines the entire auth UX.** There is no silent/SSO path. The user must visit the web UI once, and TDPdf must hold a credential that is equivalent to their whole Documenso account.

### 1.4 Webhooks

Documenso supports webhooks for document created / sent / opened / signed / completed / rejected / cancelled, recipient-completed / reminder-sent / recipient-expired, and template created / updated / deleted / used, with HMAC signature verification. ([webhooks docs](https://docs.documenso.com/docs/developers/webhooks))

**A desktop app cannot usefully consume them.** Webhooks require a stable, publicly reachable HTTPS ingress. TDPdf runs on laptops behind NAT, asleep half the day, with no certificate and no DNS. Every workaround (a relay service, a tunnel, a shared inbox) is new server-side infrastructure that is not TDPdf's to own.

**Polling is the realistic answer, and it is fine here**, because signature status is checked by a human who is already looking at the screen:
- `GET /envelope` when the *Signature Requests* window opens, plus a Refresh button.
- `POST /envelope/get-many` (≤20 ids) for a cheap refresh of the envelopes this device created.
- Optional light auto-refresh **only while that window is open** (30 s), cancelled on close.
- No background service, no toasts, no polling at app start. See §7.

### 1.5 Signature validation — what Documenso gives us

- On completion Documenso seals the PDF with a **PKCS#12 certificate, producing PAdES-compatible signatures** — "the same cryptographic standard that DocuSign and Adobe Sign use." ([signing certificate docs](https://docs.documenso.com/docs/developers/local-development/signing-certificate), [SIGNING.md](https://github.com/documenso/documenso/blob/main/SIGNING.md))
- Self-hosted signing config: `NEXT_PRIVATE_SIGNING_LOCAL_FILE_PATH` (default `/opt/documenso/cert.p12`) **or** `NEXT_PRIVATE_SIGNING_LOCAL_FILE_CONTENTS` (base64), plus `NEXT_PRIVATE_SIGNING_PASSPHRASE`. Google Cloud HSM is the other transport. ([docker docs](https://docs.documenso.com/docs/self-hosting/deployment/docker))
- **Documenso supports RFC 3161 timestamping** via `NEXT_PRIVATE_SIGNING_TIMESTAMP_AUTHORITY` (a list, tried in order), which is what enables **LTV / archival timestamps**. ([timestamp server docs](https://docs.documenso.com/docs/self-hosting/configuration/signing-certificate/timestamp-server)) — *This is a Derek action item, not a TDPdf one, and it materially reduces the value of any local verifier we write.*
- Two operational endpoints worth wiring into our "is this thing working" surface:
  - `GET {BaseUrl}/api/health` — container health.
  - `GET {BaseUrl}/api/certificate-status` — "reports whether a certificate is configured, its type, and any errors"; `"isAvailable": true` means documents can be sealed. ([self-hosting tips](https://docs.documenso.com/docs/self-hosting/getting-started/tips))
- Evidence artifacts available over the API: the **signing certificate PDF** and the **audit log** (both as PDFs, plus a structured audit-log listing). Footers carry the envelope id for reconciliation.

**What TDPdf would have to do locally to "validate" properly** — and why we should not:

TDPdf today has **zero** cryptographic code. Verified: no `X509*`, no `SignedCms`, no `CmsSigner`, no `ProtectedData`, no `/ByteRange` construction anywhere outside `third_party/`. The only `System.Security.Cryptography` use in the whole app is one `SHA256.HashData` call in `/Volumes/Data/repos/TDPdf/Diagnostics/Sanitizer.cs:70`, for crash grouping. The only mention of `/ByteRange` is the comment above `ScrubDeadSignatures`.

A *defensible* PAdES-B-LT verifier means all of:
1. Incremental-update-aware PDF parsing to enumerate every `/FT /Sig` field and its `/V` signature dictionary (PdfSharpCore can read the dictionaries; PdfPig cannot help here).
2. `/ByteRange` handling: extract the covered bytes, and — the part everyone gets wrong — prove the ranges actually cover the whole file, detect unsigned incremental updates appended after the last signature, and detect overlapping/gapped ranges.
3. Detached CMS verification. `System.Security.Cryptography.Pkcs.SignedCms` is in-box and does the heavy lifting ([SignedCms](https://learn.microsoft.com/dotnet/api/system.security.cryptography.pkcs.signedcms)), but correct use also means checking the `messageDigest` and `signingCertificateV2` signed attributes, not just `CheckSignature`.
4. `X509Chain` building with an explicitly chosen policy (revocation mode, validity time = signing time not now, EKU), and a decision about which roots to trust.
5. RFC 3161 signature-timestamp verification (`Rfc3161TimestampToken`, in-box) and its own chain.
6. DSS / VRI dictionary handling plus CRL/OCSP for LTV.
7. `DocMDP` / `FieldMDP` permission evaluation to decide whether post-signing changes were *allowed*.
8. Correct multi-signature semantics (which signature covers which revision).

That is a large, security-critical body of new code whose failure mode is **showing a green check on a document that is not valid**. Getting it wrong is worse than not having it. See §7 for the alternative.

### 1.6 Self-signing

**There is no public REST endpoint to sign.** The router does register a `field.sign` route ([`sign-envelope-field.ts`](https://raw.githubusercontent.com/documenso/documenso/main/packages/trpc/server/envelope-router/sign-envelope-field.ts)) taking `{ token, fieldId, fieldValue, authOptions? }`, and it is an *unauthenticated public procedure* keyed on the recipient token — but its `.types.ts` has **no `openapi` meta block**, unlike every REST-exposed route (compare `distribute-envelope.types.ts`, which carries `openapi: { method: 'POST', path: '/envelope/distribute', … }`). It is internal tRPC, used by Documenso's own signing page. Likewise `signingStatus` — no openapi block.

So: **self-signing = browser handoff.** `POST /envelope/distribute` returns `recipients[].signingUrl`; TDPdf opens it with the default browser. Documenso's own embedding docs point the same way — API-driven workflows use the recipient signing token, and direct links are of the form `{BaseUrl}/sign/direct/{token}`. ([embedding](https://docs.documenso.com/docs/developers/embedding), [direct links](https://docs.documenso.com/users/direct-links))

Relying on the undocumented tRPC route would be a bad trade: unversioned, unannounced breaking changes, and it would require TDPdf to render and submit a signature image into someone else's field model.

---

## 2. Auth design

### 2.1 UX

Entry point is **Settings → Document signing**, and the first-run path from the *Send for Signature* flow.

**State machine (4 states, all silent):**

| State | Condition | UI |
|---|---|---|
| **Unavailable** | no base URL from any source | The whole E-Signature submenu is `Collapsed`. Settings shows one grey line: *"No document-signing service is configured on this device."* Nothing else. |
| **Not connected** | base URL present, no stored token | Submenu visible, items enabled. Any action routes first to *Connect*. Settings shows **[Connect…]**. |
| **Connected** | base URL + token that validated | Submenu fully live. Settings shows *"Connected to `sign.example.com` as `name@example.com`."* + **[Disconnect]** + **[Test connection]**. |
| **Stale** | token present but rejected (401) or undecryptable | Same as *Not connected*, plus one inline line in the relevant window: *"Your document-signing sign-in has expired. Connect again to continue."* Never a modal on startup. |

**Connect flow** — a themed `DocumensoConnectWindow`. There is no dialog XAML anywhere in this repo (only 5 `.xaml` files exist: `App.xaml`, `MainWindow.xaml`, and the three `Themes/*.xaml`), so every dialog is built imperatively in C#. Two chrome recipes are available; use the **custom-chrome** one, matching the signature creator:

- **Custom chrome** — `OpenSignatureCreator()` at `/Volumes/Data/repos/TDPdf/MainWindow.Signatures.cs:365`: `WindowStyle = WindowStyle.None`, `AllowsTransparency = true`, `Background = Brushes.Transparent`, an `outerChrome` `Border` (`BgDark` + `AccentGreenDim` 1px + `CornerRadius(6)`), a `titleBar` `Border` (`BgPanel`, `Padding(14,8,8,8)`, `CornerRadius(5,5,0,0)`, `MouseLeftButtonDown → win.DragMove()`), title `TextBlock` in `AccentGreen`/SemiBold/13/Consolas, and a 28×28 `Segoe MDL2 Assets` close glyph that goes `DangerRed` on hover. `TdpDialog.CreateShell` (`TdpDialog.cs:74`) is the same recipe at width 380. Shared frozen brushes for this chrome already exist: `SignatureBorderBrush` / `DialogCloseNormalBrush` at `MainWindow.xaml.cs:374-375`.
- **Native frame** — `ShowSettingsDialog()` keeps the OS frame and only sets `Background`/`Foreground`. Fine for Settings (which already looks that way); wrong for a new modal.

Brush resolution: inside `MainWindow` partials use `BrushResource(string)` (`MainWindow.xaml.cs:2021`, `(SolidColorBrush)FindResource(key)` — **throws on a bad key**). In a standalone class under `Services/`, copy `TdpDialog.Brush(string)` (`TdpDialog.cs:39`), which is `TryFindResource(key) as SolidColorBrush ?? SystemBrush(key)` with a `SystemColors.*` fallback — that fallback is what keeps a dialog legible if a theme dictionary is missing. Keys to use: `BgDark`, `BgPanel`, `BgHover`, `BorderDim`, `AccentGreen`, `AccentGreenDim`, `DangerRed`, `WarningOrange`, `TextPrimary`, `TextSecondary`, `TextMuted`, `DisabledForeground`, `SeparatorBrush`. Buttons: `Style = (Style)FindResource("DarkButton")`.

Flow:

1. Line 1: *"TDPdf signs documents through your organisation's Documenso service at `{host}`."*
2. Button **[Open Documenso in your browser]** → `Process.Start(new ProcessStartInfo($"{BaseUrl}/settings/tokens") { UseShellExecute = true })`, with a short instruction: *Settings → API Tokens → Create token → copy it.* (If that path 404s on the target build, open `{BaseUrl}` — see open question Q6.)
3. A `PasswordBox`-style masked input for the token, with a **Paste** button, plus an explicit caution line: *"This token gives TDPdf full access to your Documenso account. Set an expiry when you create it."*
4. **[Connect]** → validate with `GET /api/v2/envelope?perPage=1`:
   - `2xx` → store, close, status bar *"Connected to Documenso."*
   - `401/403` → inline red text *"Documenso rejected that token."* — stay open, nothing stored.
   - network/DNS/timeout → inline *"Could not reach `{host}`."* — offer to store anyway? **No.** Never store an unvalidated credential.
5. **[Disconnect]** deletes the credential file and zeroes the in-memory copy. It does **not** revoke the token server-side (no API for that); the dialog says so and links to the Documenso tokens page.

`TdpDialog.PromptPassword(Window?, string)` already exists at `/Volumes/Data/repos/TDPdf/TdpDialog.cs:294` for encrypted PDFs. It is close but not right — it is filename-shaped and single-purpose. Build the small dedicated window; do not overload it.

### 2.2 Storage — DPAPI, not Credential Manager

**Recommendation: Windows DPAPI, `System.Security.Cryptography.ProtectedData` with `DataProtectionScope.CurrentUser`.**

> **Read this first — DPAPI was deliberately removed from this codebase.** 1.24.0.0 deleted the DPAPI-encrypted App Insights provisioning file, and `App.xaml.cs:229` / `:276` still actively delete the legacy file; `Diagnostics/TelemetryConfig.cs:34` and `Diagnostics/TelemetryStore.cs:12` record why. **The thing that was rejected is not the thing proposed here.** What was removed was *a build-time-provisioned, fleet-wide, app-owned secret obfuscated inside a file shipped to end-user laptops, which could not be rotated without a release.* What this proposes is *a per-user credential the user personally created and personally pasted, protected at rest, rotatable by that user at any moment from Documenso's own UI, and deletable with one button.* Those are opposite cases: the first was a secret the app was hiding from its owner, the second is the owner's own secret held on their behalf. The precedent that actually binds is the other half of the same decision — **no compiled-in destination, no fleet-pushed credential** — and §3.1 honours it. Flagged as **Q10** so Derek can overrule.

- File: `%LOCALAPPDATA%\TDPdf\documenso.cred` — the same directory the app already uses for per-user state (`signatures.json`, the telemetry marker; `MainWindow.xaml.cs:369-373`).
- Blob: `ProtectedData.Protect(utf8TokenBytes, optionalEntropy, DataProtectionScope.CurrentUser)`.
- **Entropy binds the credential to the endpoint it was issued for:** `optionalEntropy = SHA256("TDPdf/Documenso/v1" + normalisedBaseUrl)`. If policy repoints `BaseUrl` at a different host, the old blob will not decrypt, so a token can never be silently replayed against a host it was not issued for. That failure is a clean fall-through to *Not connected*.
- Alongside, in plain text in the same small JSON file (these are **not** secrets and are needed to render the Settings line without decrypting): the base URL the token was issued for, the account email/name returned at connect time, and an ISO timestamp.

**Why DPAPI over Credential Manager:**
- DPAPI is a two-call managed API. Credential Manager (`CredWrite`/`CredRead`/`CredDelete`) has no in-box managed surface in .NET — it needs P/Invoke, struct marshalling, and manual `CredFree`. `Windows.Security.Credentials.PasswordVault` is a WinRT API and an awkward dependency for a WPF single-file app. TDPdf has zero P/Invoke for credentials today and this is not the feature that should introduce it.
- Credential Manager's advantage is user *visibility and revocability* through `control keymgr.dll`. We get the equivalent with **[Disconnect]** in Settings, and one file the user can delete.
- Credential Manager's *generic* credentials are readable by any process running as that user — exactly the same threat boundary as DPAPI `CurrentUser`. No security gain.
- DPAPI is what the repo's own history already reasoned about: the retired telemetry provisioning file used DPAPI (`/Volumes/Data/repos/TDPdf/Diagnostics/TelemetryConfig.cs`, class remarks).

**Implementation note:** `System.Security.Cryptography.ProtectedData` may need an explicit `PackageReference` (it is a Microsoft/MIT package and has historically not been in the default framework reference set). Confirm at implementation time; if it is added, **regenerate `THIRD-PARTY-NOTICES.md`** via `build/generate-third-party-notices.ps1` per the repo convention.

### 2.3 Roaming and portable installs

| Scenario | Behaviour |
|---|---|
| Normal per-user install (`%LOCALAPPDATA%\Programs\TDPdf\`) | Works. Blob decrypts for that user on that machine. |
| Roaming user profile | `%LOCALAPPDATA%` does **not** roam, so the token simply is not there on the second machine → *Not connected*, connect once more. Correct and safe. (We deliberately do **not** use `%APPDATA%`; roaming a DPAPI blob only works with roaming DPAPI master keys and is a support liability.) |
| **Portable install (USB stick)** | The credential is **never** written next to the EXE — it goes to `%LOCALAPPDATA%\TDPdf\`, which is where `signatures.json` already lives (`SignatureDir`/`SignatureFile`, `MainWindow.xaml.cs:369-373`, with a one-shot migration from the old beside-the-EXE location). On another machine the blob is absent → *Not connected*. Same machine + same user decrypts normally. A user who wants zero footprint uses *Disconnect*. |
| Same machine, different Windows user | Blob does not decrypt → *Not connected*. Correct. |
| Windows password reset by an admin (DPAPI master key loss) | Blob fails to decrypt → caught → *Not connected*. Never a crash, never a dialog at startup. |

Every decrypt path is `try { … } catch { return null; }` — the same discipline as `TelemetryConfig.TryReadRegistryValue`.

### 2.4 Never log, never telemeter the token

- The token is added **per request**, via `HttpRequestMessage.Headers.TryAddWithoutValidation("Authorization", token)` — **not** `HttpClient.DefaultRequestHeaders`. A client instance can then never carry the credential to a non-Documenso host.
- **Never in a URL.** No query-string token, ever (it would land in the server's access log and in any `HttpRequestException` message).
- `DocumensoApiException` carries only: HTTP status, the Documenso error *code*, the method, and the **path template** (`/envelope/distribute`, not the interpolated URL). **Never the response body** — Documenso error bodies are safe today but are not ours to control.
- Telemetry: existing `Telemetry.TrackEvent(name, props)` / `Telemetry.StartOperation(name)` (`/Volumes/Data/repos/TDPdf/Diagnostics/Telemetry.cs:115,162`). Allowed properties for Documenso events: `outcome` (ok/authfailed/network/server/cancelled), `httpStatus`, `recipientCount`, `fieldCount`, `pageCount`, `apiVersion`. **Forbidden, explicitly:** the token, the base URL or host, recipient names or email addresses, envelope ids, document titles, file names, file paths.
- **`Sanitizer.Scrub` does not save us here.** `/Volumes/Data/repos/TDPdf/Diagnostics/Sanitizer.cs:19` scrubs *paths*; an `api_…` token is not path-shaped and would pass straight through. The protection has to be that the token never enters a message in the first place. A one-line belt-and-braces addition to `Sanitizer.Scrub` — redact any `api_[A-Za-z0-9_-]{16,}` run — is cheap and worth doing, but it is the second line of defence, not the first.

---

## 3. Configuration shape

### 3.1 Registry — policy, mirroring the telemetry precedent

`/Volumes/Data/repos/TDPdf/Diagnostics/TelemetryConfig.cs` is the template and its reasoning transfers verbatim (`SOFTWARE\Policies\` because it is admin-pushed *and* because `App.Uninstall` does `DeleteSubKeyTree(@"Software\TDPdf")` against both hives — policy is not the application's to delete).

```
HKEY_LOCAL_MACHINE\SOFTWARE\Policies\TDPdf\Documenso
    Enabled            REG_DWORD 0 | 1      (absent ⇒ enabled — admin kill switch)
    BaseUrl            REG_SZ    https://sign.thedoodleproject.com
    AllowUserOverride  REG_DWORD 0 | 1      (absent ⇒ treated as 1)
```

`Enabled` follows the shape already established for updates at `/Volumes/Data/repos/TDPdf/Services/UpdateCheck.cs:103` (`SOFTWARE\Policies\TDPdf\Update`, value `Enabled`, absent-means-enabled):
```csharp
using var key = Registry.LocalMachine.OpenSubKey(PolicyPath);
return key?.GetValue("Enabled") is not int disabled || disabled != 0;
```
`Enabled = 0` forces the feature off **even if a user has typed a URL into Settings** — the switch an admin needs to disable a whole outbound integration on a managed device, and the convention every outbound feature in this app already carries.

`BaseUrl` is read with an explicit 64-bit view, exactly as `TelemetryConfig.TryReadRegistryValue` does (so WOW64 cannot redirect to `Wow6432Node`):
```
RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
```

**`BaseUrl` is the only policy value, and there is deliberately no `ApiToken`.** The asymmetry with `Policies\TDPdf\Telemetry` (which *does* carry `OtlpToken`) is the point:

| | OTLP token | Documenso token |
|---|---|---|
| Identity | fleet-wide, write-only ingest | **one named human's whole account** |
| Scope | append telemetry | full CRUD on every document that user can see |
| Pushing it to every device means | every device can write telemetry | every device can read, alter and delete that user's documents, and impersonate them as a signer |

A fleet-pushed Documenso token would be a credential-sharing defect, not a convenience. Per-user, per-device, user-entered, DPAPI-protected is the only correct shape.

### 3.2 Resolution order

Mirroring `TelemetryConfig.TryResolveOtlp`'s "first match wins", but with one extra tier because the URL is not a secret:

0. **`Enabled = 0`** under the policy key ⇒ **no endpoint**, unconditionally. Checked before anything else.
1. **`TDPDF_DOCUMENSO_BASEURL`** environment variable — developers, and someone self-hosting who wants to point a public build at their own instance without admin rights. Session-scoped and obvious to anyone reading it (the same argument `TelemetryConfig` makes for not using HKCU).
2. **`HKLM\SOFTWARE\Policies\TDPdf\Documenso\BaseUrl`** — the managed path. Intune configuration profile. *This is the one that matters in the fleet.*
3. **`Settings.Default.DocumensoBaseUrl`** (user.config) — the user-configurable path, honoured **only** when tier 2 is absent or `AllowUserOverride != 0`.

Normalisation: trim, strip a trailing `/` (as `TryResolveOtlp` does), require `https://` unless the host is `localhost`/`127.0.0.1` (dev), reject anything that is not an absolute URI. A malformed value resolves to **no endpoint** — degrade, don't error.

```csharp
// Services/DocumensoConfig.cs  — shape only
internal enum Source { None, Environment, Policy, User }
internal static (string BaseUrl, Source From)? TryResolveBaseUrl();
internal static bool HasEndpoint();        // the single gate the whole feature hangs off
internal static bool IsUserOverrideAllowed();
```

`HasEndpoint()` is the exact analogue of `TelemetryConfig.HasDestination()` and is what every enablement check calls.

### 3.3 New `Properties/Settings.cs` entries

Follow the existing `[UserScopedSetting] [DefaultSettingValue(...)]` pattern (`/Volumes/Data/repos/TDPdf/Properties/Settings.cs`). **None of these is a secret.**

| Setting | Type | Default | Purpose |
|---|---|---|---|
| `DocumensoBaseUrl` | `string` | `""` | tier-3 user-entered URL |
| `DocumensoDefaultSigningOrder` | `bool` | `False` | sequential vs parallel signing default in the send wizard |
| `DocumensoIncludeSelfAsSigner` | `bool` | `False` | remembered checkbox in the send wizard |
| `DocumensoWarnOnSignedSave` | `bool` | `True` | the don't-ask-again for the §5.5 scrub warning |

**These must tolerate being reset without warning.** `App.config` declares the section with `allowExeDefinition="MachineToLocalUser"`, so `user.config` lands at `%LOCALAPPDATA%\<company>\<exe>_<hash>\<version>\user.config`, and `EnsureSettingsHealthy` in `/Volumes/Data/repos/TDPdf/App.xaml.cs:258-420` force-parses it on startup and, on failure, logs `"SETTINGS CORRUPT - resetting user.config"` and calls `Settings.Default.Reload()`. A reset therefore silently reverts `DocumensoBaseUrl` to `""` → the feature falls back to the policy tier, or goes *Unavailable*. That is acceptable (nothing is lost but a typed URL) **only because the credential is not in `user.config`** — one more reason it is not.

### 3.4 User-facing settings path

`Settings…` is on the **File**-adjacent menu bar (`MainWindow.xaml:1188`, `Click="Settings_Click"`) and on the toolbar (`MainWindow.xaml:1264`). `ShowSettingsDialog()` at `/Volumes/Data/repos/TDPdf/MainWindow.xaml.cs:1712` builds the window **in code** — a `Window` with `Background = BrushResource("BgPanel")`, a `StackPanel`, section headers as `SemiBold` `TextBlock`s, and controls coloured via `BrushResource("TextPrimary")` / `BrushResource("TextSecondary")` (helper at `MainWindow.xaml.cs:2021`). A new section slots straight in after the telemetry block.

**New section — "Document signing":**

```
Document signing                                    ← SemiBold header
[  https://sign.thedoodleproject.com            ]   ← TextBox; IsReadOnly + greyed when policy-set
                                                       and AllowUserOverride == 0
Managed by your organisation.                       ← TextSecondary, 11px; shown only when policy-set
   — or —
No document-signing service is configured on this
device, so signing is unavailable.                  ← TextSecondary, 11px; the "tell the truth" line

[ Connect… ]  [ Test connection ]                   ← Connect becomes Disconnect when connected
Connected as derek@… · last checked 14:02           ← TextSecondary
```

That truth-telling line is lifted directly from the telemetry block's own comment at `MainWindow.xaml.cs` (*"Tell the truth about what the checkbox is actually doing"* / *"No reporting destination is configured on this device, so nothing is sent."*). Same idea, same tone.

### 3.5 `PRIVACY.md` and the disclosure convention — not optional

This repo has a hard, stated rule for outbound data. `Diagnostics/TelemetryConfig.cs:88`, on `host.name`: *"Disclosed under 'Device name' in PRIVACY.md; do not add one without the other."* Every existing outbound feature carries three things together, and this one must too:

1. **An entry in `/Volumes/Data/repos/TDPdf/PRIVACY.md`** — a new section stating plainly that when a document-signing service is configured, the *document itself*, the recipient names and email addresses the user types, and the user's Documenso account identity leave the device and go to that service; that the service is operated by the user's own organisation, not by TDPdf; and that nothing is sent when no service is configured. This is a bigger disclosure than telemetry has ever made — telemetry never sends document contents, and this feature sends whole documents by design. It must be stated in those words.
2. **An admin policy switch** — `Enabled` under the policy key (§3.1).
3. **A user-visible control** — the Settings section (§3.4), which doubles as the consent surface. Sending is *always* an explicit per-document user action, so a standing consent checkbox would be noise; the Settings section plus the confirmation in the send wizard is the honest equivalent. Say so in the PR description so the reviewer sees the deliberate difference from the telemetry pattern.

Missing any of the three should block the PR.

### 3.6 Test connection

**Test connection** is the only place that reports a *reachability* failure, and it reports it inline in the dialog — never as a popup. It calls, in order, `GET /api/health`, `GET /api/certificate-status`, `GET /api/v2/envelope?perPage=1`, and renders three lines. `certificate-status` reporting `isAvailable: false` is surfaced as *"The service is reachable but cannot yet seal documents — signing requests will not complete."* That one line will save a genuinely confusing support call while the SSL.com certificate is outstanding.

---

## 4. Naming — the two-signatures trap

TDPdf already has a "Signature": `Tools ▸ Si_gnature` (`MainWindow.xaml:1161`, gesture `G`, `ToolSignature_Click`), which places a `SignatureAnnotation` — a drawn or imported **image overlay**, persisted in `signatures.json` (`MainWindow.Signatures.cs`, `Models/Annotations.cs`). It carries no cryptography whatsoever. The new feature produces a cryptographically sealed PDF. Shipping both under the word "signature" will generate support tickets from day one.

**Recommendation:**

| Thing | Old name | New name | Where |
|---|---|---|---|
| image overlay (existing) | "Signature" | **"Signature Stamp"** | `Tools` menu, gesture `G` unchanged, tool button tooltip updated |
| cryptographic e-signature (new) | — | **"E-Signature"** | new `File ▸ E-Signature ▸` submenu |

Rename is **UI strings only** — do not rename `SignatureAnnotation`, `SavedSignature` (`Models/Annotations.cs:480`), `signatures.json`, `MainWindow.Signatures.cs` or any handler. Zero behaviour change, zero migration.

The identifier collisions this avoids are real and numerous: `ToolSignature_Click` (`MainWindow.xaml.cs:3725`), `ToolSignatureBtn` (`MainWindow.xaml:1286`), `SignatureButtonLabel` (`MainWindow.xaml:1242`), `EditTool.Signature`, `SavedSignature`, `ShowSignaturePopup`/`PlaceSignature`/`OpenSignatureCreator` (all in `MainWindow.Signatures.cs`), the `G` keyboard shortcut (`MainWindow.xaml.cs:7617`) and the `Si_gnature` menu mnemonic. **Every new identifier in this feature is prefixed `ESign`/`Documenso`, never `Signature`.**

Copy discipline throughout: the overlay is a *stamp*; the new thing is an *e-signature* or *digital signature*. Never "signature" bare in new strings.

---

## 5. Feature surface

### 5.1 Where the code lives

Confirmed partial-class layout on `release/2.0.0`: `MainWindow.xaml.cs`, `.Files.cs`, `.Canvas.cs`, `.TextEditing.cs`, `.Forms.cs`, `.Signatures.cs`, `.ContinuousView.cs`, `.Sidebar.cs`, `.Bookmarks.cs`, `.Undo.cs`.

The brief's proposed shape — `MainWindow.Signing.cs` + `Services/Documenso*.cs` — is **right**, with one refinement: the name `MainWindow.Signing.cs` sits one character away from `MainWindow.Signatures.cs`, which is the *other* feature. Use **`MainWindow.ESignature.cs`**. The disambiguation that §4 does for users should also hold for whoever opens this directory in six months.

```
Services/DocumensoConfig.cs            base-URL resolution + HasEndpoint()   (mirrors TelemetryConfig)
Services/DocumensoCredentialStore.cs   DPAPI load/save/delete + account metadata
Services/DocumensoClient.cs            thin REST client over HttpClient; no UI, no WPF types
Services/DocumensoModels.cs            DTOs / enums (EnvelopeStatus, RecipientRole, FieldType)
Services/DocumensoConnectWindow.cs     themed Connect/Disconnect dialog
Services/DocumensoSendWindow.cs        themed send-for-signature wizard
Services/DocumensoRequestsWindow.cs    themed status list
MainWindow.ESignature.cs               menu handlers, enablement, canvas field placement,
                                       download-and-open, status-bar wiring
```

`Services/` already houses standalone themed windows (`PrintPreviewWindow.cs`, `ThirdPartyLicensesWindow.cs`, `TransformWindow.cs`, and `Diagnostics/CrashDialog.cs`), so window classes belong there. **No DI container. `ViewModels/MainWindowViewModel.cs` is not touched** (issue #18).

Two facts about the split that matter in practice:
- **`partial class MainWindow` is not limited to `MainWindow.*.cs`.** `Ocr.cs:18` and `Cli.cs:45` are also partials of it (which is how `Cli.cs:700` calls `MakeDownloadClient()` without declaring it). So `MainWindow.ESignature.cs` automatically shares every private member — `BrushResource`, `MakeDownloadClient`, `_doc`, `_ctx`, `_currentFile`, `SetStatus`, the manual element refs. Nothing needs to be made `internal`.
- **`MainWindow.Undo.cs` (76 lines) is the template for a minimal new partial** — file header comment, one `partial class MainWindow`, no regions.

Current sizes on `release/2.0.0`: `MainWindow.xaml.cs` 11,351 · `.Files.cs` 2,546 · `.Canvas.cs` 1,717 · `.TextEditing.cs` 1,279 · `.Forms.cs` 1,218 · `.Signatures.cs` 1,096 · `.ContinuousView.cs` 728 · `.Sidebar.cs` 656 · `.Bookmarks.cs` 587 · `.Undo.cs` 76 lines.

### 5.2 HTTP — reuse the existing shape exactly

*(Correction to the brief: `Services/OcrNativeBootstrap.cs` contains no HTTP at all — it only does embedded-resource self-extraction. `Cli.cs` has no `System.Net.Http` using either; it reaches `MakeDownloadClient()` because it is another `partial class MainWindow`. The OCR download lives in `Ocr.cs`.)*

Two existing HTTP call sites, and they agree:
- `/Volumes/Data/repos/TDPdf/Ocr.cs:262` — `MakeDownloadClient()`: `new HttpClient { Timeout = TimeSpan.FromSeconds(100) }`, `DefaultRequestHeaders.UserAgent.ParseAdd("TDPdf-OCR")`, streamed with `HttpCompletionOption.ResponseHeadersRead` and a `CancellationToken`, `.part` file + atomic `File.Move`.
- `/Volumes/Data/repos/TDPdf/Services/UpdateCheck.cs:143` — `using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) }`, explicit `User-Agent`, `System.Text.Json` `JsonDocument.Parse`.

So: **a short-lived `HttpClient` per operation, explicit `Timeout`, explicit `User-Agent`, `System.Text.Json`, `CancellationToken` on everything.** No shared static client, no `SocketsHttpHandler`, no `IHttpClientFactory`, no Polly, no Refit, no new package. `System.Text.Json` is already used across ten files and `Newtonsoft` appears nowhere — reflection-based `JsonSerializer` is fine (source-gen is unnecessary: `PublishTrimmed` is off).

Concrete client shape:
```csharp
internal sealed class DocumensoClient
{
    private readonly string _baseUrl;     // no trailing slash
    private readonly string _token;       // never logged, never in a URL
    internal DocumensoClient(string baseUrl, string token);

    Task<DocumensoAccount?>       ProbeAsync(CancellationToken ct);
    Task<string>                  CreateEnvelopeAsync(CreateEnvelopeRequest r, Stream pdf, string fileName, CancellationToken ct);
    Task                          CreateRecipientsAsync(string envelopeId, IReadOnlyList<RecipientDraft> r, CancellationToken ct);
    Task                          CreateFieldsAsync(string envelopeId, IReadOnlyList<FieldDraft> f, CancellationToken ct);
    Task<IReadOnlyList<Distributed>> DistributeAsync(string envelopeId, CancellationToken ct);
    Task<Envelope>                GetEnvelopeAsync(string envelopeId, CancellationToken ct);
    Task<IReadOnlyList<Envelope>> ListEnvelopesAsync(EnvelopeQuery q, CancellationToken ct);
    Task                          DownloadItemAsync(string itemId, string version, string destFile, CancellationToken ct);
    Task                          DownloadCertificateAsync(string envelopeId, string destFile, CancellationToken ct);
    Task                          CancelAsync(string envelopeId, string? reason, CancellationToken ct);
}
```
Upload uses `MultipartFormDataContent` with a `payload` part (`application/json`) and one or more `files` parts (`application/pdf`). Downloads reuse the `Ocr.cs` `.part`-then-`File.Move` pattern so a half-written signed PDF never appears on disk.

Timeouts: 15 s for probe/metadata calls, 120 s for upload and download.

### 5.3 Menus and enablement

New submenu in the **File** menu (`MainWindow.xaml:994`), between *Print…* and *Document Info…*:

```xml
<MenuItem x:Name="ESignatureMenuItem" Header="_E-Signature">
  <MenuItem x:Name="ESignSendMenuItem"     Header="_Send for Signature…"   Click="ESignSend_Click"/>
  <MenuItem x:Name="ESignSelfMenuItem"     Header="Sign _This Document…"   Click="ESignSelf_Click"/>
  <MenuItem x:Name="ESignRequestsMenuItem" Header="Signature _Requests…"   Click="ESignRequests_Click"/>
  <Separator/>
  <MenuItem x:Name="ESignVerifyMenuItem"   Header="_Verify Signatures…"    Click="ESignVerify_Click"/>
</MenuItem>
```

Each gets a `MenuItem.Icon` `TextBlock` with `Style="{StaticResource MenuGlyph}"` and a `Segoe MDL2 Assets` glyph, per the rest of the menu.

Enablement extends the existing hook — `FileMenu_SubmenuOpened` at `/Volumes/Data/repos/TDPdf/MainWindow.Files.cs:2113`, which today is a single line (`_removePasswordMenuItem.IsEnabled = _doc is not null && _ctx.WasProtected;`). The `x:Name`d items are re-fetched via `FindName(...)!` into `_camelCase` fields in the `// Manual element refs` block, per the repo convention.

```csharp
private void FileMenu_SubmenuOpened(object sender, RoutedEventArgs e)
{
    _removePasswordMenuItem.IsEnabled = _doc is not null && _ctx.WasProtected;

    // Absent, not disabled, when the feature does not exist on this device.
    bool configured = DocumensoConfig.HasEndpoint();
    _eSignatureMenuItem.Visibility = configured ? Visibility.Visible : Visibility.Collapsed;
    if (!configured) return;

    bool hasDoc = _doc is not null && _currentFile is not null;
    _eSignSendMenuItem.IsEnabled     = hasDoc;
    _eSignSelfMenuItem.IsEnabled     = hasDoc;
    _eSignVerifyMenuItem.IsEnabled   = hasDoc;
    _eSignRequestsMenuItem.IsEnabled = true;   // useful with no document open
}
```

**Degradation guarantees:**
- `HasEndpoint()` reads an env var and one registry key. No network, no file I/O, no `await`. Safe to call on every submenu open. It is the only gate.
- No endpoint ⇒ the submenu is `Collapsed`. Nothing to click, nothing to explain, nothing to error.
- Offline with an endpoint configured ⇒ the *only* user-visible result is an inline message inside the window the user deliberately opened, plus a status-bar line. Never `TdpDialog.Show` for a network failure, never a modal at startup, never a blocking call on the UI thread.
- No toolbar button in 2.0.0. The toolbar cannot express *Collapsed* without reflowing, and a permanently-hidden toolbar slot is a layout risk in a toolbar that has already cost this project time.

### 5.4 The four flows

**(a) Send for Signature** — `DocumensoSendWindow`, modal, owner = MainWindow.

1. Preconditions: document open; `_isDirty` ⇒ *"Save before sending?"* via existing `TdpDialog.ShowYesNo`. What is sent must be what is on disk.
2. Not connected ⇒ hand off to the Connect flow, then return here.
3. **Recipients**: an editable list — name, email, role (`SIGNER`/`APPROVER`/`VIEWER`/`CC`), with drag-reorder when *"Sign in order"* is ticked (`signingOrder`). A **[Me]** button fills the connected account. Client-side email validation.
4. **Fields**: two paths.
   - **Placement (preferred).** *"Place signature fields"* dismisses the dialog into a canvas placement mode: the user drags a rectangle per field and picks the recipient + type from a small popup. Existing canvas geometry converts to Documenso's percentage space — `pageX = x / pageWidth * 100`, `pageY = y / pageHeight * 100` (Documenso's origin is **top-left**, `page` is **1-indexed**; TDPdf's page coordinates are top-left too in the canvas overlay, but confirm against `Services/PdfPageGeometry.cs` at implementation time). The gesture is the same one the existing signature **stamp** already uses, so it is familiar and reuses `MainWindow.Canvas.cs` hit-testing.
   - **Placeholders (free).** If the PDF already contains `{{signature, r1}}` text, `POST /envelope/create` finds it and creates the fields itself — zero placement UI. Detect it with PdfPig (already used for text extraction) and show *"This document contains 2 signature placeholders — they will be used automatically."*
   - If neither, warn: *"No signature fields — recipients will be asked to place their own signature."* (That is valid Documenso behaviour, just worth saying.)
5. **[Send]** → `create` → `recipient/create-many` → `field/create-many` → `distribute`, wrapped in `using var op = Telemetry.StartOperation("DocumensoSend")`, all `await`ed off the UI thread with a progress line in the status bar and an Esc-to-cancel `CancellationToken` (the `Ocr.cs` pattern).
   Fields **cannot** be changed after distribute, so ordering matters and a partial failure must not leave a half-built envelope: on any failure after `create`, call `POST /envelope/delete` to clean up, and report *"Nothing was sent."*
6. Success: record `{envelopeId, title, recipients, sentAt}` in a local tracking list (§5.6), show *"Sent to 2 recipients for signature."*, and offer **[Copy signing link]** for any recipient.

**(b) Sign This Document (self-sign)** — the browser handoff.
Same as (a) with one recipient (the connected account, `SIGNER`) and one `SIGNATURE` field; after `distribute`, take `recipients[0].signingUrl` and `Process.Start(… UseShellExecute = true)`. A themed window stays open with *"Complete signing in your browser, then Refresh."* → **[Refresh]** → `GET /envelope/{id}` → on `COMPLETED`, offer **[Download signed copy…]**.
Be honest in the copy: *"Signing happens on the Documenso page in your browser."* Do not pretend TDPdf is signing.

**(c) Signature Requests** — `DocumensoRequestsWindow`.
`GET /envelope` + `POST /envelope/get-many` over the locally tracked ids. Columns: title, status (`DRAFT`/`PENDING`/`COMPLETED`/`REJECTED`), recipients with per-recipient `signingStatus`, sent date. Per-row actions: **Open in browser**, **Copy signing link**, **Download signed copy**, **Download certificate**, **Cancel**, **Delete** (hidden for `COMPLETED` — the API refuses). Manual **[Refresh]**, plus an optional 30 s `DispatcherTimer` that exists **only while the window is open** and is disposed on close. No background polling, ever. Since self-hosted rate limits are undocumented, the auto-refresh interval should be a constant that is easy to raise, and a 429 must back off rather than retry.

**(d) Verify Signatures** — see §7. Read-only, informational, server-backed.

### 5.5 The `ScrubDeadSignatures` hazard — must be handled in-phase

`/Volumes/Data/repos/TDPdf/MainWindow.Files.cs:1963` strips `/V` from every signature field and removes `/Perms` on **every** TDPdf save. The reasoning is sound (a full rewrite voids the digest) but the consequence for this feature is sharp: *download a completed, signed PDF → open it → press Ctrl+S → the signature is gone, silently.*

Required handling, all in `MainWindow.ESignature.cs` + a small hook in `.Files.cs`:
1. **Download to a chosen location, then open.** The downloaded file goes through `SaveFileDialog` (default name `{title} (signed).pdf`) and is then opened in a new tab. Write via `.part` + `File.Move` (the `Ocr.cs:272` pattern) — and note the existing warning at `/Volumes/Data/repos/TDPdf/Services/PdfDocumentService.cs:59` that **cloud-sync folders refuse `File.Replace`**; a user saving a signed PDF into OneDrive is the common case, so use `File.Move` with an explicit delete-then-move fallback rather than `File.Replace`.
2. **Flag the context.** Add a `bool HasDigitalSignature` to `DocumentContext`, set on open when the catalog has an `/AcroForm` field with `/FT /Sig` and a non-null `/V` (cheap PdfSharpCore dictionary read — no cryptography).
3. **Warn once before a save that would scrub.** In `SaveInPlace`/`SaveAs`/`SaveFlattened`, when `HasDigitalSignature`, show a themed confirm: *"This document carries a digital signature. Saving rewrites the file and will remove it. The signed original is unaffected if you Save As to a new name."* → Save anyway / Cancel. Reuse `TdpDialog.ShowWithCheckbox` (`/Volumes/Data/repos/TDPdf/TdpDialog.cs:401`) for a *don't ask again* that writes a new `Settings` flag.
4. **Show it in the UI.** A small "Signed" indicator in the status bar / tab, and a row in `Document Info` (F12).

This is genuinely valuable **independent of Documenso** — it fixes a real silent-data-loss edge any signed PDF hits today — which is why it is its own phase.

### 5.6 Local tracking store

A small JSON file `%LOCALAPPDATA%\TDPdf\documenso-requests.json`: `[{ envelopeId, title, sentUtc, recipientCount }]`, capped at ~200 entries, used only to make *Signature Requests* show *this device's* sends first and to enable the cheap `get-many` refresh. Contains **no** credential and **no** recipient PII (names/emails come from the API at display time, not from disk). Corrupt or missing ⇒ fall back to `GET /envelope`. Same "plain JSON, tolerate anything" discipline as `signatures.json`.

---

## 6. Phased build plan

Ordered so something reviewable and independently valuable lands first. Each phase is one commit on one branch; per the repo's git-flow rule this is **one PR**, pushed once at the end.

### Phase 0 — Config + credential + gating *(no network, no instance, no certificate)*
`Services/DocumensoConfig.cs`, `Services/DocumensoCredentialStore.cs`, `Services/DocumensoConnectWindow.cs`, the Settings section, the four `Settings.cs` properties, the collapsed-by-default `File ▸ E-Signature` submenu with all four handlers stubbed to no-ops, the `Sanitizer` token-redaction line, and **the `PRIVACY.md` section** (§3.5) — written up front, so the disclosure is reviewed alongside the mechanism rather than bolted on at the end.

**Testable now, fully:** set `TDPDF_DOCUMENSO_BASEURL` (or the HKLM key) → submenu appears; clear it → submenu is gone. `AllowUserOverride=0` → the Settings textbox is read-only. Paste a fake token → it round-trips through DPAPI; corrupt the file → clean fall-through to *Not connected*; delete `%LOCALAPPDATA%` → *Not connected*. Grep the process for the token string; confirm no telemetry property carries it. **This is the security-review phase and it needs nothing external.**

### Phase 1 — The client *(needs an instance; no certificate)*
`Services/DocumensoClient.cs` + `DocumensoModels.cs` + **Test connection** in Settings (`/api/health`, `/api/certificate-status`, `GET /api/v2/envelope?perPage=1`).

**Testable against a local `docker compose` Documenso** (see §8, Q1) with a throwaway self-signed `.p12`. No SSL.com certificate needed. Verifies auth header format, the `api_` bare-token quirk, envelope listing, and the error mapping.

### Phase 2 — Send for Signature *(needs an instance; no certificate)*
`DocumensoSendWindow`, recipients, the placeholder-detection path, the create→recipients→fields→distribute pipeline with rollback, `Copy signing link`.

**Testable end-to-end locally.** With a self-signed cert the completed PDF is genuinely sealed — Adobe shows *"signature validity is unknown"* rather than a green check. **That proves the entire pipeline.** Only the trust indicator is blocked on the certificate.

Ship field **placement** as Phase 2b so 2a can be reviewed on its own; the placeholder path makes 2a independently useful.

### Phase 3 — Status + download + the scrub guard *(needs an instance; no certificate)*
`DocumensoRequestsWindow`, the download-and-open flow, the tracking store, and **all of §5.5**. Phase 3 is the one phase that also improves TDPdf for users who never touch Documenso.

### Phase 4 — Self-sign *(needs an instance; no certificate)*
The one-recipient + browser-handoff flow and the Refresh-then-download loop. Thin on top of Phases 2 and 3.

### Phase 5 — Verify (informational, server-backed) *(needs an instance; no certificate)*
`Document Info` gains a *Digital signatures* section listing, per signature: presence, signer CN/email read from the CMS signer certificate, claimed signing time, whether `/ByteRange` covers the whole file, and whether unsigned incremental updates follow. Plus **[Download certificate of completion]** and **[Download audit log]** for envelopes we can match, and **[Open in Adobe Reader for full validation]**.

Every string is framed as *observed*, never as *valid*. **No green check, no "Signature valid", no trust decision from TDPdf's own code in 2.0.0.**

### What the SSL.com certificate actually blocks
**Nothing in TDPdf's codebase.** It blocks exactly one observable outcome: *Adobe rendering a green check instead of a yellow warning.* Every phase above is buildable, reviewable and testable today with a self-signed `.p12`. When the real cert arrives it is a Documenso-side env-var swap (`NEXT_PRIVATE_SIGNING_LOCAL_FILE_CONTENTS` + `NEXT_PRIVATE_SIGNING_PASSPHRASE`) and a container restart — no TDPdf change, no release.

### What a missing instance blocks
Phases 1–5 need *an* instance, not *the* instance. A local Docker Documenso (Postgres + app + a self-signed p12) is the development target and should be stood up before Phase 1 starts. Design for the endpoint being pure configuration and `sign.thedoodleproject.com` never needs to exist for the code to be finished and reviewed.

---

## 7. What I recommend NOT building

### 7.1 Local PAdES validation — the big one
**Do not build a PAdES verifier in 2.0.0.** §1.5 lists the eight things a defensible one needs. Reasons, in order of weight:

1. **Failure mode.** The output of a verifier is a trust assertion. A subtly wrong one ("valid" on a document with an unsigned incremental update, or a chain validated at *now* instead of at signing time) is materially worse than showing nothing, and it would ship in a GPLv3 app other people run.
2. **No existing foothold.** This is not "add a library call" — it is the app's first line of cryptographic code (the only crypto in the whole app today is one `SHA256.HashData` call), and it lands in an app with **no test suite**. `SignedCms` and `Rfc3161TimestampToken` are in-box, but they are maybe 20% of the work; the PDF-structural and policy parts are the other 80% and are where the bugs live. Worse, step 1 needs *incremental-update-aware* parsing, and PdfSharpCore does not expose revision boundaries — so it means **patching the vendored copy** under `third_party/PdfSharpCore/` (an established practice here, per `VENDORED.txt`, but one that grows the GPLv3 source bundle and the fork-sync burden).
3. **Licensing.** The libraries that already do this properly are iText 7 and DSS — **AGPL** (or commercial). Taking an AGPL dependency into a single-file GPLv3 binary is a licensing conversation, not a coding task, and it would rewrite `THIRD-PARTY-NOTICES.md` and the source-bundle story.
4. **Low marginal value.** Anyone who *needs* validation opens the PDF in Acrobat, which does all of the above and is the authority the counterparty will cite anyway. And Derek's own instance can enable `NEXT_PRIVATE_SIGNING_TIMESTAMP_AUTHORITY` for real LTV — which improves validation in every reader at once, for the cost of one env var, versus weeks of verifier work that improves it in exactly one.

**Instead** (Phase 5): the server-backed evidence trail (certificate PDF + audit log, both first-class API routes) plus a strictly *informational* local read. Revisit a real verifier in a later release, as its own project, with a test corpus — not inside a feature PR.

### 7.2 Webhook consumption
Already argued in §1.4. Concretely: **do not** build a local HTTP listener, a tunnel, or a relay. Polling on demand is correct for a human-triggered status check.

### 7.3 A background poller / notifications
No tray notification, no poll at startup, no *"your document was signed!"* toast. Cost: a permanent background network callout from an offline-capable editor, a new failure surface, and battery. The *Signature Requests* window with Refresh covers the need. Revisit only if asked.

### 7.4 Programmatic signing via `field.sign`
Do not call the undocumented tRPC route (§1.6). Unversioned and unannounced. Browser handoff.

### 7.5 A generated SDK / a new HTTP stack
No `@documenso` SDK exists for .NET; do not add Refit, NSwag, Polly, `IHttpClientFactory`, or a shared `SocketsHttpHandler`. Nine endpoints hand-rolled on the existing `new HttpClient { Timeout = … }` pattern is less code than the wiring for any of those, and adds nothing to the notices file or the single-file publish.

### 7.6 The rest of the Documenso surface
Templates, folders, attachments, bulk operations, embedded authoring (an Enterprise/Platform add-on), direct-link templates, and team management. Real features, no demand in a desktop PDF editor, and each is a window.

### 7.7 Pushing the API token via Intune
§3.1. It is a credential-sharing defect, not a convenience.

### 7.8 A toolbar button
§5.3. The toolbar cannot express *Collapsed* cleanly and this project has already lost time to toolbar layout.

---

## 8. Open questions for Derek

Guessing on any of these wastes real work.

**Q1 — Which Documenso version, and is there a dev instance?**
v2's envelope API needs Documenso **≥ 2.0.0** (released 10 Nov 2025). If the instance will be older, the whole client must target the deprecated v1 `/api/v1/documents` surface instead, which is a different design. *Please also confirm whether I should stand up a local `docker compose` Documenso as the Phase-1 development target, or wait for `sign.thedoodleproject.com`.* My recommendation: local Docker, immediately — it unblocks Phases 1–5 with zero dependency on the deployment.

**Q2 — Whose Documenso account signs, and who pays for the token's blast radius?**
A Documenso API token is **unscoped and full-account**. Options: (a) each user creates their own token from their own account — correct isolation, but everyone needs a Documenso login; (b) one shared service account whose token is distributed — then every device can read and delete that account's documents and impersonate it, which I would not ship. The design assumes **(a)**. Confirm, because (b) would need a different (and worse) design.

**Q3 — Self-hosted token-creation URL.**
The connect dialog deep-links the browser to Documenso's token page. Docs describe the path as *Settings → API Tokens* but do not pin the URL for a self-hosted, single-team instance (`/settings/tokens` vs `/t/{team}/settings/api-tokens`). Give me the working URL from the deployed instance, or I fall back to opening `{BaseUrl}` and telling the user where to click.

**Q4 — Is local PAdES validation in or out for 2.0.0?**
§7.1 recommends **out**, with the server-backed evidence trail instead. This is the single biggest scope decision in the brief and I want it explicit before Phase 5 is briefed. If it is *in*, it should be its own release and its own project with a test corpus.

**Q5 — Enable RFC 3161 timestamping on the instance?**
`NEXT_PRIVATE_SIGNING_TIMESTAMP_AUTHORITY` gives LTV in every reader for the cost of one env var, and it changes the cost/benefit of Q4. Which TSA — SSL.com's own, or a public one?

**Q6 — Fleet rollout: is `BaseUrl` going out as an Intune configuration profile?**
If yes I will note the exact OMA-URI / profile shape in the PR description so the policy push and the release land together. If it is user-entered only, tier 2 is dead code in practice and the Settings textbox has to be the primary path (it is built either way; this only changes the emphasis in the copy).

**Q7 — Field placement in Phase 2a, or placeholders only?**
Drag-a-rectangle placement (§5.4a) is the better UX and reuses `MainWindow.Canvas.cs`, but it is the largest single chunk of UI in this feature. The `{{signature, r1}}` placeholder path is nearly free. Ship placeholders first and placement as 2b, or hold 2 until placement is done?

**Q8 — Rename `Tools ▸ Signature` to `Signature Stamp`?**
§4. UI strings only, no code or file renames. It is the right call for usability but it changes a menu label users know, so it is your call, not mine.

**Q9 — Cancel/Delete in the Requests window?**
`POST /envelope/cancel` and `/envelope/delete` are destructive and irreversible and touch server-side state other people may be mid-signature on. Include them in 2.0.0 (behind a confirm), or leave destructive actions to the web UI?

**Q10 — Re-introducing DPAPI: confirm the distinction holds.**
1.24.0.0 deliberately deleted DPAPI from this codebase, and `App.xaml.cs` still cleans up after it. §2.2 argues the two cases are opposites — a *fleet-provisioned app secret hidden from its owner* (rejected, correctly) versus *the user's own credential held on their behalf* (proposed). I believe that distinction is sound and that no other mechanism is better here. But it reverses a decision you made on purpose, so it should be your call, not a design-doc footnote. If you would rather no credential be stored at all, the fallback is **prompt for the token once per session, hold it in memory only** — noticeably worse UX, materially smaller attack surface, and roughly the same amount of code.

---

## 9. Compliance checklist for the implementation brief

- [ ] No compiled-in endpoint. `DocumensoConfig` is the only resolver; `HasEndpoint()` the only gate. `Enabled = 0` policy kill switch honoured ahead of everything.
- [ ] **`PRIVACY.md` updated** in the same PR, stating that whole documents and recipient email addresses leave the device when a service is configured (§3.5). This repo's stated rule: *do not add one without the other.*
- [ ] New settings tolerate `Settings.Default.Reload()` back to their `DefaultSettingValue` (`App.xaml.cs` `EnsureSettingsHealthy`).
- [ ] No new identifier contains `Signature` — prefix `ESign`/`Documenso` (§4).
- [ ] Downloads use `.part` + `File.Move`, never `File.Replace` (cloud-sync folders reject it — `Services/PdfDocumentService.cs:59`).
- [ ] Token never in `user.config`. DPAPI `CurrentUser`, `%LOCALAPPDATA%\TDPdf\documenso.cred`, endpoint-bound entropy.
- [ ] Token never logged, never telemetered, never in a URL, never in an exception. Per-request `TryAddWithoutValidation`. `Sanitizer` redaction as the second line.
- [ ] Degrades silently and completely: submenu `Collapsed` with no endpoint; no error dialog, no hang, no startup network call.
- [ ] No `MessageBox.Show`, no default dialog chrome. Code-built `Window` using the `OpenSignatureCreator` custom-chrome recipe (`MainWindow.Signatures.cs:365`); brushes via `BrushResource(...)` inside `MainWindow` partials or the `TryFindResource`-with-`SystemColors`-fallback helper (`TdpDialog.cs:39`) in `Services/` classes. No hardcoded hex.
- [ ] If a toolbar button is ever added: matching hidden label `TextBlock` in `ToolbarA11yLabels` (`MainWindow.xaml:1232-1246`) plus `AutomationProperties.Name`/`HelpText`/`AccessKey`/`LabeledBy` and a `TabIndex`. (This design says **no** toolbar button — §5.3.)
- [ ] `SaveFileDialog` is the one exception already used repo-wide for file picking (`MainWindow.Files.cs:2123`) — keep consistent, don't introduce new chrome.
- [ ] No new HTTP stack, no DI container, no new JSON library. `new HttpClient { Timeout = … }` + `System.Text.Json`.
- [ ] `ViewModels/MainWindowViewModel.cs` untouched (issue #18).
- [ ] Nullable clean; build stays at **4 warnings / 0 errors**, checked with `--no-incremental`.
- [ ] `_isDirty` respected; save paths routed through the existing dirty-check prompts; the new signed-document guard added to all three save paths.
- [ ] New `x:Name`d XAML elements re-fetched via `FindName(...)!` into `_camelCase` fields in the `// Manual element refs` block.
- [ ] New glyphs from `Segoe MDL2 Assets` with `Style="{StaticResource MenuGlyph}"`.
- [ ] All product strings say "TDPdf"; GPLv3 headers and `NOTICE`/`LICENSE` untouched.
- [ ] If `System.Security.Cryptography.ProtectedData` is added as a `PackageReference`, **regenerate `THIRD-PARTY-NOTICES.md`**.
- [ ] Release checklist if this ships a version bump: `TDPdf.csproj` (×3 version fields), `CHANGELOG.md` (+ compare links), `build/intune/Detect-TDPdf.ps1` `$MinVersion`, `.github/release-notes/v<x.y.z.w>.md` — **all in the same PR**.
- [ ] One branch, one PR, commit per phase, push once.

---

## Sources

- [Authentication | Documenso Docs](https://docs.documenso.com/docs/developers/getting-started/authentication)
- [API Authentication | Documenso Docs](https://docs.documenso.com/developers/public-api/authentication)
- [Documents API | Documenso Docs](https://docs.documenso.com/docs/developers/api/documents)
- [Recipients API | Documenso Docs](https://docs.documenso.com/docs/developers/api/recipients)
- [Fields API | Documenso Docs](https://docs.documenso.com/docs/developers/api/fields)
- [Webhooks | Documenso Docs](https://docs.documenso.com/docs/developers/webhooks)
- [Embedding | Documenso Docs](https://docs.documenso.com/docs/developers/embedding)
- [Direct Links | Documenso Docs](https://docs.documenso.com/users/direct-links)
- [Signing Certificate | Documenso Docs](https://docs.documenso.com/docs/developers/local-development/signing-certificate)
- [Timestamp Server | Documenso Docs](https://docs.documenso.com/docs/self-hosting/configuration/signing-certificate/timestamp-server)
- [Docker deployment | Documenso Docs](https://docs.documenso.com/docs/self-hosting/deployment/docker)
- [Tips & Common Pitfalls | Documenso Docs](https://docs.documenso.com/docs/self-hosting/getting-started/tips)
- [Documenso v2 API reference (OpenAPI)](https://openapi.documenso.com/)
- [Documenso changelog](https://documenso.com/changelog)
- [Building Documenso SDK and API v2](https://documenso.com/blog/building-sdk-and-api-v2)
- [Building the Documenso Public API — The Why and How](https://documenso.com/blog/public-api)
- Source: [`envelope-router/router.ts`](https://raw.githubusercontent.com/documenso/documenso/main/packages/trpc/server/envelope-router/router.ts) · [`create-envelope.types.ts`](https://raw.githubusercontent.com/documenso/documenso/main/packages/trpc/server/envelope-router/create-envelope.types.ts) · [`distribute-envelope.types.ts`](https://raw.githubusercontent.com/documenso/documenso/main/packages/trpc/server/envelope-router/distribute-envelope.types.ts) · [`download-envelope-item.types.ts`](https://raw.githubusercontent.com/documenso/documenso/main/packages/trpc/server/envelope-router/download-envelope-item.types.ts) · [`download-envelope-certificate-pdf.types.ts`](https://raw.githubusercontent.com/documenso/documenso/main/packages/trpc/server/envelope-router/download-envelope-certificate-pdf.types.ts) · [`sign-envelope-field.ts`](https://raw.githubusercontent.com/documenso/documenso/main/packages/trpc/server/envelope-router/sign-envelope-field.ts) · [`SIGNING.md`](https://github.com/documenso/documenso/blob/main/SIGNING.md)
- [documenso#2098 — scoped API keys](https://github.com/documenso/documenso/issues/2098) · [documenso#1764 — verify signature / download audit log](https://github.com/documenso/documenso/issues/1764)
- [.NET `SignedCms`](https://learn.microsoft.com/dotnet/api/system.security.cryptography.pkcs.signedcms) · [`SignedCms(ContentInfo, bool)` detached](https://learn.microsoft.com/dotnet/api/system.security.cryptography.pkcs.signedcms.-ctor)
