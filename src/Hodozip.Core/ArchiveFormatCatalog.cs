namespace Hodozip.Core;

/// <summary>アーカイブの格納構造。</summary>
internal enum ArchiveLayoutKind
{
    /// <summary>SharpCompress の ArchiveFactory で直接開ける形式。</summary>
    Archive,

    /// <summary>tar を単一ストリーム圧縮した形式。展開してから tar として読む。</summary>
    WrappedTar,

    /// <summary>単一ファイルを圧縮しただけの形式。</summary>
    BareCompressed,
}

/// <summary>単一ストリームの圧縮方式。</summary>
internal enum CompressionWrapper
{
    None,
    GZip,
    BZip2,
    Xz,
    Zstd,
}

internal sealed record ArchiveFormatInfo(
    ArchiveLayoutKind Layout,
    CompressionWrapper Wrapper,
    string Extension,
    string DisplayName);

/// <summary>
/// 対応形式の一覧と判定。実際にフィクスチャで解凍を検証した形式だけを載せる。
/// 形式判定は拡張子を優先し、判別できない場合は SharpCompress に開かせて判定する。
/// </summary>
public static class ArchiveFormats
{
    // ユーザー向けの対応拡張子。UI・関連付け・右クリックメニューはこの一覧を使う。
    private static readonly string[] Extensions =
    {
        ".zip",
        ".7z",
        ".rar",
        ".tar",
        ".tar.gz",
        ".tgz",
        ".tar.bz2",
        ".tbz2",
        ".tar.xz",
        ".txz",
        ".tar.zst",
        ".tzst",
        ".gz",
        ".bz2",
        ".xz",
        ".zst",
    };

    public static IReadOnlyList<string> SupportedExtensions => Extensions;

    public static bool IsSupportedExtension(string path)
    {
        return TryGetInfo(path) is not null;
    }

    /// <summary>
    /// 拡張子が未知でも、SharpCompress がアーカイブとして開けるなら対応扱いにする。
    /// </summary>
    public static bool IsSupportedArchive(string path)
    {
        if (IsSupportedExtension(path))
        {
            return true;
        }

        try
        {
            return SharpCompress.Archives.ArchiveFactory.IsArchive(path, out _);
        }
        catch
        {
            return false;
        }
    }

    public static string GetDisplayName(string path)
    {
        var info = TryGetInfo(path);
        return info?.DisplayName ?? "アーカイブ";
    }

    /// <summary>アーカイブ名から拡張子を除いたフォルダ名の元になる文字列を返す。</summary>
    public static string GetArchiveBaseName(string path)
    {
        var fileName = Path.GetFileName(path);
        if (string.IsNullOrEmpty(fileName))
        {
            return "Hodozip";
        }

        var info = TryGetInfo(path);
        if (info is not null && fileName.Length > info.Extension.Length)
        {
            return fileName[..^info.Extension.Length];
        }

        var withoutExtension = Path.GetFileNameWithoutExtension(fileName);
        return string.IsNullOrWhiteSpace(withoutExtension) ? "Hodozip" : withoutExtension;
    }

    internal static ArchiveFormatInfo? TryGetInfo(string path)
    {
        var fileName = Path.GetFileName(path);
        if (string.IsNullOrEmpty(fileName))
        {
            return null;
        }

        var lower = fileName.ToLowerInvariant();

        // 複合拡張子を先に判定する。
        if (lower.EndsWith(".tar.gz", StringComparison.Ordinal))
        {
            return new ArchiveFormatInfo(ArchiveLayoutKind.WrappedTar, CompressionWrapper.GZip, ".tar.gz", "TAR.GZ");
        }

        if (lower.EndsWith(".tar.bz2", StringComparison.Ordinal))
        {
            return new ArchiveFormatInfo(ArchiveLayoutKind.WrappedTar, CompressionWrapper.BZip2, ".tar.bz2", "TAR.BZ2");
        }

        if (lower.EndsWith(".tar.xz", StringComparison.Ordinal))
        {
            return new ArchiveFormatInfo(ArchiveLayoutKind.WrappedTar, CompressionWrapper.Xz, ".tar.xz", "TAR.XZ");
        }

        if (lower.EndsWith(".tar.zst", StringComparison.Ordinal))
        {
            return new ArchiveFormatInfo(ArchiveLayoutKind.WrappedTar, CompressionWrapper.Zstd, ".tar.zst", "TAR.ZST");
        }

        var extension = Path.GetExtension(lower);
        return extension switch
        {
            ".zip" => new ArchiveFormatInfo(ArchiveLayoutKind.Archive, CompressionWrapper.None, ".zip", "ZIP"),
            ".7z" => new ArchiveFormatInfo(ArchiveLayoutKind.Archive, CompressionWrapper.None, ".7z", "7z"),
            ".rar" => new ArchiveFormatInfo(ArchiveLayoutKind.Archive, CompressionWrapper.None, ".rar", "RAR"),
            ".tar" => new ArchiveFormatInfo(ArchiveLayoutKind.Archive, CompressionWrapper.None, ".tar", "TAR"),
            ".gz" => new ArchiveFormatInfo(ArchiveLayoutKind.Archive, CompressionWrapper.None, ".gz", "GZ"),
            ".tgz" => new ArchiveFormatInfo(ArchiveLayoutKind.WrappedTar, CompressionWrapper.GZip, ".tgz", "TGZ"),
            ".bz2" => new ArchiveFormatInfo(ArchiveLayoutKind.BareCompressed, CompressionWrapper.BZip2, ".bz2", "BZ2"),
            ".tbz2" => new ArchiveFormatInfo(ArchiveLayoutKind.WrappedTar, CompressionWrapper.BZip2, ".tbz2", "TBZ2"),
            ".xz" => new ArchiveFormatInfo(ArchiveLayoutKind.BareCompressed, CompressionWrapper.Xz, ".xz", "XZ"),
            ".txz" => new ArchiveFormatInfo(ArchiveLayoutKind.WrappedTar, CompressionWrapper.Xz, ".txz", "TXZ"),
            ".zst" => new ArchiveFormatInfo(ArchiveLayoutKind.BareCompressed, CompressionWrapper.Zstd, ".zst", "ZST"),
            ".tzst" => new ArchiveFormatInfo(ArchiveLayoutKind.WrappedTar, CompressionWrapper.Zstd, ".tzst", "TZST"),
            _ => null,
        };
    }
}
