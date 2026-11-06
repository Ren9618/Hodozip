using System.Buffers;
using System.Diagnostics;
using SharpCompress.Archives;
using SharpCompress.Archives.Zip;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace Hodozip.Core;

/// <summary>
/// SharpCompress を使った解凍エンジン。Phase 1 の対応形式は ZIP。
/// </summary>
public sealed class SharpCompressExtractor : IArchiveExtractor
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
            return Failure(baseDirectory, ExtractionIssueKind.ArchiveError, "アーカイブが見つかりません。", archivePath);
        }

        try
        {
            LongPath.CreateDirectory(baseDirectory);
        }
        catch (Exception ex)
        {
            return Failure(baseDirectory, ExtractionIssueKind.ArchiveError, $"解凍先フォルダを作成できません: {ex.Message}", archivePath);
        }

        var issues = new List<ExtractionIssue>();
        long archiveLength;
        try
        {
            archiveLength = new FileInfo(archivePath).Length;
        }
        catch
        {
            archiveLength = 0;
        }

        IArchive? archive = null;
        string? password = null;
        var passwordAttempts = 0;

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    archive = ArchiveFactory.OpenArchive(archivePath, CreateReaderOptions(password));
                }
                catch (Exception ex) when (password is not null && IsPasswordError(ex))
                {
                    if (passwordAttempts >= MaxPasswordAttempts)
                    {
                        return Failure(baseDirectory, ExtractionIssueKind.Password, "パスワードが正しくないため解凍できませんでした。", archivePath);
                    }

                    var next = await RequestPasswordAsync(request.Options, passwordAttempts + 1, "パスワードが違います。", cancellationToken)
                        .ConfigureAwait(false);
                    if (next is null)
                    {
                        return Cancelled(baseDirectory, issues, archivePath);
                    }

                    password = next;
                    passwordAttempts++;
                    continue;
                }
                catch (Exception ex)
                {
                    return Failure(baseDirectory, ExtractionIssueKind.ArchiveError, $"アーカイブを開けませんでした: {ex.Message}", archivePath);
                }

                List<IArchiveEntry> entries;
                try
                {
                    entries = archive.Entries.ToList();
                }
                catch (Exception ex)
                {
                    archive.Dispose();
                    return Failure(baseDirectory, ExtractionIssueKind.ArchiveError, $"アーカイブの内容を読み取れませんでした: {ex.Message}", archivePath);
                }

                var firstEncrypted = entries.FirstOrDefault(x => x.IsEncrypted);
                if (firstEncrypted is null)
                {
                    return await ExtractEntriesAsync(archive, entries, request, baseDirectory, archiveLength, issues, cancellationToken)
                        .ConfigureAwait(false);
                }

                if (password is null)
                {
                    archive.Dispose();
                    archive = null;

                    if (passwordAttempts >= MaxPasswordAttempts)
                    {
                        return Failure(baseDirectory, ExtractionIssueKind.Password, "パスワードが必要です。", archivePath);
                    }

                    var next = await RequestPasswordAsync(request.Options, passwordAttempts + 1, null, cancellationToken)
                        .ConfigureAwait(false);
                    if (next is null)
                    {
                        return Cancelled(baseDirectory, issues, archivePath);
                    }

                    password = next;
                    passwordAttempts++;
                    continue;
                }

                try
                {
                    // 最初の暗号化エントリを最後まで読み、パスワードを検証する。
                    await DrainEntryAsync(firstEncrypted, cancellationToken).ConfigureAwait(false);
                    return await ExtractEntriesAsync(archive, entries, request, baseDirectory, archiveLength, issues, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (IsPasswordError(ex) || ex is CryptographicException)
                {
                    archive.Dispose();
                    archive = null;

                    if (passwordAttempts >= MaxPasswordAttempts)
                    {
                        return Failure(baseDirectory, ExtractionIssueKind.Password, "パスワードが正しくないため解凍できませんでした。", archivePath);
                    }

                    var next = await RequestPasswordAsync(request.Options, passwordAttempts + 1, "パスワードが違います。", cancellationToken)
                        .ConfigureAwait(false);
                    if (next is null)
                    {
                        return Cancelled(baseDirectory, issues, archivePath);
                    }

                    password = next;
                    passwordAttempts++;
                    continue;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return Cancelled(baseDirectory, issues, archivePath);
        }
        catch (Exception ex)
        {
            return Failure(baseDirectory, ExtractionIssueKind.ArchiveError, $"解凍中にエラーが発生しました: {ex.Message}", archivePath);
        }
        finally
        {
            archive?.Dispose();
        }
    }

    private static async Task<ExtractionResult> ExtractEntriesAsync(
        IArchive archive,
        IReadOnlyList<IArchiveEntry> rawEntries,
        ExtractionRequest request,
        string baseDirectory,
        long archiveLength,
        List<ExtractionIssue> issues,
        CancellationToken cancellationToken)
    {
        var workItems = new List<EntryWork>();

        foreach (var entry in rawEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!PathSanitizer.TryNormalize(entry.Key, out var relativePath, out var pathError))
            {
                if (pathError is not null)
                {
                    issues.Add(new ExtractionIssue(
                        ExtractionIssueSeverity.Error,
                        ExtractionIssueKind.PathTraversal,
                        entry.Key ?? string.Empty,
                        pathError));
                }

                continue;
            }

            var sanitized = PathSanitizer.Sanitize(relativePath);
            if (sanitized.Changed)
            {
                issues.Add(new ExtractionIssue(
                    ExtractionIssueSeverity.Warning,
                    ExtractionIssueKind.UnsafeName,
                    entry.Key ?? string.Empty,
                    $"安全でないファイル名を修正しました: {sanitized.Note}"));
            }

            workItems.Add(new EntryWork(
                entry,
                sanitized.RelativePath,
                entry.IsDirectory,
                IsSymbolicLink(entry),
                Math.Max(0, entry.Size),
                Math.Max(0, entry.CompressedSize),
                entry.LastModifiedTime));
        }

        var plan = DestinationPlanner.Create(
            baseDirectory,
            archivePath: request.ArchivePath,
            workItems.Select(x => new EntryPathInfo(x.RelativePath, x.IsDirectory, x.Size)).ToList());

        try
        {
            LongPath.CreateDirectory(plan.OutputRoot);
        }
        catch (Exception ex)
        {
            return Failure(baseDirectory, ExtractionIssueKind.ArchiveError, $"解凍先フォルダを作成できません: {ex.Message}", request.ArchivePath);
        }

        var totalBytes = workItems
            .Where(x => !x.IsDirectory && !x.IsSymbolicLink)
            .Sum(x => x.Size);

        AddExpansionWarningIfNeeded(workItems, archiveLength, request.Options, issues);

        var progress = request.Options.Progress;
        long processedBytes = 0;
        var extractedFiles = 0;
        var skippedFiles = 0;
        var lastProgress = Stopwatch.StartNew();

        try
        {
            foreach (var work in workItems)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (work.IsSymbolicLink)
                {
                    issues.Add(new ExtractionIssue(
                        ExtractionIssueSeverity.Warning,
                        ExtractionIssueKind.SymbolicLink,
                        work.RelativePath,
                        "シンボリックリンクエントリは安全のためスキップしました。"));
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
                    issues.Add(new ExtractionIssue(
                        ExtractionIssueSeverity.Error,
                        ExtractionIssueKind.EntryFailed,
                        work.RelativePath,
                        $"出力パスを組み立てられません: {ex.Message}"));
                    continue;
                }

                if (!PathSanitizer.IsUnderRoot(plan.OutputRoot, targetPath))
                {
                    issues.Add(new ExtractionIssue(
                        ExtractionIssueSeverity.Error,
                        ExtractionIssueKind.PathTraversal,
                        work.RelativePath,
                        "解凍先の外へ書き込むエントリを拒否しました。"));
                    continue;
                }

                if (work.IsDirectory)
                {
                    try
                    {
                        if (LongPath.FileExists(targetPath))
                        {
                            issues.Add(new ExtractionIssue(
                                ExtractionIssueSeverity.Error,
                                ExtractionIssueKind.EntryFailed,
                                work.RelativePath,
                                "同名のファイルが存在するためフォルダを作成できません。"));
                            continue;
                        }

                        LongPath.CreateDirectory(targetPath);
                    }
                    catch (Exception ex)
                    {
                        issues.Add(new ExtractionIssue(
                            ExtractionIssueSeverity.Error,
                            ExtractionIssueKind.EntryFailed,
                            work.RelativePath,
                            $"フォルダを作成できません: {ex.Message}"));
                    }

                    continue;
                }

                if (LongPath.DirectoryExists(targetPath))
                {
                    issues.Add(new ExtractionIssue(
                        ExtractionIssueSeverity.Error,
                        ExtractionIssueKind.EntryFailed,
                        work.RelativePath,
                        "同名のフォルダが存在するためファイルを書き込めません。"));
                    skippedFiles++;
                    continue;
                }

                var writePath = targetPath;
                var overwrite = false;

                if (LongPath.FileExists(targetPath))
                {
                    var decision = await ResolveOverwriteAsync(request.Options, work, targetPath, cancellationToken)
                        .ConfigureAwait(false);

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
                    issues.Add(new ExtractionIssue(
                        ExtractionIssueSeverity.Error,
                        ExtractionIssueKind.EntryFailed,
                        work.RelativePath,
                        "出力先フォルダを特定できません。"));
                    continue;
                }

                string? tempPath = null;
                try
                {
                    LongPath.CreateDirectory(parentDirectory);
                    tempPath = Path.Combine(parentDirectory, "~hodozip-" + Guid.NewGuid().ToString("N") + ".tmp");

                    progress?.Report(new ExtractionProgress(processedBytes, totalBytes, work.RelativePath));

                    var copied = await WriteEntryAsync(
                        work.Entry,
                        tempPath,
                        bytes => processedBytes += bytes,
                        () => processedBytes,
                        totalBytes,
                        work.RelativePath,
                        progress,
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

                    if (lastProgress.Elapsed >= ProgressInterval)
                    {
                        progress?.Report(new ExtractionProgress(processedBytes, totalBytes, work.RelativePath));
                        lastProgress.Restart();
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    issues.Add(new ExtractionIssue(
                        ExtractionIssueSeverity.Error,
                        ExtractionIssueKind.EntryFailed,
                        work.RelativePath,
                        $"ファイルを書き込めません: {ex.Message}"));
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

        progress?.Report(new ExtractionProgress(processedBytes, totalBytes, string.Empty));

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

    private static async Task<long> WriteEntryAsync(
        IArchiveEntry entry,
        string tempPath,
        Action<long> addProcessed,
        Func<long> getProcessed,
        long totalBytes,
        string currentFile,
        IProgress<ExtractionProgress>? progress,
        Stopwatch lastProgress,
        CancellationToken cancellationToken)
    {
        await using var source = entry.OpenEntryStream();
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
                    progress?.Report(new ExtractionProgress(getProcessed(), totalBytes, currentFile));
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

    private static async Task<OverwriteDecision> ResolveOverwriteAsync(
        ExtractionOptions options,
        EntryWork work,
        string targetPath,
        CancellationToken cancellationToken)
    {
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
                    work.RelativePath,
                    targetPath,
                    existing.Exists ? existing.Length : 0,
                    work.Size,
                    existing.Exists ? new DateTimeOffset(existing.LastWriteTime) : null,
                    work.LastModifiedTime is { } dt ? new DateTimeOffset(dt) : null);

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

        return await options.PasswordProvider(new PasswordRequest(attempt, errorMessage), cancellationToken)
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

    private static ReaderOptions CreateReaderOptions(string? password)
    {
        _ = EncodingHolder;
        return new ReaderOptions
        {
            Password = password,
            ArchiveEncoding = new ArchiveEncoding
            {
                // UTF-8 フラグのない ZIP エントリは CP932 (Shift_JIS) としてデコードする。
                Default = EncodingHolder.ShiftJis,
            },
        };
    }

    private static bool IsSymbolicLink(IArchiveEntry entry)
    {
        if (entry is ZipArchiveEntry zipEntry)
        {
            if (!string.IsNullOrEmpty(zipEntry.LinkTarget))
            {
                return true;
            }

            if (zipEntry.Attrib is { } attributes)
            {
                var unixMode = (attributes >> 16) & 0xF000;
                if (unixMode == 0xA000)
                {
                    return true;
                }
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

        issues.Add(new ExtractionIssue(
            ExtractionIssueSeverity.Warning,
            ExtractionIssueKind.SuspiciousExpansion,
            string.Empty,
            $"展開後サイズが圧縮サイズの {ratio:N0} 倍です。展開爆弾の可能性があります。"));
    }

    private static string GetUniqueFilePath(string path)
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
            if (!LongPath.FileExists(candidate) && !LongPath.DirectoryExists(candidate))
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
        if (exception is CryptographicException)
        {
            return true;
        }

        var message = exception.Message;
        return message.Contains("password", StringComparison.OrdinalIgnoreCase)
            || message.Contains("encrypted", StringComparison.OrdinalIgnoreCase)
            || message.Contains("authentication", StringComparison.OrdinalIgnoreCase)
            || message.Contains("cryptographic", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Cannot determine compressed stream type", StringComparison.OrdinalIgnoreCase);
    }

    private static ExtractionResult Failure(string destinationPath, ExtractionIssueKind kind, string message, string entryPath)
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
                new ExtractionIssue(ExtractionIssueSeverity.Error, kind, entryPath, message),
            },
        };
    }

    private static ExtractionResult Cancelled(string destinationPath, List<ExtractionIssue> issues, string archivePath)
    {
        issues.Add(new ExtractionIssue(
            ExtractionIssueSeverity.Warning,
            ExtractionIssueKind.Cancelled,
            archivePath,
            "解凍をキャンセルしました。"));

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
        IArchiveEntry Entry,
        string RelativePath,
        bool IsDirectory,
        bool IsSymbolicLink,
        long Size,
        long CompressedSize,
        DateTime? LastModifiedTime);

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
