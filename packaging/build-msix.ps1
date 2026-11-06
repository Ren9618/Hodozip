# Hodozip MSIX ビルドスクリプト。
#
# Phase 1 の unpackaged publish をそのままステージングし、Windows SDK の
# MakeAppx / SignTool で MSIX を作る。単一プロジェクト MSIX 化は行わないため、
# 通常の zip 配布と publish の挙動は変わらない。
#
# Usage:
#   pwsh -File packaging/build-msix.ps1
#   pwsh -File packaging/build-msix.ps1 -InstallTrustedCertificate
#
# 署名にはローカルテスト用の自己署名証明書を使う。証明書の Subject は
# Package.appxmanifest.template の Publisher (CN=Ren9618) と一致させる。

[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Platform = 'x64',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\msix'),
    [string]$CertificatePath = (Join-Path $PSScriptRoot '..\artifacts\msix\certs\Hodozip-Test.pfx'),
    [string]$CertificateCerPath = (Join-Path $PSScriptRoot '..\artifacts\msix\certs\Hodozip-Test.cer'),
    [string]$CertificatePassword = 'HodozipTestCert!',
    [switch]$SkipPublish,
    [switch]$RecreateCertificate,
    [switch]$InstallTrustedCertificate
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$propsPath = Join-Path $repoRoot 'Directory.Build.props'
$templatePath = Join-Path $PSScriptRoot 'Package.appxmanifest.template'
$appProject = Join-Path $repoRoot 'src\Hodozip\Hodozip.csproj'
$comProject = Join-Path $repoRoot 'src\Hodozip.ContextMenu\Hodozip.ContextMenu.vcxproj'
$comDll = Join-Path $repoRoot 'src\Hodozip.ContextMenu\bin\x64\Release\Hodozip.ContextMenu.dll'

if (-not (Test-Path $propsPath)) {
    throw "Directory.Build.props が見つかりません: $propsPath"
}

if (-not (Test-Path $templatePath)) {
    throw "MSIX マニフェストテンプレートが見つかりません: $templatePath"
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
$stagingDirectory = Join-Path $resolvedOutput 'staging'
$manifestPath = Join-Path $stagingDirectory 'AppxManifest.xml'
$msixPath = Join-Path $resolvedOutput "Hodozip-$version.msix"
$certificatePath = [System.IO.Path]::GetFullPath($CertificatePath)
$certificateCerPath = [System.IO.Path]::GetFullPath($CertificateCerPath)
$certificateDirectory = Split-Path -Parent $certificatePath

New-Item -ItemType Directory -Path $resolvedOutput -Force | Out-Null
New-Item -ItemType Directory -Path $certificateDirectory -Force | Out-Null

function Find-WindowsSdkTool {
    param([string]$ToolName)

    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (-not (Test-Path $kitsRoot)) {
        throw "Windows SDK の bin フォルダーが見つかりません: $kitsRoot"
    }

    $sdkDirectories = Get-ChildItem -LiteralPath $kitsRoot -Directory |
        Where-Object { $_.Name -match '^10\.\d+' } |
        Sort-Object { [version]$_.Name } -Descending

    foreach ($directory in $sdkDirectories) {
        $candidate = Join-Path $directory.FullName "x64\$ToolName"
        if (Test-Path $candidate) {
            return $candidate
        }
    }

    throw "$ToolName が見つかりません。Windows 10/11 SDK をインストールしてください。"
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

function Get-TestCertificate {
    if ($RecreateCertificate) {
        Remove-Item -LiteralPath $certificatePath -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $certificateCerPath -Force -ErrorAction SilentlyContinue
    }

    if (Test-Path $certificatePath) {
        $existingPassword = ConvertTo-SecureString -String $CertificatePassword -Force -AsPlainText
        $existing = Get-PfxCertificate -FilePath $certificatePath -Password $existingPassword
        if ($existing.Subject -ne 'CN=Ren9618') {
            throw "既存の PFX の Subject が CN=Ren9618 ではありません: $($existing.Subject)"
        }

        Export-Certificate -Cert $existing -FilePath $certificateCerPath -Type CERT -Force | Out-Null
        return $existing
    }

    $certificate = Get-ChildItem Cert:\CurrentUser\My |
        Where-Object {
            $_.Subject -eq 'CN=Ren9618' -and
            $_.HasPrivateKey -and
            $_.NotAfter -gt (Get-Date).AddDays(30)
        } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1

    if ($null -eq $certificate) {
        Write-Host '自己署名テスト証明書を作成しています (CN=Ren9618)...'
        $certificate = New-SelfSignedCertificate `
            -Type Custom `
            -Subject 'CN=Ren9618' `
            -KeyUsage DigitalSignature `
            -FriendlyName 'Hodozip Local Test Code Signing' `
            -CertStoreLocation 'Cert:\CurrentUser\My' `
            -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}') `
            -NotAfter (Get-Date).AddYears(3)
    }

    $securePassword = ConvertTo-SecureString -String $CertificatePassword -Force -AsPlainText
    Export-PfxCertificate -Cert $certificate -FilePath $certificatePath -Password $securePassword -Force | Out-Null
    Export-Certificate -Cert $certificate -FilePath $certificateCerPath -Type CERT -Force | Out-Null
    return $certificate
}

$makeAppx = Find-WindowsSdkTool -ToolName 'makeappx.exe'
$signTool = Find-WindowsSdkTool -ToolName 'signtool.exe'
$msbuild = Find-MSBuild

Write-Host "Hodozip $version (MSIX $packageVersion) をビルドします。"
Write-Host "  MakeAppx: $makeAppx"
Write-Host "  SignTool: $signTool"
Write-Host "  MSBuild:  $msbuild"

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

Write-Host ''
Write-Host '=== Windows 11 コンテキストメニュー COM DLL ==='
& $msbuild $comProject /p:Configuration=Release /p:Platform=x64 /m /v:minimal
if ($LASTEXITCODE -ne 0) {
    throw "COM DLL のビルドが失敗しました (exit $LASTEXITCODE)。"
}

if (-not (Test-Path $comDll)) {
    throw "COM DLL が見つかりません: $comDll"
}

# unpackaged zip 版でも従来型メニューを登録できるよう、publish 出力へ同梱する。
Copy-Item -LiteralPath $comDll -Destination (Join-Path $publishDirectory 'Hodozip.ContextMenu.dll') -Force

Write-Host ''
Write-Host '=== MSIX ステージング ==='
Remove-Item -LiteralPath $stagingDirectory -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $stagingDirectory -Force | Out-Null
Copy-Item -Path (Join-Path $publishDirectory '*') -Destination $stagingDirectory -Recurse -Force
Copy-Item -LiteralPath $comDll -Destination $stagingDirectory -Force

# パッケージ化された WinUI 3 アプリはルートの resources.pri を読み込む。
# unpackaged publish は Hodozip.pri を出力するため、MSIX ステージングでは
# 同じ内容を resources.pri としても配置する（Hodozip.pri はそのまま残す）。
$appPriPath = Join-Path $stagingDirectory 'Hodozip.pri'
if (-not (Test-Path -LiteralPath $appPriPath)) {
    throw "publish 出力に Hodozip.pri がありません: $appPriPath"
}
Copy-Item -LiteralPath $appPriPath -Destination (Join-Path $stagingDirectory 'resources.pri') -Force

& (Join-Path $PSScriptRoot 'generate-assets.ps1') -OutputDirectory (Join-Path $stagingDirectory 'Assets')
if ($LASTEXITCODE -ne 0) {
    throw "MSIX アセットの生成が失敗しました (exit $LASTEXITCODE)。"
}

$manifestText = Get-Content -LiteralPath $templatePath -Raw
$manifestText = $manifestText.Replace('{{PACKAGE_VERSION}}', $packageVersion)
if ($manifestText -match '\{\{') {
    throw 'MSIX マニフェストに未置換のプレースホルダーが残っています。'
}

[xml]$null = $manifestText
[System.IO.File]::WriteAllText($manifestPath, $manifestText, [System.Text.UTF8Encoding]::new($false))

Write-Host ''
Write-Host '=== MakeAppx ==='
Remove-Item -LiteralPath $msixPath -Force -ErrorAction SilentlyContinue
& $makeAppx pack /d $stagingDirectory /p $msixPath /o
if ($LASTEXITCODE -ne 0) {
    throw "MakeAppx が失敗しました (exit $LASTEXITCODE)。"
}

if (-not (Test-Path $msixPath)) {
    throw "MSIX が生成されませんでした: $msixPath"
}

Write-Host ''
Write-Host '=== 署名証明書 ==='
$certificate = Get-TestCertificate
Write-Host "  Subject:    $($certificate.Subject)"
Write-Host "  Thumbprint: $($certificate.Thumbprint)"
Write-Host "  CER:        $certificateCerPath"

if ($InstallTrustedCertificate) {
    # AppX Deployment Service は SYSTEM で動作するため、CurrentUser ではなく
    # LocalMachine\TrustedPeople への登録が必要。管理者権限で certutil を実行する。
    $temporaryCerPath = Join-Path ([System.IO.Path]::GetTempPath()) 'Hodozip-Test.cer'
    Copy-Item -LiteralPath $certificateCerPath -Destination $temporaryCerPath -Force

    $certutilPath = Join-Path $env:SystemRoot 'System32\certutil.exe'
    $elevated = Start-Process -FilePath $certutilPath `
        -ArgumentList @('-addstore', '-f', 'TrustedPeople', $temporaryCerPath) `
        -Verb RunAs -Wait -PassThru

    if ($elevated.ExitCode -ne 0) {
        throw "証明書のインポートに失敗しました (exit $($elevated.ExitCode))。管理者権限のコマンドプロンプトで次を実行してください: certutil -addstore -f TrustedPeople `"$temporaryCerPath`""
    }

    Write-Host '  証明書を LocalMachine\TrustedPeople にインポートしました。'
}

Write-Host ''
Write-Host '=== SignTool ==='
& $signTool sign /fd SHA256 /f $certificatePath /p $CertificatePassword /d 'Hodozip' $msixPath
if ($LASTEXITCODE -ne 0) {
    throw "SignTool の署名が失敗しました (exit $LASTEXITCODE)。"
}

$signature = Get-AuthenticodeSignature -LiteralPath $msixPath
Write-Host "  署名状態: $($signature.Status)"
if ($signature.Status -ne 'Valid' -and $signature.Status -ne 'UnknownError') {
    throw "MSIX の署名状態が不正です: $($signature.Status)"
}

$hash = (Get-FileHash -LiteralPath $msixPath -Algorithm SHA256).Hash
$size = (Get-Item -LiteralPath $msixPath).Length

Write-Host ''
Write-Host 'MSIX のビルドが完了しました。'
Write-Host "  Path:    $msixPath"
Write-Host "  Size:    $size bytes"
Write-Host "  SHA256:  $hash"
Write-Host "  Identity: Name=Ren9618.Hodozip Publisher=CN=Ren9618 Version=$packageVersion"
Write-Host "  CER:     $certificateCerPath"
