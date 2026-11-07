# Hodozip compression-options password state E2E (7z).
# Usage: pwsh -File tests/e2e/run-compression-7z-ui-e2e.ps1 [-ExePath <Hodozip.exe>]
#
# Verifies in Japanese and English that selecting 7z disables the password box,
# shows the dedicated 7z note, and that switching back to ZIP re-enables it.

param(
    [string]$ExePath
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Set-StrictMode -Version Latest

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if ([string]::IsNullOrWhiteSpace($ExePath)) {
    $ExePath = Join-Path $repoRoot 'src\Hodozip\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\Hodozip.exe'
}
$exePath = [System.IO.Path]::GetFullPath($ExePath)
if (-not (Test-Path -LiteralPath $exePath)) {
    throw "Hodozip.exe が見つかりません: $exePath"
}

$settingsDirectory = Join-Path $env:LOCALAPPDATA 'Hodozip'
$settingsPath = Join-Path $settingsDirectory 'settings.json'
$hadSettings = Test-Path -LiteralPath $settingsPath
$settingsBackup = if ($hadSettings) { Get-Content -LiteralPath $settingsPath -Raw } else { $null }

$sourcePath = Join-Path $env:TEMP 'HodozipCompression7zE2E\source.txt'
New-Item -ItemType Directory -Path (Split-Path -Parent $sourcePath) -Force | Out-Null
Set-Content -LiteralPath $sourcePath -Value '7z password state e2e' -Encoding utf8

$expected = @{
    Japanese = @{
        ZipNote = 'ZIP は AES-256 で暗号化します。7z の書き込みはパスワード暗号化に対応していないため、7z では入力したパスワードを無視します。'
        SevenZipNote = '7z ではパスワードは使用されません。パスワードは ZIP を選んだときだけ設定できます。'
    }
    English = @{
        ZipNote = 'ZIP uses AES-256 encryption. The 7z writer does not support password encryption, so the password is ignored for 7z.'
        SevenZipNote = 'Passwords are not used with 7z. A password can be set only when ZIP is selected.'
    }
}

function Find-Element {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$AutomationId
    )

    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId)
    return $Root.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        $condition)
}

function Wait-Element {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$AutomationId,
        [int]$TimeoutSeconds = 20
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $element = Find-Element -Root $Root -AutomationId $AutomationId
        if ($null -ne $element) {
            return $element
        }
        Start-Sleep -Milliseconds 100
    }
    throw "自動化要素が見つかりません: $AutomationId"
}

function Select-Format {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [System.Windows.Automation.AutomationElement]$ComboBox,
        [string]$ItemName
    )

    $expandPattern = [System.Windows.Automation.ExpandCollapsePattern]$ComboBox.GetCurrentPattern(
        [System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $expandPattern.Expand()

    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    $item = $null
    while ([DateTime]::UtcNow -lt $deadline -and $null -eq $item) {
        Start-Sleep -Milliseconds 100
        $items = $Root.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)
        for ($index = 0; $index -lt $items.Count; $index++) {
            $candidate = $items.Item($index)
            if ($candidate.Current.ControlType -eq [System.Windows.Automation.ControlType]::ListItem -and
                [string]$candidate.Current.Name -eq $ItemName) {
                $item = $candidate
                break
            }
        }
    }

    if ($null -eq $item) {
        $expandPattern.Collapse()
        throw "形式の項目が見つかりません: $ItemName"
    }

    try {
        $selectionPattern = [System.Windows.Automation.SelectionItemPattern]$item.GetCurrentPattern(
            [System.Windows.Automation.SelectionItemPattern]::Pattern)
        $selectionPattern.Select()
    }
    finally {
        $expandPattern.Collapse()
    }
}

function Wait-PasswordState {
    param(
        [System.Windows.Automation.AutomationElement]$PasswordBox,
        [System.Windows.Automation.AutomationElement]$NoteText,
        [bool]$ExpectedEnabled,
        [string]$ExpectedNote,
        [string]$Label
    )

    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    $lastEnabled = $null
    $lastNote = ''
    while ([DateTime]::UtcNow -lt $deadline) {
        $lastEnabled = [bool]$PasswordBox.Current.IsEnabled
        $lastNote = [string]$NoteText.Current.Name
        if ($lastEnabled -eq $ExpectedEnabled -and $lastNote -eq $ExpectedNote) {
            Write-Host "  OK: $Label (enabled=$lastEnabled)"
            return
        }
        Start-Sleep -Milliseconds 100
    }

    throw "$Label が想定と異なります: enabled=$lastEnabled / note='$lastNote'"
}

function Test-Language {
    param(
        [string]$Language,
        [string]$ZipNote,
        [string]$SevenZipNote
    )

    Write-Host "=== $Language ==="
    @{
        DestinationRule = 'SameAsArchive'
        OpenFolderAfterExtraction = $false
        CloseAfterAutoExtract = $false
        FolderCollisionPolicy = 'Merge'
        OverwritePolicy = 'Overwrite'
        Language = $Language
        CheckForUpdatesOnStartup = $false
    } | ConvertTo-Json | Set-Content -LiteralPath $settingsPath -Encoding utf8

    $process = Start-Process -FilePath $exePath -ArgumentList @('--compress', $sourcePath) -PassThru
    try {
        # WinUI 3 は起動途中でウィンドウ ハンドルを作り直すことがある。
        # ハンドル取得と AutomationElement 作成を成功するまで再試行する。
        $root = $null
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        while ($null -eq $root -and [DateTime]::UtcNow -lt $deadline) {
            $process.Refresh()
            if ($process.HasExited) {
                throw "圧縮オプション ウィンドウが表示される前に終了しました ($Language)。"
            }
            $handle = $process.MainWindowHandle
            if ($handle -ne [IntPtr]::Zero) {
                try {
                    $root = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
                }
                catch {
                    $root = $null
                }
            }
            if ($null -eq $root) {
                Start-Sleep -Milliseconds 100
            }
        }
        if ($null -eq $root) {
            throw "圧縮オプション ウィンドウの自動化要素を取得できませんでした ($Language)。"
        }

        Start-Sleep -Milliseconds 300
        $formatCombo = Wait-Element -Root $root -AutomationId 'CompressionFormatComboBox'
        $passwordBox = Wait-Element -Root $root -AutomationId 'CompressionPasswordBox'
        $noteText = Wait-Element -Root $root -AutomationId 'CompressionPasswordNote'
        $outputName = Wait-Element -Root $root -AutomationId 'CompressionOutputNameBox'

        if (-not $passwordBox.Current.IsEnabled) {
            throw "ZIP 選択時にパスワード欄が無効です ($Language)。"
        }
        if ([string]$noteText.Current.Name -ne $ZipNote) {
            throw "ZIP の注記が一致しません ($Language): '$($noteText.Current.Name)'"
        }
        Write-Host '  OK: ZIP ではパスワード欄が有効で ZIP 用注記を表示'

        $valuePattern = [System.Windows.Automation.ValuePattern]$outputName.GetCurrentPattern(
            [System.Windows.Automation.ValuePattern]::Pattern)
        if (-not $valuePattern.Current.Value.EndsWith('.zip', [StringComparison]::OrdinalIgnoreCase)) {
            throw "ZIP 選択時の出力名が .zip ではありません ($Language): '$($valuePattern.Current.Value)'"
        }

        Select-Format -Root $root -ComboBox $formatCombo -ItemName '7z (.7z)'
        Wait-PasswordState -PasswordBox $passwordBox -NoteText $noteText `
            -ExpectedEnabled $false -ExpectedNote $SevenZipNote -Label '7z のパスワード欄と注記'

        if (-not $valuePattern.Current.Value.EndsWith('.7z', [StringComparison]::OrdinalIgnoreCase)) {
            throw "7z 選択時の出力名が .7z ではありません ($Language): '$($valuePattern.Current.Value)'"
        }

        Select-Format -Root $root -ComboBox $formatCombo -ItemName 'ZIP (.zip)'
        Wait-PasswordState -PasswordBox $passwordBox -NoteText $noteText `
            -ExpectedEnabled $true -ExpectedNote $ZipNote -Label 'ZIP へ戻したときのパスワード欄と注記'
    }
    finally {
        if (-not $process.HasExited) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        }
        Start-Sleep -Milliseconds 300
    }
}

try {
    Get-Process Hodozip -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $settingsDirectory -Force | Out-Null

    Test-Language -Language 'Japanese' `
        -ZipNote $expected.Japanese.ZipNote `
        -SevenZipNote $expected.Japanese.SevenZipNote
    Test-Language -Language 'English' `
        -ZipNote $expected.English.ZipNote `
        -SevenZipNote $expected.English.SevenZipNote

    Write-Host ''
    Write-Host '7z パスワード UI E2E: すべて成功しました。'
}
finally {
    Get-Process Hodozip -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

    if ($hadSettings) {
        Set-Content -LiteralPath $settingsPath -Value $settingsBackup -Encoding utf8 -NoNewline
    }
    elseif (Test-Path -LiteralPath $settingsPath) {
        Remove-Item -LiteralPath $settingsPath -Force
    }
}
