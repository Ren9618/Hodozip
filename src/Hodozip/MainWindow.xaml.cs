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
        ApplyLocalization();

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
            ShowMessage(InfoBarSeverity.Error, Loc.Main_FilePickErrorTitle, ex.Message);
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
            var files = items.OfType<StorageFile>().ToList();
            if (files.Count == 0)
            {
                ShowMessage(InfoBarSeverity.Warning, Loc.Main_NotArchiveTitle, Loc.Main_DropFileMessage);
                return;
            }

            var archiveFiles = files
                .Where(x => ArchiveFormats.IsSupportedArchive(x.Path))
                .ToList();

            if (archiveFiles.Count == 0)
            {
                ShowMessage(
                    InfoBarSeverity.Warning,
                    Loc.Main_UnsupportedTitle,
                    Loc.Main_UnsupportedDropMessage);
                return;
            }

            if (archiveFiles.Count > 1)
            {
                ShowMessage(
                    InfoBarSeverity.Informational,
                    Loc.Main_MultipleTitle,
                    Loc.Main_MultipleMessage);
            }

            BeginExtraction(archiveFiles[0].Path);
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

        if (_progressWindow is not null)
        {
            if (!_progressWindow.IsCompleted)
            {
                _progressWindow.Activate();
                ShowMessage(
                    InfoBarSeverity.Informational,
                    Loc.Main_ExtractingTitle,
                    Loc.Main_ExtractingMessage);
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
            RecentResultTitle.Text = Loc.Main_CompletedTitle;
        }
        else if (result.Cancelled)
        {
            RecentResultTitle.Text = Loc.Main_CancelledTitle;
        }
        else
        {
            RecentResultTitle.Text = Loc.Main_FailedTitle;
            var error = result.Issues.FirstOrDefault(x => x.Severity == ExtractionIssueSeverity.Error);
            if (error is not null)
            {
                ShowMessage(InfoBarSeverity.Error, Loc.Main_FailedTitle, LocalizationService.GetIssueMessage(error));
            }
        }

        var warnings = result.Issues.Count(x => x.Severity == ExtractionIssueSeverity.Warning);
        var errors = result.Issues.Count(x => x.Severity == ExtractionIssueSeverity.Error);

        RecentResultDetails.Text =
            Loc.Main_ResultDestination(result.DestinationPath) + Environment.NewLine +
            Loc.Main_ResultFiles(result.ExtractedFileCount, result.SkippedFileCount) +
            (warnings + errors > 0
                ? Environment.NewLine + Loc.Main_ResultIssues(warnings, errors)
                : string.Empty);
    }

    private void ApplyLocalization()
    {
        TaglineText.Text = Loc.Main_Tagline;
        DropHereText.Text = Loc.Main_DropHere;
        OrText.Text = Loc.Main_Or;
        SelectFileButton.Content = Loc.Main_SelectFile;
        SupportedFormatsText.Text = Loc.Main_SupportedFormats;
        PreviousResultText.Text = Loc.Main_PreviousResult;
        RecentOpenFolderButton.Content = Loc.Common_OpenFolder;
        AboutButton.Content = Loc.Common_About;
        SettingsButton.Content = Loc.Common_Settings;
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
