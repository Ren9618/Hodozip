# Hodozip

**アーカイブを、ほどく。**

Hodozip（ホドジップ）は、往年の解凍ソフトの使い勝手を現代の実装で引き継ぐ、Windows 向けの無料・オープンソース解凍ソフトです。ZIP だけでなく 7z / RAR / TAR / GZ / BZ2 / XZ / ZST 系のアーカイブをドラッグ＆ドロップするだけで、解凍先フォルダを自動判定して展開します。

- 製品名: Hodozip（由来: ほどく + ZIP）
- バージョン: 0.3.0
- ライセンス: MIT
- 対応 OS: Windows 11（主対象）/ Windows 10 バージョン 19041 以降
- 実装: C# / .NET 8 / WinUI 3（Windows App SDK）
- 配布形態: インストール不要の unpackaged zip 版と、ファイル関連付け・右クリックメニューに対応する MSIX 版

## ダウンロード

[GitHub Releases](https://github.com/Ren9618/Hodozip/releases) から最新版をダウンロードできます。

- 最新版: [Hodozip v0.3.0](https://github.com/Ren9618/Hodozip/releases/tag/v0.3.0)
- `Hodozip-0.3.0-win-x64.zip`（unpackaged 版）: 解凍して `Hodozip.exe` を起動するだけで使えます。インストールは不要です。
- `Hodozip-0.3.0.msix`（MSIX 版・実験的）: 対応アーカイブの関連付け候補と Windows 11 の右クリックメニューが登録されます。自己署名証明書を使うため、インストール前に証明書の登録が必要です。手順は [MSIX 版のインストール](#msix-版のインストール上級者向け実験的) を参照してください。

unpackaged 版の注意: 本バージョンは未署名のため、Windows SmartScreen の警告が表示される場合があります。「詳細情報」→「実行」で起動できます。

unpackaged 版にも `Hodozip.ContextMenu.dll` が同梱されています。設定画面のトグル、またはコマンドラインから従来型コンテキストメニューを登録できます。

## Phase 3 の新機能

- 7z / RAR / TAR / TAR.GZ / TAR.BZ2 / TAR.XZ / TAR.ZST / GZ / BZ2 / XZ / ZST の解凍に対応（フィクスチャで実際に検証済み）
- 解凍先フォルダの衝突時動作を追加: 毎回確認 / 既存フォルダへ統合 / 連番フォルダ作成
- 同名ファイルの上書き確認ダイアログを通常フローへ配線: 上書き / スキップ / 名前をつけて保存 / すべて上書き / すべてスキップ / キャンセル
- 進捗ウィンドウを固定サイズ・中間省略表示のコンパクトなレイアウトに刷新
- unpackaged 版向けの従来型コンテキストメニュー（`IContextMenu`）と、設定画面からの登録・解除を追加
- 対応形式の拡張子をファイル関連付け・Windows 11 右クリックメニューへ反映
- アプリ内バージョン表記を 0.3.0 に統一（MSIX パッケージは 0.3.0.0）

## 対応形式

実際にフィクスチャを生成し、単体テストと E2E で解凍を確認した形式だけを対応として記載しています。

| 形式 | 拡張子 | フィクスチャ検証 |
| --- | --- | --- |
| ZIP | `.zip` | 済み（ZipCrypto / AES のパスワード付きを含む） |
| 7z | `.7z` | 済み（パスワード付きを含む） |
| RAR | `.rar` | 済み（RAR5 のテストアーカイブ） |
| TAR | `.tar` | 済み |
| TAR.GZ | `.tar.gz`, `.tgz` | 済み（両拡張子） |
| TAR.BZ2 | `.tar.bz2`, `.tbz2` | 済み（両拡張子） |
| TAR.XZ | `.tar.xz`, `.txz` | 済み（両拡張子） |
| TAR.ZST | `.tar.zst`, `.tzst` | 済み（両拡張子） |
| GZ | `.gz` | 済み（中の単一ファイルを解凍） |
| BZ2 | `.bz2` | 済み（単一ストリーム） |
| XZ | `.xz` | 済み（単一ストリーム） |
| ZST | `.zst` | 済み（単一ストリーム） |

- `.cab` と `.lzh` は未検証のため、対応形式には含めていません。
- GZ / BZ2 / XZ / ZST の単一ストリーム圧縮は、アーカイブ名から拡張子を除いた名前のファイルとして解凍します。
- 拡張子が未知でも、SharpCompress がアーカイブとして開ける場合は処理を試みます。ただし「対応」と宣言するのは上記の検証済み形式だけです。
- 暗号化アーカイブは ZIP（ZipCrypto / AES）と 7z をテスト済みです。RAR などの暗号化は対応を宣言していません。

## 特徴

### ダブルクリックですぐ解凍

`Hodozip.exe` にアーカイブのパスを渡して起動すると、その場で解凍を開始します。MSIX 版をインストールし、対応拡張子の既定のアプリに Hodozip を設定すると、ダブルクリックで解凍できます。

引数なしで起動した場合は、アーカイブをドロップできるメインウィンドウを表示します。

### 解凍先フォルダの自動判定

`Auto` モードでは、アーカイブの内容に応じて以下のルールで解凍先を決めます。

| アーカイブの内容 | 解凍結果 |
| --- | --- |
| ファイルが 1 つだけ（直下） | フォルダを作らず、アーカイブと同じ場所へそのまま出力 |
| すべてのエントリが単一のトップフォルダ配下 | そのトップフォルダごと出力（二重フォルダにしない） |
| 上記以外（複数ファイルがバラバラ） | アーカイブ名（拡張子なし）のフォルダを作成し、その中へ出力 |

`ForceArchiveFolder` モードでは、内容にかかわらず、必ずアーカイブ名（拡張子なし）のフォルダを作成してその中へ出力します。`.tar.gz` や `.tgz` のような複合拡張子・短縮拡張子は、アーカイブ名からまとめて取り除きます。

### 解凍先フォルダの衝突確認

解凍先フォルダが既に存在する場合の動作を設定できます。

| 設定 | 動作 |
| --- | --- |
| 毎回確認する（`Ask`・既定） | 解凍開始前に、対象フォルダの名前とパスを示すダイアログを表示 |
| 既存のフォルダに解凍する（`Merge`） | 既存フォルダへ統合して解凍 |
| 新しいフォルダを作成する（`AutoRename`） | `名前 (2)`、`名前 (3)` のように連番の新しいフォルダを作成 |

`Ask` のダイアログでは「既存のフォルダに解凍する」「新しいフォルダを作成する」「キャンセル」を選べます。「この選択を既定にする」を有効にすると、その選択が設定へ保存されます。既存の同名パスがファイルで、フォルダではない場合は「既存のフォルダに解凍する」を選べません。

単一ファイルを直接解凍するなど、フォルダを作らないパターンではこのダイアログは表示されません。同名ファイルの確認はファイル単位の上書き確認で行います。

### 同名ファイルの上書き確認

`Merge` で既存フォルダへ解凍する場合、および単一ファイル解凍で同名ファイルがある場合、設定が `Ask` なら上書き確認ダイアログを表示します。既存ファイルと新しいファイルのサイズ・更新日時を並べて比較でき、Lhaplus に合わせた次の 6 つの選択肢があります。

- **上書き**: このファイルを上書き
- **スキップ**: このファイルを残してスキップ
- **名前をつけて保存**: `名前 (2)` のように採番して保存
- **すべて上書き**: 現在の解凍セッション内のすべての衝突を上書き
- **すべてスキップ**: 現在の解凍セッション内のすべての衝突をスキップ
- **キャンセル**: 解凍全体を中止

「すべて上書き」「すべてスキップ」の適用範囲は現在の解凍セッション内だけで、設定には保存されません。Esc キーやダイアログを閉じる操作はキャンセルとして扱います。キャンセル時は書き込み中の一時ファイルを削除し、中途半端なファイルを残しません。

設定画面では、ファイルの上書き方針を `Ask` / `Overwrite` / `Skip` / `AutoRename` から選べます。

### 進捗ウィンドウ

進捗ウィンドウは 480×240 の固定サイズで、リサイズできません。ファイル名や進捗値によってウィンドウサイズは変わりません。

- タイトルバー: `58% / sample.zip` のように進捗率とアーカイブ名を表示
- 解凍先パス: 1 行で中間省略（先頭と末尾を残す）
- 全体の進捗バーとパーセント
- 現在処理中のファイル名（中間省略）と、そのファイルの進捗バー
- 状態テキストは 1 か所だけに表示
- 完了・エラー・キャンセルも同じ固定ウィンドウ内に表示

パスやファイル名が長い場合は末尾切り捨てではなく中間省略を使い、ツールチップで全体を確認できます。

### Windows 11 の右クリックメニュー

MSIX 版をインストールすると、対応拡張子の右クリックメニューに以下の 2 項目が追加されます。

- **Hodozip で解凍**: `Auto` モードで解凍します。単一フォルダ型のアーカイブは二重フォルダになりません。
- **Hodozip でフォルダに解凍**: `ForceArchiveFolder` モードで、必ずアーカイブ名のフォルダを作って解凍します。

対象拡張子は `.zip` `.7z` `.rar` `.tar` `.gz` `.tgz` `.bz2` `.tbz2` `.xz` `.txz` `.zst` `.tzst` です。Windows 11 の新しい右クリックメニューに項目が表示されない場合は、「その他のオプションを表示」を選ぶか、`Shift` + `F10` で従来のメニューを開いてください。

### 従来型コンテキストメニュー（unpackaged 版）

unpackaged 版では、Windows 10 形式の従来型コンテキストメニュー（Windows 11 では「その他のオプションを表示」側）にも同じ 2 項目を追加できます。サブメニューにはせず、2 項目をフラットに表示します。

登録方法:

1. Hodozip を起動し、「設定」を開きます。
2. 「従来型コンテキストメニュー」の「従来型コンテキストメニューに登録する」をオンにします。
3. 状態欄に「従来型コンテキストメニューに登録されています。」と表示されることを確認します。
4. 解除する場合は同じトグルをオフにします。

登録は管理者権限不要の HKCU 配下で行います。

- `HKCU\Software\Classes\SystemFileAssociations\<拡張子>\shellex\ContextMenuHandlers\Hodozip`
- `HKCU\Software\Classes\CLSID\{4F4C6D2A-71B9-4E4E-9A7D-8D9A0B5E6C03}\InprocServer32`

コマンドラインからも登録・解除できます（E2E とトラブルシューティング用）。

```powershell
# 登録
.\Hodozip.exe --register-context-menu

# 解除
.\Hodozip.exe --unregister-context-menu
```

登録には同梱の `Hodozip.ContextMenu.dll` への絶対パスが保存されます。unpackaged 版のフォルダを移動した場合は、古い登録を解除してから新しい場所で再登録してください。フォルダを削除する前に解除することをおすすめします。

### MSIX 版で従来型メニューが使えない理由

MSIX（パッケージ）版では従来型 `IContextMenu` ハンドラーを登録できません。そのため、MSIX 版の設定画面では従来型メニューのトグルが無効になり、Windows 11 の新しい右クリックメニューだけが利用できます。

理由:

- 従来型のシェル拡張は、エクスプローラーが DLL をプロセス内で読み込む方式です。パッケージ アプリは HKCU の `Software\Classes` への書き込みがパッケージ用に仮想化され、実シェルから `Hodozip.ContextMenu.dll` を in-proc ハンドラーとして読み込ませる公式手段がありません。
- Windows がパッケージ アプリに提供する右クリックメニュー拡張は `windows.fileExplorerContextMenus`（`IExplorerCommand`）だけで、これは Windows 11 の新しいメニュー側に表示されます。
- 無理に HKCU へ書き込んでも Explorer から認識されないため、MSIX 版では新メニューのみ、という整理にしています。

### 日本語ファイル名の文字化け対策

ZIP エントリの UTF-8 フラグを尊重します。

- UTF-8 フラグあり: UTF-8 としてデコード
- UTF-8 フラグなし: CP932（Shift_JIS）としてデコード

CP932 のデコードには .NET の `CodePagesEncodingProvider` を登録し、SharpCompress の `ArchiveEncoding.Default` に CP932 を指定しています。この Shift_JIS フォールバックは ZIP 固有です。7z / RAR / TAR などは UTF-8 を使い、Unicode 名を CP932 で誤デコードしません。実装は `src/Hodozip.Core/EncodingSetup.cs` と `src/Hodozip.Core/SharpCompressExtractor.cs` の `CreateReaderOptions` を参照してください。

### パスワード付きアーカイブ

ZipCrypto / AES のパスワード付き ZIP と、パスワード付き 7z に対応しています。暗号化エントリを検出すると UI がパスワード入力ダイアログを表示し、入力後に解凍を再開します。パスワード違いはリトライでき、最大 5 回まで入力できます。

### セキュリティ

- パス トラバーサル: `../` や絶対パス、ドライブレターを含むエントリを拒否。ZIP だけでなく TAR などの全対応形式に適用し、TAR の `../` エントリで拒否テストを実施
- シンボリックリンク: リンクとしても通常ファイルとしても展開せず、警告を出してスキップ
- ロングパス: 260 文字を超えるパスに `\\?\` プレフィックスで対応
- Windows 予約デバイス名（CON、PRN、AUX、NUL、COM1 など）をサニタイズ
- 展開爆弾対策: 展開後サイズが 1 GiB 以上、かつ圧縮サイズの 1000 倍以上の場合に警告

### ネットワーク通信なし

テレメトリはありません。アプリ本体はネットワーク通信を行いません。唯一の例外は、バージョン情報画面・設定画面の「開発を支援する」ボタンで、既定のブラウザを開いて寄付ページを表示する操作です。起動時や解凍完了時に寄付を促すポップアップは表示しません。

## 使い方

### アプリで解凍する

1. `Hodozip.exe` を起動します。
2. 対応形式のアーカイブをウィンドウへドラッグ＆ドロップするか、「ファイルを選択」からアーカイブを選びます。
3. 解凍の進捗が表示されます。完了後、「フォルダを開く」で解凍先を確認できます。

### コマンドラインから起動する

```powershell
# Auto モード（パスだけ渡す）
.\Hodozip.exe "C:\path\to\archive.zip"
.\Hodozip.exe "C:\path\to\archive.7z"

# Auto モードを明示
.\Hodozip.exe --extract-here "C:\path\to\archive.tar.gz"

# 必ずアーカイブ名のフォルダを作る
.\Hodozip.exe --extract-folder "C:\path\to\archive.rar"
```

| 引数 | 解凍モード | 動作 |
| --- | --- | --- |
| `<archive>` | `Auto` | アーカイブの内容に応じて自動判定 |
| `--extract-here <archive>` | `Auto` | 同上（右クリックメニュー用の明示形） |
| `--extract-folder <archive>` | `ForceArchiveFolder` | 必ずアーカイブ名フォルダを作成 |
| `--register-context-menu` | - | 従来型コンテキストメニューを登録して終了（unpackaged 版のみ） |
| `--unregister-context-menu` | - | 従来型コンテキストメニューを解除して終了（unpackaged 版のみ） |

アーカイブと同じフォルダへ、指定したモードに従って解凍します。「設定」で「ダブルクリック起動時の完了後、自動で閉じる」を有効にすると、コマンドライン起動時は完了後に自動で終了します（エラー時は閉じません）。

## MSIX 版のインストール（上級者向け・実験的）

MSIX 版は実験的配布です。署名にはローカルテスト用の自己署名証明書（`CN=Ren9618`）を使っており、Microsoft Store の審査を通った証明書ではありません。以下の操作は自己責任で行ってください。

### 1. 証明書をインストールする

リポジトリの [`packaging/certs/Hodozip-Test.cer`](https://github.com/Ren9618/Hodozip/raw/v0.3.0/packaging/certs/Hodozip-Test.cer) をダウンロードします。これは公開証明書のみで、秘密鍵は含まれていません。

管理者権限の PowerShell で、ダウンロードした CER を `LocalMachine\TrustedPeople`（信頼されたユーザー）に登録します。

```powershell
certutil -addstore -f TrustedPeople .\Hodozip-Test.cer
```

証明書の拇印は `09F6216808D67CCAF8686609F375EC7BFF837284` です。`Get-PfxCertificate` などで確認できます。

> 注: `CurrentUser\TrustedPeople` への登録だけでは `Add-AppxPackage` が `0x800B0109` で失敗する場合があります。その場合は上記のとおり `LocalMachine\TrustedPeople` へ登録してください。

### 2. MSIX をインストールする

`Hodozip-0.3.0.msix` をダブルクリックするか、PowerShell で実行します。

```powershell
Add-AppxPackage -Path .\Hodozip-0.3.0.msix
```

インストール後、スタートメニューに「Hodozip」が表示されます。パッケージ ID は `Ren9618.Hodozip_0.3.0.0_x64__ex19z75j0wj0g`、アプリケーション ID は `Ren9618.Hodozip_ex19z75j0wj0g!Hodozip` です。

### 3. 対応拡張子の既定のアプリに設定する

Windows はファイル関連付けをアプリ側から強制できません。MSIX 版をインストールすると Hodozip は対応拡張子の「プログラムから開く」候補に登録されますが、既定にするにはユーザー操作が必要です。

1. Windows の「設定」→「アプリ」→「既定のアプリ」を開きます。
2. `.zip` などの拡張子を検索するか、一覧から選びます。
3. 「Hodozip」を選択します。

または、ファイルを右クリック →「プログラムから開く」→「別のプログラムを選択」→「Hodozip」→「常にこのアプリを使ってファイルを開く」を選びます。

### 4. 右クリックメニューを使う

対応アーカイブを右クリックし、「Hodozip で解凍」または「Hodozip でフォルダに解凍」を選びます。Windows 11 の新しいメニューに表示されない場合は、「その他のオプションを表示」を選んでください。

MSIX 版では従来型 `IContextMenu` は登録できません。詳細は [MSIX 版で従来型メニューが使えない理由](#msix-版で従来型メニューが使えない理由) を参照してください。

インストール直後や更新後は、エクスプローラーを再起動するとメニューが反映されやすいです。

### 5. アンインストールする

「設定」→「アプリ」→「インストールされているアプリ」から Hodozip をアンインストールします。PowerShell の場合は以下を実行します。

```powershell
Remove-AppxPackage Ren9618.Hodozip_0.3.0.0_x64__ex19z75j0wj0g
```

証明書も不要になった場合は、管理者権限の PowerShell で以下を実行します。

```powershell
certutil -delstore TrustedPeople 09F6216808D67CCAF8686609F375EC7BFF837284
```

## ビルド

### 必要な環境

- Windows 10 バージョン 19041 以降 / Windows 11
- .NET SDK 8.0 以降（`net8.0` のビルドが可能なこと）
- Visual Studio 2022 以降または Build Tools（WinUI 3 / Windows App SDK / C++ のビルド環境）
- MSIX を作る場合は Windows 10/11 SDK（`makeappx.exe` / `signtool.exe`）
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

従来型コンテキストメニューを使うには `Hodozip.ContextMenu.dll` を publish 出力へ同梱する必要があります。MSIX ビルドスクリプトは publish と COM DLL のコピーをまとめて行うため、その出力を zip 化できます。

```powershell
pwsh -File packaging\build-msix.ps1
New-Item -ItemType Directory -Path artifacts\release\Hodozip -Force | Out-Null
Copy-Item -Path artifacts\msix\publish\* -Destination artifacts\release\Hodozip -Recurse -Force
Compress-Archive -Path artifacts\release\Hodozip -DestinationPath Hodozip-0.3.0-win-x64.zip
```

MSIX を作らずに zip 版だけ作る場合は、publish 後に C++ プロジェクトをビルドして DLL をコピーします。

```powershell
dotnet publish src\Hodozip\Hodozip.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained true -o publish\Hodozip
msbuild src\Hodozip.ContextMenu\Hodozip.ContextMenu.vcxproj /p:Configuration=Release /p:Platform=x64
Copy-Item src\Hodozip.ContextMenu\bin\x64\Release\Hodozip.ContextMenu.dll publish\Hodozip\
Compress-Archive -Path publish\Hodozip -DestinationPath Hodozip-0.3.0-win-x64.zip
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
5. `makeappx.exe` で `artifacts\msix\Hodozip-0.3.0.msix` を作成
6. 自己署名証明書で `signtool.exe` 署名

証明書は `artifacts\msix\certs\Hodozip-Test.pfx` に作成されます。PFX は秘密鍵を含むため、リポジトリや Release には含めません。

テスト証明書を `LocalMachine\TrustedPeople` に自動登録する場合は、管理者権限で以下を実行します。

```powershell
pwsh -File packaging\build-msix.ps1 -InstallTrustedCertificate
```

証明書を作り直す場合は `-RecreateCertificate` を指定します。Publisher（`CN=Ren9618`）と証明書の Subject が一致している必要があります。

### バージョン管理

バージョンは `Directory.Build.props` の `HodozipVersion` で管理します。アプリの `AssemblyVersion` / `FileVersion` と MSIX の `PackageVersion`（`0.3.0` → `0.3.0.0`）はこの値から生成されます。

## テスト

### Core 単体テスト

```powershell
dotnet test tests\Hodozip.Core.Tests\Hodozip.Core.Tests.csproj -c Release
```

64 件のテストで以下を検証します。

- 解凍先自動判定（単一ファイル / 単一フォルダ / バラバラ）
- `ForceArchiveFolder` の強制フォルダ作成（単一ファイル / 単一フォルダ / バラバラ）
- フォルダ衝突ポリシー（Merge / AutoRename / Ask）と Ask のキャンセル
- `Merge` 時に上書きコールバックが呼ばれること、自動リネーム、キャンセル時にファイルが残らないこと
- 上書き確認のサイズ・更新日時比較
- 全対応形式のフィクスチャ解凍（ZIP / 7z / RAR / TAR / TAR.GZ / TGZ / TAR.BZ2 / TBZ2 / TAR.XZ / TXZ / TAR.ZST / TZST / GZ / BZ2 / XZ / ZST）
- パスワード付き ZIP（ZipCrypto / AES）とパスワード付き 7z の正常解凍・リトライ・キャンセル
- TAR の `../` エントリ拒否
- Shift_JIS ファイル名と UTF-8 ファイル名（Shift_JIS は ZIP のみ）
- Zip Slip の拒否
- 予約デバイス名、シンボリックリンク、展開爆弾警告、ロングパス
- 進捗の全体バイト数・ファイル別バイト数レポート

パスワード付きテストフィクスチャ `tests/Hodozip.Core.Tests/Fixtures/password-*.zip` と `Fixtures/formats/password.7z` のパスワードは `secret` です。ZIP フィクスチャは 7-Zip で生成しています。

多形式フィクスチャの生成方法は `tests/Hodozip.Core.Tests/Fixtures/formats/generate-fixtures.ps1` にあります。7-Zip 互換 CLI と、RAR フィクスチャ取得のためのインターネット接続が必要です。生成済みファイルはリポジトリにコミットしています。

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

### Phase 3 で追加した E2E

```powershell
# 上書きフロー（Merge + Overwrite / Merge + Skip / Ask + UI Automation）
pwsh -File tests\e2e\run-overwrite-e2e.ps1

# 多形式の解凍（ZIP / 7z / RAR / TAR / 各圧縮 + 短縮拡張子）
pwsh -File tests\e2e\run-formats-e2e.ps1

# 従来型コンテキストメニュー（登録・メニュー項目・InvokeCommand・解除）
pwsh -File tests\e2e\run-legacy-menu-e2e.ps1
```

- `run-overwrite-e2e.ps1` は 3 ケースを検証します。3 ケース目は UI Automation で `OverwriteDialog` の表示を検出し、キャンセル ボタンを実際に押します。
- `run-formats-e2e.ps1` は 16 の形式・拡張子ケース（ZIP、7z、TAR、RAR、TAR.GZ/TGZ、TAR.BZ2/TBZ2、TAR.XZ/TXZ、TAR.ZST/TZST、GZ、BZ2、XZ、ZST）を検証します。
- `run-legacy-menu-e2e.ps1` は STA で自動再起動し、HKCU への登録、12 拡張子のハンドラー、`IContextMenu::QueryContextMenu` の 2 項目、`InvokeCommand` による両モードの解凍、解除を検証します。実行前の登録状態と設定ファイルは終了時に復元します。
- E2E は設定ファイル `%LOCALAPPDATA%\Hodozip\settings.json` を一時的に変更します。スクリプトを同時に実行しないでください。

### MSIX のインストール検証

MSIX の検証は、ビルドしたパッケージを実際にインストールして行います。

```powershell
# 証明書を LocalMachine\TrustedPeople に登録（管理者権限）
certutil -addstore -f TrustedPeople artifacts\msix\certs\Hodozip-Test.cer

# インストール
Add-AppxPackage -Path artifacts\msix\Hodozip-0.3.0.msix

# スタートメニューと関連付け候補の確認
Get-StartApps | Where-Object { $_.Name -eq 'Hodozip' }
Get-ItemProperty 'HKCU:\Software\Classes\.zip\OpenWithProgids'

# アンインストール
Remove-AppxPackage Ren9618.Hodozip_0.3.0.0_x64__ex19z75j0wj0g
```

インストール後の自動 E2E も用意しています。設定ファイルを共有するため、2 つのスクリプトは同時に実行しないでください。

```powershell
# パッケージ版の起動と --extract-here / --extract-folder の動作
pwsh -File tests\e2e\run-packaged-e2e.ps1

# 対応拡張子の関連付け候補、COM 登録、右クリックメニューのタイトルと verb 起動
pwsh -File tests\e2e\run-contextmenu-e2e.ps1
```

右クリックメニューの COM 登録は `HKLM\SOFTWARE\Classes\PackagedCom\ClassIndex` 配下の CLSID で確認できます。

- `4F4C6D2A-71B9-4E4E-9A7D-8D9A0B5E6C01`（Hodozip で解凍）
- `4F4C6D2A-71B9-4E4E-9A7D-8D9A0B5E6C02`（Hodozip でフォルダに解凍）
- `4F4C6D2A-71B9-4E4E-9A7D-8D9A0B5E6C03`（unpackaged 版の従来型メニュー）

## プロジェクト構成

```text
Hodozip.sln
Directory.Build.props      バージョンの一元管理
src/
  Hodozip.Core/            解凍ロジック（UI 非依存）
    ArchiveFormatCatalog.cs  対応形式の一覧と判定
    DestinationPlanner.cs    解凍先フォルダの決定
    SharpCompressExtractor.cs 解凍エンジン
  Hodozip/                 WinUI 3 アプリ
    Dialogs/               上書き・フォルダ衝突・パスワード等のダイアログ
    Services/              設定、従来型メニュー登録、中間省略など
  Hodozip.ContextMenu/     Windows 11 右クリックメニュー用 COM DLL（IExplorerCommand / IContextMenu）
packaging/
  build-msix.ps1           MSIX のビルド・署名
  generate-assets.ps1      MSIX 用アセット画像の生成
  Package.appxmanifest.template
  certs/                   公開証明書（秘密鍵なし）
tests/
  Hodozip.Core.Tests/      xUnit 単体テストとフィクスチャ
  e2e/                     E2E スクリプト
```

Core は `IArchiveExtractor` インターフェース越しに SharpCompress を利用します。形式判定は拡張子を優先し、判別できない場合は SharpCompress にアーカイブとして開けるかを問い合わせます。TAR.GZ / TAR.BZ2 / TAR.XZ / TAR.ZST は、単一ストリームを一時 TAR へ展開してから TAR として処理します。

COM DLL は `IExplorerCommand`（Windows 11 新メニュー）と `IContextMenu`（従来型メニュー）を実装する薄いサーバーで、解凍処理は持ちません。`Invoke` / `InvokeCommand` されたときに、`Hodozip.exe` へ `--extract-here` または `--extract-folder` と選択ファイルのパスを渡して起動します。MSIX 版の `IExplorerCommand` は `com:SurrogateServer` として `dllhost.exe` 内で動作します。

## 使用ライブラリ

| ライブラリ | バージョン | ライセンス | 用途 |
| --- | --- | --- | --- |
| [SharpCompress](https://github.com/adamhathcock/sharpcompress) | 0.50.8 | MIT | ZIP / 7z / RAR / TAR / GZ / BZ2 / XZ / ZST の解凍エンジン |
| Microsoft Windows App SDK | 1.8.260921001 | MIT | WinUI 3 実行基盤 |
| .NET | 8.0 | MIT | 実行環境 |
| xUnit | 2.5.3 | Apache-2.0 | テスト（配布物には含まれません） |

## 開発を支援する

Hodozip は無料・オープンソースです。支援は今後の開発とメンテナンスに使われます。アプリ内の「開発を支援する」ボタン、または以下のリンクから支援できます。

- GitHub Sponsors: <https://github.com/sponsors/Ren9618>
- Stripe: <https://buy.stripe.com/5kQ9AU8Xva8T7tAbnj67S00>

## 既知の制限（Phase 3）

- MSIX 版では、Windows のパッケージ アプリ向けシェル拡張の制約により従来型コンテキストメニューを登録できません。MSIX 版は Windows 11 の新しい右クリックメニューのみになります。理由は [MSIX 版で従来型メニューが使えない理由](#msix-版で従来型メニューが使えない理由) を参照してください。
- Windows 11 の新しい右クリックメニューは Windows 10 では表示されません。Windows 10 では unpackaged 版の従来型メニューを利用してください。
- ファイル関連付けは Windows の仕様上、最後の拡張子で判定されます。`.tar.gz` は `.gz`、`.tar.bz2` は `.bz2` として関連付けられます。Hodozip 本体は複合拡張子を認識するため、ドラッグ＆ドロップやコマンドラインからは `.tar.gz` のまま正しく解凍できます。
- RAR / 7z / TAR 系の解凍は SharpCompress に依存します。対応を宣言しているのはフィクスチャで検証済みの形式だけで、特殊な圧縮オプションや将来の形式バリアントは失敗する場合があります。`.cab` と `.lzh` は未対応です。
- 暗号化アーカイブの対応を宣言しているのは ZIP と 7z のみです。RAR などの暗号化アーカイブは未検証です。
- 形式ごとの専用アイコンは未実装で、全対応形式で共通のアイコンを使用します。
- MSIX 版は自己署名証明書による実験的配布です。証明書の登録が必要で、SmartScreen や組織のポリシーによってはインストールできない場合があります。
- 対応拡張子の既定のアプリはユーザーが選ぶ必要があります。アプリ側から強制することはできません。
- 従来型メニューの登録は `Hodozip.ContextMenu.dll` の絶対パスを保存します。unpackaged 版のフォルダを移動した場合は再登録が必要です。
- Microsoft Store への提出は行っていません。Store 提出時は、Store 用のパッケージ ID と信頼されたコード署名証明書に差し替える必要があります。また、`runFullTrust` の justification、プライバシーポリシー、年齢レーティングなどの Store 申請情報が必要です。
- 圧縮、アーカイブ内容のプレビュー、自動アップデートは未実装です。
- UI は日本語のみです。
- 自動番号付けは `名前 (2)` 形式で、10000 番まで試行します。
- 展開爆弾の警告は解凍を中止せず、警告として結果に表示します。

## ライセンス

MIT License. Copyright (c) 2026 Ren9618. 詳細は [LICENSE](LICENSE) を参照してください。
