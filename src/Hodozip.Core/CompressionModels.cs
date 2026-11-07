namespace Hodozip.Core;

/// <summary>新規に作成できるアーカイブ形式。</summary>
public enum ArchiveCreationFormat
{
    Zip,
    SevenZip,
}

/// <summary>圧縮レベル。Store は ZIP では無圧縮、7z では最小の LZMA2 圧縮になる。</summary>
public enum ArchiveCompressionLevel
{
    Store,
    Normal,
    Maximum,
}

/// <summary>ZIP 内のファイル名エンコーディング。</summary>
public enum ZipNameEncoding
{
    /// <summary>UTF-8（既定）。</summary>
    Utf8,

    /// <summary>Shift_JIS (CP932)。古い ZIP ツールとの互換用。</summary>
    ShiftJis,
}

/// <summary>圧縮の進捗。</summary>
public sealed record CompressionProgress(
    long ProcessedBytes,
    long TotalBytes,
    string CurrentFile,
    long CurrentFileProcessedBytes = 0,
    long CurrentFileTotalBytes = 0,
    string? OutputPath = null);

/// <summary>圧縮処理のオプション。</summary>
public sealed class CompressionOptions
{
    public ArchiveCreationFormat Format { get; init; } = ArchiveCreationFormat.Zip;

    public ArchiveCompressionLevel Level { get; init; } = ArchiveCompressionLevel.Normal;

    /// <summary>ZIP の WinZip AES-256 パスワード。7z では未対応（無視される）。</summary>
    public string? Password { get; init; }

    public ZipNameEncoding ZipNameEncoding { get; init; } = ZipNameEncoding.Utf8;

    /// <summary>
    /// macOS（Finder）で文字化けせず解凍できる ZIP を作る。
    /// UTF-8 フラグを立て、ファイル名を NFC に正規化する。ZIP だけが対象。
    /// </summary>
    public bool MacOsCompatible { get; init; }

    public IProgress<CompressionProgress>? Progress { get; init; }

    /// <summary>出力先が存在する場合に上書きする。false の場合は連番を付ける。</summary>
    public bool Overwrite { get; init; }
}

/// <summary>1 回の圧縮要求。</summary>
public sealed class CompressionRequest
{
    public CompressionRequest(
        IReadOnlyList<string> sourcePaths,
        string outputPath,
        CompressionOptions? options = null)
    {
        SourcePaths = sourcePaths;
        OutputPath = outputPath;
        Options = options ?? new CompressionOptions();
    }

    /// <summary>圧縮するファイルまたはフォルダー。複数指定できる。</summary>
    public IReadOnlyList<string> SourcePaths { get; }

    /// <summary>作成するアーカイブのパス（拡張子を含む）。</summary>
    public string OutputPath { get; }

    public CompressionOptions Options { get; }
}

/// <summary>圧縮結果。</summary>
public sealed class CompressionResult
{
    public required bool Success { get; init; }

    public bool Cancelled { get; init; }

    /// <summary>実際に作成したアーカイブのパス。失敗時は要求されたパス。</summary>
    public required string OutputPath { get; init; }

    public int FileCount { get; init; }

    public int DirectoryCount { get; init; }

    public long SourceBytes { get; init; }

    public long OutputBytes { get; init; }

    public string? ErrorMessage { get; init; }

    public string? ErrorMessageKey { get; init; }

    public IReadOnlyList<object?> ErrorArguments { get; init; } = Array.Empty<object?>();

    /// <summary>7z にパスワードが指定されたため無視された場合 true。</summary>
    public bool PasswordIgnored { get; init; }
}

/// <summary>圧縮エンジンの抽象。</summary>
public interface IArchiveCompressor
{
    Task<CompressionResult> CompressAsync(
        CompressionRequest request,
        CancellationToken cancellationToken = default);
}
