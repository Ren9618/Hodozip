# Hodozip E2E test
# Usage: pwsh -File tests/e2e/run-e2e.ps1
#        pwsh -File tests/e2e/run-e2e.ps1 -ExePath <publish>\Hodozip.exe
# Requires: Release x64 build (or publish output) already produced.

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
$workRoot = Join-Path $env:TEMP 'HodozipE2E'
$archives = Join-Path $workRoot 'archives'
$payload = Join-Path $workRoot 'payload'
$settingsDirectory = Join-Path $env:LOCALAPPDATA 'Hodozip'
$settingsPath = Join-Path $settingsDirectory 'settings.json'

if (-not (Test-Path $exePath)) {
    throw "Hodozip.exe が見つかりません。先に Release x64 ビルドを実行してください: $exePath"
}

function Assert-Exists {
    param([string]$Path, [string]$Label)
    if (-not (Test-Path $Path)) {
        throw "E2E 失敗: $Label が見つかりません: $Path"
    }
    Write-Host "  OK: $Label"
}

function Assert-NotExists {
    param([string]$Path, [string]$Label)
    if (Test-Path $Path) {
        throw "E2E 失敗: $Label が存在してはいけません: $Path"
    }
    Write-Host "  OK: $Label は存在しません"
}

$settingsBackup = $null
if (Test-Path $settingsPath) {
    $settingsBackup = Get-Content -LiteralPath $settingsPath -Raw
}

try {
    Write-Host '=== テストデータ作成 ==='
    Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $archives, $payload | Out-Null

    # 単一フォルダ型: Top/a.txt, Top/sub/b.txt
    $top = Join-Path $payload 'Top'
    New-Item -ItemType Directory -Path (Join-Path $top 'sub') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $top 'a.txt') -Value 'single folder A' -Encoding utf8
    Set-Content -LiteralPath (Join-Path $top 'sub\b.txt') -Value 'single folder B' -Encoding utf8
    Compress-Archive -LiteralPath $top -DestinationPath (Join-Path $archives 'single-folder.zip') -CompressionLevel Optimal

    # バラバラ型: readme.txt, data/value.txt
    $scattered = Join-Path $payload 'scattered'
    New-Item -ItemType Directory -Path (Join-Path $scattered 'data') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $scattered 'readme.txt') -Value 'scattered readme' -Encoding utf8
    Set-Content -LiteralPath (Join-Path $scattered 'data\value.txt') -Value 'scattered value' -Encoding utf8
    Compress-Archive -Path (Join-Path $scattered '*') -DestinationPath (Join-Path $archives 'scattered.zip') -CompressionLevel Optimal

    # --extract-here 用の単一フォルダ型: HereRoot/here.txt, HereRoot/sub/here2.txt
    $hereMode = Join-Path $payload 'here-mode'
    New-Item -ItemType Directory -Path (Join-Path $hereMode 'HereRoot\sub') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $hereMode 'HereRoot\here.txt') -Value 'extract here' -Encoding utf8
    Set-Content -LiteralPath (Join-Path $hereMode 'HereRoot\sub\here2.txt') -Value 'extract here 2' -Encoding utf8
    Compress-Archive -LiteralPath (Join-Path $hereMode 'HereRoot') -DestinationPath (Join-Path $archives 'here-mode.zip') -CompressionLevel Optimal

    # --extract-folder 用の単一フォルダ型: FolderRoot/f.txt
    $folderMode = Join-Path $payload 'folder-mode'
    New-Item -ItemType Directory -Path (Join-Path $folderMode 'FolderRoot') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $folderMode 'FolderRoot\f.txt') -Value 'extract folder' -Encoding utf8
    Compress-Archive -LiteralPath (Join-Path $folderMode 'FolderRoot') -DestinationPath (Join-Path $archives 'folder-mode.zip') -CompressionLevel Optimal

    Write-Host "  単一フォルダ型: $(Join-Path $archives 'single-folder.zip')"
    Write-Host "  バラバラ型:     $(Join-Path $archives 'scattered.zip')"
    Write-Host "  here モード:    $(Join-Path $archives 'here-mode.zip')"
    Write-Host "  folder モード:  $(Join-Path $archives 'folder-mode.zip')"

    Write-Host '=== コマンドライン起動モードの設定 ==='
    New-Item -ItemType Directory -Path $settingsDirectory -Force | Out-Null
    @{
        DestinationRule = 'SameAsArchive'
        OpenFolderAfterExtraction = $false
        CloseAfterAutoExtract = $true
        FolderCollisionPolicy = 'Merge'
        OverwritePolicy = 'Overwrite'
    } | ConvertTo-Json | Set-Content -LiteralPath $settingsPath -Encoding utf8
    Write-Host '  完了後に自動で閉じる設定を有効化しました。'

    Write-Host '=== E2E 1: 単一フォルダ型 ==='
    $process = Start-Process -FilePath $exePath -ArgumentList ('"' + (Join-Path $archives 'single-folder.zip') + '"') -PassThru
    if (-not $process.WaitForExit(30000)) {
        try { $process.Kill($true) } catch { }
        throw 'E2E 失敗: 単一フォルダ型の解凍が 30 秒以内に完了しませんでした。'
    }
    if ($process.ExitCode -ne 0) {
        throw "E2E 失敗: 単一フォルダ型の終了コードが $($process.ExitCode) でした。"
    }
    Write-Host "  終了コード: $($process.ExitCode)"
    Assert-Exists (Join-Path $archives 'Top\a.txt') 'Top\a.txt'
    Assert-Exists (Join-Path $archives 'Top\sub\b.txt') 'Top\sub\b.txt'
    Assert-NotExists (Join-Path $archives 'single-folder') '二重フォルダ single-folder'
    $singleContents = Get-ChildItem -LiteralPath (Join-Path $archives 'Top') -Recurse -File |
        ForEach-Object { $_.FullName.Substring($archives.Length + 1) }
    Write-Host '  出力ファイル一覧:'
    $singleContents | ForEach-Object { Write-Host "    $_" }

    Write-Host '=== E2E 2: バラバラ型 ==='
    $process = Start-Process -FilePath $exePath -ArgumentList ('"' + (Join-Path $archives 'scattered.zip') + '"') -PassThru
    if (-not $process.WaitForExit(30000)) {
        try { $process.Kill($true) } catch { }
        throw 'E2E 失敗: バラバラ型の解凍が 30 秒以内に完了しませんでした。'
    }
    if ($process.ExitCode -ne 0) {
        throw "E2E 失敗: バラバラ型の終了コードが $($process.ExitCode) でした。"
    }
    Write-Host "  終了コード: $($process.ExitCode)"
    Assert-Exists (Join-Path $archives 'scattered\readme.txt') 'scattered\readme.txt'
    Assert-Exists (Join-Path $archives 'scattered\data\value.txt') 'scattered\data\value.txt'
    $scatteredContents = Get-ChildItem -LiteralPath (Join-Path $archives 'scattered') -Recurse -File |
        ForEach-Object { $_.FullName.Substring($archives.Length + 1) }
    Write-Host '  出力ファイル一覧:'
    $scatteredContents | ForEach-Object { Write-Host "    $_" }

    Write-Host '=== E2E 3: --extract-here（単一フォルダ型を二重フォルダにしない） ==='
    $process = Start-Process -FilePath $exePath -ArgumentList @('--extract-here', ('"' + (Join-Path $archives 'here-mode.zip') + '"')) -PassThru
    if (-not $process.WaitForExit(30000)) {
        try { $process.Kill($true) } catch { }
        throw 'E2E 失敗: --extract-here の解凍が 30 秒以内に完了しませんでした。'
    }
    if ($process.ExitCode -ne 0) {
        throw "E2E 失敗: --extract-here の終了コードが $($process.ExitCode) でした。"
    }
    Write-Host "  終了コード: $($process.ExitCode)"
    Assert-Exists (Join-Path $archives 'HereRoot\here.txt') 'HereRoot\here.txt'
    Assert-Exists (Join-Path $archives 'HereRoot\sub\here2.txt') 'HereRoot\sub\here2.txt'
    Assert-NotExists (Join-Path $archives 'here-mode') '--extract-here で作られない here-mode フォルダ'

    Write-Host '=== E2E 4: --extract-folder（単一フォルダ型でもアーカイブ名フォルダを作る） ==='
    $process = Start-Process -FilePath $exePath -ArgumentList @('--extract-folder', ('"' + (Join-Path $archives 'folder-mode.zip') + '"')) -PassThru
    if (-not $process.WaitForExit(30000)) {
        try { $process.Kill($true) } catch { }
        throw 'E2E 失敗: --extract-folder の解凍が 30 秒以内に完了しませんでした。'
    }
    if ($process.ExitCode -ne 0) {
        throw "E2E 失敗: --extract-folder の終了コードが $($process.ExitCode) でした。"
    }
    Write-Host "  終了コード: $($process.ExitCode)"
    Assert-Exists (Join-Path $archives 'folder-mode\FolderRoot\f.txt') 'folder-mode\FolderRoot\f.txt'
    Assert-NotExists (Join-Path $archives 'FolderRoot') '--extract-folder で作られない FolderRoot（直下）'

    Write-Host '=== E2E 5: 引数なし起動でウィンドウ表示 ==='
    $process = Start-Process -FilePath $exePath -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while (-not $process.HasExited -and [DateTime]::UtcNow -lt $deadline) {
        $process.Refresh()
        if ($process.MainWindowTitle -eq 'Hodozip') {
            break
        }
        Start-Sleep -Milliseconds 200
    }

    if ($process.HasExited -or $process.MainWindowTitle -ne 'Hodozip') {
        try { $process.Kill($true) } catch { }
        throw "E2E 失敗: 引数なし起動で Hodozip ウィンドウを確認できませんでした（タイトル: '$($process.MainWindowTitle)'）。"
    }
    Write-Host "  OK: ウィンドウタイトル '$($process.MainWindowTitle)'"
    $null = $process.CloseMainWindow()
    if (-not $process.WaitForExit(10000)) {
        try { $process.Kill($true) } catch { }
        throw 'E2E 失敗: 引数なし起動のウィンドウを閉じてもプロセスが終了しませんでした。'
    }
    Write-Host '  OK: ウィンドウを閉じて終了しました。'

    Write-Host ''
    Write-Host 'E2E: すべて成功しました。'
    Write-Host "出力先: $archives"
}
finally {
    if ($null -ne $settingsBackup) {
        Set-Content -LiteralPath $settingsPath -Value $settingsBackup -Encoding utf8 -NoNewline
    }
    elseif (Test-Path $settingsPath) {
        Remove-Item -LiteralPath $settingsPath -Force
    }
}
