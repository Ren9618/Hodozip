using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;
using Hodozip.Core;
using Hodozip.Dialogs;
using Hodozip.Services;

namespace Hodozip;

public sealed partial class MainWindow : Window
{
    private readonly Brush _dropZoneDefaultBrush;
    private readonly Brush _dropZoneActiveBrush;
    private readonly UpdateService _updateService = new();
    private ProgressWindow? _progressWindow;
    private CompressionOptionsWindow? _compressionOptionsWindow;
    private SettingsWindow? _settingsWindow;
    private AboutWindow? _aboutWindow;
    private UpdateRelease? _pendingUpdate;
    private string? _lastDestination;
    private bool _closed;

    public MainWindow()
    {
        InitializeComponent();

        Title = AppInfo.ProductName;
        WindowHelpers.Configure(this, 860, 680);
        VersionText.Text = $"Version {AppInfo.Version}";
        ApplyLocalization();

        _dropZoneDefaultBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];
        _dropZoneActiveBrush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];

        JobsList.ItemsSource = JobRegistry.Jobs;
        JobRegistry.Jobs.CollectionChanged += Jobs_CollectionChanged;
        UpdateJobsCard();

        Closed += (_, _) =>
        {
            _closed = true;
            _progressWindow?.Close();
            _compressionOptionsWindow?.Close();
            _settingsWindow?.Close();
            _aboutWindow?.Close();
        };

        _ = CheckForUpdatesAsync();
    }

    private async void SelectFileButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.Downloads,
                ViewMode = PickerViewMode.List,
            };

            foreach (var extension in GetPickerExtensions())
            {
                picker.FileTypeFilter.Add(extension);
            }

            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

            var files = await picker.PickMultipleFilesAsync();
            if (files is null || files.Count == 0)
            {
                return;
            }

            var paths = files
                .Select(x => x.Path)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();

            if (paths.Count == 1)
            {
                BeginExtraction(paths[0]);
            }
            else if (paths.Count > 1)
            {
                await BeginBatchExtractionAsync(paths);
            }
        }
        catch (Exception ex)
        {
            ShowMessage(InfoBarSeverity.Error, Loc.Main_FilePickErrorTitle, ex.Message);
        }
    }

    private void CompressButton_Click(object sender, RoutedEventArgs e)
    {
        OpenCompressionOptions(null);
    }

    private void DropZone_DragEnter(object sender, DragEventArgs e)
    {
        DropZone.BorderBrush = _dropZoneActiveBrush;
    }

    private void DropZone_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = Loc.Main_DropCaption;
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsContentVisible = true;
        }
        else
        {
            e.AcceptedOperation = DataPackageOperation.None;
        }
    }

    private void DropZone_DragLeave(object sender, DragEventArgs e)
    {
        DropZone.BorderBrush = _dropZoneDefaultBrush;
    }

    private async void DropZone_Drop(object sender, DragEventArgs e)
    {
        DropZone.BorderBrush = _dropZoneDefaultBrush;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.Handled = true;

        try
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                ShowMessage(InfoBarSeverity.Warning, Loc.Main_NotArchiveTitle, Loc.Main_DropFileMessage);
                return;
            }

            var items = await e.DataView.GetStorageItemsAsync();
            var paths = items
                .Select(x => x.Path)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Where(x => File.Exists(x) || Directory.Exists(x))
                .ToList();

            if (paths.Count == 0)
            {
                ShowMessage(InfoBarSeverity.Warning, Loc.Main_NotArchiveTitle, Loc.Main_DropFileMessage);
                return;
            }

            var archiveFiles = paths
                .Where(x => File.Exists(x) && ArchiveFormats.IsSupportedArchive(x))
                .ToList();

            if (archiveFiles.Count == 0)
            {
                // アーカイブ以外は圧縮の入力として扱う。
                OpenCompressionOptions(paths);
                return;
            }

            if (archiveFiles.Count > 1)
            {
                await BeginBatchExtractionAsync(archiveFiles);
                return;
            }

            BeginExtraction(archiveFiles[0]);
        }
        catch (Exception ex)
        {
            ShowMessage(InfoBarSeverity.Error, Loc.Main_ReadErrorTitle, ex.Message);
        }
    }

    private void BeginExtraction(string archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
        {
            ShowMessage(InfoBarSeverity.Error, Loc.Main_FileNotFoundTitle, archivePath);
            return;
        }

        if (!ArchiveFormats.IsSupportedArchive(archivePath))
        {
            ShowMessage(
                InfoBarSeverity.Warning,
                Loc.Main_UnsupportedTitle,
                Loc.Main_UnsupportedFileMessage);
            return;
        }

        if (!TryPrepareProgressWindow())
        {
            return;
        }

        StatusInfoBar.IsOpen = false;

        var progressWindow = new ProgressWindow(archivePath, isCommandLine: false);
        _progressWindow = progressWindow;
        progressWindow.ExtractionCompleted += ProgressWindow_ExtractionCompleted;
        progressWindow.Closed += (_, _) =>
        {
            if (ReferenceEquals(_progressWindow, progressWindow))
            {
                _progressWindow = null;
            }
        };

        progressWindow.Activate();
    }

    /// <summary>複数のアーカイブを 1 つのフォルダへまとめて解凍する。</summary>
    private async Task BeginBatchExtractionAsync(IReadOnlyList<string> archivePaths)
    {
        var archives = archivePaths
            .Where(x => !string.IsNullOrWhiteSpace(x) && File.Exists(x) && ArchiveFormats.IsSupportedArchive(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (archives.Count == 0)
        {
            ShowMessage(
                InfoBarSeverity.Warning,
                Loc.Main_UnsupportedTitle,
                Loc.Main_UnsupportedFileMessage);
            return;
        }

        if (archives.Count == 1)
        {
            BeginExtraction(archives[0]);
            return;
        }

        if (!TryPrepareProgressWindow())
        {
            return;
        }

        var xamlRoot = RootGrid.XamlRoot;
        if (xamlRoot is null)
        {
            return;
        }

        var destination = await BatchExtractionDialog.ShowAsync(
            xamlRoot,
            WindowNative.GetWindowHandle(this),
            archives.Count,
            SuggestBatchDestination(archives));

        if (destination is null)
        {
            return;
        }

        // ダイアログ表示中に別の解凍が始まった場合に備えて再確認する。
        if (!TryPrepareProgressWindow())
        {
            return;
        }

        StatusInfoBar.IsOpen = false;

        var progressWindow = new ProgressWindow(archives, destination, isCommandLine: false);
        _progressWindow = progressWindow;
        progressWindow.ExtractionCompleted += ProgressWindow_ExtractionCompleted;
        progressWindow.Closed += (_, _) =>
        {
            if (ReferenceEquals(_progressWindow, progressWindow))
            {
                _progressWindow = null;
            }
        };

        progressWindow.Activate();
    }

    /// <summary>
    /// コマンド ライン（エクスプローラーでの複数選択など）からまとめて解凍を開始する。
    /// ウィンドウの読み込み完了を待ってから解凍先ダイアログを表示する。
    /// </summary>
    public async Task BeginBatchExtractionFromCommandLineAsync(IReadOnlyList<string> archivePaths)
    {
        try
        {
            await WaitForContentLoadedAsync();
            Activate();
            await BeginBatchExtractionAsync(archivePaths);
        }
        catch (Exception ex)
        {
            ShowMessage(InfoBarSeverity.Error, Loc.Main_ReadErrorTitle, ex.Message);
        }
    }

    private Task WaitForContentLoadedAsync()
    {
        if (RootGrid.IsLoaded)
        {
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void OnLoaded(object sender, RoutedEventArgs e)
        {
            RootGrid.Loaded -= OnLoaded;
            completion.TrySetResult(null);
        }

        RootGrid.Loaded += OnLoaded;
        return completion.Task;
    }

    private static string SuggestBatchDestination(IReadOnlyList<string> archivePaths)
    {
        var settings = SettingsService.Load();
        if (settings.ExtractionDestination == DestinationSelectionMode.FixedFolder &&
            !string.IsNullOrWhiteSpace(settings.ExtractionDestinationFolder))
        {
            return settings.ExtractionDestinationFolder;
        }

        var baseDirectory = Path.GetDirectoryName(archivePaths[0]);
        if (string.IsNullOrEmpty(baseDirectory))
        {
            baseDirectory = Environment.CurrentDirectory;
        }

        return UiHelpers.GetUniqueFolderPath(baseDirectory, Loc.Batch_DefaultFolderName);
    }

    private void StartCompression(CompressionRequest request)
    {
        if (!TryPrepareProgressWindow())
        {
            return;
        }

        StatusInfoBar.IsOpen = false;

        var progressWindow = new ProgressWindow(request, isCommandLine: false);
        _progressWindow = progressWindow;
        progressWindow.CompressionCompleted += ProgressWindow_CompressionCompleted;
        progressWindow.Closed += (_, _) =>
        {
            if (ReferenceEquals(_progressWindow, progressWindow))
            {
                _progressWindow = null;
            }
        };

        progressWindow.Activate();
    }

    private bool TryPrepareProgressWindow()
    {
        if (_progressWindow is null)
        {
            return true;
        }

        if (!_progressWindow.IsCompleted)
        {
            _progressWindow.ShowFromBackground();
            ShowMessage(
                InfoBarSeverity.Informational,
                Loc.Main_ExtractingTitle,
                Loc.Main_ExtractingMessage);
            return false;
        }

        _progressWindow.Close();
        _progressWindow = null;
        return true;
    }

    private void ProgressWindow_ExtractionCompleted(object? sender, ExtractionResult result)
    {
        _lastDestination = result.DestinationPath;
        RecentResultCard.Visibility = Visibility.Visible;
        RecentOpenFolderButton.Visibility = Directory.Exists(result.DestinationPath)
            ? Visibility.Visible
            : Visibility.Collapsed;

        var isBatch = result.ArchiveCount > 1;

        if (result.Success)
        {
            RecentResultTitle.Text = isBatch ? Loc.Main_BatchCompletedTitle : Loc.Main_CompletedTitle;
        }
        else if (result.Cancelled)
        {
            RecentResultTitle.Text = Loc.Main_CancelledTitle;
        }
        else
        {
            var partial = isBatch && result.SucceededArchiveCount > 0;
            RecentResultTitle.Text = partial ? Loc.Main_BatchPartialTitle : Loc.Main_FailedTitle;
            var error = result.Issues.FirstOrDefault(x => x.Severity == ExtractionIssueSeverity.Error);
            if (error is not null)
            {
                ShowMessage(
                    partial ? InfoBarSeverity.Warning : InfoBarSeverity.Error,
                    RecentResultTitle.Text,
                    FormatIssueMessage(error));
            }
        }

        var warnings = result.Issues.Count(x => x.Severity == ExtractionIssueSeverity.Warning);
        var errors = result.Issues.Count(x => x.Severity == ExtractionIssueSeverity.Error);

        var details = new List<string> { Loc.Main_ResultDestination(result.DestinationPath) };
        if (isBatch)
        {
            details.Add(Loc.Main_ResultArchives(result.SucceededArchiveCount, result.ArchiveCount));
        }

        details.Add(Loc.Main_ResultFiles(result.ExtractedFileCount, result.SkippedFileCount));
        if (warnings + errors > 0)
        {
            details.Add(Loc.Main_ResultIssues(warnings, errors));
        }

        RecentResultDetails.Text = string.Join(Environment.NewLine, details);
    }

    private static string FormatIssueMessage(ExtractionIssue issue)
    {
        var message = LocalizationService.GetIssueMessage(issue);
        return string.IsNullOrEmpty(issue.ArchivePath)
            ? message
            : $"{Path.GetFileName(issue.ArchivePath)}: {message}";
    }

    private void ProgressWindow_CompressionCompleted(object? sender, CompressionResult result)
    {
        var outputDirectory = string.IsNullOrWhiteSpace(result.OutputPath)
            ? null
            : Path.GetDirectoryName(result.OutputPath);
        _lastDestination = outputDirectory;
        RecentResultCard.Visibility = Visibility.Visible;
        RecentOpenFolderButton.Visibility = outputDirectory is not null && Directory.Exists(outputDirectory)
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (result.Success)
        {
            RecentResultTitle.Text = Loc.Main_CompressionCompletedTitle;
        }
        else if (result.Cancelled)
        {
            RecentResultTitle.Text = Loc.Main_CompressionCancelledTitle;
        }
        else
        {
            RecentResultTitle.Text = Loc.Main_CompressionFailedTitle;
            ShowMessage(
                InfoBarSeverity.Error,
                Loc.Main_CompressionFailedTitle,
                LocalizationService.GetCompressionError(result));
        }

        RecentResultDetails.Text =
            Loc.Main_CompressionResultOutput(result.OutputPath) + Environment.NewLine +
            Loc.Main_CompressionResultFiles(
                result.FileCount,
                UiHelpers.FormatBytes(result.SourceBytes),
                UiHelpers.FormatBytes(result.OutputBytes)) +
            (result.PasswordIgnored
                ? Environment.NewLine + Loc.Progress_PasswordIgnored
                : string.Empty);
    }

    private void OpenCompressionOptions(IReadOnlyList<string>? sources)
    {
        if (_compressionOptionsWindow is not null)
        {
            _compressionOptionsWindow.Activate();
            return;
        }

        var window = new CompressionOptionsWindow(sources);
        _compressionOptionsWindow = window;
        window.CompressionRequested += (_, request) => StartCompression(request);
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_compressionOptionsWindow, window))
            {
                _compressionOptionsWindow = null;
            }
        };
        window.Activate();
    }

    private async Task CheckForUpdatesAsync()
    {
        var release = await _updateService.CheckForUpdateAsync();
        if (release is null || _closed)
        {
            return;
        }

        _pendingUpdate = release;
        UpdateBannerTitleText.Text = Loc.Main_UpdateAvailableTitle;
        UpdateBannerMessageText.Text = Loc.Main_UpdateAvailableMessage(release.Version.ToString())
            + (AppInfo.IsSetupInstalled ? string.Empty : " " + Loc.Main_UpdatePortableMessage);
        UpdateNowButton.Content = Loc.Main_UpdateNow;
        UpdateLaterButton.Content = Loc.Main_UpdateLater;
        UpdateSkipButton.Content = Loc.Main_UpdateSkip;
        UpdateBanner.Visibility = Visibility.Visible;
    }

    private async void UpdateNowButton_Click(object sender, RoutedEventArgs e)
    {
        var release = _pendingUpdate;
        if (release is null)
        {
            return;
        }

        if (!AppInfo.IsSetupInstalled)
        {
            UpdateService.OpenReleasePage(release);
            UpdateBanner.Visibility = Visibility.Collapsed;
            return;
        }

        UpdateNowButton.IsEnabled = false;
        UpdateLaterButton.IsEnabled = false;
        UpdateSkipButton.IsEnabled = false;
        UpdateBannerMessageText.Text = Loc.Main_UpdateDownloading(0);

        var progress = new Progress<double>(value =>
        {
            var percent = (int)Math.Round(value * 100);
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_closed)
                {
                    UpdateBannerMessageText.Text = Loc.Main_UpdateDownloading(percent);
                }
            });
        });

        var installerPath = await _updateService.DownloadSetupAsync(release, progress);
        if (installerPath is null)
        {
            UpdateBannerMessageText.Text = Loc.Main_UpdateAvailableMessage(release.Version.ToString())
                + (AppInfo.IsSetupInstalled ? string.Empty : " " + Loc.Main_UpdatePortableMessage);
            UpdateNowButton.IsEnabled = true;
            UpdateLaterButton.IsEnabled = true;
            UpdateSkipButton.IsEnabled = true;
            ShowMessage(InfoBarSeverity.Error, Loc.Main_UpdateFailedTitle, Loc.Main_UpdatePortableMessage);
            return;
        }

        if (UpdateService.TryLaunchInstaller(installerPath))
        {
            Close();
            return;
        }

        UpdateBannerMessageText.Text = Loc.Main_UpdateAvailableMessage(release.Version.ToString());
        UpdateNowButton.IsEnabled = true;
        UpdateLaterButton.IsEnabled = true;
        UpdateSkipButton.IsEnabled = true;
        ShowMessage(InfoBarSeverity.Error, Loc.Main_UpdateFailedTitle, Loc.Main_UpdatePortableMessage);
    }

    private void UpdateLaterButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateBanner.Visibility = Visibility.Collapsed;
    }

    private void UpdateSkipButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate is not null)
        {
            UpdateService.SkipVersion(_pendingUpdate);
        }

        UpdateBanner.Visibility = Visibility.Collapsed;
    }

    private void ApplyLocalization()
    {
        TaglineText.Text = Loc.Main_Tagline;
        DropHereText.Text = Loc.Main_DropHere;
        OrText.Text = Loc.Main_Or;
        SelectFileButton.Content = Loc.Main_SelectFile;
        CompressButton.Content = Loc.Main_Compress;
        SupportedFormatsText.Text = Loc.Main_SupportedFormats;
        PreviousResultText.Text = Loc.Main_PreviousResult;
        RecentOpenFolderButton.Content = Loc.Common_OpenFolder;
        SupportLinkButton.Content = Loc.Common_Support;
        AboutButton.Content = Loc.Common_About;
        SettingsButton.Content = Loc.Common_Settings;
        JobsTitleText.Text = Loc.Main_JobsTitle;
        JobsHintText.Text = Loc.Main_JobsHint;
    }

    private void Jobs_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateJobsCard();
    }

    private void UpdateJobsCard()
    {
        // バックグラウンド設定が ON のときだけ、実行中のジョブをここに出す。
        var show = JobRegistry.Jobs.Count > 0 && SettingsService.Load().RunInBackground;
        JobsCard.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void JobShowButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is JobInfo job)
        {
            job.Window.ShowFromBackground();
        }
    }

    private void JobCancelButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is JobInfo job)
        {
            job.Window.CancelFromHost();
        }
    }

    private void RecentOpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastDestination is not null)
        {
            UiHelpers.OpenFolder(_lastDestination);
        }
    }

    private async void SupportLinkButton_Click(object sender, RoutedEventArgs e)
    {
        if (RootGrid.XamlRoot is { } xamlRoot)
        {
            await DonationDialog.ShowAsync(xamlRoot);
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        SettingsWindow window;
        try
        {
            window = new SettingsWindow();
        }
        catch (Exception ex)
        {
            ShowMessage(InfoBarSeverity.Error, Loc.Common_Settings, ex.Message);
            return;
        }

        _settingsWindow = window;
        window.Closed += (_, _) => _settingsWindow = null;
        window.Activate();
    }

    private void AboutButton_Click(object sender, RoutedEventArgs e)
    {
        if (_aboutWindow is not null)
        {
            _aboutWindow.Activate();
            return;
        }

        var window = new AboutWindow();
        _aboutWindow = window;
        window.Closed += (_, _) => _aboutWindow = null;
        window.Activate();
    }

    private void ShowMessage(InfoBarSeverity severity, string title, string message)
    {
        StatusInfoBar.Severity = severity;
        StatusInfoBar.Title = title;
        StatusInfoBar.Message = message;
        StatusInfoBar.IsOpen = true;
    }

    private static IEnumerable<string> GetPickerExtensions()
    {
        return ArchiveFormats.SupportedExtensions
            .Select(x => Path.GetExtension(x))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
    }
}
