# Hodozip Phase 5 icon E2E.
# Usage: pwsh -File tests/e2e/run-icon-e2e.ps1 [-SetupPath <setup.exe>] [-PublishedExePath <Hodozip.exe>]
#
# Installs the Inno Setup package silently into a temporary directory and
# verifies the exe icon, the installed .ico, the Start Menu shortcut icon,
# the window icon, and the icons in the published zip payload / MSIX package.

param(
    [string]$SetupPath,
    [string]$PublishedExePath
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class IconNative
{
    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        int ActivateApplication(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string arguments,
            int options,
            out uint processId);

        int ActivateForFile(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            IntPtr itemArray,
            [MarshalAs(UnmanagedType.LPWStr)] string verb,
            out uint processId);

        int ActivateForProtocol(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            IntPtr itemArray,
            out uint processId);
    }

    [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    private class ApplicationActivationManager
    {
    }

    public static uint ActivateApplication(string appUserModelId)
    {
        var manager = (IApplicationActivationManager)new ApplicationActivationManager();
        uint processId;
        int hr = manager.ActivateApplication(appUserModelId, string.Empty, 0, out processId);
        if (hr < 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }
        return processId;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern uint ExtractIconEx(
        string lpszFile,
        int nIconIndex,
        IntPtr[] phiconLarge,
        IntPtr[] phiconSmall,
        uint nIcons);

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
    public static extern IntPtr GetClassLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr hIcon);

    public delegate bool EnumResNameProc(IntPtr hModule, IntPtr lpszType, IntPtr lpszName, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(string lpFileName, IntPtr hFile, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr hModule);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumResourceNames(IntPtr hModule, IntPtr lpszType, EnumResNameProc lpEnumFunc, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindResource(IntPtr hModule, IntPtr lpName, IntPtr lpType);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LoadResource(IntPtr hModule, IntPtr hResInfo);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LockResource(IntPtr hResData);

    [DllImport("kernel32.dll")]
    private static extern uint SizeofResource(IntPtr hModule, IntPtr hResInfo);

    // Returns the number of images across every RT_GROUP_ICON resource. A PE
    // file normally has one icon group that contains all of the .ico frames.
    public static int GetIconFrameCount(string path)
    {
        const uint loadLibraryAsDataFile = 0x00000002;
        const uint loadLibraryAsImageResource = 0x00000020;
        IntPtr module = LoadLibraryEx(path, IntPtr.Zero, loadLibraryAsDataFile | loadLibraryAsImageResource);
        if (module == IntPtr.Zero)
        {
            return 0;
        }

        int total = 0;
        try
        {
            EnumResourceNames(module, new IntPtr(14), (h, type, name, param) =>
            {
                IntPtr info = FindResource(h, name, new IntPtr(14));
                if (info == IntPtr.Zero)
                {
                    return true;
                }

                IntPtr resource = LoadResource(h, info);
                if (resource == IntPtr.Zero)
                {
                    return true;
                }

                IntPtr data = LockResource(resource);
                if (data == IntPtr.Zero || SizeofResource(h, info) < 6)
                {
                    return true;
                }

                total += Marshal.ReadInt16(data, 4) & 0xFFFF;
                return true;
            }, IntPtr.Zero);
        }
        finally
        {
            FreeLibrary(module);
        }

        return total;
    }
}
'@

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if ([string]::IsNullOrWhiteSpace($SetupPath)) {
    [xml]$props = Get-Content -LiteralPath (Join-Path $repoRoot 'Directory.Build.props') -Raw
    $version = [string]$props.Project.PropertyGroup.HodozipVersion
    $SetupPath = Join-Path $repoRoot "artifacts\installer\Hodozip-$version-setup.exe"
}
if ([string]::IsNullOrWhiteSpace($PublishedExePath)) {
    $PublishedExePath = Join-Path $repoRoot 'artifacts\installer\publish\Hodozip.exe'
}

$setupPath = [System.IO.Path]::GetFullPath($SetupPath)
$publishedExePath = [System.IO.Path]::GetFullPath($PublishedExePath)
$workRoot = Join-Path $env:TEMP 'HodozipIconE2E'
$installDir = Join-Path $workRoot 'install'
$logPath = Join-Path $workRoot 'install.log'
$shortcutPath = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Hodozip.lnk'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{A7C4E2B1-6D3F-4E8A-9B5C-1F2D3E4A5B6C}_is1'

if (-not (Test-Path -LiteralPath $setupPath)) {
    throw "setup.exe が見つかりません。先に packaging/build-installer.ps1 を実行してください: $setupPath"
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

function Get-IconFrameCount {
    param([string]$IcoPath)

    $bytes = [System.IO.File]::ReadAllBytes($IcoPath)
    if ($bytes.Length -lt 6 -or $bytes[0] -ne 0 -or $bytes[1] -ne 0 -or $bytes[2] -ne 1 -or $bytes[3] -ne 0) {
        throw "E2E 失敗: ICO ヘッダーが不正です: $IcoPath"
    }
    return [BitConverter]::ToUInt16($bytes, 4)
}

function Assert-IcoFile {
    param([string]$Path, [string]$Label, [int]$MinimumFrames = 8)

    Assert-Exists -Path $Path -Label $Label
    $frames = Get-IconFrameCount -IcoPath $Path
    if ($frames -lt $MinimumFrames) {
        throw "E2E 失敗: $Label のフレーム数が不足しています: $frames (期待: $MinimumFrames 以上)"
    }
    Write-Host "  OK: $Label ($frames frames)"
}

function Get-ExeIconFrameCount {
    param([string]$Path)

    return [IconNative]::GetIconFrameCount($Path)
}

function Assert-ExeIcon {
    param([string]$Path, [string]$Label, [int]$MinimumFrames = 8)

    Assert-Exists -Path $Path -Label $Label
    $count = Get-ExeIconFrameCount -Path $Path
    if ($count -lt $MinimumFrames) {
        throw "E2E 失敗: $Label の埋め込みアイコン数が不足しています: $count (期待: $MinimumFrames 以上)"
    }
    Write-Host "  OK: $Label の埋め込みアイコン ($count frames)"
}

function Assert-SetupIcon {
    param([string]$Path)

    Assert-Exists -Path $Path -Label 'setup.exe'
    $count = Get-ExeIconFrameCount -Path $Path
    if ($count -lt 1) {
        throw "E2E 失敗: setup.exe にアイコンが埋め込まれていません: $Path"
    }
    Write-Host "  OK: setup.exe のアイコン ($count frames)"
}

function Assert-WindowIcon {
    param([string]$ExePath, [string]$Label, [string]$AppUserModelId)

    $process = $null
    if ([string]::IsNullOrWhiteSpace($AppUserModelId)) {
        $process = Start-Process -FilePath $ExePath -PassThru
    }
    else {
        [uint32]$processId = [IconNative]::ActivateApplication($AppUserModelId)
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        while ([DateTime]::UtcNow -lt $deadline) {
            $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
            if ($null -ne $process) {
                break
            }
            Start-Sleep -Milliseconds 200
        }
        if ($null -eq $process) {
            throw "E2E 失敗: $Label のプロセスを取得できませんでした (PID=$processId)。"
        }
    }

    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while (-not $process.HasExited -and [DateTime]::UtcNow -lt $deadline) {
        $process.Refresh()
        if ($process.MainWindowHandle -ne [IntPtr]::Zero) {
            break
        }
        Start-Sleep -Milliseconds 200
    }

    if ($process.HasExited -or $process.MainWindowHandle -eq [IntPtr]::Zero) {
        try { $process.Kill($true) } catch { }
        throw "E2E 失敗: $Label のメイン ウィンドウを取得できませんでした。"
    }

    try {
        $bigIcon = [IconNative]::SendMessage($process.MainWindowHandle, 0x007F, [IntPtr]1, [IntPtr]::Zero)
        $smallIcon = [IconNative]::SendMessage($process.MainWindowHandle, 0x007F, [IntPtr]0, [IntPtr]::Zero)
        $classIcon = [IconNative]::GetClassLongPtr($process.MainWindowHandle, -14)
        if ($bigIcon -eq [IntPtr]::Zero -and $smallIcon -eq [IntPtr]::Zero -and $classIcon -eq [IntPtr]::Zero) {
            throw "E2E 失敗: $Label のウィンドウにアイコンが設定されていません。"
        }
        Write-Host "  OK: $Label のウィンドウ アイコン (big=$bigIcon small=$smallIcon class=$classIcon)"
    }
    finally {
        $null = $process.CloseMainWindow()
        if (-not $process.WaitForExit(10000)) {
            try { $process.Kill($true) } catch { }
        }
    }
}

try {
    Write-Host '=== IC1: ビルド成果物のアイコン ==='
    Assert-SetupIcon -Path $setupPath
    if (Test-Path -LiteralPath $publishedExePath) {
        Assert-ExeIcon -Path $publishedExePath -Label 'publish 出力の Hodozip.exe（zip 版）'
    }
    else {
        Write-Host "  INFO: publish 出力が見つからないため zip 版の確認をスキップ: $publishedExePath"
    }

    Write-Host '=== IC2: setup.exe をサイレント インストール ==='
    Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $workRoot -Force | Out-Null
    if (Test-Path -LiteralPath $installDir) {
        Remove-Item -LiteralPath $installDir -Recurse -Force
    }

    $installArguments = @(
        '/VERYSILENT',
        '/SUPPRESSMSGBOXES',
        '/NORESTART',
        ('/DIR=' + $installDir),
        ('/LOG=' + $logPath),
        '/TASKS=""'
    )
    $install = Start-Process -FilePath $setupPath -ArgumentList $installArguments -PassThru -Wait
    if ($install.ExitCode -ne 0) {
        throw "E2E 失敗: setup.exe のサイレント インストールが終了コード $($install.ExitCode) で失敗しました。ログ: $logPath"
    }
    Write-Host "  OK: サイレント インストール (exit 0) -> $installDir"

    $installedExe = Join-Path $installDir 'Hodozip.exe'
    $installedIco = Join-Path $installDir 'Hodozip.ico'
    Assert-Exists -Path $installedExe -Label 'インストールされた Hodozip.exe'
    Assert-IcoFile -Path $installedIco -Label 'インストールされた Hodozip.ico'
    Assert-ExeIcon -Path $installedExe -Label 'インストールされた Hodozip.exe'
    Assert-Exists -Path (Join-Path $installDir 'Hodozip.ContextMenu.dll') -Label 'インストールされた Hodozip.ContextMenu.dll'

    Write-Host '=== IC3: スタート メニューのショートカット ==='
    Assert-Exists -Path $shortcutPath -Label 'スタート メニューの Hodozip.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    if (-not [string]::Equals([System.IO.Path]::GetFullPath($shortcut.TargetPath), [System.IO.Path]::GetFullPath($installedExe), [StringComparison]::OrdinalIgnoreCase)) {
        throw "E2E 失敗: ショートカットの TargetPath が一致しません: '$($shortcut.TargetPath)'"
    }
    Write-Host "  OK: ショートカット TargetPath = $($shortcut.TargetPath)"
    $iconLocation = $shortcut.IconLocation
    if ([string]::IsNullOrWhiteSpace($iconLocation)) {
        throw 'E2E 失敗: ショートカットに IconLocation が設定されていません。'
    }
    $iconFile = ($iconLocation -split ',')[0].Trim('"')
    if (-not (Test-Path -LiteralPath $iconFile)) {
        throw "E2E 失敗: ショートカットのアイコン ファイルが見つかりません: $iconLocation"
    }
    Write-Host "  OK: ショートカット IconLocation = $iconLocation"

    Write-Host '=== IC4: ウィンドウ アイコン（タイトルバー / タスクバー） ==='
    Assert-WindowIcon -ExePath $installedExe -Label 'setup.exe 版'

    Write-Host '=== IC5: MSIX パッケージのアイコン ==='
    $package = Get-AppxPackage Ren9618.Hodozip -ErrorAction SilentlyContinue
    if ($null -ne $package) {
        $msixExe = Join-Path $package.InstallLocation 'Hodozip.exe'
        Assert-ExeIcon -Path $msixExe -Label 'MSIX パッケージの Hodozip.exe'
        $appId = "$($package.PackageFamilyName)!Hodozip"
        Assert-WindowIcon -ExePath $msixExe -Label 'MSIX 版' -AppUserModelId $appId
    }
    else {
        Write-Host '  INFO: MSIX 版がインストールされていないため、MSIX の確認をスキップ'
    }

    Write-Host '=== IC6: サイレント アンインストール ==='
    $uninstaller = Join-Path $installDir 'unins000.exe'
    Assert-Exists -Path $uninstaller -Label 'unins000.exe'
    $uninstall = Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') -PassThru -Wait
    if ($uninstall.ExitCode -ne 0) {
        throw "E2E 失敗: アンインストールが終了コード $($uninstall.ExitCode) で失敗しました。"
    }
    Start-Sleep -Seconds 2
    Assert-NotExists -Path $installedExe -Label 'アンインストール後の Hodozip.exe'
    Assert-NotExists -Path $shortcutPath -Label 'アンインストール後のショートカット'
    if (Test-Path -LiteralPath $uninstallKey) {
        throw "E2E 失敗: アンインストール後もレジストリ キーが残っています: $uninstallKey"
    }
    Write-Host '  OK: アンインストール完了'

    Write-Host ''
    Write-Host 'アイコン E2E: すべて成功しました。'
}
finally {
    Get-Process Hodozip -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}
