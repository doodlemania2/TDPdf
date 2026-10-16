# Electronic Signature smoke test on real Windows (.github/workflows/esign-smoke.yml).
#
# Drives the real, single-file-published TDPdf.exe through UI Automation, mouse and keyboard,
# against build/esign-smoke/mock_server.py — a stand-in signing service on 127.0.0.1 that TDPdf is
# pointed at with its ordinary TDPDF_DOCUMENSO_BASEURL environment variable. Nothing in the product
# is test-only. Every step captures a screenshot and a UI Automation dump, and writes PASS/FAIL
# lines; the script exits non-zero if anything FAILed.
#
# Helpers are the ones build/screenshots/capture.ps1 uses (DWM frame bounds, UIA, mouse_event).
param(
    [string]$Exe,
    [string]$Demo,
    [string]$Out,
    [int]$MockPort = 8899
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$log = Join-Path $Out 'smoke.log'
Set-Content $log ''

Add-Type -AssemblyName System.Drawing, System.Windows.Forms, UIAutomationClient, UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class W {
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hgt, bool r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint x, uint y, uint d, UIntPtr e);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT r, int s);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
"@
$UIA = [System.Windows.Automation.AutomationElement]
$Scope = [System.Windows.Automation.TreeScope]

$script:failures = 0
function Log([string]$s) { Write-Host $s; Add-Content $log $s }
function Pass([string]$what, [string]$detail = '') { Log ("PASS  $what" + $(if ($detail) { "  -- $detail" } else { '' })) }
function Fail([string]$what, [string]$detail = '') { $script:failures++; Log ("FAIL  $what" + $(if ($detail) { "  -- $detail" } else { '' })) }
function Check([string]$what, [bool]$ok, [string]$detail = '') { if ($ok) { Pass $what $detail } else { Fail $what $detail } }

function VisibleRect([IntPtr]$h) {
    $r = New-Object W+RECT
    if ([W]::DwmGetWindowAttribute($h, 9, [ref]$r, 16) -ne 0) { [W]::GetWindowRect($h, [ref]$r) | Out-Null }
    $r
}
function Grab([string]$name, [int]$x, [int]$y, [int]$w, [int]$ht) {
    if ($w -le 0 -or $ht -le 0) { return }
    $bmp = New-Object System.Drawing.Bitmap $w, $ht
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($x, $y, 0, 0, $bmp.Size)
    $bmp.Save((Join-Path $Out "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
}
function Full([string]$name) {
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    Grab $name 0 0 $b.Width $b.Height
}
function Dump([string]$name, $root) {
    $all = $root.FindAll($Scope::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    $lines = foreach ($e in $all) {
        $c = $e.Current
        $br = $c.BoundingRectangle
        $pos = if ($br.IsEmpty -or [double]::IsInfinity($br.X)) { '-' } else { '{0},{1},{2},{3}' -f [int]$br.X, [int]$br.Y, [int]$br.Width, [int]$br.Height }
        "{0}`t{1}`t{2}`t{3}`t{4}" -f $c.ControlType.ProgrammaticName, $c.AutomationId, $c.Name, $c.ClassName, $pos
    }
    $lines | Set-Content (Join-Path $Out "$name.uia.txt")
}
# Top-level windows of a process, including OWNED windows (a WPF dialog with an Owner shows up in
# UIA as a child of its owner, not of the desktop).
function TopWindows($proc) {
    $pc = New-Object System.Windows.Automation.PropertyCondition($UIA::ProcessIdProperty, $proc.Id)
    $wc = New-Object System.Windows.Automation.PropertyCondition($UIA::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)
    $list = New-Object System.Collections.ArrayList
    $queue = New-Object System.Collections.Queue
    foreach ($w in $UIA::RootElement.FindAll($Scope::Children, $pc)) { $queue.Enqueue($w) }
    while ($queue.Count -gt 0) {
        $w = $queue.Dequeue()
        [void]$list.Add($w)
        foreach ($c in $w.FindAll($Scope::Children, $wc)) { $queue.Enqueue($c) }   # owned windows nest
    }
    @($list)
}
# A screenshot of the whole screen plus every top-level TDPdf window, and a UIA dump of each.
function Step([string]$name) {
    Start-Sleep -Milliseconds 400
    Full "$name-screen"
    $i = 0
    foreach ($w in TopWindows $app) {
        $h = [IntPtr]$w.Current.NativeWindowHandle
        $r = VisibleRect $h
        Grab "$name-win$i" $r.L $r.T ($r.R - $r.L) ($r.B - $r.T)
        Dump "$name-win$i" $w
        $i++
    }
}
function FindIn($root, [string]$name, [int]$wait = 10) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($UIA::NameProperty, $name)
    for ($i = 0; $i -lt $wait * 2; $i++) {
        $e = $root.FindFirst($Scope::Descendants, $cond)
        if ($e) { return $e }
        Start-Sleep -Milliseconds 500
    }
    $null
}
# Searches every TDPdf top-level window (dialogs are separate top-levels).
function FindApp([string]$name, [int]$wait = 10) {
    for ($i = 0; $i -lt $wait * 2; $i++) {
        foreach ($w in TopWindows $app) {
            $e = FindIn $w $name 0
            if ($e) { return $e }
        }
        Start-Sleep -Milliseconds 500
    }
    $null
}
function WindowNamed([string]$name, [int]$wait = 15) {
    for ($i = 0; $i -lt $wait * 2; $i++) {
        foreach ($w in TopWindows $app) { if ($w.Current.Name -like $name) { return $w } }
        Start-Sleep -Milliseconds 500
    }
    $null
}
function Click([int]$x, [int]$y) {
    [W]::SetCursorPos($x, $y) | Out-Null
    Start-Sleep -Milliseconds 150
    [W]::mouse_event(0x2, 0, 0, 0, [UIntPtr]::Zero)
    [W]::mouse_event(0x4, 0, 0, 0, [UIntPtr]::Zero)
}
function ClickEl($el) {
    $r = $el.Current.BoundingRectangle
    Click ([int]($r.X + $r.Width / 2)) ([int]($r.Y + $r.Height / 2))
}
function InvokeEl($el) {
    $p = $null
    if ($el.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$p)) { $p.Invoke() } else { ClickEl $el }
}
# A button by its visible caption: TDPdf gives row buttons a longer accessible name, so find the
# caption TextBlock and walk up to the Button, then Invoke it (no mouse, so nothing on top of the
# window — a browser the sign-in opened — can swallow the click).
function Button($root, [string]$caption, [int]$wait = 10) {
    $t = FindIn $root $caption $wait
    if (-not $t) { return $null }
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $e = $t
    for ($i = 0; $i -lt 4 -and $e; $i++) {
        if ($e.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button) { return $e }
        $e = $walker.GetParent($e)
    }
    $t
}
function Press($root, [string]$caption, [int]$wait = 10) {
    $b = Button $root $caption $wait
    if (-not $b) { throw "button '$caption' not found" }
    InvokeEl $b
}
function Expand($el) { $el.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand() }
function Mock([string]$path) { Invoke-RestMethod "http://127.0.0.1:$MockPort$path" -TimeoutSec 10 }
function Attempt([string]$what, [scriptblock]$b) {
    try { & $b } catch { Fail $what ("exception: " + $_.Exception.Message); Full "exception-$what" }
}
# http.sys owns HttpListener sockets (PID 4), so Get-NetTCPConnection cannot attribute them to
# TDPdf; `netsh http show servicestate` lists each registered URL with the process that owns it.
function LoopbackRegistrations() {
    $txt = (netsh http show servicestate view=requestq verbose=no) -join "`n"
    Set-Content (Join-Path $Out ("netsh-" + (Get-Date -Format 'HHmmss') + ".txt")) $txt
    $blocks = $txt -split '(?=Request queue name:)'
    foreach ($b in $blocks) {
        if ($b -match "\b$($app.Id)\b") {
            [regex]::Matches($b, '(?i)HTTP://(127\.0\.0\.1|LOCALHOST):(\d+)/') | ForEach-Object { $_.Value }
        }
    }
}
# Proof that TDPdf itself is listening on $port: our loopback page answers a stray path with 404
# and the fixed text "There is nothing here." (http.sys's own 404 says "Not Found" from HTTPAPI).
function TdpdfAnswers([int]$port) {
    try {
        $r = Invoke-WebRequest "http://127.0.0.1:$port/favicon.ico" -TimeoutSec 5 -SkipHttpErrorCheck
        return ($r.StatusCode -eq 404 -and $r.Content -match 'There is nothing here')
    } catch { return $false }
}
function Listening([int]$port) {
    [bool](Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue)
}
function PortOf([string]$redirect) {
    $m = [regex]::Match([Uri]::UnescapeDataString("$redirect"), 'http://(127\.0\.0\.1|localhost):(\d+)/cb')
    if ($m.Success) { [int]$m.Groups[2].Value } else { 0 }
}
function BrowserCommandLines() {
    Get-CimInstance Win32_Process | Where-Object { $_.Name -in 'msedge.exe', 'chrome.exe', 'firefox.exe' } |
        ForEach-Object { $_.CommandLine } | Where-Object { $_ }
}

# ── Environment ─────────────────────────────────────────────────────────────────────────
$local = [Environment]::GetFolderPath('LocalApplicationData')
Remove-Item -Recurse -Force (Join-Path $local 'TDPdf') -ErrorAction SilentlyContinue
$pdf = Join-Path $Demo 'services-agreement.pdf'

function LaunchApp() {
    $p = Start-Process -FilePath $Exe -ArgumentList "`"$pdf`"" -PassThru
    $hw = [IntPtr]::Zero
    for ($i = 0; $i -lt 90 -and $hw -eq [IntPtr]::Zero; $i++) { Start-Sleep -Milliseconds 500; $p.Refresh(); $hw = $p.MainWindowHandle }
    if ($hw -ne [IntPtr]::Zero) {
        [W]::MoveWindow($hw, 40, 30, 1500, 950, $true) | Out-Null
        [W]::SetForegroundWindow($hw) | Out-Null
        Start-Sleep -Seconds 8
    }
    @{ P = $p; H = $hw }
}

function OpenConnectDialog([string]$prefix) {
    $m = $UIA::FromHandle($script:h)
    [W]::SetForegroundWindow($script:h) | Out-Null
    Expand (FindIn $m 'File')
    Start-Sleep -Seconds 1
    $es = FindIn $UIA::RootElement 'Electronic Signature' 5
    Check "$prefix File menu has Electronic Signature" ($null -ne $es)
    Expand $es
    Start-Sleep -Seconds 1
    Full "$prefix-esign-menu"
    InvokeEl (FindIn $UIA::RootElement 'Signature Requests…' 5)
    Start-Sleep -Seconds 3
    $d = WindowNamed 'Electronic Signature' 10
    Check "$prefix not connected → the Connect dialog opens" ($null -ne $d)
    $d
}

# Edge's address bar, read through UIA.
function EdgeAddress() {
    foreach ($e in Get-Process msedge -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero }) {
        $root = $UIA::FromHandle($e.MainWindowHandle)
        $ec = New-Object System.Windows.Automation.PropertyCondition($UIA::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)
        foreach ($ed in $root.FindAll($Scope::Descendants, $ec)) {
            $vp = $null
            if ($ed.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$vp)) {
                $v = $vp.Current.Value
                if ($v -match 'tdpdf\.com|127\.0\.0\.1') { return $v }
            }
        }
    }
    $null
}

# ── A. The LIVE service: sign.tdpdf.com, no account, stop at its sign-in page ───────────
Remove-Item Env:TDPDF_DOCUMENSO_BASEURL -ErrorAction SilentlyContinue
$script:failuresBeforeLive = $script:failures
$la = LaunchApp
$app = $la.P
$script:h = $la.H
Check 'LIVE: app launches with the demo PDF (default service sign.tdpdf.com)' ($h -ne [IntPtr]::Zero) "pid $($app.Id)"
Attempt 'live' {
    $d = OpenConnectDialog 'L01'
    Step 'L01-connect-dialog'
    Check 'LIVE: Connect dialog names sign.tdpdf.com' ($null -ne (FindIn $d 'Electronic Signature uses TDPdf Signing at sign.tdpdf.com. Sign in once and TDPdf can send documents for signature and let you sign them here.' 3))
    InvokeEl (FindIn $d 'Sign in with your browser' 5)
    $cl = @()
    for ($i = 0; $i -lt 40 -and $cl.Count -eq 0; $i++) { Start-Sleep -Milliseconds 500; $cl = @(BrowserCommandLines | Where-Object { $_ -match 'sign\.tdpdf\.com/connect/tdpdf' }) }
    if ($cl.Count -gt 0) { Log ("LIVE browser command line: " + $cl[0]) }
    Check 'LIVE: the capability probe saw sign-in as supported (browser opened; no "does not offer" fallback)' (
        $cl.Count -gt 0 -and $null -eq (FindIn $d 'sign.tdpdf.com does not offer browser sign-in. Use an API token instead (below).' 1))
    Check 'LIVE: the default browser was started at https://sign.tdpdf.com/connect/tdpdf?...' ($cl.Count -gt 0)
    $lport = 0
    if ($cl.Count -gt 0) {
        $u = [regex]::Match($cl[0], 'https://sign\.tdpdf\.com/connect/tdpdf\?[^\s"]+').Value
        $lport = PortOf ([regex]::Match($u, 'redirect_uri=([^&]+)').Groups[1].Value)
        Check 'LIVE: that URL carries code_challenge, S256, state and a loopback redirect_uri' (
            $u -match 'code_challenge=[A-Za-z0-9_-]{43}' -and $u -match 'code_challenge_method=S256' -and $u -match 'state=[A-Za-z0-9_-]{43}' -and $lport -gt 0) $u
    }
    $regs = @(LoopbackRegistrations)
    Log ("LIVE http.sys registrations attributed to TDPdf: " + ($regs -join ', '))
    Check "LIVE: port $lport is listening (http.sys) and the answer is TDPdf's loopback page" ((Listening $lport) -and (TdpdfAnswers $lport))
    $addr = $null
    for ($i = 0; $i -lt 40; $i++) { Start-Sleep -Milliseconds 500; $addr = EdgeAddress; if ($addr -match '/signin') { break } }
    Log "LIVE Edge address bar: $addr"
    Full 'L02-live-browser-signin'
    Check 'LIVE: the browser landed on sign.tdpdf.com/signin' ($addr -match 'sign\.tdpdf\.com/signin')
    if ($addr -match 'returnTo=([^&]+)') {
        $rt = [Uri]::UnescapeDataString($Matches[1])
        Log "LIVE returnTo (decoded): $rt"
        Check 'LIVE: returnTo preserves /connect/tdpdf with state, code_challenge and redirect_uri' (
            $rt -match '^/connect/tdpdf\?' -and $rt -match 'state=' -and $rt -match 'code_challenge=' -and $rt -match 'redirect_uri=')
    } else { Fail 'LIVE: returnTo present in the sign-in URL' "$addr" }

    [W]::SetForegroundWindow([IntPtr]$d.Current.NativeWindowHandle) | Out-Null
    Step 'L03-live-waiting'
    InvokeEl (FindIn $d 'Stop' 5)
    Start-Sleep -Seconds 2
    Check 'LIVE: Stop closes the listener (TDPdf no longer answers on that port)' (-not (TdpdfAnswers $lport)) "listening=$(Listening $lport)"
    Step 'L04-live-stopped'
    Check 'LIVE: nothing was stored' (-not (Test-Path (Join-Path $local 'TDPdf\documenso.cred')))
}
Get-Process msedge -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Get-Process TDPdf -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2

# ── B. The stand-in service: everything an account would unlock ─────────────────────────
# Started here, not in an earlier workflow step: Actions kills a step's processes when it ends.
$mock = Start-Process python -ArgumentList (Join-Path $PSScriptRoot 'mock_server.py'), "$MockPort" -PassThru `
    -RedirectStandardError (Join-Path $Out 'mock.err.txt') -RedirectStandardOutput (Join-Path $Out 'mock.out.txt')
for ($i = 0; $i -lt 30; $i++) { try { Invoke-RestMethod "http://127.0.0.1:$MockPort/api/health" -TimeoutSec 2 | Out-Null; break } catch { Start-Sleep 1 } }
$base = "http://127.0.0.1:$MockPort"
$env:TDPDF_DOCUMENSO_BASEURL = $base

$assoc = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice' -ErrorAction SilentlyContinue).ProgId
Log "default https handler: $assoc"
$wv = Get-ItemProperty 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}' -ErrorAction SilentlyContinue
Log "WebView2 Runtime on runner: $($wv.pv)"

# ── 1. Launch ───────────────────────────────────────────────────────────────────────────
$la = LaunchApp
$app = $la.P
$script:h = $la.H
Check 'app launches with the demo PDF' ($h -ne [IntPtr]::Zero) "pid $($app.Id)"
if ($h -eq [IntPtr]::Zero) { exit 1 }
Step '01-launched'

# ── 2. File ▸ Electronic Signature ▸ Signature Requests… → Connect ─────────────────────
Attempt 'open-connect' {
    $script:dlg = OpenConnectDialog '02'
    Step '02-connect-dialog'
    Check 'Connect dialog: "Sign in with your browser" button' ($null -ne (FindIn $dlg 'Sign in with your browser' 2))
    $adv = FindIn $dlg 'Advanced: use an API token instead  ▸' 2
    Check 'Connect dialog: Advanced toggle present, collapsed' ($null -ne $adv)
    Check 'Connect dialog: token box hidden until Advanced' ($null -eq (FindIn $dlg 'API token' 1))
    if ($adv) { InvokeEl $adv; Start-Sleep -Seconds 1 }
    Step '03-connect-advanced'
    Check 'Advanced: the paste-a-token fallback renders (API token box, Paste, Connect)' (
        ($null -ne (FindIn $dlg 'API token' 2)) -and ($null -ne (FindIn $dlg 'Paste' 2)) -and ($null -ne (FindIn $dlg 'Connect' 2)))
    $adv2 = FindIn $dlg 'Advanced: use an API token instead  ▾' 2
    if ($adv2) { InvokeEl $adv2; Start-Sleep -Milliseconds 500 }
}

# ── 3. Browser sign-in: listener binds, browser opens the authorize URL; then Stop ─────
Attempt 'signin-hold' {
    Mock '/_mode?m=hold' | Out-Null
    InvokeEl (FindIn $dlg 'Sign in with your browser' 5)

    $auth = $null
    for ($i = 0; $i -lt 40 -and -not $auth; $i++) { Start-Sleep -Milliseconds 500; $s = Mock '/_state'; if ($s.authorize.Count -gt 0) { $auth = $s.authorize[-1] } }
    Check 'sign-in: the default browser opened /connect/tdpdf' ($null -ne $auth)
    $script:port = if ($auth) { PortOf $auth.redirect_uri } else { 0 }
    Check "sign-in: port $port is listening and answers as TDPdf's loopback page" ((Listening $port) -and (TdpdfAnswers $port))
    $bound = @(Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue | ForEach-Object { $_.LocalAddress })
    Log ("listen addresses on port ${port}: " + ($bound -join ', '))
    $regs = @(LoopbackRegistrations)
    Log ("http.sys registrations attributed to TDPdf: " + ($regs -join ', '))
    Check 'sign-in: the http.sys URL registration is loopback-only (127.0.0.1/localhost, never + or *)' (
        $regs.Count -gt 0 -and -not ($regs | Where-Object { $_ -notmatch '(?i)127\.0\.0\.1|localhost' })) ($regs -join ', ')
    if ($auth) {
        Log ("authorize query: " + ($auth | ConvertTo-Json -Compress))
        Check 'authorize URL: code_challenge is 43 base64url chars, S256' ($auth.code_challenge -match '^[A-Za-z0-9_-]{43}$' -and $auth.code_challenge_method -eq 'S256')
        Check 'authorize URL: state present' ($auth.state -match '^[A-Za-z0-9_-]{43}$')
        Check 'authorize URL: redirect_uri is the loopback listener' ($auth.redirect_uri -match "^http://(127\.0\.0\.1|localhost):$port/cb$") $auth.redirect_uri
        Check 'authorize URL: no verifier and no token in it' (-not ($auth.PSObject.Properties.Name -match 'verifier|token'))
    }
    $cl = @(BrowserCommandLines | Where-Object { $_ -match 'connect/tdpdf' })
    Log ("browser command lines with connect/tdpdf: " + $cl.Count)
    if ($cl.Count -gt 0) { Log ("  " + $cl[0]) }
    Check 'sign-in: a browser process was started with the /connect/tdpdf URL' ($cl.Count -gt 0)

    [W]::SetForegroundWindow([IntPtr]$dlg.Current.NativeWindowHandle) | Out-Null
    Step '04-signin-waiting'
    Check 'sign-in: the dialog says it is waiting for the browser' ($null -ne (FindIn $dlg 'Waiting for you to finish signing in in your browser…  (Stop to cancel)' 3))

    InvokeEl (FindIn $dlg 'Stop' 5)
    Start-Sleep -Seconds 2
    Check 'Stop: TDPdf no longer answers on that port' (-not (TdpdfAnswers $port)) "listening=$(Listening $port)"
    $after = @(LoopbackRegistrations)
    Check 'Stop: no http.sys registration attributed to TDPdf remains' ($after.Count -eq 0) ($after -join ', ')
    Step '05-signin-stopped'
    Check 'Stop: the dialog says nothing was stored' ($null -ne (FindIn $dlg 'Sign-in stopped. Nothing was stored.' 3))
    Check 'Stop: no credential file was written' (-not (Test-Path (Join-Path $local 'TDPdf\documenso.cred')))
}

# ── 4. Browser sign-in, end to end ─────────────────────────────────────────────────────
Attempt 'signin-allow' {
    Mock '/_mode?m=allow' | Out-Null
    InvokeEl (FindIn $dlg 'Sign in with your browser' 5)
    $cred = Join-Path $local 'TDPdf\documenso.cred'
    for ($i = 0; $i -lt 60 -and -not (Test-Path $cred); $i++) { Start-Sleep -Milliseconds 500 }
    Start-Sleep -Seconds 2
    $s = Mock '/_state'
    Log ("exchanges: " + ($s.exchanges | ConvertTo-Json -Compress))
    Check 'end to end: the code was exchanged with a verifier matching the challenge' ($s.exchanges.Count -ge 1 -and $s.exchanges[-1].pkceOk)
    Check 'end to end: the exchange body is exactly {code, code_verifier}, unauthenticated' (
        $s.exchanges.Count -ge 1 -and (($s.exchanges[-1].keys -join ',') -eq 'code,code_verifier') -and -not $s.exchanges[-1].authorization)
    Check 'end to end: the token was probed (GET /api/v2/envelope) before storing' ($s.probes -ge 1) "probes=$($s.probes)"
    Check 'end to end: the credential was stored (DPAPI file)' (Test-Path $cred)
    if (Test-Path $cred) {
        $txt = Get-Content -Raw $cred
        Check 'the stored file does not contain the token in clear' (-not $txt.Contains('api_SMOKEtoken'))
    }
    Get-Process msedge -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Step '06-connected'
}

# ── 5. Signature Requests and the in-app signing window ────────────────────────────────
Attempt 'requests' {
    $script:rw = WindowNamed 'Signature Requests' 15
    Check 'Signature Requests opens after connecting' ($null -ne $rw)
    Start-Sleep -Seconds 3
    Step '07-requests'
    Check 'Signature Requests lists the request' ($null -ne (FindIn $rw 'Smoke test agreement' 5))
    foreach ($n in 'Open', 'Audit log & certificate', 'Resend reminder', 'Sign on this computer', 'Copy signing link') {
        Check "Signature Requests row offers '$n'" ($null -ne (FindIn $rw $n 2))
    }
}

Attempt 'reminder' {
    Press $rw 'Resend reminder'
    Start-Sleep -Seconds 3
    $s = Mock '/_state'
    Log ("redistribute: " + ($s.redistribute | ConvertTo-Json -Compress))
    Check 'Resend reminder: POST /envelope/redistribute for the signer only (not the CC)' (
        $s.redistribute.Count -ge 1 -and (($s.redistribute[-1].recipients -join ',') -eq '11') -and $s.redistribute[-1].envelopeId -eq 'envelope_smoke')
    Step '08-reminder'
}

Attempt 'webview-open' {
    Press $rw 'Open'
    $script:ww = WindowNamed 'Smoke test agreement' 20
    Check 'Open: the in-app signing window opens (WebView2 Runtime detected)' ($null -ne $ww)
    $loader = Get-ChildItem (Join-Path $local 'TDPdf\webview2-loader') -Recurse -Filter WebView2Loader.dll -ErrorAction SilentlyContinue | Select-Object -First 1
    Check 'WebView2Loader.dll was extracted to %LOCALAPPDATA%\TDPdf\webview2-loader\<version>' ($null -ne $loader) "$($loader.FullName)"
    $loaded = (Get-Process -Id $app.Id).Modules | Where-Object { $_.ModuleName -eq 'WebView2Loader.dll' }
    Check 'the extracted loader is the one loaded into TDPdf' ($loaded -and $loaded[0].FileName -eq $loader.FullName) "$($loaded[0].FileName)"
    Check 'the browser profile is %LOCALAPPDATA%\TDPdf\WebView2' (Test-Path (Join-Path $local 'TDPdf\WebView2\EBWebView'))
    $page = FindIn $ww 'Smoke request page' 20
    Check 'the request page rendered inside TDPdf' ($null -ne $page)
    Step '09-webview-request'

    # The page tries to go to https://example.com after 2.5 s: handed to the default browser.
    Start-Sleep -Seconds 6
    $ext = @(BrowserCommandLines | Where-Object { $_ -match 'example\.com/smoke-offsite' })
    Check 'off-site navigation is handed to the default browser' ($ext.Count -gt 0)
    Check '…and the in-app window stays on the service' ($null -ne (FindIn $ww 'Smoke request page' 3))
    $addr = FindIn $ww "127.0.0.1:$MockPort/t/smoke-team/documents/envelope_smoke" 3
    Check 'the address line shows the service page' ($null -ne $addr)
    Step '10-webview-after-offsite'
}

Attempt 'webview-logs' {
    [W]::SetForegroundWindow([IntPtr]$rw.Current.NativeWindowHandle) | Out-Null
    Press $rw 'Audit log & certificate'
    Start-Sleep -Seconds 4
    $lw = WindowNamed 'Audit log*' 10
    Check 'Audit log & certificate reuses the single window and shows the logs page' ($null -ne $lw -and $null -ne (FindIn $lw 'Audit log page' 15))
    Check 'there is still only one signing window' (@(TopWindows $app | Where-Object { $_.Current.Name -like 'Smoke test agreement' -or $_.Current.Name -like 'Audit log*' -or $_.Current.Name -like 'Sign —*' }).Count -eq 1)
    Step '11-webview-logs'
}

Attempt 'webview-sign' {
    [W]::SetForegroundWindow([IntPtr]$rw.Current.NativeWindowHandle) | Out-Null
    Press $rw 'Sign on this computer'
    $sw = WindowNamed 'Sign —*' 10
    Check 'Sign on this computer opens the signing page in the window' ($null -ne $sw -and $null -ne (FindIn $sw 'Smoke signing page' 15))
    $addrOk = $null -ne (FindIn $sw "127.0.0.1:$MockPort/sign/…" 5)
    Check 'the address line hides the signing token' $addrOk
    Step '12-webview-signing'
    $dl = $null
    for ($i = 0; $i -lt 40 -and -not $dl; $i++) { Start-Sleep -Milliseconds 500; $dl = FindIn $sw 'Download signed copy…' 0 }
    Check 'completion detected (/complete page + status) → Download signed copy offered' ($null -ne $dl)
    Step '13-webview-completed'
}

Attempt 'shutdown' {
    $s = Mock '/_state'
    Log ("pages served: " + ($s.pages -join ' '))
    Check 'no page off the service was loaded in the window' (-not ($s.pages | Where-Object { $_ -match 'offsite' }))
    $tok = 'api_SMOKEtoken0123456789abcdef'
    $leak = Get-ChildItem (Join-Path $local 'TDPdf') -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\\WebView2\\' -and $_.Length -lt 5MB } |
        Where-Object { (Get-Content -Raw $_.FullName -ErrorAction SilentlyContinue) -match [regex]::Escape($tok) }
    Check 'the token is in no file TDPdf wrote outside the browser profile' (-not $leak) ($leak.FullName -join ', ')
}

Get-Process TDPdf -ErrorAction SilentlyContinue | Stop-Process -Force
if ($mock) { Stop-Process -Id $mock.Id -Force -ErrorAction SilentlyContinue }
Log ""
Log $(if ($failures -eq 0) { 'SMOKE: ALL PASS' } else { "SMOKE: $failures FAILURE(S)" })
exit $(if ($failures -eq 0) { 0 } else { 1 })
