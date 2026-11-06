# Hodozip Phase 3 overwrite-flow E2E.
# Usage: pwsh -File tests/e2e/run-overwrite-e2e.ps1 [-ExePath <Hodozip.exe>]
# Requires: Release x64 build (or publish output). Uses UI Automation for the Ask case.

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
$workRoot = Join-Path $env:TEMP 'HodozipOverwriteE2E'
$archives = Join-Path $workRoot 'archives'
$payload = Join-Path $workRoot 'payload'
$settingsDirectory = Join-Path $env:LOCALAPPDATA 'Hodozip'
$settingsPath = Join-Path $settingsDirectory 'settings.json'

if (-not (Test-Path -LiteralPath $exePath)) {
    throw "Hodozip.exe が見つかりません。先に Release x64 ビルドを実行してください: $exePath"
}

function Write-Settings {
    param(
        [string]$FolderPolicy,
        [string]$OverwritePolicy
    )

    New-Item -ItemType Directory -Path $settingsDirectory -Force | Out-Null
    @{
        DestinationRule = 'SameAsArchive'
        OpenFolderAfterExtraction = $false
        CloseAfterAutoExtract = $true
        FolderCollisionPolicy = $FolderPolicy
        OverwritePolicy = $OverwritePolicy
    } | ConvertTo-Json | Set-Content -LiteralPath $settingsPath -Encoding utf8
}

function Assert-Content {
    param([string]$Path, [string]$Expected, [string]$Label)
    if (-not (Test-Path -LiteralPath $Path)) {
        throw "E2E 失敗: $Label が見つかりません: $Path"
    }

    $actual = (Get-Content -LiteralPath $Path -Raw).TrimEnd()
    if ($actual -ne $Expected) {
        throw "E2E 失敗: $Label の内容が '$Expected' ではありません: '$actual'"
    }

    Write-Host "  OK: $Label = $Expected"
}

function Assert-NotExists {
    param([string]$Path, [string]$Label)
    if (Test-Path -LiteralPath $Path) {
        throw "E2E 失敗: $Label が存在してはいけません: $Path"
    }
    Write-Host "  OK: $Label は存在しません"
}

function New-FolderZip {
    param([string]$Name, [string]$AContent)

    $zipPath = Join-Path $archives $Name
    Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue
    $source = Join-Path $payload $Name
    Remove-Item -LiteralPath $source -Recurse -Force -ErrorAction SilentlyContinue
    $top = Join-Path $source 'Top'
    New-Item -ItemType Directory -Path (Join-Path $top 'sub') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $top 'a.txt') -Value $AContent -Encoding utf8
    Set-Content -LiteralPath (Join-Path $top 'sub\b.txt') -Value 'new-b' -Encoding utf8
    Compress-Archive -LiteralPath $top -DestinationPath $zipPath -CompressionLevel Optimal
    return $zipPath
}

function Invoke-Hodozip {
    param([string]$ArchivePath, [int]$TimeoutSeconds = 30)

    $process = Start-Process -FilePath $exePath -ArgumentList ('"' + $ArchivePath + '"') -PassThru
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        try { $process.Kill($true) } catch { }
        throw "E2E 失敗: 解凍が $TimeoutSeconds 秒以内に完了しませんでした: $ArchivePath"
    }

    if ($process.ExitCode -ne 0) {
        throw "E2E 失敗: 終了コードが $($process.ExitCode) でした: $ArchivePath"
    }

    Write-Host "  終了コード: $($process.ExitCode)"
}

$settingsBackup = $null
if (Test-Path -LiteralPath $settingsPath) {
    $settingsBackup = Get-Content -LiteralPath $settingsPath -Raw
}

try {
    Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $archives, $payload | Out-Null

    Write-Host '=== E2E O1: Merge + Overwrite（既存フォルダへ統合して上書き） ==='
    $mergeZip = New-FolderZip -Name 'overwrite-merge.zip' -AContent 'new-a'
    Write-Settings -FolderPolicy 'Merge' -OverwritePolicy 'Overwrite'
    # アーカイブをケース専用の作業フォルダへ移し、事前に作成した Top と衝突させる。
    $mergeWork = Join-Path $archives 'overwrite-merge-case'
    New-Item -ItemType Directory -Path (Join-Path $mergeWork 'Top') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $mergeWork 'Top\a.txt') -Value 'old-a' -Encoding utf8
    Move-Item -LiteralPath $mergeZip -Destination (Join-Path $mergeWork 'overwrite-merge.zip') -Force
    Invoke-Hodozip -ArchivePath (Join-Path $mergeWork 'overwrite-merge.zip')
    Assert-Content -Path (Join-Path $mergeWork 'Top\a.txt') -Expected 'new-a' -Label 'Top\a.txt'
    Assert-Content -Path (Join-Path $mergeWork 'Top\sub\b.txt') -Expected 'new-b' -Label 'Top\sub\b.txt'
    Assert-NotExists -Path (Join-Path $mergeWork 'Top (2)') -Label 'Top (2)'

    Write-Host '=== E2E O2: Merge + Skip（既存ファイルを残す） ==='
    $skipWork = Join-Path $archives 'overwrite-skip-case'
    New-Item -ItemType Directory -Path (Join-Path $skipWork 'Top') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $skipWork 'Top\a.txt') -Value 'old-a' -Encoding utf8
    $skipZip = New-FolderZip -Name 'overwrite-skip.zip' -AContent 'new-a'
    Move-Item -LiteralPath $skipZip -Destination (Join-Path $skipWork 'overwrite-skip.zip') -Force
    Write-Settings -FolderPolicy 'Merge' -OverwritePolicy 'Skip'
    Invoke-Hodozip -ArchivePath (Join-Path $skipWork 'overwrite-skip.zip')
    Assert-Content -Path (Join-Path $skipWork 'Top\a.txt') -Expected 'old-a' -Label 'Top\a.txt（スキップ）'
    Assert-Content -Path (Join-Path $skipWork 'Top\sub\b.txt') -Expected 'new-b' -Label 'Top\sub\b.txt'

    Write-Host '=== E2E O3: Ask + UI Automation（上書き確認ダイアログが実際に表示される） ==='
    $askWork = Join-Path $archives 'overwrite-ask-case'
    New-Item -ItemType Directory -Path (Join-Path $askWork 'Top') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $askWork 'Top\a.txt') -Value 'old-a' -Encoding utf8
    $askZip = New-FolderZip -Name 'overwrite-ask.zip' -AContent 'new-a'
    Move-Item -LiteralPath $askZip -Destination (Join-Path $askWork 'overwrite-ask.zip') -Force
    Write-Settings -FolderPolicy 'Merge' -OverwritePolicy 'Ask'

    $process = Start-Process -FilePath $exePath -ArgumentList ('"' + (Join-Path $askWork 'overwrite-ask.zip') + '"') -PassThru
    $dialog = $null
    $deadline = [DateTime]::UtcNow.AddSeconds(25)
    while ([DateTime]::UtcNow -lt $deadline) {
        $condition = New-Object System.Windows.Automation.AndCondition(
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
                $process.Id)),
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
                'OverwriteDialog')))
        $dialog = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            $condition)

        if ($null -ne $dialog) {
            break
        }

        Start-Sleep -Milliseconds 300
    }

    if ($null -eq $dialog) {
        try { $process.Kill($true) } catch { }
        throw 'E2E 失敗: 上書き確認ダイアログ（AutomationId=OverwriteDialog）が表示されませんでした。'
    }

    Write-Host "  OK: 上書き確認ダイアログを検出しました（Name='$($dialog.Current.Name)'）"
    $cancelCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        'CancelButton')
    $cancelButton = $dialog.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cancelCondition)
    if ($null -eq $cancelButton) {
        try { $process.Kill($true) } catch { }
        throw 'E2E 失敗: 上書き確認ダイアログのキャンセル ボタンが見つかりませんでした。'
    }

    $invokePattern = $cancelButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $invokePattern.Invoke()
    Write-Host '  OK: キャンセル ボタンを UIA で押しました。'

    Start-Sleep -Seconds 2
    Assert-Content -Path (Join-Path $askWork 'Top\a.txt') -Expected 'old-a' -Label 'Top\a.txt（キャンセル）'
    Assert-NotExists -Path (Join-Path $askWork 'Top\sub\b.txt') -Label 'Top\sub\b.txt（キャンセル）'

    if (-not $process.HasExited) {
        $null = $process.CloseMainWindow()
        if (-not $process.WaitForExit(10000)) {
            try { $process.Kill($true) } catch { }
        }
    }

    Write-Host ''
    Write-Host '上書きフロー E2E: すべて成功しました。'
}
finally {
    if ($null -ne $settingsBackup) {
        Set-Content -LiteralPath $settingsPath -Value $settingsBackup -Encoding utf8 -NoNewline
    }
    elseif (Test-Path -LiteralPath $settingsPath) {
        Remove-Item -LiteralPath $settingsPath -Force
    }
}
