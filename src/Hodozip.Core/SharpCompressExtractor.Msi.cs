using System.Diagnostics;

namespace Hodozip.Core;

public sealed partial class SharpCompressExtractor
{
    // MSI の Directory テーブルで使われる標準フォルダーの表示名。
    // DefaultDir は作成元のソース フォルダー名になっていることがあり、
    // そのまま使うと PFiles64 のような分かりにくい階層になるため置き換える。
    private static readonly Dictionary<string, string> MsiStandardDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ProgramFilesFolder"] = "Program Files (x86)",
        ["ProgramFiles64Folder"] = "Program Files",
        ["CommonFilesFolder"] = "Common Files",
        ["CommonFiles64Folder"] = "Common Files",
        ["ProgramMenuFolder"] = "Start Menu\\Programs",
        ["CommonProgramsFolder"] = "Start Menu\\Programs",
        ["StartMenuFolder"] = "Start Menu",
        ["StartupFolder"] = "Start Menu\\Programs\\Startup",
        ["DesktopFolder"] = "Desktop",
        ["AppDataFolder"] = "AppData\\Roaming",
        ["LocalAppDataFolder"] = "AppData\\Local",
        ["CommonAppDataFolder"] = "ProgramData",
        ["WindowsFolder"] = "Windows",
        ["SystemFolder"] = "Windows\\System32",
        ["System64Folder"] = "Windows\\System32",
        ["FontsFolder"] = "Windows\\Fonts",
        ["TempFolder"] = "Temp",
        ["UserProfileFolder"] = "Users",
        ["PersonalFolder"] = "Documents",
        ["MyPicturesFolder"] = "Pictures",
        ["MyMusicFolder"] = "Music",
        ["MyVideosFolder"] = "Videos",
    };

    private static async Task<ExtractionResult> ExtractMsiAsync(
        string archivePath,
        ExtractionRequest request,
        string baseDirectory,
        long archiveLength,
        List<ExtractionIssue> issues,
        CancellationToken cancellationToken)
    {
        var fullArchivePath = Path.GetFullPath(archivePath);
        var msiDirectory = Path.GetDirectoryName(fullArchivePath) ?? Environment.CurrentDirectory;
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "Hodozip", Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(temporaryDirectory);

            using var database = MsiArchiveReader.Open(fullArchivePath);

            var files = database.ReadFiles();
            if (files.Count == 0)
            {
                issues.Add(new ExtractionIssue(
                    ExtractionIssueSeverity.Error,
                    ExtractionIssueKind.ArchiveError,
                    archivePath,
                    "MSI にファイルが含まれていません。"));
                return CreateMsiResult(baseDirectory, issues);
            }

            var media = database.ReadMedia()
                .OrderBy(x => x.LastSequence)
                .ToList();
            var components = database.ReadComponents()
                .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
            var directoryResolver = MsiDirectoryPathResolver.Create(database.ReadDirectories());
            var streamNames = CreateStreamNameMap(database.ReadStreamNames());

            var cabinetCache = new Dictionary<string, Task<MsiCabinetResult>>(StringComparer.OrdinalIgnoreCase);
            var reportedCabinetErrors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var workItems = new List<EntryWork>();
            var progress = request.Options.Progress;
            var streamIndex = 0;

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var mediaRow = FindMsiMedia(media, file.Sequence);
                MsiCabinetResult? cabinetResult = null;

                if (!string.IsNullOrWhiteSpace(mediaRow?.Cabinet))
                {
                    var cabinetValue = mediaRow!.Cabinet!;
                    if (!cabinetCache.TryGetValue(cabinetValue, out var cabinetTask))
                    {
                        cabinetTask = ExtractMsiCabinetAsync(
                            cabinetValue,
                            msiDirectory,
                            temporaryDirectory,
                            cabinetCache.Count,
                            database,
                            streamNames,
                            progress,
                            cancellationToken);

                        cabinetCache[cabinetValue] = cabinetTask;
                    }

                    cabinetResult = await cabinetTask.ConfigureAwait(false);

                    if (cabinetResult.DirectoryPath is null && reportedCabinetErrors.Add(cabinetValue))
                    {
                        issues.Add(new ExtractionIssue(
                            ExtractionIssueSeverity.Error,
                            ExtractionIssueKind.EntryFailed,
                            cabinetValue,
                            cabinetResult.Error ?? "MSI のキャビネットを展開できませんでした。"));
                    }
                }

                var extractedPath = cabinetResult?.DirectoryPath is { } cabinetDirectory
                    ? FindMsiCabinetFile(cabinetResult.FilesByName, file)
                    : null;

                if (extractedPath is null)
                {
                    try
                    {
                        extractedPath = TryExtractMsiStream(
                            database,
                            file,
                            streamNames,
                            Path.Combine(temporaryDirectory, "streams"),
                            streamIndex++,
                            cancellationToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        issues.Add(new ExtractionIssue(
                            ExtractionIssueSeverity.Error,
                            ExtractionIssueKind.EntryFailed,
                            DisplayMsiFileName(file),
                            $"MSI のファイルを読み取れませんでした: {ex.Message}"));
                    }
                }

                if (extractedPath is null)
                {
                    issues.Add(new ExtractionIssue(
                        ExtractionIssueSeverity.Error,
                        ExtractionIssueKind.EntryFailed,
                        DisplayMsiFileName(file),
                        "MSI 内にファイルが見つかりません（外部ソースの可能性があります）。"));
                    continue;
                }

                var component = file.Component is not null
                    && components.TryGetValue(file.Component, out var foundComponent)
                        ? foundComponent
                        : null;

                var directoryPath = directoryResolver.Resolve(component?.Directory);
                var fileName = ParseMsiName(file.FileName, file.Key, longName: true);
                var relativePath = CombineRelativePath(directoryPath, fileName);

                if (!PathSanitizer.TryNormalize(relativePath, out var normalizedPath, out var pathError))
                {
                    issues.Add(new ExtractionIssue(
                        ExtractionIssueSeverity.Error,
                        ExtractionIssueKind.PathTraversal,
                        DisplayMsiFileName(file),
                        pathError ?? "MSI のファイル パスが不正です。"));
                    continue;
                }

                var sanitized = PathSanitizer.Sanitize(normalizedPath);
                if (sanitized.Changed)
                {
                    issues.Add(new ExtractionIssue(
                        ExtractionIssueSeverity.Warning,
                        ExtractionIssueKind.UnsafeName,
                        DisplayMsiFileName(file),
                        $"安全でないファイル名を修正しました: {sanitized.Note}"));
                }

                var capturedPath = extractedPath;
                var fileSize = file.Size > 0 ? file.Size : GetMsiFileLength(capturedPath);
                var lastWriteTime = GetMsiLastWriteTime(capturedPath);

                workItems.Add(new EntryWork(
                    () => new FileStream(
                        LongPath.ToFileSystemPath(capturedPath),
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        CopyBufferSize,
                        FileOptions.SequentialScan),
                    sanitized.RelativePath,
                    IsDirectory: false,
                    IsSymbolicLink: false,
                    fileSize,
                    lastWriteTime));
            }

            if (workItems.Count == 0)
            {
                return CreateMsiResult(baseDirectory, issues);
            }

            return await ExtractWorkItemsAsync(
                workItems,
                request,
                archivePath,
                baseDirectory,
                archiveLength,
                issues,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            issues.Add(new ExtractionIssue(
                ExtractionIssueSeverity.Error,
                ExtractionIssueKind.ArchiveError,
                archivePath,
                $"MSI を解凍できませんでした: {ex.Message}"));
            return CreateMsiResult(baseDirectory, issues);
        }
        finally
        {
            DeleteDirectoryQuietly(temporaryDirectory);
        }
    }

    private static ExtractionResult CreateMsiResult(string destinationPath, List<ExtractionIssue> issues)
    {
        return new ExtractionResult
        {
            Success = false,
            Cancelled = false,
            DestinationPath = destinationPath,
            ExtractedFileCount = 0,
            SkippedFileCount = 0,
            Issues = issues,
        };
    }

    private static MsiMediaRecord? FindMsiMedia(IReadOnlyList<MsiMediaRecord> media, int sequence)
    {
        if (media.Count == 0)
        {
            return null;
        }

        MsiMediaRecord? best = null;
        foreach (var row in media)
        {
            if (row.LastSequence >= sequence
                && (best is null || row.LastSequence < best.LastSequence))
            {
                best = row;
            }
        }

        return best ?? media[^1];
    }

    private static async Task<MsiCabinetResult> ExtractMsiCabinetAsync(
        string cabinetValue,
        string msiDirectory,
        string temporaryDirectory,
        int index,
        MsiArchiveReader database,
        IReadOnlyDictionary<string, string> streamNames,
        IProgress<ExtractionProgress>? progress,
        CancellationToken cancellationToken)
    {
        string cabinetPath;
        string cabinetName;

        if (cabinetValue.StartsWith('#'))
        {
            cabinetName = cabinetValue[1..];
            if (!streamNames.TryGetValue(cabinetName, out var actualStreamName))
            {
                return new MsiCabinetResult(null, null, $"埋め込みキャビネットが見つかりません: {cabinetName}");
            }

            cabinetPath = Path.Combine(temporaryDirectory, $"cab{index}.cab");
            try
            {
                if (!database.TryExportStream(actualStreamName, cabinetPath, cancellationToken))
                {
                    return new MsiCabinetResult(null, null, $"埋め込みキャビネットを読み取れません: {cabinetName}");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new MsiCabinetResult(null, null, $"埋め込みキャビネットを展開できません: {ex.Message}");
            }
        }
        else
        {
            cabinetName = Path.GetFileName(cabinetValue);
            var candidate = Path.GetFullPath(Path.Combine(msiDirectory, cabinetValue));
            if (!PathSanitizer.IsUnderRoot(msiDirectory, candidate))
            {
                return new MsiCabinetResult(null, null, $"外部キャビネットのパスが不正です: {cabinetValue}");
            }

            if (!LongPath.FileExists(candidate))
            {
                var fallback = Path.Combine(msiDirectory, cabinetName);
                if (!LongPath.FileExists(fallback))
                {
                    return new MsiCabinetResult(null, null, $"外部キャビネットが見つかりません: {cabinetValue}");
                }

                candidate = fallback;
            }

            cabinetPath = candidate;
        }

        var destinationDirectory = Path.Combine(temporaryDirectory, $"cab{index}");
        try
        {
            Directory.CreateDirectory(destinationDirectory);
            progress?.Report(new ExtractionProgress(0, 0, cabinetName, 0, 0));
            await ExpandCabinetAsync(cabinetPath, destinationDirectory, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new MsiCabinetResult(null, null, $"キャビネットを展開できません: {ex.Message}");
        }

        var filesByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var filePath in Directory.EnumerateFiles(
                LongPath.ToFileSystemPath(destinationDirectory),
                "*",
                SearchOption.AllDirectories))
            {
                var fileName = Path.GetFileName(filePath);
                if (!filesByName.ContainsKey(fileName))
                {
                    filesByName[fileName] = filePath;
                }
            }
        }
        catch (Exception ex)
        {
            return new MsiCabinetResult(null, null, $"キャビネットの内容を列挙できません: {ex.Message}");
        }

        if (filesByName.Count == 0)
        {
            return new MsiCabinetResult(null, null, "キャビネットからファイルを展開できませんでした。");
        }

        return new MsiCabinetResult(destinationDirectory, filesByName, null);
    }

    private static async Task ExpandCabinetAsync(
        string cabinetPath,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        var expandPath = Path.Combine(Environment.SystemDirectory, "expand.exe");
        if (!File.Exists(expandPath))
        {
            expandPath = "expand.exe";
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = expandPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        startInfo.ArgumentList.Add("-F:*");
        startInfo.ArgumentList.Add(LongPath.ToFileSystemPath(cabinetPath));
        startInfo.ArgumentList.Add(LongPath.ToFileSystemPath(destinationDirectory));

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("expand.exe を起動できませんでした。");

        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // キャンセル時の終了失敗は無視する。
            }

            throw;
        }

        var output = await standardOutput.ConfigureAwait(false);
        var error = await standardError.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(error) ? output : error;
            detail = detail.Trim();
            throw new IOException(
                string.IsNullOrEmpty(detail)
                    ? $"expand.exe が終了コード {process.ExitCode} で失敗しました。"
                    : $"expand.exe が終了コード {process.ExitCode} で失敗しました: {detail}");
        }
    }

    private static string? FindMsiCabinetFile(
        IReadOnlyDictionary<string, string>? filesByName,
        MsiFileRecord file)
    {
        if (filesByName is null || filesByName.Count == 0)
        {
            return null;
        }

        foreach (var candidate in GetMsiNameCandidates(file))
        {
            var fileName = Path.GetFileName(candidate);
            if (!string.IsNullOrWhiteSpace(fileName)
                && filesByName.TryGetValue(fileName, out var path))
            {
                return path;
            }
        }

        // expand.exe は 1 ファイルだけのキャビネットを展開すると、内部名ではなく
        // キャビネット名で出力することがある。サイズが一致する場合だけ読み替える。
        if (filesByName.Count == 1)
        {
            var onlyPath = filesByName.Values.First();
            if (file.Size <= 0 || GetMsiFileLength(onlyPath) == file.Size)
            {
                return onlyPath;
            }
        }

        return null;
    }

    private static string? TryExtractMsiStream(
        MsiArchiveReader database,
        MsiFileRecord file,
        IReadOnlyDictionary<string, string> streamNames,
        string streamsDirectory,
        int index,
        CancellationToken cancellationToken)
    {
        foreach (var candidate in GetMsiNameCandidates(file))
        {
            if (!streamNames.TryGetValue(candidate, out var actualStreamName))
            {
                continue;
            }

            Directory.CreateDirectory(streamsDirectory);
            var destinationPath = Path.Combine(streamsDirectory, $"file{index}");
            return database.TryExportStream(actualStreamName, destinationPath, cancellationToken)
                ? destinationPath
                : null;
        }

        return null;
    }

    private static IEnumerable<string> GetMsiNameCandidates(MsiFileRecord file)
    {
        yield return file.Key;

        var shortName = ParseMsiName(file.FileName, file.Key, longName: false);
        if (!string.Equals(shortName, file.Key, StringComparison.OrdinalIgnoreCase))
        {
            yield return shortName;
        }

        var longName = ParseMsiName(file.FileName, file.Key, longName: true);
        if (!string.Equals(longName, file.Key, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(longName, shortName, StringComparison.OrdinalIgnoreCase))
        {
            yield return longName;
        }
    }

    private static Dictionary<string, string> CreateStreamNameMap(IReadOnlyList<string> streamNames)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in streamNames)
        {
            map.TryAdd(name, name);
        }

        return map;
    }

    private static string DisplayMsiFileName(MsiFileRecord file)
    {
        return string.IsNullOrWhiteSpace(file.FileName) ? file.Key : file.FileName!;
    }

    private static string ParseMsiName(string? value, string fallback, bool longName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var target = value;
        var colonIndex = target.IndexOf(':');
        if (colonIndex >= 0)
        {
            target = target[..colonIndex];
        }

        var pipeIndex = target.IndexOf('|');
        if (pipeIndex >= 0)
        {
            target = longName
                ? target[(pipeIndex + 1)..]
                : target[..pipeIndex];
        }

        target = target.Trim();
        return string.IsNullOrEmpty(target) || target == "." ? fallback : target;
    }

    private static string CombineRelativePath(string directoryPath, string fileName)
    {
        if (string.IsNullOrEmpty(directoryPath))
        {
            return fileName;
        }

        if (string.IsNullOrEmpty(fileName))
        {
            return directoryPath;
        }

        return directoryPath.TrimEnd('\\') + "\\" + fileName.TrimStart('\\');
    }

    private static long GetMsiFileLength(string path)
    {
        try
        {
            return new FileInfo(LongPath.ToFileSystemPath(path)).Length;
        }
        catch
        {
            return 0;
        }
    }

    private static DateTime? GetMsiLastWriteTime(string path)
    {
        try
        {
            return File.GetLastWriteTime(LongPath.ToFileSystemPath(path));
        }
        catch
        {
            return null;
        }
    }

    private sealed record MsiCabinetResult(
        string? DirectoryPath,
        IReadOnlyDictionary<string, string>? FilesByName,
        string? Error);

    private sealed class MsiDirectoryPathResolver
    {
        private readonly Dictionary<string, MsiDirectoryRecord> _directories;
        private readonly Dictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);

        private MsiDirectoryPathResolver(IReadOnlyList<MsiDirectoryRecord> directories)
        {
            _directories = new Dictionary<string, MsiDirectoryRecord>(StringComparer.OrdinalIgnoreCase);
            foreach (var directory in directories)
            {
                _directories[directory.Key] = directory;
            }
        }

        public static MsiDirectoryPathResolver Create(IReadOnlyList<MsiDirectoryRecord> directories)
        {
            return new MsiDirectoryPathResolver(directories);
        }

        public string Resolve(string? key)
        {
            if (string.IsNullOrWhiteSpace(key) || key == ".")
            {
                return string.Empty;
            }

            return ResolveCore(key, new HashSet<string>(StringComparer.OrdinalIgnoreCase), depth: 0);
        }

        private string ResolveCore(string key, HashSet<string> visiting, int depth)
        {
            if (depth > 128)
            {
                throw new InvalidDataException("MSI の Directory テーブルが深すぎます。");
            }

            if (_cache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            if (!_directories.TryGetValue(key, out var directory))
            {
                return string.Empty;
            }

            if (!visiting.Add(key))
            {
                throw new InvalidDataException($"MSI の Directory テーブルに循環参照があります: {key}");
            }

            try
            {
                var parentPath = string.IsNullOrWhiteSpace(directory.Parent)
                    ? string.Empty
                    : ResolveCore(directory.Parent, visiting, depth + 1);

                var directoryName = GetDirectoryName(key, directory.DefaultDir);
                var result = CombineRelativePath(parentPath, directoryName);
                _cache[key] = result;
                return result;
            }
            finally
            {
                visiting.Remove(key);
            }
        }

        private static string GetDirectoryName(string key, string? defaultDir)
        {
            if (key.Equals("TARGETDIR", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            if (MsiStandardDirectories.TryGetValue(key, out var standardName))
            {
                return standardName;
            }

            var name = ParseMsiName(defaultDir, key, longName: true);
            return name == "." ? string.Empty : name;
        }
    }
}
