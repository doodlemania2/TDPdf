# Takes Store / tdpdf.com screenshots of a released TDPdf build on a GitHub-hosted
# Windows runner. Drives the app through UI Automation and keyboard input only, so what
# it captures is the real app, not a mock-up.
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

function Shot([string]$name, [IntPtr]$h) {
    $r = VisibleRect $h
    $w = $r.R - $r.L; $ht = $r.B - $r.T
    $bmp = New-Object System.Drawing.Bitmap $w, $ht
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.L, $r.T, 0, 0, $bmp.Size)
    $bmp.Save((Join-Path $Out "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Host "captured $name ${w}x$ht"
}

function Full([string]$name) {
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen(0, 0, 0, 0, $bmp.Size)
    $bmp.Save((Join-Path $Out "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Host "captured $name (full screen)"
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
    Write-Host "dumped $name ($($lines.Count) elements)"
}

function Find($root, [string]$name) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($UIA::NameProperty, $name)
    $root.FindFirst($Scope::Descendants, $cond)
}

function Expand($el) {
    $p = $el.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $p.Expand()
}

function MainWindow($proc) {
    for ($i = 0; $i -lt 60; $i++) {
        $proc.Refresh()
        if ($proc.MainWindowHandle -ne [IntPtr]::Zero) { return $proc.MainWindowHandle }
        Start-Sleep -Milliseconds 500
    }
    throw "TDPdf main window never appeared"
}

function Try([string]$what, [scriptblock]$b) {
    try { & $b } catch { Write-Host "FAILED $what : $($_.Exception.Message)" }
}

$agreement = Join-Path $Demo 'services-agreement.pdf'
$incident  = Join-Path $Demo 'incident-report.pdf'

$p = Start-Process -FilePath $Exe -ArgumentList "`"$agreement`"" -PassThru
$h = MainWindow $p
[W]::MoveWindow($h, 40, 30, 1600, 1000, $true) | Out-Null
[W]::SetForegroundWindow($h) | Out-Null
Start-Sleep -Seconds 10
$root = $UIA::FromHandle($h)
Shot 'hero-1doc' $h
Try 'dump main' { Dump 'main' $root }

# Second document: does a second launch become a tab in the same window?
Start-Process -FilePath $Exe -ArgumentList "`"$incident`"" | Out-Null
Start-Sleep -Seconds 8
[W]::SetForegroundWindow($h) | Out-Null
Start-Sleep -Seconds 2
Full 'desktop-2'
Shot 'two-docs' $h
Get-Process TDPdf -ErrorAction SilentlyContinue | Select-Object Id, MainWindowTitle | Format-Table | Out-String | Write-Host

# Menus: dump File and Tools so the next pass can script the e-sign and tool shots.
foreach ($m in 'File', 'Edit', 'Tools') {
    Try "menu $m" {
        [W]::SetForegroundWindow($h) | Out-Null
        Expand (Find $root $m)
        Start-Sleep -Seconds 1
        Full "menu-$m"
        $pc = New-Object System.Windows.Automation.PropertyCondition($UIA::ProcessIdProperty, $p.Id)
        $i = 0
        foreach ($top in $UIA::RootElement.FindAll($Scope::Children, $pc)) { Dump "menu-$m-$i" $top; $i++ }
        [System.Windows.Forms.SendKeys]::SendWait('{ESC}{ESC}')
        Start-Sleep -Milliseconds 500
    }
}
