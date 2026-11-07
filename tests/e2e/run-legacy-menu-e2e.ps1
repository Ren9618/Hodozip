# Hodozip Phase 3 legacy context-menu (IContextMenu) E2E.
# Usage: pwsh -File tests/e2e/run-legacy-menu-e2e.ps1 [-ExePath <Hodozip.exe>] [-ComDllPath <Hodozip.ContextMenu.dll>]
# Requires: Release x64 build and the C++ COM DLL build.
#
# Verifies the unpackaged registration toggle, registry layout, menu items and
# both invocation paths through the real IContextMenu COM server.

param(
    [string]$ExePath,
    [string]$ComDllPath
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

[ComImport, Guid("000214E8-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellExtInit
{
    [PreserveSig] int Initialize(IntPtr pidlFolder, IntPtr pdtobj, IntPtr hkeyProgID);
}

[ComImport, Guid("000214E4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IContextMenu
{
    [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
    [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFO pici);
    [PreserveSig] int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
}

[StructLayout(LayoutKind.Sequential)]
public struct CMINVOKECOMMANDINFO
{
    public int cbSize;
    public int fMask;
    public IntPtr hwnd;
    public IntPtr lpVerb;
    public IntPtr lpParameters;
    public IntPtr lpDirectory;
    public int nShow;
    public int cbShow;
    public IntPtr dwHotKey;
    public IntPtr hIcon;
}

public static class LegacyMenuCom
{
    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(
        ref Guid rclsid,
        IntPtr pUnkOuter,
        uint dwClsContext,
        ref Guid riid,
        out IntPtr ppv);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMenuStringW(IntPtr hMenu, uint uIDItem, System.Text.StringBuilder lpString, int cchMax, uint uFlag);

    [DllImport("user32.dll")]
    private static extern uint GetMenuItemID(IntPtr hMenu, int nPos);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(
        string pszName,
        IntPtr pbc,
        out IntPtr ppidl,
        uint sfgaoIn,
        out uint psfgaoOut);

    [DllImport("shell32.dll")]
    private static extern int SHBindToParent(
        IntPtr pidl,
        ref Guid riid,
        out IntPtr ppv,
        out IntPtr ppidlLast);

    [DllImport("shell32.dll")]
    private static extern int SHCreateDataObject(
        IntPtr pidlFolder,
        uint cidl,
        IntPtr[] apidl,
        IntPtr pdtInner,
        ref Guid riid,
        out IntPtr ppv);

    private static object Create(Guid clsid)
    {
        Guid iidUnknown = new Guid("00000000-0000-0000-C000-000000000046");
        int hr = CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iidUnknown, out IntPtr unknown);
        if (hr < 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }

        try
        {
            return Marshal.GetObjectForIUnknown(unknown);
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    public static string[] QueryMenu(Guid clsid, out int commandCount)
    {
        object comObject = Create(clsid);
        try
        {
            IContextMenu menu = (IContextMenu)comObject;
            IntPtr hmenu = CreatePopupMenu();
            if (hmenu == IntPtr.Zero)
            {
                throw new InvalidOperationException("CreatePopupMenu に失敗しました。");
            }

            try
            {
                int hr = menu.QueryContextMenu(hmenu, 0, 1, 0x7FFF, 0);
                if (hr < 0)
                {
                    Marshal.ThrowExceptionForHR(hr);
                }

                commandCount = hr & 0xFFFF;
                string[] result = new string[commandCount];
                for (int index = 0; index < commandCount; index++)
                {
                    if (GetMenuItemID(hmenu, index) != (uint)(index + 1))
                    {
                        throw new InvalidOperationException("メニュー ID が想定と異なります: " + index);
                    }

                    var builder = new System.Text.StringBuilder(256);
                    GetMenuStringW(hmenu, (uint)index, builder, builder.Capacity, 0x400);
                    result[index] = builder.ToString();
                }

                return result;
            }
            finally
            {
                DestroyMenu(hmenu);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(comObject);
        }
    }

    public static void InvokeCommand(Guid clsid, string path, int command)
    {
        object comObject = Create(clsid);
        IntPtr parentPidl = IntPtr.Zero;
        IntPtr fullPidl = IntPtr.Zero;
        IntPtr shellFolder = IntPtr.Zero;
        IntPtr dataObject = IntPtr.Zero;
        try
        {
            IShellExtInit shellExtInit = (IShellExtInit)comObject;
            IContextMenu menu = (IContextMenu)comObject;

            string parentDirectory = System.IO.Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(parentDirectory))
            {
                throw new InvalidOperationException("親フォルダを特定できません: " + path);
            }

            uint attributes;
            ThrowIfFailed(SHParseDisplayName(parentDirectory, IntPtr.Zero, out parentPidl, 0, out attributes));
            ThrowIfFailed(SHParseDisplayName(path, IntPtr.Zero, out fullPidl, 0, out attributes));

            Guid iidShellFolder = new Guid("000214E6-0000-0000-C000-000000000046");
            IntPtr childPidl;
            ThrowIfFailed(SHBindToParent(fullPidl, ref iidShellFolder, out shellFolder, out childPidl));

            Guid iidDataObject = new Guid("0000010e-0000-0000-C000-000000000046");
            ThrowIfFailed(SHCreateDataObject(
                parentPidl,
                1,
                new[] { childPidl },
                IntPtr.Zero,
                ref iidDataObject,
                out dataObject));

            int hr = shellExtInit.Initialize(IntPtr.Zero, dataObject, IntPtr.Zero);
            if (hr < 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            var info = new CMINVOKECOMMANDINFO
            {
                cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFO>(),
                lpVerb = (IntPtr)command,
                nShow = 1,
            };

            hr = menu.InvokeCommand(ref info);
            if (hr < 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }
        }
        finally
        {
            if (dataObject != IntPtr.Zero)
            {
                Marshal.Release(dataObject);
            }
            if (shellFolder != IntPtr.Zero)
            {
                Marshal.Release(shellFolder);
            }
            if (fullPidl != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(fullPidl);
            }
            if (parentPidl != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(parentPidl);
            }
            Marshal.ReleaseComObject(comObject);
        }
    }

    private static void ThrowIfFailed(int hr)
    {
        if (hr < 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }
    }
}
'@

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if ([string]::IsNullOrWhiteSpace($ExePath)) {
    $ExePath = Join-Path $repoRoot 'src\Hodozip\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\Hodozip.exe'
}
if ([string]::IsNullOrWhiteSpace($ComDllPath)) {
    $ComDllPath = Join-Path $repoRoot 'src\Hodozip.ContextMenu\bin\x64\Release\Hodozip.ContextMenu.dll'
}

$exePath = [System.IO.Path]::GetFullPath($ExePath)
$comDllPath = [System.IO.Path]::GetFullPath($ComDllPath)
$exeDirectory = Split-Path -Parent $exePath
$installedComDll = Join-Path $exeDirectory 'Hodozip.ContextMenu.dll'

# 従来型メニューは Apartment スレッドで動くため、MTA で起動されたら STA で再実行する。
if ([System.Threading.Thread]::CurrentThread.GetApartmentState() -ne [System.Threading.ApartmentState]::STA) {
    $relaunch = @('-NoProfile', '-STA', '-File', $PSCommandPath, '-ExePath', $exePath, '-ComDllPath', $comDllPath)
    & (Get-Process -Id $PID).Path @relaunch
    exit $LASTEXITCODE
}

if (-not (Test-Path -LiteralPath $exePath)) {
    throw "Hodozip.exe が見つかりません。先に Release x64 ビルドを実行してください: $exePath"
}
if (-not (Test-Path -LiteralPath $comDllPath)) {
    throw "Hodozip.ContextMenu.dll が見つかりません。先に C++ プロジェクトをビルドしてください: $comDllPath"
}

$legacyClsid = [Guid]'4F4C6D2A-71B9-4E4E-9A7D-8D9A0B5E6C03'
$compressClsid = [Guid]'4F4C6D2A-71B9-4E4E-9A7D-8D9A0B5E6C05'
$clsidKey = "HKCU:\Software\Classes\CLSID\$($legacyClsid.ToString('B').ToUpperInvariant())"
$inprocKey = Join-Path $clsidKey 'InprocServer32'
$compressClsidKey = "HKCU:\Software\Classes\CLSID\$($compressClsid.ToString('B').ToUpperInvariant())"
$compressInprocKey = Join-Path $compressClsidKey 'InprocServer32'
$compressHandlerPaths = @(
    'HKCU:\Software\Classes\*\shellex\ContextMenuHandlers\HodozipCompress',
    'HKCU:\Software\Classes\Directory\shellex\ContextMenuHandlers\HodozipCompress'
)
$expectedExtensions = @('.zip', '.7z', '.rar', '.msi', '.tar', '.gz', '.tgz', '.bz2', '.tbz2', '.xz', '.txz', '.zst', '.tzst')

$workRoot = Join-Path $env:TEMP 'HodozipLegacyMenuE2E'
$archives = Join-Path $workRoot 'archives'
$payload = Join-Path $workRoot 'payload'
$settingsDirectory = Join-Path $env:LOCALAPPDATA 'Hodozip'
$settingsPath = Join-Path $settingsDirectory 'settings.json'

if (-not (Test-Path $settingsPath)) {
    $settingsBackup = $null
    $hadSettings = $false
}
else {
    $settingsBackup = Get-Content -LiteralPath $settingsPath -Raw
    $hadSettings = $true
}

$wasRegistered = Test-Path -LiteralPath $clsidKey

function Assert-Exists {
    param([string]$Path, [string]$Label)
    if (-not (Test-Path -LiteralPath $Path)) {
        throw "E2E 失敗: $Label が見つかりません: $Path"
    }
    Write-Host "  OK: $Label"
}

function Assert-Content {
    param([string]$Path, [string]$Expected, [string]$Label)
    Assert-Exists -Path $Path -Label $Label
    $actual = (Get-Content -LiteralPath $Path -Raw).TrimEnd()
    if ($actual -ne $Expected) {
        throw "E2E 失敗: $Label の内容が '$Expected' ではありません: '$actual'"
    }
}

function Invoke-Registration {
    param([string]$Switch)

    $process = Start-Process -FilePath $exePath -ArgumentList $Switch -PassThru -Wait
    if ($process.ExitCode -ne 0) {
        throw "E2E 失敗: $Switch が終了コード $($process.ExitCode) で失敗しました。"
    }
    Write-Host "  OK: $Switch"
}

function Wait-HodozipExit {
    param([int]$TimeoutSeconds = 45)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (-not (Get-Process Hodozip -ErrorAction SilentlyContinue)) {
            return
        }
        Start-Sleep -Milliseconds 250
    }
    Get-Process Hodozip -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    throw "Hodozip が $TimeoutSeconds 秒以内に終了しませんでした。"
}

function Get-RegistryDefault {
    param([string]$Path)
    $key = Get-Item -LiteralPath $Path -ErrorAction SilentlyContinue
    if ($null -eq $key) {
        return $null
    }
    return [string]$key.GetValue('')
}

function Wait-CompressProcess {
    param([string]$SourcePath, [int]$TimeoutSeconds = 30)

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $processes = Get-CimInstance Win32_Process -Filter "Name='Hodozip.exe'" -ErrorAction SilentlyContinue
        foreach ($process in $processes) {
            if ($null -ne $process.CommandLine -and $process.CommandLine.Contains($SourcePath)) {
                return $process
            }
        }
        Start-Sleep -Milliseconds 250
    }

    Get-Process Hodozip -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    throw "圧縮用の Hodozip プロセスが $TimeoutSeconds 秒以内に起動しませんでした: $SourcePath"
}

function New-TestZip {
    param([string]$ZipPath, [switch]$Scattered)

    Remove-Item -LiteralPath $ZipPath -Force -ErrorAction SilentlyContinue
    if ($Scattered) {
        $source = Join-Path $payload 'scattered'
        New-Item -ItemType Directory -Path (Join-Path $source 'data') -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $source 'readme.txt') -Value 'legacy folder readme' -Encoding utf8
        Set-Content -LiteralPath (Join-Path $source 'data\value.txt') -Value 'legacy folder value' -Encoding utf8
        Compress-Archive -Path (Join-Path $source '*') -DestinationPath $ZipPath -CompressionLevel Optimal
    }
    else {
        $source = Join-Path $payload 'Top'
        New-Item -ItemType Directory -Path (Join-Path $source 'sub') -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $source 'a.txt') -Value 'legacy here A' -Encoding utf8
        Set-Content -LiteralPath (Join-Path $source 'sub\b.txt') -Value 'legacy here B' -Encoding utf8
        Compress-Archive -LiteralPath $source -DestinationPath $ZipPath -CompressionLevel Optimal
    }
}

try {
    Get-Process Hodozip -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $archives, $payload | Out-Null
    if (-not [string]::Equals($comDllPath, $installedComDll, [StringComparison]::OrdinalIgnoreCase)) {
        Copy-Item -LiteralPath $comDllPath -Destination $installedComDll -Force
    }

    New-Item -ItemType Directory -Path $settingsDirectory -Force | Out-Null
    @{
        DestinationRule = 'SameAsArchive'
        OpenFolderAfterExtraction = $false
        CloseAfterAutoExtract = $true
        FolderCollisionPolicy = 'Merge'
        OverwritePolicy = 'Overwrite'
    } | ConvertTo-Json | Set-Content -LiteralPath $settingsPath -Encoding utf8

    Write-Host '=== L1: CLI で従来型メニューを登録 ==='
    Invoke-Registration -Switch '--register-context-menu'

    $registeredDll = Get-RegistryDefault -Path $inprocKey
    if (-not [string]::Equals($registeredDll, $installedComDll, [StringComparison]::OrdinalIgnoreCase)) {
        throw "E2E 失敗: InprocServer32 が '$installedComDll' ではありません: '$registeredDll'"
    }
    Write-Host "  OK: InprocServer32 = $registeredDll"

    foreach ($extension in $expectedExtensions) {
        $handlerPath =
            "HKCU:\Software\Classes\SystemFileAssociations\$extension\shellex\ContextMenuHandlers\Hodozip"
        $handlerClsid = Get-RegistryDefault -Path $handlerPath
        if (-not [string]::Equals($handlerClsid, $legacyClsid.ToString('D'), [StringComparison]::OrdinalIgnoreCase)) {
            throw "E2E 失敗: $extension のハンドラー CLSID が一致しません: '$handlerClsid'"
        }
    }
    Write-Host "  OK: $($expectedExtensions.Count) 拡張子にハンドラーを登録"

    $compressDll = Get-RegistryDefault -Path $compressInprocKey
    if (-not [string]::Equals($compressDll, $installedComDll, [StringComparison]::OrdinalIgnoreCase)) {
        throw "E2E 失敗: 圧縮用 InprocServer32 が '$installedComDll' ではありません: '$compressDll'"
    }
    foreach ($handlerPath in $compressHandlerPaths) {
        $handlerClsid = Get-RegistryDefault -Path $handlerPath
        if (-not [string]::Equals($handlerClsid, $compressClsid.ToString('D'), [StringComparison]::OrdinalIgnoreCase)) {
            throw "E2E 失敗: 圧縮ハンドラー CLSID が一致しません: '$handlerPath' -> '$handlerClsid'"
        }
    }
    Write-Host '  OK: * と Directory に圧縮ハンドラーを登録'

    Write-Host '=== L2: COM サーバーを直接作成し、メニュー項目を検証 ==='
    [int]$commandCount = 0
    $menuItems = [LegacyMenuCom]::QueryMenu($legacyClsid, [ref]$commandCount)
    if ($commandCount -ne 2) {
        throw "E2E 失敗: メニュー項目数が 2 ではありません: $commandCount"
    }
    if ($menuItems[0] -ne 'Hodozip で解凍' -or $menuItems[1] -ne 'Hodozip でフォルダに解凍') {
        throw "E2E 失敗: メニュー文字列が想定と異なります: '$($menuItems[0])' / '$($menuItems[1])'"
    }
    Write-Host "  OK: メニュー項目 '$($menuItems[0])' / '$($menuItems[1])'"

    [int]$compressCommandCount = 0
    $compressMenuItems = [LegacyMenuCom]::QueryMenu($compressClsid, [ref]$compressCommandCount)
    if ($compressCommandCount -ne 1) {
        throw "E2E 失敗: 圧縮メニュー項目数が 1 ではありません: $compressCommandCount"
    }
    if ($compressMenuItems[0] -ne 'Hodozip で圧縮') {
        throw "E2E 失敗: 圧縮メニュー文字列が想定と異なります: '$($compressMenuItems[0])'"
    }
    Write-Host "  OK: 圧縮メニュー項目 '$($compressMenuItems[0])'"

    Write-Host '=== L3: 従来型メニュー「Hodozip で解凍」を実行 ==='
    $hereZip = Join-Path $archives 'legacy-here.zip'
    New-TestZip -ZipPath $hereZip

    [LegacyMenuCom]::InvokeCommand($legacyClsid, $hereZip, 0)
    Wait-HodozipExit
    Assert-Content -Path (Join-Path $archives 'Top\a.txt') -Expected 'legacy here A' -Label 'Top\a.txt'
    Assert-Content -Path (Join-Path $archives 'Top\sub\b.txt') -Expected 'legacy here B' -Label 'Top\sub\b.txt'

    Write-Host '=== L4: 従来型メニュー「Hodozip でフォルダに解凍」を実行 ==='
    $folderZip = Join-Path $archives 'legacy-folder.zip'
    New-TestZip -ZipPath $folderZip -Scattered

    [LegacyMenuCom]::InvokeCommand($legacyClsid, $folderZip, 1)
    Wait-HodozipExit
    Assert-Content -Path (Join-Path $archives 'legacy-folder\readme.txt') -Expected 'legacy folder readme' -Label 'legacy-folder\readme.txt'
    Assert-Content -Path (Join-Path $archives 'legacy-folder\data\value.txt') -Expected 'legacy folder value' -Label 'legacy-folder\data\value.txt'

    Write-Host '=== L5: 従来型メニュー「Hodozip で圧縮」を実行 ==='
    $compressSource = Join-Path $payload 'compress-source.txt'
    Set-Content -LiteralPath $compressSource -Value 'legacy compress source' -Encoding utf8

    [LegacyMenuCom]::InvokeCommand($compressClsid, $compressSource, 0)
    $compressProcess = Wait-CompressProcess -SourcePath $compressSource
    Write-Host "  OK: Hodozip 起動 (PID=$($compressProcess.ProcessId))"
    if ($compressProcess.CommandLine -notmatch '--compress') {
        throw "E2E 失敗: 圧縮コマンド ラインに --compress がありません: $($compressProcess.CommandLine)"
    }
    if (-not $compressProcess.CommandLine.Contains($compressSource)) {
        throw "E2E 失敗: 圧縮コマンド ラインに選択ファイルがありません: $($compressProcess.CommandLine)"
    }
    Write-Host "  OK: コマンド ラインに --compress と選択ファイルを含む"
    Stop-Process -Id $compressProcess.ProcessId -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500

    Write-Host '=== L6: CLI で従来型メニューを解除 ==='
    Invoke-Registration -Switch '--unregister-context-menu'

    if (Test-Path -LiteralPath $clsidKey) {
        throw "E2E 失敗: 解除後も CLSID キーが残っています: $clsidKey"
    }
    if (Test-Path -LiteralPath $compressClsidKey) {
        throw "E2E 失敗: 解除後も圧縮 CLSID キーが残っています: $compressClsidKey"
    }
    foreach ($extension in $expectedExtensions) {
        $handlerPath =
            "HKCU:\Software\Classes\SystemFileAssociations\$extension\shellex\ContextMenuHandlers\Hodozip"
        if (Test-Path -LiteralPath $handlerPath) {
            throw "E2E 失敗: 解除後もハンドラー キーが残っています: $handlerPath"
        }
    }
    foreach ($handlerPath in $compressHandlerPaths) {
        if (Test-Path -LiteralPath $handlerPath) {
            throw "E2E 失敗: 解除後も圧縮ハンドラー キーが残っています: $handlerPath"
        }
    }
    Write-Host '  OK: レジストリ キーを解除しました。'

    Write-Host ''
    Write-Host '従来型コンテキストメニュー E2E: すべて成功しました。'
}
finally {
    if ($wasRegistered) {
        Start-Process -FilePath $exePath -ArgumentList '--register-context-menu' -Wait -WindowStyle Hidden | Out-Null
    }
    else {
        Start-Process -FilePath $exePath -ArgumentList '--unregister-context-menu' -Wait -WindowStyle Hidden | Out-Null
    }

    if ($hadSettings) {
        Set-Content -LiteralPath $settingsPath -Value $settingsBackup -Encoding utf8 -NoNewline
    }
    elseif (Test-Path -LiteralPath $settingsPath) {
        Remove-Item -LiteralPath $settingsPath -Force
    }
}
