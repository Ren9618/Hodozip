# Hodozip progress UI E2E.
# Usage: pwsh -File tests/e2e/run-progress-e2e.ps1 [-ExePath <Hodozip.exe>]
#
# Verifies at 500- and 1,000-file scale that:
#   - the window title percent, overall progress bar and numeric percent agree
#   - the percent advances instead of staying at 0%
#   - the UI thread stays responsive (no "not responding", UIA calls stay fast)

param(
    [string]$ExePath
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Set-StrictMode -Version Latest

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if ([string]::IsNullOrWhiteSpace($ExePath)) {
    $ExePath = Join-Path $repoRoot 'src\Hodozip\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\Hodozip.exe'
}
$exePath = [System.IO.Path]::GetFullPath($ExePath)
if (-not (Test-Path -LiteralPath $exePath)) {
    throw "Hodozip.exe が見つかりません: $exePath"
}

$workRoot = Join-Path $env:TEMP 'HodozipProgressE2E'
$settingsDirectory = Join-Path $env:LOCALAPPDATA 'Hodozip'
$settingsPath = Join-Path $settingsDirectory 'settings.json'

$hadSettings = Test-Path -LiteralPath $settingsPath
$settingsBackup = if ($hadSettings) { Get-Content -LiteralPath $settingsPath -Raw } else { $null }

function New-TestArchive {
    param(
        [string]$Path,
        [int]$FileCount,
        [int]$BytesPerFile
    )

    Remove-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Create)
    try {
        $zip = New-Object System.IO.Compression.ZipArchive(
            $stream,
            [System.IO.Compression.ZipArchiveMode]::Create,
            $false)
        try {
            $random = [System.Security.Cryptography.RandomNumberGenerator]::Create()
            $buffer = New-Object byte[] 65536
            for ($index = 0; $index -lt $FileCount; $index++) {
                $entry = $zip.CreateEntry(
                    ('file{0:D5}.bin' -f $index),
                    [System.IO.Compression.CompressionLevel]::NoCompression)
                $entryStream = $entry.Open()
                try {
                    $remaining = $BytesPerFile
                    while ($remaining -gt 0) {
                        $chunk = [Math]::Min($buffer.Length, $remaining)
                        $random.GetBytes($buffer, 0, $chunk)
                        $entryStream.Write($buffer, 0, $chunk)
                        $remaining -= $chunk
                    }
                }
                finally {
                    $entryStream.Dispose()
                }
            }
        }
        finally {
            $zip.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Find-AutomationElement {
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

function Wait-MainWindow {
    param(
        [System.Diagnostics.Process]$Process,
        [int]$TimeoutSeconds = 30
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $Process.Refresh()
        if ($Process.HasExited) {
            throw "Hodozip が進捗ウィンドウを表示する前に終了しました。"
        }
        if ($Process.MainWindowHandle -ne [IntPtr]::Zero) {
            return
        }
        Start-Sleep -Milliseconds 50
    }
    throw "進捗ウィンドウが $TimeoutSeconds 秒以内に表示されませんでした。"
}

function Test-ProgressRun {
    param(
        [string]$Label,
        [string]$ArchivePath,
        [int]$MinimumIntermediateSamples = 2
    )

    Write-Host "=== ${Label}: $([System.IO.Path]::GetFileName($ArchivePath)) ==="
    $process = Start-Process -FilePath $exePath -ArgumentList @($ArchivePath, '--extract-here') -PassThru
    try {
        Wait-MainWindow -Process $process
        $root = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
        $percentElement = Find-AutomationElement -Root $root -AutomationId 'ProgressPercentText'
        $barElement = Find-AutomationElement -Root $root -AutomationId 'ProgressOverallBar'
        if ($null -eq $percentElement -or $null -eq $barElement) {
            throw "進捗 UI の自動化要素が見つかりません。"
        }

        $samples = New-Object System.Collections.Generic.List[object]
        $intermediate = 0
        $lastPercent = -1
        $lastTick = [DateTime]::UtcNow
        $maxSampleGapMs = 0.0
        $maxAutomationMs = 0.0
        $notResponding = 0
        $deadline = [DateTime]::UtcNow.AddSeconds(180)
        $completed = $false

        while ([DateTime]::UtcNow -lt $deadline) {
            $process.Refresh()
            if ($process.HasExited) {
                break
            }

            if (-not $process.Responding) {
                $notResponding++
            }

            # UIA の 3 つの読み取りは原子的ではない。10 Hz 更新の途中に
            # 挟まると 1 ティック分だけずれて見えることがあるため、
            # 一致するスナップショットが取れるまで短時間だけ再取得する。
            # 実際に表示がずれ続けている場合はここで失敗する。
            $attempt = 0
            $consistent = $false
            $mismatchDetail = $null
            while (-not $consistent) {
                $attempt++
                $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
                $title = [string]$root.Current.Name
                $percentText = [string]$percentElement.Current.Name
                $barPercent = $null
                try {
                    $rangePattern = [System.Windows.Automation.RangeValuePattern]$barElement.GetCurrentPattern(
                        [System.Windows.Automation.RangeValuePattern]::Pattern)
                    if ($rangePattern.Current.Maximum -gt 0) {
                        $barPercent = [Math]::Round(
                            $rangePattern.Current.Value * 100.0 / $rangePattern.Current.Maximum, 0)
                    }
                }
                catch {
                    # バーのパターンが一時的に取れない場合は次の試行で再取得する。
                }
                $stopwatch.Stop()
                $automationMs = $stopwatch.Elapsed.TotalMilliseconds
                if ($automationMs -gt $maxAutomationMs) {
                    $maxAutomationMs = $automationMs
                }

                if ($percentText -match '^(\d+)%$') {
                    $percent = [int]$Matches[1]
                    $titlePercent = $null
                    if ($title -match '^(\d+)% / ') {
                        $titlePercent = [int]$Matches[1]
                    }

                    if ($null -eq $titlePercent) {
                        $mismatchDetail = "タイトルに進捗率がありません: '$title'"
                    }
                    elseif ($titlePercent -ne $percent) {
                        $mismatchDetail = "タイトル ($titlePercent%) と数値表示 ($percent%) が一致しません: '$title'"
                    }
                    elseif ($null -ne $barPercent -and [Math]::Abs($barPercent - $percent) -gt 0) {
                        $mismatchDetail = "進捗バー ($barPercent%) と数値表示 ($percent%) が一致しません。"
                    }
                    else {
                        $consistent = $true
                    }
                }
                elseif ($percentText -eq '…') {
                    $consistent = $true
                }
                else {
                    $mismatchDetail = "数値表示が想定外です: '$percentText'"
                }

                if (-not $consistent) {
                    if ($attempt -ge 20) {
                        throw "$mismatchDetail（$attempt 回再取得しても一致しませんでした）"
                    }
                    Start-Sleep -Milliseconds 10
                }
            }

            $now = [DateTime]::UtcNow
            $gapMs = ($now - $lastTick).TotalMilliseconds
            if ($gapMs -gt $maxSampleGapMs) {
                $maxSampleGapMs = $gapMs
            }
            $lastTick = $now

            if ($percentText -match '^(\d+)%$') {
                $percent = [int]$Matches[1]

                if ($percent -gt 0 -and $percent -lt 100 -and $percent -ne $lastPercent) {
                    $intermediate++
                }
                $lastPercent = $percent

                $samples.Add([pscustomobject]@{
                    Percent = $percent
                    BarPercent = $barPercent
                    Title = $title
                    AutomationMs = $automationMs
                })

                if ($percent -ge 100) {
                    $completed = $true
                    break
                }
            }

            Start-Sleep -Milliseconds 50
        }

        $summary = $samples | ForEach-Object { "$($_.Percent)%" }
        Write-Host "  サンプル: $($summary -join ', ')"
        Write-Host ("  中間サンプル数: {0} / 最大 UIA 応答: {1:0} ms / 最大サンプル間隔: {2:0} ms" -f `
            $intermediate, $maxAutomationMs, $maxSampleGapMs)

        if ($notResponding -gt 0) {
            throw "進捗中にウィンドウが応答なしと判定されました ($notResponding 回)。"
        }
        if ($maxAutomationMs -gt 2000) {
            throw "UI オートメーション応答が 2 秒を超えました: $maxAutomationMs ms"
        }
        if ($maxSampleGapMs -gt 2000) {
            throw "進捗サンプル間隔が 2 秒を超えました: $maxSampleGapMs ms"
        }
        if (-not $completed) {
            throw "進捗が 100% に到達しませんでした。最後の表示: '$($percentText)'"
        }
        if ($intermediate -lt $MinimumIntermediateSamples) {
            throw "進捗率が 0% のまま停滞した可能性があります（中間サンプル $intermediate 件）。"
        }

        Write-Host "  OK: タイトル・バー・数値表示が一致し、応答を維持したまま 100% に到達"
    }
    finally {
        if (-not $process.HasExited) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        }
    }
}

try {
    Get-Process Hodozip -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $workRoot -Force | Out-Null

    New-Item -ItemType Directory -Path $settingsDirectory -Force | Out-Null
    @{
        DestinationRule = 'SameAsArchive'
        OpenFolderAfterExtraction = $false
        CloseAfterAutoExtract = $false
        FolderCollisionPolicy = 'Merge'
        OverwritePolicy = 'Overwrite'
        Language = 'Japanese'
        CheckForUpdatesOnStartup = $false
    } | ConvertTo-Json | Set-Content -LiteralPath $settingsPath -Encoding utf8

    $case500 = Join-Path $workRoot 'case500'
    New-Item -ItemType Directory -Path $case500 -Force | Out-Null
    $archive500 = Join-Path $case500 'progress-500.zip'
    Write-Host '500 ファイルのアーカイブを生成中...'
    New-TestArchive -Path $archive500 -FileCount 500 -BytesPerFile 262144
    Test-ProgressRun -Label 'P1 (500 ファイル)' -ArchivePath $archive500

    $case1000 = Join-Path $workRoot 'case1000'
    New-Item -ItemType Directory -Path $case1000 -Force | Out-Null
    $archive1000 = Join-Path $case1000 'progress-1000.zip'
    Write-Host '1,000 ファイルのアーカイブを生成中...'
    New-TestArchive -Path $archive1000 -FileCount 1000 -BytesPerFile 131072
    Test-ProgressRun -Label 'P2 (1,000 ファイル)' -ArchivePath $archive1000

    $archive500Count = @(Get-ChildItem -LiteralPath (Join-Path $case500 'progress-500') -Filter 'file*.bin').Count
    $archive1000Count = @(Get-ChildItem -LiteralPath (Join-Path $case1000 'progress-1000') -Filter 'file*.bin').Count
    if ($archive500Count -ne 500 -or $archive1000Count -ne 1000) {
        throw "展開されたファイル数が想定と異なります: $archive500Count / $archive1000Count"
    }

    Write-Host ''
    Write-Host '進捗 UI E2E: すべて成功しました。'
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
