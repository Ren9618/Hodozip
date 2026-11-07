$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

[ComImport, Guid("a08ce4d0-fa25-44ab-b57c-c7b1c323e0b9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IExplorerCommandRaw
{
    [PreserveSig] int GetTitle(IntPtr psiItemArray, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
    [PreserveSig] int GetIcon(IntPtr psiItemArray, [MarshalAs(UnmanagedType.LPWStr)] out string ppszIcon);
    [PreserveSig] int GetToolTip(IntPtr psiItemArray, [MarshalAs(UnmanagedType.LPWStr)] out string ppszInfotip);
    [PreserveSig] int GetCanonicalName(out Guid pguidCommandName);
    [PreserveSig] int GetState(IntPtr psiItemArray, [MarshalAs(UnmanagedType.Bool)] bool fOkToBeSlow, out int pCmdState);
    [PreserveSig] int Invoke(IntPtr psiItemArray, IntPtr pbc);
    [PreserveSig] int GetFlags(out int pFlags);
    [PreserveSig] int EnumSubCommands(out IntPtr ppEnum);
}

public static class ExplorerCommandTest
{
    [DllImport("ole32.dll", PreserveSig = false)]
    private static extern void CoCreateInstance(
        ref Guid rclsid,
        IntPtr pUnkOuter,
        uint dwClsContext,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IExplorerCommandRaw ppv);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        string pszPath,
        IntPtr pbc,
        ref Guid riid,
        out IntPtr ppv);

    [DllImport("shell32.dll", PreserveSig = false)]
    private static extern void SHCreateShellItemArrayFromShellItem(
        IntPtr psi,
        ref Guid riid,
        out IntPtr ppsiItemArray);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHParseDisplayName(
        string pszName,
        IntPtr pbc,
        out IntPtr ppidl,
        uint sfgaoIn,
        out uint psfgaoOut);

    [DllImport("shell32.dll", PreserveSig = false)]
    private static extern void SHCreateShellItemArrayFromIDLists(
        uint cidl,
        IntPtr[] rgpidl,
        out IntPtr ppsiItemArray);


    public static string GetTitle(Guid clsid, string path)
    {
        IntPtr item = IntPtr.Zero;
        IntPtr array = IntPtr.Zero;
        IExplorerCommandRaw command = null;
        try
        {
            Guid iidUnknown = new Guid("00000000-0000-0000-C000-000000000046");
            Guid iidExplorerCommand = new Guid("a08ce4d0-fa25-44ab-b57c-c7b1c323e0b9");
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref iidUnknown, out item);
            SHCreateShellItemArrayFromShellItem(item, ref iidUnknown, out array);
            CoCreateInstance(ref clsid, IntPtr.Zero, 23, ref iidExplorerCommand, out command);

            int hr = command.GetTitle(array, out string title);
            if (hr < 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }
            return title;
        }
        finally
        {
            if (command != null)
            {
                Marshal.ReleaseComObject(command);
            }
            if (array != IntPtr.Zero)
            {
                Marshal.Release(array);
            }
            if (item != IntPtr.Zero)
            {
                Marshal.Release(item);
            }
        }
    }

    public static void Invoke(Guid clsid, string path)
    {
        InvokeMultiple(clsid, new[] { path });
    }

    public static void InvokeMultiple(Guid clsid, string[] paths)
    {
        var pidls = new IntPtr[paths.Length];
        IntPtr array = IntPtr.Zero;
        IExplorerCommandRaw command = null;
        try
        {
            Guid iidExplorerCommand = new Guid("a08ce4d0-fa25-44ab-b57c-c7b1c323e0b9");
            for (int index = 0; index < paths.Length; index++)
            {
                SHParseDisplayName(paths[index], IntPtr.Zero, out pidls[index], 0, out uint attributes);
            }

            SHCreateShellItemArrayFromIDLists((uint)pidls.Length, pidls, out array);
            CoCreateInstance(ref clsid, IntPtr.Zero, 23, ref iidExplorerCommand, out command);

            int hr = command.Invoke(array, IntPtr.Zero);
            if (hr < 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }
        }
        finally
        {
            if (command != null)
            {
                Marshal.ReleaseComObject(command);
            }
            if (array != IntPtr.Zero)
            {
                Marshal.Release(array);
            }
            foreach (IntPtr pidl in pidls)
            {
                if (pidl != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(pidl);
                }
            }
        }
    }
}
'@

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$extractHereClsid = [Guid]'4F4C6D2A-71B9-4E4E-9A7D-8D9A0B5E6C01'
$extractFolderClsid = [Guid]'4F4C6D2A-71B9-4E4E-9A7D-8D9A0B5E6C02'
$compressClsid = [Guid]'4F4C6D2A-71B9-4E4E-9A7D-8D9A0B5E6C04'
$sevenZipFixture = Join-Path $repoRoot 'tests\Hodozip.Core.Tests\Fixtures\formats\sample.7z'
$workRoot = Join-Path $env:TEMP 'HodozipContextMenuE2E'
$archives = Join-Path $workRoot 'archives'
$payload = Join-Path $workRoot 'payload'
$settingsDirectory = Join-Path $env:LOCALAPPDATA 'Hodozip'
$settingsPath = Join-Path $settingsDirectory 'settings.json'

Write-Host '=== MSIX 登録の確認 ==='
$package = Get-AppxPackage Ren9618.Hodozip
if ($null -eq $package) {
    throw 'MSIX 版の Hodozip がインストールされていません。先に Add-AppxPackage でインストールしてください。'
}
Write-Host "  Package: $($package.PackageFullName)"

$classIndex = Get-ChildItem 'HKLM:\SOFTWARE\Classes\PackagedCom\ClassIndex' -ErrorAction SilentlyContinue
foreach ($clsid in @($extractHereClsid.ToString('B'), $extractFolderClsid.ToString('B'), $compressClsid.ToString('B'))) {
    if ($null -eq ($classIndex | Where-Object { $_.PSChildName -ieq $clsid })) {
        throw "PackagedCom の CLSID が登録されていません: $clsid"
    }
    Write-Host "  OK: PackagedCom $clsid"
}

$openWith = Get-ItemProperty 'HKCU:\Software\Classes\.zip\OpenWithProgids' -ErrorAction SilentlyContinue
$hodozipProgId = $null
$hodozipApplicationKey = $null
foreach ($property in $openWith.PSObject.Properties) {
    if ($property.Name -notlike 'AppX*') {
        continue
    }

    $applicationKey = "HKCU:\Software\Classes\$($property.Name)\Application"
    if (-not (Test-Path $applicationKey)) {
        continue
    }

    if ((Get-ItemProperty $applicationKey).ApplicationName -eq 'Hodozip') {
        $hodozipProgId = $property.Name
        $hodozipApplicationKey = $applicationKey
        break
    }
}

if ($null -eq $hodozipProgId) {
    throw '.zip の OpenWithProgids に Hodozip が登録されていません。'
}
Write-Host "  OK: .zip OpenWith progid $hodozipProgId"

$application = Get-ItemProperty $hodozipApplicationKey
if ($application.AppUserModelID -ne "$($package.PackageFamilyName)!Hodozip") {
    throw "AppUserModelID が一致しません: $($application.AppUserModelID)"
}
Write-Host "  OK: AppUserModelID $($application.AppUserModelID)"

function Assert-Exists {
    param([string]$Path, [string]$Label)
    if (-not (Test-Path -LiteralPath $Path)) {
        throw "E2E 失敗: $Label が見つかりません: $Path"
    }
    Write-Host "  OK: $Label"
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

function Wait-CompressProcess {
    param([string[]]$SourcePaths, [int]$TimeoutSeconds = 30)

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $processes = Get-CimInstance Win32_Process -Filter "Name='Hodozip.exe'" -ErrorAction SilentlyContinue
        foreach ($process in $processes) {
            if ($null -eq $process.CommandLine) {
                continue
            }

            $matchesAll = $true
            foreach ($sourcePath in $SourcePaths) {
                if (-not $process.CommandLine.Contains($sourcePath)) {
                    $matchesAll = $false
                    break
                }
            }

            if ($matchesAll) {
                return $process
            }
        }
        Start-Sleep -Milliseconds 250
    }

    Get-Process Hodozip -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    throw "圧縮用の Hodozip プロセスが $TimeoutSeconds 秒以内に起動しませんでした。"
}

$settingsBackup = $null
if (Test-Path -LiteralPath $settingsPath) {
    $settingsBackup = Get-Content -LiteralPath $settingsPath -Raw
}

try {
    Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $archives, $payload | Out-Null

    $top = Join-Path $payload 'Top'
    New-Item -ItemType Directory -Path (Join-Path $top 'sub') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $top 'a.txt') -Value 'context menu A' -Encoding utf8
    Set-Content -LiteralPath (Join-Path $top 'sub\b.txt') -Value 'context menu B' -Encoding utf8
    Compress-Archive -LiteralPath $top -DestinationPath (Join-Path $archives 'context-here.zip') -CompressionLevel Optimal

    $scattered = Join-Path $payload 'scattered'
    New-Item -ItemType Directory -Path (Join-Path $scattered 'data') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $scattered 'readme.txt') -Value 'context folder readme' -Encoding utf8
    Set-Content -LiteralPath (Join-Path $scattered 'data\value.txt') -Value 'context folder value' -Encoding utf8
    Compress-Archive -Path (Join-Path $scattered '*') -DestinationPath (Join-Path $archives 'context-folder.zip') -CompressionLevel Optimal

    New-Item -ItemType Directory -Path $settingsDirectory -Force | Out-Null
    @{
        DestinationRule = 'SameAsArchive'
        OpenFolderAfterExtraction = $false
        CloseAfterAutoExtract = $true
        FolderCollisionPolicy = 'Merge'
        OverwritePolicy = 'Overwrite'
    } | ConvertTo-Json | Set-Content -LiteralPath $settingsPath -Encoding utf8

    Write-Host '=== COM 登録とタイトル ==='
    $hereTitle = [ExplorerCommandTest]::GetTitle($extractHereClsid, (Join-Path $archives 'context-here.zip'))
    $folderTitle = [ExplorerCommandTest]::GetTitle($extractFolderClsid, (Join-Path $archives 'context-folder.zip'))
    $compressSourceA = Join-Path $payload 'context-compress-a.txt'
    $compressSourceB = Join-Path $payload 'context-compress-b.txt'
    Set-Content -LiteralPath $compressSourceA -Value 'context compress A' -Encoding utf8
    Set-Content -LiteralPath $compressSourceB -Value 'context compress B' -Encoding utf8
    $compressTitle = [ExplorerCommandTest]::GetTitle($compressClsid, $compressSourceA)
    Write-Host "  ExtractHere:   $hereTitle"
    Write-Host "  ExtractFolder: $folderTitle"
    Write-Host "  Compress:      $compressTitle"
    if ($hereTitle -ne 'Hodozip で解凍' -or $folderTitle -ne 'Hodozip でフォルダに解凍' -or $compressTitle -ne 'Hodozip で圧縮') {
        throw 'コンテキストメニューのタイトルが期待値と一致しません。'
    }

    Write-Host '=== コンテキストメニュー verb: Hodozip で解凍 ==='
    [ExplorerCommandTest]::Invoke($extractHereClsid, (Join-Path $archives 'context-here.zip'))
    Wait-HodozipExit
    Assert-Exists -Path (Join-Path $archives 'Top\a.txt') -Label 'Top\a.txt'
    Assert-Exists -Path (Join-Path $archives 'Top\sub\b.txt') -Label 'Top\sub\b.txt'

    Write-Host '=== コンテキストメニュー verb: Hodozip でフォルダに解凍 ==='
    [ExplorerCommandTest]::Invoke($extractFolderClsid, (Join-Path $archives 'context-folder.zip'))
    Wait-HodozipExit
    Assert-Exists -Path (Join-Path $archives 'context-folder\readme.txt') -Label 'context-folder\readme.txt'
    Assert-Exists -Path (Join-Path $archives 'context-folder\data\value.txt') -Label 'context-folder\data\value.txt'

    Write-Host '=== コンテキストメニュー verb: 7z（ZIP 以外の対応形式） ==='
    if (-not (Test-Path -LiteralPath $sevenZipFixture)) {
        throw "7z フィクスチャが見つかりません: $sevenZipFixture"
    }
    $sevenZipPath = Join-Path $archives 'context-seven.7z'
    Copy-Item -LiteralPath $sevenZipFixture -Destination $sevenZipPath -Force
    [ExplorerCommandTest]::Invoke($extractHereClsid, $sevenZipPath)
    Wait-HodozipExit
    Assert-Exists -Path (Join-Path $archives 'context-seven\hello.txt') -Label 'context-seven\hello.txt'
    Assert-Exists -Path (Join-Path $archives 'context-seven\Top\a.txt') -Label 'context-seven\Top\a.txt'

    Write-Host '=== コンテキストメニュー verb: Hodozip で圧縮（複数選択） ==='
    [ExplorerCommandTest]::InvokeMultiple($compressClsid, @($compressSourceA, $compressSourceB))
    $compressProcess = Wait-CompressProcess -SourcePaths @($compressSourceA, $compressSourceB)
    Write-Host "  OK: Hodozip 起動 (PID=$($compressProcess.ProcessId))"
    if ($compressProcess.CommandLine -notmatch '--compress') {
        throw "E2E 失敗: 圧縮コマンド ラインに --compress がありません: $($compressProcess.CommandLine)"
    }
    Write-Host '  OK: コマンド ラインに --compress と 2 ファイルを含む'
    Stop-Process -Id $compressProcess.ProcessId -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500

    Write-Host 'コンテキストメニュー E2E: すべて成功しました。'
}
finally {
    if ($null -ne $settingsBackup) {
        Set-Content -LiteralPath $settingsPath -Value $settingsBackup -Encoding utf8 -NoNewline
    }
    elseif (Test-Path -LiteralPath $settingsPath) {
        Remove-Item -LiteralPath $settingsPath -Force
    }
}

