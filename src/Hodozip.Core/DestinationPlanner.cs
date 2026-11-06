namespace Hodozip.Core;

internal sealed record EntryPathInfo(string RelativePath, bool IsDirectory, long Size);

internal enum DestinationLayout
{
    SingleFile,
    SingleTopFolder,
    Scattered,
}

internal sealed record DestinationPlan(
    DestinationLayout Layout,
    string BaseDirectory,
    string OutputRoot,
    string? TopFolderName,
    bool CreateOutputRoot)
{
    public string MapPath(string relativePath)
    {
        if (Layout != DestinationLayout.SingleTopFolder || string.IsNullOrEmpty(TopFolderName))
        {
            return relativePath;
        }

        if (string.Equals(relativePath, TopFolderName, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        var prefix = TopFolderName + "\\";
        return relativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? relativePath[prefix.Length..]
            : relativePath;
    }
}

internal static class DestinationPlanner
{
    public static DestinationPlan Create(
        string baseDirectory,
        string archivePath,
        IReadOnlyList<EntryPathInfo> entries,
        ExtractionDestinationMode mode = ExtractionDestinationMode.Auto)
    {
        if (mode == ExtractionDestinationMode.ForceArchiveFolder)
        {
            var archiveName = GetArchiveFolderName(archivePath);
            var forcedRoot = GetUniqueDirectoryPath(baseDirectory, archiveName);
            return new DestinationPlan(
                DestinationLayout.Scattered,
                baseDirectory,
                forcedRoot,
                TopFolderName: null,
                CreateOutputRoot: true);
        }

        var fileEntries = entries.Where(x => !x.IsDirectory).ToList();

        // 単一ファイルのみ: フォルダを作らず基準フォルダへそのまま出す。
        // ただしフォルダ内に 1 ファイルだけある場合は、単一トップフォルダの規則を優先する。
        if (fileEntries.Count == 1 && !fileEntries[0].RelativePath.Contains('\\'))
        {
            return new DestinationPlan(
                DestinationLayout.SingleFile,
                baseDirectory,
                baseDirectory,
                TopFolderName: null,
                CreateOutputRoot: false);
        }

        var topFolders = entries
            .Select(x => GetTopSegment(x.RelativePath))
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var allUnderSingleTop = topFolders.Count == 1
            && fileEntries.All(x => x.RelativePath.Contains('\\'));

        // 全エントリが単一のトップフォルダ配下: そのフォルダごと出す（二重フォルダにしない）。
        if (allUnderSingleTop && topFolders[0].Length > 0)
        {
            var topFolder = topFolders[0];
            var outputRoot = GetUniqueDirectoryPath(baseDirectory, topFolder);
            return new DestinationPlan(
                DestinationLayout.SingleTopFolder,
                baseDirectory,
                outputRoot,
                topFolder,
                CreateOutputRoot: true);
        }

        // バラバラ: アーカイブ名（拡張子なし）のフォルダを作る。
        var scatteredRoot = GetUniqueDirectoryPath(baseDirectory, GetArchiveFolderName(archivePath));
        return new DestinationPlan(
            DestinationLayout.Scattered,
            baseDirectory,
            scatteredRoot,
            TopFolderName: null,
            CreateOutputRoot: true);
    }

    private static string GetArchiveFolderName(string archivePath)
    {
        var archiveName = Path.GetFileNameWithoutExtension(archivePath);
        if (string.IsNullOrWhiteSpace(archiveName))
        {
            archiveName = "Hodozip";
        }

        return PathSanitizer.Sanitize(archiveName).RelativePath;
    }

    private static string GetTopSegment(string relativePath)
    {
        var index = relativePath.IndexOf('\\');
        return index < 0 ? relativePath : relativePath[..index];
    }

    private static string GetUniqueDirectoryPath(string baseDirectory, string name)
    {
        var candidate = Path.Combine(baseDirectory, name);
        if (!LongPath.FileExists(candidate) && !LongPath.DirectoryExists(candidate))
        {
            return candidate;
        }

        for (var number = 2; number < 10000; number++)
        {
            var numbered = Path.Combine(baseDirectory, $"{name} ({number})");
            if (!LongPath.FileExists(numbered) && !LongPath.DirectoryExists(numbered))
            {
                return numbered;
            }
        }

        throw new IOException($"空きフォルダ名が見つかりません: {name}");
    }
}
