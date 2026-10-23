# Telemetry

TDPdf can report usage and crash information to **an OpenTelemetry collector you choose**. This
page explains how to point it at yours.

If you don't, TDPdf sends nothing. No destination is built into it, so there is nowhere for
reports to go.

The [privacy](/privacy/) page lists every field and event name. This page covers the setup: how to
turn reporting on, and what your collector receives.

## No built-in destination

The binary contains **no destination**: no endpoint and no key, obfuscated or otherwise.

A key embedded in software installed on many laptops can be extracted, and changing it would mean
shipping a new signed release. So TDPdf looks the destination up when it runs, and reporting
needs **two separate things**. Either one alone sends nothing:

| | What it is | Default |
|---|---|---|
| **Consent** | A per-user setting: Settings → Privacy → *Send anonymous usage and crash reports*. | On |
| **Destination** | An OTLP endpoint and a token. | **Absent** |

Consent is on by default, but it does nothing without a destination. A build from a public
checkout of this repository has no destination, so the consent setting makes no difference either
way. The Settings dialog shows which state you are in.

## Pointing TDPdf at your collector

A destination is **an endpoint and a token together**. If only one is set, TDPdf treats it as no
destination, so an endpoint without a token doesn't produce a stream of 401 errors from every
machine.

There are two ways to set them. TDPdf uses the first source that supplies both values.

### 1. Environment variables, for one machine

```
TDPDF_OTLP_ENDPOINT   https://otlp.example.com
TDPDF_OTLP_TOKEN      <your collector's bearer token>
```

Use these as a developer, or to send your own build's reports to your own collector without admin
rights.

They are environment variables, not registry values under `HKCU`. Any user can write to `HKCU`, so
a production setting there could be used to redirect reports. An environment variable is clearly
limited to the session that sets it.

`TDPDF_DEPLOYMENT_ENVIRONMENT` (optional) overrides the `deployment.environment` resource
attribute. A Release build reports `production` and a Debug build reports `development`, so a
contributor's local runs stay out of the production data if they point one at a collector.

### 2. Administrator policy, for a fleet

```
HKEY_LOCAL_MACHINE\SOFTWARE\Policies\TDPdf\Telemetry
    OtlpEndpoint  (REG_SZ)  https://otlp.example.com
    OtlpToken     (REG_SZ)  <your collector's bearer token>
```

These live in `HKLM` under `SOFTWARE\Policies\` for two reasons.

That is where Windows expects administrator policy, so an admin looking at the registry can see
it is managed configuration and not something TDPdf wrote for itself.

Also, uninstalling TDPdf deletes `Software\TDPdf` from both hives. If the destination lived under
`SOFTWARE\TDPdf\`, an uninstall would delete your pushed configuration along with TDPdf's own
keys. Reporting would then stop, with no explanation, until the policy was applied again.
Uninstalling TDPdf leaves `SOFTWARE\Policies\` alone.

With this route, **rotating the token is a policy push.** You don't need a new release of TDPdf.

Setting this up through Intune is covered in
[`docs/intune-distribution.md`]({{REPO_URL}}/blob/main/docs/intune-distribution.md).

## What your collector needs to accept

TDPdf exports over **OTLP/HTTP with protobuf encoding**. It does not use gRPC. The reference
deployment reaches its collector through a Cloudflare Tunnel, which carries only HTTP; with the
exporter's default gRPC protocol, nothing would arrive and no error would show.

With `OtlpEndpoint` set to `https://otlp.example.com`, TDPdf posts to:

| Signal | Path |
|---|---|
| Logs (events and crashes) | `https://otlp.example.com/v1/logs` |
| Traces (operation timings) | `https://otlp.example.com/v1/traces` |

A trailing slash on the endpoint is removed, so either form works.

Every request carries `Authorization: Bearer <OtlpToken>`. Any collector that accepts a static
bearer token works. The reference deployment uses a self-hosted [SigNoz](https://signoz.io/), but
nothing in TDPdf depends on SigNoz.

### Resource attributes

These identify the stream and stay the same for the life of the process:

| Attribute | Value |
|---|---|
| `service.name` | `tdpdf` (fixed) |
| `service.version` | The application version |
| `service.namespace` | `stfoa` |
| `deployment.environment` and `deployment.environment.name` | `production`, `development`, or your override. Both names are set because the second is the current OpenTelemetry semantic convention and some tools' environment filters read the first. |
| `session.id` | A random identifier created at every launch, never written to disk, never reused |
| `host.name` | The machine name |

If a machine goes quiet, `session.id` tells you whether it stopped crashing or stopped running. It
also lets you tell a hundred crashes on one laptop from a hundred crashes across the fleet.

## What arrives

**Event names.** `App.Startup`, `App.Heartbeat` (every 15 minutes, with only how long TDPdf has
been running), `App.SessionEnd`, `File.Open`, `File.Merge`, `File.Print`, `Tool.Selected`,
`Install.Success`, the `Update.*` events, and others. [Privacy](/privacy/) lists them all.
Operation timings arrive as `Op.*`.

An `App.Startup` with no matching `App.SessionEnd` is counted as a crash. That is how the crash
rate is measured without tracking anyone.

**Technical context.** Application version, Windows version, 64-bit or not, .NET runtime version,
processor count, and whether the install is per-user or machine-wide.

**Crash reports.** Exception type, message, stack trace, and a grouping key. The key is a short
hash of the exception type and the first stack frame, so similar crashes group together without
using the message text.

## What doesn't arrive

No document contents. No file names, paths or folder names. No text you typed, form field values
or search terms. No signature data. No username or account identifier. No persistent user or device
identifier apart from the machine name.

Two things keep it that way:

- **Events don't include document details.** `File.Open` records that a file was opened, how long
  it took and whether it succeeded. It doesn't record which file. `Tool.Selected` records which
  tool you chose but not what you did with it. The annotation events record that placement
  started and how it ended, but not the text.
- **Everything else passes through `Diagnostics/Sanitizer.cs`**, which removes UNC and
  drive-letter Windows paths, POSIX paths, stack-trace `in /…:line N` frames, and anything shaped
  like `password=` or `passphrase=` before it leaves the process. Exception text often contains
  the path of the open document, so this matters.

The sanitiser's own source describes it as a last line of defence, not a security boundary. The
main protection is the first point: those fields are never collected. **TDPdf has no way to report
an exception without sanitising it.** There is no `TrackException(Exception)` overload for a
contributor to call by mistake.

### The machine name is the one identifying field

Reports carry `host.name`, for example `L-JSMITH`. It shows whether a problem is on one machine or
across the fleet.

On a work computer that name is often based on the user's name, so treat it as identifying the
device and, indirectly, its user. It is the only identifying field in a report, and the one to
consider when deciding whether to enable reporting.

## Local queues

A report raised while offline isn't discarded. Two small queues in the user's own profile hold it,
containing only the sanitised data above:

| Location | Contents | Lifetime |
|---|---|---|
| `%LOCALAPPDATA%\TDPdf\telemetry-spool` | Batches whose upload failed | Deleted once sent |
| `%LOCALAPPDATA%\TDPdf\pending-crashes` | Crash records the app died before it could send | Deleted on replay; dropped unsent after 14 days, capped at 50 |

The second queue exists because a crash that ends the process also loses any report still in
memory. You can delete either folder by hand; you lose unsent reports and nothing else. With
reporting off, nothing new is written to either.

## Turning it off

- **One user on this computer:** Settings → Privacy, untick *Send anonymous usage and crash
  reports*. This takes effect immediately and stays off.
- **The whole device, permanently:** `TDPdf.exe /clear-telemetry`. This writes an opt-out marker
  that TDPdf checks **before it reads any destination**. It overrides both the environment
  variables and administrator policy, and it survives reinstalls.

Policy can't override the device opt-out.

## Update checks are separate

The twice-daily check against the public GitHub releases listing runs whatever your reporting
setting is. It is the only network request TDPdf always makes. It carries no identifier and not
even your version number; the comparison happens on your computer against a public page.

An administrator can disable it for an organisation by setting `Enabled` to `0` under
`HKEY_LOCAL_MACHINE\SOFTWARE\Policies\TDPdf\Update`.

## Source

This page is based on `Diagnostics/TelemetryConfig.cs`, `Diagnostics/OtlpTelemetry.cs`,
`Diagnostics/Telemetry.cs` and `Diagnostics/Sanitizer.cs`. If the page and the source disagree, the
source describes what TDPdf does. [The source is published]({{REPO_URL}}/tree/main/Diagnostics),
and each release ships with the source for that exact binary.
