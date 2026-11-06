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

    private readonly string _archivePath;
    private readonly string _archiveName;
    private readonly bool _isCommandLine;
    private readonly ExtractionDestinationMode _destinationMode;
    private readonly AppSettings _settings;
    private readonly SharpCompressExtractor _extractor = new();
    private readonly CancellationTokenSource _cancellation = new();

    private DispatcherTimer? _autoCloseTimer;
    private OverwriteDecision? _applyToAllDecision;
    private string? _destinationPath;
    private bool _started;
    private bool _completed;
    private bool _closed;
    private bool _closeRequested;
    private int _autoCloseSeconds;

    public ProgressWindow(
        string archivePath,
        bool isCommandLine,
        ExtractionDestinationMode destinationMode = ExtractionDestinationMode.Auto)
    {
        InitializeComponent();

        _archivePath = archivePath;
        _archiveName = Path.GetFileName(archivePath);
        _isCommandLine = isCommandLine;
        _destinationMode = destinationMode;
        _settings = SettingsService.Load();

        Title = $"0% / {_archiveName}";
        WindowHelpers.ConfigureFixed(this, 480, 240);
        ApplyLocalization();

        SetDestination(Path.GetDirectoryName(archivePath) ?? Environment.CurrentDirectory);

        CurrentFileText.Text = " ";
        ToolTipService.SetToolTip(CurrentFileText, null);

        RootGrid.Loaded += RootGrid_Loaded;
        AppWindow.Closing += AppWindow_Closing;
        Closed += ProgressWindow_Closed;
    }

    public event EventHandler<ExtractionResult>? ExtractionCompleted;

    public bool IsCompleted => _completed;

    private void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _ = RunExtractionAsync();
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
            Progress = new DispatcherProgress(DispatcherQueue, UpdateProgress),
            MaxDegreeOfParallelism = _settings.MaxDegreeOfParallelism,
        };

        ExtractionResult result;
        try
        {
            result = await _extractor
                .ExtractAsync(
                    new ExtractionRequest(_archivePath, null, options, _destinationMode),
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

    private ExtractionResult CreateFailureResult(string message, bool cancelled, string? messageKey, params object?[] arguments)
    {
        return new ExtractionResult
        {
            Success = false,
            Cancelled = cancelled,
            DestinationPath = Path.GetDirectoryName(_archivePath) ?? Environment.CurrentDirectory,
            Issues = new[]
            {
                new ExtractionIssue(
                    cancelled ? ExtractionIssueSeverity.Warning : ExtractionIssueSeverity.Error,
                    cancelled ? ExtractionIssueKind.Cancelled : ExtractionIssueKind.ArchiveError,
                    _archivePath,
                    message)
                {
                    MessageKey = messageKey,
                    MessageArguments = arguments,
                },
            },
        };
    }

    private void CompleteOnUiThread(ExtractionResult result)
    {
        if (_closed)
        {
            return;
        }

        _completed = true;
        ShowResult(result);

        if (_closeRequested)
        {
            Close();
            return;
        }

        ExtractionCompleted?.Invoke(this, result);

        if (_isCommandLine && _settings.CloseAfterAutoExtract && result.Success)
        {
            StartAutoCloseCountdown();
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
            var percent = Math.Min(100d, progress.ProcessedBytes * 100d / progress.TotalBytes);
            ProgressBar.IsIndeterminate = false;
            ProgressBar.Maximum = Math.Max(1, progress.TotalBytes);
            ProgressBar.Value = Math.Min(progress.ProcessedBytes, ProgressBar.Maximum);
            PercentText.Text = $"{percent:0}%";
            Title = $"{percent:0}% / {_archiveName}";
        }
        else
        {
            ProgressBar.IsIndeterminate = true;
            PercentText.Text = "…";
            Title = $"… / {_archiveName}";
        }

        if (!string.IsNullOrEmpty(progress.DestinationPath))
        {
            SetDestination(progress.DestinationPath);
        }

        var currentFile = progress.CurrentFile;
        CurrentFileText.Text = string.IsNullOrEmpty(currentFile)
            ? " "
            : Loc.Progress_File(MiddleEllipsis.CompactMiddle(currentFile, FileNameMaxLength));
        ToolTipService.SetToolTip(CurrentFileText, string.IsNullOrEmpty(currentFile) ? null : currentFile);

        if (progress.CurrentFileTotalBytes > 0)
        {
            FileProgressBar.IsIndeterminate = false;
            FileProgressBar.Maximum = Math.Max(1, progress.CurrentFileTotalBytes);
            FileProgressBar.Value = Math.Min(progress.CurrentFileProcessedBytes, FileProgressBar.Maximum);
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

        StatusText.Text = Loc.Progress_Extracting;
    }

    private void SetDestination(string destination)
    {
        if (string.Equals(_destinationPath, destination, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _destinationPath = destination;
        DestinationText.Text = Loc.Progress_Destination(MiddleEllipsis.CompactMiddle(destination, PathMaxLength));
        ToolTipService.SetToolTip(DestinationText, destination);
    }

    private void ShowResult(ExtractionResult result)
    {
        SetDestination(result.DestinationPath);

        ProgressBar.IsIndeterminate = false;
        ProgressBar.Maximum = 1;
        ProgressBar.Value = result.Success ? 1 : 0;
        FileProgressBar.IsIndeterminate = false;
        FileProgressBar.Maximum = 1;
        FileProgressBar.Value = result.Success ? 1 : 0;
        PercentText.Text = result.Success ? "100%" : "—";
        CurrentFileText.Text = " ";
        ToolTipService.SetToolTip(CurrentFileText, null);

        var warnings = result.Issues.Count(x => x.Severity == ExtractionIssueSeverity.Warning);
        var errors = result.Issues.Count(x => x.Severity == ExtractionIssueSeverity.Error);

        if (result.Success)
        {
            Title = $"100% / {_archiveName}";
            StatusText.Text = warnings > 0
                ? Loc.Progress_CompletedWarnings(warnings, result.SkippedFileCount)
                : Loc.Progress_Completed(result.ExtractedFileCount, result.SkippedFileCount);
            StatusText.Foreground = (Brush)Application.Current.Resources["SystemFillColorSuccessBrush"];
        }
        else if (result.Cancelled)
        {
            Title = $"{Loc.Progress_CancelledTitle} / {_archiveName}";
            StatusText.Text = Loc.Progress_Cancelled;
            StatusText.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        }
        else
        {
            Title = $"{Loc.Progress_ErrorTitle} / {_archiveName}";
            var error = result.Issues.FirstOrDefault(x => x.Severity == ExtractionIssueSeverity.Error);
            var message = error is null ? Loc.Progress_FailedMessage : LocalizationService.GetIssueMessage(error);
            StatusText.Text = Loc.Progress_ErrorStatus(MiddleEllipsis.CompactMiddle(message, 56));
            ToolTipService.SetToolTip(StatusText, message);
            StatusText.Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
        }

        CancelButton.Visibility = Visibility.Collapsed;
        CloseButton.Visibility = Visibility.Visible;
        OpenFolderButton.Visibility = Directory.Exists(result.DestinationPath)
            ? Visibility.Visible
            : Visibility.Collapsed;

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
        _closeRequested = true;
        RequestCancel();
    }

    private void ProgressWindow_Closed(object sender, WindowEventArgs args)
    {
        _closed = true;
        _autoCloseTimer?.Stop();

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
        StatusText.Text = Loc.Progress_Extracting;
        OpenFolderButton.Content = Loc.Common_OpenFolder;
        CancelButton.Content = Loc.Common_Cancel;
        CloseButton.Content = Loc.Common_Close;
    }

    private sealed class DispatcherProgress : IProgress<ExtractionProgress>
    {
        private readonly DispatcherQueue _dispatcherQueue;
        private readonly Action<ExtractionProgress> _report;

        public DispatcherProgress(DispatcherQueue dispatcherQueue, Action<ExtractionProgress> report)
        {
            _dispatcherQueue = dispatcherQueue;
            _report = report;
        }

        public void Report(ExtractionProgress value)
        {
            _dispatcherQueue.TryEnqueue(() => _report(value));
        }
    }
}
