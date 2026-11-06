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

    private readonly string _archivePath;
    private readonly bool _isCommandLine;
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

    public ProgressWindow(string archivePath, bool isCommandLine)
    {
        InitializeComponent();

        _archivePath = archivePath;
        _isCommandLine = isCommandLine;
        _settings = SettingsService.Load();

        Title = $"{AppInfo.ProductName} - 解凍";
        WindowHelpers.Configure(this, 790, 660);

        ArchiveNameText.Text = Path.GetFileName(archivePath);
        ToolTipService.SetToolTip(ArchiveNameText, archivePath);

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
            OverwritePolicy = OverwritePolicy.Ask,
            OverwriteResolver = ResolveOverwriteAsync,
            PasswordProvider = ProvidePasswordAsync,
            Progress = new DispatcherProgress(DispatcherQueue, UpdateProgress),
        };

        ExtractionResult result;
        try
        {
            result = await _extractor
                .ExtractAsync(new ExtractionRequest(_archivePath, null, options), _cancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result = CreateFailureResult("解凍をキャンセルしました。", cancelled: true);
        }
        catch (Exception ex)
        {
            result = CreateFailureResult($"解凍中に予期しないエラーが発生しました: {ex.Message}", cancelled: false);
        }

        DispatcherQueue.TryEnqueue(() => CompleteOnUiThread(result));
    }

    private ExtractionResult CreateFailureResult(string message, bool cancelled)
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
                    message),
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
            ProgressBar.IsIndeterminate = false;
            ProgressBar.Maximum = Math.Max(1, progress.TotalBytes);
            ProgressBar.Value = Math.Min(progress.ProcessedBytes, ProgressBar.Maximum);
            PercentText.Text = $"{Math.Min(100d, progress.ProcessedBytes * 100d / progress.TotalBytes):0}%";
            BytesText.Text = $"{UiHelpers.FormatBytes(progress.ProcessedBytes)} / {UiHelpers.FormatBytes(progress.TotalBytes)}";
        }
        else
        {
            ProgressBar.IsIndeterminate = true;
            BytesText.Text = UiHelpers.FormatBytes(progress.ProcessedBytes);
        }

        CurrentFileText.Text = string.IsNullOrEmpty(progress.CurrentFile)
            ? " "
            : progress.CurrentFile;
    }

    private void ShowResult(ExtractionResult result)
    {
        _destinationPath = result.DestinationPath;

        BusyRing.IsActive = false;
        BusyRing.Visibility = Visibility.Collapsed;
        ProgressBar.IsIndeterminate = false;
        if (ProgressBar.Maximum <= 1)
        {
            ProgressBar.Maximum = 1;
            ProgressBar.Value = 1;
        }

        PercentText.Text = "100%";
        if (string.IsNullOrEmpty(CurrentFileText.Text.Trim()))
        {
            CurrentFileText.Text = " ";
        }

        if (result.Success)
        {
            ResultSymbol.Text = "✓";
            ResultSymbol.Foreground = (Brush)Application.Current.Resources["SystemFillColorSuccessBrush"];
            ResultTitle.Text = result.HasWarnings ? "解凍が完了しました（警告あり）" : "解凍が完了しました";
            StatusTitle.Text = "処理が完了しました";
        }
        else if (result.Cancelled)
        {
            ResultSymbol.Text = "×";
            ResultSymbol.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
            ResultTitle.Text = "解凍をキャンセルしました";
            StatusTitle.Text = "処理を中止しました";
        }
        else
        {
            ResultSymbol.Text = "!";
            ResultSymbol.Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
            ResultTitle.Text = "解凍できませんでした";
            StatusTitle.Text = "エラーが発生しました";
        }

        DestinationText.Text = result.DestinationPath;
        var warnings = result.Issues.Count(x => x.Severity == ExtractionIssueSeverity.Warning);
        var errors = result.Issues.Count(x => x.Severity == ExtractionIssueSeverity.Error);
        ResultStatsText.Text =
            $"ファイル: {result.ExtractedFileCount} 件（スキップ {result.SkippedFileCount} 件）" +
            (warnings + errors > 0 ? $"／警告 {warnings} 件・エラー {errors} 件" : string.Empty);

        BuildIssueList(result);

        ResultPanel.Visibility = Visibility.Visible;
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

    private void BuildIssueList(ExtractionResult result)
    {
        IssuesPanel.Children.Clear();

        if (result.Issues.Count == 0)
        {
            IssuesExpander.Visibility = Visibility.Collapsed;
            return;
        }

        IssuesExpander.Visibility = Visibility.Visible;
        IssuesExpander.Header = $"警告・エラー ({result.Issues.Count})";
        IssuesExpander.IsExpanded = result.HasErrors;

        foreach (var issue in result.Issues)
        {
            var label = issue.Severity == ExtractionIssueSeverity.Error ? "エラー" : "警告";
            var text = string.IsNullOrEmpty(issue.EntryPath)
                ? $"[{label}] {issue.Message}"
                : $"[{label}] {issue.EntryPath}{Environment.NewLine}{issue.Message}";

            IssuesPanel.Children.Add(new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.Resources[
                    issue.Severity == ExtractionIssueSeverity.Error
                        ? "SystemFillColorCriticalBrush"
                        : "SystemFillColorCautionBrush"],
            });
        }
    }

    private Task<OverwriteDecision> ResolveOverwriteAsync(
        OverwriteConflict conflict,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(OverwriteDecision.Skip);
        }

        return RunOnUiThreadAsync(async () =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return OverwriteDecision.Skip;
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
        AutoCloseText.Text = $"{_autoCloseSeconds} 秒後に自動で閉じます。";

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

            AutoCloseText.Text = $"{_autoCloseSeconds} 秒後に自動で閉じます。";
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
        StatusTitle.Text = "キャンセルしています...";
        CurrentFileText.Text = " ";
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
