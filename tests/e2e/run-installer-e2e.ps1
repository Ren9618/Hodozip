# Hodozip installer (Inno Setup) E2E.
# Usage: pwsh -File tests/e2e/run-installer-e2e.ps1 [-SetupPath <setup.exe>]
#
# Verifies:
#   - v0.5.0 dirty registration (unbraced handler values) is repaired by setup
#   - the installed app matches Directory.Build.props and the installer wrote every expected braced value
#   - app --unregister-context-menu leaves no registry leftovers
#   - app --register-context-menu writes exactly the same registry contents as setup
#
# Requires: artifacts\installer\Hodozip-<version>-setup.exe (packaging/build-installer.ps1).

param(
    [string]$SetupPath
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
[xml]$props = Get-Content -LiteralPath (Join-Path $repoRoot 'Directory.Build.props') -Raw
$version = [string]$props.Project.PropertyGroup.HodozipVersion
if ([string]::IsNullOrWhiteSpace($SetupPath)) {
    $SetupPath = Join-Path $repoRoot "artifacts\installer\Hodozip-$version-setup.exe"
}
$setupPath = [System.IO.Path]::GetFullPath($SetupPath)
$expectedVersion = $version
$installDirectory = Join-Path $env:LOCALAPPDATA 'Programs\Hodozip'
$installedExe = Join-Path $installDirectory 'Hodozip.exe'

$unifiedClsid = '{4F4C6D2A-71B9-4E4E-9A7D-8D9A0B5E6C03}'
$oldCompressClsid = '{4F4C6D2A-71B9-4E4E-9A7D-8D9A0B5E6C05}'
$extensions = @(
    '.zip', '.7z', '.rar', '.msi', '.tar', '.gz', '.tgz', '.bz2',
    '.tbz2', '.xz', '.txz', '.zst', '.tzst'
)
$unifiedHandlerPaths = @(
    'Software\Classes\*\shellex\ContextMenuHandlers\Hodozip',
    'Software\Classes\Directory\shellex\ContextMenuHandlers\Hodozip'
)
$oldCompressHandlerPaths = @(
    'Software\Classes\*\shellex\ContextMenuHandlers\HodozipCompress',
    'Software\Classes\Directory\shellex\ContextMenuHandlers\HodozipCompress'
)
$clsidPaths = @(
    "Software\Classes\CLSID\$unifiedClsid",
    "Software\Classes\CLSID\$unifiedClsid\InprocServer32",
    "Software\Classes\CLSID\$oldCompressClsid",
    "Software\Classes\CLSID\$oldCompressClsid\InprocServer32"
)

if (-not (Test-Path -LiteralPath $setupPath)) {
    throw "setup.exe が見つかりません。先に packaging/build-installer.ps1 を実行してください: $setupPath"
}

function Get-RegValue {
    param([string]$Path, [string]$Name)
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($Path)
    if ($null -eq $key) {
        return $null
    }
    try {
        return $key.GetValue($Name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
    }
    finally {
        $key.Dispose()
    }
}

function Set-RegValue {
    param([string]$Path, [string]$Name, [string]$Value)
    $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($Path, $true)
    if ($null -eq $key) {
        throw "レジストリ キーを作成できません: $Path"
    }
    try {
        $key.SetValue($Name, $Value, [Microsoft.Win32.RegistryValueKind]::String)
    }
    finally {
        $key.Dispose()
    }
}

function Remove-RegTree {
    param([string]$Path)
    [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($Path, $false)
}

function Assert-Equal {
    param([string]$Label, $Actual, $Expected)
    if (-not [string]::Equals([string]$Actual, [string]$Expected, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label が一致しません: actual='$Actual' expected='$Expected'"
    }
}

function Get-RegistrationSnapshot {
    $values = [ordered]@{}
    foreach ($path in $unifiedHandlerPaths) {
        $values["$path|"] = Get-RegValue -Path $path -Name ''
    }
    foreach ($extension in $extensions) {
        $path = "Software\Classes\SystemFileAssociations\$extension\shellex\ContextMenuHandlers\Hodozip"
        $values["legacy:$path|"] = Get-RegValue -Path $path -Name ''
    }
    foreach ($path in $oldCompressHandlerPaths) {
        $values["legacy:$path|"] = Get-RegValue -Path $path -Name ''
    }
    foreach ($path in $clsidPaths) {
        $values["$path|"] = Get-RegValue -Path $path -Name ''
        $values["$path|ThreadingModel"] = Get-RegValue -Path $path -Name 'ThreadingModel'
    }
    return $values
}

function Assert-KeyMissing {
    param([string]$Path)
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($Path)
    if ($null -ne $key) {
        $key.Dispose()
        throw "解除後もレジストリ キーが残っています: $Path"
    }
}

function Assert-NoRegistrationLeftovers {
    foreach ($extension in $extensions) {
        Assert-KeyMissing "Software\Classes\SystemFileAssociations\$extension\shellex\ContextMenuHandlers\Hodozip"
    }
    foreach ($path in $unifiedHandlerPaths) {
        Assert-KeyMissing $path
    }
    foreach ($path in $oldCompressHandlerPaths) {
        Assert-KeyMissing $path
    }
    foreach ($path in @(
        "Software\Classes\CLSID\$unifiedClsid",
        "Software\Classes\CLSID\$oldCompressClsid")) {
        Assert-KeyMissing $path
    }
    Write-Host '  OK: 解除後にレジストリの残骸なし'
}

function Assert-InstalledRegistration {
    $expectedDll = Join-Path $installDirectory 'Hodozip.ContextMenu.dll'
    foreach ($path in $unifiedHandlerPaths) {
        Assert-Equal "$path のハンドラー値" (Get-RegValue -Path $path -Name '') $unifiedClsid
    }
    $inproc = "Software\Classes\CLSID\$unifiedClsid\InprocServer32"
    Assert-Equal "$unifiedClsid の InprocServer32" (Get-RegValue -Path $inproc -Name '') $expectedDll
    Assert-Equal "$unifiedClsid の ThreadingModel" (Get-RegValue -Path $inproc -Name 'ThreadingModel') 'Apartment'
    foreach ($extension in $extensions) {
        $path = "Software\Classes\SystemFileAssociations\$extension\shellex\ContextMenuHandlers\Hodozip"
        if ($null -ne (Get-RegValue -Path $path -Name '')) {
            throw "旧レイアウトの拡張子別ハンドラーが残っています: $path"
        }
    }
    foreach ($path in $oldCompressHandlerPaths) {
        if ($null -ne (Get-RegValue -Path $path -Name '')) {
            throw "旧圧縮ハンドラーが残っています: $path"
        }
    }
    if ($null -ne (Get-RegValue -Path "Software\Classes\CLSID\$oldCompressClsid" -Name '')) {
        throw '旧圧縮 CLSID が残っています。'
    }
    Write-Host '  OK: * と Directory に統合ハンドラー（中かっこ付き）を登録'
    Write-Host '  OK: 旧レイアウトは残っていない'
    Write-Host '  OK: CLSID の InprocServer32 はインストール先 DLL を指す'
}

function Invoke-InstalledApp {
    param([string]$Arguments, [string]$Label)
    $process = Start-Process -FilePath $installedExe -ArgumentList $Arguments -PassThru
    if (-not $process.WaitForExit(60000)) {
        try { $process.Kill($true) } catch { }
        throw "$Label が 60 秒以内に完了しませんでした。"
    }
    if ($process.ExitCode -ne 0) {
        throw "$Label が終了コード $($process.ExitCode) で失敗しました。"
    }
    Write-Host "  OK: $Label (exit 0)"
}

try {
    Get-Process Hodozip -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

    Write-Host '=== I1: v0.5.0 の dirty 登録状態を再現 ==='
    # v0.5.0 は中かっこなしの CLSID を書き、Explorer から無視されていた。
    Set-RegValue "Software\Classes\SystemFileAssociations\.zip\shellex\ContextMenuHandlers\Hodozip" '' '4F4C6D2A-71B9-4E4E-9A7D-8D9A0B5E6C03'
    Set-RegValue "Software\Classes\SystemFileAssociations\.7z\shellex\ContextMenuHandlers\Hodozip" '' '4F4C6D2A-71B9-4E4E-9A7D-8D9A0B5E6C03'
    Set-RegValue $oldCompressHandlerPaths[0] '' '4F4C6D2A-71B9-4E4E-9A7D-8D9A0B5E6C05'
    Set-RegValue $oldCompressHandlerPaths[1] '' '4F4C6D2A-71B9-4E4E-9A7D-8D9A0B5E6C05'
    Write-Host '  OK: .zip / .7z / * / Directory に中かっこなしの値を書き込み'

    Write-Host '=== I2: setup.exe サイレント アップグレード ==='
    $logPath = Join-Path $env:TEMP 'hodozip-installer-e2e-setup.log'
    Remove-Item -LiteralPath $logPath -Force -ErrorAction SilentlyContinue
    $arguments = @(
        '/VERYSILENT',
        '/SUPPRESSMSGBOXES',
        '/NORESTART',
        '/NOCLOSEAPPLICATIONS',
        '/TASKS="contextmenu,associate"',
        "/LOG=`"$logPath`""
    )
    $setup = Start-Process -FilePath $setupPath -ArgumentList $arguments -PassThru -Wait
    if ($setup.ExitCode -ne 0) {
        throw "setup.exe が終了コード $($setup.ExitCode) で失敗しました。ログ: $logPath"
    }
    Write-Host "  OK: setup.exe exit 0 (log: $logPath)"

    if (-not (Test-Path -LiteralPath $installedExe)) {
        throw "インストール後に Hodozip.exe が見つかりません: $installedExe"
    }
    $installedVersion = (Get-Item -LiteralPath $installedExe).VersionInfo.FileVersion
    if (-not $installedVersion.StartsWith($expectedVersion, [StringComparison]::OrdinalIgnoreCase)) {
        throw "インストールされた Hodozip.exe のバージョンが $expectedVersion ではありません: $installedVersion"
    }
    Write-Host "  OK: インストール済み Hodozip.exe のバージョン = $installedVersion"

    $uninstallKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
        [Microsoft.Win32.RegistryHive]::CurrentUser,
        [Microsoft.Win32.RegistryView]::Registry32).OpenSubKey(
            'Software\Microsoft\Windows\CurrentVersion\Uninstall\{A7C4E2B1-6D3F-4E8A-9B5C-1F2D3E4A5B6C}_is1')
    if ($null -eq $uninstallKey) {
        throw 'アンインストール情報が見つかりません。'
    }
    try {
        $uninstallVersion = [string]$uninstallKey.GetValue('DisplayVersion')
        $uninstallName = [string]$uninstallKey.GetValue('DisplayName')
    }
    finally {
        $uninstallKey.Dispose()
    }
    if (-not $uninstallName.StartsWith('Hodozip', [StringComparison]::Ordinal)) {
        throw "アンインストール情報の名前が想定と異なります: '$uninstallName'"
    }
    Assert-Equal 'アンインストール情報のバージョン' $uninstallVersion $expectedVersion
    Write-Host "  OK: アンインストール情報のバージョン = $uninstallVersion"

    Write-Host '=== I3: インストーラーが書いた登録内容を検証 ==='
    Assert-InstalledRegistration

    $installerSnapshot = Get-RegistrationSnapshot

    Write-Host '=== I4: アプリの --unregister-context-menu ==='
    Invoke-InstalledApp -Arguments '--unregister-context-menu' -Label '--unregister-context-menu'
    Assert-NoRegistrationLeftovers

    Write-Host '=== I5: アプリの --register-context-menu と installer の一致 ==='
    Invoke-InstalledApp -Arguments '--register-context-menu' -Label '--register-context-menu'
    Assert-InstalledRegistration
    $appSnapshot = Get-RegistrationSnapshot
    foreach ($key in $installerSnapshot.Keys) {
        if (-not $appSnapshot.Contains($key)) {
            throw "アプリ登録にキーがありません: $key"
        }
        Assert-Equal "installer/app 登録内容 ($key)" $appSnapshot[$key] $installerSnapshot[$key]
    }
    Write-Host '  OK: installer とアプリの登録内容が完全一致'

    Write-Host ''
    Write-Host 'インストーラー E2E: すべて成功しました。'
}
finally {
    # 通常のインストール状態（登録済み）に戻す。
    if (Test-Path -LiteralPath $installedExe) {
        $process = Start-Process -FilePath $installedExe -ArgumentList '--register-context-menu' -PassThru
        if (-not $process.WaitForExit(60000)) {
            try { $process.Kill($true) } catch { }
        }
    }
}
