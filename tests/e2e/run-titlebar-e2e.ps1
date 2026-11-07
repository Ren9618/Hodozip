# Hodozip title-bar theme E2E.
# Usage: pwsh -File tests/e2e/run-titlebar-e2e.ps1 [-ExePath <Hodozip.exe>] [-BeforeExePath <Hodozip.exe>]
#
# Verifies that the native title bar matches the app body in both Windows
# light and dark themes, that caption buttons stay usable, and that dragging,
# resizing and closing still work. Screenshots are written to %TEMP%.

param(
    [string]$ExePath,
    [string]$BeforeExePath
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Set-StrictMode -Version Latest

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName Microsoft.VisualBasic

Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class TitleBarNative
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out RECT rect, int size);

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int command);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hWnd);

    public const uint LeftDown = 0x0002;
    public const uint LeftUp = 0x0004;
    public const int SwRestore = 9;
}
'@

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if ([string]::IsNullOrWhiteSpace($ExePath)) {
    $ExePath = Join-Path $repoRoot 'src\Hodozip\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\Hodozip.exe'
}
$exePath = [System.IO.Path]::GetFullPath($ExePath)
if (-not (Test-Path -LiteralPath $exePath)) {
    throw "Hodozip.exe が見つかりません。先に Release x64 ビルドを実行してください: $exePath"
}

if ([string]::IsNullOrWhiteSpace($BeforeExePath)) {
    $candidate = Join-Path $env:LOCALAPPDATA 'Programs\Hodozip\Hodozip.exe'
    if (Test-Path -LiteralPath $candidate) {
        $BeforeExePath = $candidate
    }
}
$beforeExePath = if ([string]::IsNullOrWhiteSpace($BeforeExePath)) { $null } else { [System.IO.Path]::GetFullPath($BeforeExePath) }

$themeKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize'
$themeBackup = $null
if (Test-Path -LiteralPath $themeKey) {
    $themeBackup = (Get-ItemProperty -LiteralPath $themeKey -Name AppsUseLightTheme -ErrorAction SilentlyContinue).AppsUseLightTheme
}

$workRoot = Join-Path $env:TEMP ('HodozipTitleBarE2E_' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $workRoot -Force | Out-Null

function Set-AppTheme {
    param([bool]$Dark)

    New-Item -Path $themeKey -Force | Out-Null
    Set-ItemProperty -LiteralPath $themeKey -Name AppsUseLightTheme -Value $(if ($Dark) { 0 } else { 1 }) -Type DWord
    Start-Sleep -Milliseconds 2500
}

function Get-WindowRect {
    param([IntPtr]$Handle)

    $rect = New-Object TitleBarNative+RECT
    $size = [System.Runtime.InteropServices.Marshal]::SizeOf($rect)
    [TitleBarNative]::DwmGetWindowAttribute($Handle, 9, [ref]$rect, $size) | Out-Null
    if ($rect.Right -eq 0) {
        [TitleBarNative]::GetWindowRect($Handle, [ref]$rect) | Out-Null
    }
    return $rect
}

function Get-WindowRectOuter {
    param([IntPtr]$Handle)

    $rect = New-Object TitleBarNative+RECT
    [TitleBarNative]::GetWindowRect($Handle, [ref]$rect) | Out-Null
    return $rect
}

function Capture-Window {
    param([IntPtr]$Handle, [string]$Path)

    [TitleBarNative]::SetForegroundWindow($Handle) | Out-Null
    Start-Sleep -Milliseconds 500
    $rect = Get-WindowRect -Handle $Handle
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    $bitmap = New-Object System.Drawing.Bitmap $width, $height
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size)
    }
    finally {
        $graphics.Dispose()
    }

    $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    return [pscustomobject]@{
        Bitmap = $bitmap
        Width = $width
        Height = $height
        Rect = $rect
    }
}

function Get-PixelColor {
    param($Bitmap, [int]$X, [int]$Y)
    $pixel = $Bitmap.GetPixel($X, $Y)
    return @($pixel.R, $pixel.G, $pixel.B)
}

function Get-ColorDistance {
    param([int[]]$A, [int[]]$B)
    return [Math]::Max([Math]::Abs($A[0] - $B[0]), [Math]::Max([Math]::Abs($A[1] - $B[1]), [Math]::Abs($A[2] - $B[2])))
}

function Wait-MainWindow {
    param([System.Diagnostics.Process]$Process, [int]$TimeoutSeconds = 30)

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $Process.Refresh()
        if ($Process.HasExited) {
            throw 'Hodozip が起動直後に終了しました。'
        }
        if ($Process.MainWindowHandle -ne [IntPtr]::Zero) {
            return $Process.MainWindowHandle
        }
        Start-Sleep -Milliseconds 200
    }
    throw 'メイン ウィンドウ ハンドルを取得できませんでした。'
}

function Get-CaptionButton {
    param($Root, [string]$NamePattern)

    $buttons = $Root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button))
    foreach ($button in $buttons) {
        if ($button.Current.Name -match $NamePattern -and $button.Current.BoundingRectangle.Width -gt 0) {
            return $button
        }
    }
    return $null
}

function Invoke-CaptionButton {
    param($Button, [string]$Label)

    $pattern = $Button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
    Start-Sleep -Milliseconds 600
    Write-Host "  OK: $Label ボタンを UIA で操作"
}

function Move-MouseAndClick {
    param([int]$X, [int]$Y, [switch]$Click)

    [TitleBarNative]::SetCursorPos($X, $Y) | Out-Null
    Start-Sleep -Milliseconds 120
    if ($Click) {
        [TitleBarNative]::mouse_event([TitleBarNative]::LeftDown, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 80
        [TitleBarNative]::mouse_event([TitleBarNative]::LeftUp, 0, 0, 0, [UIntPtr]::Zero)
    }
}

function Drag-Mouse {
    param([int]$FromX, [int]$FromY, [int]$ToX, [int]$ToY)

    [TitleBarNative]::SetCursorPos($FromX, $FromY) | Out-Null
    Start-Sleep -Milliseconds 150
    [TitleBarNative]::mouse_event([TitleBarNative]::LeftDown, 0, 0, 0, [UIntPtr]::Zero)
    for ($step = 1; $step -le 8; $step++) {
        $x = $FromX + [int](($ToX - $FromX) * $step / 8)
        $y = $FromY + [int](($ToY - $FromY) * $step / 8)
        [TitleBarNative]::SetCursorPos($x, $y) | Out-Null
        Start-Sleep -Milliseconds 40
    }
    [TitleBarNative]::mouse_event([TitleBarNative]::LeftUp, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 300
}

function Assert-ColorMatch {
    param([string]$Label, [int[]]$Title, [int[]]$Body, [int]$Tolerance = 3)

    $distance = Get-ColorDistance $Title $Body
    if ($distance -gt $Tolerance) {
        throw "E2E 失敗: $Label のタイトルバー色 $($Title -join ',') と本文背景 $($Body -join ',') が一致しません (差 $distance)。"
    }
    Write-Host "  OK: $Label タイトルバー = $($Title -join ',') / 本文背景 = $($Body -join ',') (差 $distance)"
}

function Test-BeforeScreenshot {
    param([string]$ThemeLabel, [bool]$Dark)

    if ($null -eq $beforeExePath -or -not (Test-Path -LiteralPath $beforeExePath)) {
        Write-Host '  INFO: 比較用の旧バージョンがないため Before はスキップ'
        return
    }

    $process = Start-Process -FilePath $beforeExePath -PassThru
    try {
        $handle = Wait-MainWindow -Process $process
        Start-Sleep -Seconds 4
        $capture = Capture-Window -Handle $handle -Path (Join-Path $workRoot "before-$ThemeLabel.png")
        try {
            $title = Get-PixelColor $capture.Bitmap ([int]($capture.Width / 2)) 8
            $body = Get-PixelColor $capture.Bitmap 12 ($capture.Height - 30)
            Write-Host "  BEFORE ($ThemeLabel): title=$($title -join ',') body=$($body -join ',') diff=$(Get-ColorDistance $title $body)"
        }
        finally {
            $capture.Bitmap.Dispose()
        }
    }
    finally {
        if (-not $process.HasExited) {
            try { $process.Kill($true) } catch { }
        }
    }
}

function Test-Theme {
    param([string]$Label, [bool]$Dark, [switch]$Interact)

    Set-AppTheme -Dark $Dark
    Test-BeforeScreenshot -ThemeLabel $Label -Dark $Dark

    $process = Start-Process -FilePath $exePath -PassThru
    $handle = $null
    try {
        $handle = Wait-MainWindow -Process $process
        Start-Sleep -Seconds 2
        $capture = Capture-Window -Handle $handle -Path (Join-Path $workRoot "after-$Label.png")
        try {
            $title = Get-PixelColor $capture.Bitmap ([int]($capture.Width / 2)) 8
            $body = Get-PixelColor $capture.Bitmap 12 ($capture.Height - 30)
            Assert-ColorMatch -Label $Label -Title $title -Body $body
        }
        finally {
            $capture.Bitmap.Dispose()
        }

        $root = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
        if ($null -eq (Get-CaptionButton -Root $root -NamePattern '最小化|Minimize')) {
            throw "E2E 失敗: $Label で最小化ボタンが見つかりません。"
        }

        if ($Interact) {
            $maximize = Get-CaptionButton -Root $root -NamePattern '最大化|Maximize'
            $minimize = Get-CaptionButton -Root $root -NamePattern '最小化|Minimize'
            $close = Get-CaptionButton -Root $root -NamePattern '閉じる|Close'
            if ($null -eq $maximize -or $null -eq $minimize -or $null -eq $close) {
                throw "E2E 失敗: $Label でキャプション ボタンが揃っていません。"
            }

            # Hover changes the caption button background without hiding the glyph.
            $windowRect = Get-WindowRect -Handle $handle
            [TitleBarNative]::SetForegroundWindow($handle) | Out-Null
            [TitleBarNative]::SetCursorPos($windowRect.Left + 30, $windowRect.Bottom - 30) | Out-Null
            Start-Sleep -Milliseconds 400
            $hoverRect = $maximize.Current.BoundingRectangle
            $probeX = [int]($hoverRect.X + 4)
            $probeY = [int]($hoverRect.Y + $hoverRect.Height / 2)
            $beforeHover = Get-PixelColorFromScreen -X $probeX -Y $probeY
            Move-MouseAndClick -X ([int]($hoverRect.X + $hoverRect.Width / 2)) -Y ([int]($hoverRect.Y + $hoverRect.Height / 2))
            Start-Sleep -Milliseconds 300
            $afterHover = Get-PixelColorFromScreen -X $probeX -Y $probeY
            $hoverDistance = Get-ColorDistance $beforeHover $afterHover
            if ($hoverDistance -lt 3) {
                throw "E2E 失敗: キャプション ボタンのホバー色が変化しません (差 $hoverDistance)。"
            }
            Write-Host "  OK: ホバーでキャプション背景が変化 (差 $hoverDistance)"

            # Minimize / restore.
            Invoke-CaptionButton -Button $minimize -Label '最小化'
            if (-not [TitleBarNative]::IsIconic($handle)) {
                throw 'E2E 失敗: 最小化ボタンで最小化されませんでした。'
            }
            [TitleBarNative]::ShowWindow($handle, [TitleBarNative]::SwRestore) | Out-Null
            Start-Sleep -Milliseconds 700
            [TitleBarNative]::SetForegroundWindow($handle) | Out-Null
            Start-Sleep -Milliseconds 300
            Write-Host '  OK: 最小化 / 復元'

            # Maximize / restore.
            $root = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
            $maximize = Get-CaptionButton -Root $root -NamePattern '最大化|Maximize'
            $rect = $maximize.Current.BoundingRectangle
            Move-MouseAndClick -X ([int]($rect.X + $rect.Width / 2)) -Y ([int]($rect.Y + $rect.Height / 2)) -Click
            Start-Sleep -Milliseconds 600
            if (-not [TitleBarNative]::IsZoomed($handle)) {
                throw 'E2E 失敗: 最大化ボタンで最大化されませんでした。'
            }
            Move-MouseAndClick -X ([int]($rect.X + $rect.Width / 2)) -Y ([int]($rect.Y + $rect.Height / 2)) -Click
            Start-Sleep -Milliseconds 700
            Write-Host '  OK: 最大化 / 元に戻す'

            # Drag the title bar.
            $before = Get-WindowRect -Handle $handle
            $dragX = [int](($before.Left + $before.Right) / 2)
            $dragY = $before.Top + 16
            Drag-Mouse -FromX $dragX -FromY $dragY -ToX ($dragX + 50) -ToY ($dragY + 35)
            $after = Get-WindowRect -Handle $handle
            if ([Math]::Abs($after.Left - $before.Left) -lt 10 -and [Math]::Abs($after.Top - $before.Top) -lt 10) {
                throw 'E2E 失敗: タイトルバーのドラッグでウィンドウが移動しませんでした。'
            }
            Write-Host "  OK: タイトルバー ドラッグで移動 ($($before.Left),$($before.Top) -> $($after.Left),$($after.Top))"

            # Resize from the bottom-right corner (the outer rect includes the
            # resize border that the DWM frame bounds exclude).
            $beforeResize = Get-WindowRectOuter -Handle $handle
            $resizeX = $beforeResize.Right - 3
            $resizeY = $beforeResize.Bottom - 3
            Drag-Mouse -FromX $resizeX -FromY $resizeY -ToX ($resizeX + 40) -ToY ($resizeY + 30)
            $afterResize = Get-WindowRectOuter -Handle $handle
            if (($afterResize.Right - $afterResize.Left) -le ($beforeResize.Right - $beforeResize.Left) -and
                ($afterResize.Bottom - $afterResize.Top) -le ($beforeResize.Bottom - $beforeResize.Top)) {
                throw 'E2E 失敗: 右下ドラッグでウィンドウがリサイズされませんでした。'
            }
            Write-Host "  OK: 右下ドラッグでリサイズ ($($beforeResize.Right - $beforeResize.Left)x$($beforeResize.Bottom - $beforeResize.Top) -> $($afterResize.Right - $afterResize.Left)x$($afterResize.Bottom - $afterResize.Top))"

            # Close via the caption button.
            [TitleBarNative]::SetForegroundWindow($handle) | Out-Null
            $root = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
            $close = Get-CaptionButton -Root $root -NamePattern '閉じる|Close'
            $rect = $close.Current.BoundingRectangle
            Move-MouseAndClick -X ([int]($rect.X + $rect.Width / 2)) -Y ([int]($rect.Y + $rect.Height / 2)) -Click
            if (-not $process.WaitForExit(5000)) {
                throw 'E2E 失敗: 閉じるボタンでプロセスが終了しませんでした。'
            }
            Write-Host '  OK: 閉じるボタンで終了'
        }
        else {
            $close = Get-CaptionButton -Root $root -NamePattern '閉じる|Close'
            if ($null -eq $close) {
                throw "E2E 失敗: $Label で閉じるボタンが見つかりません。"
            }
        }
    }
    finally {
        if (-not $process.HasExited) {
            try { $process.Kill($true) } catch { }
        }
    }
}

function Get-PixelColorFromScreen {
    param([int]$X, [int]$Y)
    $bitmap = New-Object System.Drawing.Bitmap 1, 1
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen($X, $Y, 0, 0, $bitmap.Size)
    }
    finally {
        $graphics.Dispose()
    }
    try {
        $pixel = $bitmap.GetPixel(0, 0)
        return @($pixel.R, $pixel.G, $pixel.B)
    }
    finally {
        $bitmap.Dispose()
    }
}

try {
    Get-Process Hodozip -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

    Write-Host '=== T1: ダーク テーマ（既存の症状と修正後） ==='
    Test-Theme -Label 'dark' -Dark $true -Interact

    Write-Host '=== T2: ライト テーマ ==='
    Test-Theme -Label 'light' -Dark $false

    Write-Host ''
    Write-Host "タイトルバー E2E: すべて成功しました。スクリーンショット: $workRoot"
}
finally {
    if (-not [string]::IsNullOrWhiteSpace($BeforeExePath) -and $null -ne $themeBackup) {
        Set-ItemProperty -LiteralPath $themeKey -Name AppsUseLightTheme -Value $themeBackup -Type DWord
    }
    elseif ($null -ne $themeBackup) {
        Set-ItemProperty -LiteralPath $themeKey -Name AppsUseLightTheme -Value $themeBackup -Type DWord
    }
    Get-Process Hodozip -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}
