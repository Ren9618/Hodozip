# Hodozip feedback button E2E.
# Usage: pwsh -File tests/e2e/run-feedback-e2e.ps1 [-ExePath <Hodozip.exe>]
#
# Verifies the About and Settings feedback buttons, their Japanese/English
# labels, and that the exact Google Forms URL is passed to the URL opener.
# HODOZIP_OPEN_URL_LOG makes the app record the URL instead of opening a browser.

param(
    [string]$ExePath
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Set-StrictMode -Version Latest

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName Microsoft.VisualBasic
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class FeedbackNative
{
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
'@

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if ([string]::IsNullOrWhiteSpace($ExePath)) {
    $ExePath = Join-Path $repoRoot 'src\Hodozip\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\Hodozip.exe'
}
$exePath = [System.IO.Path]::GetFullPath($ExePath)
if (-not (Test-Path -LiteralPath $exePath)) {
    throw "Hodozip.exe が見つかりません: $exePath"
}

$expectedUrl = 'https://docs.google.com/forms/d/e/1FAIpQLScFpnTH8fFGBcCQfoNoN7O-mGnPb1u8aLhShMMQZ8uFLxQ9GQ/viewform'
$settingsDirectory = Join-Path $env:LOCALAPPDATA 'Hodozip'
$settingsPath = Join-Path $settingsDirectory 'settings.json'
$logPath = Join-Path $env:TEMP ('hodozip-feedback-url-' + [Guid]::NewGuid().ToString('N') + '.log')
$hadSettings = Test-Path -LiteralPath $settingsPath
$settingsBackup = if ($hadSettings) { Get-Content -LiteralPath $settingsPath -Raw } else { $null }
$hadLogHook = $null -ne (Get-Item Env:HODOZIP_OPEN_URL_LOG -ErrorAction SilentlyContinue)
$oldLogHook = $env:HODOZIP_OPEN_URL_LOG
$env:HODOZIP_OPEN_URL_LOG = $logPath
Remove-Item -LiteralPath $logPath -Force -ErrorAction SilentlyContinue

function Set-Language {
    param([string]$Language)

    New-Item -ItemType Directory -Path $settingsDirectory -Force | Out-Null
    @{
        DestinationRule = 'SameAsArchive'
        OpenFolderAfterExtraction = $false
        CloseAfterAutoExtract = $false
        FolderCollisionPolicy = 'Merge'
        OverwritePolicy = 'Overwrite'
        Language = $Language
        MaxDegreeOfParallelism = 0
        CheckForUpdatesOnStartup = $false
    } | ConvertTo-Json | Set-Content -LiteralPath $settingsPath -Encoding utf8
}

function Get-AppWindows {
    param([int]$ProcessId)

    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
        $ProcessId)
    return @([System.Windows.Automation.AutomationElement]::RootElement.FindAll(
        [System.Windows.Automation.TreeScope]::Children,
        $condition))
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
    throw 'メイン ウィンドウを取得できませんでした。'
}

function Find-ByAutomationId {
    param($Root, [string]$AutomationId, [int]$TimeoutSeconds = 20)

    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $element = $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($null -ne $element) {
            return $element
        }
        Start-Sleep -Milliseconds 200
    }
    return $null
}

function Find-ByName {
    param($Root, [string]$Name, [System.Windows.Automation.ControlType]$ControlType, [int]$TimeoutSeconds = 20)

    $condition = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            $Name),
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            $ControlType))
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $element = $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($null -ne $element) {
            return $element
        }
        Start-Sleep -Milliseconds 200
    }
    return $null
}

function Click-Element {
    param($Element, [string]$Label, [int]$ProcessId)

    if ($null -eq $Element) {
        throw "E2E 失敗: $Label が見つかりません。"
    }

    try {
        $Element.GetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern).ScrollIntoView()
        Start-Sleep -Milliseconds 300
    }
    catch { }

    try {
        $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Milliseconds 500
        return
    }
    catch {
        # WinUI の UIA 実装によっては Invoke が使えないため、フォーカス＋Space で押す。
    }

    [Microsoft.VisualBasic.Interaction]::AppActivate($ProcessId) | Out-Null
    Start-Sleep -Milliseconds 200
    $Element.SetFocus()
    Start-Sleep -Milliseconds 200
    [System.Windows.Forms.SendKeys]::SendWait(' ')
    Start-Sleep -Milliseconds 500
}

function Wait-WindowWithAutomationId {
    param([int]$ProcessId, [string]$AutomationId, [int]$TimeoutSeconds = 20)

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        foreach ($window in Get-AppWindows -ProcessId $ProcessId) {
            $element = Find-ByAutomationId -Root $window -AutomationId $AutomationId -TimeoutSeconds 1
            if ($null -ne $element) {
                return [pscustomobject]@{ Window = $window; Element = $element }
            }
        }
        Start-Sleep -Milliseconds 250
    }
    throw "E2E 失敗: $AutomationId を持つウィンドウが現れませんでした。"
}

function Assert-FeedbackUrl {
    param([string]$Label)

    if (-not (Test-Path -LiteralPath $logPath)) {
        throw "E2E 失敗: $Label で URL の起動要求が記録されませんでした。"
    }
    $lines = @(Get-Content -LiteralPath $logPath | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($lines.Count -eq 0) {
        throw "E2E 失敗: $Label で URL が記録されませんでした。"
    }
    $actual = $lines[-1].Trim()
    if (-not [string]::Equals($actual, $expectedUrl, [StringComparison]::Ordinal)) {
        throw "E2E 失敗: $Label の URL が一致しません: '$actual'"
    }
    Write-Host "  OK: $Label が正しいフィードバック URL を開こうとした"
}

function Close-Window {
    param([IntPtr]$Handle)
    [FeedbackNative]::PostMessage($Handle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    Start-Sleep -Milliseconds 500
}

function Test-Language {
    param([string]$Label, [string]$AboutButtonName, [string]$SettingsButtonName, [string]$AboutFeedbackName, [string]$SettingsFeedbackName)

    Set-Language -Language $(if ($Label -eq 'ja') { 'Japanese' } else { 'English' })
    Remove-Item -LiteralPath $logPath -Force -ErrorAction SilentlyContinue
    $process = Start-Process -FilePath $exePath -PassThru
    try {
        $mainHandle = Wait-MainWindow -Process $process
        Start-Sleep -Seconds 2
        $main = [System.Windows.Automation.AutomationElement]::FromHandle($mainHandle)

        $aboutButton = Find-ByAutomationId -Root $main -AutomationId 'AboutButton' 
        Click-Element -Element $aboutButton -Label "$Label About ボタン" -ProcessId $process.Id
        $about = Wait-WindowWithAutomationId -ProcessId $process.Id -AutomationId 'AboutFeedbackButton'
        $aboutFeedback = $about.Element
        if ($aboutFeedback.Current.Name -ne $AboutFeedbackName) {
            throw "E2E 失敗: About のフィードバック文言が '$AboutFeedbackName' ではありません: '$($aboutFeedback.Current.Name)'"
        }
        Write-Host "  OK: $Label About のフィードバック文言 = $AboutFeedbackName"
        Click-Element -Element $aboutFeedback -Label "$Label About フィードバック" -ProcessId $process.Id
        Assert-FeedbackUrl -Label "$Label About"
        Close-Window -Handle ([IntPtr]$about.Window.Current.NativeWindowHandle)

        $settingsButton = Find-ByAutomationId -Root $main -AutomationId 'SettingsButton' 
        Click-Element -Element $settingsButton -Label "$Label 設定ボタン" -ProcessId $process.Id
        $settings = Wait-WindowWithAutomationId -ProcessId $process.Id -AutomationId 'SettingsFeedbackButton'
        $settingsFeedback = $settings.Element
        if ($settingsFeedback.Current.Name -ne $SettingsFeedbackName) {
            throw "E2E 失敗: 設定のフィードバック文言が '$SettingsFeedbackName' ではありません: '$($settingsFeedback.Current.Name)'"
        }
        Write-Host "  OK: $Label 設定のフィードバック文言 = $SettingsFeedbackName"
        Click-Element -Element $settingsFeedback -Label "$Label 設定フィードバック" -ProcessId $process.Id
        Assert-FeedbackUrl -Label "$Label Settings"
        Close-Window -Handle ([IntPtr]$settings.Window.Current.NativeWindowHandle)
    }
    finally {
        if (-not $process.HasExited) {
            try { $process.Kill($true) } catch { }
        }
    }
}

try {
    Get-Process Hodozip -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

    Write-Host '=== F1: 日本語 UI ==='
    Test-Language -Label 'ja' -AboutButtonName 'バージョン情報' -SettingsButtonName '設定' -AboutFeedbackName 'フィードバック' -SettingsFeedbackName 'フィードバックを送る'

    Write-Host '=== F2: English UI ==='
    Test-Language -Label 'en' -AboutButtonName 'About' -SettingsButtonName 'Settings' -AboutFeedbackName 'Feedback' -SettingsFeedbackName 'Send feedback'

    Write-Host ''
    Write-Host 'フィードバック ボタン E2E: すべて成功しました。'
}
finally {
    Get-Process Hodozip -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    if ($hadSettings) {
        Set-Content -LiteralPath $settingsPath -Value $settingsBackup -Encoding utf8 -NoNewline
    }
    elseif (Test-Path -LiteralPath $settingsPath) {
        Remove-Item -LiteralPath $settingsPath -Force
    }

    if ($hadLogHook) {
        $env:HODOZIP_OPEN_URL_LOG = $oldLogHook
    }
    else {
        Remove-Item Env:HODOZIP_OPEN_URL_LOG -ErrorAction SilentlyContinue
    }
    Remove-Item -LiteralPath $logPath -Force -ErrorAction SilentlyContinue
}
