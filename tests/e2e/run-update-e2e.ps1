# Hodozip Phase 5 auto-update E2E.
# Usage: pwsh -File tests/e2e/run-update-e2e.ps1 [-ExePath <Hodozip.exe>]
#
# Verifies the daily throttle, the startup-check toggle, and the default ON
# behaviour using the real settings file and a normal unpackaged app launch.
# Version detection, skip policy and digest verification are covered by the
# mocked unit tests in Hodozip.Core.Tests.

param(
    [string]$ExePath
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

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

$settingsBackup = $null
$hadSettings = $false
if (Test-Path -LiteralPath $settingsPath) {
    $settingsBackup = Get-Content -LiteralPath $settingsPath -Raw
    $hadSettings = $true
}

function Write-SettingsJson {
    param([string]$Json)
    New-Item -ItemType Directory -Path $settingsDirectory -Force | Out-Null
    Set-Content -LiteralPath $settingsPath -Value $Json -Encoding utf8 -NoNewline
}

function Start-And-CloseHodozip {
    param([int]$HoldSeconds = 4)

    $process = Start-Process -FilePath $exePath -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while (-not $process.HasExited -and [DateTime]::UtcNow -lt $deadline) {
        $process.Refresh()
        if ($process.MainWindowTitle -eq 'Hodozip') {
            break
        }
        Start-Sleep -Milliseconds 200
    }

    if ($process.HasExited -or $process.MainWindowTitle -ne 'Hodozip') {
        try { $process.Kill($true) } catch { }
        throw "E2E 失敗: Hodozip ウィンドウを確認できませんでした（タイトル: '$($process.MainWindowTitle)'）。"
    }

    Start-Sleep -Seconds $HoldSeconds
    $null = $process.CloseMainWindow()
    if (-not $process.WaitForExit(15000)) {
        try { $process.Kill($true) } catch { }
        throw 'E2E 失敗: Hodozip のウィンドウを閉じてもプロセスが終了しませんでした。'
    }
}

try {
    Write-Host '=== U1: 前回チェックから 1 日未満なら再チェックしない ==='
    $recent = [DateTimeOffset]::UtcNow.AddHours(-1).ToString('o')
    $recentJson = @{
        DestinationRule = 'SameAsArchive'
        OpenFolderAfterExtraction = $false
        CloseAfterAutoExtract = $false
        FolderCollisionPolicy = 'Merge'
        OverwritePolicy = 'Overwrite'
        CheckForUpdatesOnStartup = $true
        LastUpdateCheckUtc = $recent
    } | ConvertTo-Json
    Write-SettingsJson -Json $recentJson
    $before = Get-Content -LiteralPath $settingsPath -Raw
    Start-And-CloseHodozip -HoldSeconds 4
    $after = Get-Content -LiteralPath $settingsPath -Raw
    if ($before -ne $after) {
        throw 'E2E 失敗: 1 日以内の起動で settings.json が変更されました（更新確認が間引きされていません）。'
    }
    Write-Host '  OK: 1 日以内の起動では LastUpdateCheckUtc が変更されない'

    Write-Host '=== U2: 設定トグル OFF なら更新確認しない ==='
    $disabledJson = @{
        DestinationRule = 'SameAsArchive'
        OpenFolderAfterExtraction = $false
        CloseAfterAutoExtract = $false
        FolderCollisionPolicy = 'Merge'
        OverwritePolicy = 'Overwrite'
        CheckForUpdatesOnStartup = $false
        LastUpdateCheckUtc = $null
    } | ConvertTo-Json
    Write-SettingsJson -Json $disabledJson
    $before = Get-Content -LiteralPath $settingsPath -Raw
    Start-And-CloseHodozip -HoldSeconds 4
    $after = Get-Content -LiteralPath $settingsPath -Raw
    if ($before -ne $after) {
        throw 'E2E 失敗: 更新確認が OFF なのに settings.json が変更されました。'
    }
    Write-Host '  OK: トグル OFF では LastUpdateCheckUtc が書き込まれない'

    Write-Host '=== U3: 未設定時は既定で更新確認が ON ==='
    $defaultJson = @{
        DestinationRule = 'SameAsArchive'
        OpenFolderAfterExtraction = $false
        CloseAfterAutoExtract = $false
        FolderCollisionPolicy = 'Merge'
        OverwritePolicy = 'Overwrite'
        LastUpdateCheckUtc = $null
    } | ConvertTo-Json
    Write-SettingsJson -Json $defaultJson
    Start-And-CloseHodozip -HoldSeconds 6
    $saved = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    if ($saved.CheckForUpdatesOnStartup -ne $true) {
        throw "E2E 失敗: 未設定時の既定値が ON ではありません: '$($saved.CheckForUpdatesOnStartup)'"
    }
    if ($null -eq $saved.LastUpdateCheckUtc) {
        throw 'E2E 失敗: 既定 ON の初回起動で更新確認が記録されませんでした。'
    }
    Write-Host "  OK: 既定 ON で LastUpdateCheckUtc = $($saved.LastUpdateCheckUtc)"

    Write-Host ''
    Write-Host 'アップデート E2E: すべて成功しました。'
}
finally {
    if ($hadSettings) {
        Set-Content -LiteralPath $settingsPath -Value $settingsBackup -Encoding utf8 -NoNewline
    }
    elseif (Test-Path -LiteralPath $settingsPath) {
        Remove-Item -LiteralPath $settingsPath -Force
    }
}
