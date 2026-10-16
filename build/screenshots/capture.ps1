# Takes Store / tdpdf.com screenshots of a released TDPdf build on a GitHub-hosted
# Windows runner. Drives the app through UI Automation, mouse and keyboard input only, so
# every image is the real app, not a mock-up. Each shot is attempted independently: one
# that fails is logged and the rest still run.
param(
    [string]$Exe,
    [string]$Demo,
    [string]$Out
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $Out | Out-Null

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

function Grab([string]$name, [int]$x, [int]$y, [int]$w, [int]$ht) {
    $bmp = New-Object System.Drawing.Bitmap $w, $ht
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($x, $y, 0, 0, $bmp.Size)
    $bmp.Save((Join-Path $Out "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Host "captured $name ${w}x$ht"
}

function Shot([string]$name, [IntPtr]$h) {
    $r = VisibleRect $h
    Grab $name $r.L $r.T ($r.R - $r.L) ($r.B - $r.T)
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

function TopWindows($proc) {
    $pc = New-Object System.Windows.Automation.PropertyCondition($UIA::ProcessIdProperty, $proc.Id)
    @($UIA::RootElement.FindAll($Scope::Children, $pc))
}

function DumpProcess([string]$name, $proc) {
    $i = 0
    foreach ($top in TopWindows $proc) { Dump "$name-$i" $top; $i++ }
}

function Find($root, [string]$name, [int]$wait = 10) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($UIA::NameProperty, $name)
    for ($i = 0; $i -lt $wait * 2; $i++) {
        $e = $root.FindFirst($Scope::Descendants, $cond)
        if ($e) { return $e }
        Start-Sleep -Milliseconds 500
    }
    throw "element '$name' not found"
}

function FindId($root, [string]$id) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($UIA::AutomationIdProperty, $id)
    $e = $root.FindFirst($Scope::Descendants, $cond)
    if (-not $e) { throw "automation id '$id' not found" }
    $e
}

function Click([int]$x, [int]$y, [switch]$Double) {
    [W]::SetCursorPos($x, $y) | Out-Null
    Start-Sleep -Milliseconds 150
    $n = if ($Double) { 2 } else { 1 }
    for ($i = 0; $i -lt $n; $i++) {
        [W]::mouse_event(0x2, 0, 0, 0, [UIntPtr]::Zero)
        [W]::mouse_event(0x4, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 60
    }
}

function ClickEl($el, [switch]$Double) {
    $r = $el.Current.BoundingRectangle
    Click ([int]($r.X + $r.Width / 2)) ([int]($r.Y + $r.Height / 2)) -Double:$Double
}

function InvokeEl($el) {
    $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

function Expand($el) {
    $el.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
}

function Keys([string]$k) { [System.Windows.Forms.SendKeys]::SendWait($k); Start-Sleep -Milliseconds 400 }

function MainWindow($proc) {
    for ($i = 0; $i -lt 60; $i++) {
        $proc.Refresh()
        if ($proc.MainWindowHandle -ne [IntPtr]::Zero) { return $proc.MainWindowHandle }
        Start-Sleep -Milliseconds 500
    }
    throw "TDPdf main window never appeared"
}

function Attempt([string]$what, [scriptblock]$b) {
    try { & $b } catch { Write-Host "FAILED $what : $($_.Exception.Message)"; Full "failed-$what" }
}

function Launch([string]$file) {
    $p = Start-Process -FilePath $Exe -ArgumentList "`"$file`"" -PassThru
    $h = MainWindow $p
    [W]::MoveWindow($h, 40, 30, 1600, 1000, $true) | Out-Null
    [W]::SetForegroundWindow($h) | Out-Null
    Start-Sleep -Seconds 10
    @{ P = $p; H = $h; Root = $UIA::FromHandle($h) }
}

# A point on the rendered page, as fractions of the page image (so it survives tab-strip
# and zoom changes).
function PagePoint($root, [double]$fx, [double]$fy) {
    $r = (FindId $root 'PageImage').Current.BoundingRectangle
    @([int]($r.X + $r.Width * $fx), [int]($r.Y + $r.Height * $fy))
}

$agreement = Join-Path $Demo 'services-agreement.pdf'
$incident  = Join-Path $Demo 'incident-report.pdf'

$app = Launch $agreement

# ── edit-text: edit "1.5%" in section 2 ─────────────────────────────────────────────
# Measured on the first capture: "1.5%" sits at 61.4% across, 33.7% down the page image.
Attempt 'edit-text' {
    InvokeEl (FindId $app.Root 'ToolEditTextBtn')
    Start-Sleep -Milliseconds 500
    $pt = PagePoint $app.Root 0.614 0.337
    Click $pt[0] $pt[1]
    Start-Sleep -Seconds 2
    Shot 'edit-text-open' $app.H
    DumpProcess 'edit-text' $app.P
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
    Shot 'edit-text' $app.H
    Keys '{ESC}'
    Keys '{ESC}'
    InvokeEl (FindId $app.Root 'ToolSelectBtn')
}

# ── second document as a tab ────────────────────────────────────────────────────────
Start-Process -FilePath $Exe -ArgumentList "`"$incident`"" | Out-Null
Start-Sleep -Seconds 8
[W]::SetForegroundWindow($app.H) | Out-Null
Start-Sleep -Seconds 2

# ── redact: the Redact tool, dragged over the names and phone numbers ─────────────
# Boxes are fractions of the page image, measured on an earlier capture of incident-report.pdf.
# ("Redact matches" is not used here: on this file it marks whole lines, filed as an issue.)
function Drag([int]$x1, [int]$y1, [int]$x2, [int]$y2) {
    [W]::SetCursorPos($x1, $y1) | Out-Null
    Start-Sleep -Milliseconds 120
    [W]::mouse_event(0x2, 0, 0, 0, [UIntPtr]::Zero)
    for ($s = 1; $s -le 12; $s++) {
        [W]::SetCursorPos([int]($x1 + ($x2 - $x1) * $s / 12), [int]($y1 + ($y2 - $y1) * $s / 12)) | Out-Null
        Start-Sleep -Milliseconds 25
    }
    [W]::mouse_event(0x4, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 300
}
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
    foreach ($b in $boxes) {
        $a = PagePoint $app.Root $b[0] $b[1]
        $z = PagePoint $app.Root $b[2] $b[3]
        Drag $a[0] $a[1] $z[0] $z[1]
    }
    [W]::SetCursorPos(1900, 1070) | Out-Null
    Start-Sleep -Seconds 2
    Shot 'redact' $app.H
}

# ── hero: the agreement tab, both tabs showing ──────────────────────────────────────
Attempt 'hero' {
    Keys '{ESC}'
    ClickEl (Find $app.Root 'services-agreement.pdf')
    Start-Sleep -Seconds 3
    [W]::SetCursorPos(1900, 1070) | Out-Null   # pointer off the window, so no tooltip
    Start-Sleep -Seconds 2
    Shot 'hero' $app.H
}

# ── esign-send: File ▸ Electronic Signature ▸ Send for Signature ────────────────────
Attempt 'esign' {
    [W]::SetForegroundWindow($app.H) | Out-Null
    Expand (Find $app.Root 'File')
    Start-Sleep -Seconds 1
    Expand (Find $UIA::RootElement 'Electronic Signature')
    Start-Sleep -Seconds 1
    Full 'esign-menu'
    DumpProcess 'esign-menu' $app.P
    $send = $null
    foreach ($n in 'Send for Signature...', 'Send for Signature…', 'Send for Signature') {
        try { $send = Find $UIA::RootElement $n 2; break } catch { }
    }
    if (-not $send) { throw 'Send for Signature item not found' }
    InvokeEl $send
    Start-Sleep -Seconds 4
    Full 'esign-send-full'
    DumpProcess 'esign-send' $app.P
    foreach ($w in TopWindows $app.P) {
        $hw = [IntPtr]$w.Current.NativeWindowHandle
        if ($hw -ne $app.H) { Shot ('esign-window-' + ($w.Current.Name -replace '[^\w]+', '-')) $hw }
    }
}

# ── install: the Upgrade / Reinstall / Uninstall prompt over an existing install ────
Attempt 'install' {
    Get-Process TDPdf -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 2
    $i = Start-Process -FilePath $Exe -ArgumentList '/install', '/silent' -PassThru -Wait
    Write-Host "silent install exit code $($i.ExitCode)"
    $arp = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\TDPdf' -ErrorAction SilentlyContinue
    Write-Host "ARP: $($arp.DisplayName) | $($arp.Publisher) | $($arp.DisplayVersion)"
    $script:app = Launch $agreement
    InvokeEl (FindId $app.Root 'InstallBtn')
    Start-Sleep -Seconds 3
    Full 'install-full'
    DumpProcess 'install' $app.P
    foreach ($w in TopWindows $app.P) {
        $hw = [IntPtr]$w.Current.NativeWindowHandle
        if ($hw -ne $app.H) { Shot ('install-window-' + ($w.Current.Name -replace '[^\w]+', '-')) $hw }
    }
}
