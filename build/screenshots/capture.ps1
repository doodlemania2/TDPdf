# Takes Store / tdpdf.com screenshots of a released TDPdf build on a Windows runner with an
# interactive desktop (the fleet's tdp-windows-capture machine, or GitHub-hosted windows-2022).
# Drives the app through UI Automation, mouse and keyboard input only, so every image is the real
# app, not a mock-up. Each shot is attempted independently: one that fails is logged and the rest
# still run.
#
# Every published picture holds TDPdf's own windows and nothing else: the main window, plus any
# dialog or menu it has open, cut out onto a transparent background. The desktop, the taskbar and
# its clock never appear. A full-screen grab is taken only when a shot fails, and it goes to -DebugOut,
# which is uploaded separately and never published.
#
# The install shots depend on the machine. On a machine with TDPdf installed for all users (the
# capture VM, where Intune manages it) only the "managed" prompt can be shown, so that is the one
# taken; the script never installs anything there. On a clean machine (a hosted runner) it takes the
# first-run "Install TDPdf" prompt, installs for the current user, and takes the Reinstall prompt.
param(
    [string]$Exe,
    [string]$Demo,
    [string]$Out,
    [string]$DebugOut = (Join-Path $Out '..\shots-debug'),
    # Names of the shots to take; empty takes them all.
    [string[]]$Shots = @()
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $Out, $DebugOut | Out-Null

Add-Type -AssemblyName System.Drawing, System.Windows.Forms, UIAutomationClient, UIAutomationTypes
Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class W {
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hgt, bool r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint x, uint y, uint d, UIntPtr e);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT r, int s);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out int v, int s);
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc f, IntPtr l);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }

  // Visible, uncloaked top-level windows of one process, topmost first.
  public static List<IntPtr> Windows(uint pid) {
    var list = new List<IntPtr>();
    EnumWindows((h, l) => {
      uint p; GetWindowThreadProcessId(h, out p);
      if (p != pid || !IsWindowVisible(h)) return true;
      int cloaked; // DWMWA_CLOAKED = 14
      if (DwmGetWindowAttribute(h, 14, out cloaked, 4) == 0 && cloaked != 0) return true;
      list.Add(h);
      return true;
    }, IntPtr.Zero);
    return list;
  }
}
"@
$UIA = [System.Windows.Automation.AutomationElement]
$Scope = [System.Windows.Automation.TreeScope]

# TDPdf's default theme follows Windows; the shot list wants Dark.
$pk = 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize'
New-Item -Force -Path $pk | Out-Null
Set-ItemProperty $pk AppsUseLightTheme 0 -Type DWord
Set-ItemProperty $pk SystemUsesLightTheme 0 -Type DWord

function VisibleRect([IntPtr]$h) {
    # DWMWA_EXTENDED_FRAME_BOUNDS = 9: the window as drawn, without the invisible resize border.
    $r = New-Object W+RECT
    if ([W]::DwmGetWindowAttribute($h, 9, [ref]$r, 16) -ne 0) { [W]::GetWindowRect($h, [ref]$r) | Out-Null }
    $r
}

# Saves TDPdf's open windows as one picture: each window's pixels copied from the screen into a
# transparent canvas the size of their combined bounds. Anything between or around them (desktop,
# taskbar) stays transparent.
function Shot([string]$name, $proc) {
    $screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $rects = foreach ($h in [W]::Windows([uint32]$proc.Id)) {
        $r = VisibleRect $h
        $l = [Math]::Max($r.L, $screen.Left); $t = [Math]::Max($r.T, $screen.Top)
        $rr = [Math]::Min($r.R, $screen.Right); $b = [Math]::Min($r.B, $screen.Bottom)
        if ($rr - $l -ge 8 -and $b - $t -ge 8) { [System.Drawing.Rectangle]::FromLTRB($l, $t, $rr, $b) }
    }
    if (-not $rects) { throw "no visible TDPdf window for $name" }
    $u = $rects[0]
    foreach ($r in $rects) { $u = [System.Drawing.Rectangle]::Union($u, $r) }
    $bmp = New-Object System.Drawing.Bitmap $u.Width, $u.Height, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::Transparent)
    foreach ($r in $rects) {
        $g.CopyFromScreen($r.X, $r.Y, $r.X - $u.X, $r.Y - $u.Y, $r.Size)
    }
    $bmp.Save((Join-Path $Out "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Host "captured $name $($u.Width)x$($u.Height) from $(@($rects).Count) window(s)"
    DumpProcess $name $proc
}

# Debug only: the whole screen, saved outside the published folder.
function Full([string]$name) {
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen(0, 0, 0, 0, $bmp.Size)
    $bmp.Save((Join-Path $DebugOut "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
}

# The UI Automation text of every TDPdf window, next to the picture, for the site's safety scan.
function DumpProcess([string]$name, $proc) {
    $pc = New-Object System.Windows.Automation.PropertyCondition($UIA::ProcessIdProperty, $proc.Id)
    $i = 0
    foreach ($top in @($UIA::RootElement.FindAll($Scope::Children, $pc))) {
        $all = $top.FindAll($Scope::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
        $lines = foreach ($e in $all) {
            $c = $e.Current
            $br = $c.BoundingRectangle
            $pos = if ($br.IsEmpty -or [double]::IsInfinity($br.X)) { '-' } else { '{0},{1},{2},{3}' -f [int]$br.X, [int]$br.Y, [int]$br.Width, [int]$br.Height }
            "{0}`t{1}`t{2}`t{3}`t{4}" -f $c.ControlType.ProgrammaticName, $c.AutomationId, $c.Name, $c.ClassName, $pos
        }
        $lines | Set-Content (Join-Path $Out "$name-$i.uia.txt")
        $i++
    }
}

function Find($root, [string[]]$names, [int]$wait = 10) {
    for ($i = 0; $i -lt $wait * 2; $i++) {
        foreach ($n in $names) {
            $cond = New-Object System.Windows.Automation.PropertyCondition($UIA::NameProperty, $n)
            $e = $root.FindFirst($Scope::Descendants, $cond)
            if ($e) { return $e }
        }
        Start-Sleep -Milliseconds 500
    }
    throw "element '$($names -join "' / '")' not found"
}

function FindId($root, [string]$id) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($UIA::AutomationIdProperty, $id)
    $e = $root.FindFirst($Scope::Descendants, $cond)
    if (-not $e) { throw "automation id '$id' not found" }
    $e
}

function Click([int]$x, [int]$y, [switch]$Double, [switch]$Right) {
    [W]::SetCursorPos($x, $y) | Out-Null
    Start-Sleep -Milliseconds 150
    $down, $up = if ($Right) { 0x8, 0x10 } else { 0x2, 0x4 }
    $n = if ($Double) { 2 } else { 1 }
    for ($i = 0; $i -lt $n; $i++) {
        [W]::mouse_event($down, 0, 0, 0, [UIntPtr]::Zero)
        [W]::mouse_event($up, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 60
    }
}

function ClickEl($el, [switch]$Double, [switch]$Right) {
    $r = $el.Current.BoundingRectangle
    Click ([int]($r.X + $r.Width / 2)) ([int]($r.Y + $r.Height / 2)) -Double:$Double -Right:$Right
}

function InvokeEl($el) {
    $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

function Expand($el) {
    $el.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
}

function Keys([string]$k) { [System.Windows.Forms.SendKeys]::SendWait($k); Start-Sleep -Milliseconds 400 }

# Starts a drag and leaves the button down, so a shot can catch a live readout (Measure).
function DragTo([int]$x1, [int]$y1, [int]$x2, [int]$y2, [switch]$Hold) {
    [W]::SetCursorPos($x1, $y1) | Out-Null
    Start-Sleep -Milliseconds 120
    [W]::mouse_event(0x2, 0, 0, 0, [UIntPtr]::Zero)
    for ($s = 1; $s -le 12; $s++) {
        [W]::SetCursorPos([int]($x1 + ($x2 - $x1) * $s / 12), [int]($y1 + ($y2 - $y1) * $s / 12)) | Out-Null
        Start-Sleep -Milliseconds 25
    }
    if (-not $Hold) { [W]::mouse_event(0x4, 0, 0, 0, [UIntPtr]::Zero) }
    Start-Sleep -Milliseconds 300
}
function Release { [W]::mouse_event(0x4, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 300 }

function MainWindow($proc) {
    for ($i = 0; $i -lt 60; $i++) {
        $proc.Refresh()
        if ($proc.MainWindowHandle -ne [IntPtr]::Zero) { return $proc.MainWindowHandle }
        Start-Sleep -Milliseconds 500
    }
    throw "TDPdf main window never appeared"
}

function Wanted([string]$what) { $Shots.Count -eq 0 -or $Shots -contains $what }

function Attempt([string]$what, [scriptblock]$b) {
    if (-not (Wanted $what)) { return }
    try { & $b } catch { Write-Host "FAILED $what : $($_.Exception.Message)"; Full "failed-$what" }
    # Leave the app as the next shot expects it: no menu, dialog or tool left open.
    try { Settle } catch { }
}

function Launch([string]$file) {
    $p = Start-Process -FilePath $Exe -ArgumentList "`"$file`"" -PassThru
    $h = MainWindow $p
    [W]::MoveWindow($h, 40, 30, 1600, 1000, $true) | Out-Null
    [W]::SetForegroundWindow($h) | Out-Null
    Start-Sleep -Seconds 10
    @{ P = $p; H = $h; Root = $UIA::FromHandle($h) }
}

function Front { [W]::SetForegroundWindow($app.H) | Out-Null; Start-Sleep -Milliseconds 400 }

# Pointer off the window, so no hover highlight or tooltip lands in a shot.
function Park { [W]::SetCursorPos(1900, 1070) | Out-Null; Start-Sleep -Milliseconds 800 }

function Settle {
    Park
    for ($i = 0; $i -lt 3; $i++) {
        if (@([W]::Windows([uint32]$app.P.Id)).Count -le 1) { break }
        Keys '{ESC}'
    }
    Front
    InvokeEl (FindId $app.Root 'ToolSelectBtn')
}

function Tab([string]$file) { ClickEl (Find $app.Root (Split-Path $file -Leaf)); Start-Sleep -Seconds 2 }

# Opens a menu path from the main menu bar, for example 'File', 'Electronic Signature'.
function MenuPath([string[]]$path) {
    Front
    Expand (Find $app.Root $path[0])
    Start-Sleep -Milliseconds 800
    for ($i = 1; $i -lt $path.Count; $i++) {
        $item = Find $UIA::RootElement @($path[$i], "$($path[$i])...", "$($path[$i])…")
        if ($i -lt $path.Count - 1) { Expand $item } else { return $item }
        Start-Sleep -Milliseconds 800
    }
}

# A point on the rendered page, as fractions of the page image (so it survives tab-strip
# and zoom changes). At the default zoom about the top 46% of a page is on screen.
function PagePoint([double]$fx, [double]$fy) {
    $r = (FindId $app.Root 'PageImage').Current.BoundingRectangle
    @([int]($r.X + $r.Width * $fx), [int]($r.Y + $r.Height * $fy))
}
function PageClick([double]$fx, [double]$fy, [switch]$Double) { $p = PagePoint $fx $fy; Click $p[0] $p[1] -Double:$Double }
function PageDrag([double]$x1, [double]$y1, [double]$x2, [double]$y2, [switch]$Hold) {
    $a = PagePoint $x1 $y1; $z = PagePoint $x2 $y2; DragTo $a[0] $a[1] $z[0] $z[1] -Hold:$Hold
}

$agreement = Join-Path $Demo 'services-agreement.pdf'
$incident  = Join-Path $Demo 'incident-report.pdf'
$form      = Join-Path $Demo 'membership-form.pdf'
$managed   = (Get-ItemProperty 'HKLM:\SOFTWARE\TDPdf' -ErrorAction SilentlyContinue).Installed -eq 1
Write-Host "TDPdf installed for all users on this machine: $managed"

$app = Launch $agreement

# ── edit-text: edit "1.5%" in section 2 ─────────────────────────────────────────────
# Measured on the first capture: "1.5%" sits at 61.4% across, 33.7% down the page image.
Attempt 'edit-text' {
    InvokeEl (FindId $app.Root 'ToolEditTextBtn')
    Start-Sleep -Milliseconds 500
    PageClick 0.614 0.337
    Start-Sleep -Seconds 2
    Shot 'edit-text-open' $app.P
    # Retype the figure in whatever editor opened.
    $cond = New-Object System.Windows.Automation.PropertyCondition($UIA::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)
    foreach ($ed in $app.Root.FindAll($Scope::Descendants, $cond)) {
        $vp = $null
        if ($ed.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$vp)) {
            $v = $vp.Current.Value
            if ($v -like '*1.5%*') { $vp.SetValue($v.Replace('1.5%', '1%')); Write-Host "edited: $v" }
        }
    }
    Start-Sleep -Seconds 1
    Shot 'edit-text' $app.P
    Keys '{ESC}'
}

# ── the other demo documents, as tabs ───────────────────────────────────────────────
foreach ($f in $incident, $form) {
    Start-Process -FilePath $Exe -ArgumentList "`"$f`"" | Out-Null
    Start-Sleep -Seconds 8
}
Front

# ── search: every "Jordan Ellis" in the incident report ───────────────────────────
Attempt 'search' {
    Tab $incident
    Keys '^f'
    Keys 'Jordan Ellis'
    Keys '{ENTER}'
    Start-Sleep -Seconds 2
    Park
    Shot 'search' $app.P
}

# ── redact: the Redact tool, dragged over the names and phone numbers ─────────────
# Boxes are fractions of the page image, measured on an earlier capture of incident-report.pdf.
# ("Redact matches" is not used here: on this file it marks whole lines, filed as an issue.)
Attempt 'redact' {
    Tab $incident
    InvokeEl (FindId $app.Root 'ToolRedactBtn')
    Start-Sleep -Seconds 1
    $boxes = @(
        @(0.2970, 0.1853, 0.5022, 0.2011),   # "Jordan Ellis. Jordan Ellis" in the first paragraph
        @(0.1224, 0.2725, 0.2178, 0.2907),   # table: Jordan Ellis
        @(0.5990, 0.2725, 0.7236, 0.2907),   # table: phone
        @(0.5990, 0.3008, 0.7236, 0.3178),   # table: phone
        @(0.5990, 0.3280, 0.7236, 0.3450),   # table: phone
        @(0.7544, 0.3688, 0.8556, 0.3830)    # "Jordan Ellis" in the last paragraph
    )
    foreach ($b in $boxes) { PageDrag $b[0] $b[1] $b[2] $b[3] }
    Park
    Start-Sleep -Seconds 1
    Shot 'redact' $app.P
}

# ── form-fill: type into the membership form ───────────────────────────────────────
# Field centres as fractions of the page, from build/screenshots/make-form-demo.py.
Attempt 'form-fill' {
    Tab $form
    PageClick 0.500 0.229; Keys 'Avery Quinn'
    PageClick 0.297 0.294; Keys '{(}555{)} 014-0142'   # SendKeys reads ( ) as grouping
    PageClick 0.703 0.294; Keys '1 March 2027'
    PageClick 0.297 0.360; Keys 'B-12, near the shed'
    PageClick 0.129 0.415
    Park
    Start-Sleep -Seconds 1
    Shot 'form-fill' $app.P
}

# ── hero: the agreement tab, every tab showing ─────────────────────────────────────
Attempt 'hero' {
    Tab $agreement
    Park
    Shot 'hero' $app.P
}

# ── page-sidebar: the right-click menu on a page thumbnail ─────────────────────────
Attempt 'page-sidebar' {
    Tab $agreement
    $list = FindId $app.Root 'PageList'
    $item = $list.FindFirst($Scope::Children, [System.Windows.Automation.Condition]::TrueCondition)
    ClickEl $item -Right
    Start-Sleep -Seconds 1
    Shot 'page-sidebar' $app.P
}

# ── markup: highlight, underline, a shape, ink and a text box on the agreement ─────
# Line positions as fractions of the page: section 1 text at 0.220, section 2 heading at 0.282,
# section 3 text at 0.408; text starts at 0.116 across.
Attempt 'markup' {
    Tab $agreement
    InvokeEl (FindId $app.Root 'ToolHighlightBtn'); Start-Sleep -Milliseconds 400
    PageDrag 0.117 0.220 0.600 0.220
    InvokeEl (FindId $app.Root 'ToolUnderlineBtn'); Start-Sleep -Milliseconds 400
    PageDrag 0.117 0.408 0.520 0.408
    InvokeEl (FindId $app.Root 'ToolShapeBtn'); Start-Sleep -Milliseconds 400
    PageDrag 0.105 0.268 0.375 0.298
    InvokeEl (FindId $app.Root 'ToolDrawBtn'); Start-Sleep -Milliseconds 400
    PageDrag 0.050 0.300 0.065 0.315
    PageDrag 0.065 0.315 0.090 0.280
    InvokeEl (FindId $app.Root 'ToolTextBtn'); Start-Sleep -Milliseconds 400
    PageClick 0.600 0.100
    Start-Sleep -Milliseconds 600
    Keys 'Approved, ready to sign'
    InvokeEl (FindId $app.Root 'ToolSelectBtn')
    Park
    Start-Sleep -Seconds 1
    Shot 'markup' $app.P
}

# ── measure: the live readout while dragging across the title ─────────────────────
Attempt 'measure' {
    Tab $agreement
    InvokeEl (FindId $app.Root 'ToolMeasureBtn'); Start-Sleep -Milliseconds 400
    PageDrag 0.117 0.150 0.430 0.150 -Hold
    Start-Sleep -Seconds 1
    try { Shot 'measure' $app.P } finally { Release }
}

# ── crop: the crop box on the page ────────────────────────────────────────────────
Attempt 'crop' {
    Tab $agreement
    InvokeEl (FindId $app.Root 'ToolCropBtn')
    Start-Sleep -Seconds 1
    Park
    Shot 'crop' $app.P
}

# ── menus and windows opened from the menu bar ────────────────────────────────────
Attempt 'esign-menu' {
    Tab $agreement
    MenuPath 'File', 'Electronic Signature', 'Send for Signature' | Out-Null
    Park
    Shot 'esign-menu' $app.P
}

# Without a connected account, Send for Signature opens the Connect TDPdf Signing window.
Attempt 'esign-connect' {
    Tab $agreement
    InvokeEl (MenuPath 'File', 'Electronic Signature', 'Send for Signature')
    Start-Sleep -Seconds 4
    Park
    Shot 'esign-connect' $app.P
}

Attempt 'ocr' {
    Tab $incident
    Expand (MenuPath 'Tools', 'OCR')
    Start-Sleep -Seconds 1
    Park
    Shot 'ocr' $app.P
}

Attempt 'transform' {
    Tab $agreement
    InvokeEl (MenuPath 'Tools', 'Transform Page')
    Start-Sleep -Seconds 3
    Park
    Shot 'transform' $app.P
}

Attempt 'export-images' {
    Tab $agreement
    InvokeEl (MenuPath 'File', 'Export Pages as Images')
    Start-Sleep -Seconds 3
    Park
    Shot 'export-images' $app.P
}

Attempt 'print-preview' {
    Tab $agreement
    Keys '^p'
    Start-Sleep -Seconds 5
    Park
    Shot 'print-preview' $app.P
}

Attempt 'keyboard-map' {
    Tab $agreement
    Keys '{F1}'
    Start-Sleep -Seconds 2
    Park
    Shot 'keyboard-map' $app.P
}

# ── form-build: the Form tool's field box on the agreement ────────────────────────
Attempt 'form-build' {
    Tab $agreement
    InvokeEl (FindId $app.Root 'ToolFormBtn'); Start-Sleep -Milliseconds 400
    PageDrag 0.600 0.120 0.880 0.140
    Start-Sleep -Seconds 1
    Park
    Shot 'form-build' $app.P
}

# ── install: what the status-bar install button offers ────────────────────────────
function InstallPrompt([string]$name) {
    InvokeEl (FindId $app.Root 'InstallBtn')
    Start-Sleep -Seconds 3
    Park
    Shot $name $app.P
}

if ($managed) {
    # Never install or uninstall here: the machine-wide copy belongs to Intune.
    Attempt 'install' { InstallPrompt 'install-managed' }
} else {
    Attempt 'install' {
        InstallPrompt 'install-fresh'
        Keys '{ESC}'
        Get-Process TDPdf -ErrorAction SilentlyContinue | Stop-Process -Force
        Start-Sleep -Seconds 2
        $i = Start-Process -FilePath $Exe -ArgumentList '/install', '/silent' -PassThru -Wait
        Write-Host "silent install exit code $($i.ExitCode)"
        $script:app = Launch $agreement
        InstallPrompt 'install'
    }
}

Get-Process TDPdf -ErrorAction SilentlyContinue | Stop-Process -Force
