# Hodozip Phase 5 compression E2E.
# Usage: pwsh -File tests/e2e/run-compression-e2e.ps1 [-ExePath <Hodozip.exe>] [-SevenZipPath <7z.exe>]
#
# Verifies ZIP / 7z creation, round-trip byte equality through Hodozip extraction,
# AES password ZIP, Shift_JIS names, collision numbering and 7z password handling.

param(
    [string]$ExePath,
    [string]$SevenZipPath = 'C:\Program Files\7-Zip\7z.exe'
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if ([string]::IsNullOrWhiteSpace($ExePath)) {
    $ExePath = Join-Path $repoRoot 'src\Hodozip\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\Hodozip.exe'
}

$exePath = [System.IO.Path]::GetFullPath($ExePath)
$sevenZipPath = [System.IO.Path]::GetFullPath($SevenZipPath)
$workRoot = Join-Path $env:TEMP 'HodozipCompressionE2E'
$payload = Join-Path $workRoot 'payload'
$archives = Join-Path $workRoot 'archives'
$settingsDirectory = Join-Path $env:LOCALAPPDATA 'Hodozip'
$settingsPath = Join-Path $settingsDirectory 'settings.json'

if (-not (Test-Path -LiteralPath $exePath)) {
    throw "Hodozip.exe が見つかりません。先に Release x64 ビルドを実行してください: $exePath"
}
if (-not (Test-Path -LiteralPath $sevenZipPath)) {
    throw "7-Zip が見つかりません: $sevenZipPath"
}

$settingsBackup = $null
$hadSettings = $false
if (Test-Path -LiteralPath $settingsPath) {
    $settingsBackup = Get-Content -LiteralPath $settingsPath -Raw
    $hadSettings = $true
}

function Assert-Exists {
    param([string]$Path, [string]$Label)
    if (-not (Test-Path -LiteralPath $Path)) {
        throw "E2E 失敗: $Label が見つかりません: $Path"
    }
    Write-Host "  OK: $Label"
}

function Assert-NotExists {
    param([string]$Path, [string]$Label)
    if (Test-Path -LiteralPath $Path) {
        throw "E2E 失敗: $Label が存在してはいけません: $Path"
    }
    Write-Host "  OK: $Label は存在しません"
}

function Assert-FileEquals {
    param([string]$ExpectedPath, [string]$ActualPath, [string]$Label)
    Assert-Exists -Path $ActualPath -Label $Label
    $expectedHash = (Get-FileHash -LiteralPath $ExpectedPath -Algorithm SHA256).Hash
    $actualHash = (Get-FileHash -LiteralPath $ActualPath -Algorithm SHA256).Hash
    if ($expectedHash -ne $actualHash) {
        throw "E2E 失敗: $Label の内容が一致しません ($expectedHash / $actualHash)"
    }
    Write-Host "  OK: $Label の内容が一致"
}

function Invoke-Hodozip {
    param([string[]]$Arguments, [string]$Label)

    $process = Start-Process -FilePath $exePath -ArgumentList $Arguments -PassThru -Wait
    if ($process.ExitCode -ne 0) {
        throw "E2E 失敗: $Label が終了コード $($process.ExitCode) で失敗しました。"
    }
    Write-Host "  OK: $Label (exit 0)"
}

function Invoke-Compression {
    param([string[]]$Sources, [string]$OutputPath, [string]$Format = 'zip', [string[]]$ExtraArguments = @())

    $arguments = @('--compress')
    foreach ($source in $Sources) {
        $arguments += ('"' + $source + '"')
    }
    $arguments += @('--output', ('"' + $OutputPath + '"'), '--format', $Format)
    $arguments += $ExtraArguments
    Invoke-Hodozip -Arguments $arguments -Label "圧縮 $Format -> $(Split-Path -Leaf $OutputPath)"
}

function Test-SevenZipArchive {
    param([string]$ArchivePath, [string]$Password = '')

    $arguments = @('t', $ArchivePath)
    if (-not [string]::IsNullOrEmpty($Password)) {
        $arguments += "-p$Password"
    }
    & $sevenZipPath @arguments | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "E2E 失敗: 7-Zip の整合性テストに失敗しました: $ArchivePath (exit $LASTEXITCODE)"
    }
    Write-Host "  OK: 7-Zip 整合性テスト $(Split-Path -Leaf $ArchivePath)"
}

function Compare-Tree {
    param([string]$ExpectedRoot, [string]$ActualRoot, [string]$Label)

    $expectedFiles = Get-ChildItem -LiteralPath $ExpectedRoot -Recurse -File
    if ($expectedFiles.Count -eq 0) {
        throw "E2E 失敗: 比較対象のファイルがありません: $ExpectedRoot"
    }

    foreach ($file in $expectedFiles) {
        $relative = [System.IO.Path]::GetRelativePath($ExpectedRoot, $file.FullName)
        $actual = Join-Path $ActualRoot $relative
        Assert-FileEquals -ExpectedPath $file.FullName -ActualPath $actual -Label "$Label/$relative"
    }
}

try {
    Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $payload, $archives | Out-Null

    $top = Join-Path $payload 'Top'
    New-Item -ItemType Directory -Path (Join-Path $top 'sub'), (Join-Path $top 'empty') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $top 'a.txt') -Value 'compression E2E A' -Encoding utf8
    Set-Content -LiteralPath (Join-Path $top 'sub\b.txt') -Value 'compression E2E B' -Encoding utf8
    Set-Content -LiteralPath (Join-Path $top 'sub\日本語ファイル.txt') -Value '日本語の内容' -Encoding utf8

    New-Item -ItemType Directory -Path $settingsDirectory -Force | Out-Null
    @{
        DestinationRule = 'SameAsArchive'
        OpenFolderAfterExtraction = $false
        CloseAfterAutoExtract = $true
        FolderCollisionPolicy = 'Merge'
        OverwritePolicy = 'Overwrite'
    } | ConvertTo-Json | Set-Content -LiteralPath $settingsPath -Encoding utf8

    Write-Host '=== C1: ZIP (UTF-8, 最高圧縮) の作成とラウンドトリップ ==='
    $zipPath = Join-Path $archives 'roundtrip.zip'
    Invoke-Compression -Sources @($top) -OutputPath $zipPath -Format 'zip' -ExtraArguments @('--level', 'max')
    Test-SevenZipArchive -ArchivePath $zipPath
    Invoke-Hodozip -Arguments @('--extract-here', ('"' + $zipPath + '"')) -Label 'Hodozip で ZIP を解凍'
    Compare-Tree -ExpectedRoot $top -ActualRoot (Join-Path $archives 'Top') -Label 'ZIP'
    Assert-Exists -Path (Join-Path $archives 'Top\empty') -Label '空フォルダ Top\empty'

    Write-Host '=== C2: 7z の作成とラウンドトリップ ==='
    $sevenZipArchive = Join-Path $archives 'roundtrip.7z'
    Invoke-Compression -Sources @($top) -OutputPath $sevenZipArchive -Format '7z'
    Test-SevenZipArchive -ArchivePath $sevenZipArchive
    Remove-Item -LiteralPath (Join-Path $archives 'Top') -Recurse -Force
    Invoke-Hodozip -Arguments @('--extract-here', ('"' + $sevenZipArchive + '"')) -Label 'Hodozip で 7z を解凍'
    Compare-Tree -ExpectedRoot $top -ActualRoot (Join-Path $archives 'Top') -Label '7z'

    Write-Host '=== C3: AES パスワード付き ZIP ==='
    $aesZip = Join-Path $archives 'password-aes.zip'
    $password = 'HodozipE2E!42'
    Invoke-Compression -Sources @($top) -OutputPath $aesZip -Format 'zip' -ExtraArguments @('--password', $password)
    Test-SevenZipArchive -ArchivePath $aesZip -Password $password
    & $sevenZipPath t "-pwrong-password" $aesZip | Out-Null
    if ($LASTEXITCODE -eq 0) {
        throw 'E2E 失敗: 誤ったパスワードでも AES ZIP のテストが成功しました。'
    }
    Write-Host '  OK: 誤ったパスワードでは 7-Zip テストが失敗'

    Write-Host '=== C4: Shift_JIS ファイル名の ZIP ==='
    $shiftJisZip = Join-Path $archives 'shiftjis.zip'
    Invoke-Compression -Sources @($top) -OutputPath $shiftJisZip -Format 'zip' -ExtraArguments @('--zip-encoding', 'sjis')
    Test-SevenZipArchive -ArchivePath $shiftJisZip
    Remove-Item -LiteralPath (Join-Path $archives 'Top') -Recurse -Force
    Invoke-Hodozip -Arguments @('--extract-here', ('"' + $shiftJisZip + '"')) -Label 'Hodozip で Shift_JIS ZIP を解凍'
    Assert-Exists -Path (Join-Path $archives 'Top\sub\日本語ファイル.txt') -Label 'Shift_JIS ZIP の日本語ファイル名'
    Assert-FileEquals `
        -ExpectedPath (Join-Path $top 'sub\日本語ファイル.txt') `
        -ActualPath (Join-Path $archives 'Top\sub\日本語ファイル.txt') `
        -Label 'Shift_JIS ZIP の日本語ファイル内容'

    Write-Host '=== C5: 出力ファイルの衝突時に連番を付ける ==='
    $collisionZip = Join-Path $archives 'collision.zip'
    Invoke-Compression -Sources @($top) -OutputPath $collisionZip -Format 'zip'
    $firstHash = (Get-FileHash -LiteralPath $collisionZip -Algorithm SHA256).Hash
    Set-Content -LiteralPath (Join-Path $top 'a.txt') -Value 'compression E2E A updated' -Encoding utf8
    Invoke-Compression -Sources @($top) -OutputPath $collisionZip -Format 'zip'
    $numberedZip = Join-Path $archives 'collision (2).zip'
    Assert-Exists -Path $numberedZip -Label '連番付き collision (2).zip'
    $secondHash = (Get-FileHash -LiteralPath $numberedZip -Algorithm SHA256).Hash
    if ($firstHash -eq $secondHash) {
        throw 'E2E 失敗: 連番付き ZIP が元の ZIP と同じ内容です。'
    }
    Test-SevenZipArchive -ArchivePath $numberedZip

    Write-Host '=== C6: --overwrite で既存ファイルを置き換える ==='
    Invoke-Compression -Sources @($top) -OutputPath $collisionZip -Format 'zip' -ExtraArguments @('--overwrite')
    $overwrittenHash = (Get-FileHash -LiteralPath $collisionZip -Algorithm SHA256).Hash
    if ($overwrittenHash -ne $secondHash) {
        throw 'E2E 失敗: --overwrite 後の ZIP が連番 ZIP と一致しません。'
    }
    Assert-NotExists -Path (Join-Path $archives 'collision (3).zip') -Label '不要な collision (3).zip'
    Test-SevenZipArchive -ArchivePath $collisionZip

    Write-Host '=== C7: 7z ではパスワードが無視される ==='
    $sevenZipWithPassword = Join-Path $archives 'password-ignored.7z'
    Invoke-Compression -Sources @($top) -OutputPath $sevenZipWithPassword -Format '7z' -ExtraArguments @('--password', 'ignored')
    Test-SevenZipArchive -ArchivePath $sevenZipWithPassword

    Write-Host ''
    Write-Host '圧縮 E2E: すべて成功しました。'
    Write-Host "出力先: $archives"
}
finally {
    if ($hadSettings) {
        Set-Content -LiteralPath $settingsPath -Value $settingsBackup -Encoding utf8 -NoNewline
    }
    elseif (Test-Path -LiteralPath $settingsPath) {
        Remove-Item -LiteralPath $settingsPath -Force
    }
}
