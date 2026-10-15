# tdpdf.com screenshots

The home page has a slot for each file below. Until a PNG is on `main`, the page shows
an illustration in that slot instead (labelled as one), so nothing ever renders broken.
Add a file, merge it, run `build/ghost/publish-site.ps1`, and the slot switches over.

## How to take them

- Windows 11, TDPdf **Dark** theme, window about **1600 × 1000**, 100% display scaling
  (or 200%, then it is still crisp). Maximised windows look cramped; a floating window is better.
- Crop to the TDPdf window only — no taskbar, no desktop. The page draws its own frame
  around each image, so do not add a shadow.
- PNG, under ~600 KB each. File names exactly as below.
- Use the two documents in `demo/`. They have believable content and no real names. They
  were made for this, so nothing in a screenshot needs blurring.

## Shot list

| File | Document | What to show |
|---|---|---|
| `hero.png` | `demo/services-agreement.pdf` | The whole window: page sidebar open, page 1 at a comfortable zoom, the toolbar visible, and ideally a second tab open so tabs show. This is the first thing every visitor sees. |
| `esign-send.png` | `demo/services-agreement.pdf` | **File ▸ Electronic Signature ▸ Send for Signature**, with two recipients (Dana Whitfield, signer 1; Marcus Lee, signer 2, in order), and signature + date fields placed for both. The `{{signature, r1}}` placeholders in this file are picked up automatically. |
| `esign-requests.png` | — | **Signature Requests** with two or three requests in different states (one completed, one waiting on a signer). Send a couple of test requests to yourself first. |
| `redact.png` | `demo/incident-report.pdf` | Redact tool (`R`): the names and phone numbers marked (dashed red outlines), **before** pressing Redact Permanently. "Redact matches" on a search for *Jordan Ellis* marks all three in one go. |
| `edit-text.png` | `demo/services-agreement.pdf` | Double-click "1.5%" in section 2 and retype it as "1%", with the edit box open. |
| `install.png` | — | Run the downloaded exe on a machine that already has TDPdf installed, click **Install TDPdf…**: the dialog that offers Upgrade / Reinstall / Uninstall. |

Optional, not slotted yet: form building (Form tool, `F`) and Verify Signatures in
Document Info. Send them anyway; adding a slot is a few lines in `content/home.md`.
