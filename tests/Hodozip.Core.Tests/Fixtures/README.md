# テスト用フィクスチャ

## パスワード付き ZIP

パスワード付き ZIP のテストで使うフィクスチャです。パスワードはいずれも `secret`、内容は `secret.txt`（`Hodozip password test`）です。

| ファイル | 暗号化方式 |
| --- | --- |
| `password-zipcrypto.zip` | ZipCrypto |
| `password-aes.zip` | AES-256 |

7-Zip を使って以下のように生成しています。

```powershell
$payload = Join-Path $env:TEMP 'hodozip-fixture'
New-Item -ItemType Directory -Path $payload -Force | Out-Null
Set-Content -LiteralPath (Join-Path $payload 'secret.txt') -Value 'Hodozip password test' -Encoding utf8

& 'C:\Program Files\7-Zip\7z.exe' a -tzip -psecret -mem=ZipCrypto `
  (Join-Path $PSScriptRoot 'password-zipcrypto.zip') (Join-Path $payload 'secret.txt')
& 'C:\Program Files\7-Zip\7z.exe' a -tzip -psecret -mem=AES256 `
  (Join-Path $PSScriptRoot 'password-aes.zip') (Join-Path $payload 'secret.txt')
```

SharpZipLib で生成した AES ZIP は SharpCompress で読めなかったため、7-Zip の AES-256 を使用しています。

## 多形式

`formats/` には Phase 3 で対応を確定した形式のフィクスチャがあります。内容は `hello.txt`、`Top/a.txt`、`Top/sub/b.txt`、`日本語テスト.txt` です（RAR は SharpCompress の RAR5 テストアーカイブ、MSI は `Program Files\Sample` 配下へ展開されます）。

| ファイル | 形式 | 備考 |
| --- | --- | --- |
| `sample.7z` | 7z | |
| `password.7z` | 7z | パスワードは `secret` |
| `sample.rar` | RAR | RAR5 |
| `sample.msi` | MSI | Windows Installer（`Program Files\Sample` へ展開される埋め込みキャビネット） |
| `msi-external/sample-external.msi` / `cab1.cab` | MSI | 外部キャビネット版（`Program Files\SampleExternal` へ展開。2 ファイルを同じディレクトリに置いて使用） |
| `sample.tar` | TAR | |
| `sample.tar.gz` / `sample.tgz` | TAR.GZ | 同じ内容の別名 |
| `sample.tar.bz2` / `sample.tbz2` | TAR.BZ2 | 同じ内容の別名 |
| `sample.tar.xz` / `sample.txz` | TAR.XZ | 同じ内容の別名 |
| `sample.tar.zst` / `sample.tzst` | TAR.ZST | 同じ内容の別名 |
| `sample.gz` | GZ | `hello.txt` を単一ストリーム圧縮 |
| `sample.bz2` | BZ2 | `hello.txt` を単一ストリーム圧縮 |
| `sample.xz` | XZ | `hello.txt` を単一ストリーム圧縮 |
| `sample.zst` | ZST | `hello.txt` を単一ストリーム圧縮 |

`formats/generate-fixtures.ps1` で 7-Zip 系フィクスチャを再生成できます。7-Zip 互換 CLI（`7z.exe`）が必要で、RAR フィクスチャの取得にインターネット接続を使用します。`formats/generate-msi-fixture.ps1` は WiX Toolset の `wix.exe` を使って `sample.msi` と `msi-external/sample-external.msi`、`msi-external/cab1.cab` を再生成します。
