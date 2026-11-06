# Hodozip Inno Setup インストーラー ビルドスクリプト。
#
# unpackaged の自己完結 publish 出力を Inno Setup で 1 つの setup.exe にまとめる。
# 管理者権限は不要で、既定ではユーザーごとの %LOCALAPPDATA%\Programs\Hodozip に
# インストールする。
#
# Usage:
#   pwsh -File packaging/build-installer.ps1
#   pwsh -File packaging/build-installer.ps1 -InnoCompilerPath "C:\...\ISCC.exe"
#   pwsh -File packaging/build-installer.ps1 -SkipPublish

[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Platform = 'x64',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\installer'),
    [string]$InnoCompilerPath,
    [switch]$SkipPublish,
    [switch]$SkipComBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$propsPath = Join-Path $repoRoot 'Directory.Build.props'
$appProject = Join-Path $repoRoot 'src\Hodozip\Hodozip.csproj'
$comProject = Join-Path $repoRoot 'src\Hodozip.ContextMenu\Hodozip.ContextMenu.vcxproj'
$comDll = Join-Path $repoRoot 'src\Hodozip.ContextMenu\bin\x64\Release\Hodozip.ContextMenu.dll'
$issPath = Join-Path $PSScriptRoot 'Hodozip.iss'
$generateAssets = Join-Path $PSScriptRoot 'generate-assets.ps1'

if (-not (Test-Path $propsPath)) {
    throw "Directory.Build.props が見つかりません: $propsPath"
}

if (-not (Test-Path $issPath)) {
    throw "Inno Setup スクリプトが見つかりません: $issPath"
}

function Find-MSBuild {
    $candidates = @(
        (Join-Path ${env:ProgramFiles} 'Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe'),
        (Join-Path ${env:ProgramFiles} 'Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe'),
        (Join-Path ${env:ProgramFiles} 'Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe'),
        (Join-Path ${env:ProgramFiles} 'Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe')
    )

    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $found = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find 'MSBuild\**\Bin\MSBuild.exe'
        if ($LASTEXITCODE -eq 0) {
            $candidates += @($found)
        }
    }

    foreach ($candidate in $candidates) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and (Test-Path $candidate)) {
            return $candidate
        }
    }

    throw 'MSBuild.exe が見つかりません。Visual Studio 2022 の C++ ビルドツールをインストールしてください。'
}

function Find-InnoCompiler {
    param([string]$ExplicitPath)

    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        if (Test-Path -LiteralPath $ExplicitPath) {
            return [System.IO.Path]::GetFullPath($ExplicitPath)
        }

        throw "指定された Inno Setup コンパイラーが見つかりません: $ExplicitPath"
    }

    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    )

    foreach ($candidate in $candidates) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and (Test-Path $candidate)) {
            return $candidate
        }
    }

    $command = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    throw 'ISCC.exe が見つかりません。Inno Setup 6 をインストールするか、-InnoCompilerPath を指定してください。'
}

function New-IcoFromPng {
    param(
        [string]$PngPath,
        [string]$IcoPath,
        [int]$Size = 256
    )

    Add-Type -AssemblyName System.Drawing

    $source = [System.Drawing.Image]::FromFile($PngPath)
    try {
        $bitmap = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
            try {
                $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
                $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
                $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                $graphics.DrawImage($source, 0, 0, $Size, $Size)
            }
            finally {
                $graphics.Dispose()
            }

            $pngStream = [System.IO.MemoryStream]::new()
            try {
                $bitmap.Save($pngStream, [System.Drawing.Imaging.ImageFormat]::Png)
                $pngBytes = $pngStream.ToArray()
            }
            finally {
                $pngStream.Dispose()
            }
        }
        finally {
            $bitmap.Dispose()
        }
    }
    finally {
        $source.Dispose()
    }

    $stream = [System.IO.MemoryStream]::new()
    $writer = [System.IO.BinaryWriter]::new($stream)
    try {
        # ICO header: reserved, type (1 = icon), image count.
        $writer.Write([UInt16]0)
        $writer.Write([UInt16]1)
        $writer.Write([UInt16]1)

        # Single 256x256 PNG-compressed frame. A dimension byte of 0 means 256.
        $writer.Write([Byte]0)
        $writer.Write([Byte]0)
        $writer.Write([Byte]0)
        $writer.Write([Byte]0)
        $writer.Write([UInt16]1)
        $writer.Write([UInt16]32)
        $writer.Write([UInt32]$pngBytes.Length)
        $writer.Write([UInt32]22)
        $writer.Write($pngBytes)
        $writer.Flush()

        [System.IO.File]::WriteAllBytes($IcoPath, $stream.ToArray())
    }
    finally {
        $writer.Dispose()
        $stream.Dispose()
    }
}

# MSBuild で評価した値を使う。Directory.Build.props の
# HodozipPackageVersion は $(HodozipVersion).0 と定義されているため、
# XML をそのまま読むとプロパティ式が残ってしまう。
$versionJson = (& dotnet msbuild $appProject -getProperty:HodozipVersion,HodozipPackageVersion -nologo | Out-String)
if ($LASTEXITCODE -ne 0) {
    throw "バージョン情報の取得に失敗しました (exit $LASTEXITCODE)。"
}

$versionProperties = $versionJson | ConvertFrom-Json
$version = [string]$versionProperties.Properties.HodozipVersion
$packageVersion = [string]$versionProperties.Properties.HodozipPackageVersion

if ([string]::IsNullOrWhiteSpace($version) -or [string]::IsNullOrWhiteSpace($packageVersion)) {
    throw 'Directory.Build.props に HodozipVersion / HodozipPackageVersion がありません。'
}

$resolvedOutput = [System.IO.Path]::GetFullPath($OutputDirectory)
$publishDirectory = Join-Path $resolvedOutput 'publish'
$assetsDirectory = Join-Path $resolvedOutput 'assets'
$iconPath = Join-Path $assetsDirectory 'Hodozip.ico'
$setupPath = Join-Path $resolvedOutput "Hodozip-$version-setup.exe"
$hashPath = "$setupPath.sha256"

New-Item -ItemType Directory -Path $resolvedOutput -Force | Out-Null
New-Item -ItemType Directory -Path $assetsDirectory -Force | Out-Null

if (-not $SkipPublish) {
    Write-Host ''
    Write-Host '=== unpackaged publish ==='
    Remove-Item -LiteralPath $publishDirectory -Recurse -Force -ErrorAction SilentlyContinue
    & dotnet publish $appProject -c $Configuration -p:Platform=$Platform -r win-x64 --self-contained true -o $publishDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish が失敗しました (exit $LASTEXITCODE)。"
    }
}

$requiredPublishFiles = @(
    (Join-Path $publishDirectory 'Hodozip.exe'),
    (Join-Path $publishDirectory 'Hodozip.dll'),
    (Join-Path $publishDirectory 'Hodozip.pri'),
    (Join-Path $publishDirectory 'AboutWindow.xbf')
)

foreach ($required in $requiredPublishFiles) {
    if (-not (Test-Path $required)) {
        throw "publish 出力に必要なファイルがありません: $required"
    }
}

if (-not $SkipComBuild) {
    Write-Host ''
    Write-Host '=== Windows 11 コンテキストメニュー COM DLL ==='
    $msbuild = Find-MSBuild
    & $msbuild $comProject /p:Configuration=Release /p:Platform=x64 /m /v:minimal
    if ($LASTEXITCODE -ne 0) {
        throw "COM DLL のビルドが失敗しました (exit $LASTEXITCODE)。"
    }
}

if (-not (Test-Path $comDll)) {
    throw "COM DLL が見つかりません: $comDll"
}

# unpackaged 版の従来型メニュー登録は、アプリと同じフォルダーの DLL を使う。
Copy-Item -LiteralPath $comDll -Destination (Join-Path $publishDirectory 'Hodozip.ContextMenu.dll') -Force

Write-Host ''
Write-Host '=== インストーラー用アイコン ==='
& $generateAssets -OutputDirectory $assetsDirectory
if ($LASTEXITCODE -ne 0) {
    throw "MSIX アセットの生成が失敗しました (exit $LASTEXITCODE)。"
}

$iconSource = Join-Path $assetsDirectory 'Square310x310Logo.png'
if (-not (Test-Path $iconSource)) {
    throw "アイコンの元画像が見つかりません: $iconSource"
}

New-IcoFromPng -PngPath $iconSource -IcoPath $iconPath
Write-Host "  Icon: $iconPath"

$iscc = Find-InnoCompiler -ExplicitPath $InnoCompilerPath
Write-Host ''
Write-Host '=== Inno Setup ==='
Write-Host "  ISCC: $iscc"
Write-Host "  Version: $version ($packageVersion)"

Remove-Item -LiteralPath $setupPath -Force -ErrorAction SilentlyContinue
$isccArguments = @(
    "/DAppVersion=$version",
    "/DAppVersion4=$packageVersion",
    "/DSourceDir=$publishDirectory",
    "/DOutputDir=$resolvedOutput",
    "/DIconFile=$iconPath",
    $issPath
)

& $iscc @isccArguments
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup のコンパイルが失敗しました (exit $LASTEXITCODE)。"
}

if (-not (Test-Path $setupPath)) {
    throw "インストーラーが生成されませんでした: $setupPath"
}

$hash = (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash
$size = (Get-Item -LiteralPath $setupPath).Length
"$hash  Hodozip-$version-setup.exe" | Set-Content -LiteralPath $hashPath -Encoding ascii

Write-Host ''
Write-Host 'インストーラーのビルドが完了しました。'
Write-Host "  Path:    $setupPath"
Write-Host "  Size:    $size bytes"
Write-Host "  SHA256:  $hash"
Write-Host "  SHA256 file: $hashPath"
Write-Host "  Install: $setupPath"
