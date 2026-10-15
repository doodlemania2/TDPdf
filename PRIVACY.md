# Privacy

TDPdf can report anonymous usage and crash information, and it can send a document you choose
to an electronic-signature service. This page describes what each of those sends. It is based on
the source in `Diagnostics/` and `Services/Documenso*.cs`. If this page and the source ever
disagree, the source describes what TDPdf does, and this page needs fixing.

## The short version

- **Your documents stay on your computer unless you send one for electronic signature.**
  Reporting sends no PDF content, file names or file paths. Nothing you open, type, annotate or
  place a signature picture on is transmitted. The exception is a document you send with
  **File → Electronic Signature**. See *[Electronic Signature](#electronic-signature)*.
- **A build with no reporting destination configured sends nothing.** Any build compiled from a
  public checkout of this repository is in that state.
- **You can turn reporting off** in **Settings → Privacy → "Send anonymous usage and crash
  reports"**, or for the whole device with `TDPdf.exe /clear-telemetry`.
- **One check runs whether reporting is on or off:** twice a day TDPdf asks GitHub whether a newer
  version has been released. The request carries nothing about you. See
  *[Checking for updates](#checking-for-updates)*.

## Two independent switches

Reporting needs both of the following. Either one alone sends nothing.

| | What it is | Where it lives |
|---|---|---|
| **Consent** | A per-user setting, on by default. | Settings → Privacy |
| **Destination** | An address to report to. Absent unless someone configures one. | See below |

Consent is on by default, but consent alone has nowhere to send anything. A destination can only
be configured in one of these two ways:

1. The `TDPDF_OTLP_ENDPOINT` and `TDPDF_OTLP_TOKEN` environment variables. These are for
   developers, or for anyone who wants their own build to report to their own collector. Both are
   required. An endpoint without a token counts as no destination.
2. Registry values at `HKLM\SOFTWARE\Policies\TDPdf\Telemetry\OtlpEndpoint` and `…\OtlpToken`,
   which an organisation pushes to machines it manages.

If you compiled TDPdf yourself and set neither, it has no destination and reports nothing. The
Settings dialog shows which state you are in.

Reports go to an OpenTelemetry collector run by the organisation that deployed TDPdf. TDPdf cannot
send reports anywhere else.

## What is sent, when reporting is on

**Events.** Each event is a name, with nothing about the document involved:

`App.Startup`, `Install.Start`, `Install.Success`, `Uninstall.Start`, `Uninstall.Success`,
`Tool.Selected`, `Annotation.PlaceStarted`, `Annotation.PlaceCompleted`,
`Annotation.TextEditorFocusLost`, `Annotation.TextEditorFocusRestored`,
`Annotation.TextEditorInputStarted`, `Annotation.TextEditorClosed`,
`Annotation.TextEditorCommitDeferred`, `Zoom.Churn`, `Update.Available`,
`Update.SyncTriggered`, `Update.RestartPending`, `Update.CheckFailed`, `File.New`, `File.Open`,
`File.Merge`, `File.Split`, `File.Print`,
`File.ExportImages`, `File.OpenFailed`, `File.OpenRecovered`, `File.OpenUnlockedByPdfium`,
`File.SaveFailed`, `File.SaveRecoveryAttempt`, `File.PrintFailed`, `File.ExportFailed`,
`Settings.Recovered`, `App.Heartbeat`, `App.SessionEnd`, and operation timings named `Op.*`.

`App.Heartbeat` is sent every 15 minutes while TDPdf is running. It says that TDPdf is still
running and for how long. `App.SessionEnd` records a normal close. A session with no
`App.SessionEnd` ended in a crash, which is how the crash rate is measured without tracking you.

`Tool.Selected` records which tool you chose (Text, Signature, Highlight …). It does not record
what you did with it. The annotation placement events record whether Text or Signature placement
started, whether the text editor stayed attached and got keyboard focus, and whether the text was
committed, canceled, deleted or left empty. A separate breadcrumb records that editing began; it
does not record the typed text. None of these events record text, signature data, a page number
or document details. `File.Open` records that a file was opened, how long it took and whether it
succeeded. It does not record which file.

`Zoom.Churn` is a self-diagnostic. If the view re-applies its zoom 8 or more times a second for at
least two seconds, it reports how many times, for how long, the name of the method in TDPdf's
source that asked for most of them, how many that method accounted for, and the current zoom fit
mode and view mode. Method names are fixed at compile time and the two modes are settings you
chose, so the report describes a part of the program and your view settings. It does not describe
a document, a page, or anything you typed. It is sent at most once every 15 minutes.

`Annotation.TextEditorCommitDeferred` records that TDPdf kept open a text box you had not typed
into yet, and which part of the program asked to close it. `Annotation.TextEditorClosed` carries
the same part-of-the-program name along with the outcome described above. Like the other
annotation events, they record no text and no document detail.

**Technical context.** Application version, Windows version, 64-bit or not, .NET runtime version,
processor count, and whether the install is per-user or machine-wide.

**Crash reports.** Exception type, message, stack trace and a derived grouping key. Messages and
stack traces pass through `Diagnostics/Sanitizer.cs`, which removes file-system paths before
sending, because exception text often contains the path of the open document. TDPdf has no way to
report an exception without sanitising it first.

**A session identifier.** Every report carries a random identifier created each time TDPdf
starts. It is never written to disk or reused, so it cannot link one run of TDPdf to another, or
to you. It lets someone reading the reports tell a hundred crashes on one machine from a hundred
crashes spread across many machines.

**Device name.** Reports carry the computer's name, for example `L-JSMITH`, in the `host.name`
field. It shows whether a problem is on one machine or on many.
On a work computer the name is often based on the user's name, so treat it as identifying the
device and, indirectly, its user. It is the only identifying field in a report, and the one to
consider when deciding whether to enable reporting.

## Checking for updates

Twice a day, TDPdf asks GitHub whether a newer version of TDPdf has been released. It is an HTTPS
request to this project's public releases listing. It is the **only** network request TDPdf makes
on its own, and it runs whatever your reporting setting is.

**The request says nothing about you.** It carries no identifier, no document information and no
machine name. It doesn't include your version number either: TDPdf downloads the latest public
version number and compares it on your computer. To GitHub it looks the same as anyone else
visiting that public page.

**It never downloads or installs anything.** On a computer managed by your organisation, when
TDPdf finds a newer version it asks Windows to check in with the management service early. The
update then arrives promptly instead of up to eight hours later. Your organisation still delivers
the update, the same way it always does.

The four `Update.*` events listed above record these checks. They carry TDPdf version numbers and
whether the check-in request succeeded, and nothing else.

**Turning it off.** An administrator can disable the check for an organisation by setting
`Enabled` to `0` under `HKEY_LOCAL_MACHINE\SOFTWARE\Policies\TDPdf\Update`. Without network
access, the check fails quietly and TDPdf carries on.

## Electronic Signature

**File → Electronic Signature** sends documents to a signing service so that other people, or you,
can sign them with a digital signature. Unlike the rest of this page, this feature sends the
document itself.

**Where it goes.** By default, to `https://sign.tdpdf.com`, a Documenso signing service run by The
Doodle Project. Your organisation can point TDPdf at its own Documenso service instead (the
`BaseUrl` value under `HKEY_LOCAL_MACHINE\SOFTWARE\Policies\TDPdf\Documenso`). You can also enter
a different address in **Settings → Electronic signature**, unless your organisation has fixed it.
Settings always shows which service is in use.

**What is sent when you use it:**

- **The whole document** you send for signature or choose to sign (every page, as saved on disk),
  and its title.
- **The names and email addresses of the recipients you enter**, and their signing order. The
  signing service emails each of them a link to sign.
- **Your account on that service.** TDPdf connects with an API token from your account, so the
  service knows which account sent the request.

The signing service keeps the documents, the recipients and an audit trail of who signed what and
when, under its own policies. That record is what lets a signature be checked later. Files you
download from it in TDPdf (a signed copy, a certificate of completion, an audit log) are saved
where you choose.

**Nothing is sent unless you use it.** TDPdf contacts the signing service only when you choose Send
for Signature, Sign This Document, Signature Requests, Verify Signatures, Connect, or Test
connection. It does not contact it at startup or in the background, and it does not send a
document you haven't chosen to send.

**Your token.** When you connect, TDPdf stores the API token you paste in
`%LOCALAPPDATA%\TDPdf\documenso.cred`, encrypted with Windows data protection so that only your
Windows account on this computer can read it. It is sent only to the signing service it was
created for. It is never included in usage or crash reports and is not copied to other computers.
**Disconnect** in Settings deletes it from this computer. To revoke it, also delete it from your
account on the signing service.

**Turning it off.** An administrator can remove the feature by setting `Enabled` to `0` under
`HKEY_LOCAL_MACHINE\SOFTWARE\Policies\TDPdf\Documenso`. It then disappears from the menu.

## What is never sent

This section is about usage and crash reporting. Electronic Signature, above, is the only feature
that sends a document or a person's details, and only when you use it.

- Document contents, in whole or in part.
- File names, file paths, or folder names.
- Signature images or drawn signatures.
- Text you type into a PDF, form field values, or search terms.
- Your username, email address, or any account identifier.
- A persistent user or device identifier. TDPdf sets no user ID and no device ID apart from the
  machine name described above. The session identifier is created fresh at every launch and never
  stored, so nothing links one session to the next except that name.

## What is stored on your machine

TDPdf keeps two small local queues so that a report raised while offline isn't lost. Both hold
**only** the sanitised data described above (no document contents, names or paths), and both are
in your own user profile:

| Location | Contents | Lifetime |
|---|---|---|
| `%LOCALAPPDATA%\TDPdf\telemetry-spool` | Batches whose upload failed | Deleted once sent |
| `%LOCALAPPDATA%\TDPdf\pending-crashes` | Crash records the app died before it could send | Deleted on replay; dropped unsent after 14 days, capped at 50 |

The second queue exists because a crash that ends the process also loses any report still in
memory. You can delete either folder at any time; you lose unsent reports and nothing else. With
reporting turned off, nothing new is written to either.

## Where it goes

Reports go to the collector the destination points to. Where this repository's maintainers deploy
TDPdf, that is a self-hosted collector run by the same organisation that deploys TDPdf. If you
configure your own destination, reports go to yours, and the maintainers of this project receive
nothing.

## Turning it off

- **One user on this computer:** Settings → Privacy → untick *Send anonymous usage and crash
  reports*. This takes effect immediately and stays off.
- **The whole device, permanently:** `TDPdf.exe /clear-telemetry`. This writes an opt-out marker
  that overrides every destination above, including one pushed by an administrator, and it
  survives reinstalls.
