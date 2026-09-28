# Telemetry

TDPdf can report usage and crash information to **an OpenTelemetry collector you
choose**. This page is about how to point it at one of yours.

If you never do this, TDPdf sends nothing. That is not a promise about intent — there
is nowhere for it to send anything to. Read on for why that is structural rather than
a setting.

For the exhaustive list of every field and event name, see [privacy](/privacy/). This
page is the operator's view: how to turn it on, and what you get when you do.

## Nothing is compiled in

There is **no destination inside the binary**. Not an endpoint, not a key, not an
obfuscated one.

An earlier version had one — an Azure Application Insights key embedded at build time.
It was removed in **1.24.0.0**, along with the DPAPI provisioning file that could also
supply a destination, and it is not coming back. Embedding a secret in a binary handed
to end-user laptops obfuscates it at best, and rotating it meant shipping a whole new
signed release.

What replaced it is a runtime lookup. Reporting requires **two independent things**,
and either alone sends nothing:

| | What it is | Default |
|---|---|---|
| **Consent** | A per-user setting: Settings → Privacy → *Send anonymous usage and crash reports*. | On |
| **Destination** | An OTLP endpoint and a token. | **Absent** |

Consent defaults to on, and that is safe precisely because it is useless by itself.
A build from a public checkout of this repository has no destination, so the consent
setting has nothing to act on whichever way it is set. The Settings dialog tells you
which of the two states you are in rather than leaving you to infer it.

## Pointing TDPdf at your collector

A destination is **an endpoint and a token together**. Supplying one without the other
is treated as no destination at all — an endpoint with no token would produce a stream
of 401s from every machine, which is worse than staying quiet.

There are two ways to supply them, and the first one that yields *both* values wins.

### 1. Environment variables — for one machine

```
TDPDF_OTLP_ENDPOINT   https://otlp.example.com
TDPDF_OTLP_TOKEN      <your collector's bearer token>
```

This is the route for a developer, or for anyone self-hosting who wants their own
build reporting to their own collector without administrative rights.

These are deliberately **not** registry values under `HKCU`. A user-writable production
path is a redirection surface, and an environment variable is obviously session-scoped
to anyone reading it.

Optionally, `TDPDF_DEPLOYMENT_ENVIRONMENT` overrides the `deployment.environment`
resource attribute. A Release build reports `production` and a Debug build reports
`development`, which is what keeps a contributor's local runs out of a production
stream if they point one at a collector.

### 2. Administrator-pushed policy — for a fleet

```
HKEY_LOCAL_MACHINE\SOFTWARE\Policies\TDPdf\Telemetry
    OtlpEndpoint  (REG_SZ)  https://otlp.example.com
    OtlpToken     (REG_SZ)  <your collector's bearer token>
```

`HKLM`, under `SOFTWARE\Policies\`, for two reasons that are both load-bearing.

It is the Windows convention for administrator-pushed policy, so an admin reading the
hive can tell at a glance this is managed configuration rather than something the
application wrote about itself.

More concretely: uninstalling TDPdf deletes `Software\TDPdf` from both hives. Putting
the destination under `SOFTWARE\TDPdf\` would mean an uninstall silently destroyed
your pushed configuration along with the app's own keys. The policy profile would
eventually re-apply and reporting would be dark until it did, with nothing to say why.
Policy is not the application's to delete.

The practical payoff of the policy route is that **rotating the token is a policy push,
not a signed release of the whole application.**

Provisioning this through Intune is documented in
[`docs/intune-distribution.md`]({{REPO_URL}}/blob/main/docs/intune-distribution.md).

## What your collector needs to accept

TDPdf exports over **OTLP/HTTP with protobuf encoding**, not gRPC. The reference
deployment reaches its collector through a Cloudflare Tunnel, which carries HTTP only;
with the exporter's default gRPC protocol it fails silently and simply never arrives.

Given `OtlpEndpoint` = `https://otlp.example.com`, TDPdf posts to:

| Signal | Path |
|---|---|
| Logs (events and crashes) | `https://otlp.example.com/v1/logs` |
| Traces (operation timings) | `https://otlp.example.com/v1/traces` |

A trailing slash on the configured endpoint is trimmed, so both spellings work.

Every request carries `Authorization: Bearer <OtlpToken>`. Any collector that accepts
a static bearer token will do — the reference deployment is a self-hosted
[SigNoz](https://signoz.io/), but nothing in TDPdf is SigNoz-specific.

### Resource attributes

These identify the stream and are constant for the life of the process:

| Attribute | Value |
|---|---|
| `service.name` | `tdpdf` — fixed, and never changes |
| `service.version` | The application version |
| `service.namespace` | `stfoa` |
| `deployment.environment` and `deployment.environment.name` | `production`, `development`, or your override. Both spellings are set on purpose — one is the current OpenTelemetry semantic convention, the other is what some tools' environment filters read. |
| `session.id` | A random identifier generated fresh at every launch, never written to disk, never reused |
| `host.name` | The machine name |

If a machine goes quiet and you cannot tell whether it stopped crashing or stopped
running, `session.id` is the attribute that tells the two apart — and it is why a
hundred crashes from one unlucky laptop can be distinguished from a hundred crashes
across a fleet, which need very different responses.

## What actually arrives

**Event names.** `App.Startup`, `App.Heartbeat` (every 15 minutes, saying only that it
is still running and for how long), `App.SessionEnd`, `File.Open`, `File.Merge`,
`File.Print`, `Tool.Selected`, `Install.Success`, the `Update.*` family, and the rest —
[privacy](/privacy/) lists them all. Operation timings arrive as `Op.*`.

The absence of `App.SessionEnd` after a `App.Startup` is how crash rate is measured,
without anything having to track a user.

**Technical context.** Application version, Windows version, 64-bit or not, .NET runtime
version, processor count, and whether the install is per-user or machine-wide.

**Crash reports.** Exception type, message, stack trace, and a derived grouping key —
a short hash of the exception type and first stack frame, so crashes bin together
without the message text being the key.

## What does not arrive, and why it can't

No document contents. No file names, paths, or folder names. No text you typed, no form
field values, no search terms. No signature data. No username or account identifier. No
persistent user or device identifier beyond the machine name.

Two mechanisms rather than one policy:

- **Events carry no document identity to begin with.** `File.Open` records that an open
  happened, how long it took and whether it succeeded — not which file. `Tool.Selected`
  records which tool you chose, never what you did with it. The annotation events record
  that placement started and how it ended, never the text.
- **Everything else goes through `Diagnostics/Sanitizer.cs`**, which scrubs UNC and
  drive-rooted Windows paths, POSIX paths, stack-trace `in /…:line N` frames, and
  anything shaped like `password=` or `passphrase=` before it leaves the process.
  Exception text routinely contains the path of the document being worked on, so this
  is not theoretical.

The sanitiser is described in its own source as a pragmatic last line of defence rather
than a security boundary, and that is the honest framing. The real guarantee is the
first mechanism: the fields are not collected. **TDPdf deliberately exposes no way to
report a raw, unscrubbed exception** — there is no `TrackException(Exception)` overload
for a future contributor to reach for by accident.

### The machine name is the one identifying field

Reports carry `host.name` — for example `L-JSMITH`. It is there because it is what
makes a report actionable: it separates one machine with a fault from a fleet-wide
regression.

On a corporate machine that name is often derived from the user's name, so treat it as
identifying the device and, indirectly, its user. This is the only identifying field in
the payload, and it is the one to weigh when deciding whether to enable reporting.

## Local queues

A report raised without a network connection is not thrown away. Two small queues live
under the user's own profile and hold only the sanitised data above:

| Location | Contents | Lifetime |
|---|---|---|
| `%LOCALAPPDATA%\TDPdf\telemetry-spool` | Batches whose upload failed | Deleted once sent |
| `%LOCALAPPDATA%\TDPdf\pending-crashes` | Crash records the app died before it could send | Deleted on replay; dropped unsent after 14 days, capped at 50 |

The second exists because a crash that kills the process takes its in-memory report with
it. Both are safe to delete by hand; you lose unsent reports and nothing else. With
reporting off, nothing new is written to either.

## Turning it off

- **One user, this machine** — Settings → Privacy, untick *Send anonymous usage and
  crash reports*. Effective immediately, and persists.
- **The whole device, permanently** — `TDPdf.exe /clear-telemetry`. This writes an
  opt-out marker that is checked **before any destination is read at all**, so it
  outranks both the environment variables and administrator-pushed policy, and it
  survives reinstalls.

That ordering is deliberate. A device-level opt-out that policy could override would not
be an opt-out.

## Updates are separate

The twice-daily check against the public GitHub releases listing happens regardless of
your reporting setting, and is the only thing TDPdf does that reaches the network
unconditionally. It carries no identifier and not even your version number — the
comparison runs on your machine against a public page.

An administrator can disable it for an organisation by setting `Enabled` to `0` under
`HKEY_LOCAL_MACHINE\SOFTWARE\Policies\TDPdf\Update`.

## If this page and the code disagree

The code wins, and this page is the bug. Everything here is drawn from
`Diagnostics/TelemetryConfig.cs`, `Diagnostics/OtlpTelemetry.cs`,
`Diagnostics/Telemetry.cs` and `Diagnostics/Sanitizer.cs`, which you can read
yourself — [the source is published]({{REPO_URL}}/tree/main/Diagnostics) and the
corresponding source for every released binary ships beside it.
