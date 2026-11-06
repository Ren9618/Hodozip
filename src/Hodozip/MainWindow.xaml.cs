using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;
using Hodozip.Core;
using Hodozip.Services;

namespace Hodozip;

public sealed partial class MainWindow : Window
{
    private readonly Brush _dropZoneDefaultBrush;
    private readonly Brush _dropZoneActiveBrush;
    private ProgressWindow? _progressWindow;
    private SettingsWindow? _settingsWindow;
    private AboutWindow? _aboutWindow;
    private string? _lastDestination;

    public MainWindow()
    {
        InitializeComponent();

        Title = AppInfo.ProductName;
        WindowHelpers.Configure(this, 940, 760);
        VersionText.Text = $"Version {AppInfo.Version}";

        _dropZoneDefaultBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];
        _dropZoneActiveBrush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];

        Closed += (_, _) =>
        {
            _progressWindow?.Close();
            _settingsWindow?.Close();
            _aboutWindow?.Close();
        };
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

            var file = await picker.PickSingleFileAsync();
            if (file is not null)
            {
                BeginExtraction(file.Path);
            }
        }
        catch (Exception ex)
        {
            ShowMessage(InfoBarSeverity.Error, "ファイルを選択できません", ex.Message);
        }
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
            e.DragUIOverride.Caption = "ここにドロップして解凍";
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
                ShowMessage(InfoBarSeverity.Warning, "アーカイブではありません", "ファイルをドロップしてください。");
                return;
            }

            var items = await e.DataView.GetStorageItemsAsync();
            var files = items.OfType<StorageFile>().ToList();
            if (files.Count == 0)
            {
                ShowMessage(InfoBarSeverity.Warning, "アーカイブではありません", "ファイルをドロップしてください。");
                return;
            }

            var archiveFiles = files
                .Where(x => ArchiveFormats.IsSupportedArchive(x.Path))
                .ToList();

            if (archiveFiles.Count == 0)
            {
                ShowMessage(
                    InfoBarSeverity.Warning,
                    "対応していない形式です",
                    "ZIP / 7z / RAR / TAR / GZ / BZ2 / XZ / ZST などのアーカイブをドロップしてください。");
                return;
            }

            if (archiveFiles.Count > 1)
            {
                ShowMessage(
                    InfoBarSeverity.Informational,
                    "複数のアーカイブがドロップされました",
                    "最初の 1 つを解凍します。残りは 1 つずつドロップしてください。");
            }

            BeginExtraction(archiveFiles[0].Path);
        }
        catch (Exception ex)
        {
            ShowMessage(InfoBarSeverity.Error, "ファイルを読み取れません", ex.Message);
        }
    }

    private void BeginExtraction(string archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
        {
            ShowMessage(InfoBarSeverity.Error, "ファイルが見つかりません", archivePath);
            return;
        }

        if (!ArchiveFormats.IsSupportedArchive(archivePath))
        {
            ShowMessage(
                InfoBarSeverity.Warning,
                "対応していない形式です",
                "ZIP / 7z / RAR / TAR / GZ / BZ2 / XZ / ZST などのアーカイブを指定してください。");
            return;
        }

        if (_progressWindow is not null)
        {
            if (!_progressWindow.IsCompleted)
            {
                _progressWindow.Activate();
                ShowMessage(
                    InfoBarSeverity.Informational,
                    "解凍中です",
                    "現在の解凍が終わってから、次のファイルを指定してください。");
                return;
            }

            _progressWindow.Close();
            _progressWindow = null;
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

    private void ProgressWindow_ExtractionCompleted(object? sender, ExtractionResult result)
    {
        _lastDestination = result.DestinationPath;
        RecentResultCard.Visibility = Visibility.Visible;
        RecentOpenFolderButton.Visibility = Directory.Exists(result.DestinationPath)
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (result.Success)
        {
            RecentResultTitle.Text = "解凍が完了しました";
        }
        else if (result.Cancelled)
        {
            RecentResultTitle.Text = "解凍をキャンセルしました";
        }
        else
        {
            RecentResultTitle.Text = "解凍できませんでした";
            var error = result.Issues.FirstOrDefault(x => x.Severity == ExtractionIssueSeverity.Error);
            if (error is not null)
            {
                ShowMessage(InfoBarSeverity.Error, "解凍できませんでした", error.Message);
            }
        }

        var warnings = result.Issues.Count(x => x.Severity == ExtractionIssueSeverity.Warning);
        var errors = result.Issues.Count(x => x.Severity == ExtractionIssueSeverity.Error);

        RecentResultDetails.Text =
            $"解凍先: {result.DestinationPath}{Environment.NewLine}" +
            $"ファイル: {result.ExtractedFileCount} 件（スキップ {result.SkippedFileCount} 件）" +
            (warnings + errors > 0
                ? $"{Environment.NewLine}警告 {warnings} 件 / エラー {errors} 件"
                : string.Empty);
    }

    private void RecentOpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastDestination is not null)
        {
            UiHelpers.OpenFolder(_lastDestination);
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        var window = new SettingsWindow();
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
