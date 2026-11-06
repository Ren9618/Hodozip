# Hodozip MSI test fixture generator.
#
# Usage:
#   pwsh -File tests/Hodozip.Core.Tests/Fixtures/formats/generate-msi-fixture.ps1
#
# Requirements:
#   - WiX Toolset v4/v5 CLI (wix.exe). Install with:
#       dotnet tool install --global wix
#
# The generated fixtures are committed to the repository. sample.msi embeds its
# cabinet, while msi-external/sample-external.msi references cab1.cab in the same
# directory. The unit tests open them with msi.dll and extract the cabinets.

param(
    [string]$Wix = (Join-Path $env:USERPROFILE '.dotnet\tools\wix.exe'),
    [string]$OutputPath = (Join-Path $PSScriptRoot 'sample.msi')
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

if (-not (Test-Path -LiteralPath $Wix)) {
    throw "WiX CLI が見つかりません: $Wix"
}

$externalDirectory = Join-Path $PSScriptRoot 'msi-external'
$externalOutputPath = Join-Path $externalDirectory 'sample-external.msi'
$workRoot = Join-Path $env:TEMP ('HodozipMsiFixture_' + [Guid]::NewGuid().ToString('N'))
$source = Join-Path $workRoot 'src'

try {
    New-Item -ItemType Directory -Path $source, (Join-Path $source 'Top\sub') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $source 'hello.txt') -Value 'hello world' -Encoding utf8 -NoNewline
    Set-Content -LiteralPath (Join-Path $source 'Top\a.txt') -Value 'A content' -Encoding utf8 -NoNewline
    Set-Content -LiteralPath (Join-Path $source 'Top\sub\b.txt') -Value 'B content' -Encoding utf8 -NoNewline
    Set-Content -LiteralPath (Join-Path $source '日本語テスト.txt') -Value 'こんにちは' -Encoding utf8 -NoNewline

    $wxs = @'
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">
  <Package Name="Hodozip MSI Fixture" Manufacturer="Hodozip" Version="1.0.0.0"
           UpgradeCode="7d5c9b2e-4f5a-4b6c-8e9d-1a2b3c4d5e6f" Scope="perUser">
    <MediaTemplate EmbedCab="yes" />
    <StandardDirectory Id="ProgramFiles64Folder">
      <Directory Id="INSTALLDIR" Name="Sample">
        <Component Id="HelloComponent" Guid="11111111-2222-3333-4444-555555555555">
          <File Id="hello" Source="src\hello.txt" />
        </Component>
        <Component Id="JapaneseComponent" Guid="11111111-2222-3333-4444-555555555557">
          <File Id="japanese" Source="src\日本語テスト.txt" />
        </Component>
        <Directory Id="TOPDIR" Name="Top">
          <Component Id="TopComponent" Guid="11111111-2222-3333-4444-555555555556">
            <File Id="a" Source="src\Top\a.txt" />
          </Component>
          <Directory Id="SUBDIR" Name="sub">
            <Component Id="SubComponent" Guid="11111111-2222-3333-4444-555555555559">
              <File Id="b" Source="src\Top\sub\b.txt" />
            </Component>
          </Directory>
        </Directory>
      </Directory>
    </StandardDirectory>
    <Feature Id="Main">
      <ComponentRef Id="HelloComponent" />
      <ComponentRef Id="JapaneseComponent" />
      <ComponentRef Id="TopComponent" />
      <ComponentRef Id="SubComponent" />
    </Feature>
  </Package>
</Wix>
'@

    $wxsPath = Join-Path $workRoot 'fixture.wxs'
    Set-Content -LiteralPath $wxsPath -Value $wxs -Encoding utf8

    Push-Location $workRoot
    try {
        & $Wix build $wxsPath -arch x64 -o $OutputPath
        if ($LASTEXITCODE -ne 0) {
            throw "wix build が失敗しました (exit $LASTEXITCODE)"
        }
    }
    finally {
        Pop-Location
    }

    # 外部キャビネット版: MediaTemplate の EmbedCab を no にして cab1.cab を別出力する。
    New-Item -ItemType Directory -Path $externalDirectory -Force | Out-Null
    $externalWxs = $wxs.
        Replace('Name="Hodozip MSI Fixture"', 'Name="Hodozip MSI External Fixture"').
        Replace('7d5c9b2e-4f5a-4b6c-8e9d-1a2b3c4d5e6f', '9e7dbd4a-6b8c-4d0e-af12-3c4d5e6f7081').
        Replace('EmbedCab="yes"', 'EmbedCab="no"').
        Replace('Name="Sample"', 'Name="SampleExternal"')
    $externalWxsPath = Join-Path $workRoot 'fixture-external.wxs'
    Set-Content -LiteralPath $externalWxsPath -Value $externalWxs -Encoding utf8

    Push-Location $workRoot
    try {
        & $Wix build $externalWxsPath -arch x64 -o $externalOutputPath
        if ($LASTEXITCODE -ne 0) {
            throw "wix build (外部キャビネット) が失敗しました (exit $LASTEXITCODE)"
        }
    }
    finally {
        Pop-Location
    }

    # 外部キャビネットは MSI と同じディレクトリに cab1.cab として配置する必要がある。
    if (-not (Test-Path -LiteralPath (Join-Path $externalDirectory 'cab1.cab'))) {
        $builtCabinet = Get-ChildItem -LiteralPath $workRoot -Filter '*.cab' -File |
            Select-Object -First 1
        if ($null -ne $builtCabinet) {
            Move-Item -LiteralPath $builtCabinet.FullName `
                -Destination (Join-Path $externalDirectory 'cab1.cab') -Force
        }
    }

    if (-not (Test-Path -LiteralPath (Join-Path $externalDirectory 'cab1.cab'))) {
        throw '外部キャビネット cab1.cab が生成されませんでした。'
    }

    # wix.exe はデバッグ用の .wixpdb を出力するため、リポジトリには残さない。
    Remove-Item -LiteralPath ([IO.Path]::ChangeExtension($OutputPath, '.wixpdb')) `
        -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath ([IO.Path]::ChangeExtension($externalOutputPath, '.wixpdb')) `
        -Force -ErrorAction SilentlyContinue

    Get-Item -LiteralPath $OutputPath, $externalOutputPath, (Join-Path $externalDirectory 'cab1.cab') |
        Select-Object Name, Length
}
finally {
    Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue
}
