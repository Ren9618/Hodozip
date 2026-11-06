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

/// <summary>解凍中に検出した警告・エラー。</summary>
public sealed record ExtractionIssue(
    ExtractionIssueSeverity Severity,
    ExtractionIssueKind Kind,
    string EntryPath,
    string Message);

/// <summary>上書き確認に必要な新旧ファイル情報。</summary>
public sealed record OverwriteConflict(
    string EntryPath,
    string DestinationPath,
    long ExistingSize,
    long NewSize,
    DateTimeOffset? ExistingLastWriteTime,
    DateTimeOffset? NewLastWriteTime);

/// <summary>パスワード入力要求。Attempt は 1 から始まる。</summary>
public sealed record PasswordRequest(int Attempt, string? ErrorMessage);

/// <summary>解凍の進捗。</summary>
public sealed record ExtractionProgress(
    long ProcessedBytes,
    long TotalBytes,
    string CurrentFile);

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

    /// <summary>暗号化 ZIP のパスワードを要求する。null を返すとキャンセル。</summary>
    public Func<PasswordRequest, CancellationToken, Task<string?>>? PasswordProvider { get; init; }

    public IProgress<ExtractionProgress>? Progress { get; init; }

    public long ExpansionWarningMinBytes { get; init; } = DefaultExpansionWarningMinBytes;

    public double ExpansionWarningRatio { get; init; } = DefaultExpansionWarningRatio;
}

/// <summary>1 回の解凍要求。</summary>
public sealed class ExtractionRequest
{
    public ExtractionRequest(string archivePath, string? destinationDirectory, ExtractionOptions? options = null)
    {
        ArchivePath = archivePath;
        DestinationDirectory = destinationDirectory;
        Options = options ?? new ExtractionOptions();
    }

    public string ArchivePath { get; }

    /// <summary>解凍先の基準フォルダ。null の場合はアーカイブと同じフォルダ。</summary>
    public string? DestinationDirectory { get; }

    public ExtractionOptions Options { get; }
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
