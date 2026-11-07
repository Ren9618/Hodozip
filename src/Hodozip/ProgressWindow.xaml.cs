using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Hodozip.Core;
using Hodozip.Dialogs;
using Hodozip.Services;

namespace Hodozip;

public sealed partial class ProgressWindow : Window
{
    private static readonly TimeSpan AutoCloseDelay = TimeSpan.FromSeconds(3);
    private const int PathMaxLength = 62;
    private const int FileNameMaxLength = 44;

    private readonly string? _archivePath;
    private readonly CompressionRequest? _compressionRequest;
    private readonly string _displayName;
    private readonly bool _isCommandLine;
    private readonly ExtractionDestinationMode _destinationMode;
    private readonly AppSettings _settings;
    private readonly SharpCompressExtractor _extractor = new();
    private readonly SharpCompressCompressor _compressor = new();
    private readonly CancellationTokenSource _cancellation = new();

    private DispatcherTimer? _autoCloseTimer;
    private DispatcherTimer? _progressTimer;
    private ExtractionProgress? _latestExtractionProgress;
    private CompressionProgress? _latestCompressionProgress;
    private OverwriteDecision? _applyToAllDecision;
    private string? _destinationPath;
    private bool _started;
    private bool _completed;
    private bool _closed;
    private bool _closeRequested;
    private bool _hiddenInBackground;
    private bool _closeConfirmOpen;
    private int _autoCloseSeconds;
    private JobInfo? _job;

    public ProgressWindow(
        string archivePath,
        bool isCommandLine,
        ExtractionDestinationMode destinationMode = ExtractionDestinationMode.Auto)
    {
        InitializeComponent();

        _archivePath = archivePath;
        _compressionRequest = null;
        _displayName = Path.GetFileName(archivePath);
        _isCommandLine = isCommandLine;
        _destinationMode = destinationMode;
        _settings = SettingsService.Load();

        ConfigureCommon();
        SetDestination(Path.GetDirectoryName(archivePath) ?? Environment.CurrentDirectory);
    }

    public ProgressWindow(CompressionRequest request, bool isCommandLine)
    {
        InitializeComponent();

        _archivePath = null;
        _compressionRequest = request;
        _displayName = Path.GetFileName(request.OutputPath);
        _isCommandLine = isCommandLine;
        _destinationMode = ExtractionDestinationMode.Auto;
        _settings = SettingsService.Load();

        ConfigureCommon();
        SetDestination(Path.GetDirectoryName(request.OutputPath) ?? Environment.CurrentDirectory);
    }

    public event EventHandler<ExtractionResult>? ExtractionCompleted;

    public event EventHandler<CompressionResult>? CompressionCompleted;

    public bool IsCompleted => _completed;

    private void ConfigureCommon()
    {
        Title = $"0% / {_displayName}";
        WindowHelpers.ConfigureFixed(this, 480, 240);
        ApplyLocalization();

        CurrentFileText.Text = " ";
        ToolTipService.SetToolTip(CurrentFileText, null);

        // 進捗通知はワーカー スレッドから届くため、最新値だけを保持し、
        // UI スレッドのタイマーで最大 10 回/秒に間引いて反映する。
        // 通知のたびに DispatcherQueue へ積むと、数千ファイルの解凍で
        // UI スレッドが飽和し、描画が止まって「応答なし」になる。
        _progressTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _progressTimer.Tick += ProgressTimer_Tick;

        RootGrid.Loaded += RootGrid_Loaded;
        AppWindow.Closing += AppWindow_Closing;
        Closed += ProgressWindow_Closed;
    }

    private void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        if (_started)
        {
            return;
        }

        _started = true;

        _job = new JobInfo(this, _displayName, _compressionRequest is not null);
        _job.StatusText = _compressionRequest is null ? Loc.Progress_Extracting : Loc.Progress_Compressing;
        JobRegistry.Add(_job);

        _progressTimer?.Start();
        _ = _compressionRequest is null ? RunExtractionAsync() : RunCompressionAsync();
    }

    private async Task RunExtractionAsync()
    {
        await Task.Delay(120).ConfigureAwait(false);

        var options = new ExtractionOptions
        {
            OverwritePolicy = _settings.OverwritePolicy,
            FolderCollisionPolicy = _settings.FolderCollisionPolicy,
            OverwriteResolver = ResolveOverwriteAsync,
            FolderCollisionResolver = ResolveFolderCollisionAsync,
            PasswordProvider = ProvidePasswordAsync,
            Progress = new LatestProgress<ExtractionProgress>(
                value => Volatile.Write(ref _latestExtractionProgress, value)),
            MaxDegreeOfParallelism = _settings.MaxDegreeOfParallelism,
            SkipExistingIfNewer = _settings.SkipExistingIfNewer,
            ExcludeMacOsMetadata = _settings.ExcludeMacOsMetadata,
            NormalizeFileNamesNfc = _settings.NormalizeFileNamesNfc,
        };

        var destination = await ResolveConfiguredDestinationAsync().ConfigureAwait(false);
        if (destination.Cancelled)
        {
            var cancelledResult = CreateFailureResult(
                Loc.Progress_CancelledMessage,
                cancelled: true,
                "Issue_Cancelled");
            DispatcherQueue.TryEnqueue(() => CompleteOnUiThread(cancelledResult));
            return;
        }

        ExtractionResult result;
        try
        {
            result = await _extractor
                .ExtractAsync(
                    new ExtractionRequest(_archivePath!, destination.Directory, options, _destinationMode),
                    _cancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result = CreateFailureResult(Loc.Progress_CancelledMessage, cancelled: true, "Issue_Cancelled");
        }
        catch (Exception ex)
        {
            result = CreateFailureResult(Loc.Progress_UnexpectedError(ex.Message), cancelled: false, "Issue_Unexpected", ex.Message);
        }

        DispatcherQueue.TryEnqueue(() => CompleteOnUiThread(result));
    }

    private async Task RunCompressionAsync()
    {
        await Task.Delay(120).ConfigureAwait(false);

        CompressionResult result;
        try
        {
            var request = _compressionRequest!;
            var options = request.Options;
            var progressRequest = new CompressionRequest(
                request.SourcePaths,
                request.OutputPath,
                new CompressionOptions
                {
                    Format = options.Format,
                    Level = options.Level,
                    Password = options.Password,
                    ZipNameEncoding = options.ZipNameEncoding,
                    MacOsCompatible = options.MacOsCompatible,
                    Overwrite = options.Overwrite,
                    Progress = new LatestProgress<CompressionProgress>(
                        value => Volatile.Write(ref _latestCompressionProgress, value)),
                });

            result = await _compressor
                .CompressAsync(progressRequest, _cancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result = CreateCompressionFailure(
                Loc.Progress_CompressionCancelledMessage,
                cancelled: true,
                "Issue_Cancelled");
        }
        catch (Exception ex)
        {
            result = CreateCompressionFailure(
                Loc.Progress_CompressionUnexpectedError(ex.Message),
                cancelled: false,
                "Compression_ErrorUnexpected",
                ex.Message);
        }

        DispatcherQueue.TryEnqueue(() => CompleteCompressionOnUiThread(result));
    }

    private ExtractionResult CreateFailureResult(string message, bool cancelled, string? messageKey, params object?[] arguments)
    {
        return new ExtractionResult
        {
            Success = false,
            Cancelled = cancelled,
            DestinationPath = Path.GetDirectoryName(_archivePath!) ?? Environment.CurrentDirectory,
            Issues = new[]
            {
                new ExtractionIssue(
                    cancelled ? ExtractionIssueSeverity.Warning : ExtractionIssueSeverity.Error,
                    cancelled ? ExtractionIssueKind.Cancelled : ExtractionIssueKind.ArchiveError,
                    _archivePath!,
                    message)
                {
                    MessageKey = messageKey,
                    MessageArguments = arguments,
                },
            },
        };
    }

    private CompressionResult CreateCompressionFailure(
        string message,
        bool cancelled,
        string? messageKey,
        params object?[] arguments)
    {
        return new CompressionResult
        {
            Success = false,
            Cancelled = cancelled,
            OutputPath = _compressionRequest?.OutputPath ?? string.Empty,
            ErrorMessage = message,
            ErrorMessageKey = messageKey,
            ErrorArguments = arguments,
        };
    }

    private void CompleteOnUiThread(ExtractionResult result)
    {
        if (_closed)
        {
            return;
        }

        _progressTimer?.Stop();
        _completed = true;
        ShowResult(result);
        JobRegistry.Remove(_job);

        if (_closeRequested)
        {
            Close();
            return;
        }

        ExtractionCompleted?.Invoke(this, result);
        NotifyExtractionCompleted(result);

        if (_hiddenInBackground)
        {
            Close();
            return;
        }

        if (_isCommandLine && _settings.CloseAfterAutoExtract && result.Success)
        {
            StartAutoCloseCountdown();
        }
    }

    private void CompleteCompressionOnUiThread(CompressionResult result)
    {
        if (_closed)
        {
            return;
        }

        _progressTimer?.Stop();
        _completed = true;
        ShowCompressionResult(result);
        JobRegistry.Remove(_job);

        if (_closeRequested)
        {
            Close();
            return;
        }

        CompressionCompleted?.Invoke(this, result);
        NotifyCompressionCompleted(result);

        if (_hiddenInBackground)
        {
            Close();
        }
    }

    private void ProgressTimer_Tick(object? sender, object e)
    {
        if (_completed || _closed)
        {
            _progressTimer?.Stop();
            return;
        }

        if (_compressionRequest is null)
        {
            var progress = Volatile.Read(ref _latestExtractionProgress);
            if (progress is not null)
            {
                UpdateProgress(progress);
            }
        }
        else
        {
            var progress = Volatile.Read(ref _latestCompressionProgress);
            if (progress is not null)
            {
                UpdateCompressionProgress(progress);
            }
        }
    }

    private void UpdateProgress(ExtractionProgress progress)
    {
        if (_completed || _closed)
        {
            return;
        }

        if (progress.TotalBytes > 0)
        {
            // タイトル、全体バー、数値表示を同じ丸め済みの値から更新する。
            // バーは 0-100 に正規化し、3 者が常に同じ % を示すようにする。
            var percent = Math.Clamp((int)Math.Round(progress.ProcessedBytes * 100d / progress.TotalBytes), 0, 100);
            PercentText.Text = $"{percent}%";
            Title = $"{percent}% / {_displayName}";
            ProgressBar.IsIndeterminate = false;
            ProgressBar.Maximum = 100d;
            ProgressBar.Value = percent;
            UpdateJob(percent, Loc.Progress_Extracting);
        }
        else
        {
            ProgressBar.IsIndeterminate = true;
            PercentText.Text = "…";
            Title = $"… / {_displayName}";
            UpdateJob((int)Math.Round(_job?.ProgressPercent ?? 0), Loc.Progress_Extracting);
        }

        if (!string.IsNullOrEmpty(progress.DestinationPath))
        {
            SetDestination(progress.DestinationPath);
        }

        SetCurrentFile(progress.CurrentFile, progress.CurrentFileProcessedBytes, progress.CurrentFileTotalBytes);
        StatusText.Text = Loc.Progress_Extracting;
    }

    private void UpdateCompressionProgress(CompressionProgress progress)
    {
        if (_completed || _closed)
        {
            return;
        }

        if (progress.TotalBytes > 0)
        {
            var percent = Math.Clamp((int)Math.Round(progress.ProcessedBytes * 100d / progress.TotalBytes), 0, 100);
            PercentText.Text = $"{percent}%";
            Title = $"{percent}% / {_displayName}";
            ProgressBar.IsIndeterminate = false;
            ProgressBar.Maximum = 100d;
            ProgressBar.Value = percent;
            UpdateJob(percent, Loc.Progress_Compressing);
        }
        else
        {
            ProgressBar.IsIndeterminate = true;
            PercentText.Text = "…";
            Title = $"… / {_displayName}";
            UpdateJob((int)Math.Round(_job?.ProgressPercent ?? 0), Loc.Progress_Compressing);
        }

        if (!string.IsNullOrEmpty(progress.OutputPath))
        {
            SetDestination(Path.GetDirectoryName(progress.OutputPath) ?? _destinationPath ?? string.Empty);
        }

        SetCurrentFile(progress.CurrentFile, progress.CurrentFileProcessedBytes, progress.CurrentFileTotalBytes);
        StatusText.Text = Loc.Progress_Compressing;
    }

    private void SetCurrentFile(string currentFile, long fileProcessed, long fileTotal)
    {
        CurrentFileText.Text = string.IsNullOrEmpty(currentFile)
            ? " "
            : Loc.Progress_File(MiddleEllipsis.CompactMiddle(currentFile, FileNameMaxLength));
        ToolTipService.SetToolTip(CurrentFileText, string.IsNullOrEmpty(currentFile) ? null : currentFile);

        if (fileTotal > 0)
        {
            FileProgressBar.IsIndeterminate = false;
            FileProgressBar.Maximum = Math.Max(1, fileTotal);
            FileProgressBar.Value = Math.Min(fileProcessed, FileProgressBar.Maximum);
        }
        else if (!string.IsNullOrEmpty(currentFile))
        {
            FileProgressBar.IsIndeterminate = true;
        }
        else
        {
            FileProgressBar.IsIndeterminate = false;
            FileProgressBar.Value = 0;
        }
    }

    private void SetDestination(string destination)
    {
        if (string.Equals(_destinationPath, destination, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _destinationPath = destination;
        DestinationText.Text = _compressionRequest is null
            ? Loc.Progress_Destination(MiddleEllipsis.CompactMiddle(destination, PathMaxLength))
            : Loc.Progress_Output(MiddleEllipsis.CompactMiddle(destination, PathMaxLength));
        ToolTipService.SetToolTip(DestinationText, destination);
    }

    private void ShowResult(ExtractionResult result)
    {
        SetDestination(result.DestinationPath);

        ResetProgressBars(result.Success);
        PercentText.Text = result.Success ? "100%" : "—";
        CurrentFileText.Text = " ";
        ToolTipService.SetToolTip(CurrentFileText, null);

        var warnings = result.Issues.Count(x => x.Severity == ExtractionIssueSeverity.Warning);
        var errors = result.Issues.Count(x => x.Severity == ExtractionIssueSeverity.Error);

        if (result.Success)
        {
            Title = $"100% / {_displayName}";
            StatusText.Text = warnings > 0
                ? Loc.Progress_CompletedWarnings(warnings, result.SkippedFileCount)
                : Loc.Progress_Completed(result.ExtractedFileCount, result.SkippedFileCount);
            StatusText.Foreground = (Brush)Application.Current.Resources["SystemFillColorSuccessBrush"];
        }
        else if (result.Cancelled)
        {
            Title = $"{Loc.Progress_CancelledTitle} / {_displayName}";
            StatusText.Text = Loc.Progress_Cancelled;
            StatusText.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        }
        else
        {
            Title = $"{Loc.Progress_ErrorTitle} / {_displayName}";
            var error = result.Issues.FirstOrDefault(x => x.Severity == ExtractionIssueSeverity.Error);
            var message = error is null ? Loc.Progress_FailedMessage : LocalizationService.GetIssueMessage(error);
            StatusText.Text = Loc.Progress_ErrorStatus(MiddleEllipsis.CompactMiddle(message, 56));
            ToolTipService.SetToolTip(StatusText, message);
            StatusText.Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
        }

        ShowCompletionButtons(result.Success, result.DestinationPath);

        if (_settings.OpenFolderAfterExtraction && result.Success)
        {
            try
            {
                UiHelpers.OpenFolder(result.DestinationPath);
            }
            catch
            {
                // フォルダを開けない場合も解凍結果の表示は続ける。
            }
        }
    }

    private void ShowCompressionResult(CompressionResult result)
    {
        var outputDirectory = string.IsNullOrWhiteSpace(result.OutputPath)
            ? _destinationPath
            : Path.GetDirectoryName(result.OutputPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            SetDestination(outputDirectory);
        }

        ResetProgressBars(result.Success);
        PercentText.Text = result.Success ? "100%" : "—";
        CurrentFileText.Text = " ";
        ToolTipService.SetToolTip(CurrentFileText, null);

        if (result.Success)
        {
            Title = $"100% / {_displayName}";
            StatusText.Text = Loc.Progress_CompressionCompleted(
                result.FileCount,
                UiHelpers.FormatBytes(result.SourceBytes),
                UiHelpers.FormatBytes(result.OutputBytes))
                + (result.PasswordIgnored ? Loc.Progress_PasswordIgnored : string.Empty);
            StatusText.Foreground = (Brush)Application.Current.Resources["SystemFillColorSuccessBrush"];
        }
        else if (result.Cancelled)
        {
            Title = $"{Loc.Progress_CompressionCancelledTitle} / {_displayName}";
            StatusText.Text = Loc.Progress_CompressionCancelled;
            StatusText.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        }
        else
        {
            Title = $"{Loc.Progress_ErrorTitle} / {_displayName}";
            var message = LocalizationService.GetCompressionError(result);
            if (string.IsNullOrWhiteSpace(message))
            {
                message = Loc.Progress_FailedMessage;
            }

            StatusText.Text = Loc.Progress_ErrorStatus(MiddleEllipsis.CompactMiddle(message, 56));
            ToolTipService.SetToolTip(StatusText, message);
            StatusText.Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
        }

        ShowCompletionButtons(result.Success, outputDirectory);
    }

    private void ResetProgressBars(bool success)
    {
        ProgressBar.IsIndeterminate = false;
        ProgressBar.Maximum = 1;
        ProgressBar.Value = success ? 1 : 0;
        FileProgressBar.IsIndeterminate = false;
        FileProgressBar.Maximum = 1;
        FileProgressBar.Value = success ? 1 : 0;
    }

    private void ShowCompletionButtons(bool success, string? destination)
    {
        CancelButton.Visibility = Visibility.Collapsed;
        CloseButton.Visibility = Visibility.Visible;
        OpenFolderButton.Visibility = !string.IsNullOrWhiteSpace(destination) && Directory.Exists(destination)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private Task<OverwriteDecision> ResolveOverwriteAsync(
        OverwriteConflict conflict,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(OverwriteDecision.Cancel);
        }

        return RunOnUiThreadAsync(async () =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return OverwriteDecision.Cancel;
            }

            if (_applyToAllDecision is { } preset)
            {
                return preset;
            }

            var xamlRoot = RootGrid.XamlRoot;
            if (xamlRoot is null)
            {
                return OverwriteDecision.Skip;
            }

            var (decision, applyToAll) = await OverwriteDialog.ShowAsync(xamlRoot, conflict);
            if (applyToAll)
            {
                _applyToAllDecision = decision;
            }

            return decision;
        });
    }

    private Task<FolderCollisionDecision> ResolveFolderCollisionAsync(
        FolderCollisionConflict conflict,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(FolderCollisionDecision.Cancel);
        }

        return RunOnUiThreadAsync(async () =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return FolderCollisionDecision.Cancel;
            }

            var xamlRoot = RootGrid.XamlRoot;
            if (xamlRoot is null)
            {
                return FolderCollisionDecision.AutoRename;
            }

            var (decision, saveAsDefault) = await FolderCollisionDialog.ShowAsync(xamlRoot, conflict);
            if (saveAsDefault)
            {
                var settings = SettingsService.Load();
                settings.FolderCollisionPolicy = decision switch
                {
                    FolderCollisionDecision.Merge => FolderCollisionPolicy.Merge,
                    FolderCollisionDecision.AutoRename => FolderCollisionPolicy.AutoRename,
                    _ => FolderCollisionPolicy.Ask,
                };
                SettingsService.Save(settings);
            }

            return decision;
        });
    }

    private Task<string?> ProvidePasswordAsync(
        PasswordRequest request,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult<string?>(null);
        }

        return RunOnUiThreadAsync(async () =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            var xamlRoot = RootGrid.XamlRoot;
            return xamlRoot is null
                ? null
                : await PasswordDialog.ShowAsync(xamlRoot, request);
        });
    }

    private Task<T> RunOnUiThreadAsync<T>(Func<Task<T>> action)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            try
            {
                return action();
            }
            catch (Exception ex)
            {
                return Task.FromException<T>(ex);
            }
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var enqueued = DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                completion.TrySetResult(await action());
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });

        if (!enqueued)
        {
            completion.TrySetCanceled();
        }

        return completion.Task;
    }

    private void StartAutoCloseCountdown()
    {
        _autoCloseSeconds = (int)AutoCloseDelay.TotalSeconds;
        AutoCloseText.Visibility = Visibility.Visible;
        AutoCloseText.Text = Loc.Progress_AutoClose(_autoCloseSeconds);

        _autoCloseTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _autoCloseTimer.Tick += (_, _) =>
        {
            _autoCloseSeconds--;
            if (_autoCloseSeconds <= 0)
            {
                _autoCloseTimer?.Stop();
                Close();
                return;
            }

            AutoCloseText.Text = Loc.Progress_AutoClose(_autoCloseSeconds);
        };
        _autoCloseTimer.Start();
    }

    private void RequestCancel()
    {
        if (_cancellation.IsCancellationRequested || _completed)
        {
            return;
        }

        _cancellation.Cancel();
        CancelButton.IsEnabled = false;
        StatusText.Text = Loc.Progress_Cancelling;
        CurrentFileText.Text = " ";
        FileProgressBar.IsIndeterminate = false;
        FileProgressBar.Value = 0;
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_completed)
        {
            return;
        }

        args.Cancel = true;

        if (_settings.RunInBackground)
        {
            HideToBackground();
            return;
        }

        if (!_closeConfirmOpen)
        {
            _ = ConfirmCancelAsync();
        }
    }

    private async Task ConfirmCancelAsync()
    {
        _closeConfirmOpen = true;
        try
        {
            var xamlRoot = RootGrid.XamlRoot;
            if (xamlRoot is null)
            {
                _closeRequested = true;
                RequestCancel();
                return;
            }

            var dialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = Loc.Progress_CloseConfirmTitle,
                Content = Loc.Progress_CloseConfirmMessage,
                PrimaryButtonText = Loc.Progress_CloseConfirmYes,
                CloseButtonText = Loc.Progress_CloseConfirmNo,
                DefaultButton = ContentDialogButton.Close,
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                _closeRequested = true;
                RequestCancel();
            }
        }
        catch
        {
            // ダイアログを出せない場合は従来どおりキャンセルする。
            _closeRequested = true;
            RequestCancel();
        }
        finally
        {
            _closeConfirmOpen = false;
        }
    }

    /// <summary>バックグラウンド設定が ON のとき、ウィンドウを閉じずに隠す。</summary>
    private void HideToBackground()
    {
        if (_hiddenInBackground)
        {
            return;
        }

        _hiddenInBackground = true;
        try
        {
            AppWindow.Hide();
        }
        catch
        {
            // 非表示にできない場合は表示したままにする。
            _hiddenInBackground = false;
        }
    }

    /// <summary>メイン画面の「再表示」から呼ぶ。非表示にしていた場合も表示する。</summary>
    public void ShowFromBackground()
    {
        _hiddenInBackground = false;
        try
        {
            AppWindow.Show();
        }
        catch
        {
            // 既に表示されている場合は無視する。
        }

        Activate();
    }

    /// <summary>メイン画面の「キャンセル」から呼ぶ。</summary>
    public void CancelFromHost()
    {
        RequestCancel();
    }

    private void UpdateJob(int percent, string status)
    {
        if (_job is null)
        {
            return;
        }

        _job.ProgressPercent = percent;
        _job.StatusText = status;
    }

    private void NotifyExtractionCompleted(ExtractionResult result)
    {
        if (result.Cancelled)
        {
            return;
        }

        var title = result.Success
            ? Loc.Notification_ExtractCompletedTitle
            : Loc.Notification_ExtractFailedTitle;
        var message = result.Success
            ? Loc.Notification_ExtractCompletedMessage(_displayName, result.ExtractedFileCount, result.SkippedFileCount)
            : Loc.Notification_ExtractFailedMessage(
                _displayName,
                result.Issues.FirstOrDefault(x => x.Severity == ExtractionIssueSeverity.Error) is { } issue
                    ? LocalizationService.GetIssueMessage(issue)
                    : Loc.Progress_FailedMessage);

        NotificationService.NotifyCompletion(title, message, _settings);
    }

    private void NotifyCompressionCompleted(CompressionResult result)
    {
        if (result.Cancelled)
        {
            return;
        }

        var title = result.Success
            ? Loc.Notification_CompressionCompletedTitle
            : Loc.Notification_CompressionFailedTitle;
        var message = result.Success
            ? Loc.Notification_CompressionCompletedMessage(_displayName, result.OutputPath)
            : Loc.Notification_CompressionFailedMessage(_displayName, LocalizationService.GetCompressionError(result));

        NotificationService.NotifyCompletion(title, message, _settings);
    }

    private Task<(bool Cancelled, string? Directory)> ResolveConfiguredDestinationAsync()
    {
        if (_destinationMode == ExtractionDestinationMode.ForceArchiveFolder)
        {
            return Task.FromResult((false, (string?)null));
        }

        switch (_settings.ExtractionDestination)
        {
            case DestinationSelectionMode.FixedFolder:
                return Task.FromResult((
                    false,
                    string.IsNullOrWhiteSpace(_settings.ExtractionDestinationFolder)
                        ? null
                        : _settings.ExtractionDestinationFolder));

            case DestinationSelectionMode.AskEveryTime:
                return RunOnUiThreadAsync(async () =>
                {
                    var picker = new Windows.Storage.Pickers.FolderPicker
                    {
                        SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
                    };
                    picker.FileTypeFilter.Add("*");
                    WinRT.Interop.InitializeWithWindow.Initialize(
                        picker,
                        WinRT.Interop.WindowNative.GetWindowHandle(this));
                    var folder = await picker.PickSingleFolderAsync();
                    return (folder is null, (string?)folder?.Path);
                });

            default:
                return Task.FromResult((false, (string?)null));
        }
    }

    private void ProgressWindow_Closed(object sender, WindowEventArgs args)
    {
        _closed = true;
        _autoCloseTimer?.Stop();
        _progressTimer?.Stop();
        JobRegistry.Remove(_job);

        if (!_completed)
        {
            _cancellation.Cancel();
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        RequestCancel();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_destinationPath is not null)
        {
            UiHelpers.OpenFolder(_destinationPath);
        }
    }

    private void ApplyLocalization()
    {
        StatusText.Text = _compressionRequest is null
            ? Loc.Progress_Extracting
            : Loc.Progress_Compressing;
        OpenFolderButton.Content = Loc.Common_OpenFolder;
        CancelButton.Content = Loc.Common_Cancel;
        CloseButton.Content = Loc.Common_Close;
    }

    /// <summary>
    /// 最新の進捗値だけを保持する IProgress 実装。
    /// ワーカー スレッドから呼ばれるが DispatcherQueue には触れず、
    /// UI スレッドのタイマーが最新値を読み取って反映する。
    /// </summary>
    private sealed class LatestProgress<T> : IProgress<T>
    {
        private readonly Action<T> _publish;

        public LatestProgress(Action<T> publish)
        {
            _publish = publish;
        }

        public void Report(T value)
        {
            _publish(value);
        }
    }
}
