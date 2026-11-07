$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

[ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IApplicationActivationManager
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
public class ApplicationActivationManager
{
}

public static class AppActivator
{
    public static uint Activate(string appUserModelId, string arguments)
    {
        var manager = (IApplicationActivationManager)new ApplicationActivationManager();
        uint processId;
        int hr = manager.ActivateApplication(appUserModelId, arguments, 0, out processId);
        if (hr < 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }
        return processId;
    }
}
'@

$package = Get-AppxPackage Ren9618.Hodozip
if ($null -eq $package) {
    throw 'MSIX 版の Hodozip がインストールされていません。先に Add-AppxPackage でインストールしてください。'
}
$appId = "$($package.PackageFamilyName)!Hodozip"
$workRoot = Join-Path $env:TEMP 'HodozipPackagedE2E'
$archives = Join-Path $workRoot 'archives'
$payload = Join-Path $workRoot 'payload'
$settingsDirectory = Join-Path $env:LOCALAPPDATA 'Hodozip'
$settingsPath = Join-Path $settingsDirectory 'settings.json'

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

function Invoke-PackagedHodozip {
    param([string]$Arguments, [int]$TimeoutSeconds = 45)

    [uint32]$processId = [AppActivator]::Activate($appId, $Arguments)

    Write-Host "  Activated PID=$processId args=$Arguments"
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
        if ($null -eq $process) {
            return
        }
        Start-Sleep -Milliseconds 250
    }

    $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
    if ($null -ne $process) {
        $process.Kill($true)
    }
    throw "Hodozip が $TimeoutSeconds 秒以内に終了しませんでした (PID=$processId)。"
}

function Find-AutomationElementByID {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$AutomationId,
        [int]$TimeoutSeconds = 30
    )

    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $element = $Root.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            $condition)
        if ($null -ne $element) {
            return $element
        }
        Start-Sleep -Milliseconds 250
    }

    return $null
}

function Assert-PackagedUpdateSettingDisabled {
    [uint32]$processId = [AppActivator]::Activate($appId, '')
    Write-Host "  Activated PID=$processId"

    $mainWindow = $null
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    while ([DateTime]::UtcNow -lt $deadline) {
        $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
        if ($null -ne $process -and $process.MainWindowHandle -ne [IntPtr]::Zero) {
            $mainWindow = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
            if ($null -ne $mainWindow) {
                break
            }
        }
        Start-Sleep -Milliseconds 250
    }

    if ($null -eq $mainWindow) {
        try { (Get-Process -Id $processId -ErrorAction SilentlyContinue).Kill($true) } catch { }
        throw 'E2E 失敗: MSIX 版のメイン ウィンドウを取得できませんでした。'
    }

    try {
        $settingsButton = Find-AutomationElementByID -Root $mainWindow -AutomationId 'SettingsButton'
        if ($null -eq $settingsButton) {
            throw 'E2E 失敗: 設定ボタンが見つかりませんでした。'
        }
        $settingsButton.GetCurrentPattern(
            [System.Windows.Automation.InvokePattern]::Pattern).Invoke()

        $settingsWindow = $null
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        while ([DateTime]::UtcNow -lt $deadline) {
            $windowCondition = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
                [int]$processId)
            $windows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
                [System.Windows.Automation.TreeScope]::Children,
                $windowCondition)
            foreach ($window in $windows) {
                if ($null -ne (Find-AutomationElementByID -Root $window -AutomationId 'CheckUpdatesToggle' -TimeoutSeconds 1)) {
                    $settingsWindow = $window
                    break
                }
            }
            if ($null -ne $settingsWindow) {
                break
            }
            Start-Sleep -Milliseconds 250
        }

        if ($null -eq $settingsWindow) {
            throw 'E2E 失敗: 設定ウィンドウを取得できませんでした。'
        }

        $toggle = Find-AutomationElementByID -Root $settingsWindow -AutomationId 'CheckUpdatesToggle'
        if ($null -eq $toggle) {
            throw 'E2E 失敗: 更新確認トグルが見つかりませんでした。'
        }
        if ($toggle.Current.IsEnabled) {
            throw 'E2E 失敗: MSIX 版で更新確認トグルが有効になっています。'
        }
        $togglePattern = $toggle.GetCurrentPattern(
            [System.Windows.Automation.TogglePattern]::Pattern)
        if ($togglePattern.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::Off) {
            throw "E2E 失敗: MSIX 版で更新確認トグルが OFF ではありません: $($togglePattern.Current.ToggleState)"
        }

        $note = Find-AutomationElementByID -Root $settingsWindow -AutomationId 'CheckUpdatesNoteText'
        if ($null -eq $note) {
            throw 'E2E 失敗: MSIX 版の更新確認注記が見つかりませんでした。'
        }
        if ($note.Current.Name -notmatch 'MSIX') {
            throw "E2E 失敗: MSIX 版の更新確認注記が想定と異なります: '$($note.Current.Name)'"
        }
        Write-Host "  OK: 更新確認トグルは無効・OFF（注記: '$($note.Current.Name)'）"
    }
    finally {
        $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
        if ($null -ne $process) {
            $null = $process.CloseMainWindow()
            if (-not $process.WaitForExit(10000)) {
                try { $process.Kill($true) } catch { }
            }
        }
    }
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
    Set-Content -LiteralPath (Join-Path $top 'a.txt') -Value 'single folder A' -Encoding utf8
    Set-Content -LiteralPath (Join-Path $top 'sub\b.txt') -Value 'single folder B' -Encoding utf8
    Compress-Archive -LiteralPath $top -DestinationPath (Join-Path $archives 'single-folder.zip') -CompressionLevel Optimal

    $scattered = Join-Path $payload 'scattered'
    New-Item -ItemType Directory -Path (Join-Path $scattered 'data') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $scattered 'readme.txt') -Value 'scattered readme' -Encoding utf8
    Set-Content -LiteralPath (Join-Path $scattered 'data\value.txt') -Value 'scattered value' -Encoding utf8
    Compress-Archive -Path (Join-Path $scattered '*') -DestinationPath (Join-Path $archives 'scattered.zip') -CompressionLevel Optimal

    New-Item -ItemType Directory -Path $settingsDirectory -Force | Out-Null
    @{
        DestinationRule = 'SameAsArchive'
        OpenFolderAfterExtraction = $false
        CloseAfterAutoExtract = $true
        FolderCollisionPolicy = 'Merge'
        OverwritePolicy = 'Overwrite'
    } | ConvertTo-Json | Set-Content -LiteralPath $settingsPath -Encoding utf8

    Write-Host '=== Packaged E2E 1: --extract-here (single folder) ==='
    Invoke-PackagedHodozip -Arguments ('--extract-here "' + (Join-Path $archives 'single-folder.zip') + '"')
    Assert-Exists -Path (Join-Path $archives 'Top\a.txt') -Label 'Top\a.txt'
    Assert-Exists -Path (Join-Path $archives 'Top\sub\b.txt') -Label 'Top\sub\b.txt'
    Assert-NotExists -Path (Join-Path $archives 'single-folder') -Label '二重フォルダ single-folder'

    Write-Host '=== Packaged E2E 2: --extract-folder (scattered) ==='
    Invoke-PackagedHodozip -Arguments ('--extract-folder "' + (Join-Path $archives 'scattered.zip') + '"')
    Assert-Exists -Path (Join-Path $archives 'scattered\readme.txt') -Label 'scattered\readme.txt'
    Assert-Exists -Path (Join-Path $archives 'scattered\data\value.txt') -Label 'scattered\data\value.txt'

    Write-Host '=== Packaged E2E 3: --extract-here (scattered) ==='
    Remove-Item -LiteralPath (Join-Path $archives 'scattered') -Recurse -Force
    Invoke-PackagedHodozip -Arguments ('--extract-here "' + (Join-Path $archives 'scattered.zip') + '"')
    Assert-Exists -Path (Join-Path $archives 'scattered\readme.txt') -Label 'scattered\readme.txt'
    Assert-Exists -Path (Join-Path $archives 'scattered\data\value.txt') -Label 'scattered\data\value.txt'

    Write-Host '=== Packaged E2E 4: MSIX 版は自動更新が無効 ==='
    Assert-PackagedUpdateSettingDisabled

    Write-Host 'Packaged E2E: すべて成功しました。'
}
finally {
    if ($null -ne $settingsBackup) {
        Set-Content -LiteralPath $settingsPath -Value $settingsBackup -Encoding utf8 -NoNewline
    }
    elseif (Test-Path -LiteralPath $settingsPath) {
        Remove-Item -LiteralPath $settingsPath -Force
    }
}


