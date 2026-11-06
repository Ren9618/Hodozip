# GPU extraction evaluation (cozip)

- 評価日: 2026-11-07
- 対象: [bea4dev/cozip](https://github.com/bea4dev/cozip) commit `85a1e17b74882506c2f2f12a2b092ee72e9a3d7f`（MIT License）
- ツールチェーン: rustc 1.99.1 / cargo 1.99.1
- 検証機: AMD Ryzen 7 5800X（8 コア / 16 論理プロセッサ）、Windows 11 x64
- 比較対象: Hodozip v0.4.0 の CPU 並列 ZIP 解凍（SharpCompress、16 並列）
- 評価用ヘルパー: [`tools/cozip-spike`](../../../tools/cozip-spike)（配布物には含めない）

## 方法

`hodozip-gpu-spike.exe` という小さな Rust ヘルパーから、cozip の
`CoZip::init(CoZipOptions::Zip { options })` と
`decompress_auto_from_name` を呼び出した。

- `parallel_read_threads` / `parallel_write_threads` は 16 に設定
- `--hybrid`（既定）は `ZipDeflateMode::Hybrid`。cozip のデスクトップ版と同様に
  GPU バックエンド（wgpu）を初期化する
- `--cpu` は `ZipDeflateMode::Cpu`。GPU を初期化しない
- cozip 内部の `init_ms`（初期化）と `extract_ms`（解凍）に加え、
  プロセス起動から終了までの wall clock を計測
- wall clock は各フィクスチャ 3 回の中央値
- 出力は SharpCompress / 7-Zip で解凍した参照出力と SHA-256 で比較
- Shift_JIS ファイル名の ZIP は別途生成し、ファイル名と内容を確認
- 暗号化 ZIP（ZipCrypto / AES）と 7z は cozip の対応可否を確認

## 計測結果

SharpCompress 並列の値は `tests/perf/reports/comparison.md` の最終計測
（large 806 ms / many-small 8,210 ms / store 184 ms / 7z 3,111 ms）を使った。

| フィクスチャ | SharpCompress 並列 (wall) | cozip Hybrid（init / extract / wall） | cozip CPU のみ（init / extract / wall） | Hybrid の速度比 | CPU のみの速度比 |
| --- | ---: | ---: | ---: | ---: | ---: |
| large-compressible（1 GiB 展開、16 entries） | 806 ms | 822 / 256 / 1,235 ms | 0 / 232 / 320 ms | 0.65x | 2.52x |
| many-small-files（10,000 entries） | 8,210 ms | 1,355 / 2,988 / 3,958 ms | 0 / 2,735 / 2,858 ms | 2.07x | 2.87x |
| store-heavy（256 MiB、128 stored entries） | 184 ms | 1,332 / 183 / 1,808 ms | 0 / 190 / 315 ms | 0.10x | 0.58x |
| representative-7z | 3,111 ms | 非対応（EOCD not found） | 非対応 | - | - |

補足:

- cozip の Hybrid は GPU を初期化するが、ZIP の deflate **解凍** 自体は
  CPU 実装（`deflate_decompress_stream_on_cpu`）で行われる。GPU 初期化に
  0.8〜1.3 秒かかり、wall clock では SharpCompress 並列より遅い。
- cozip の README とソースのとおり、GPU 加速は **圧縮** 用で、
  cozip 自身のインデックス付きアーカイブ（`_czdi_index`）以外では
  GPU 解凍は選ばれない。
- store-heavy は cozip の書き込み経路が SharpCompress より遅く、明確な退行。
- CPU のみの many-small は 2.87x と速いが、これは GPU ではなく Rust 製
  CPU 並列実装の速度である。

## 正しさ

- large-compressible: 16 ファイルすべて SHA-256 一致
- many-small-files: 10,000 ファイルすべて SHA-256 一致
- store-heavy: 128 ファイルすべて SHA-256 一致
- Shift_JIS ZIP: `日本語フォルダ\テスト.txt` などの名前・内容が正しく解凍された
- 暗号化 ZIP: ZipCrypto は `encrypted zip entries are unsupported`、
  AES は `only deflate/store methods are supported` で失敗（SharpCompress への
  フォールバックが必須）
- 7z: 非対応（SharpCompress へのフォールバックが必須）

## 判定: 不採用

GPU ゲート（large フィクスチャで 2 倍以上、通常 ZIP / Shift_JIS ZIP の出力一致、
暗号化 ZIP のフォールバック）について、**Hybrid の実時間では 2 倍を満たさない**
（1,235 ms vs 806 ms、0.65x）。cozip の ZIP 解凍は CPU のみで、GPU 初期化の
オーバーヘッドが解凍時間を上回る。

CPU のみでは large 2.52x / many-small 2.87x を満たすが、それは GPU ではなく
Rust 製 CPU 並列実装の速度であり、次の理由から採用しない。

1. 「GPU 解凍」として提供できない（通常の ZIP では GPU 経路が存在しない）。
2. store-heavy が 0.58x と退行し、フィクスチャ全体で安定した改善にならない。
3. 7z / 暗号化 ZIP は非対応で、形式ごとのフォールバック分岐が必要。
4. 別プロセス ヘルパー（約 8.3 MB）の同梱、進捗通知・キャンセル伝播・
   Zip Slip 検査・上書き確認の再実装が必要になり、保守コストが利点を上回る。

したがって v0.4.0 は CPU 並列解凍のみを搭載し、GPU エンジン設定・
`hodozip-gpu-spike.exe` の同梱は行わない。この判定はリリース失敗ではない。
