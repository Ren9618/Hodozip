using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using SharpCompress.Archives;
using SharpCompress.Archives.Zip;
using SharpCompress.Common;
using SharpCompress.Compressors.BZip2;
using SharpCompress.Compressors.Xz;
using SharpCompress.Compressors.ZStandard;
using SharpCompress.Readers;
using SharpCompressionMode = SharpCompress.Compressors.CompressionMode;

namespace Hodozip.Core;

/// <summary>
/// SharpCompress を使った解凍エンジン。
/// Phase 3 では ZIP に加え、7z / RAR / TAR / TAR.GZ / TAR.BZ2 / TAR.XZ / TAR.ZST /
/// 単一ストリームの GZ / BZ2 / XZ / ZST に対応する。
/// </summary>
public sealed partial class SharpCompressExtractor : IArchiveExtractor
{
    private const int MaxPasswordAttempts = 5;
    private const int CopyBufferSize = 81920;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(150);
    private static readonly EncodingNameHolder EncodingHolder = new();

    public Task<ExtractionResult> ExtractAsync(ExtractionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.Run(() => ExtractCoreAsync(request, cancellationToken), CancellationToken.None);
    }

    private static async Task<ExtractionResult> ExtractCoreAsync(ExtractionRequest request, CancellationToken cancellationToken)
    {
        var archivePath = Path.GetFullPath(request.ArchivePath);
        var baseDirectory = request.DestinationDirectory is null
            ? Path.GetDirectoryName(archivePath) ?? Environment.CurrentDirectory
            : Path.GetFullPath(request.DestinationDirectory);

        if (!LongPath.FileExists(archivePath))
        {
            return Failure(baseDirectory, ExtractionIssueKind.ArchiveError, "アーカイブが見つかりません。", archivePath, "Issue_ArchiveNotFound");
        }

        try
        {
            LongPath.CreateDirectory(baseDirectory);
        }
        catch (Exception ex)
        {
            return Failure(baseDirectory, ExtractionIssueKind.ArchiveError, $"解凍先フォルダを作成できません: {ex.Message}", archivePath, "Issue_DestinationCreateFailed", ex.Message);
        }

        var issues = new List<ExtractionIssue>();
        long archiveLength;
        try
        {
            archiveLength = new FileInfo(LongPath.ToFileSystemPath(archivePath)).Length;
        }
        catch
        {
            archiveLength = 0;
        }

        var format = ArchiveFormats.TryGetInfo(archivePath);
        string? temporaryTarPath = null;

        try
        {
            if (format is { Layout: ArchiveLayoutKind.Msi })
            {
                return await ExtractMsiAsync(
                    archivePath,
                    request,
                    baseDirectory,
                    archiveLength,
                    issues,
                    cancellationToken).ConfigureAwait(false);
            }

            if (format is { Layout: ArchiveLayoutKind.WrappedTar })
            {
                temporaryTarPath = await DecompressToTemporaryTarAsync(
                    archivePath,
                    format.Wrapper,
                    request.Options,
                    cancellationToken).ConfigureAwait(false);

                return await ExtractArchiveFileAsync(
                    temporaryTarPath,
                    archivePath,
                    request,
                    baseDirectory,
                    archiveLength,
                    issues,
                    cancellationToken).ConfigureAwait(false);
            }

            if (format is { Layout: ArchiveLayoutKind.BareCompressed })
            {
                return await ExtractBareCompressedAsync(
                    archivePath,
                    format,
                    request,
                    baseDirectory,
                    issues,
                    cancellationToken).ConfigureAwait(false);
            }

            return await ExtractArchiveFileAsync(
                archivePath,
                archivePath,
                request,
                baseDirectory,
                archiveLength,
                issues,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Cancelled(baseDirectory, issues, archivePath);
        }
        catch (Exception ex)
        {
            return Failure(baseDirectory, ExtractionIssueKind.ArchiveError, $"解凍中にエラーが発生しました: {ex.Message}", archivePath, "Issue_Unexpected", ex.Message);
        }
        finally
        {
            DeleteTemporaryDirectory(temporaryTarPath);
        }
    }

    /// <summary>
    /// ArchiveFactory で開ける形式（ZIP / 7z / RAR / TAR / GZ）を解凍する。
    /// openPath は実際に開くファイル、displayArchivePath はフォルダ名の決定に使う元のパス。
    /// </summary>
    private static async Task<ExtractionResult> ExtractArchiveFileAsync(
        string openPath,
        string displayArchivePath,
        ExtractionRequest request,
        string baseDirectory,
        long archiveLength,
        List<ExtractionIssue> issues,
        CancellationToken cancellationToken)
    {
        string? password = null;
        var passwordAttempts = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IArchive archive;
            try
            {
                archive = ArchiveFactory.OpenArchive(openPath, CreateReaderOptions(password, openPath));
            }
            catch (Exception ex) when ((password is not null || IsDefinitePasswordError(ex)) && IsPasswordError(ex))
            {
                if (passwordAttempts >= MaxPasswordAttempts)
                {
                    return Failure(baseDirectory, ExtractionIssueKind.Password, "パスワードが正しくないため解凍できませんでした。", displayArchivePath, "Issue_PasswordIncorrect");
                }

                var next = await RequestPasswordAsync(
                    request.Options,
                    passwordAttempts + 1,
                    password is null ? null : "パスワードが違います。",
                    cancellationToken).ConfigureAwait(false);
                if (next is null)
                {
                    return Cancelled(baseDirectory, issues, displayArchivePath);
                }

                password = next;
                passwordAttempts++;
                continue;
            }
            catch (Exception ex)
            {
                return Failure(baseDirectory, ExtractionIssueKind.ArchiveError, $"アーカイブを開けませんでした: {ex.Message}", displayArchivePath, "Issue_OpenFailed", ex.Message);
            }

            try
            {
                List<IArchiveEntry> entries;
                try
                {
                    entries = archive.Entries.ToList();
                }
                catch (Exception ex) when (IsPasswordError(ex))
                {
                    if (passwordAttempts >= MaxPasswordAttempts)
                    {
                        return Failure(baseDirectory, ExtractionIssueKind.Password, "パスワードが正しくないため解凍できませんでした。", displayArchivePath, "Issue_PasswordIncorrect");
                    }

                    var next = await RequestPasswordAsync(
                        request.Options,
                        passwordAttempts + 1,
                        password is null ? null : "パスワードが違います。",
                        cancellationToken).ConfigureAwait(false);
                    if (next is null)
                    {
                        return Cancelled(baseDirectory, issues, displayArchivePath);
                    }

                    password = next;
                    passwordAttempts++;
                    continue;
                }
                catch (Exception ex)
                {
                    return Failure(baseDirectory, ExtractionIssueKind.ArchiveError, $"アーカイブの内容を読み取れませんでした: {ex.Message}", displayArchivePath, "Issue_ReadFailed", ex.Message);
                }

                var firstEncrypted = entries.FirstOrDefault(x => x.IsEncrypted);
                if (firstEncrypted is not null)
                {
                    if (password is null)
                    {
                        if (passwordAttempts >= MaxPasswordAttempts)
                        {
                            return Failure(baseDirectory, ExtractionIssueKind.Password, "パスワードが必要です。", displayArchivePath, "Issue_PasswordRequired");
                        }

                        var next = await RequestPasswordAsync(request.Options, passwordAttempts + 1, null, cancellationToken)
                            .ConfigureAwait(false);
                        if (next is null)
                        {
                            return Cancelled(baseDirectory, issues, displayArchivePath);
                        }

                        password = next;
                        passwordAttempts++;
                        continue;
                    }

                    try
                    {
                        // 最初の暗号化エントリを最後まで読み、パスワードを検証する。
                        await DrainEntryAsync(firstEncrypted, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (IsPasswordError(ex) || ex is CryptographicException)
                    {
                        if (passwordAttempts >= MaxPasswordAttempts)
                        {
                            return Failure(baseDirectory, ExtractionIssueKind.Password, "パスワードが正しくないため解凍できませんでした。", displayArchivePath, "Issue_PasswordIncorrect");
                        }

                        var next = await RequestPasswordAsync(
                            request.Options,
                            passwordAttempts + 1,
                            "パスワードが違います。",
                            cancellationToken).ConfigureAwait(false);
                        if (next is null)
                        {
                            return Cancelled(baseDirectory, issues, displayArchivePath);
                        }

                        password = next;
                        passwordAttempts++;
                        continue;
                    }
                }

                return await ExtractEntriesAsync(
                    entries,
                    request,
                    displayArchivePath,
                    baseDirectory,
                    archiveLength,
                    issues,
                    parallelSourcePath: string.Equals(Path.GetExtension(openPath), ".zip", StringComparison.OrdinalIgnoreCase)
                        && entries.All(x => !x.IsEncrypted)
                            ? openPath
                            : null,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                archive.Dispose();
            }
        }
    }

    private static async Task<ExtractionResult> ExtractEntriesAsync(
        IReadOnlyList<IArchiveEntry> rawEntries,
        ExtractionRequest request,
        string displayArchivePath,
        string baseDirectory,
        long archiveLength,
        List<ExtractionIssue> issues,
        string? parallelSourcePath,
        CancellationToken cancellationToken)
    {
        var workItems = new List<EntryWork>();
        var skippedMacMetadata = 0;

        for (var index = 0; index < rawEntries.Count; index++)
        {
            var entry = rawEntries[index];
            cancellationToken.ThrowIfCancellationRequested();

            if (!PathSanitizer.TryNormalize(entry.Key, out var relativePath, out var pathError, out var pathErrorKey))
            {
                if (pathError is not null)
                {
                    issues.Add(new ExtractionIssue(
                        ExtractionIssueSeverity.Error,
                        ExtractionIssueKind.PathTraversal,
                        entry.Key ?? string.Empty,
                        pathError)
                    {
                        MessageKey = pathErrorKey,
                    });
                }

                continue;
            }

            // macOS が付ける不要ファイルは、解凍もせずにスキップ件数へ数える。
            if (request.Options.ExcludeMacOsMetadata && IsMacOsMetadata(relativePath))
            {
                skippedMacMetadata++;
                continue;
            }

            // NFD で格納された日本語ファイル名（濁点が分離した形）を NFC に直す。
            if (request.Options.NormalizeFileNamesNfc)
            {
                relativePath = relativePath.Normalize(System.Text.NormalizationForm.FormC);
            }

            var sanitized = PathSanitizer.Sanitize(relativePath);
            if (sanitized.Changed)
            {
                issues.Add(CreateIssue(
                    ExtractionIssueSeverity.Warning,
                    ExtractionIssueKind.UnsafeName,
                    entry.Key ?? string.Empty,
                    $"安全でないファイル名を修正しました: {sanitized.Note}",
                    "Issue_UnsafeNameFixed",
                    sanitized.Note));
            }

            workItems.Add(new EntryWork(
                entry.OpenEntryStream,
                sanitized.RelativePath,
                entry.IsDirectory,
                IsSymbolicLink(entry),
                Math.Max(0, entry.Size),
                entry.LastModifiedTime,
                index,
                entry.Key ?? sanitized.RelativePath));
        }

        return await ExtractWorkItemsAsync(
            workItems,
            request,
            displayArchivePath,
            baseDirectory,
            archiveLength,
            issues,
            parallelSourcePath,
            cancellationToken,
            skippedMacMetadata).ConfigureAwait(false);
    }

    private static async Task<ExtractionResult> ExtractWorkItemsAsync(
        List<EntryWork> workItems,
        ExtractionRequest request,
        string displayArchivePath,
        string baseDirectory,
        long archiveLength,
        List<ExtractionIssue> issues,
        string? parallelSourcePath,
        CancellationToken cancellationToken,
        int initialSkippedFiles = 0)
    {
        var plan = DestinationPlanner.Create(
            baseDirectory,
            archivePath: displayArchivePath,
            workItems.Select(x => new EntryPathInfo(x.RelativePath, x.IsDirectory, x.Size)).ToList(),
            request.DestinationMode,
            request.Options.FolderCollisionPolicy);

        var collisionDecision = await ResolveFolderCollisionAsync(plan, request.Options, cancellationToken)
            .ConfigureAwait(false);

        if (collisionDecision == FolderCollisionDecision.Cancel)
        {
            return Cancelled(baseDirectory, issues, displayArchivePath);
        }

        plan = ApplyFolderCollisionDecision(plan, collisionDecision);

        try
        {
            LongPath.CreateDirectory(plan.OutputRoot);
        }
        catch (Exception ex)
        {
            return Failure(baseDirectory, ExtractionIssueKind.ArchiveError, $"解凍先フォルダを作成できません: {ex.Message}", displayArchivePath, "Issue_DestinationCreateFailed", ex.Message);
        }

        var totalBytes = workItems
            .Where(x => !x.IsDirectory && !x.IsSymbolicLink)
            .Sum(x => x.Size);

        AddExpansionWarningIfNeeded(workItems, archiveLength, request.Options, issues);

        if (parallelSourcePath is not null)
        {
            var parallelism = ResolveParallelism(request.Options.MaxDegreeOfParallelism);
            var fileEntryCount = workItems.Count(x => !x.IsDirectory && !x.IsSymbolicLink);
            if (parallelism > 1
                && fileEntryCount > 1
                && !HasDuplicateFileTargets(workItems, plan)
                && CanOpenIndependentArchive(parallelSourcePath))
            {
                return await ExtractWorkItemsParallelAsync(
                    workItems,
                    request,
                    plan,
                    totalBytes,
                    issues,
                    parallelSourcePath,
                    parallelism,
                    cancellationToken,
                    initialSkippedFiles).ConfigureAwait(false);
            }
        }

        return await ExtractWorkItemsSequentialAsync(workItems, request, plan, totalBytes, issues, cancellationToken, initialSkippedFiles)
            .ConfigureAwait(false);
    }

    private static async Task<ExtractionResult> ExtractWorkItemsSequentialAsync(
        List<EntryWork> workItems,
        ExtractionRequest request,
        DestinationPlan plan,
        long totalBytes,
        List<ExtractionIssue> issues,
        CancellationToken cancellationToken,
        int initialSkippedFiles = 0)
    {
        var progress = request.Options.Progress;
        long processedBytes = 0;
        var extractedFiles = 0;
        var skippedFiles = initialSkippedFiles;
        var lastProgress = Stopwatch.StartNew();

        try
        {
            foreach (var work in workItems)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (work.IsSymbolicLink)
                {
                    issues.Add(CreateIssue(
                        ExtractionIssueSeverity.Warning,
                        ExtractionIssueKind.SymbolicLink,
                        work.RelativePath,
                        "シンボリックリンクエントリは安全のためスキップしました。",
                        "Issue_SymlinkSkipped"));
                    skippedFiles++;
                    continue;
                }

                var mappedPath = plan.MapPath(work.RelativePath);
                if (string.IsNullOrEmpty(mappedPath))
                {
                    continue;
                }

                string targetPath;
                try
                {
                    targetPath = Path.GetFullPath(Path.Combine(plan.OutputRoot, mappedPath));
                }
                catch (Exception ex)
                {
                    issues.Add(CreateIssue(
                        ExtractionIssueSeverity.Error,
                        ExtractionIssueKind.EntryFailed,
                        work.RelativePath,
                        $"出力パスを組み立てられません: {ex.Message}",
                        "Issue_OutputPathFailed",
                        ex.Message));
                    continue;
                }

                if (!PathSanitizer.IsUnderRoot(plan.OutputRoot, targetPath))
                {
                    issues.Add(CreateIssue(
                        ExtractionIssueSeverity.Error,
                        ExtractionIssueKind.PathTraversal,
                        work.RelativePath,
                        "解凍先の外へ書き込むエントリを拒否しました。",
                        "Issue_OutsideRoot"));
                    continue;
                }

                if (work.IsDirectory)
                {
                    try
                    {
                        if (LongPath.FileExists(targetPath))
                        {
                            issues.Add(CreateIssue(
                                ExtractionIssueSeverity.Error,
                                ExtractionIssueKind.EntryFailed,
                                work.RelativePath,
                                "同名のファイルが存在するためフォルダを作成できません。",
                                "Issue_FileBlocksFolder"));
                            continue;
                        }

                        LongPath.CreateDirectory(targetPath);
                    }
                    catch (Exception ex)
                    {
                        issues.Add(CreateIssue(
                            ExtractionIssueSeverity.Error,
                            ExtractionIssueKind.EntryFailed,
                            work.RelativePath,
                            $"フォルダを作成できません: {ex.Message}",
                            "Issue_CreateFolderFailed",
                            ex.Message));
                    }

                    continue;
                }

                if (LongPath.DirectoryExists(targetPath))
                {
                    issues.Add(CreateIssue(
                        ExtractionIssueSeverity.Error,
                        ExtractionIssueKind.EntryFailed,
                        work.RelativePath,
                        "同名のフォルダが存在するためファイルを書き込めません。",
                        "Issue_FolderBlocksFile"));
                    skippedFiles++;
                    continue;
                }

                var writePath = targetPath;
                var overwrite = false;

                if (LongPath.FileExists(targetPath))
                {
                    var decision = await ResolveOverwriteAsync(
                        request.Options,
                        work.RelativePath,
                        work.Size,
                        work.LastModifiedTime,
                        targetPath,
                        cancellationToken).ConfigureAwait(false);

                    if (decision == OverwriteDecision.Cancel)
                    {
                        throw new OperationCanceledException(cancellationToken);
                    }

                    if (decision == OverwriteDecision.Skip)
                    {
                        skippedFiles++;
                        continue;
                    }

                    if (decision == OverwriteDecision.AutoRename)
                    {
                        writePath = GetUniqueFilePath(targetPath);
                    }
                    else
                    {
                        overwrite = true;
                    }
                }

                var parentDirectory = Path.GetDirectoryName(writePath);
                if (string.IsNullOrEmpty(parentDirectory))
                {
                    issues.Add(CreateIssue(
                        ExtractionIssueSeverity.Error,
                        ExtractionIssueKind.EntryFailed,
                        work.RelativePath,
                        "出力先フォルダを特定できません。",
                        "Issue_OutputParentMissing"));
                    continue;
                }

                string? tempPath = null;
                try
                {
                    LongPath.CreateDirectory(parentDirectory);
                    tempPath = Path.Combine(parentDirectory, "~hodozip-" + Guid.NewGuid().ToString("N") + ".tmp");

                    progress?.Report(new ExtractionProgress(
                        processedBytes,
                        totalBytes,
                        work.RelativePath,
                        0,
                        work.Size,
                        plan.OutputRoot));

                    var copied = await WriteEntryAsync(
                        work.OpenStream,
                        tempPath,
                        bytes => processedBytes += bytes,
                        () => processedBytes,
                        totalBytes,
                        work.RelativePath,
                        work.Size,
                        progress is null ? null : progress.Report,
                        lastProgress,
                        cancellationToken).ConfigureAwait(false);

                    if (work.LastModifiedTime is { } lastModified)
                    {
                        LongPath.SetLastWriteTime(tempPath, lastModified);
                    }

                    if (overwrite && LongPath.FileExists(writePath))
                    {
                        TryClearReadOnly(writePath);
                    }

                    LongPath.MoveFile(tempPath, writePath, overwrite);
                    tempPath = null;
                    extractedFiles++;

                    if (work.LastModifiedTime is { } modified)
                    {
                        try
                        {
                            LongPath.SetLastWriteTime(writePath, modified);
                        }
                        catch
                        {
                            // 更新日時の設定失敗は解凍自体の失敗にはしない。
                        }
                    }

                    progress?.Report(new ExtractionProgress(
                        processedBytes,
                        totalBytes,
                        work.RelativePath,
                        copied,
                        work.Size));
                    lastProgress.Restart();
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    issues.Add(CreateIssue(
                        ExtractionIssueSeverity.Error,
                        ExtractionIssueKind.EntryFailed,
                        work.RelativePath,
                        $"ファイルを書き込めません: {ex.Message}",
                        "Issue_WriteFailed",
                        ex.Message));
                }
                finally
                {
                    if (tempPath is not null)
                    {
                        try
                        {
                            LongPath.DeleteFile(tempPath);
                        }
                        catch
                        {
                            // 一時ファイルの削除失敗は無視する。
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            return new ExtractionResult
            {
                Success = false,
                Cancelled = true,
                DestinationPath = plan.OutputRoot,
                ExtractedFileCount = extractedFiles,
                SkippedFileCount = skippedFiles,
                Issues = issues,
            };
        }

        progress?.Report(new ExtractionProgress(processedBytes, totalBytes, string.Empty, 0, 0, plan.OutputRoot));

        return new ExtractionResult
        {
            Success = !issues.Any(x => x.Severity == ExtractionIssueSeverity.Error),
            Cancelled = false,
            DestinationPath = plan.OutputRoot,
            ExtractedFileCount = extractedFiles,
            SkippedFileCount = skippedFiles,
            Issues = issues,
        };
    }

    /// <summary>
    /// macOS が自動生成するメタデータ（__MACOSX フォルダー、.DS_Store、._ で始まる名前）か。
    /// </summary>
    private static bool IsMacOsMetadata(string relativePath)
    {
        foreach (var segment in relativePath.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.Equals("__MACOSX", StringComparison.OrdinalIgnoreCase)
                || segment.Equals(".DS_Store", StringComparison.OrdinalIgnoreCase)
                || segment.StartsWith("._", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static int ResolveParallelism(int requested)
    {
        var maximum = Math.Max(1, Environment.ProcessorCount);
        return requested <= 0 ? maximum : Math.Clamp(requested, 1, maximum);
    }

    /// <summary>
    /// 同じ出力パスを指すエントリがあるアーカイブは、逐次処理の上書き判定順序を
    /// 再現できないため並列化しない。
    /// </summary>
    private static bool HasDuplicateFileTargets(IReadOnlyList<EntryWork> workItems, DestinationPlan plan)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var work in workItems)
        {
            if (work.IsDirectory || work.IsSymbolicLink)
            {
                continue;
            }

            var mappedPath = plan.MapPath(work.RelativePath);
            if (string.IsNullOrEmpty(mappedPath))
            {
                continue;
            }

            try
            {
                var targetPath = Path.GetFullPath(Path.Combine(plan.OutputRoot, mappedPath));
                if (!paths.Add(targetPath))
                {
                    return true;
                }
            }
            catch
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// ワーカーごとに独立した ZipArchive を開けるか確認する。
    /// 開けない場合は呼び出し元が逐次処理へフォールバックする。
    /// </summary>
    private static bool CanOpenIndependentArchive(string path)
    {
        try
        {
            using var archive = ArchiveFactory.OpenArchive(path, CreateReaderOptions(password: null, path));
            using var enumerator = archive.Entries.GetEnumerator();
            return enumerator.MoveNext();
        }
        catch
        {
            return false;
        }
    }

    private static async Task<ExtractionResult> ExtractWorkItemsParallelAsync(
        List<EntryWork> workItems,
        ExtractionRequest request,
        DestinationPlan plan,
        long totalBytes,
        List<ExtractionIssue> issues,
        string sourcePath,
        int parallelism,
        CancellationToken cancellationToken,
        int initialSkippedFiles = 0)
    {
        var options = request.Options;
        var progress = options.Progress;
        var counters = new ExtractionCounters();
        var prepared = new List<PreparedEntry>();
        var reservedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skippedFiles = initialSkippedFiles;

        // 上書き確認・フォルダ作成・Zip Slip 検査をすべてワーカー起動前に済ませる。
        // ダイアログが並列ワーカーから同時に要求されることはない。
        foreach (var work in workItems)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (work.IsSymbolicLink)
            {
                counters.AddIssue(issues, CreateIssue(
                    ExtractionIssueSeverity.Warning,
                    ExtractionIssueKind.SymbolicLink,
                    work.RelativePath,
                    "シンボリックリンクエントリは安全のためスキップしました。",
                    "Issue_SymlinkSkipped"));
                skippedFiles++;
                continue;
            }

            var mappedPath = plan.MapPath(work.RelativePath);
            if (string.IsNullOrEmpty(mappedPath))
            {
                continue;
            }

            string targetPath;
            try
            {
                targetPath = Path.GetFullPath(Path.Combine(plan.OutputRoot, mappedPath));
            }
            catch (Exception ex)
            {
                counters.AddIssue(issues, CreateIssue(
                    ExtractionIssueSeverity.Error,
                    ExtractionIssueKind.EntryFailed,
                    work.RelativePath,
                    $"出力パスを組み立てられません: {ex.Message}",
                    "Issue_OutputPathFailed",
                    ex.Message));
                continue;
            }

            if (!PathSanitizer.IsUnderRoot(plan.OutputRoot, targetPath))
            {
                counters.AddIssue(issues, CreateIssue(
                    ExtractionIssueSeverity.Error,
                    ExtractionIssueKind.PathTraversal,
                    work.RelativePath,
                    "解凍先の外へ書き込むエントリを拒否しました。",
                    "Issue_OutsideRoot"));
                continue;
            }

            if (work.IsDirectory)
            {
                try
                {
                    if (LongPath.FileExists(targetPath))
                    {
                        counters.AddIssue(issues, CreateIssue(
                            ExtractionIssueSeverity.Error,
                            ExtractionIssueKind.EntryFailed,
                            work.RelativePath,
                            "同名のファイルが存在するためフォルダを作成できません。",
                            "Issue_FileBlocksFolder"));
                        continue;
                    }

                    LongPath.CreateDirectory(targetPath);
                }
                catch (Exception ex)
                {
                    counters.AddIssue(issues, CreateIssue(
                        ExtractionIssueSeverity.Error,
                        ExtractionIssueKind.EntryFailed,
                        work.RelativePath,
                        $"フォルダを作成できません: {ex.Message}",
                        "Issue_CreateFolderFailed",
                        ex.Message));
                }

                continue;
            }

            if (LongPath.DirectoryExists(targetPath))
            {
                counters.AddIssue(issues, CreateIssue(
                    ExtractionIssueSeverity.Error,
                    ExtractionIssueKind.EntryFailed,
                    work.RelativePath,
                    "同名のフォルダが存在するためファイルを書き込めません。",
                    "Issue_FolderBlocksFile"));
                skippedFiles++;
                continue;
            }

            var writePath = targetPath;
            var overwrite = false;

            if (LongPath.FileExists(targetPath))
            {
                var decision = await ResolveOverwriteAsync(
                    options,
                    work.RelativePath,
                    work.Size,
                    work.LastModifiedTime,
                    targetPath,
                    cancellationToken).ConfigureAwait(false);

                if (decision == OverwriteDecision.Cancel)
                {
                    return Cancelled(plan.OutputRoot, issues, sourcePath);
                }

                if (decision == OverwriteDecision.Skip)
                {
                    skippedFiles++;
                    continue;
                }

                if (decision == OverwriteDecision.AutoRename)
                {
                    writePath = GetUniqueFilePath(targetPath, reservedPaths);
                }
                else
                {
                    overwrite = true;
                }
            }

            // 自動リネーム先が後続エントリの出力先とぶつかる場合は、
            // 逐次処理と同じ結果になるようフォールバックする。
            if (!reservedPaths.Add(writePath))
            {
                return await ExtractWorkItemsSequentialAsync(
                    workItems,
                    request,
                    plan,
                    totalBytes,
                    issues,
                    cancellationToken,
                    skippedFiles).ConfigureAwait(false);
            }

            var parentDirectory = Path.GetDirectoryName(writePath);
            if (string.IsNullOrEmpty(parentDirectory))
            {
                counters.AddIssue(issues, CreateIssue(
                    ExtractionIssueSeverity.Error,
                    ExtractionIssueKind.EntryFailed,
                    work.RelativePath,
                    "出力先フォルダを特定できません。",
                    "Issue_OutputParentMissing"));
                continue;
            }

            try
            {
                LongPath.CreateDirectory(parentDirectory);
            }
            catch (Exception ex)
            {
                counters.AddIssue(issues, CreateIssue(
                    ExtractionIssueSeverity.Error,
                    ExtractionIssueKind.EntryFailed,
                    work.RelativePath,
                    $"ファイルを書き込めません: {ex.Message}",
                    "Issue_WriteFailed",
                    ex.Message));
                continue;
            }

            prepared.Add(new PreparedEntry(work, writePath, overwrite));
        }

        counters.SkippedFiles = skippedFiles;

        if (prepared.Count == 0)
        {
            counters.Report(progress, new ExtractionProgress(0, totalBytes, string.Empty, 0, 0, plan.OutputRoot));
            return new ExtractionResult
            {
                Success = !issues.Any(x => x.Severity == ExtractionIssueSeverity.Error),
                Cancelled = false,
                DestinationPath = plan.OutputRoot,
                ExtractedFileCount = counters.ExtractedFiles,
                SkippedFileCount = counters.SkippedFiles,
                Issues = issues,
            };
        }

        counters.Report(progress, new ExtractionProgress(0, totalBytes, string.Empty, 0, 0, plan.OutputRoot));

        var queue = new ConcurrentQueue<PreparedEntry>(prepared);
        var workerCount = Math.Min(parallelism, prepared.Count);
        var workers = new Task[workerCount];

        for (var index = 0; index < workerCount; index++)
        {
            workers[index] = Task.Run(async () =>
            {
                IArchive workerArchive;
                try
                {
                    workerArchive = ArchiveFactory.OpenArchive(sourcePath, CreateReaderOptions(password: null, sourcePath));
                }
                catch (Exception ex)
                {
                    counters.AddIssue(issues, CreateIssue(
                        ExtractionIssueSeverity.Error,
                        ExtractionIssueKind.ArchiveError,
                        sourcePath,
                        $"アーカイブを開けませんでした: {ex.Message}",
                        "Issue_OpenFailed",
                        ex.Message));
                    return;
                }

                try
                {
                    var entries = workerArchive.Entries.ToList();
                    while (queue.TryDequeue(out var item))
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        if (item.Work.Index < 0 || item.Work.Index >= entries.Count)
                        {
                            counters.AddIssue(issues, CreateIssue(
                                ExtractionIssueSeverity.Error,
                                ExtractionIssueKind.EntryFailed,
                                item.Work.RelativePath,
                                "アーカイブのエントリを特定できませんでした。",
                                "Issue_Generic"));
                            continue;
                        }

                        var entry = entries[item.Work.Index];
                        await ExtractPreparedEntryAsync(
                            entry.OpenEntryStream,
                            item,
                            plan.OutputRoot,
                            totalBytes,
                            counters,
                            issues,
                            progress,
                            cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    // キャンセルは呼び出し元で結果へ変換する。
                }
                catch (Exception ex)
                {
                    counters.AddIssue(issues, CreateIssue(
                        ExtractionIssueSeverity.Error,
                        ExtractionIssueKind.ArchiveError,
                        sourcePath,
                        $"アーカイブの内容を読み取れませんでした: {ex.Message}",
                        "Issue_ReadFailed",
                        ex.Message));
                }
                finally
                {
                    workerArchive.Dispose();
                }
            }, CancellationToken.None);
        }

        await Task.WhenAll(workers).ConfigureAwait(false);

        if (cancellationToken.IsCancellationRequested)
        {
            issues.Add(CreateIssue(
                ExtractionIssueSeverity.Warning,
                ExtractionIssueKind.Cancelled,
                sourcePath,
                "解凍をキャンセルしました。",
                "Issue_Cancelled"));

            return new ExtractionResult
            {
                Success = false,
                Cancelled = true,
                DestinationPath = plan.OutputRoot,
                ExtractedFileCount = counters.ExtractedFiles,
                SkippedFileCount = counters.SkippedFiles,
                Issues = issues,
            };
        }

        counters.Report(progress, new ExtractionProgress(
            counters.ProcessedBytes,
            totalBytes,
            string.Empty,
            0,
            0,
            plan.OutputRoot));

        return new ExtractionResult
        {
            Success = !issues.Any(x => x.Severity == ExtractionIssueSeverity.Error),
            Cancelled = false,
            DestinationPath = plan.OutputRoot,
            ExtractedFileCount = counters.ExtractedFiles,
            SkippedFileCount = counters.SkippedFiles,
            Issues = issues,
        };
    }

    private static async Task ExtractPreparedEntryAsync(
        Func<Stream> openStream,
        PreparedEntry item,
        string outputRoot,
        long totalBytes,
        ExtractionCounters counters,
        List<ExtractionIssue> issues,
        IProgress<ExtractionProgress>? progress,
        CancellationToken cancellationToken)
    {
        var work = item.Work;
        string? tempPath = null;

        try
        {
            var parentDirectory = Path.GetDirectoryName(item.WritePath);
            if (string.IsNullOrEmpty(parentDirectory))
            {
                counters.AddIssue(issues, CreateIssue(
                    ExtractionIssueSeverity.Error,
                    ExtractionIssueKind.EntryFailed,
                    work.RelativePath,
                    "出力先フォルダを特定できません。",
                    "Issue_OutputParentMissing"));
                return;
            }

            LongPath.CreateDirectory(parentDirectory);
            tempPath = Path.Combine(parentDirectory, "~hodozip-" + Guid.NewGuid().ToString("N") + ".tmp");
            var lastProgress = Stopwatch.StartNew();

            counters.Report(progress, new ExtractionProgress(
                counters.ProcessedBytes,
                totalBytes,
                work.RelativePath,
                0,
                work.Size,
                outputRoot));

            var copied = await WriteEntryAsync(
                openStream,
                tempPath,
                counters.AddProcessed,
                counters.GetProcessed,
                totalBytes,
                work.RelativePath,
                work.Size,
                value => counters.Report(progress, value),
                lastProgress,
                cancellationToken).ConfigureAwait(false);

            if (work.LastModifiedTime is { } lastModified)
            {
                LongPath.SetLastWriteTime(tempPath, lastModified);
            }

            if (item.Overwrite && LongPath.FileExists(item.WritePath))
            {
                TryClearReadOnly(item.WritePath);
            }

            LongPath.MoveFile(tempPath, item.WritePath, item.Overwrite);
            tempPath = null;
            counters.AddExtracted();

            if (work.LastModifiedTime is { } modified)
            {
                try
                {
                    LongPath.SetLastWriteTime(item.WritePath, modified);
                }
                catch
                {
                    // 更新日時の設定失敗は解凍自体の失敗にはしない。
                }
            }

            counters.Report(progress, new ExtractionProgress(
                counters.ProcessedBytes,
                totalBytes,
                work.RelativePath,
                copied,
                work.Size));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            counters.AddIssue(issues, CreateIssue(
                ExtractionIssueSeverity.Error,
                ExtractionIssueKind.EntryFailed,
                work.RelativePath,
                $"ファイルを書き込めません: {ex.Message}",
                "Issue_WriteFailed",
                ex.Message));
        }
        finally
        {
            if (tempPath is not null)
            {
                try
                {
                    LongPath.DeleteFile(tempPath);
                }
                catch
                {
                    // 一時ファイルの削除失敗は無視する。
                }
            }
        }
    }

    /// <summary>
    /// 単一ストリーム圧縮（.bz2 / .xz / .zst）を 1 ファイルとして解凍する。
    /// </summary>
    private static async Task<ExtractionResult> ExtractBareCompressedAsync(
        string archivePath,
        ArchiveFormatInfo format,
        ExtractionRequest request,
        string baseDirectory,
        List<ExtractionIssue> issues,
        CancellationToken cancellationToken)
    {
        var outputName = ArchiveFormats.GetArchiveBaseName(archivePath);
        var sanitized = PathSanitizer.Sanitize(outputName);
        if (sanitized.Changed)
        {
            issues.Add(CreateIssue(
                ExtractionIssueSeverity.Warning,
                ExtractionIssueKind.UnsafeName,
                outputName,
                $"安全でないファイル名を修正しました: {sanitized.Note}",
                "Issue_UnsafeNameFixed",
                sanitized.Note));
        }

        var relativePath = string.IsNullOrWhiteSpace(sanitized.RelativePath) ? "output" : sanitized.RelativePath;

        string targetPath;
        try
        {
            targetPath = Path.GetFullPath(Path.Combine(baseDirectory, relativePath));
        }
        catch (Exception ex)
        {
            return Failure(baseDirectory, ExtractionIssueKind.ArchiveError, $"出力パスを組み立てられません: {ex.Message}", archivePath, "Issue_OutputPathFailed", ex.Message);
        }

        if (!PathSanitizer.IsUnderRoot(baseDirectory, targetPath))
        {
            return Failure(baseDirectory, ExtractionIssueKind.PathTraversal, "解凍先の外へ書き込むエントリを拒否しました。", archivePath, "Issue_OutsideRoot");
        }

        var writePath = targetPath;
        var overwrite = false;
        var skippedFiles = 0;

        if (LongPath.FileExists(targetPath))
        {
            var decision = await ResolveOverwriteAsync(
                request.Options,
                relativePath,
                -1,
                null,
                targetPath,
                cancellationToken).ConfigureAwait(false);

            if (decision == OverwriteDecision.Cancel)
            {
                return Cancelled(baseDirectory, issues, archivePath);
            }

            if (decision == OverwriteDecision.Skip)
            {
                skippedFiles = 1;
                return new ExtractionResult
                {
                    Success = true,
                    DestinationPath = baseDirectory,
                    ExtractedFileCount = 0,
                    SkippedFileCount = skippedFiles,
                    Issues = issues,
                };
            }

            if (decision == OverwriteDecision.AutoRename)
            {
                writePath = GetUniqueFilePath(targetPath);
            }
            else
            {
                overwrite = true;
            }
        }

        var progress = request.Options.Progress;
        progress?.Report(new ExtractionProgress(0, 0, relativePath, 0, 0, baseDirectory));

        string? tempPath = null;
        try
        {
            var parentDirectory = Path.GetDirectoryName(writePath) ?? baseDirectory;
            LongPath.CreateDirectory(parentDirectory);
            tempPath = Path.Combine(parentDirectory, "~hodozip-" + Guid.NewGuid().ToString("N") + ".tmp");

            await using (var input = File.OpenRead(LongPath.ToFileSystemPath(archivePath)))
            await using (var decompressed = CreateDecompressionStream(input, format.Wrapper))
            await using (var output = new FileStream(
                LongPath.ToFileSystemPath(tempPath),
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
                long copied = 0;
                var lastProgress = Stopwatch.StartNew();
                try
                {
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var read = await decompressed.ReadAsync(buffer.AsMemory(0, CopyBufferSize), cancellationToken).ConfigureAwait(false);
                        if (read == 0)
                        {
                            break;
                        }

                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        copied += read;

                        if (lastProgress.Elapsed >= ProgressInterval)
                        {
                            progress?.Report(new ExtractionProgress(copied, 0, relativePath, copied, 0));
                            lastProgress.Restart();
                        }
                    }

                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }

            if (overwrite && LongPath.FileExists(writePath))
            {
                TryClearReadOnly(writePath);
            }

            LongPath.MoveFile(tempPath, writePath, overwrite);
            tempPath = null;

            progress?.Report(new ExtractionProgress(0, 0, string.Empty, 0, 0, baseDirectory));

            return new ExtractionResult
            {
                Success = true,
                DestinationPath = baseDirectory,
                ExtractedFileCount = 1,
                SkippedFileCount = skippedFiles,
                Issues = issues,
            };
        }
        catch (OperationCanceledException)
        {
            return Cancelled(baseDirectory, issues, archivePath);
        }
        catch (Exception ex)
        {
            issues.Add(CreateIssue(
                ExtractionIssueSeverity.Error,
                ExtractionIssueKind.EntryFailed,
                relativePath,
                $"ファイルを書き込めません: {ex.Message}",
                "Issue_WriteFailed",
                ex.Message));

            return new ExtractionResult
            {
                Success = false,
                DestinationPath = baseDirectory,
                ExtractedFileCount = 0,
                SkippedFileCount = skippedFiles,
                Issues = issues,
            };
        }
        finally
        {
            if (tempPath is not null)
            {
                try
                {
                    LongPath.DeleteFile(tempPath);
                }
                catch
                {
                    // 一時ファイルの削除失敗は無視する。
                }
            }
        }
    }

    private static async Task<long> WriteEntryAsync(
        Func<Stream> openStream,
        string tempPath,
        Action<long> addProcessed,
        Func<long> getProcessed,
        long totalBytes,
        string currentFile,
        long currentFileTotalBytes,
        Action<ExtractionProgress>? report,
        Stopwatch lastProgress,
        CancellationToken cancellationToken)
    {
        await using var source = openStream();
        await using var destination = new FileStream(
            LongPath.ToFileSystemPath(tempPath),
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        long copied = 0;

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await source.ReadAsync(buffer.AsMemory(0, CopyBufferSize), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                copied += read;
                addProcessed(read);

                if (lastProgress.Elapsed >= ProgressInterval)
                {
                    report?.Invoke(new ExtractionProgress(
                        getProcessed(),
                        totalBytes,
                        currentFile,
                        copied,
                        currentFileTotalBytes));
                    lastProgress.Restart();
                }
            }

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return copied;
    }

    private static async Task<FolderCollisionDecision> ResolveFolderCollisionAsync(
        DestinationPlan plan,
        ExtractionOptions options,
        CancellationToken cancellationToken)
    {
        if (plan.Collision is not { } collision)
        {
            return FolderCollisionDecision.Merge;
        }

        switch (options.FolderCollisionPolicy)
        {
            case FolderCollisionPolicy.Merge:
                return FolderCollisionDecision.Merge;
            case FolderCollisionPolicy.AutoRename:
                return FolderCollisionDecision.AutoRename;
            case FolderCollisionPolicy.Ask:
            default:
                if (options.FolderCollisionResolver is null)
                {
                    // 旧バージョンとの互換性のため、指定がなければ連番フォルダを作る。
                    return FolderCollisionDecision.AutoRename;
                }

                break;
        }

        try
        {
            return await options.FolderCollisionResolver(
                new FolderCollisionConflict(
                    collision.FolderName,
                    collision.FolderPath,
                    plan.BaseDirectory,
                    collision.ExistingIsDirectory),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return FolderCollisionDecision.AutoRename;
        }
    }

    private static DestinationPlan ApplyFolderCollisionDecision(DestinationPlan plan, FolderCollisionDecision decision)
    {
        if (plan.Collision is not { } collision)
        {
            return plan;
        }

        if (decision == FolderCollisionDecision.Merge && collision.ExistingIsDirectory)
        {
            return plan;
        }

        return plan with
        {
            OutputRoot = DestinationPlanner.GetUniqueDirectoryPath(plan.BaseDirectory, collision.FolderName),
        };
    }

    private static async Task<OverwriteDecision> ResolveOverwriteAsync(
        ExtractionOptions options,
        string relativePath,
        long newSize,
        DateTime? newLastWriteTime,
        string targetPath,
        CancellationToken cancellationToken)
    {
        // 確認モードでも、既存ファイルの方が新しい場合はダイアログを出さずスキップする。
        if (options.SkipExistingIfNewer
            && options.OverwritePolicy == OverwritePolicy.Ask
            && newLastWriteTime is { } newTime)
        {
            try
            {
                var existingInfo = new FileInfo(LongPath.ToFileSystemPath(targetPath));
                if (existingInfo.Exists
                    && existingInfo.LastWriteTimeUtc > newTime.ToUniversalTime())
                {
                    return OverwriteDecision.Skip;
                }
            }
            catch
            {
                // 更新日時を読めない場合は通常の確認へ回す。
            }
        }

        switch (options.OverwritePolicy)
        {
            case OverwritePolicy.Overwrite:
                return OverwriteDecision.Overwrite;
            case OverwritePolicy.Skip:
                return OverwriteDecision.Skip;
            case OverwritePolicy.AutoRename:
                return OverwriteDecision.AutoRename;
            case OverwritePolicy.Ask:
            default:
                if (options.OverwriteResolver is null)
                {
                    return OverwriteDecision.Skip;
                }

                FileInfo existing;
                try
                {
                    existing = new FileInfo(LongPath.ToFileSystemPath(targetPath));
                }
                catch
                {
                    return OverwriteDecision.Skip;
                }

                var conflict = new OverwriteConflict(
                    relativePath,
                    targetPath,
                    existing.Exists ? existing.Length : 0,
                    newSize,
                    existing.Exists ? new DateTimeOffset(existing.LastWriteTime) : null,
                    newLastWriteTime is { } dt ? new DateTimeOffset(dt) : null);

                try
                {
                    return await options.OverwriteResolver(conflict, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    return OverwriteDecision.Skip;
                }
        }
    }

    private static async Task<string?> RequestPasswordAsync(
        ExtractionOptions options,
        int attempt,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        if (options.PasswordProvider is null)
        {
            return null;
        }

        return await options.PasswordProvider(
                new PasswordRequest(attempt, errorMessage)
                {
                    ErrorMessageKey = errorMessage is null ? null : "Issue_PasswordMismatch",
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task DrainEntryAsync(IArchiveEntry entry, CancellationToken cancellationToken)
    {
        await using var stream = entry.OpenEntryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            while (await stream.ReadAsync(buffer.AsMemory(0, CopyBufferSize), cancellationToken).ConfigureAwait(false) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static ReaderOptions CreateReaderOptions(string? password, string path)
    {
        _ = EncodingHolder;

        // Shift_JIS のフォールバックは ZIP 固有。7z / RAR / TAR などの
        // Unicode 名を CP932 で誤デコードしないよう、ZIP 以外は UTF-8 を使う。
        var isZip = string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase);

        return new ReaderOptions
        {
            Password = password,
            ArchiveEncoding = new ArchiveEncoding
            {
                Default = isZip ? EncodingHolder.ShiftJis : System.Text.Encoding.UTF8,
            },
        };
    }

    private static bool IsSymbolicLink(IArchiveEntry entry)
    {
        if (!string.IsNullOrEmpty(entry.LinkTarget))
        {
            return true;
        }

        if (entry is SharpCompress.Archives.Zip.ZipArchiveEntry zipEntry && zipEntry.Attrib is { } attributes)
        {
            var unixMode = (attributes >> 16) & 0xF000;
            if (unixMode == 0xA000)
            {
                return true;
            }
        }

        return false;
    }

    private static void AddExpansionWarningIfNeeded(
        IReadOnlyList<EntryWork> workItems,
        long archiveLength,
        ExtractionOptions options,
        List<ExtractionIssue> issues)
    {
        if (archiveLength <= 0)
        {
            return;
        }

        long totalSize;
        try
        {
            totalSize = workItems.Where(x => !x.IsDirectory).Sum(x => x.Size);
        }
        catch (OverflowException)
        {
            totalSize = long.MaxValue;
        }

        if (totalSize < options.ExpansionWarningMinBytes)
        {
            return;
        }

        var ratio = (double)totalSize / archiveLength;
        if (ratio < options.ExpansionWarningRatio)
        {
            return;
        }

        issues.Add(CreateIssue(
            ExtractionIssueSeverity.Warning,
            ExtractionIssueKind.SuspiciousExpansion,
            string.Empty,
            $"展開後サイズが圧縮サイズの {ratio:N0} 倍です。展開爆弾の可能性があります。",
            "Issue_ExpansionWarning",
            ratio));
    }

    private static string GetUniqueFilePath(string path)
    {
        return GetUniqueFilePath(path, null);
    }

    private static string GetUniqueFilePath(string path, HashSet<string>? reservedPaths)
    {
        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var fileName = Path.GetFileName(path);
        var extension = Path.GetExtension(fileName);
        var baseName = Path.GetFileNameWithoutExtension(fileName);

        if (string.IsNullOrEmpty(baseName))
        {
            baseName = fileName;
            extension = string.Empty;
        }

        for (var number = 2; number < 10000; number++)
        {
            var candidate = Path.Combine(directory, $"{baseName} ({number}){extension}");
            if (!LongPath.FileExists(candidate)
                && !LongPath.DirectoryExists(candidate)
                && reservedPaths?.Contains(candidate) != true)
            {
                return candidate;
            }
        }

        throw new IOException($"空きファイル名が見つかりません: {fileName}");
    }

    private static void TryClearReadOnly(string path)
    {
        try
        {
            var attributes = File.GetAttributes(LongPath.ToFileSystemPath(path));
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(LongPath.ToFileSystemPath(path), attributes & ~FileAttributes.ReadOnly);
            }
        }
        catch
        {
            // 失敗した場合は File.Move 側でエラーになる。
        }
    }

    private static bool IsPasswordError(Exception exception)
    {
        if (IsDefinitePasswordError(exception))
        {
            return true;
        }

        // AES 暗号化 ZIP はパスワードが違うと形式判定に失敗したように見えることがある。
        return exception.Message.Contains("Cannot determine compressed stream type", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDefinitePasswordError(Exception exception)
    {
        if (exception is CryptographicException)
        {
            return true;
        }

        var message = exception.Message;
        return message.Contains("password", StringComparison.OrdinalIgnoreCase)
            || message.Contains("encrypted", StringComparison.OrdinalIgnoreCase)
            || message.Contains("authentication", StringComparison.OrdinalIgnoreCase)
            || message.Contains("cryptographic", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> DecompressToTemporaryTarAsync(
        string archivePath,
        CompressionWrapper wrapper,
        ExtractionOptions options,
        CancellationToken cancellationToken)
    {
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "Hodozip", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        var temporaryTarPath = Path.Combine(temporaryDirectory, "archive.tar");

        try
        {
            options.Progress?.Report(new ExtractionProgress(0, 0, Path.GetFileName(archivePath), 0, 0));

            await using var input = File.OpenRead(LongPath.ToFileSystemPath(archivePath));
            await using var decompressed = CreateDecompressionStream(input, wrapper);
            await using var output = new FileStream(
                LongPath.ToFileSystemPath(temporaryTarPath),
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            await decompressed.CopyToAsync(output, CopyBufferSize, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            return temporaryTarPath;
        }
        catch
        {
            DeleteTemporaryDirectory(temporaryTarPath);
            throw;
        }
    }

    private static Stream CreateDecompressionStream(Stream input, CompressionWrapper wrapper)
    {
        return wrapper switch
        {
            CompressionWrapper.GZip => new GZipStream(input, System.IO.Compression.CompressionMode.Decompress, leaveOpen: false),
            CompressionWrapper.BZip2 => BZip2Stream.Create(
                input,
                SharpCompressionMode.Decompress,
                decompressConcatenated: true,
                leaveOpen: false,
                tolerateTruncatedStream: false),
            CompressionWrapper.Xz => new XZStream(input),
            CompressionWrapper.Zstd => new DecompressionStream(input, 0, true, false),
            _ => throw new InvalidOperationException("圧縮方式が指定されていません。"),
        };
    }

    private static void DeleteTemporaryDirectory(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return;
        }

        var directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        DeleteDirectoryQuietly(directory);
    }

    private static void DeleteDirectoryQuietly(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // 一時フォルダの削除失敗は無視する。
        }
    }

    private static ExtractionIssue CreateIssue(
        ExtractionIssueSeverity severity,
        ExtractionIssueKind kind,
        string entryPath,
        string fallbackMessage,
        string messageKey,
        params object?[] arguments)
    {
        return new ExtractionIssue(severity, kind, entryPath, fallbackMessage)
        {
            MessageKey = messageKey,
            MessageArguments = arguments,
        };
    }

    private static ExtractionResult Failure(
        string destinationPath,
        ExtractionIssueKind kind,
        string message,
        string entryPath,
        string? messageKey = null,
        params object?[] arguments)
    {
        return new ExtractionResult
        {
            Success = false,
            Cancelled = false,
            DestinationPath = destinationPath,
            ExtractedFileCount = 0,
            SkippedFileCount = 0,
            Issues = new[]
            {
                new ExtractionIssue(ExtractionIssueSeverity.Error, kind, entryPath, message)
                {
                    MessageKey = messageKey,
                    MessageArguments = arguments,
                },
            },
        };
    }

    private static ExtractionResult Cancelled(string destinationPath, List<ExtractionIssue> issues, string archivePath)
    {
        issues.Add(CreateIssue(
            ExtractionIssueSeverity.Warning,
            ExtractionIssueKind.Cancelled,
            archivePath,
            "解凍をキャンセルしました。",
            "Issue_Cancelled"));

        return new ExtractionResult
        {
            Success = false,
            Cancelled = true,
            DestinationPath = destinationPath,
            ExtractedFileCount = 0,
            SkippedFileCount = 0,
            Issues = issues,
        };
    }

    private sealed record EntryWork(
        Func<Stream> OpenStream,
        string RelativePath,
        bool IsDirectory,
        bool IsSymbolicLink,
        long Size,
        DateTime? LastModifiedTime,
        int Index,
        string EntryKey);

    private sealed record PreparedEntry(EntryWork Work, string WritePath, bool Overwrite);

    /// <summary>並列ワーカー間で共有する進捗・件数・問題リスト。</summary>
    private sealed class ExtractionCounters
    {
        private long _processedBytes;
        private int _extractedFiles;
        private int _skippedFiles;
        private readonly object _progressLock = new();
        private readonly object _issuesLock = new();

        public long ProcessedBytes => Interlocked.Read(ref _processedBytes);

        public int ExtractedFiles => Volatile.Read(ref _extractedFiles);

        public int SkippedFiles
        {
            get => Volatile.Read(ref _skippedFiles);
            set => Volatile.Write(ref _skippedFiles, value);
        }

        public void AddProcessed(long bytes) => Interlocked.Add(ref _processedBytes, bytes);

        public long GetProcessed() => Interlocked.Read(ref _processedBytes);

        public void AddExtracted() => Interlocked.Increment(ref _extractedFiles);

        public void Report(IProgress<ExtractionProgress>? progress, ExtractionProgress value)
        {
            if (progress is null)
            {
                return;
            }

            // IProgress<T> の実装はスレッドセーフとは限らないため直列化する。
            lock (_progressLock)
            {
                progress.Report(value);
            }
        }

        public void AddIssue(List<ExtractionIssue> issues, ExtractionIssue issue)
        {
            lock (_issuesLock)
            {
                issues.Add(issue);
            }
        }
    }

    private sealed class EncodingNameHolder
    {
        public EncodingNameHolder()
        {
            EncodingSetup.EnsureRegistered();
            ShiftJis = System.Text.Encoding.GetEncoding(932);
        }

        public System.Text.Encoding ShiftJis { get; }
    }
}
