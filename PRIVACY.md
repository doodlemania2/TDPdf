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

1. The `TDPDF_TELEMETRY_CONNECTION` environment variable — for developers, or for anyone
   self-hosting who wants to point their own build at their own collector.
2. A registry value at `HKLM\SOFTWARE\Policies\TDPdf\Telemetry\ConnectionString`, which an
   organisation pushes to machines it manages.
3. A provisioning file written by an administrator running `TDPdf.exe /set-telemetry`.

If you are running a build you compiled yourself and have done none of the above, TDPdf has no
destination and reports nothing. The Settings dialog tells you which of these two states you are
in rather than leaving you to guess.

## What is sent, when reporting is on

**Events.** A name, and nothing about the document involved:

`App.Startup`, `Install.Start`, `Install.Success`, `Uninstall.Start`, `Uninstall.Success`,
`Tool.Selected`, `File.New`, `File.Open`, `File.Merge`, `File.Split`, `File.Print`,
`File.ExportImages`, `File.OpenFailed`, `File.OpenRecovered`, `File.OpenUnlockedByPdfium`,
`File.SaveFailed`, `File.SaveRecoveryAttempt`, `File.PrintFailed`, `File.ExportFailed`,
`Settings.Recovered`, and operation timings named `Op.*`.

`Tool.Selected` records *which tool* you chose (Text, Signature, Highlight …), never what you did
with it. `File.Open` records that an open happened, its duration and whether it succeeded — not
which file.

**Technical context.** Application version, Windows version, 64-bit or not, .NET runtime version,
processor count, and whether the install is per-user or machine-wide.

**Crash reports.** Exception type, message, stack trace and a derived grouping key. Messages and
stack traces are passed through `Diagnostics/Sanitizer.cs`, which scrubs file-system paths before
transmission, because exception text routinely contains the path of the document being worked on.
TDPdf deliberately exposes no way to report a raw, unscrubbed exception.

**Device name.** Reports carry the machine's name — for example `L-JSMITH` — because it is what
makes a report actionable: it separates one machine with a fault from a fleet-wide regression.
On a corporate machine the name is often derived from the user's name, so treat it as identifying
that device and, indirectly, its user. This is the only identifying field in the payload, and it
is the one to weigh if you are deciding whether to enable reporting.

## What is never sent

- Document contents, in whole or in part.
- File names, file paths, or folder names.
- Signature images or drawn signatures.
- Text you type into a PDF, form field values, or search terms.
- Your username, email address, or any account identifier.
- A persistent user or device identifier. TDPdf explicitly does not set the reporting SDK's user
  or device ID fields, so there is no cross-session fingerprint beyond the machine name above.

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
