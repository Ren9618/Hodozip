namespace Hodozip.Core;

internal sealed record CompressionEntry(
    string SourcePath,
    string EntryPath,
    long Length,
    DateTime ModifiedTime,
    bool IsDirectory);

/// <summary>圧縮先の既定名・衝突回避・入力の展開。</summary>
public static class CompressionDefaults
{
    public static string GetExtension(ArchiveCreationFormat format) =>
        format == ArchiveCreationFormat.SevenZip ? ".7z" : ".zip";

    /// <summary>
    /// 既定の出力パスを返す。単一項目はその名前、複数項目は親フォルダー名を使い、
    /// 元の項目と同じフォルダーに置く。
    /// </summary>
    public static string GetDefaultOutputPath(
        IReadOnlyList<string> sourcePaths,
        ArchiveCreationFormat format)
    {
        if (sourcePaths.Count == 0)
        {
            return Path.Combine(
                Environment.CurrentDirectory,
                "Hodozip" + GetExtension(format));
        }

        var first = TrimPath(sourcePaths[0]);
        var firstParent = Path.GetDirectoryName(first) ?? Environment.CurrentDirectory;

        string baseName;
        if (sourcePaths.Count == 1)
        {
            baseName = Directory.Exists(first)
                ? Path.GetFileName(first)
                : Path.GetFileNameWithoutExtension(first);
        }
        else
        {
            baseName = Path.GetFileName(firstParent);
            if (string.IsNullOrWhiteSpace(baseName))
            {
                baseName = "Hodozip";
            }
        }

        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = "Hodozip";
        }

        return Path.Combine(firstParent, baseName + GetExtension(format));
    }

    /// <summary>既に存在する場合は「名前 (2).zip」のように連番を付けた空きパスを返す。</summary>
    public static string GetUniqueOutputPath(string desiredPath)
    {
        if (!File.Exists(desiredPath) && !Directory.Exists(desiredPath))
        {
            return desiredPath;
        }

        var directory = Path.GetDirectoryName(desiredPath) ?? Environment.CurrentDirectory;
        var name = Path.GetFileNameWithoutExtension(desiredPath);
        var extension = Path.GetExtension(desiredPath);

        for (var index = 2; index < int.MaxValue; index++)
        {
            var candidate = Path.Combine(directory, $"{name} ({index}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return desiredPath;
    }

    internal static string TrimPath(string path)
    {
        var trimmed = path.Trim().Trim('"');
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed));
        }
        catch
        {
            return trimmed;
        }
    }
}

internal static class CompressionPlanner
{
    public static IReadOnlyList<CompressionEntry> Plan(
        IReadOnlyList<string> sourcePaths,
        string outputPath)
    {
        var roots = sourcePaths
            .Select(CompressionDefaults.TrimPath)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (roots.Count == 0)
        {
            throw new ArgumentException("圧縮するファイルまたはフォルダーが指定されていません。", nameof(sourcePaths));
        }

        var outputFullPath = CompressionDefaults.TrimPath(outputPath);
        var entries = new List<CompressionEntry>();
        var usedRootNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            if (File.Exists(root))
            {
                var info = new FileInfo(root);
                if (IsReparsePoint(info.Attributes))
                {
                    continue;
                }

                var fileName = Path.GetFileName(root);
                entries.Add(new CompressionEntry(
                    root,
                    GetUniqueRootName(fileName, usedRootNames),
                    info.Length,
                    info.LastWriteTime,
                    IsDirectory: false));
                continue;
            }

            if (!Directory.Exists(root))
            {
                throw new FileNotFoundException("圧縮するファイルまたはフォルダーが見つかりません。", root);
            }

            var rootInfo = new DirectoryInfo(root);
            if (IsReparsePoint(rootInfo.Attributes))
            {
                continue;
            }

            var rootName = Path.GetFileName(root);
            if (string.IsNullOrWhiteSpace(rootName))
            {
                rootName = rootInfo.Name;
            }

            if (string.IsNullOrWhiteSpace(rootName))
            {
                rootName = "Hodozip";
            }

            rootName = GetUniqueRootName(rootName, usedRootNames);

            entries.Add(new CompressionEntry(
                root,
                rootName,
                0,
                rootInfo.LastWriteTime,
                IsDirectory: true));

            foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            {
                var info = new DirectoryInfo(directory);
                if (IsReparsePoint(info.Attributes))
                {
                    continue;
                }

                var relative = Path.GetRelativePath(root, directory);
                entries.Add(new CompressionEntry(
                    directory,
                    ToEntryPath(rootName, relative),
                    0,
                    info.LastWriteTime,
                    IsDirectory: true));
            }

            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var info = new FileInfo(file);
                if (IsReparsePoint(info.Attributes))
                {
                    continue;
                }

                if (string.Equals(info.FullName, outputFullPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var relative = Path.GetRelativePath(root, file);
                entries.Add(new CompressionEntry(
                    file,
                    ToEntryPath(rootName, relative),
                    info.Length,
                    info.LastWriteTime,
                    IsDirectory: false));
            }
        }

        return entries
            .OrderBy(x => x.EntryPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string GetUniqueRootName(string name, HashSet<string> usedNames)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "Hodozip";
        }

        if (usedNames.Add(name))
        {
            return name;
        }

        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var index = 2; index < int.MaxValue; index++)
        {
            var candidate = $"{stem} ({index}){extension}";
            if (usedNames.Add(candidate))
            {
                return candidate;
            }
        }

        return name;
    }

    private static string ToEntryPath(string rootName, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative == ".")
        {
            return rootName;
        }

        return rootName + "/" + relative.Replace('\\', '/');
    }

    private static bool IsReparsePoint(FileAttributes attributes) =>
        (attributes & FileAttributes.ReparsePoint) != 0;
}
