# Hodozip

**ZIP を、ほどく。**

Hodozip（ホドジップ）は、往年の解凍ソフトの使い勝手を現代の実装で引き継ぐ、Windows 向けの無料・オープンソース ZIP 解凍ソフトです。ZIP をドラッグ＆ドロップするだけで、解凍先フォルダを自動判定して展開します。

- 製品名: Hodozip（由来: ほどく + ZIP）
- バージョン: 0.2.0
- ライセンス: MIT
- 対応 OS: Windows 11（主対象）/ Windows 10 バージョン 19041 以降
- 実装: C# / .NET 8 / WinUI 3（Windows App SDK）
- 配布形態: インストール不要の unpackaged zip 版と、ファイル関連付け・右クリックメニューに対応する MSIX 版

## ダウンロード

[GitHub Releases](https://github.com/Ren9618/Hodozip/releases) から最新版をダウンロードできます。

- 最新版: [Hodozip v0.2.0](https://github.com/Ren9618/Hodozip/releases/tag/v0.2.0)
- `Hodozip-0.2.0-win-x64.zip`（unpackaged 版）: 解凍して `Hodozip.exe` を起動するだけで使えます。インストールは不要です。
- `Hodozip-0.2.0.msix`（MSIX 版・実験的）: `.zip` の関連付け候補と Windows 11 の右クリックメニューが登録されます。自己署名証明書を使うため、インストール前に証明書の登録が必要です。手順は [MSIX 版のインストール](#msix-版のインストール上級者向け実験的) を参照してください。

unpackaged 版の注意: 本バージョンは未署名のため、Windows SmartScreen の警告が表示される場合があります。「詳細情報」→「実行」で起動できます。

## Phase 2 の新機能

- `.zip` のファイル関連付け候補として Hodozip を登録（Windows の「プログラムから開く」に表示）
- Windows 11 の右クリックメニューに「Hodozip で解凍」「Hodozip でフォルダに解凍」を追加
- 解凍モード `Auto` / `ForceArchiveFolder` と、対応する CLI 引数 `--extract-here` / `--extract-folder` を追加
- アプリ内バージョン表記を 0.2.0 に統一（MSIX パッケージは 0.2.0.0）

## 特徴

### ダブルクリックですぐ解凍

`Hodozip.exe` に ZIP ファイルのパスを渡して起動すると、その場で解凍を開始します。MSIX 版をインストールし、`.zip` の既定のアプリに Hodozip を設定すると、ZIP のダブルクリックで解凍できます。

引数なしで起動した場合は、ZIP をドロップできるメインウィンドウを表示します。

### 解凍先フォルダの自動判定

`Auto` モードでは、ZIP の内容に応じて以下のルールで解凍先を決めます。

| ZIP の内容 | 解凍結果 |
| --- | --- |
| ファイルが 1 つだけ（ZIP 直下） | フォルダを作らず、アーカイブと同じ場所へそのまま出力 |
| すべてのエントリが単一のトップフォルダ配下 | そのトップフォルダごと出力（二重フォルダにしない） |
| 上記以外（複数ファイルがバラバラ） | アーカイブ名（拡張子なし）のフォルダを作成し、その中へ出力 |

`ForceArchiveFolder` モードでは、ZIP の内容にかかわらず、必ずアーカイブ名（拡張子なし）のフォルダを作成してその中へ出力します。

作成先フォルダが既に存在する場合は上書きせず、`名前 (2)`、`名前 (3)` のように自動で番号を付けます。

### Windows 11 の右クリックメニュー

MSIX 版をインストールすると、`.zip` ファイルの右クリックメニューに以下の 2 項目が追加されます。

- **Hodozip で解凍**: `Auto` モードで解凍します。単一フォルダ型の ZIP は二重フォルダになりません。
- **Hodozip でフォルダに解凍**: `ForceArchiveFolder` モードで、必ずアーカイブ名のフォルダを作って解凍します。

Windows 11 の新しい右クリックメニューに項目が表示されない場合は、「その他のオプションを表示」を選ぶか、`Shift` + `F10` で従来のメニューを開いてください。

### 日本語ファイル名の文字化け対策

ZIP エントリの UTF-8 フラグを尊重します。

- UTF-8 フラグあり: UTF-8 としてデコード
- UTF-8 フラグなし: CP932（Shift_JIS）としてデコード

CP932 のデコードには .NET の `CodePagesEncodingProvider` を登録し、SharpCompress の `ArchiveEncoding.Default` に CP932 を指定しています。実装は `src/Hodozip.Core/EncodingSetup.cs` と `src/Hodozip.Core/SharpCompressExtractor.cs` の `CreateReaderOptions` を参照してください。

### パスワード付き ZIP

ZipCrypto / AES のパスワード付き ZIP に対応しています。暗号化エントリを検出すると UI がパスワード入力ダイアログを表示し、入力後に解凍を再開します。パスワード違いはリトライでき、最大 5 回まで入力できます。

### 上書きポリシー

Core の `OverwritePolicy` として以下を扱います。

- `Ask`: 新旧のサイズ・更新日時を並べて確認
- `Overwrite`: 常に上書き
- `Skip`: 既存ファイルを残してスキップ
- `AutoRename`: 既存ファイルを残し、`名前 (2)` のように採番

`Ask` では「以降すべてに適用」を選べます。

### 進捗表示とキャンセル

処理済みバイト数 / 総バイト数と現在のファイル名を表示します。キャンセル時は書き込み中の一時ファイルを削除し、中途半端なファイルを残しません。

### セキュリティ

- Zip Slip: `../` や絶対パス、ドライブレターを含むエントリを拒否
- シンボリックリンク: リンクとしても通常ファイルとしても展開せず、警告を出してスキップ
- ロングパス: 260 文字を超えるパスに `\\?\` プレフィックスで対応
- Windows 予約デバイス名（CON、PRN、AUX、NUL、COM1 など）をサニタイズ
- 展開爆弾対策: 展開後サイズが 1 GiB 以上、かつ圧縮サイズの 1000 倍以上の場合に警告

### ネットワーク通信なし

テレメトリはありません。アプリ本体はネットワーク通信を行いません。唯一の例外は、バージョン情報画面・設定画面の「開発を支援する」ボタンで、既定のブラウザを開いて寄付ページを表示する操作です。起動時や解凍完了時に寄付を促すポップアップは表示しません。

## 使い方

### アプリで解凍する

1. `Hodozip.exe` を起動します。
2. ZIP ファイルをウィンドウへドラッグ＆ドロップするか、「ファイルを選択」から ZIP を選びます。
3. 解凍の進捗が表示されます。完了後、「フォルダを開く」で解凍先を確認できます。

### コマンドラインから起動する

```powershell
# Auto モード（Phase 1 と同じ。パスだけ渡す）
.\Hodozip.exe "C:\path\to\archive.zip"

# Auto モードを明示
.\Hodozip.exe --extract-here "C:\path\to\archive.zip"

# 必ずアーカイブ名のフォルダを作る
.\Hodozip.exe --extract-folder "C:\path\to\archive.zip"
```

| 引数 | 解凍モード | 動作 |
| --- | --- | --- |
| `<archive>` | `Auto` | ZIP の内容に応じて自動判定 |
| `--extract-here <archive>` | `Auto` | 同上（右クリックメニュー用の明示形） |
| `--extract-folder <archive>` | `ForceArchiveFolder` | 必ずアーカイブ名フォルダを作成 |

アーカイブと同じフォルダへ、指定したモードに従って解凍します。「設定」で「ダブルクリック起動時の完了後、自動で閉じる」を有効にすると、コマンドライン起動時は完了後に自動で終了します（エラー時は閉じません）。

## MSIX 版のインストール（上級者向け・実験的）

MSIX 版は実験的配布です。署名にはローカルテスト用の自己署名証明書（`CN=Ren9618`）を使っており、Microsoft Store の審査を通った証明書ではありません。以下の操作は自己責任で行ってください。

### 1. 証明書をインストールする

リポジトリの [`packaging/certs/Hodozip-Test.cer`](https://github.com/Ren9618/Hodozip/raw/v0.2.0/packaging/certs/Hodozip-Test.cer) をダウンロードします。これは公開証明書のみで、秘密鍵は含まれていません。

管理者権限の PowerShell で、ダウンロードした CER を `LocalMachine\TrustedPeople`（信頼されたユーザー）に登録します。

```powershell
certutil -addstore -f TrustedPeople .\Hodozip-Test.cer
```

証明書の拇印は `09F6216808D67CCAF8686609F375EC7BFF837284` です。`Get-PfxCertificate` などで確認できます。

> 注: `CurrentUser\TrustedPeople` への登録だけでは `Add-AppxPackage` が `0x800B0109` で失敗する場合があります。その場合は上記のとおり `LocalMachine\TrustedPeople` へ登録してください。

### 2. MSIX をインストールする

`Hodozip-0.2.0.msix` をダブルクリックするか、PowerShell で実行します。

```powershell
Add-AppxPackage -Path .\Hodozip-0.2.0.msix
```

インストール後、スタートメニューに「Hodozip」が表示されます。パッケージ ID は `Ren9618.Hodozip_0.2.0.0_x64__ex19z75j0wj0g`、アプリケーション ID は `Ren9618.Hodozip_ex19z75j0wj0g!Hodozip` です。

### 3. `.zip` の既定のアプリに設定する

Windows はファイル関連付けをアプリ側から強制できません。MSIX 版をインストールすると Hodozip は `.zip` の「プログラムから開く」候補に登録されますが、既定にするにはユーザー操作が必要です。

1. Windows の「設定」→「アプリ」→「既定のアプリ」を開きます。
2. `.zip` を検索するか、一覧から `.zip` を選びます。
3. 「Hodozip」を選択します。

または、`.zip` を右クリック →「プログラムから開く」→「別のプログラムを選択」→「Hodozip」→「常にこのアプリを使って .zip ファイルを開く」を選びます。

### 4. 右クリックメニューを使う

`.zip` ファイルを右クリックし、「Hodozip で解凍」または「Hodozip でフォルダに解凍」を選びます。Windows 11 の新しいメニューに表示されない場合は、「その他のオプションを表示」を選んでください。

インストール直後や更新後は、エクスプローラーを再起動するとメニューが反映されやすいです。

### 5. アンインストールする

「設定」→「アプリ」→「インストールされているアプリ」から Hodozip をアンインストールします。PowerShell の場合は以下を実行します。

```powershell
Remove-AppxPackage Ren9618.Hodozip_0.2.0.0_x64__ex19z75j0wj0g
```

証明書も不要になった場合は、管理者権限の PowerShell で以下を実行します。

```powershell
certutil -delstore TrustedPeople 09F6216808D67CCAF8686609F375EC7BFF837284
```

## ビルド

### 必要な環境

- Windows 10 バージョン 19041 以降 / Windows 11
- .NET SDK 8.0 以降（`net8.0` のビルドが可能なこと）
- Visual Studio 2022 または Build Tools（WinUI 3 / Windows App SDK のビルド環境）
- MSIX を作る場合は Windows 10/11 SDK（`makeappx.exe` / `signtool.exe`）と C++ ビルドツール
- x64

### アプリのビルド

```powershell
dotnet build Hodozip.sln -c Release -p:Platform=x64
```

ビルド成果物:

```text
src\Hodozip\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\Hodozip.exe
```

Windows App SDK は self-contained で出力されるため、unpackaged 版の実行には MSIX インストールは不要です。

### unpackaged 版の publish と zip 作成

```powershell
dotnet publish src\Hodozip\Hodozip.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained true -o publish\Hodozip
Compress-Archive -Path publish\Hodozip -DestinationPath Hodozip-0.2.0-win-x64.zip
```

zip のトップレベルには `Hodozip` フォルダが 1 つだけ入ります。

### MSIX のビルド

```powershell
pwsh -File packaging\build-msix.ps1
```

スクリプトは以下を行います。

1. `dotnet publish` で unpackaged の publish 出力を作成
2. `src\Hodozip.ContextMenu` の COM DLL を x64 Release でビルド
3. publish 出力と COM DLL、生成したアセット画像を `artifacts\msix\staging` にステージング
4. `packaging\Package.appxmanifest.template` から `AppxManifest.xml` を生成
5. `makeappx.exe` で `artifacts\msix\Hodozip-0.2.0.msix` を作成
6. 自己署名証明書で `signtool.exe` 署名

証明書は `artifacts\msix\certs\Hodozip-Test.pfx` に作成されます。PFX は秘密鍵を含むため、リポジトリや Release には含めません。

テスト証明書を `LocalMachine\TrustedPeople` に自動登録する場合は、管理者権限で以下を実行します。

```powershell
pwsh -File packaging\build-msix.ps1 -InstallTrustedCertificate
```

証明書を作り直す場合は `-RecreateCertificate` を指定します。Publisher（`CN=Ren9618`）と証明書の Subject が一致している必要があります。

### バージョン管理

バージョンは `Directory.Build.props` の `HodozipVersion` で管理します。アプリの `AssemblyVersion` / `FileVersion` と MSIX の `PackageVersion`（`0.2.0` → `0.2.0.0`）はこの値から生成されます。

## テスト

### Core 単体テスト

```powershell
dotnet test tests\Hodozip.Core.Tests\Hodozip.Core.Tests.csproj -c Release
```

26 件のテストで以下を検証します。

- 解凍先自動判定（単一ファイル / 単一フォルダ / バラバラ）
- `ForceArchiveFolder` の強制フォルダ作成（単一ファイル / 単一フォルダ / バラバラ）
- Shift_JIS ファイル名と UTF-8 ファイル名
- Zip Slip の拒否
- パスワード付き ZIP（ZipCrypto / AES）の正常解凍とリトライ
- 上書きポリシー（Overwrite / Skip / AutoRename / Ask）
- キャンセル時に一時ファイルが残らないこと
- 予約デバイス名、シンボリックリンク、展開爆弾警告、ロングパス

パスワード付きテストフィクスチャ `tests/Hodozip.Core.Tests/Fixtures/password-*.zip` のパスワードは `secret` です。フィクスチャは 7-Zip で生成しています。

### アプリ E2E

Release ビルド後、または publish 後に以下を実行します。

```powershell
# ビルド出力を使う
pwsh -File tests\e2e\run-e2e.ps1

# publish 出力を使う
pwsh -File tests\e2e\run-e2e.ps1 -ExePath publish\Hodozip\Hodozip.exe
```

E2E では一時フォルダに ZIP を作成し、以下の 5 パターンを検証します。

1. 単一フォルダ型をパス指定（Auto）で解凍し、二重フォルダにならないこと
2. バラバラ型をパス指定（Auto）で解凍し、アーカイブ名フォルダが作られること
3. `--extract-here` で単一フォルダ型を解凍し、二重フォルダにならないこと
4. `--extract-folder` で単一フォルダ型を解凍し、必ずアーカイブ名フォルダが作られること
5. 引数なし起動でメインウィンドウが表示されること

テスト中は設定ファイルを一時的に変更し、終了時に復元します。

### MSIX のインストール検証

MSIX の検証は、ビルドしたパッケージを実際にインストールして行います。

```powershell
# 証明書を LocalMachine\TrustedPeople に登録（管理者権限）
certutil -addstore -f TrustedPeople artifacts\msix\certs\Hodozip-Test.cer

# インストール
Add-AppxPackage -Path artifacts\msix\Hodozip-0.2.0.msix

# スタートメニューと関連付け候補の確認
Get-StartApps | Where-Object { $_.Name -eq 'Hodozip' }
Get-ItemProperty 'HKCU:\Software\Classes\.zip\OpenWithProgids'

# アンインストール
Remove-AppxPackage Ren9618.Hodozip_0.2.0.0_x64__ex19z75j0wj0g
```

インストール後の自動 E2E も用意しています。設定ファイルを共有するため、2 つのスクリプトは同時に実行しないでください。

```powershell
# パッケージ版の起動と --extract-here / --extract-folder の動作
pwsh -File tests\e2e\run-packaged-e2e.ps1

# .zip の関連付け候補、COM 登録、右クリックメニューのタイトルと verb 起動
pwsh -File tests\e2e\run-contextmenu-e2e.ps1
```

右クリックメニューの COM 登録は `HKLM\SOFTWARE\Classes\PackagedCom\ClassIndex` 配下の CLSID で確認できます。

- `4F4C6D2A-71B9-4E4E-9A7D-8D9A0B5E6C01`（Hodozip で解凍）
- `4F4C6D2A-71B9-4E4E-9A7D-8D9A0B5E6C02`（Hodozip でフォルダに解凍）

## プロジェクト構成

```text
Hodozip.sln
Directory.Build.props      バージョンの一元管理
src/
  Hodozip.Core/            解凍ロジック（UI 非依存）
  Hodozip/                 WinUI 3 アプリ
  Hodozip.ContextMenu/     Windows 11 右クリックメニュー用 COM DLL（IExplorerCommand）
packaging/
  build-msix.ps1           MSIX のビルド・署名
  generate-assets.ps1      MSIX 用アセット画像の生成
  Package.appxmanifest.template
  certs/                   公開証明書（秘密鍵なし）
tests/
  Hodozip.Core.Tests/      xUnit 単体テストとフィクスチャ
  e2e/                     E2E スクリプト
```

Core は `IArchiveExtractor` インターフェース越しに SharpCompress を利用します。対応形式は ZIP です。

COM DLL は `IExplorerCommand` を実装するだけの薄いサーバーで、解凍処理は持ちません。`Invoke` されたときに、パッケージ内の `Hodozip.exe` へ `--extract-here` または `--extract-folder` と選択ファイルのパスを渡して起動します。COM サーバーは MSIX の `com:SurrogateServer` として `dllhost.exe` 内で動作します。

## 使用ライブラリ

| ライブラリ | バージョン | ライセンス | 用途 |
| --- | --- | --- | --- |
| [SharpCompress](https://github.com/adamhathcock/sharpcompress) | 0.50.8 | MIT | ZIP 解凍エンジン |
| Microsoft Windows App SDK | 1.8.260921001 | MIT | WinUI 3 実行基盤 |
| .NET | 8.0 | MIT | 実行環境 |
| xUnit | 2.5.3 | Apache-2.0 | テスト（配布物には含まれません） |

## 開発を支援する

Hodozip は無料・オープンソースです。支援は今後の開発とメンテナンスに使われます。アプリ内の「開発を支援する」ボタン、または以下のリンクから支援できます。

- GitHub Sponsors: <https://github.com/sponsors/Ren9618>
- Stripe: <https://buy.stripe.com/5kQ9AU8Xva8T7tAbnj67S00>

## 既知の制限（Phase 2）

- 解凍できるのは ZIP のみです（SharpCompress 経由で他形式が読める場合がありますが、サポート対象外です）。
- MSIX 版は自己署名証明書による実験的配布です。証明書の登録が必要で、SmartScreen や組織のポリシーによってはインストールできない場合があります。
- `.zip` の既定のアプリはユーザーが選ぶ必要があります。アプリ側から強制することはできません。
- 右クリックメニューは Windows 11 の `windows.fileExplorerContextMenus` を使っています。Windows 10 では表示されない場合があります。
- Microsoft Store への提出は行っていません。Store 提出時は、Store 用のパッケージ ID と信頼されたコード署名証明書に差し替える必要があります。また、`runFullTrust` の justification、プライバシーポリシー、年齢レーティングなどの Store 申請情報が必要です。
- 圧縮、アーカイブ内容のプレビュー、自動アップデートは未実装です。
- UI は日本語のみです。
- 自動番号付けは `名前 (2)` 形式で、10000 番まで試行します。
- 展開爆弾の警告は解凍を中止せず、警告として結果に表示します。

## ライセンス

MIT License. Copyright (c) 2026 Ren9618. 詳細は [LICENSE](LICENSE) を参照してください。
