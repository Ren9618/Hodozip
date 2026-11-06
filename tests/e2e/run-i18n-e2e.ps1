# Hodozip Phase 4 localization smoke E2E.
# Usage: pwsh -File tests/e2e/run-i18n-e2e.ps1 [-ExePath <Hodozip.exe>]
# Requires: Release x64 build (or publish output). Uses UI Automation.
#
# Verifies that the main window and Settings window are shown in English when
# Language=English, and in Japanese when Language=Japanese. The language is
# stored in settings.json and applied on the next launch.

param(
    [string]$ExePath
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if ([string]::IsNullOrWhiteSpace($ExePath)) {
    $ExePath = Join-Path $repoRoot 'src\Hodozip\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\Hodozip.exe'
}
$exePath = [System.IO.Path]::GetFullPath($ExePath)
$settingsDirectory = Join-Path $env:LOCALAPPDATA 'Hodozip'
$settingsPath = Join-Path $settingsDirectory 'settings.json'

if (-not (Test-Path -LiteralPath $exePath)) {
    throw "Hodozip.exe が見つかりません。先に Release x64 ビルドを実行してください: $exePath"
}

function Write-LanguageSetting {
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
    } | ConvertTo-Json | Set-Content -LiteralPath $settingsPath -Encoding utf8
}

function Get-ProcessCondition {
    param([int]$ProcessId)

    return New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
        $ProcessId)
}

function Start-Hodozip {
    $process = Start-Process -FilePath $exePath -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($process.HasExited) {
            throw "E2E 失敗: Hodozip が起動直後に終了しました（終了コード $($process.ExitCode)）。"
        }

        $condition = New-Object System.Windows.Automation.AndCondition(
            (Get-ProcessCondition -ProcessId $process.Id),
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::Window)))
        $window = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            $condition)
        if ($null -ne $window) {
            return $process
        }

        Start-Sleep -Milliseconds 250
    }

    try { $process.Kill($true) } catch { }
    throw 'E2E 失敗: Hodozip のメイン ウィンドウを UIA で検出できませんでした。'
}

function Get-ElementNames {
    param([int]$ProcessId)

    $condition = Get-ProcessCondition -ProcessId $ProcessId
    $all = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        $condition)
    $names = New-Object System.Collections.Generic.List[string]
    foreach ($element in $all) {
        $name = $element.Current.Name
        if (-not [string]::IsNullOrWhiteSpace($name)) {
            $names.Add($name)
        }
    }

    return $names
}

function Assert-NamePresent {
    param(
        [int]$ProcessId,
        [string]$Expected,
        [string]$Label
    )

    $names = Get-ElementNames -ProcessId $ProcessId
    if (-not $names.Contains($Expected)) {
        $visible = ($names | Select-Object -Unique | Select-Object -First 50) -join ' | '
        throw "E2E 失敗: $Label に '$Expected' が見つかりません。検出した要素: $visible"
    }

    Write-Host "  OK: $Label = $Expected"
}

function Invoke-Button {
    param(
        [int]$ProcessId,
        [string]$Name,
        [string]$Label
    )

    $condition = New-Object System.Windows.Automation.AndCondition(
        (Get-ProcessCondition -ProcessId $ProcessId),
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            $Name)),
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button)))
    $button = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        $condition)
    if ($null -eq $button) {
        throw "E2E 失敗: $Label のボタン '$Name' が見つかりません。"
    }

    $pattern = $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
    Write-Host "  OK: $Label のボタン '$Name' を押しました。"
}

function Wait-ForName {
    param(
        [int]$ProcessId,
        [string]$Expected,
        [int]$TimeoutSeconds = 15
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ((Get-ElementNames -ProcessId $ProcessId).Contains($Expected)) {
            return $true
        }

        Start-Sleep -Milliseconds 250
    }

    return $false
}

function Stop-Hodozip {
    param([System.Diagnostics.Process]$Process)

    if (-not $Process.HasExited) {
        $null = $Process.CloseMainWindow()
        if (-not $Process.WaitForExit(10000)) {
            try { $Process.Kill($true) } catch { }
        }
    }
}

$settingsBackup = $null
if (Test-Path -LiteralPath $settingsPath) {
    $settingsBackup = Get-Content -LiteralPath $settingsPath -Raw
}

try {
    Write-Host '=== I1: English UI ==='
    Write-LanguageSetting -Language 'English'
    $english = Start-Hodozip
    try {
        Assert-NamePresent -ProcessId $english.Id -Expected 'Unpack archives in a snap. Just drag and drop to extract.' -Label 'English tagline'
        Assert-NamePresent -ProcessId $english.Id -Expected 'Drop an archive here' -Label 'English drop zone'
        Assert-NamePresent -ProcessId $english.Id -Expected 'Select a file' -Label 'English select button'
        Assert-NamePresent -ProcessId $english.Id -Expected 'Settings' -Label 'English settings button'

        Invoke-Button -ProcessId $english.Id -Name 'Settings' -Label 'English main window'
        $settingsExpected = 'Language changes take effect the next time Hodozip starts.'
        if (-not (Wait-ForName -ProcessId $english.Id -Expected $settingsExpected)) {
            throw "E2E 失敗: 設定ウィンドウに英語の言語注記 '$settingsExpected' が見つかりません。"
        }
        Write-Host "  OK: English settings note = $settingsExpected"
        Assert-NamePresent -ProcessId $english.Id -Expected 'Parallel extraction' -Label 'English parallel settings'
        Assert-NamePresent -ProcessId $english.Id -Expected 'Save' -Label 'English save button'
        Invoke-Button -ProcessId $english.Id -Name 'Cancel' -Label 'English settings'
    }
    finally {
        Stop-Hodozip -Process $english
    }

    Write-Host '=== I2: Japanese UI（再起動後に反映） ==='
    Write-LanguageSetting -Language 'Japanese'
    $japanese = Start-Hodozip
    try {
        Assert-NamePresent -ProcessId $japanese.Id -Expected 'アーカイブを、ほどく。ドラッグ＆ドロップですぐに解凍できます。' -Label '日本語タグライン'
        Assert-NamePresent -ProcessId $japanese.Id -Expected 'アーカイブをここにドロップ' -Label '日本語ドロップ ゾーン'
        Assert-NamePresent -ProcessId $japanese.Id -Expected 'ファイルを選択' -Label '日本語ファイル選択ボタン'
        Assert-NamePresent -ProcessId $japanese.Id -Expected '設定' -Label '日本語設定ボタン'

        Invoke-Button -ProcessId $japanese.Id -Name '設定' -Label '日本語メイン ウィンドウ'
        $settingsExpected = '言語の変更は次回起動時に反映されます。'
        if (-not (Wait-ForName -ProcessId $japanese.Id -Expected $settingsExpected)) {
            throw "E2E 失敗: 設定ウィンドウに日本語の言語注記 '$settingsExpected' が見つかりません。"
        }
        Write-Host "  OK: 日本語の設定注記 = $settingsExpected"
        Assert-NamePresent -ProcessId $japanese.Id -Expected '並列解凍' -Label '日本語並列設定'
        Assert-NamePresent -ProcessId $japanese.Id -Expected '保存' -Label '日本語保存ボタン'
        Invoke-Button -ProcessId $japanese.Id -Name 'キャンセル' -Label '日本語設定'
    }
    finally {
        Stop-Hodozip -Process $japanese
    }

    Write-Host ''
    Write-Host '多言語スモーク E2E: すべて成功しました。'
}
finally {
    if ($null -ne $settingsBackup) {
        Set-Content -LiteralPath $settingsPath -Value $settingsBackup -Encoding utf8 -NoNewline
    }
    elseif (Test-Path -LiteralPath $settingsPath) {
        Remove-Item -LiteralPath $settingsPath -Force
    }
}
