# Hodozip Phase 6 E2E: associations, fixed destination, background, password
# generation, and macOS compatibility.
#
# Usage: pwsh -File tests/e2e/run-phase6-e2e.ps1 [-ExePath <Hodozip.exe>]
# Requires: Release x64 build.

param(
    [string]$ExePath
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Set-StrictMode -Version Latest

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName Microsoft.VisualBasic
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class NativeWindow
{
    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
'@
Add-Type -AssemblyName System.IO.Compression.FileSystem

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if ([string]::IsNullOrWhiteSpace($ExePath)) {
    $ExePath = Join-Path $repoRoot 'src\Hodozip\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\Hodozip.exe'
}
$exePath = [System.IO.Path]::GetFullPath($ExePath)
if (-not (Test-Path -LiteralPath $exePath)) {
    throw "Hodozip.exe が見つかりません。先に Release x64 ビルドを実行してください: $exePath"
}

$workRoot = Join-Path $env:TEMP 'HodozipPhase6E2E'
$settingsDirectory = Join-Path $env:LOCALAPPDATA 'Hodozip'
$settingsPath = Join-Path $settingsDirectory 'settings.json'
$progId = 'Hodozip.Archive'
$assocKey = 'HKCU:\Software\Classes\.zip\OpenWithProgids'

$hadSettings = Test-Path -LiteralPath $settingsPath
$settingsBackup = if ($hadSettings) { Get-Content -LiteralPath $settingsPath -Raw } else { $null }
$hadAssociation = $false
if (Test-Path -LiteralPath $assocKey) {
    $hadAssociation = [bool]((Get-Item -LiteralPath $assocKey).GetValueNames() -contains $progId)
}

function Write-Settings {
    param([hashtable]$Overrides)

    $settings = @{
        DestinationRule = 'SameAsArchive'
        ExtractionDestination = 'SameAsArchive'
        ExtractionDestinationFolder = $null
        CompressionDestination = 'SameAsArchive'
        CompressionDestinationFolder = $null
        OpenFolderAfterExtraction = $false
        CloseAfterAutoExtract = $true
        FolderCollisionPolicy = 'Merge'
        OverwritePolicy = 'Overwrite'
        Language = 'System'
        MaxDegreeOfParallelism = 1
        CheckForUpdatesOnStartup = $false
        SkipExistingIfNewer = $false
        ExcludeMacOsMetadata = $true
        NormalizeFileNamesNfc = $true
        NotifyOnCompletion = $false
        RunInBackground = $false
        NotificationSound = 'None'
        NotificationSoundPath = $null
        GeneratedPasswordLength = 20
        GeneratedPasswordLowerCase = $true
        GeneratedPasswordUpperCase = $true
        GeneratedPasswordDigits = $true
        GeneratedPasswordSymbols = $true
    }

    foreach ($key in $Overrides.Keys) {
        $settings[$key] = $Overrides[$key]
    }

    New-Item -ItemType Directory -Path $settingsDirectory -Force | Out-Null
    $settings | ConvertTo-Json | Set-Content -LiteralPath $settingsPath -Encoding utf8
}

function New-Zip {
    param(
        [string]$Path,
        [array]$Entries,
        [System.IO.Compression.CompressionLevel]$Level = [System.IO.Compression.CompressionLevel]::Optimal
    )

    Remove-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path (Split-Path -Parent $Path) -Force | Out-Null
    $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Create)
    try {
        $archive = [System.IO.Compression.ZipArchive]::new(
            $stream,
            [System.IO.Compression.ZipArchiveMode]::Create,
            $false)
        try {
            foreach ($entry in $Entries) {
                $zipEntry = $archive.CreateEntry($entry.Name, $Level)
                if ($null -eq $entry.Content) {
                    continue
                }

                $writer = [System.IO.StreamWriter]::new($zipEntry.Open(), [System.Text.UTF8Encoding]::new($false))
                try {
                    $writer.Write([string]$entry.Content)
                }
                finally {
                    $writer.Dispose()
                }
            }
        }
        finally {
            $archive.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Wait-ForFile {
    param([string]$Path, [int]$TimeoutSeconds = 60)

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $Path) {
            return
        }
        Start-Sleep -Milliseconds 200
    }

    throw "E2E 失敗: ファイルが $TimeoutSeconds 秒以内にできませんでした: $Path"
}

function Wait-ForExit {
    param([System.Diagnostics.Process]$Process, [int]$TimeoutSeconds = 90)

    if (-not $Process.WaitForExit($TimeoutSeconds * 1000)) {
        try { $Process.Kill($true) } catch { }
        throw "E2E 失敗: プロセスが $TimeoutSeconds 秒以内に終了しませんでした。"
    }
}

function Find-ByAutomationId {
    param($Root, [string]$AutomationId, [int]$TimeoutSeconds = 30)

    $condition = [System.Windows.Automation.PropertyCondition]::new(
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

function Find-ByName {
    param($Root, [string]$Name, [System.Windows.Automation.ControlType]$ControlType, [int]$TimeoutSeconds = 30)

    $conditions = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            $Name),
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            $ControlType))
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $element = $Root.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            $conditions)
        if ($null -ne $element) {
            return $element
        }
        Start-Sleep -Milliseconds 250
    }
    return $null
}

function Invoke-Element {
    param($Element, [string]$Label, [int]$ProcessId)

    if ($null -eq $Element) {
        throw "E2E 失敗: $Label が見つかりません。"
    }

    try {
        $scroll = $Element.GetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern)
        $scroll.ScrollIntoView()
    }
    catch { }

    $invoked = $false
    try {
        $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        $invoked = $true
    }
    catch { }

    if (-not $invoked) {
        [Microsoft.VisualBasic.Interaction]::AppActivate($ProcessId) | Out-Null
        Start-Sleep -Milliseconds 200
        try {
            $Element.SetFocus()
        }
        catch { }
        Start-Sleep -Milliseconds 200
        [System.Windows.Forms.SendKeys]::SendWait(' ')
    }

    Start-Sleep -Milliseconds 500
    Write-Host "  OK: $Label を操作"
}

function Get-AppWindows {
    param([int]$ProcessId)

    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
        $ProcessId)
    return @([System.Windows.Automation.AutomationElement]::RootElement.FindAll(
        [System.Windows.Automation.TreeScope]::Children,
        $condition))
}

function Wait-AppWindow {
    param([int]$ProcessId, [int]$TimeoutSeconds = 30)

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $windows = @(Get-AppWindows -ProcessId $ProcessId)
        if ($windows.Count -gt 0) {
            return $windows[0]
        }
        Start-Sleep -Milliseconds 250
    }

    throw "E2E 失敗: Hodozip のウィンドウが $TimeoutSeconds 秒以内に現れませんでした。"
}

function Assert-RegistryValue {
    param([string]$Path, [string]$Name, [bool]$Expected)

    $exists = $false
    if (Test-Path -LiteralPath $Path) {
        $exists = [bool]((Get-Item -LiteralPath $Path).GetValueNames() -contains $Name)
    }

    if ($exists -ne $Expected) {
        throw "E2E 失敗: レジストリ $Path\$Name の存在が想定と異なります（期待: $Expected / 実際: $exists）。"
    }
    Write-Host "  OK: $Path\$Name = $Expected"
}

function Read-CentralDirectory {
    param([string]$Path)

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $entries = @()
    for ($offset = 0; $offset -le $bytes.Length - 46; $offset++) {
        if ([BitConverter]::ToUInt32($bytes, $offset) -ne 0x02014b50) {
            continue
        }

        $flags = [BitConverter]::ToUInt16($bytes, $offset + 8)
        $nameLength = [BitConverter]::ToUInt16($bytes, $offset + 28)
        $extraLength = [BitConverter]::ToUInt16($bytes, $offset + 30)
        $commentLength = [BitConverter]::ToUInt16($bytes, $offset + 32)
        $name = [System.Text.Encoding]::UTF8.GetString($bytes, $offset + 46, $nameLength)
        $entries += [pscustomobject]@{
            Name = $name
            Utf8Flag = (($flags -band 0x0800) -ne 0)
        }
        $offset += 45 + $nameLength + $extraLength + $commentLength
    }

    return $entries
}

try {
    Get-Process Hodozip -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $workRoot -Force | Out-Null

    Write-Host '=== P1: 関連付けトグル（OpenWithProgids） ==='
    Write-Settings @{}
    $app = Start-Process -FilePath $exePath -PassThru
    $mainWindow = Wait-AppWindow -ProcessId $app.Id
    Start-Sleep -Seconds 2
    $settingsButton = Find-ByAutomationId -Root $mainWindow -AutomationId 'SettingsButton'
    Invoke-Element -Element $settingsButton -Label '設定ボタン' -ProcessId $app.Id

    Start-Sleep -Seconds 2
    $settingsWindow = $null
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while ([DateTime]::UtcNow -lt $deadline -and $null -eq $settingsWindow) {
        foreach ($window in Get-AppWindows -ProcessId $app.Id) {
            $zipCheck = Find-ByName -Root $window -Name '.zip' -ControlType ([System.Windows.Automation.ControlType]::CheckBox) -TimeoutSeconds 1
            if ($null -ne $zipCheck) {
                $settingsWindow = $window
                break
            }
        }
        if ($null -eq $settingsWindow) {
            Start-Sleep -Milliseconds 250
        }
    }

    if ($null -eq $settingsWindow) {
        throw 'E2E 失敗: 設定画面の関連付け一覧が見つかりませんでした。'
    }
    Write-Host '  OK: 設定画面の関連付け一覧を確認'

    $zipCheck = Find-ByName -Root $settingsWindow -Name '.zip' -ControlType ([System.Windows.Automation.ControlType]::CheckBox)
    $isOn = [bool]((Get-Item -LiteralPath $assocKey -ErrorAction SilentlyContinue).GetValueNames() -contains $progId)
    if (-not $isOn) {
        Invoke-Element -Element $zipCheck -Label '.zip の関連付け' -ProcessId $app.Id
        Assert-RegistryValue -Path $assocKey -Name $progId -Expected $true
    }

    Invoke-Element -Element $zipCheck -Label '.zip の関連付け解除' -ProcessId $app.Id
    Assert-RegistryValue -Path $assocKey -Name $progId -Expected $false

    Invoke-Element -Element $zipCheck -Label '.zip の関連付け再登録' -ProcessId $app.Id
    Assert-RegistryValue -Path $assocKey -Name $progId -Expected $true
    Write-Host '  OK: 関連付けトグルが即時にレジストリへ反映され、読み直しでも一致'

    try { $app.Kill($true) } catch { }
    Start-Sleep -Milliseconds 500

    Write-Host '=== P2: 固定解凍先 ==='
    $fixedDestination = Join-Path $workRoot 'fixed-destination'
    $p2Archive = Join-Path $workRoot 'p2\fixed.zip'
    New-Zip -Path $p2Archive -Entries @(
        @{ Name = 'data/p2.txt'; Content = 'fixed destination' }
    )
    Write-Settings @{
        ExtractionDestination = 'FixedFolder'
        ExtractionDestinationFolder = $fixedDestination
    }
    $process = Start-Process -FilePath $exePath -ArgumentList @($p2Archive, '--extract-here') -PassThru
    Wait-ForExit -Process $process -TimeoutSeconds 45
    $expected = Join-Path $fixedDestination 'data\p2.txt'
    Wait-ForFile -Path $expected
    if ((Get-Content -LiteralPath $expected -Raw).Trim() -ne 'fixed destination') {
        throw 'E2E 失敗: 固定解凍先の内容が一致しません。'
    }
    Write-Host "  OK: 固定解凍先へ展開: $expected"

    Write-Host '=== P3: バックグラウンド継続 ==='
    $bgArchive = Join-Path $workRoot 'p3\background.zip'
    $bgEntries = @()
    for ($index = 0; $index -lt 12000; $index++) {
        $bgEntries += @{ Name = ('bulk/file{0:D5}.txt' -f $index); Content = ('payload-{0}' -f $index) }
    }
    New-Zip -Path $bgArchive -Entries $bgEntries -Level ([System.IO.Compression.CompressionLevel]::NoCompression)
    Write-Settings @{
        RunInBackground = $true
        NotifyOnCompletion = $true
        NotificationSound = 'System'
        CloseAfterAutoExtract = $false
    }
    $bgProcess = Start-Process -FilePath $exePath -ArgumentList @($bgArchive, '--extract-here') -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while ([DateTime]::UtcNow -lt $deadline) {
        $bgProcess.Refresh()
        if (-not $bgProcess.HasExited -and $bgProcess.MainWindowTitle -match '%|…') {
            break
        }
        Start-Sleep -Milliseconds 200
    }

    if ($bgProcess.HasExited) {
        throw 'E2E 失敗: バックグラウンド試験の進捗ウィンドウが起動前に終了しました。'
    }

    $null = $bgProcess.CloseMainWindow()
    Start-Sleep -Seconds 2
    $bgProcess.Refresh()
    if ($bgProcess.HasExited) {
        throw 'E2E 失敗: バックグラウンド設定 ON なのに、閉じた直後にプロセスが終了しました。'
    }
    Write-Host '  OK: 進捗ウィンドウを閉じてもプロセスは継続'

    Wait-ForExit -Process $bgProcess -TimeoutSeconds 120
    Wait-ForFile -Path (Join-Path $workRoot 'p3\bulk\file11999.txt')
    Write-Host '  OK: 閉じた後も最後まで解凍し、完了後に終了（トースト/通知音の設定も有効）'

    Write-Host '=== P3b: メイン画面の「実行中のジョブ」（再表示 / キャンセル） ==='
    Write-Settings @{
        RunInBackground = $true
        NotifyOnCompletion = $false
        CloseAfterAutoExtract = $false
    }
    $jobProcess = Start-Process -FilePath $exePath -PassThru
    $jobMainWindow = Wait-AppWindow -ProcessId $jobProcess.Id
    Start-Sleep -Seconds 2

    $selectButton = Find-ByAutomationId -Root $jobMainWindow -AutomationId 'SelectFileButton'
    Invoke-Element -Element $selectButton -Label 'ファイルを選択' -ProcessId $jobProcess.Id
    Start-Sleep -Seconds 2
    [System.Windows.Forms.SendKeys]::SendWait($bgArchive)
    Start-Sleep -Milliseconds 300
    [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')

    $jobProgress = $null
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while ([DateTime]::UtcNow -lt $deadline -and $null -eq $jobProgress) {
        foreach ($window in Get-AppWindows -ProcessId $jobProcess.Id) {
            if ($window.Current.Name -match '%|…') {
                $jobProgress = $window
                break
            }
        }
        Start-Sleep -Milliseconds 200
    }
    if ($null -eq $jobProgress) {
        throw 'E2E 失敗: メイン画面からの解凍で進捗ウィンドウが現れませんでした。'
    }

    [NativeWindow]::PostMessage(
        [IntPtr]$jobProgress.Current.NativeWindowHandle,
        0x0010,
        [IntPtr]::Zero,
        [IntPtr]::Zero) | Out-Null
    Start-Sleep -Seconds 2
    $jobProcess.Refresh()
    if ($jobProcess.HasExited) {
        throw 'E2E 失敗: バックグラウンド設定 ON で進捗ウィンドウを閉じた直後に終了しました。'
    }

    $jobMainWindow = $null
    foreach ($window in Get-AppWindows -ProcessId $jobProcess.Id) {
        if ($window.Current.Name -eq 'Hodozip') {
            $jobMainWindow = $window
            break
        }
    }
    if ($null -eq $jobMainWindow) {
        throw 'E2E 失敗: 進捗ウィンドウを閉じた後にメイン画面が見つかりません。'
    }

    $jobsTitle = Find-ByName -Root $jobMainWindow -Name '実行中のジョブ' -ControlType ([System.Windows.Automation.ControlType]::Text)
    if ($null -eq $jobsTitle) {
        throw 'E2E 失敗: メイン画面に「実行中のジョブ」が表示されません。'
    }
    $jobTextCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Text)
    $jobHeaderFound = $false
    $jobTexts = @()
    foreach ($element in $jobMainWindow.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        $jobTextCondition)) {
        $name = $element.Current.Name
        $jobTexts += $name
        if ($name -like '*background.zip*') {
            $jobHeaderFound = $true
        }
    }
    if (-not $jobHeaderFound) {
        throw "E2E 失敗: 実行中ジョブの名前がメイン画面に表示されません: $($jobTexts -join ' / ')"
    }

    $showButton = Find-ByName -Root $jobMainWindow -Name '再表示' -ControlType ([System.Windows.Automation.ControlType]::Button)
    $cancelButton = Find-ByName -Root $jobMainWindow -Name 'キャンセル' -ControlType ([System.Windows.Automation.ControlType]::Button)
    if ($null -eq $showButton -or $null -eq $cancelButton) {
        throw 'E2E 失敗: 実行中ジョブの「再表示」「キャンセル」ボタンがありません。'
    }
    Write-Host '  OK: 閉じた後もメイン画面にジョブ名・再表示・キャンセルが表示'

    Invoke-Element -Element $showButton -Label 'ジョブの再表示' -ProcessId $jobProcess.Id
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    $restored = $false
    while ([DateTime]::UtcNow -lt $deadline) {
        foreach ($window in Get-AppWindows -ProcessId $jobProcess.Id) {
            if ($window.Current.Name -match '%|…') {
                $restored = $true
                break
            }
        }
        if ($restored) { break }
        Start-Sleep -Milliseconds 200
    }
    if (-not $restored) {
        throw 'E2E 失敗: 「再表示」で進捗ウィンドウが戻りませんでした。'
    }
    Write-Host '  OK: 「再表示」で進捗ウィンドウが戻る'
    try { $jobProcess.Kill($true) } catch { }

    Write-Host '=== P4: 圧縮パスワードのランダム生成（UI） ==='
    $passwordSource = Join-Path $workRoot 'p4\source.txt'
    New-Item -ItemType Directory -Path (Split-Path -Parent $passwordSource) -Force | Out-Null
    Set-Content -LiteralPath $passwordSource -Value 'password source' -Encoding utf8
    Write-Settings @{}
    $uiProcess = Start-Process -FilePath $exePath -ArgumentList @('--compress', $passwordSource) -PassThru
    $compressionWindow = Wait-AppWindow -ProcessId $uiProcess.Id
    $generateButton = Find-ByAutomationId -Root $compressionWindow -AutomationId 'CompressionGeneratePasswordButton'
    Invoke-Element -Element $generateButton -Label 'ランダム生成ボタン' -ProcessId $uiProcess.Id

    $showButton = Find-ByAutomationId -Root $compressionWindow -AutomationId 'CompressionShowPasswordButton'
    Invoke-Element -Element $showButton -Label 'パスワード表示ボタン' -ProcessId $uiProcess.Id
    Start-Sleep -Milliseconds 300

    $passwordBox = Find-ByAutomationId -Root $compressionWindow -AutomationId 'CompressionPasswordBox'
    if ($null -eq $passwordBox) {
        throw 'E2E 失敗: パスワード欄が見つかりませんでした。'
    }
    $valuePattern = $passwordBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $generated = $valuePattern.Current.Value
    if ($generated.Length -ne 20) {
        throw "E2E 失敗: 生成されたパスワードの長さが 20 ではありません: $($generated.Length)"
    }
    if ($generated -notmatch '[a-z]' -or $generated -notmatch '[A-Z]' -or $generated -notmatch '[0-9]' -or $generated -notmatch '[^a-zA-Z0-9]') {
        throw "E2E 失敗: 生成されたパスワードの文字種が不足しています: $generated"
    }
    Write-Host "  OK: 20 桁・全文字種を含むパスワードを生成（先頭: $($generated.Substring(0, 4))…）"
    try { $uiProcess.Kill($true) } catch { }

    Write-Host '=== P5: macOS 互換の解凍（メタデータ除外 / NFC） ==='
    $nfdName = 'テスト'.Normalize([System.Text.NormalizationForm]::FormD)
    $nfcName = $nfdName.Normalize([System.Text.NormalizationForm]::FormC)
    $macArchive = Join-Path $workRoot 'p5\mac.zip'
    New-Zip -Path $macArchive -Entries @(
        @{ Name = "docs/$nfdName.txt"; Content = 'mac japanese' }
        @{ Name = "__MACOSX/docs/._$nfdName.txt"; Content = 'metadata' }
        @{ Name = '.DS_Store'; Content = 'metadata' }
        @{ Name = 'docs/._resource'; Content = 'metadata' }
    )
    Write-Settings @{ ExcludeMacOsMetadata = $true; NormalizeFileNamesNfc = $true }
    $process = Start-Process -FilePath $exePath -ArgumentList @($macArchive, '--extract-here') -PassThru
    Wait-ForExit -Process $process -TimeoutSeconds 45
    $macOutput = Join-Path (Split-Path -Parent $macArchive) 'docs'
    Wait-ForFile -Path (Join-Path $macOutput ($nfcName + '.txt'))
    if (Test-Path -LiteralPath (Join-Path $macOutput '._resource')) {
        throw 'E2E 失敗: macOS メタデータが除外されていません。'
    }
    if (Test-Path -LiteralPath (Join-Path (Split-Path -Parent $macArchive) '.DS_Store')) {
        throw 'E2E 失敗: .DS_Store が除外されていません。'
    }
    Write-Host '  OK: __MACOSX / .DS_Store / ._ を除外し、NFC 名で展開'

    Write-Host '=== P6: macOS 互換の圧縮（UTF-8 フラグ + NFC） ==='
    $macSource = Join-Path $workRoot 'p6\source'
    New-Item -ItemType Directory -Path $macSource -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $macSource ($nfdName + '.txt')) -Value 'nfd name' -Encoding utf8
    $macZip = Join-Path $workRoot 'p6\mac-compatible.zip'
    $process = Start-Process -FilePath $exePath -ArgumentList @(
        '--compress', $macSource, '--output', $macZip, '--format', 'zip', '--macos-compatible') -PassThru
    Wait-ForExit -Process $process -TimeoutSeconds 60
    if ($process.ExitCode -ne 0) {
        throw "E2E 失敗: macOS 互換圧縮が終了コード $($process.ExitCode) で失敗しました。"
    }
    Wait-ForFile -Path $macZip
    $entries = Read-CentralDirectory -Path $macZip
    if ($entries.Count -eq 0) {
        throw 'E2E 失敗: ZIP のセントラル ディレクトリを読めませんでした。'
    }
    foreach ($entry in $entries) {
        if (-not $entry.Utf8Flag) {
            throw "E2E 失敗: UTF-8 フラグが立っていません: $($entry.Name)"
        }
        if ($entry.Name -ne $entry.Name.Normalize([System.Text.NormalizationForm]::FormC)) {
            throw "E2E 失敗: ファイル名が NFC ではありません: $($entry.Name)"
        }
    }
    if (-not ($entries | Where-Object { $_.Name.EndsWith($nfcName + '.txt', [System.StringComparison]::Ordinal) })) {
        throw 'E2E 失敗: NFC に正規化されたエントリ名が見つかりません。'
    }
    Write-Host "  OK: 全エントリに UTF-8 フラグ、NFC 名: $($entries.Name -join ', ')"

    Write-Host ''
    Write-Host 'Phase 6 E2E: すべて成功しました。'
}
finally {
    Get-Process Hodozip -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

    if ($hadSettings) {
        Set-Content -LiteralPath $settingsPath -Value $settingsBackup -Encoding utf8 -NoNewline
    }
    elseif (Test-Path -LiteralPath $settingsPath) {
        Remove-Item -LiteralPath $settingsPath -Force
    }

    if ($hadAssociation) {
        New-Item -Path $assocKey -Force | Out-Null
        Set-ItemProperty -LiteralPath $assocKey -Name $progId -Value ([byte[]]@())
    }
    elseif (Test-Path -LiteralPath $assocKey) {
        Remove-ItemProperty -LiteralPath $assocKey -Name $progId -ErrorAction SilentlyContinue
    }
}
