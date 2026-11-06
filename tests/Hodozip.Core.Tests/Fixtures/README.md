# テスト用フィクスチャ

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
