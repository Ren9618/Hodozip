namespace Hodozip.Core;

public sealed partial class SharpCompressExtractor
{
    /// <summary>
    /// 複数のアーカイブを 1 つのフォルダへ順番に解凍する。
    /// 1 つのアーカイブで失敗しても残りは続行し、全体の結果に集約する。
    /// </summary>
    public Task<ExtractionResult> ExtractManyAsync(
        MultiArchiveExtractionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.Run(() => ExtractManyCoreAsync(request, cancellationToken), CancellationToken.None);
    }

    private static async Task<ExtractionResult> ExtractManyCoreAsync(
        MultiArchiveExtractionRequest request,
        CancellationToken cancellationToken)
    {
        var archivePaths = request.ArchivePaths;
        var destination = Path.GetFullPath(request.DestinationDirectory);

        if (archivePaths.Count == 0)
        {
            return CreateBatchFailure(
                destination,
                archiveCount: 0,
                "解凍するアーカイブが指定されていません。",
                "Issue_ArchiveNotFound");
        }

        try
        {
            LongPath.CreateDirectory(destination);
        }
        catch (Exception ex)
        {
            return CreateBatchFailure(
                destination,
                archivePaths.Count,
                $"解凍先フォルダを作成できません: {ex.Message}",
                "Issue_DestinationCreateFailed",
                ex.Message);
        }

        var issues = new List<ExtractionIssue>();
        var extractedFiles = 0;
        var skippedFiles = 0;
        var succeededArchives = 0;
        var cancelled = false;

        for (var index = 0; index < archivePaths.Count; index++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }

            var archivePath = archivePaths[index];
            var archiveName = Path.GetFileName(archivePath);
            var archiveProgress = request.Options.Progress is { } progress
                ? new ArchiveProgressReporter(progress, index + 1, archivePaths.Count, archiveName)
                : null;

            // パスワード入力などで時間がかかる場合でも、どのアーカイブを
            // 処理しているかが分かるように開始時点で進捗を通知する。
            archiveProgress?.Report(new ExtractionProgress(
                ProcessedBytes: 0,
                TotalBytes: 0,
                CurrentFile: string.Empty,
                DestinationPath: destination));

            var options = CopyOptions(request.Options, archiveProgress);
            ExtractionResult result;
            try
            {
                result = await ExtractCoreAsync(
                    new ExtractionRequest(
                        archivePath,
                        destination,
                        options,
                        ExtractionDestinationMode.DirectToFolder),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                break;
            }
            catch (Exception ex)
            {
                result = Failure(
                    destination,
                    ExtractionIssueKind.ArchiveError,
                    $"解凍中にエラーが発生しました: {ex.Message}",
                    archivePath,
                    "Issue_Unexpected",
                    ex.Message);
            }

            extractedFiles += result.ExtractedFileCount;
            skippedFiles += result.SkippedFileCount;

            foreach (var issue in result.Issues)
            {
                issues.Add(issue.ArchivePath is null
                    ? issue with { ArchivePath = archivePath }
                    : issue);
            }

            if (result.Cancelled)
            {
                cancelled = true;
                break;
            }

            if (result.Success)
            {
                succeededArchives++;
            }
        }

        return new ExtractionResult
        {
            Success = !cancelled && succeededArchives == archivePaths.Count,
            Cancelled = cancelled,
            DestinationPath = destination,
            ExtractedFileCount = extractedFiles,
            SkippedFileCount = skippedFiles,
            Issues = issues,
            ArchiveCount = archivePaths.Count,
            SucceededArchiveCount = succeededArchives,
        };
    }

    private static ExtractionResult CreateBatchFailure(
        string destination,
        int archiveCount,
        string message,
        string messageKey,
        params object?[] arguments)
    {
        return new ExtractionResult
        {
            Success = false,
            DestinationPath = destination,
            ArchiveCount = archiveCount,
            Issues = new[]
            {
                new ExtractionIssue(
                    ExtractionIssueSeverity.Error,
                    ExtractionIssueKind.ArchiveError,
                    string.Empty,
                    message)
                {
                    MessageKey = messageKey,
                    MessageArguments = arguments,
                },
            },
        };
    }

    private static ExtractionOptions CopyOptions(
        ExtractionOptions source,
        IProgress<ExtractionProgress>? progress)
    {
        return new ExtractionOptions
        {
            OverwritePolicy = source.OverwritePolicy,
            OverwriteResolver = source.OverwriteResolver,
            FolderCollisionPolicy = source.FolderCollisionPolicy,
            FolderCollisionResolver = source.FolderCollisionResolver,
            PasswordProvider = source.PasswordProvider,
            Progress = progress,
            MaxDegreeOfParallelism = source.MaxDegreeOfParallelism,
            ExpansionWarningMinBytes = source.ExpansionWarningMinBytes,
            ExpansionWarningRatio = source.ExpansionWarningRatio,
            SkipExistingIfNewer = source.SkipExistingIfNewer,
            ExcludeMacOsMetadata = source.ExcludeMacOsMetadata,
            NormalizeFileNamesNfc = source.NormalizeFileNamesNfc,
            ZipFileNameEncoding = source.ZipFileNameEncoding,
        };
    }

    private sealed class ArchiveProgressReporter : IProgress<ExtractionProgress>
    {
        private readonly IProgress<ExtractionProgress> _inner;
        private readonly int _index;
        private readonly int _count;
        private readonly string _archiveName;

        public ArchiveProgressReporter(
            IProgress<ExtractionProgress> inner,
            int index,
            int count,
            string archiveName)
        {
            _inner = inner;
            _index = index;
            _count = count;
            _archiveName = archiveName;
        }

        public void Report(ExtractionProgress value)
        {
            _inner.Report(value with
            {
                ArchiveIndex = _index,
                ArchiveCount = _count,
                ArchiveName = _archiveName,
            });
        }
    }
}
