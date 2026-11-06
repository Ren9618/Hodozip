namespace Hodozip.Core;

internal sealed record EntryPathInfo(string RelativePath, bool IsDirectory, long Size);

internal enum DestinationLayout
{
    SingleFile,
    SingleTopFolder,
    Scattered,
}

internal sealed record FolderCollisionInfo(
    string FolderName,
    string FolderPath,
    bool ExistingIsDirectory);

internal sealed record DestinationPlan(
    DestinationLayout Layout,
    string BaseDirectory,
    string OutputRoot,
    string? TopFolderName,
    bool CreateOutputRoot,
    FolderCollisionInfo? Collision = null)
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
        ExtractionDestinationMode mode = ExtractionDestinationMode.Auto,
        FolderCollisionPolicy collisionPolicy = FolderCollisionPolicy.Ask)
    {
        if (mode == ExtractionDestinationMode.ForceArchiveFolder)
        {
            return CreateWithRoot(
                DestinationLayout.Scattered,
                baseDirectory,
                topFolderName: null,
                GetArchiveFolderName(archivePath),
                collisionPolicy);
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
            return CreateWithRoot(
                DestinationLayout.SingleTopFolder,
                baseDirectory,
                topFolder,
                topFolder,
                collisionPolicy);
        }

        // バラバラ: アーカイブ名（拡張子なし）のフォルダを作る。
        return CreateWithRoot(
            DestinationLayout.Scattered,
            baseDirectory,
            topFolderName: null,
            GetArchiveFolderName(archivePath),
            collisionPolicy);
    }

    private static DestinationPlan CreateWithRoot(
        DestinationLayout layout,
        string baseDirectory,
        string? topFolderName,
        string folderName,
        FolderCollisionPolicy collisionPolicy)
    {
        var intendedPath = Path.Combine(baseDirectory, folderName);

        if (!LongPath.FileExists(intendedPath) && !LongPath.DirectoryExists(intendedPath))
        {
            return new DestinationPlan(
                layout,
                baseDirectory,
                intendedPath,
                topFolderName,
                CreateOutputRoot: true);
        }

        var existingIsDirectory = LongPath.DirectoryExists(intendedPath);

        // 既存フォルダへの統合。既存がファイルの場合は統合できないため連番へ回す。
        if (collisionPolicy == FolderCollisionPolicy.Merge && existingIsDirectory)
        {
            return new DestinationPlan(
                layout,
                baseDirectory,
                intendedPath,
                topFolderName,
                CreateOutputRoot: true);
        }

        if (collisionPolicy == FolderCollisionPolicy.Ask)
        {
            return new DestinationPlan(
                layout,
                baseDirectory,
                intendedPath,
                topFolderName,
                CreateOutputRoot: true,
                new FolderCollisionInfo(folderName, intendedPath, existingIsDirectory));
        }

        return new DestinationPlan(
            layout,
            baseDirectory,
            GetUniqueDirectoryPath(baseDirectory, folderName),
            topFolderName,
            CreateOutputRoot: true);
    }

    internal static string GetUniqueDirectoryPath(string baseDirectory, string name)
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

    private static string GetArchiveFolderName(string archivePath)
    {
        var archiveName = ArchiveFormats.GetArchiveBaseName(archivePath);
        return PathSanitizer.Sanitize(archiveName).RelativePath;
    }

    private static string GetTopSegment(string relativePath)
    {
        var index = relativePath.IndexOf('\\');
        return index < 0 ? relativePath : relativePath[..index];
    }
}
