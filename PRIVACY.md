# Privacy

TDPdf can report anonymous usage and crash information. This document describes exactly what
that means. It is accurate against the source in `Diagnostics/`, and it is the source — not this
document — that decides behaviour, so if the two ever disagree, the code wins and this file is a
bug.

## The short version

- **Your documents never leave your machine.** TDPdf sends no PDF content, no file names and no
  file paths. Nothing you open, type, sign or annotate is transmitted.
- **A build with no reporting destination configured sends nothing at all**, to anyone. That is
  the state of any build compiled from a public checkout of this repository.
- **You can turn reporting off** in **Settings → Privacy → "Send anonymous usage and crash
  reports"**, or device-wide with `TDPdf.exe /clear-telemetry`.

## Two independent switches

Reporting requires *both* of the following. Either one alone sends nothing.

| | What it is | Where it lives |
|---|---|---|
| **Consent** | A per-user setting, on by default. | Settings → Privacy |
| **Destination** | An address to report to. Absent unless someone deliberately configures one. | See below |

The default-on consent setting is not a way of enabling reporting by stealth. It cannot be,
because consent alone has nowhere to send anything. A destination is configured in exactly one of
these ways, and none of them happens by accident:

1. The `TDPDF_OTLP_ENDPOINT` and `TDPDF_OTLP_TOKEN` environment variables — for developers, or for
   anyone self-hosting who wants to point their own build at their own collector. Both are
   required; an endpoint without a token is treated as no destination at all.
2. Registry values at `HKLM\SOFTWARE\Policies\TDPdf\Telemetry\OtlpEndpoint` and `…\OtlpToken`,
   which an organisation pushes to machines it manages.

If you are running a build you compiled yourself and have done none of the above, TDPdf has no
destination and reports nothing. The Settings dialog tells you which of these two states you are
in rather than leaving you to guess.

Reports go to an OpenTelemetry collector operated by the deploying organisation. As of version
1.24.0.0 that is the only destination TDPdf can send to; earlier versions could also report to
Azure Application Insights, and that path has been removed from the application entirely.

## What is sent, when reporting is on

**Events.** A name, and nothing about the document involved:

`App.Startup`, `Install.Start`, `Install.Success`, `Uninstall.Start`, `Uninstall.Success`,
`Tool.Selected`, `Annotation.PlaceStarted`, `Annotation.PlaceCompleted`,
`Annotation.TextEditorFocusLost`, `Annotation.TextEditorFocusRestored`,
`Annotation.TextEditorInputStarted`, `Annotation.TextEditorClosed`, `File.New`, `File.Open`,
`File.Merge`, `File.Split`, `File.Print`,
`File.ExportImages`, `File.OpenFailed`, `File.OpenRecovered`, `File.OpenUnlockedByPdfium`,
`File.SaveFailed`, `File.SaveRecoveryAttempt`, `File.PrintFailed`, `File.ExportFailed`,
`Settings.Recovered`, `App.Heartbeat`, `App.SessionEnd`, and operation timings named `Op.*`.

`App.Heartbeat` is sent every 15 minutes while TDPdf is running and says only that it is still
running and for how long. `App.SessionEnd` records that it closed normally — its *absence* is what
tells us a run ended in a crash, which is how crash rate is measured without tracking anything
about you.

`Tool.Selected` records *which tool* you chose (Text, Signature, Highlight …), never what you did
with it. The annotation placement events record only whether Text or Signature placement started,
whether the text editor remained attached and received keyboard focus, and whether it was committed,
canceled, deleted, or left empty. A separate breadcrumb records only that editing began; it never
records the typed text. These events never record text, signature data, a page number, or document
details. `File.Open` records that an open happened, its duration and whether it succeeded — not
which file.

**Technical context.** Application version, Windows version, 64-bit or not, .NET runtime version,
processor count, and whether the install is per-user or machine-wide.

**Crash reports.** Exception type, message, stack trace and a derived grouping key. Messages and
stack traces are passed through `Diagnostics/Sanitizer.cs`, which scrubs file-system paths before
transmission, because exception text routinely contains the path of the document being worked on.
TDPdf deliberately exposes no way to report a raw, unscrubbed exception.

**A session identifier.** Every report carries a random identifier generated fresh each time
TDPdf starts. It is never written to disk and never reused, so it cannot link one run of the
application to another, or to you. It exists so that a hundred crashes from one unlucky machine can
be told apart from a hundred crashes across the whole fleet — those need very different responses.

**Device name.** Reports carry the machine's name — for example `L-JSMITH` — as the `host.name`
field, because it is what makes a report actionable: it separates one machine with a fault from a
fleet-wide regression.
On a corporate machine the name is often derived from the user's name, so treat it as identifying
that device and, indirectly, its user. This is the only identifying field in the payload, and it
is the one to weigh if you are deciding whether to enable reporting.

## What is never sent

- Document contents, in whole or in part.
- File names, file paths, or folder names.
- Signature images or drawn signatures.
- Text you type into a PDF, form field values, or search terms.
- Your username, email address, or any account identifier.
- A persistent user or device identifier. TDPdf sets no user ID and no device ID beyond the
  machine name described above. The session identifier is regenerated on every launch and never
  stored, so there is no cross-session fingerprint beyond that name.

## What is stored on your machine

TDPdf keeps two small local queues so a report raised without a network connection is not simply
lost. Both hold **only** the sanitised data described above — never document contents, names or
paths — and both live under your own user profile:

| Location | Contents | Lifetime |
|---|---|---|
| `%LOCALAPPDATA%\TDPdf\telemetry-spool` | Batches whose upload failed | Deleted once sent |
| `%LOCALAPPDATA%\TDPdf\pending-crashes` | Crash records the app died before it could send | Deleted on replay; dropped unsent after 14 days, capped at 50 |

The second exists because a crash that kills the process takes its in-memory report with it. Both
are safe to delete by hand at any time; deleting them loses unsent reports and nothing else. With
reporting turned off, nothing new is written to either.

## Where it goes

To whichever collector the destination points at. In the deployment this repository is maintained
for, that is a self-hosted collector operated by the same organisation that deploys TDPdf. If you
configure your own destination, it goes to yours, and the maintainers of this project receive
nothing.

## Turning it off

- **One user, this machine:** Settings → Privacy → untick *Send anonymous usage and crash
  reports*. This takes effect immediately for the rest of the session, and persists.
- **Whole device, permanently:** `TDPdf.exe /clear-telemetry`. This writes an opt-out marker that
  outranks every destination above, including one pushed by an administrator, and survives
  reinstalls.

## Changing this file

If you change what `Diagnostics/` collects, change this document in the same pull request. A
privacy notice that has drifted from the code is worse than none, because people rely on it.
