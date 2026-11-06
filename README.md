# Hodozip

**ZIP を、ほどく。**

Hodozip（ホドジップ）は、往年の解凍ソフトの使い勝手を現代の実装で引き継ぐ、Windows 向けの無料・オープンソース ZIP 解凍ソフトです。ZIP をドラッグ＆ドロップするだけで、解凍先フォルダを自動判定して展開します。

- 製品名: Hodozip（由来: ほどく + ZIP）
- ライセンス: MIT
- 対応 OS: Windows 11（主対象）/ Windows 10 バージョン 19041 以降
- 実装: C# / .NET 8 / WinUI 3（Windows App SDK）
- Phase 1 は unpackaged（MSIX 未使用）の x64 アプリです。

## ダウンロード

[GitHub Releases](https://github.com/Ren9618/Hodozip/releases) から最新版をダウンロードできます。

- 最新版: [Hodozip v0.1.0](https://github.com/Ren9618/Hodozip/releases/tag/v0.1.0)（`Hodozip-0.1.0-win-x64.zip`）
- 使い方: zip を解凍し、`Hodozip.exe` を起動して ZIP ファイルをドラッグ＆ドロップするか、`Hodozip.exe <ファイル.zip>` で起動します。アーカイブと同じ場所へ解凍されます。
- 注意: 本バージョンは未署名のため、Windows SmartScreen の警告が表示される場合があります。「詳細情報」→「実行」で起動できます。

## 特徴

### ダブルクリックですぐ解凍

`Hodozip.exe` に ZIP ファイルのパスを渡して起動すると、その場で解凍を開始します。ZIP の関連付けや右クリックメニューは Phase 2 で対応予定ですが、Phase 1 でもコマンドライン引数による起動を受け付けます。

引数なしで起動した場合は、ZIP をドロップできるメインウィンドウを表示します。

### 解凍先フォルダの自動判定

ZIP の内容に応じて、以下のルールで解凍先を決めます。

| ZIP の内容 | 解凍結果 |
| --- | --- |
| ファイルが 1 つだけ（ZIP 直下） | フォルダを作らず、アーカイブと同じ場所へそのまま出力 |
| すべてのエントリが単一のトップフォルダ配下 | そのトップフォルダごと出力（二重フォルダにしない） |
| 上記以外（複数ファイルがバラバラ） | アーカイブ名（拡張子なし）のフォルダを作成し、その中へ出力 |

作成先フォルダが既に存在する場合は上書きせず、`名前 (2)`、`名前 (3)` のように自動で番号を付けます。

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

1. `Hodozip.exe` を起動します。
2. ZIP ファイルをウィンドウへドラッグ＆ドロップするか、「ファイルを選択」から ZIP を選びます。
3. 解凍の進捗が表示されます。完了後、「フォルダを開く」で解凍先を確認できます。

コマンドラインから起動する場合:

```powershell
.\Hodozip.exe "C:\path\to\archive.zip"
```

アーカイブと同じフォルダへ、自動判定ルールに従って解凍します。「設定」で「ダブルクリック起動時の完了後、自動で閉じる」を有効にすると、コマンドライン起動時は完了後に自動で終了します（エラー時は閉じません）。

## ビルド

### 必要な環境

- Windows 10 バージョン 19041 以降 / Windows 11
- .NET SDK 8.0 以降（`net8.0` のビルドが可能なこと）
- Visual Studio 2022 または Build Tools（WinUI 3 / Windows App SDK のビルド環境）
- x64

### コマンド

```powershell
dotnet build Hodozip.sln -c Release -p:Platform=x64
```

ビルド成果物:

```text
src\Hodozip\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\Hodozip.exe
```

Windows App SDK は self-contained で出力されるため、Phase 1 の実行には MSIX インストールは不要です。

## テスト

### Core 単体テスト

```powershell
dotnet test tests\Hodozip.Core.Tests\Hodozip.Core.Tests.csproj -c Release
```

23 件のテストで以下を検証します。

- 解凍先自動判定（単一ファイル / 単一フォルダ / バラバラ）
- Shift_JIS ファイル名と UTF-8 ファイル名
- Zip Slip の拒否
- パスワード付き ZIP（ZipCrypto / AES）の正常解凍とリトライ
- 上書きポリシー（Overwrite / Skip / AutoRename / Ask）
- キャンセル時に一時ファイルが残らないこと
- 予約デバイス名、シンボリックリンク、展開爆弾警告、ロングパス

パスワード付きテストフィクスチャ `tests/Hodozip.Core.Tests/Fixtures/password-*.zip` のパスワードは `secret` です。フィクスチャは 7-Zip で生成しています。

### アプリ E2E

Release ビルド後に以下を実行します。

```powershell
pwsh -File tests/e2e/run-e2e.ps1
```

E2E では一時フォルダに単一フォルダ型・バラバラ型の ZIP を作成し、`Hodozip.exe <zip>` で無人解凍してファイル構成を検証します。あわせて引数なし起動でメインウィンドウが表示されることを確認します。テスト中は設定ファイルを一時的に変更し、終了時に復元します。

## プロジェクト構成

```text
Hodozip.sln
src/
  Hodozip.Core/         解凍ロジック（UI 非依存）
  Hodozip/              WinUI 3 アプリ
tests/
  Hodozip.Core.Tests/   xUnit 単体テストとフィクスチャ
  e2e/                  E2E スクリプト
```

Core は `IArchiveExtractor` インターフェース越しに SharpCompress を利用します。Phase 1 の対応形式は ZIP ですが、インターフェースは形式に依存しません。

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

## 既知の制限（Phase 1）

- 解凍できるのは ZIP のみです（SharpCompress 経由で他形式が読める場合がありますが、サポート対象外です）。
- MSIX パッケージ化、ファイル関連付けの登録、Windows 11 右クリックメニューは未実装です。
- 圧縮、アーカイブ内容のプレビュー、自動アップデートは未実装です。
- UI は日本語のみです。
- 自動番号付けは `名前 (2)` 形式で、10000 番まで試行します。
- 展開爆弾の警告は解凍を中止せず、警告として結果に表示します。

## ライセンス

MIT License. Copyright (c) 2026 Ren9618. 詳細は [LICENSE](LICENSE) を参照してください。
