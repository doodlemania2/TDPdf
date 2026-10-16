# TDPdf

<!--
  Product page. Written as HTML blocks because it is a layout, not a document; the classes
  are defined in build/ghost/theme/code-injection-head.html under "Product pages".
  Markdown rules that matter here: no blank line INSIDE a section (a blank line ends a raw
  HTML block and the next indented line would turn into a code block), and every section is
  a top-level element so Ghost's grid lets it run full width.
  Screenshot slots and their fallbacks are explained at Expand-Shot in publish-site.ps1;
  the shot list is build/ghost/screenshots/README.md.
-->

<section class="tdp tdp-band tdp-hero">
<div class="tdp-wrap tdp-hero-grid">
<div>
<span class="tdp-eyebrow">New in 2.0 · Electronic signatures</span>
<h1>Edit, redact and sign PDFs on Windows. <em>Free.</em></h1>
<p class="tdp-lead">TDPdf handles the everyday PDF jobs: fixing a typo, filling a form, blacking out a name, getting a contract signed. It costs nothing, needs no account, and installs without an administrator.</p>
<div class="tdp-ctas">
<a class="tdp-btn primary" href="/download/">Download for Windows <small>{{VERSION}} · free</small></a>
<a class="tdp-btn ghost" href="#esign">See what's new</a>
</div>
<ul class="tdp-facts"><li>Free &amp; open source</li><li>Windows 10 &amp; 11</li><li>One signed file</li><li>No tracking</li></ul>
</div>
<div>
<!--shot:hero-->
<figure class="tdp-frame"><div class="tdp-frame-bar"><i></i><i></i><i></i><span>TDPdf</span></div><img src="{{SHOT}}" alt="TDPdf with a contract open: page thumbnails on the left, the document in the centre, the editing toolbar along the top." loading="eager"></figure>
<!--/shot:hero-->
<!--noshot:hero-->
<figure class="tdp-frame" aria-label="Illustration of TDPdf: a document with one redaction applied, one pending, and a signature field"><div class="tdp-frame-bar"><i></i><i></i><i></i><span>Illustration</span></div>
<svg viewBox="0 0 560 380" xmlns="http://www.w3.org/2000/svg" role="img" aria-hidden="true"><rect width="560" height="380" fill="#151515"/><rect x="0" y="0" width="560" height="34" fill="#1d1d1d"/><g fill="#3a3a3a"><rect x="14" y="11" width="14" height="12" rx="2"/><rect x="38" y="11" width="14" height="12" rx="2"/><rect x="62" y="11" width="14" height="12" rx="2"/><rect x="96" y="11" width="14" height="12" rx="2"/><rect x="120" y="11" width="14" height="12" rx="2"/></g><rect x="452" y="9" width="94" height="16" rx="4" fill="#22543d"/><text x="499" y="21" text-anchor="middle" font-family="Segoe UI,Arial" font-size="10" fill="#4ade80">Redact ▸ Apply</text><rect x="0" y="34" width="92" height="346" fill="#1a1a1a"/><g fill="#d9d9d9"><rect x="14" y="48" width="64" height="84" rx="2"/><rect x="14" y="144" width="64" height="84" rx="2" opacity=".55"/><rect x="14" y="240" width="64" height="84" rx="2" opacity=".55"/></g><rect x="12" y="46" width="68" height="88" rx="3" fill="none" stroke="#4ade80" stroke-width="2"/><rect x="128" y="50" width="400" height="320" rx="3" fill="#f2f2ee"/><rect x="128" y="50" width="400" height="44" fill="#2d4a6b"/><text x="148" y="78" font-family="Segoe UI,Arial" font-size="15" font-weight="700" fill="#fff">Services Agreement</text><g fill="#bdbdb6"><rect x="148" y="112" width="340" height="7" rx="2"/><rect x="148" y="128" width="310" height="7" rx="2"/><rect x="148" y="144" width="80" height="7" rx="2"/><rect x="322" y="144" width="150" height="7" rx="2"/><rect x="148" y="176" width="330" height="7" rx="2"/><rect x="148" y="192" width="90" height="7" rx="2"/><rect x="148" y="208" width="300" height="7" rx="2"/></g><rect x="232" y="140" width="86" height="15" fill="#111"/><rect x="244" y="188" width="120" height="15" fill="rgba(239,68,68,.12)" stroke="#ef4444" stroke-width="1.6" stroke-dasharray="5 3"/><text x="370" y="200" font-family="Segoe UI,Arial" font-size="9.5" fill="#c0392b">pending</text><rect x="148" y="262" width="170" height="54" rx="4" fill="rgba(232,207,99,.18)" stroke="#c9a92c" stroke-width="1.6"/><text x="160" y="282" font-family="Segoe UI,Arial" font-size="10" fill="#8a6d0b">Signature · Signer 1</text><path d="M162 302 c 14 -16 22 6 34 -6 s 16 -10 26 4 s 18 -6 30 -2" fill="none" stroke="#2d4a6b" stroke-width="2.2" stroke-linecap="round"/><rect x="342" y="262" width="150" height="24" rx="4" fill="rgba(232,207,99,.18)" stroke="#c9a92c" stroke-width="1.6"/><text x="352" y="278" font-family="Segoe UI,Arial" font-size="10" fill="#8a6d0b">Date signed</text><rect x="300" y="334" width="214" height="26" rx="13" fill="#123222" stroke="#4ade80"/><text x="407" y="351" text-anchor="middle" font-family="Segoe UI,Arial" font-size="10.5" fill="#4ade80">✓ Checked: redacted areas are empty</text></svg>
<figcaption>Illustration: one redaction applied, one still pending, and signature fields placed for a signer.</figcaption></figure>
<!--/noshot:hero-->
</div>
</div>
</section>

<section class="tdp tdp-band" style="padding-top:0">
<div class="tdp-wrap tdp-strip">
<div><b>Trusted by Acrobat</b><span>Signed documents get the green check</span></div>
<div><b>Checked redactions</b><span>TDPdf checks the saved file for leftovers</span></div>
<div><b>Installs in seconds</b><span>Per user, no admin, no UAC prompt</span></div>
<div><b>Open source</b><span>GPLv3, with the source for every release</span></div>
</div>
</section>

<section class="tdp tdp-band alt" id="esign">
<div class="tdp-wrap">
<div class="tdp-row">
<div>
<span class="tdp-tag">New in 2.0</span>
<h2>Send documents for signature from TDPdf</h2>
<p class="tdp-lead">Open the document, add the people who need to sign, mark where they sign, and send. You can follow each request from inside TDPdf until the signed copy comes back.</p>
<ul class="tdp-checks">
<li><b>Place fields</b> by dragging a box for each signature, initial, name, date or text field. If the document already has <code>{{signature, r1}}</code> placeholders, TDPdf uses them.</li>
<li><b>Set an order</b> for signers, approvers, viewers and CCs, and write your own subject and message.</li>
<li><b>Sign it yourself</b> if you're the only signer. The signed copy opens back in TDPdf.</li>
<li><b>See where each request stands</b>, and download the signed PDF, the certificate of completion and the audit log.</li>
<li><b>Catch mistakes before sending.</b> TDPdf won't send if a signer has nothing to sign, or if you marked text for redaction and didn't apply it.</li>
</ul>
<p class="tdp-muted">Documents are sealed with a certificate on Adobe's Approved Trust List, held in a hardware security module and timestamped. Create a free account at <a href="https://sign.tdpdf.com/signup">sign.tdpdf.com</a> and connect it once. Organisations can use their own Documenso server instead.</p>
</div>
<div>
<!--shot:esign-send-->
<figure class="tdp-frame"><div class="tdp-frame-bar"><i></i><i></i><i></i><span>Send for Signature</span></div><img src="{{SHOT}}" alt="The Send for Signature window: recipients in signing order on one side, signature and date fields placed on the page on the other." loading="lazy"></figure>
<!--/shot:esign-send-->
<!--noshot:esign-send-->
<figure class="tdp-frame" aria-label="Diagram of the electronic signature flow"><div class="tdp-frame-bar"><i></i><i></i><i></i><span>How it works</span></div>
<svg viewBox="0 0 560 330" xmlns="http://www.w3.org/2000/svg" role="img" aria-hidden="true"><rect width="560" height="330" fill="#141414"/><defs><marker id="ah" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto"><path d="M0 0 L10 5 L0 10z" fill="#4ade80"/></marker></defs><g font-family="Segoe UI,Arial"><rect x="24" y="96" width="120" height="140" rx="10" fill="#1f1f1f" stroke="#333"/><rect x="44" y="114" width="80" height="96" rx="3" fill="#f2f2ee"/><g fill="#bdbdb6"><rect x="54" y="128" width="60" height="5"/><rect x="54" y="140" width="52" height="5"/><rect x="54" y="152" width="58" height="5"/></g><rect x="54" y="174" width="50" height="22" rx="3" fill="rgba(232,207,99,.25)" stroke="#c9a92c"/><text x="84" y="256" text-anchor="middle" font-size="12" fill="#e0e0e0">TDPdf</text><text x="84" y="272" text-anchor="middle" font-size="10" fill="#888">place fields · send</text><line x1="150" y1="166" x2="214" y2="166" stroke="#4ade80" stroke-width="2" marker-end="url(#ah)"/><rect x="220" y="116" width="128" height="100" rx="12" fill="#17311f" stroke="#4ade80"/><text x="284" y="160" text-anchor="middle" font-size="13" font-weight="700" fill="#4ade80">sign.tdpdf.com</text><text x="284" y="180" text-anchor="middle" font-size="10" fill="#9fd9b2">emails each signer</text><line x1="352" y1="140" x2="402" y2="96" stroke="#4ade80" stroke-width="1.6" marker-end="url(#ah)"/><line x1="352" y1="166" x2="402" y2="166" stroke="#4ade80" stroke-width="1.6" marker-end="url(#ah)"/><line x1="352" y1="192" x2="402" y2="236" stroke="#4ade80" stroke-width="1.6" marker-end="url(#ah)"/><g fill="#2a2a2a" stroke="#555"><circle cx="424" cy="86" r="16"/><circle cx="424" cy="166" r="16"/><circle cx="424" cy="246" r="16"/></g><g font-size="10" fill="#cfcfcf"><text x="448" y="90">Signer 1 ✓</text><text x="448" y="170">Signer 2 ✓</text><text x="448" y="250">Approver ✓</text></g><rect x="200" y="262" width="168" height="44" rx="10" fill="#101d14" stroke="#4ade80" stroke-dasharray="4 3"/><text x="284" y="281" text-anchor="middle" font-size="11" font-weight="700" fill="#4ade80">✓ Sealed PDF + audit log</text><text x="284" y="296" text-anchor="middle" font-size="9.5" fill="#9fd9b2">back in TDPdf · green check in Acrobat</text></g></svg>
<figcaption>How it works: TDPdf sends the document, the signing service emails each signer, and the signed copy comes back.</figcaption></figure>
<!--/noshot:esign-send-->
<!--shot:esign-requests-->
<figure class="tdp-frame" style="margin-top:1.2rem"><div class="tdp-frame-bar"><i></i><i></i><i></i><span>Signature Requests</span></div><img src="{{SHOT}}" alt="Signature Requests: each document with every recipient's progress and buttons to download the signed copy and audit log." loading="lazy"></figure>
<!--/shot:esign-requests-->
</div>
</div>
</div>
</section>

<section class="tdp tdp-band">
<div class="tdp-wrap">
<div class="tdp-row flip">
<div>
<span class="tdp-tag">Redaction</span>
<h2>Redaction that removes the text</h2>
<p class="tdp-lead">Drawing a black rectangle over a name leaves the name in the file, where anyone can copy it out. TDPdf deletes the text and images under your mark, then <b>opens the saved file and checks</b> that nothing is left.</p>
<ul class="tdp-checks">
<li><b>If the check finds anything, the file isn't saved.</b> You get an error that says what was found.</li>
<li><b>Redact every match of a search</b> across the whole document, then click any you want to keep.</li>
<li><b>Works on scanned pages.</b> The hidden OCR text is removed along with the image, and TDPdf asks before it flattens a page.</li>
<li><b>Clears the metadata</b>, including the title, author and the second copy kept in XMP.</li>
</ul>
</div>
<div>
<!--shot:redact-->
<figure class="tdp-frame"><div class="tdp-frame-bar"><i></i><i></i><i></i><span>Redact</span></div><img src="{{SHOT}}" alt="Pending redaction marks shown as dashed red outlines over names, with the Redact Permanently button." loading="lazy"></figure>
<!--/shot:redact-->
<!--noshot:redact-->
<figure class="tdp-frame" aria-label="Diagram of the redaction pipeline"><div class="tdp-frame-bar"><i></i><i></i><i></i><span>How it works</span></div>
<svg viewBox="0 0 560 300" xmlns="http://www.w3.org/2000/svg" role="img" aria-hidden="true"><rect width="560" height="300" fill="#141414"/><defs><marker id="ar" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto"><path d="M0 0 L10 5 L0 10z" fill="#666"/></marker></defs><g font-family="Segoe UI,Arial" text-anchor="middle"><g><rect x="20" y="70" width="112" height="120" rx="10" fill="#1f1f1f" stroke="#333"/><rect x="40" y="96" width="72" height="16" fill="rgba(239,68,68,.15)" stroke="#ef4444" stroke-dasharray="4 3"/><text x="76" y="140" font-size="12.5" font-weight="700" fill="#fff">1 · Mark</text><text x="76" y="158" font-size="9.5" fill="#888">drag, or a search</text></g><line x1="136" y1="130" x2="156" y2="130" stroke="#666" stroke-width="2" marker-end="url(#ar)"/><g><rect x="160" y="70" width="112" height="120" rx="10" fill="#1f1f1f" stroke="#333"/><rect x="180" y="96" width="72" height="16" fill="#111" stroke="#444"/><text x="216" y="140" font-size="12.5" font-weight="700" fill="#fff">2 · Remove</text><text x="216" y="158" font-size="9.5" fill="#888">text, images, metadata</text></g><line x1="276" y1="130" x2="296" y2="130" stroke="#666" stroke-width="2" marker-end="url(#ar)"/><g><rect x="300" y="70" width="112" height="120" rx="10" fill="#1f1f1f" stroke="#4ade80"/><circle cx="356" cy="104" r="13" fill="none" stroke="#4ade80" stroke-width="2.4"/><line x1="365" y1="113" x2="374" y2="122" stroke="#4ade80" stroke-width="2.4"/><text x="356" y="140" font-size="12.5" font-weight="700" fill="#4ade80">3 · Re-read</text><text x="356" y="158" font-size="9.5" fill="#9fd9b2">the finished file</text></g><line x1="416" y1="110" x2="436" y2="96" stroke="#666" stroke-width="2" marker-end="url(#ar)"/><line x1="416" y1="150" x2="436" y2="166" stroke="#666" stroke-width="2" marker-end="url(#ar)"/><g><rect x="440" y="58" width="104" height="58" rx="10" fill="#123222" stroke="#4ade80"/><text x="492" y="84" font-size="12" font-weight="700" fill="#4ade80">✓ Saved</text><text x="492" y="100" font-size="9" fill="#9fd9b2">area proven empty</text></g><g><rect x="440" y="144" width="104" height="58" rx="10" fill="#2a1414" stroke="#ef4444"/><text x="492" y="170" font-size="12" font-weight="700" fill="#ef4444">✕ Refused</text><text x="492" y="186" font-size="9" fill="#e7a3a3">nothing written</text></g><text x="280" y="250" font-size="11" fill="#888">The file is saved only if the check passes.</text></g></svg>
<figcaption>How it works: mark, remove, re-read the saved file, then save only if the check passes.</figcaption></figure>
<!--/noshot:redact-->
</div>
</div>
</div>
</section>

<section class="tdp tdp-band alt">
<div class="tdp-wrap">
<div class="tdp-row">
<div>
<span class="tdp-tag">Editing</span>
<h2>Edit the text that's already there</h2>
<p class="tdp-lead">Double-click a line and type. TDPdf changes the text in the document, in the same font, size and colour, so the old wording is gone rather than covered with a white box.</p>
<ul class="tdp-checks">
<li><b>Tells you about missing letters.</b> PDFs often include only the characters they use. If you type one the font doesn't have, TDPdf says which.</li>
<li><b>Create forms</b> with text boxes, checkboxes, radio buttons, dropdowns and signature fields.</li>
<li><b>Mark up documents</b> with text boxes, highlights, freehand ink, shapes, images and a saved picture of your signature.</li>
</ul>
</div>
<div>
<!--shot:edit-text-->
<figure class="tdp-frame"><div class="tdp-frame-bar"><i></i><i></i><i></i><span>Edit text</span></div><img src="{{SHOT}}" alt="Inline text editing: a line of a document being retyped in its own font." loading="lazy"></figure>
<!--/shot:edit-text-->
<!--noshot:edit-text-->
<figure class="tdp-frame" aria-label="Illustration of text editing before and after"><div class="tdp-frame-bar"><i></i><i></i><i></i><span>Illustration</span></div>
<svg viewBox="0 0 560 280" xmlns="http://www.w3.org/2000/svg" role="img" aria-hidden="true"><rect width="560" height="280" fill="#141414"/><g font-family="Georgia,Times New Roman,serif"><rect x="30" y="30" width="230" height="200" rx="4" fill="#f2f2ee"/><text x="48" y="64" font-size="13" fill="#333">Payment terms</text><text x="48" y="96" font-size="12" fill="#555">Invoices are due within</text><text x="48" y="116" font-size="12" fill="#555">30 days. Late fee:</text><rect x="152" y="104" width="44" height="16" fill="#fff"/><text x="154" y="116" font-size="12" fill="#555">5%</text><rect x="148" y="102" width="52" height="20" fill="none" stroke="#ef4444" stroke-dasharray="3 2"/><text x="48" y="160" font-family="Segoe UI,Arial" font-size="10" fill="#b03a2e">Most editors: a white box over "1.5%"</text><text x="48" y="176" font-family="Segoe UI,Arial" font-size="10" fill="#b03a2e">The old text is still in the file.</text><rect x="300" y="30" width="230" height="200" rx="4" fill="#f2f2ee"/><text x="318" y="64" font-size="13" fill="#333">Payment terms</text><text x="318" y="96" font-size="12" fill="#555">Invoices are due within</text><text x="318" y="116" font-size="12" fill="#555">30 days. Late fee: 5%</text><rect x="420" y="102" width="26" height="20" fill="none" stroke="#2f9e5a"/><text x="318" y="160" font-family="Segoe UI,Arial" font-size="10" fill="#1d7a43">TDPdf: the text itself is changed,</text><text x="318" y="176" font-family="Segoe UI,Arial" font-size="10" fill="#1d7a43">in the same font. "1.5%" is gone.</text></g></svg>
<figcaption>Illustration: covering the old text versus changing it.</figcaption></figure>
<!--/noshot:edit-text-->
</div>
</div>
</div>
</section>

<section class="tdp tdp-band">
<div class="tdp-wrap tdp-center">
<span class="tdp-eyebrow">And the rest</span>
<h2>Everything else you'd expect from a PDF editor</h2>
<div class="tdp-grid" style="text-align:left">
<div class="tdp-card"><div class="tdp-ico">⧉</div><h3>Merge, split &amp; reorder</h3><p>Drag pages around the sidebar; insert, rotate, extract and delete across a selection.</p></div>
<div class="tdp-card"><div class="tdp-ico">☰</div><h3>Fill any form</h3><p>Type into form fields, then save, or flatten the form so it can't be changed.</p></div>
<div class="tdp-card"><div class="tdp-ico">⌕</div><h3>Search &amp; OCR</h3><p>Search the whole document, copy text out, and make scanned pages searchable.</p></div>
<div class="tdp-card"><div class="tdp-ico">▤</div><h3>Tabs</h3><p>Several PDFs in one window, each with its own unsaved-changes tracking.</p></div>
<div class="tdp-card"><div class="tdp-ico">✓</div><h3>Verify signatures</h3><p>See who signed any PDF, when, and whether anything was added afterwards.</p></div>
<div class="tdp-card"><div class="tdp-ico">⎙</div><h3>Print &amp; flatten</h3><p>Print with your annotations, or flatten every page so the file can't be edited.</p></div>
<div class="tdp-card"><div class="tdp-ico">⚿</div><h3>Locked PDFs</h3><p>Password-protected files ask for the password. Damaged files are repaired when they open.</p></div>
<div class="tdp-card"><div class="tdp-ico">✎</div><h3>Bookmarks &amp; measure</h3><p>Edit bookmarks, measure distances and areas, and export pages as images.</p></div>
<div class="tdp-card"><div class="tdp-ico">◐</div><h3>Light, dark &amp; night</h3><p>Follows your Windows theme, with high contrast and a night mode for reading in the dark.</p></div>
</div>
</div>
</section>

<section class="tdp tdp-band alt">
<div class="tdp-wrap">
<div class="tdp-row flip">
<div>
<span class="tdp-tag">For IT</span>
<h2>Easy to deploy</h2>
<p class="tdp-lead">The whole application is one file, <code>TDPdf.exe</code>. There's no runtime to install first and no admin prompt.</p>
<ul class="tdp-checks">
<li><b>Run it from anywhere</b>, including a USB stick. <i>Install TDPdf…</i> installs it for the current user, with a Start Menu entry and an uninstaller.</li>
<li><b>Silent install</b> with <code>TDPdf.exe /install /silent</code>. Run as SYSTEM (as Intune does), it installs for every user on the machine.</li>
<li><b>Code-signed by The Doodle Project</b> through Microsoft Artifact Signing.</li>
<li><b>Registry policies</b> for telemetry, update checks and the signing server. All optional.</li>
</ul>
</div>
<div>
<!--shot:install-->
<figure class="tdp-frame"><div class="tdp-frame-bar"><i></i><i></i><i></i><span>Install</span></div><img src="{{SHOT}}" alt="Running a downloaded copy where TDPdf is already installed: it offers to reinstall or uninstall the existing install." loading="lazy"></figure>
<!--/shot:install-->
<!--noshot:install-->
<figure class="tdp-frame"><div class="tdp-frame-bar"><i></i><i></i><i></i><span>PowerShell</span></div>
<pre style="margin:0;border:0;border-radius:0;background:#0f0f0f;padding:1.3rem 1.4rem;font-size:.86rem;line-height:1.7"><code><span style="color:#888"># install (per user, or per machine when run as SYSTEM)</span>
.\TDPdf.exe /install /silent
<span style="color:#888"># remove</span>
.\TDPdf.exe /uninstall /silent
<span style="color:#888"># verify the publisher</span>
(Get-AuthenticodeSignature .\TDPdf.exe).SignerCertificate.Subject
<span style="color:#4ade80">CN=The Doodle Project, O=The Doodle Project, …</span></code></pre>
<figcaption>Install, uninstall and check the signature from PowerShell.</figcaption></figure>
<!--/noshot:install-->
</div>
</div>
</div>
</section>

<section class="tdp tdp-band">
<div class="tdp-wrap tdp-center">
<span class="tdp-eyebrow">Private by default</span>
<h2>No tracking</h2>
<p class="tdp-lead">TDPdf doesn't send usage data anywhere unless your organisation sets up its own collector for it. Your documents leave your computer only when you send them for signature.</p>
<div class="tdp-vs" style="text-align:left">
<div class="yes"><h3>What you get</h3><ul><li>Every feature, free</li><li>Open a file without an account</li><li>Install without an administrator</li><li>Redactions verified before they're saved</li><li>Source code for every release</li></ul></div>
<div class="no"><h3>What you don't get</h3><ul><li>Subscriptions or per-seat licences</li><li>Upgrade prompts or "Pro" features</li><li>Uploading files to edit them</li><li>Usage tracking by default</li><li>Closed source</li></ul></div>
</div>
<p class="tdp-muted" style="margin-top:1.4rem">The <a href="/privacy/">privacy policy</a> lists every field that can be sent. <a href="/telemetry/">Telemetry</a> explains how an organisation can collect it.</p>
</div>
</section>

<section class="tdp tdp-band tdp-final alt">
<div class="tdp-wrap">
<h2>Get TDPdf {{VERSION}}</h2>
<p class="tdp-lead" style="margin:0 auto">Free for Windows 10 and 11. One {{EXE_SIZE_MB}}&nbsp;MB download.</p>
<div class="tdp-ctas"><a class="tdp-btn primary" href="/download/">Download for Windows</a><a class="tdp-btn ghost" href="{{REPO_URL}}">Source on GitHub</a></div>
<p class="tdp-muted">TDPdf is GPLv3 software from The Doodle Project, LLC, built on <a href="https://github.com/SteveTheKiller/KillerPDF">KillerPDF</a>. <a href="/third-party-licenses/">Third-party licences</a>.</p>
</div>
</section>
