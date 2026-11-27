# Takes Store / tdpdf.com screenshots of a released TDPdf build on a Windows runner with an
# interactive desktop (the fleet's tdp-windows-capture machine, or GitHub-hosted windows-2022).
# Drives the app through UI Automation, mouse and keyboard input only, so every image is the real
# app, not a mock-up. Each shot is attempted independently: one that fails is logged and the rest
# still run.
#
# Every published picture holds TDPdf's own windows and nothing else: the main window, plus any
# dialog or menu it has open, cut out onto a transparent background. The desktop, the taskbar and
# its clock never appear. Each window's own pixels come from PrintWindow, not from the screen, so
# nothing drawn over the desktop lands in a picture (the "Activate Windows" watermark of an
# unactivated capture machine, for one); a window falls back to a screen copy only when PrintWindow
# fails or returns a blank image, and the log says which method each window used. A full-screen
# grab is taken only when a shot fails, and it goes to -DebugOut, which is uploaded separately and
# never published.
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
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT r, int s);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out int v, int s);
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc f, IntPtr l);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }

  // Mouse wheel: positive notches scroll up, negative scroll down, at the point (x, y).
  public static void Wheel(int x, int y, int notches) {
    SetCursorPos(x, y);
    mouse_event(0x0800, 0, 0, unchecked((uint)(120 * notches)), UIntPtr.Zero);
  }

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
# PowerShell 7 forwards System.Drawing to System.Drawing.Common / .Primitives, so reference the
# assemblies that actually hold Bitmap and Rectangle.
$drawingRefs = @([System.Drawing.Bitmap].Assembly.Location, [System.Drawing.Rectangle].Assembly.Location) | Select-Object -Unique
Add-Type -ReferencedAssemblies $drawingRefs @"
using System;
using System.Drawing;
using System.Drawing.Imaging;
public static class T {
  // The box of a 24bpp bitmap left after peeling off edge rows and columns that are entirely pure
  // black (0,0,0): what PrintWindow gives for the transparent shadow margin of a layered window.
  // Returns {left, top, right, bottom}, right and bottom exclusive. Black inside the window stays.
  public static int[] Trim(Bitmap b) {
    int w = b.Width, h = b.Height;
    var d = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
    try {
      int stride = d.Stride;
      var px = new byte[Math.Abs(stride) * h];
      System.Runtime.InteropServices.Marshal.Copy(d.Scan0, px, 0, px.Length);
      Func<int, bool> rowBlack = y => { for (int x = 0; x < w * 3; x++) if (px[y * stride + x] != 0) return false; return true; };
      Func<int, bool> colBlack = x => { for (int y = 0; y < h; y++) { int i = y * stride + x * 3; if (px[i] != 0 || px[i + 1] != 0 || px[i + 2] != 0) return false; } return true; };
      int t = 0, bt = h, l = 0, r = w;
      while (t < bt - 1 && rowBlack(t)) t++;
      while (bt - 1 > t && rowBlack(bt - 1)) bt--;
      while (l < r - 1 && colBlack(l)) l++;
      while (r - 1 > l && colBlack(r - 1)) r--;
      return new[] { l, t, r, bt };
    } finally { b.UnlockBits(d); }
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

# A window's own pixels through PrintWindow (PW_RENDERFULLCONTENT = 2), as a bitmap the size of the
# window's full rectangle (GetWindowRect, which includes the invisible resize border). Null when the
# call fails or the picture is one flat colour, which is what a window that cannot be printed gives.
function PrintWin([IntPtr]$h, $wr) {
    $w = $wr.R - $wr.L; $hgt = $wr.B - $wr.T
    if ($w -lt 8 -or $hgt -lt 8) { return $null }
    # No alpha channel: GDI leaves it at zero, and the window is opaque anyway.
    $bmp = New-Object System.Drawing.Bitmap $w, $hgt, ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $ok = $false
    try {
        $dc = $g.GetHdc()
        try { $ok = [W]::PrintWindow($h, $dc, 2) } finally { $g.ReleaseHdc($dc) }
    } catch { $ok = $false }
    $g.Dispose()
    if (-not $ok) { $bmp.Dispose(); return $null }
    # Sample a grid of pixels: all one colour means nothing was drawn.
    $first = $bmp.GetPixel(0, 0).ToArgb(); $varied = $false
    for ($yy = 0; $yy -lt 9 -and -not $varied; $yy++) {
        for ($xx = 0; $xx -lt 13; $xx++) {
            $px = [int](($w - 1) * $xx / 12); $py = [int](($hgt - 1) * $yy / 8)
            if ($bmp.GetPixel($px, $py).ToArgb() -ne $first) { $varied = $true; break }
        }
    }
    if (-not $varied) { $bmp.Dispose(); return $null }
    $script:lastTrim = [T]::Trim($bmp)
    $bmp
}

# Saves TDPdf's open windows as one picture: each window's own pixels (PrintWindow, so nothing on the
# desktop behind or over them can appear) placed in a transparent canvas the size of their combined
# bounds. Anything between or around them (desktop, taskbar) stays transparent. A window whose pixels
# PrintWindow cannot give is copied from the screen instead.
function Shot([string]$name, $proc) {
    $screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $wins = @(foreach ($h in [W]::Windows([uint32]$proc.Id)) {
        $r = VisibleRect $h
        $wr = New-Object W+RECT
        [W]::GetWindowRect($h, [ref]$wr) | Out-Null
        $l = [Math]::Max($r.L, $screen.Left); $t = [Math]::Max($r.T, $screen.Top)
        $rr = [Math]::Min($r.R, $screen.Right); $b = [Math]::Min($r.B, $screen.Bottom)
        if ($rr - $l -ge 8 -and $b - $t -ge 8) {
            @{ H = $h; Rect = [System.Drawing.Rectangle]::FromLTRB($l, $t, $rr, $b); Wr = $wr }
        }
    })
    if (-not $wins) { throw "no visible TDPdf window for $name" }
    # Windows() lists them topmost first; handle the bottom one first so dialogs and menus land on top.
    [array]::Reverse($wins)
    # PrintWindow renders the transparent shadow margin of a layered window as pure black. Trim those
    # edge rows and columns off, so only the window itself is kept (black inside the window stays).
    foreach ($w in $wins) {
        $w.Bmp = $null
        try { $script:lastTrim = $null; $w.Bmp = PrintWin $w.H $w.Wr } catch { $w.Bmp = $null }
        if ($w.Bmp -and $script:lastTrim) {
            $tr = $script:lastTrim
            $vis = $w.Rect
            $tl = [Math]::Max($vis.X, $w.Wr.L + $tr[0]); $tt = [Math]::Max($vis.Y, $w.Wr.T + $tr[1])
            $tright = [Math]::Min($vis.Right, $w.Wr.L + $tr[2]); $tb = [Math]::Min($vis.Bottom, $w.Wr.T + $tr[3])
            if ($tright - $tl -ge 8 -and $tb - $tt -ge 8) {
                $w.Rect = [System.Drawing.Rectangle]::FromLTRB($tl, $tt, $tright, $tb)
            }
        }
    }
    $u = $wins[0].Rect
    foreach ($w in $wins) { $u = [System.Drawing.Rectangle]::Union($u, $w.Rect) }
    $bmp = New-Object System.Drawing.Bitmap $u.Width, $u.Height, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::Transparent)
    $methods = @()
    foreach ($w in $wins) {
        $r = $w.Rect
        $dest = New-Object System.Drawing.Rectangle ($r.X - $u.X), ($r.Y - $u.Y), $r.Width, $r.Height
        if ($w.Bmp) {
            # PrintWindow draws from the window's top-left corner; the kept part starts at r.
            $src = New-Object System.Drawing.Rectangle ($r.X - $w.Wr.L), ($r.Y - $w.Wr.T), $r.Width, $r.Height
            $g.DrawImage($w.Bmp, $dest, $src, [System.Drawing.GraphicsUnit]::Pixel)
            $w.Bmp.Dispose()
            $methods += 'PrintWindow'
        } else {
            $g.CopyFromScreen($r.X, $r.Y, $dest.X, $dest.Y, $r.Size)
            $methods += 'screen copy'
        }
    }
    $bmp.Save((Join-Path $Out "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Host "captured $name $($u.Width)x$($u.Height) from $($wins.Count) window(s): $($methods -join ', ')"
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

# An element by name in any of TDPdf's own windows (a dialog is its own top-level window).
function FindInApp([string]$name, [int]$wait = 10) {
    $pc = New-Object System.Windows.Automation.PropertyCondition($UIA::ProcessIdProperty, $app.P.Id)
    $nc = New-Object System.Windows.Automation.PropertyCondition($UIA::NameProperty, $name)
    for ($i = 0; $i -lt $wait * 2; $i++) {
        foreach ($top in @($UIA::RootElement.FindAll($Scope::Children, $pc))) {
            $e = $top.FindFirst($Scope::Descendants, $nc)
            if ($e) { return $e }
        }
        Start-Sleep -Milliseconds 500
    }
    throw "element '$name' not found in TDPdf's windows"
}

# Polls for up to about 6 s: right after a mark is placed the toolbar can lag a moment. Halfway
# through, one Escape (drops a popup or an open box, harmless to a placed mark) is tried.
function FindId($root, [string]$id) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($UIA::AutomationIdProperty, $id)
    for ($i = 0; $i -lt 24; $i++) {
        $e = $root.FindFirst($Scope::Descendants, $cond)
        if ($e) { return $e }
        if ($i -eq 12) { [System.Windows.Forms.SendKeys]::SendWait('{ESC}') }
        Start-Sleep -Milliseconds 250
    }
    throw "automation id '$id' not found"
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

# A click with Ctrl held (Ctrl+click edits a whole paragraph in the Edit Existing Text tool).
function CtrlClick([int]$x, [int]$y) {
    [W]::keybd_event(0x11, 0, 0, [UIntPtr]::Zero)
    try { Click $x $y } finally { [W]::keybd_event(0x11, 0, 0x2, [UIntPtr]::Zero) }
    Start-Sleep -Milliseconds 300
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

# form-fill shows the form overlays, which 2.2.1.0 and earlier draw about 14% too large (#220,
# fixed after 2.2.1.0). Off by default for those builds; ask for it with -Shots form-fill.
# epub-open is off by default on every build: the first EPUB on a machine asks for a book layout and
# the book is laid out by WebView2, neither checked on the capture machine yet; ask for it with
# -Shots epub-open.
$OffByDefault = @('epub-open')
$exeVersion = [version](Get-Item $Exe).VersionInfo.FileVersion
if ($exeVersion -le [version]'2.2.1.0') { $OffByDefault += 'form-fill' }
Write-Host "TDPdf $exeVersion; off by default: $($OffByDefault -join ', ')"
function Wanted([string]$what) { if ($Shots.Count -eq 0) { $OffByDefault -notcontains $what } else { $Shots -contains $what } }

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
    Start-Sleep -Seconds 6
    # Fit width again now the window has its final size, so the page and its form-field
    # overlays are laid out for this size, not the one the window opened at.
    [System.Windows.Forms.SendKeys]::SendWait('^2')
    Start-Sleep -Seconds 4
    @{ P = $p; H = $h; Root = $UIA::FromHandle($h) }
}

function Quit {
    Get-Process TDPdf -ErrorAction SilentlyContinue | Stop-Process -Force
    for ($i = 0; $i -lt 20 -and (Get-Process TDPdf -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 500 }
    Start-Sleep -Seconds 1
}

# A fresh TDPdf with these documents open, the LAST one active. Opening documents in order and
# letting the newest tab win is more reliable than clicking a tab header from a script.
function Session([string[]]$files) {
    Quit
    $script:app = Launch $files[0]
    for ($i = 1; $i -lt $files.Count; $i++) {
        Start-Process -FilePath $Exe -ArgumentList "`"$($files[$i])`"" | Out-Null
        Start-Sleep -Seconds 8
    }
    Front
}

# True when any of these shots is wanted, so a session nobody needs is never started.
function AnyWanted([string[]]$names) { foreach ($n in $names) { if (Wanted $n) { return $true } }; $false }

function Front { [W]::SetForegroundWindow($app.H) | Out-Null; Start-Sleep -Milliseconds 400 }

# Pointer off the window, so no hover highlight or tooltip lands in a shot.
function Park { [W]::SetCursorPos(1900, 1070) | Out-Null; Start-Sleep -Milliseconds 800 }

function Settle {
    Park
    # One Escape always (an overlay inside the window, such as the shortcut list, is not a window
    # of its own), then more while a dialog or menu window is still open.
    Keys '{ESC}'
    for ($i = 0; $i -lt 3; $i++) {
        if (@([W]::Windows([uint32]$app.P.Id)).Count -le 1) { break }
        Keys '{ESC}'
    }
    Front
    # The Select tool by its shortcut: invoking the button gives it keyboard focus, and WPF then
    # shows its tooltip in the next shot.
    Keys 'v'
}

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
$quoteV1   = Join-Path $Demo 'garden-quote-v1.pdf'              # the pair Compare is shown on: build/screenshots/make-compare-demo.py
$quoteV2   = Join-Path $Demo 'garden-quote-v2.pdf'
$bakery    = Join-Path $Demo 'bakery-price-list.pdf'            # a page with a picture: build/screenshots/make-picture-demo.py
$epub      = Join-Path $Demo 'window-garden.epub'               # a two-chapter book: build/screenshots/make-epub-demo.py
$managed   = (Get-ItemProperty 'HKLM:\SOFTWARE\TDPdf' -ErrorAction SilentlyContinue).Installed -eq 1
Write-Host "TDPdf installed for all users on this machine: $managed"

$app = $null

# ════ the agreement on its own: edit existing text ════
if (AnyWanted 'edit-text') {
    Session $agreement
}

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

# ════ the incident report: search, redaction, OCR ════
if (AnyWanted 'search', 'redact', 'ocr') {
    Session $incident
}

Attempt 'ocr' {
    Expand (MenuPath 'Tools', 'OCR')
    Start-Sleep -Seconds 1
    Park
    Shot 'ocr' $app.P
}

# ── search: every "Jordan Ellis" in the incident report ───────────────────────────
Attempt 'search' {
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

# ════ the membership form: filling it in ════
if (AnyWanted 'form-fill') {
    Session $form
}

# ── form-fill: type into the membership form ───────────────────────────────────────
# Field centres as fractions of the page, from build/screenshots/make-form-demo.py.
# Off by default for 2.2.1.0 and earlier (#220): there the field overlays are drawn too large, the
# clicks miss the fields and the typing reaches the tool shortcuts.
Attempt 'form-fill' {
    PageClick 0.500 0.229; Keys 'Avery Quinn'
    PageClick 0.297 0.294; Keys '{(}555{)} 014-0142'   # SendKeys reads ( ) as grouping
    PageClick 0.703 0.294; Keys '1 March 2027'
    PageClick 0.297 0.360; Keys 'B-12, near the shed'
    PageClick 0.129 0.415
    Park
    Start-Sleep -Seconds 1
    Shot 'form-fill' $app.P
}

# ════ two tabs, the agreement in front: the window, menus and tools ════
if (AnyWanted 'hero', 'page-sidebar', 'esign-menu', 'esign-connect', 'transform', 'export-images', 'print-preview', 'keyboard-map', 'crop', 'measure', 'form-build', 'markup') {
    Session $incident, $agreement
}

# ── hero: the agreement tab, every tab showing ─────────────────────────────────────
Attempt 'hero' {
    Park
    Shot 'hero' $app.P
}

# ── page-sidebar: the right-click menu on a page thumbnail ─────────────────────────
Attempt 'page-sidebar' {
    $list = FindId $app.Root 'PageList'
    $item = $list.FindFirst($Scope::Children, [System.Windows.Automation.Condition]::TrueCondition)
    ClickEl $item -Right
    Start-Sleep -Seconds 1
    Shot 'page-sidebar' $app.P
}

# ── menus and windows opened from the menu bar ────────────────────────────────────
Attempt 'esign-menu' {
    MenuPath 'File', 'Electronic Signature', 'Send for Signature' | Out-Null
    Park
    Shot 'esign-menu' $app.P
}

# Without a connected account, Send for Signature opens the Connect TDPdf Signing window.
Attempt 'esign-connect' {
    InvokeEl (MenuPath 'File', 'Electronic Signature', 'Send for Signature')
    Start-Sleep -Seconds 4
    Park
    Shot 'esign-connect' $app.P
}

Attempt 'transform' {
    InvokeEl (MenuPath 'Tools', 'Transform Page')
    Start-Sleep -Seconds 2
    # First a warning that transforming rasterizes the page; OK only opens the Transform window,
    # nothing changes until Apply, which is never pressed.
    InvokeEl (FindInApp 'OK')
    Start-Sleep -Seconds 3
    Park
    Shot 'transform' $app.P
}

Attempt 'export-images' {
    InvokeEl (MenuPath 'File', 'Export Pages as Images')
    Start-Sleep -Seconds 3
    Park
    Shot 'export-images' $app.P
}

Attempt 'print-preview' {
    Keys '^p'
    Start-Sleep -Seconds 5
    Park
    Shot 'print-preview' $app.P
}

Attempt 'keyboard-map' {
    Keys '^/'   # Ctrl+? (F1 opens About in this release)
    Start-Sleep -Seconds 2
    Park
    Shot 'keyboard-map' $app.P
}

# ── crop: the crop box on the page ────────────────────────────────────────────────
Attempt 'crop' {
    Keys 'c'    # the shortcut, not the button, so no focus tooltip lands in the shot
    Park
    Start-Sleep -Seconds 2
    Shot 'crop' $app.P
}

# ── measure: the live readout while dragging across the title ─────────────────────
Attempt 'measure' {
    InvokeEl (FindId $app.Root 'ToolMeasureBtn'); Start-Sleep -Milliseconds 400
    PageDrag 0.117 0.150 0.430 0.150 -Hold
    Start-Sleep -Seconds 1
    try { Shot 'measure' $app.P } finally { Release }
}

# ── markup: highlight, underline, a shape, ink and a text box on the agreement ─────
# Line positions as fractions of the page: section 1 text at 0.220, section 2 heading at 0.282,
# section 3 text at 0.408; text starts at 0.116 across.
Attempt 'markup' {
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


# ── form-build: the Form tool's field box on the agreement ────────────────────────
Attempt 'form-build' {
    # A fresh copy of the documents, so none of the markup above is in this shot.
    Session $incident, $agreement
    InvokeEl (FindId $app.Root 'ToolFormBtn'); Start-Sleep -Milliseconds 400
    PageDrag 0.500 0.178 0.850 0.198   # empty space right of the "1. Scope of work" heading
    Start-Sleep -Seconds 1
    Park
    Shot 'form-build' $app.P
}

# ════ 3.0 features ════
# Each of these starts from its own fresh session, so nothing from an earlier shot is on the page.

# ── multi-select: three marks on the agreement, picked up together with a drag box ──
# Highlight, shape and text box as in the markup shot. The Select tool's drag box starts on empty
# page (the top-left margin, no text character, no mark) and releases over all three, which selects
# them (MainWindow.MultiSelect.cs: a box that touches marks selects them). The page then shows an
# outline per mark and one group box.
Attempt 'multi-select' {
    # The same session as the markup shot, where these tool buttons are known to resolve.
    Session $incident, $agreement
    InvokeEl (FindId $app.Root 'ToolHighlightBtn'); Start-Sleep -Milliseconds 400
    PageDrag 0.117 0.220 0.600 0.220
    InvokeEl (FindId $app.Root 'ToolShapeBtn'); Start-Sleep -Milliseconds 400
    PageDrag 0.105 0.268 0.375 0.298
    InvokeEl (FindId $app.Root 'ToolTextBtn'); Start-Sleep -Milliseconds 400
    PageClick 0.600 0.100
    Start-Sleep -Milliseconds 600
    Keys 'Approved, ready to sign'
    InvokeEl (FindId $app.Root 'ToolSelectBtn'); Start-Sleep -Milliseconds 600
    PageDrag 0.040 0.065 0.820 0.312
    Park
    Start-Sleep -Seconds 1
    Shot 'multi-select' $app.P
}

# ── stamps: the Stamp tool (shortcut A) with Approved, its default, placed near the title ──
Attempt 'stamps' {
    Session $agreement
    InvokeEl (FindId $app.Root 'ToolStampBtn'); Start-Sleep -Seconds 1
    PageClick 0.700 0.090
    Start-Sleep -Seconds 1
    Park
    Shot 'stamps' $app.P
}

# ── compare: the first quote against its revised copy, which is the other open tab ───
# Tools > Compare PDFs asks which open tab to compare with; the first (the only other tab) is
# selected, and its Compare button opens the compare window with the list of changes.
Attempt 'compare' {
    Session $quoteV2, $quoteV1
    InvokeEl (MenuPath 'Tools', 'Compare PDFs')
    Start-Sleep -Seconds 2
    InvokeEl (FindInApp 'Compare')
    Start-Sleep -Seconds 8
    Park
    Shot 'compare' $app.P
}

# ── picture-edit: the Select Picture tool (key 3), clicking the logo on the bakery page ──
# The logo's centre is 0.265 across and 0.235 down the page (make-picture-demo.py).
Attempt 'picture-edit' {
    Session $bakery
    InvokeEl (FindId $app.Root 'ToolEditImageBtn'); Start-Sleep -Milliseconds 600
    PageClick 0.265 0.235
    Start-Sleep -Seconds 2
    Park
    Shot 'picture-edit' $app.P
}

# ── paragraph-edit: Ctrl+click a line in the Edit Existing Text tool opens the whole paragraph ──
# The first line of section 1 sits at 0.222 down the page.
Attempt 'paragraph-edit' {
    Session $agreement
    InvokeEl (FindId $app.Root 'ToolEditTextBtn'); Start-Sleep -Milliseconds 600
    $p = PagePoint 0.300 0.222
    CtrlClick $p[0] $p[1]
    Start-Sleep -Seconds 2
    Shot 'paragraph-edit' $app.P
}

# ── accessibility: Tools > Check Accessibility, its panel with the results ─────────
Attempt 'accessibility' {
    Session $agreement
    InvokeEl (MenuPath 'Tools', 'Check Accessibility')
    Start-Sleep -Seconds 5
    Park
    Shot 'accessibility' $app.P
}

# ── print-color: the print window with its COLOR section opened and set to "Let TDPdf manage colors",
# which is the mode that shows the printer profile, rendering intent and the other controls
# (AddColorSection in Services/PrintPreviewWindow.cs). Print is never pressed. ──
# Wheels the options column a few notches at a time until the named element is on screen and clear of
# the button row, and returns it. $anchor is a point inside the options column.
function ScrollIntoView([string]$name, $anchor) {
    $cancelTop = (FindInApp 'Cancel').Current.BoundingRectangle.Y
    for ($i = 0; $i -lt 20; $i++) {
        $el = FindInApp $name 4
        $r = $el.Current.BoundingRectangle
        if (-not $el.Current.IsOffscreen -and $r.Height -gt 0 -and ($r.Y + $r.Height) -lt ($cancelTop - 12)) { return $el }
        [W]::Wheel($anchor[0], $anchor[1], -3); Start-Sleep -Milliseconds 400
    }
    throw "'$name' never scrolled into view"
}

Attempt 'print-color' {
    Session $agreement
    Keys '^p'
    Start-Sleep -Seconds 5
    $r = (FindInApp 'PRINTER').Current.BoundingRectangle
    $anchor = @([int]($r.X + 120), [int]($r.Y + 150))
    ClickEl (ScrollIntoView 'COLOR' $anchor)
    Start-Sleep -Milliseconds 800
    # Open the mode list (the box sits just under its label) and pick the managed mode.
    try {
        $lab = (ScrollIntoView 'Color management' $anchor).Current.BoundingRectangle
        Click ([int]($lab.X + 100)) ([int]($lab.Y + $lab.Height + 18))
        Start-Sleep -Milliseconds 600
        ClickEl (FindInApp 'Let TDPdf manage colors' 4)
        Start-Sleep -Seconds 2
    } catch { Write-Host "print-color: mode not changed: $($_.Exception.Message)" }
    # Wheel on to the bottom so the profile, intent and options show.
    [W]::Wheel($anchor[0], $anchor[1], -30); Start-Sleep -Milliseconds 600
    Park
    Shot 'print-color' $app.P
}

# ── epub-open: an EPUB book opened in a tab. Off by default (see $OffByDefault): the first EPUB
# on a machine asks for a book layout, and the book is laid out by WebView2, neither of which has
# been checked on the capture machine. Ask for it with -Shots epub-open. ──
Attempt 'epub-open' {
    Quit
    $p = Start-Process -FilePath $Exe -ArgumentList "`"$epub`"" -PassThru
    $h = MainWindow $p
    [W]::MoveWindow($h, 40, 30, 1600, 1000, $true) | Out-Null
    $script:app = @{ P = $p; H = $h; Root = $UIA::FromHandle($h) }
    Front
    # The first-open layout question, when this profile has not answered it yet.
    try { InvokeEl (FindInApp 'Open book' 6) } catch { Write-Host 'no book layout question' }
    Start-Sleep -Seconds 15
    Front
    Park
    Shot 'epub-open' $app.P
}

# ════ install ════
function InstallPrompt([string]$name) {
    InvokeEl (FindId $app.Root 'InstallBtn')
    Start-Sleep -Seconds 3
    Park
    Shot $name $app.P
}

if (Wanted 'install') { Session $agreement }

if ($managed) {
    # Never install or uninstall here: the machine-wide copy belongs to Intune.
    Attempt 'install' { InstallPrompt 'install-managed' }
} else {
    Attempt 'install' {
        InstallPrompt 'install-fresh'
        Keys '{ESC}'
        Quit
        $i = Start-Process -FilePath $Exe -ArgumentList '/install', '/silent' -PassThru -Wait
        Write-Host "silent install exit code $($i.ExitCode)"
        $script:app = Launch $agreement
        InstallPrompt 'install'
    }
}

Quit
