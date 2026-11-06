# Hodozip multi-format test fixture generator.
#
# Usage:
#   pwsh -File tests/Hodozip.Core.Tests/Fixtures/formats/generate-fixtures.ps1
#
# Requirements:
#   - 7-Zip compatible CLI on PATH (7z.exe). NanaZip also works.
#   - Internet access for the RAR fixture (SharpCompress test archive, MIT).
#
# The generated files are committed to the repository. The unit tests copy them
# to the test output directory and verify that each format extracts correctly.

param(
    [string]$SevenZip = '7z'
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$scriptDirectory = $PSScriptRoot
$workRoot = Join-Path $env:TEMP ('HodozipFormatFixtures_' + [Guid]::NewGuid().ToString('N'))
$payload = Join-Path $workRoot 'payload'
$output = $scriptDirectory

function Invoke-SevenZip {
    param([string[]]$Arguments)

    & $SevenZip @Arguments | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "7z が失敗しました (exit $LASTEXITCODE): $($Arguments -join ' ')"
    }
}

try {
    New-Item -ItemType Directory -Path $payload, (Join-Path $payload 'Top\sub') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $payload 'hello.txt') -Value 'hello world' -Encoding utf8 -NoNewline
    Set-Content -LiteralPath (Join-Path $payload 'Top\a.txt') -Value 'A content' -Encoding utf8 -NoNewline
    Set-Content -LiteralPath (Join-Path $payload 'Top\sub\b.txt') -Value 'B content' -Encoding utf8 -NoNewline
    Set-Content -LiteralPath (Join-Path $payload '日本語テスト.txt') -Value 'こんにちは' -Encoding utf8 -NoNewline
    Set-Content -LiteralPath (Join-Path $payload 'secret.txt') -Value 'Hodozip 7z password test' -Encoding utf8 -NoNewline

    $files = @('hello.txt', 'Top', '日本語テスト.txt')
    Push-Location $payload
    try {
        Invoke-SevenZip @('a', '-t7z', (Join-Path $output 'sample.7z'), 'hello.txt', 'Top', '日本語テスト.txt')
        Invoke-SevenZip @('a', '-ttar', (Join-Path $output 'sample.tar'), 'hello.txt', 'Top', '日本語テスト.txt')

        Invoke-SevenZip @('a', '-tgzip', (Join-Path $output 'sample.tar.gz'), (Join-Path $output 'sample.tar'))
        Invoke-SevenZip @('a', '-tbzip2', (Join-Path $output 'sample.tar.bz2'), (Join-Path $output 'sample.tar'))
        Invoke-SevenZip @('a', '-txz', (Join-Path $output 'sample.tar.xz'), (Join-Path $output 'sample.tar'))
        Invoke-SevenZip @('a', '-tzstd', (Join-Path $output 'sample.tar.zst'), (Join-Path $output 'sample.tar'))

        # 短縮拡張子の別名。同じ内容を別名でも解凍できることを検証する。
        Copy-Item (Join-Path $output 'sample.tar.gz') (Join-Path $output 'sample.tgz')
        Copy-Item (Join-Path $output 'sample.tar.bz2') (Join-Path $output 'sample.tbz2')
        Copy-Item (Join-Path $output 'sample.tar.xz') (Join-Path $output 'sample.txz')
        Copy-Item (Join-Path $output 'sample.tar.zst') (Join-Path $output 'sample.tzst')

        Invoke-SevenZip @('a', '-tgzip', (Join-Path $output 'sample.gz'), 'hello.txt')
        Invoke-SevenZip @('a', '-tbzip2', (Join-Path $output 'sample.bz2'), 'hello.txt')
        Invoke-SevenZip @('a', '-txz', (Join-Path $output 'sample.xz'), 'hello.txt')
        Invoke-SevenZip @('a', '-tzstd', (Join-Path $output 'sample.zst'), 'hello.txt')

        Invoke-SevenZip @('a', '-t7z', '-psecret', (Join-Path $output 'password.7z'), 'secret.txt')
    }
    finally {
        Pop-Location
    }

    $rarUrl = 'https://raw.githubusercontent.com/adamhathcock/sharpcompress/master/tests/TestArchives/Archives/Rar5.rar'
    Invoke-WebRequest -Uri $rarUrl -OutFile (Join-Path $output 'sample.rar') -Headers @{ 'User-Agent' = 'Hodozip-tests' }

    Get-ChildItem -LiteralPath $output -File |
        Sort-Object Name |
        Select-Object Name, Length |
        Format-Table -AutoSize
}
finally {
    Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue
}
