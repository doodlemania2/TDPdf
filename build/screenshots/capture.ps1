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
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint x, uint y, uint d, UIntPtr e);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
"@

function Shot([string]$name, [IntPtr]$h) {
    $r = New-Object W+RECT
    [W]::GetWindowRect($h, [ref]$r) | Out-Null
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
}

function Dump([string]$name, $root) {
    $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                         [System.Windows.Automation.Condition]::TrueCondition)
    $lines = foreach ($e in $all) {
        $c = $e.Current
        $br = $c.BoundingRectangle
        "{0}`t{1}`t{2}`t{3}`t{4},{5},{6},{7}" -f $c.ControlType.ProgrammaticName, $c.AutomationId, $c.Name, $c.ClassName, [int]$br.X, [int]$br.Y, [int]$br.Width, [int]$br.Height
    }
    $lines | Set-Content (Join-Path $Out "$name.uia.txt")
}

function MainWindow($proc) {
    for ($i = 0; $i -lt 60; $i++) {
        $proc.Refresh()
        if ($proc.MainWindowHandle -ne [IntPtr]::Zero) { return $proc.MainWindowHandle }
        Start-Sleep -Milliseconds 500
    }
    throw "TDPdf main window never appeared"
}

$agreement = Join-Path $Demo 'services-agreement.pdf'
$incident  = Join-Path $Demo 'incident-report.pdf'

$p = Start-Process -FilePath $Exe -ArgumentList "`"$agreement`"" -PassThru
$h = MainWindow $p
Start-Sleep -Seconds 6
[W]::MoveWindow($h, 40, 30, 1600, 1000, $true) | Out-Null
[W]::SetForegroundWindow($h) | Out-Null
Start-Sleep -Seconds 3
Full 'desktop-1'
Shot 'hero-raw' $h
Dump 'main' ([System.Windows.Automation.AutomationElement]::FromHandle($h))

# Second document: does a second launch become a tab in the same window?
Start-Process -FilePath $Exe -ArgumentList "`"$incident`"" | Out-Null
Start-Sleep -Seconds 6
[W]::SetForegroundWindow($h) | Out-Null
Start-Sleep -Seconds 2
Full 'desktop-2'
Shot 'two-docs' $h
Dump 'main-2' ([System.Windows.Automation.AutomationElement]::FromHandle($h))

Get-Process TDPdf -ErrorAction SilentlyContinue | Select-Object Id, MainWindowTitle | Format-Table | Out-String | Write-Host
