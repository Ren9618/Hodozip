# Hodozip Phase 4 localization smoke E2E.
# Usage: pwsh -File tests/e2e/run-i18n-e2e.ps1 [-ExePath <Hodozip.exe>]
# Requires: Release x64 build (or publish output). Uses UI Automation.
#
# Verifies that the main window and Settings window are shown in English when
# Language=English, and in Japanese when Language=Japanese. The language is
# stored in settings.json and applied on the next launch.

param(
    [string]$ExePath
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if ([string]::IsNullOrWhiteSpace($ExePath)) {
    $ExePath = Join-Path $repoRoot 'src\Hodozip\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\Hodozip.exe'
}
$exePath = [System.IO.Path]::GetFullPath($ExePath)
$settingsDirectory = Join-Path $env:LOCALAPPDATA 'Hodozip'
$settingsPath = Join-Path $settingsDirectory 'settings.json'

if (-not (Test-Path -LiteralPath $exePath)) {
    throw "Hodozip.exe が見つかりません。先に Release x64 ビルドを実行してください: $exePath"
}

function Write-LanguageSetting {
    param([string]$Language)

    New-Item -ItemType Directory -Path $settingsDirectory -Force | Out-Null
    @{
        DestinationRule = 'SameAsArchive'
        OpenFolderAfterExtraction = $false
        CloseAfterAutoExtract = $false
        FolderCollisionPolicy = 'Merge'
        OverwritePolicy = 'Overwrite'
        Language = $Language
        MaxDegreeOfParallelism = 0
    } | ConvertTo-Json | Set-Content -LiteralPath $settingsPath -Encoding utf8
}

function Get-ProcessCondition {
    param([int]$ProcessId)

    return New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
        $ProcessId)
}

function Start-Hodozip {
    param([string[]]$Arguments = @())

    $process = Start-Process -FilePath $exePath -ArgumentList $Arguments -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($process.HasExited) {
            throw "E2E 失敗: Hodozip が起動直後に終了しました（終了コード $($process.ExitCode)）。"
        }

        $condition = New-Object System.Windows.Automation.AndCondition(
            (Get-ProcessCondition -ProcessId $process.Id),
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::Window)))
        $window = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            $condition)
        if ($null -ne $window) {
            return $process
        }

        Start-Sleep -Milliseconds 250
    }

    try { $process.Kill($true) } catch { }
    throw 'E2E 失敗: Hodozip のメイン ウィンドウを UIA で検出できませんでした。'
}

function Get-ElementNames {
    param([int]$ProcessId)

    $condition = Get-ProcessCondition -ProcessId $ProcessId
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while ([DateTime]::UtcNow -lt $deadline) {
        try {
            $all = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
                [System.Windows.Automation.TreeScope]::Descendants,
                $condition)
            $names = New-Object System.Collections.Generic.List[string]
            foreach ($element in $all) {
                try {
                    $name = $element.Current.Name
                    if (-not [string]::IsNullOrWhiteSpace($name)) {
                        $names.Add($name)
                    }
                }
                catch {
                    # 列挙中に閉じたウィンドウの要素は読み飛ばす。
                }
            }

            return $names
        }
        catch {
            # ウィンドウ切り替え直後は UIA が一時的に
            # E_ELEMENTNOTAVAILABLE を返すことがあるため再試行する。
            Start-Sleep -Milliseconds 150
        }
    }

    throw "E2E 失敗: UIA 要素を列挙できませんでした (PID=$ProcessId)。"
}

function Assert-NamePresent {
    param(
        [int]$ProcessId,
        [string]$Expected,
        [string]$Label
    )

    $names = Get-ElementNames -ProcessId $ProcessId
    if (-not $names.Contains($Expected)) {
        $visible = ($names | Select-Object -Unique | Select-Object -First 50) -join ' | '
        throw "E2E 失敗: $Label に '$Expected' が見つかりません。検出した要素: $visible"
    }

    Write-Host "  OK: $Label = $Expected"
}

function Invoke-Button {
    param(
        [int]$ProcessId,
        [string]$Name,
        [string]$Label
    )

    $condition = New-Object System.Windows.Automation.AndCondition(
        (Get-ProcessCondition -ProcessId $ProcessId),
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            $Name)),
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button)))
    $button = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        $condition)
    if ($null -eq $button) {
        throw "E2E 失敗: $Label のボタン '$Name' が見つかりません。"
    }

    $pattern = $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
    Write-Host "  OK: $Label のボタン '$Name' を押しました。"
}

function Wait-ForName {
    param(
        [int]$ProcessId,
        [string]$Expected,
        [int]$TimeoutSeconds = 15
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ((Get-ElementNames -ProcessId $ProcessId).Contains($Expected)) {
            return $true
        }

        Start-Sleep -Milliseconds 250
    }

    return $false
}

function Stop-Hodozip {
    param([System.Diagnostics.Process]$Process)

    if (-not $Process.HasExited) {
        $null = $Process.CloseMainWindow()
        if (-not $Process.WaitForExit(10000)) {
            try { $Process.Kill($true) } catch { }
        }
    }
}

function Find-ElementById {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$AutomationId,
        [int]$TimeoutSeconds = 10
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
        Start-Sleep -Milliseconds 200
    }
    return $null
}

function Select-CompressionFormat {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$ItemName
    )

    $combo = Find-ElementById -Root $Root -AutomationId 'CompressionFormatComboBox'
    if ($null -eq $combo) {
        throw 'E2E 失敗: 圧縮形式のコンボ ボックスが見つかりません。'
    }

    $expand = $combo.GetCurrentPattern(
        [System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $expand.Expand()
    Start-Sleep -Milliseconds 400

    $condition = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            $ItemName)),
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::ListItem)))
    $item = $Root.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        $condition)
    if ($null -eq $item) {
        throw "E2E 失敗: 圧縮形式 '$ItemName' が見つかりません。"
    }

    $selection = $item.GetCurrentPattern(
        [System.Windows.Automation.SelectionItemPattern]::Pattern)
    $selection.Select()
    Start-Sleep -Milliseconds 400
}

function Assert-CompressionPasswordState {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [bool]$ExpectedEnabled,
        [string]$ExpectedNote,
        [string]$Label
    )

    $box = Find-ElementById -Root $Root -AutomationId 'CompressionPasswordBox'
    $note = Find-ElementById -Root $Root -AutomationId 'CompressionPasswordNote'
    if ($null -eq $box -or $null -eq $note) {
        throw "E2E 失敗: $Label のパスワード欄または注記が見つかりません。"
    }
    if ($box.Current.IsEnabled -ne $ExpectedEnabled) {
        throw "E2E 失敗: $Label のパスワード欄の有効状態が想定と異なります: $($box.Current.IsEnabled)"
    }
    if ($note.Current.Name -ne $ExpectedNote) {
        throw "E2E 失敗: $Label のパスワード注記が想定と異なります: '$($note.Current.Name)'"
    }
    Write-Host "  OK: $Label（パスワード欄 enabled=$ExpectedEnabled、注記='$ExpectedNote'）"
}

function Test-CompressionPasswordUi {
    param(
        [string]$Language,
        [string]$ZipNote,
        [string]$SevenZipNote,
        [string]$Label
    )

    Write-Host "=== ${Label}: 圧縮オプションの 7z パスワード表示 ==="
    Write-LanguageSetting -Language $Language
    $source = Join-Path $env:TEMP 'HodozipI18nCompressSource.txt'
    Set-Content -LiteralPath $source -Value 'i18n compression source' -Encoding utf8

    $process = Start-Hodozip -Arguments @('--compress', ('"' + $source + '"'))
    try {
        $root = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
        if ($null -eq $root) {
            throw "E2E 失敗: $Label の圧縮オプション ウィンドウを取得できませんでした。"
        }

        Assert-CompressionPasswordState -Root $root -ExpectedEnabled $true -ExpectedNote $ZipNote -Label "$Label ZIP 初期状態"
        Select-CompressionFormat -Root $root -ItemName '7z (.7z)'
        Assert-CompressionPasswordState -Root $root -ExpectedEnabled $false -ExpectedNote $SevenZipNote -Label "$Label 7z 選択時"
        Select-CompressionFormat -Root $root -ItemName 'ZIP (.zip)'
        Assert-CompressionPasswordState -Root $root -ExpectedEnabled $true -ExpectedNote $ZipNote -Label "$Label ZIP に戻したとき"
    }
    finally {
        Stop-Hodozip -Process $process
    }
}

$settingsBackup = $null
if (Test-Path -LiteralPath $settingsPath) {
    $settingsBackup = Get-Content -LiteralPath $settingsPath -Raw
}

try {
    Write-Host '=== I1: English UI ==='
    Write-LanguageSetting -Language 'English'
    $english = Start-Hodozip
    try {
        Assert-NamePresent -ProcessId $english.Id -Expected 'Unpack archives in a snap. Just drag and drop to extract.' -Label 'English tagline'
        Assert-NamePresent -ProcessId $english.Id -Expected 'Drop an archive here' -Label 'English drop zone'
        Assert-NamePresent -ProcessId $english.Id -Expected 'Select a file' -Label 'English select button'
        Assert-NamePresent -ProcessId $english.Id -Expected 'Settings' -Label 'English settings button'

        Invoke-Button -ProcessId $english.Id -Name 'Settings' -Label 'English main window'
        $settingsExpected = 'Language changes take effect the next time Hodozip starts.'
        if (-not (Wait-ForName -ProcessId $english.Id -Expected $settingsExpected)) {
            throw "E2E 失敗: 設定ウィンドウに英語の言語注記 '$settingsExpected' が見つかりません。"
        }
        Write-Host "  OK: English settings note = $settingsExpected"
        Assert-NamePresent -ProcessId $english.Id -Expected 'Parallel extraction' -Label 'English parallel settings'
        Assert-NamePresent -ProcessId $english.Id -Expected 'Save' -Label 'English save button'
        Invoke-Button -ProcessId $english.Id -Name 'Cancel' -Label 'English settings'
    }
    finally {
        Stop-Hodozip -Process $english
    }

    Write-Host '=== I2: Japanese UI（再起動後に反映） ==='
    Write-LanguageSetting -Language 'Japanese'
    $japanese = Start-Hodozip
    try {
        Assert-NamePresent -ProcessId $japanese.Id -Expected 'アーカイブを、ほどく。ドラッグ＆ドロップですぐに解凍できます。' -Label '日本語タグライン'
        Assert-NamePresent -ProcessId $japanese.Id -Expected 'アーカイブをここにドロップ' -Label '日本語ドロップ ゾーン'
        Assert-NamePresent -ProcessId $japanese.Id -Expected 'ファイルを選択' -Label '日本語ファイル選択ボタン'
        Assert-NamePresent -ProcessId $japanese.Id -Expected '設定' -Label '日本語設定ボタン'

        Invoke-Button -ProcessId $japanese.Id -Name '設定' -Label '日本語メイン ウィンドウ'
        $settingsExpected = '言語の変更は次回起動時に反映されます。'
        if (-not (Wait-ForName -ProcessId $japanese.Id -Expected $settingsExpected)) {
            throw "E2E 失敗: 設定ウィンドウに日本語の言語注記 '$settingsExpected' が見つかりません。"
        }
        Write-Host "  OK: 日本語の設定注記 = $settingsExpected"
        Assert-NamePresent -ProcessId $japanese.Id -Expected '並列解凍' -Label '日本語並列設定'
        Assert-NamePresent -ProcessId $japanese.Id -Expected '保存' -Label '日本語保存ボタン'
        Invoke-Button -ProcessId $japanese.Id -Name 'キャンセル' -Label '日本語設定'
    }
    finally {
        Stop-Hodozip -Process $japanese
    }

    Test-CompressionPasswordUi `
        -Language 'Japanese' `
        -ZipNote 'ZIP は AES-256 で暗号化します。7z の書き込みはパスワード暗号化に対応していないため、7z では入力したパスワードを無視します。' `
        -SevenZipNote '7z ではパスワードは使用されません。パスワードは ZIP を選んだときだけ設定できます。' `
        -Label '日本語'

    Test-CompressionPasswordUi `
        -Language 'English' `
        -ZipNote 'ZIP uses AES-256 encryption. The 7z writer does not support password encryption, so the password is ignored for 7z.' `
        -SevenZipNote 'Passwords are not used with 7z. A password can be set only when ZIP is selected.' `
        -Label 'English'

    Write-Host ''
    Write-Host '多言語スモーク E2E: すべて成功しました。'
}
finally {
    if ($null -ne $settingsBackup) {
        Set-Content -LiteralPath $settingsPath -Value $settingsBackup -Encoding utf8 -NoNewline
    }
    elseif (Test-Path -LiteralPath $settingsPath) {
        Remove-Item -LiteralPath $settingsPath -Force
    }
}
