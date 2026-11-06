# Hodozip Phase 3 multi-format E2E.
# Usage: pwsh -File tests/e2e/run-formats-e2e.ps1 [-ExePath <Hodozip.exe>]
# Requires: Release x64 build (or publish output) and the committed format fixtures.

param(
    [string]$ExePath,
    [string]$FixtureDirectory
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if ([string]::IsNullOrWhiteSpace($ExePath)) {
    $ExePath = Join-Path $repoRoot 'src\Hodozip\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\Hodozip.exe'
}
if ([string]::IsNullOrWhiteSpace($FixtureDirectory)) {
    $FixtureDirectory = Join-Path $repoRoot 'tests\Hodozip.Core.Tests\Fixtures\formats'
}

$exePath = [System.IO.Path]::GetFullPath($ExePath)
$fixtures = [System.IO.Path]::GetFullPath($FixtureDirectory)
$workRoot = Join-Path $env:TEMP 'HodozipFormatsE2E'
$settingsDirectory = Join-Path $env:LOCALAPPDATA 'Hodozip'
$settingsPath = Join-Path $settingsDirectory 'settings.json'

if (-not (Test-Path -LiteralPath $exePath)) {
    throw "Hodozip.exe が見つかりません。先に Release x64 ビルドを実行してください: $exePath"
}

if (-not (Test-Path -LiteralPath $fixtures)) {
    throw "フィクスチャ フォルダが見つかりません: $fixtures"
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
}

function Invoke-Extraction {
    param([string]$ArchivePath)

    $process = Start-Process -FilePath $exePath -ArgumentList @('--extract-folder', ('"' + $ArchivePath + '"')) -PassThru
    if (-not $process.WaitForExit(45000)) {
        try { $process.Kill($true) } catch { }
        throw "E2E 失敗: 45 秒以内に完了しませんでした: $ArchivePath"
    }

    if ($process.ExitCode -ne 0) {
        throw "E2E 失敗: 終了コードが $($process.ExitCode) でした: $ArchivePath"
    }
}

function Test-StructuredArchive {
    param([string]$CaseDirectory, [string]$BaseName, [string]$FixturePath, [string]$Label)

    $archivePath = Join-Path $CaseDirectory ([System.IO.Path]::GetFileName($FixturePath))
    Copy-Item -LiteralPath $FixturePath -Destination $archivePath -Force
    Invoke-Extraction -ArchivePath $archivePath

    $root = Join-Path $CaseDirectory $BaseName
    Assert-Content -Path (Join-Path $root 'hello.txt') -Expected 'hello world' -Label "$Label hello.txt"
    Assert-Content -Path (Join-Path $root 'Top\a.txt') -Expected 'A content' -Label "$Label Top\a.txt"
    Assert-Content -Path (Join-Path $root 'Top\sub\b.txt') -Expected 'B content' -Label "$Label Top\sub\b.txt"
    Assert-Content -Path (Join-Path $root '日本語テスト.txt') -Expected 'こんにちは' -Label "$Label 日本語テスト.txt"
    Write-Host "  OK: $Label"
}

function Test-WrappedArchive {
    param(
        [string]$CaseDirectory,
        [string]$BaseName,
        [string]$FixturePath,
        [string]$TargetFileName,
        [string]$Label
    )

    $archivePath = Join-Path $CaseDirectory $TargetFileName
    Copy-Item -LiteralPath $FixturePath -Destination $archivePath -Force
    Invoke-Extraction -ArchivePath $archivePath
    $root = Join-Path $CaseDirectory $BaseName
    Assert-Content -Path (Join-Path $root 'hello.txt') -Expected 'hello world' -Label "$Label hello.txt"
    Assert-Content -Path (Join-Path $root 'Top\a.txt') -Expected 'A content' -Label "$Label Top\a.txt"
    Assert-Content -Path (Join-Path $root 'Top\sub\b.txt') -Expected 'B content' -Label "$Label Top\sub\b.txt"
    Assert-Content -Path (Join-Path $root '日本語テスト.txt') -Expected 'こんにちは' -Label "$Label 日本語テスト.txt"
    Write-Host "  OK: $Label"
}

function Test-BareCompressed {
    param([string]$CaseDirectory, [string]$BaseName, [string]$FixturePath, [string]$Label)

    $archivePath = Join-Path $CaseDirectory ([System.IO.Path]::GetFileName($FixturePath))
    Copy-Item -LiteralPath $FixturePath -Destination $archivePath -Force
    Invoke-Extraction -ArchivePath $archivePath
    Assert-Content -Path (Join-Path $CaseDirectory $BaseName) -Expected 'hello world' -Label "$Label $BaseName"
    Write-Host "  OK: $Label"
}

$settingsBackup = $null
if (Test-Path -LiteralPath $settingsPath) {
    $settingsBackup = Get-Content -LiteralPath $settingsPath -Raw
}

try {
    Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $workRoot | Out-Null

    New-Item -ItemType Directory -Path $settingsDirectory -Force | Out-Null
    @{
        DestinationRule = 'SameAsArchive'
        OpenFolderAfterExtraction = $false
        CloseAfterAutoExtract = $true
        FolderCollisionPolicy = 'Merge'
        OverwritePolicy = 'Overwrite'
    } | ConvertTo-Json | Set-Content -LiteralPath $settingsPath -Encoding utf8

    Write-Host '=== E2E F1: ZIP ==='
    $zipCase = Join-Path $workRoot 'zip'
    New-Item -ItemType Directory -Path (Join-Path $zipCase 'Top\sub') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $zipCase 'Top\a.txt') -Value 'A content' -Encoding utf8
    Set-Content -LiteralPath (Join-Path $zipCase 'Top\sub\b.txt') -Value 'B content' -Encoding utf8
    $zipPath = Join-Path $workRoot 'zip-case.zip'
    Compress-Archive -LiteralPath (Join-Path $zipCase 'Top') -DestinationPath $zipPath -CompressionLevel Optimal
    Remove-Item -LiteralPath (Join-Path $zipCase 'Top') -Recurse -Force
    Invoke-Extraction -ArchivePath $zipPath
    Assert-Content -Path (Join-Path $workRoot 'zip-case\Top\a.txt') -Expected 'A content' -Label 'ZIP Top\a.txt'
    Assert-Content -Path (Join-Path $workRoot 'zip-case\Top\sub\b.txt') -Expected 'B content' -Label 'ZIP Top\sub\b.txt'
    Write-Host '  OK: ZIP'

    Write-Host '=== E2E F2: 7z / TAR ==='
    $sevenCase = Join-Path $workRoot '7z'
    New-Item -ItemType Directory -Path $sevenCase -Force | Out-Null
    Test-StructuredArchive -CaseDirectory $sevenCase -BaseName 'sample' -FixturePath (Join-Path $fixtures 'sample.7z') -Label '7z'

    $tarCase = Join-Path $workRoot 'tar'
    New-Item -ItemType Directory -Path $tarCase -Force | Out-Null
    Test-StructuredArchive -CaseDirectory $tarCase -BaseName 'sample' -FixturePath (Join-Path $fixtures 'sample.tar') -Label 'TAR'

    Write-Host '=== E2E F3: RAR ==='
    $rarCase = Join-Path $workRoot 'rar'
    New-Item -ItemType Directory -Path $rarCase -Force | Out-Null
    $rarPath = Join-Path $rarCase 'sample.rar'
    Copy-Item -LiteralPath (Join-Path $fixtures 'sample.rar') -Destination $rarPath -Force
    Invoke-Extraction -ArchivePath $rarPath
    if (-not (Test-Path -LiteralPath (Join-Path $rarCase 'sample\exe\test.exe'))) {
        throw 'E2E 失敗: RAR の exe\test.exe が見つかりません。'
    }
    if (-not (Test-Path -LiteralPath (Join-Path $rarCase 'sample\jpg\test.jpg'))) {
        throw 'E2E 失敗: RAR の jpg\test.jpg が見つかりません。'
    }
    Write-Host '  OK: RAR'

    Write-Host '=== E2E F4: TAR.GZ / TGZ ==='
    $tgzCase = Join-Path $workRoot 'tar-gz'
    New-Item -ItemType Directory -Path $tgzCase -Force | Out-Null
    Test-StructuredArchive -CaseDirectory $tgzCase -BaseName 'sample' -FixturePath (Join-Path $fixtures 'sample.tar.gz') -Label 'TAR.GZ'

    $tgzAliasCase = Join-Path $workRoot 'tgz'
    New-Item -ItemType Directory -Path $tgzAliasCase -Force | Out-Null
    Test-WrappedArchive -CaseDirectory $tgzAliasCase -BaseName 'sample' -FixturePath (Join-Path $fixtures 'sample.tar.gz') -TargetFileName 'sample.tgz' -Label 'TGZ'

    Write-Host '=== E2E F5: TAR.BZ2 / TBZ2 ==='
    $tbzCase = Join-Path $workRoot 'tar-bz2'
    New-Item -ItemType Directory -Path $tbzCase -Force | Out-Null
    Test-StructuredArchive -CaseDirectory $tbzCase -BaseName 'sample' -FixturePath (Join-Path $fixtures 'sample.tar.bz2') -Label 'TAR.BZ2'

    $tbzAliasCase = Join-Path $workRoot 'tbz2'
    New-Item -ItemType Directory -Path $tbzAliasCase -Force | Out-Null
    Test-WrappedArchive -CaseDirectory $tbzAliasCase -BaseName 'sample' -FixturePath (Join-Path $fixtures 'sample.tar.bz2') -TargetFileName 'sample.tbz2' -Label 'TBZ2'

    Write-Host '=== E2E F6: TAR.XZ / TXZ ==='
    $txzCase = Join-Path $workRoot 'tar-xz'
    New-Item -ItemType Directory -Path $txzCase -Force | Out-Null
    Test-StructuredArchive -CaseDirectory $txzCase -BaseName 'sample' -FixturePath (Join-Path $fixtures 'sample.tar.xz') -Label 'TAR.XZ'

    $txzAliasCase = Join-Path $workRoot 'txz'
    New-Item -ItemType Directory -Path $txzAliasCase -Force | Out-Null
    Test-WrappedArchive -CaseDirectory $txzAliasCase -BaseName 'sample' -FixturePath (Join-Path $fixtures 'sample.tar.xz') -TargetFileName 'sample.txz' -Label 'TXZ'

    Write-Host '=== E2E F7: TAR.ZST / TZST ==='
    $tzstCase = Join-Path $workRoot 'tar-zst'
    New-Item -ItemType Directory -Path $tzstCase -Force | Out-Null
    Test-StructuredArchive -CaseDirectory $tzstCase -BaseName 'sample' -FixturePath (Join-Path $fixtures 'sample.tar.zst') -Label 'TAR.ZST'

    $tzstAliasCase = Join-Path $workRoot 'tzst'
    New-Item -ItemType Directory -Path $tzstAliasCase -Force | Out-Null
    Test-WrappedArchive -CaseDirectory $tzstAliasCase -BaseName 'sample' -FixturePath (Join-Path $fixtures 'sample.tar.zst') -TargetFileName 'sample.tzst' -Label 'TZST'

    Write-Host '=== E2E F8: GZ ==='
    $gzCase = Join-Path $workRoot 'gz'
    New-Item -ItemType Directory -Path $gzCase -Force | Out-Null
    $gzPath = Join-Path $gzCase 'sample.gz'
    Copy-Item -LiteralPath (Join-Path $fixtures 'sample.gz') -Destination $gzPath -Force
    Invoke-Extraction -ArchivePath $gzPath
    Assert-Content -Path (Join-Path $gzCase 'sample\hello.txt') -Expected 'hello world' -Label 'GZ hello.txt'
    Write-Host '  OK: GZ'

    Write-Host '=== E2E F9: BZ2 / XZ / ZST（単一ストリーム） ==='
    $bz2Case = Join-Path $workRoot 'bz2'
    New-Item -ItemType Directory -Path $bz2Case -Force | Out-Null
    Test-BareCompressed -CaseDirectory $bz2Case -BaseName 'sample' -FixturePath (Join-Path $fixtures 'sample.bz2') -Label 'BZ2'

    $xzCase = Join-Path $workRoot 'xz'
    New-Item -ItemType Directory -Path $xzCase -Force | Out-Null
    Test-BareCompressed -CaseDirectory $xzCase -BaseName 'sample' -FixturePath (Join-Path $fixtures 'sample.xz') -Label 'XZ'

    $zstCase = Join-Path $workRoot 'zst'
    New-Item -ItemType Directory -Path $zstCase -Force | Out-Null
    Test-BareCompressed -CaseDirectory $zstCase -BaseName 'sample' -FixturePath (Join-Path $fixtures 'sample.zst') -Label 'ZST'

    Write-Host '=== E2E F10: MSI ==='
    $msiCase = Join-Path $workRoot 'msi'
    New-Item -ItemType Directory -Path $msiCase -Force | Out-Null
    $msiPath = Join-Path $msiCase 'sample.msi'
    Copy-Item -LiteralPath (Join-Path $fixtures 'sample.msi') -Destination $msiPath -Force
    Invoke-Extraction -ArchivePath $msiPath
    $msiRoot = Join-Path $msiCase 'sample\Program Files\Sample'
    Assert-Content -Path (Join-Path $msiRoot 'hello.txt') -Expected 'hello world' -Label 'MSI hello.txt'
    Assert-Content -Path (Join-Path $msiRoot 'Top\a.txt') -Expected 'A content' -Label 'MSI Top\a.txt'
    Assert-Content -Path (Join-Path $msiRoot 'Top\sub\b.txt') -Expected 'B content' -Label 'MSI Top\sub\b.txt'
    Assert-Content -Path (Join-Path $msiRoot '日本語テスト.txt') -Expected 'こんにちは' -Label 'MSI 日本語テスト.txt'
    Write-Host '  OK: MSI'

    Write-Host ''
    Write-Host '多形式 E2E: すべて成功しました。'
}
finally {
    if ($null -ne $settingsBackup) {
        Set-Content -LiteralPath $settingsPath -Value $settingsBackup -Encoding utf8 -NoNewline
    }
    elseif (Test-Path -LiteralPath $settingsPath) {
        Remove-Item -LiteralPath $settingsPath -Force
    }
}
