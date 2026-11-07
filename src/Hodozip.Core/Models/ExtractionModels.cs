namespace Hodozip.Core;

public enum ExtractionIssueSeverity
{
    Warning,
    Error,
}

public enum ExtractionIssueKind
{
    PathTraversal,
    SymbolicLink,
    UnsafeName,
    SuspiciousExpansion,
    EntryFailed,
    Password,
    ArchiveError,
    Cancelled,
}

/// <summary>解凍先フォルダの決定方法。</summary>
public enum ExtractionDestinationMode
{
    /// <summary>ZIP の内容から自動判定する（Phase 1 と同じ）。</summary>
    Auto,

    /// <summary>常にアーカイブ名のフォルダを作成し、その中へ解凍する。</summary>
    ForceArchiveFolder,
}

/// <summary>解凍中に検出した警告・エラー。</summary>
public sealed record ExtractionIssue(
    ExtractionIssueSeverity Severity,
    ExtractionIssueKind Kind,
    string EntryPath,
    string Message)
{
    /// <summary>
    /// UI が表示言語へ変換するためのリソース キー。null の場合は Message をそのまま表示する。
    /// Core 自体は UI 言語を知らないため、日本語の Message をフォールバックとして保持する。
    /// </summary>
    public string? MessageKey { get; init; }

    /// <summary>MessageKey の書式引数。</summary>
    public IReadOnlyList<object?> MessageArguments { get; init; } = Array.Empty<object?>();
}

/// <summary>上書き確認に必要な新旧ファイル情報。</summary>
public sealed record OverwriteConflict(
    string EntryPath,
    string DestinationPath,
    long ExistingSize,
    long NewSize,
    DateTimeOffset? ExistingLastWriteTime,
    DateTimeOffset? NewLastWriteTime);

/// <summary>パスワード入力要求。Attempt は 1 から始まる。</summary>
public sealed record PasswordRequest(int Attempt, string? ErrorMessage)
{
    /// <summary>UI が表示言語へ変換するためのリソース キー。</summary>
    public string? ErrorMessageKey { get; init; }
}

/// <summary>解凍の進捗。</summary>
public sealed record ExtractionProgress(
    long ProcessedBytes,
    long TotalBytes,
    string CurrentFile,
    long CurrentFileProcessedBytes = 0,
    long CurrentFileTotalBytes = 0,
    string? DestinationPath = null);

/// <summary>解凍処理のオプション。</summary>
public sealed class ExtractionOptions
{
    /// <summary>展開後サイズの警告を出す最小サイズ（既定 1 GiB）。</summary>
    public const long DefaultExpansionWarningMinBytes = 1L * 1024 * 1024 * 1024;

    /// <summary>展開後サイズの警告を出す圧縮比（既定 1000:1）。</summary>
    public const double DefaultExpansionWarningRatio = 1000d;

    public OverwritePolicy OverwritePolicy { get; init; } = OverwritePolicy.Ask;

    /// <summary>Ask 方針で既存ファイルに当たったときに呼ばれる。</summary>
    public Func<OverwriteConflict, CancellationToken, Task<OverwriteDecision>>? OverwriteResolver { get; init; }

    /// <summary>解凍先フォルダが既に存在するときの扱い。</summary>
    public FolderCollisionPolicy FolderCollisionPolicy { get; init; } = FolderCollisionPolicy.Ask;

    /// <summary>
    /// Ask 方針で解凍先フォルダが既に存在するときに呼ばれる。
    /// null の場合は従来どおり連番フォルダを作成する。
    /// </summary>
    public Func<FolderCollisionConflict, CancellationToken, Task<FolderCollisionDecision>>? FolderCollisionResolver { get; init; }

    /// <summary>暗号化 ZIP のパスワードを要求する。null を返すとキャンセル。</summary>
    public Func<PasswordRequest, CancellationToken, Task<string?>>? PasswordProvider { get; init; }

    public IProgress<ExtractionProgress>? Progress { get; init; }

    /// <summary>
    /// ZIP 解凍の最大並列度。0 は CPU コア数に合わせる自動、1 は逐次。
    /// </summary>
    public int MaxDegreeOfParallelism { get; init; }

    public long ExpansionWarningMinBytes { get; init; } = DefaultExpansionWarningMinBytes;

    public double ExpansionWarningRatio { get; init; } = DefaultExpansionWarningRatio;

    /// <summary>
    /// 既存ファイルの更新日時がアーカイブ内のエントリより新しい場合、
    /// 確認せずにスキップする（Ask 方針のときだけ有効）。
    /// </summary>
    public bool SkipExistingIfNewer { get; init; }

    /// <summary>
    /// macOS 由来の不要ファイル（__MACOSX/、.DS_Store、._ で始まる名前）を
    /// 解凍せずにスキップし、スキップ件数に数える。既定 ON。
    /// </summary>
    public bool ExcludeMacOsMetadata { get; init; } = true;

    /// <summary>
    /// NFD で格納されたファイル名を NFC へ正規化して書き出す。既定 ON。
    /// </summary>
    public bool NormalizeFileNamesNfc { get; init; } = true;
}

/// <summary>1 回の解凍要求。</summary>
public sealed class ExtractionRequest
{
    public ExtractionRequest(
        string archivePath,
        string? destinationDirectory,
        ExtractionOptions? options = null,
        ExtractionDestinationMode destinationMode = ExtractionDestinationMode.Auto)
    {
        ArchivePath = archivePath;
        DestinationDirectory = destinationDirectory;
        Options = options ?? new ExtractionOptions();
        DestinationMode = destinationMode;
    }

    public string ArchivePath { get; }

    /// <summary>解凍先の基準フォルダ。null の場合はアーカイブと同じフォルダ。</summary>
    public string? DestinationDirectory { get; }

    public ExtractionOptions Options { get; }

    /// <summary>解凍先フォルダの決定方法。</summary>
    public ExtractionDestinationMode DestinationMode { get; }
}

/// <summary>解凍結果。</summary>
public sealed class ExtractionResult
{
    public required bool Success { get; init; }

    public bool Cancelled { get; init; }

    /// <summary>実際にファイルを出力したフォルダ。単一ファイルの場合は基準フォルダ。</summary>
    public required string DestinationPath { get; init; }

    public int ExtractedFileCount { get; init; }

    public int SkippedFileCount { get; init; }

    public IReadOnlyList<ExtractionIssue> Issues { get; init; } = Array.Empty<ExtractionIssue>();

    public bool HasWarnings => Issues.Any(x => x.Severity == ExtractionIssueSeverity.Warning);

    public bool HasErrors => Issues.Any(x => x.Severity == ExtractionIssueSeverity.Error);
}
