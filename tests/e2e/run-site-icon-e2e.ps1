# Hodozip 公式サイトのファビコン / ヘッダーロゴ E2E。
# Usage: pwsh -File tests/e2e/run-site-icon-e2e.ps1 [-SkipLayout]
#
# 検証内容:
#   SI1: docs/ のファビコン一式が exe 埋め込みアイコン（src/Hodozip/Assets/Hodozip.ico）由来か
#   SI2: 日英両ページの <link rel="icon"> / apple-touch-icon / ヘッダーロゴの HTML が正しいか
#   SI3: ローカル HTTP サーバー経由で日英両ページと全アイコン参照が 200 で読めるか
#   SI4: ヘッダーのレイアウトが差し替え前（git HEAD）と崩れていないか
#        （ヘッドレス Edge で DOM の矩形を比較 + スクリーンショットの差分がロゴ領域内に収まることを確認）
#
# 前提: python（静的サーバー用）と Microsoft Edge（レイアウト比較用）

param(
    [switch]$SkipLayout
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName System.Drawing

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$docsRoot = Join-Path $repoRoot 'docs'
$canonicalIco = Join-Path $repoRoot 'src\Hodozip\Assets\Hodozip.ico'
$failures = 0

function Write-Pass([string]$Message) { Write-Host "  OK: $Message" }
function Write-Info([string]$Message) { Write-Host "  INFO: $Message" }
function Write-Fail([string]$Message) {
    Write-Host "  NG: $Message" -ForegroundColor Red
    $script:failures++
}

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if ($Condition) { Write-Pass $Message } else { Write-Fail $Message }
}

function Get-IcoFrameCount {
    param([string]$IcoPath)
    $bytes = [System.IO.File]::ReadAllBytes($IcoPath)
    if ($bytes.Length -lt 6 -or $bytes[0] -ne 0 -or $bytes[1] -ne 0 -or $bytes[2] -ne 1 -or $bytes[3] -ne 0) {
        throw "ICO ヘッダーが不正です: $IcoPath"
    }
    return [BitConverter]::ToUInt16($bytes, 4)
}

function Get-IcoLargestPng {
    param([string]$IcoPath)

    $bytes = [System.IO.File]::ReadAllBytes($IcoPath)
    $count = [BitConverter]::ToUInt16($bytes, 4)
    $best = $null
    for ($i = 0; $i -lt $count; $i++) {
        $offset = 6 + ($i * 16)
        $width = [int]$bytes[$offset]
        if ($width -eq 0) { $width = 256 }
        $size = [BitConverter]::ToUInt32($bytes, $offset + 8)
        $dataOffset = [BitConverter]::ToUInt32($bytes, $offset + 12)
        $frame = [byte[]]::new($size)
        [Array]::Copy($bytes, $dataOffset, $frame, 0, $size)
        if ($null -eq $best -or $width -gt $best.Width) {
            $best = [pscustomobject]@{ Width = $width; Bytes = $frame }
        }
    }
    if ($null -eq $best -or $best.Bytes[0] -ne 0x89 -or $best.Bytes[1] -ne 0x50) {
        throw "ICO に PNG フレームが含まれていません: $IcoPath"
    }
    Add-Type -AssemblyName System.IO.Compression
    $stream = [System.IO.MemoryStream]::new($best.Bytes)
    $image = [System.Drawing.Image]::FromStream($stream)
    return [pscustomobject]@{ Image = $image; Stream = $stream; Width = $best.Width }
}

function Get-ImageSignature {
    param([System.Drawing.Image]$Image, [int]$Size = 64)

    $bitmap = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.DrawImage($Image, 0, 0, $Size, $Size)
    }
    finally {
        $graphics.Dispose()
    }

    $pixels = [int[]]::new($Size * $Size * 4)
    for ($y = 0; $y -lt $Size; $y++) {
        for ($x = 0; $x -lt $Size; $x++) {
            $index = (($y * $Size) + $x) * 4
            $color = $bitmap.GetPixel($x, $y)
            $pixels[$index] = $color.R
            $pixels[$index + 1] = $color.G
            $pixels[$index + 2] = $color.B
            $pixels[$index + 3] = $color.A
        }
    }
    $bitmap.Dispose()
    return $pixels
}

function Get-SignatureDifference {
    param([int[]]$Left, [int[]]$Right)

    $sum = 0
    $count = 0
    for ($i = 0; $i -lt $Left.Length; $i++) {
        if (($i % 4) -eq 3) { continue } # アルファは端で揺れるため比較しない
        $sum += [Math]::Abs($Left[$i] - $Right[$i])
        $count++
    }
    return $sum / $count
}

function Start-StaticServer {
    param([string]$Root, [int]$Port)

    $python = (Get-Command python -ErrorAction SilentlyContinue)
    if ($null -eq $python) {
        throw 'python が見つかりません（ローカル HTTP サーバー用）。'
    }

    $process = Start-Process -FilePath $python.Source `
        -ArgumentList @('-m', 'http.server', "$Port", '--bind', '127.0.0.1', '--directory', $Root) `
        -PassThru -WindowStyle Hidden

    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while ([DateTime]::UtcNow -lt $deadline) {
        try {
            $response = Invoke-WebRequest -Uri "http://127.0.0.1:$Port/" -UseBasicParsing -TimeoutSec 2
            if ($response.StatusCode -eq 200) {
                return $process
            }
        }
        catch {
            Start-Sleep -Milliseconds 250
        }
    }
    try { $process.Kill($true) } catch { }
    throw "静的サーバーが起動しませんでした: $Root (port $Port)"
}

function Get-HeadlessBrowser {
    param([string]$ProfileRoot)

    $candidates = @(
        (Join-Path $env:ProgramFiles 'Google\Chrome\Application\chrome.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Google\Chrome\Application\chrome.exe'),
        (Join-Path $env:ProgramFiles 'Microsoft\Edge\Application\msedge.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Microsoft\Edge\Application\msedge.exe')
    )
    foreach ($candidate in $candidates) {
        if (-not $candidate -or -not (Test-Path -LiteralPath $candidate)) { continue }
        $profile = Join-Path $ProfileRoot ([Guid]::NewGuid().ToString('N'))
        try {
            $probe = & $candidate '--headless=new' '--disable-gpu' '--no-first-run' '--no-default-browser-check' `
                "--user-data-dir=$profile" '--dump-dom' 'about:blank' 2>$null | Out-String
            if (-not [string]::IsNullOrWhiteSpace($probe)) { return $candidate }
        }
        catch {
            # 次の候補へ
        }
        finally {
            Remove-Item -LiteralPath $profile -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    return $null
}

# DOM の矩形を #__e2e_metrics に書き出すスクリプト（レイアウトには影響しない）
$metricsScript = @'
<script>
window.addEventListener('load', function () {
  var rect = function (el) {
    if (!el) { return null; }
    var b = el.getBoundingClientRect();
    return { x: Math.round(b.x * 100) / 100, y: Math.round(b.y * 100) / 100, w: Math.round(b.width * 100) / 100, h: Math.round(b.height * 100) / 100 };
  };
  var out = {
    header: rect(document.querySelector('.site-header')),
    inner: rect(document.querySelector('.site-header-inner')),
    brand: rect(document.querySelector('.site-brand')),
    icon: rect(document.querySelector('.site-brand-icon')),
    nav: rect(document.querySelector('.site-nav')),
    lang: rect(document.querySelector('.lang-switch')),
    docWidth: document.documentElement.clientWidth
  };
  var el = document.createElement('div');
  el.id = '__e2e_metrics';
  el.textContent = JSON.stringify(out);
  document.body.appendChild(el);
});
</script>
'@

function Get-PageMetrics {
    param([string]$Edge, [string]$Url, [string]$ProfileRoot, [int]$Width, [int]$Height)

    $profile = Join-Path $ProfileRoot ([Guid]::NewGuid().ToString('N'))
    $arguments = @(
        '--headless=new', '--disable-gpu', '--no-first-run', '--no-default-browser-check',
        '--force-device-scale-factor=1',
        "--user-data-dir=$profile", "--window-size=$Width,$Height", '--virtual-time-budget=3000',
        '--dump-dom', $Url
    )
    $dom = & $Edge @arguments 2>$null | Out-String
    Remove-Item -LiteralPath $profile -Recurse -Force -ErrorAction SilentlyContinue

    $match = [regex]::Match($dom, '<div id="__e2e_metrics">(.*?)</div>', 'Singleline')
    if (-not $match.Success) {
        throw "ページからレイアウト情報を取得できませんでした: $Url"
    }
    $json = [System.Net.WebUtility]::HtmlDecode($match.Groups[1].Value)
    return ($json | ConvertFrom-Json)
}

function Save-PageScreenshot {
    param([string]$Edge, [string]$Url, [string]$Path, [string]$ProfileRoot, [int]$Width, [int]$Height)

    $profile = Join-Path $ProfileRoot ([Guid]::NewGuid().ToString('N'))
    $arguments = @(
        '--headless=new', '--disable-gpu', '--no-first-run', '--no-default-browser-check',
        '--force-device-scale-factor=1',
        "--user-data-dir=$profile", "--window-size=$Width,$Height", '--virtual-time-budget=3000',
        "--screenshot=$Path", $Url
    )
    & $Edge @arguments 2>$null | Out-Null
    Remove-Item -LiteralPath $profile -Recurse -Force -ErrorAction SilentlyContinue
    if (-not (Test-Path -LiteralPath $Path)) {
        throw "スクリーンショットを取得できませんでした: $Url"
    }
}

function Get-BitmapBytes {
    param([System.Drawing.Bitmap]$Bitmap)

    $rectangle = [System.Drawing.Rectangle]::new(0, 0, $Bitmap.Width, $Bitmap.Height)
    $data = $Bitmap.LockBits($rectangle, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $bytes = [byte[]]::new($data.Stride * $data.Height)
        [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
        return [pscustomobject]@{ Bytes = $bytes; Stride = $data.Stride; Width = $Bitmap.Width; Height = $Bitmap.Height }
    }
    finally {
        $Bitmap.UnlockBits($data)
    }
}

function Get-PixelDiffBounds {
    param([string]$BeforePath, [string]$AfterPath, [int]$Tolerance = 8)

    $beforeBitmap = [System.Drawing.Bitmap]::FromFile($BeforePath)
    $afterBitmap = [System.Drawing.Bitmap]::FromFile($AfterPath)
    try {
        if ($beforeBitmap.Width -ne $afterBitmap.Width -or $beforeBitmap.Height -ne $afterBitmap.Height) {
            throw "スクリーンショットのサイズが一致しません: $($beforeBitmap.Width)x$($beforeBitmap.Height) vs $($afterBitmap.Width)x$($afterBitmap.Height)"
        }
        $before = Get-BitmapBytes -Bitmap $beforeBitmap
        $after = Get-BitmapBytes -Bitmap $afterBitmap

        $minX = [int]::MaxValue; $minY = [int]::MaxValue; $maxX = -1; $maxY = -1; $changed = 0
        $stride = $before.Stride
        for ($y = 0; $y -lt $before.Height; $y++) {
            $rowStart = $y * $stride
            $rowChanged = $false
            for ($x = 0; $x -lt $before.Width; $x++) {
                $index = $rowStart + ($x * 4)
                if ([Math]::Abs([int]$before.Bytes[$index] - [int]$after.Bytes[$index]) -gt $Tolerance -or
                    [Math]::Abs([int]$before.Bytes[$index + 1] - [int]$after.Bytes[$index + 1]) -gt $Tolerance -or
                    [Math]::Abs([int]$before.Bytes[$index + 2] - [int]$after.Bytes[$index + 2]) -gt $Tolerance) {
                    $changed++
                    $rowChanged = $true
                    if ($x -lt $minX) { $minX = $x }
                    if ($x -gt $maxX) { $maxX = $x }
                }
            }
            if ($rowChanged) {
                if ($y -lt $minY) { $minY = $y }
                if ($y -gt $maxY) { $maxY = $y }
            }
        }
        if ($changed -eq 0) {
            return [pscustomobject]@{ Changed = 0; MinX = 0; MinY = 0; MaxX = -1; MaxY = -1 }
        }
        return [pscustomobject]@{ Changed = $changed; MinX = $minX; MinY = $minY; MaxX = $maxX; MaxY = $maxY }
    }
    finally {
        $beforeBitmap.Dispose()
        $afterBitmap.Dispose()
    }
}

function Resolve-IconPaths {
    param([string]$HtmlPath, [string]$PageUrl)

    $html = Get-Content -LiteralPath $HtmlPath -Raw
    $paths = [System.Collections.Generic.List[string]]::new()

    foreach ($match in [regex]::Matches($html, '<link\b[^>]*>')) {
        $tag = $match.Value
        if ($tag -match 'rel="(icon|apple-touch-icon)"' -and $tag -match 'href="([^"]+)"') {
            $paths.Add($Matches[1])
        }
    }
    if ($html -match '<img class="site-brand-icon"[^>]*src="([^"]+)"') {
        $paths.Add($Matches[1])
    }

    $resolved = foreach ($path in $paths) {
        [Uri]::new([Uri]::new($PageUrl), $path).AbsoluteUri
    }
    return @($resolved)
}

$tempRoot = Join-Path $env:TEMP ('HodozipSiteIconE2E_' + [Guid]::NewGuid().ToString('N'))
$server = $null
$beforeServer = $null
$afterServer = $null

try {
    Write-Host '=== SI1: 正本アイコンと docs/ のファビコン一式 ==='
    Assert-True (Test-Path -LiteralPath $canonicalIco) "正本 ICO が存在する ($canonicalIco)"
    $canonicalFrames = Get-IcoFrameCount -IcoPath $canonicalIco
    Assert-True ($canonicalFrames -ge 8) "正本 ICO のフレーム数が 8 以上 ($canonicalFrames)"

    $faviconPath = Join-Path $docsRoot 'favicon.ico'
    Assert-True (Test-Path -LiteralPath $faviconPath) 'docs/favicon.ico が存在する'
    $canonicalHash = (Get-FileHash -LiteralPath $canonicalIco -Algorithm SHA256).Hash
    $faviconHash = (Get-FileHash -LiteralPath $faviconPath -Algorithm SHA256).Hash
    Assert-True ($canonicalHash -eq $faviconHash) 'docs/favicon.ico が正本 ICO とバイト一致する'
    Assert-True ((Get-IcoFrameCount -IcoPath $faviconPath) -eq $canonicalFrames) 'docs/favicon.ico のフレーム数が正本と一致する'

    $largest = Get-IcoLargestPng -IcoPath $canonicalIco
    try {
        $canonicalSignature = Get-ImageSignature -Image $largest.Image

        $pngAssets = @(
            @{ Path = 'icons\icon-32.png'; Size = 32 },
            @{ Path = 'icons\apple-touch-icon.png'; Size = 180 },
            @{ Path = 'icons\icon-192.png'; Size = 192 },
            @{ Path = 'icons\icon-512.png'; Size = 512 }
        )
        foreach ($asset in $pngAssets) {
            $path = Join-Path $docsRoot $asset.Path
            if (-not (Test-Path -LiteralPath $path)) {
                Write-Fail "docs/$($asset.Path) が存在しません"
                continue
            }
            $image = [System.Drawing.Image]::FromFile($path)
            try {
                Assert-True (($image.Width -eq $asset.Size) -and ($image.Height -eq $asset.Size)) "docs/$($asset.Path) が $($asset.Size)x$($asset.Size)"
                Assert-True ($image.RawFormat.Guid -eq [System.Drawing.Imaging.ImageFormat]::Png.Guid) "docs/$($asset.Path) が PNG"
                $signature = Get-ImageSignature -Image $image
                $difference = Get-SignatureDifference -Left $canonicalSignature -Right $signature
                Assert-True ($difference -lt 12) ("docs/{0} が正本アイコンと同一デザイン (平均差 {1:N2})" -f $asset.Path, $difference)
            }
            finally {
                $image.Dispose()
            }
        }
    }
    finally {
        $largest.Image.Dispose()
        $largest.Stream.Dispose()
    }

    Write-Host '=== SI2: HTML の参照（日英） ==='
    $pages = @(
        @{ Html = 'index.html'; Url = 'http://127.0.0.1:0/'; Label = '日本語' },
        @{ Html = 'en\index.html'; Url = 'http://127.0.0.1:0/en/'; Label = 'English' }
    )
    foreach ($page in $pages) {
        $htmlPath = Join-Path $docsRoot $page.Html
        $html = Get-Content -LiteralPath $htmlPath -Raw
        Assert-True ($html -match '<link rel="icon" href="(\.\./)?favicon\.ico" sizes="any">') "$($page.Label): favicon.ico の link がある"
        Assert-True ($html -match '<link rel="apple-touch-icon" sizes="180x180" href="(\.\./)?icons/apple-touch-icon\.png">') "$($page.Label): apple-touch-icon の link がある"
        Assert-True ($html -match '<img class="site-brand-icon"') "$($page.Label): ヘッダーロゴが img になっている"
        Assert-True ($html -match 'class="site-brand-icon"[^>]*alt="Hodozip"') "$($page.Label): ヘッダーロゴの alt が Hodozip"
        Assert-True ($html -match 'class="site-brand-icon"[^>]*width="30"[^>]*height="30"') "$($page.Label): ヘッダーロゴの表示サイズが 30x30 固定"
        Assert-True ($html -notmatch 'class="site-brand-icon"[^>]*>\s*<svg') "$($page.Label): 旧 SVG ロゴが残っていない"
    }

    $jaHtml = Get-Content -LiteralPath (Join-Path $docsRoot 'index.html') -Raw
    Assert-True ($jaHtml -match '<meta name="google-site-verification" content="ZKp2IVrQhjrzaVAF2PiH8LKatHasBkUKiwv11c35H_Y">') 'google-site-verification の meta が保持されている'

    $untrackedOrModified = & git -C $repoRoot status --porcelain -- docs/sitemap.xml docs/robots.txt 2>$null
    Assert-True ([string]::IsNullOrWhiteSpace(($untrackedOrModified | Out-String))) 'sitemap.xml / robots.txt に変更がない'

    Write-Host '=== SI3: ローカル HTTP サーバー経由の取得 ==='
    $port = 8771
    $server = Start-StaticServer -Root $docsRoot -Port $port

    $httpChecks = @(
        @{ Url = "http://127.0.0.1:$port/"; Label = '日本語ページ（/）'; ContentType = 'text/html' },
        @{ Url = "http://127.0.0.1:$port/en/"; Label = '英語ページ（/en/）'; ContentType = 'text/html' },
        @{ Url = "http://127.0.0.1:$port/favicon.ico"; Label = 'favicon.ico'; ContentType = 'image' },
        @{ Url = "http://127.0.0.1:$port/icons/icon-32.png"; Label = 'icons/icon-32.png'; ContentType = 'image/png' },
        @{ Url = "http://127.0.0.1:$port/icons/apple-touch-icon.png"; Label = 'icons/apple-touch-icon.png'; ContentType = 'image/png' },
        @{ Url = "http://127.0.0.1:$port/icons/icon-192.png"; Label = 'icons/icon-192.png'; ContentType = 'image/png' },
        @{ Url = "http://127.0.0.1:$port/icons/icon-512.png"; Label = 'icons/icon-512.png'; ContentType = 'image/png' }
    )
    foreach ($check in $httpChecks) {
        $response = Invoke-WebRequest -Uri $check.Url -UseBasicParsing -TimeoutSec 10
        $contentType = ($response.Headers['Content-Type'] -join ', ')
        Assert-True ($response.StatusCode -eq 200) "$($check.Label) が 200"
        Assert-True ($contentType -like "*$($check.ContentType)*") "$($check.Label) の Content-Type ($contentType)"
        Assert-True ($response.RawContentLength -gt 0) "$($check.Label) の本文が空でない ($($response.RawContentLength) bytes)"
    }

    # 各ページの実際の参照パスを解決して、全参照が 200 で読めることを確認する
    foreach ($page in $pages) {
        $pageUrl = "http://127.0.0.1:$port/" + ($(if ($page.Html -like 'en\*') { 'en/' } else { '' }))
        $paths = Resolve-IconPaths -HtmlPath (Join-Path $docsRoot $page.Html) -PageUrl $pageUrl
        Assert-True ($paths.Count -ge 6) "$($page.Label): ページ内のアイコン参照数 ($($paths.Count))"
        foreach ($path in $paths) {
            $response = Invoke-WebRequest -Uri $path -UseBasicParsing -TimeoutSec 10
            Assert-True ($response.StatusCode -eq 200) "$($page.Label): $path が 200"
        }
        $brandMatch = [regex]::Match((Get-Content -LiteralPath (Join-Path $docsRoot $page.Html) -Raw), 'class="site-brand-icon"[^>]*src="([^"]+)"')
        $brandUrl = [Uri]::new([Uri]::new($pageUrl), $brandMatch.Groups[1].Value).AbsoluteUri
        $brandResponse = Invoke-WebRequest -Uri $brandUrl -UseBasicParsing -TimeoutSec 10
        Assert-True ($brandResponse.StatusCode -eq 200) "$($page.Label): ヘッダーロゴ $brandUrl が 200"
    }

    Write-Host '=== SI4: ヘッダー レイアウトの非破壊確認（git HEAD との比較） ==='
    if ($SkipLayout) {
        Write-Info '-SkipLayout のためレイアウト比較をスキップ'
    }
    else {
        $edge = Get-HeadlessBrowser -ProfileRoot $tempRoot
        if ($null -eq $edge) {
            Write-Fail 'DOM を出力できるヘッドレス ブラウザー（Chrome / Edge）が見つかりません'
        }
        else {
            Write-Info "使用ブラウザー: $edge"

            # 差し替え前（git HEAD）の docs を一時ディレクトリに再現する
            $beforeRoot = Join-Path $tempRoot 'before'
            New-Item -ItemType Directory -Path (Join-Path $beforeRoot 'en') -Force | Out-Null
            $headFiles = @('index.html', 'style.css', 'sitemap.xml', 'en/index.html')
            foreach ($relative in $headFiles) {
                $content = & git -C $repoRoot show "HEAD:docs/$relative" 2>$null
                if ($LASTEXITCODE -ne 0) {
                    throw "git HEAD から docs/$relative を取得できませんでした"
                }
                $target = Join-Path $beforeRoot $relative
                New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
                $utf8 = [System.Text.UTF8Encoding]::new($false)
                [System.IO.File]::WriteAllText($target, (($content | Out-String).TrimEnd() + "`n"), $utf8)
            }

            $afterRoot = Join-Path $tempRoot 'after'
            Copy-Item -LiteralPath $docsRoot -Destination $afterRoot -Recurse
            $beforeServer = Start-StaticServer -Root $beforeRoot -Port 8772
            $afterServer = Start-StaticServer -Root $afterRoot -Port 8773

            $utf8 = [System.Text.UTF8Encoding]::new($false)
            $metricsBody = $metricsScript -replace '(?s)^\s*<script>', '' -replace '</script>\s*$', ''

            foreach ($relative in @('index.html', 'en/index.html')) {
                foreach ($root in @($beforeRoot, $afterRoot)) {
                    $target = Join-Path $root $relative
                    $html = [System.IO.File]::ReadAllText($target)
                    $injection = "<script>$metricsBody</script>`n</body>"
                    [System.IO.File]::WriteAllText($target, ($html -replace '</body>', $injection), $utf8)
                }
            }

            $viewports = @(
                @{ Width = 1280; Height = 900; Label = 'desktop' },
                # Chrome のウィンドウ最小幅 (500px) に合わせて狭幅を検証する
                @{ Width = 500; Height = 844; Label = 'narrow' }
            )
            $pagePaths = @(
                @{ Path = ''; Label = '日本語' },
                @{ Path = 'en/'; Label = 'English' }
            )

            foreach ($viewport in $viewports) {
                foreach ($pagePath in $pagePaths) {
                    $beforeUrl = "http://127.0.0.1:8772/$($pagePath.Path)"
                    $afterUrl = "http://127.0.0.1:8773/$($pagePath.Path)"
                    $suffix = "$($viewport.Label)-$(if ($pagePath.Path) { 'en' } else { 'ja' })"

                    $before = Get-PageMetrics -Edge $edge -Url $beforeUrl -ProfileRoot $tempRoot -Width $viewport.Width -Height $viewport.Height
                    $after = Get-PageMetrics -Edge $edge -Url $afterUrl -ProfileRoot $tempRoot -Width $viewport.Width -Height $viewport.Height

                    Assert-True (($before.docWidth -eq $after.docWidth)) "$($pagePath.Label)/$($viewport.Label): ドキュメント幅が一致 ($($after.docWidth))"
                    Assert-True (($after.docWidth -gt 300) -and ($after.docWidth -le $viewport.Width)) "$($pagePath.Label)/$($viewport.Label): ドキュメント幅が妥当 ($($after.docWidth) / viewport $($viewport.Width))"
                    foreach ($key in @('header', 'inner', 'brand', 'nav', 'lang')) {
                        $b = $before.$key
                        $a = $after.$key
                        if ($null -eq $b -or $null -eq $a) {
                            Write-Fail "$($pagePath.Label)/$($viewport.Label): $key の矩形を取得できません"
                            continue
                        }
                        $delta = [Math]::Max([Math]::Abs($b.x - $a.x), [Math]::Max([Math]::Abs($b.y - $a.y), [Math]::Max([Math]::Abs($b.w - $a.w), [Math]::Abs($b.h - $a.h))))
                        Assert-True ($delta -le 1) "$($pagePath.Label)/$($viewport.Label): $key の矩形が差し替え前と同じ (最大差 $([Math]::Round($delta, 2))px)"
                    }
                    Assert-True (($after.icon.w -eq 30) -and ($after.icon.h -eq 30)) "$($pagePath.Label)/$($viewport.Label): ロゴの表示サイズが 30x30"

                    $beforeShot = Join-Path $tempRoot "before-$suffix.png"
                    $afterShot = Join-Path $tempRoot "after-$suffix.png"
                    Save-PageScreenshot -Edge $edge -Url $beforeUrl -Path $beforeShot -ProfileRoot $tempRoot -Width $viewport.Width -Height $viewport.Height
                    Save-PageScreenshot -Edge $edge -Url $afterUrl -Path $afterShot -ProfileRoot $tempRoot -Width $viewport.Width -Height $viewport.Height

                    $diff = Get-PixelDiffBounds -BeforePath $beforeShot -AfterPath $afterShot
                    if ($diff.Changed -eq 0) {
                        Write-Fail "$($pagePath.Label)/$($viewport.Label): ロゴが差し替わったのに描画差分がありません"
                        continue
                    }
                    # レイアウトが崩れていなければ、描画差分はヘッダー内のロゴ (30x30) の範囲に収まる。
                    # scrollbar 幅の差でロゴの水平位置が数 px ずれるため、水平方向はブランド位置を基準に広めに許容する。
                    $logoSize = 34
                    $diffWidth = ($diff.MaxX - $diff.MinX) + 1
                    $diffHeight = ($diff.MaxY - $diff.MinY) + 1
                    Assert-True (($diffWidth -le $logoSize) -and ($diffHeight -le $logoSize)) "$($pagePath.Label)/$($viewport.Label): 描画差分がロゴ 1 個分の大きさ ($($diffWidth)x$($diffHeight) / 上限 $($logoSize))"
                    $headerBottom = $after.header.y + $after.header.h
                    $insideHeader = ($diff.MinY -ge ($after.header.y - 4)) -and ($diff.MaxY -le ($headerBottom + 4))
                    Assert-True $insideHeader ("$($pagePath.Label)/$($viewport.Label): 描画差分がヘッダー内 " +
                        "(y {0}-{1} / ヘッダー {2}-{3})" -f $diff.MinY, $diff.MaxY, $after.header.y, $headerBottom)
                    $brandLeft = $after.brand.x
                    $diffCenterX = ($diff.MinX + $diff.MaxX) / 2
                    $brandCenterX = $brandLeft + ($after.icon.w / 2)
                    Assert-True ([Math]::Abs($diffCenterX - $brandCenterX) -le 40) ("$($pagePath.Label)/$($viewport.Label): 描画差分がロゴ位置にある " +
                        "(中心 x {0:N1} / 期待 {1:N1})" -f $diffCenterX, $brandCenterX)

                    Write-Info "スクリーンショット: $beforeShot / $afterShot"
                }
            }
        }
    }

    Write-Host ''
    if ($failures -gt 0) {
        throw "サイト アイコン E2E: $failures 件失敗しました。"
    }
    Write-Host 'サイト アイコン E2E: すべて成功しました。'
}
finally {
    foreach ($process in @($server, $beforeServer, $afterServer)) {
        if ($null -ne $process) {
            try { $process.Kill($true) } catch { }
        }
    }
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
