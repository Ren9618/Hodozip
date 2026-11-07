using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;
using Hodozip.Core;
using Hodozip.Services;

namespace Hodozip;

public sealed partial class CompressionOptionsWindow : Window
{
    private readonly ObservableCollection<string> _sources = new();
    private bool _initializing = true;

    public CompressionOptionsWindow(IReadOnlyList<string>? initialSources = null)
    {
        InitializeComponent();

        Title = Loc.Compression_WindowTitle(AppInfo.ProductName);
        WindowHelpers.Configure(this, 760, 820);

        SourcesList.ItemsSource = _sources;
        ApplyLocalization();

        if (initialSources is not null)
        {
            foreach (var source in initialSources)
            {
                AddSource(source);
            }
        }

        var settings = SettingsService.Load();
        var defaultFolder = ResolveDefaultFolder(initialSources, settings);
        OutputFolderBox.Text = defaultFolder;
        OutputNameBox.Text = ResolveDefaultName(initialSources, ArchiveCreationFormat.Zip);
        FormatComboBox.SelectedIndex = 0;
        LevelComboBox.SelectedIndex = 1;
        ZipEncodingComboBox.SelectedIndex = 0;

        _initializing = false;
        UpdateSourceState();
        UpdateFormatState();
    }

    public event EventHandler<CompressionRequest>? CompressionRequested;

    private void ApplyLocalization()
    {
        CompressionTitleText.Text = Loc.Compression_Title;
        CompressionSubtitleText.Text = Loc.Compression_Subtitle;
        SourcesSectionText.Text = Loc.Compression_Sources;
        AddFilesButton.Content = Loc.Compression_AddFiles;
        AddFolderButton.Content = Loc.Compression_AddFolder;
        RemoveSelectedButton.Content = Loc.Compression_RemoveSelected;
        OutputSectionText.Text = Loc.Compression_Output;
        OutputFolderLabelText.Text = Loc.Compression_OutputFolder;
        OutputNameLabelText.Text = Loc.Compression_OutputName;
        BrowseFolderButton.Content = Loc.Common_Browse;
        FormatLabelText.Text = Loc.Compression_Format;
        LevelLabelText.Text = Loc.Compression_Level;
        PasswordLabelText.Text = Loc.Compression_Password;
        PasswordBox.PlaceholderText = Loc.Compression_PasswordPlaceholder;
        PasswordNoteText.Text = Loc.Compression_PasswordNote;
        ZipEncodingLabelText.Text = Loc.Compression_ZipEncoding;
        ZipEncodingNoteText.Text = Loc.Compression_ZipEncodingNote;
        CollisionNoteText.Text = Loc.Compression_CollisionNote;
        CancelButton.Content = Loc.Common_Cancel;
        StartButton.Content = Loc.Compression_Start;
        SourcesHintText.Text = Loc.Compression_NoSources;

        SetItemContent(FormatComboBox, Loc.Compression_FormatZip, Loc.Compression_Format7z);
        SetItemContent(LevelComboBox, Loc.Compression_LevelStore, Loc.Compression_LevelNormal, Loc.Compression_LevelMaximum);
        SetItemContent(ZipEncodingComboBox, Loc.Compression_ZipEncodingUtf8, Loc.Compression_ZipEncodingShiftJis);
    }

    private static void SetItemContent(ComboBox comboBox, params string[] contents)
    {
        for (var index = 0; index < comboBox.Items.Count && index < contents.Length; index++)
        {
            if (comboBox.Items[index] is ComboBoxItem item)
            {
                item.Content = contents[index];
            }
        }
    }

    private static string ResolveDefaultFolder(
        IReadOnlyList<string>? sources,
        AppSettings settings)
    {
        if (sources is { Count: > 0 })
        {
            var defaultPath = CompressionDefaults.GetDefaultOutputPath(
                sources,
                ArchiveCreationFormat.Zip);
            var folder = Path.GetDirectoryName(defaultPath);
            if (!string.IsNullOrWhiteSpace(folder))
            {
                return folder;
            }
        }

        if (!string.IsNullOrWhiteSpace(settings.LastCompressionDirectory)
            && Directory.Exists(settings.LastCompressionDirectory))
        {
            return settings.LastCompressionDirectory;
        }

        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return string.IsNullOrWhiteSpace(documents)
            ? Environment.CurrentDirectory
            : documents;
    }

    private static string ResolveDefaultName(
        IReadOnlyList<string>? sources,
        ArchiveCreationFormat format)
    {
        if (sources is not { Count: > 0 })
        {
            return "Hodozip" + CompressionDefaults.GetExtension(format);
        }

        var defaultPath = CompressionDefaults.GetDefaultOutputPath(sources, format);
        var name = Path.GetFileName(defaultPath);
        return string.IsNullOrWhiteSpace(name)
            ? "Hodozip" + CompressionDefaults.GetExtension(format)
            : name;
    }

    private void AddSource(string path)
    {
        string fullPath;
        try
        {
            fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim().Trim('"')));
        }
        catch
        {
            return;
        }

        if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
        {
            return;
        }

        if (_sources.Any(x => string.Equals(x, fullPath, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        _sources.Add(fullPath);
        UpdateSourceState();
    }

    private void UpdateSourceState()
    {
        SourcesHintText.Visibility = _sources.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        RemoveSelectedButton.IsEnabled = SourcesList.SelectedItems.Count > 0;
    }

    private void UpdateFormatState()
    {
        var format = GetSelectedFormat();
        ZipEncodingComboBox.IsEnabled = format == ArchiveCreationFormat.Zip;
        ZipEncodingNoteText.Opacity = format == ArchiveCreationFormat.Zip ? 1 : 0.5;
    }

    private ArchiveCreationFormat GetSelectedFormat()
    {
        return string.Equals(
            (FormatComboBox.SelectedItem as ComboBoxItem)?.Tag as string,
            "SevenZip",
            StringComparison.OrdinalIgnoreCase)
            ? ArchiveCreationFormat.SevenZip
            : ArchiveCreationFormat.Zip;
    }

    private ArchiveCompressionLevel GetSelectedLevel()
    {
        var tag = (LevelComboBox.SelectedItem as ComboBoxItem)?.Tag as string;
        return Enum.TryParse<ArchiveCompressionLevel>(tag, ignoreCase: true, out var level)
            ? level
            : ArchiveCompressionLevel.Normal;
    }

    private ZipNameEncoding GetSelectedZipEncoding()
    {
        return string.Equals(
            (ZipEncodingComboBox.SelectedItem as ComboBoxItem)?.Tag as string,
            "ShiftJis",
            StringComparison.OrdinalIgnoreCase)
            ? ZipNameEncoding.ShiftJis
            : ZipNameEncoding.Utf8;
    }

    private void FormatComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        UpdateFormatState();

        var format = GetSelectedFormat();
        var extension = CompressionDefaults.GetExtension(format);
        var name = OutputNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            OutputNameBox.Text = "Hodozip" + extension;
            return;
        }

        var currentExtension = Path.GetExtension(name);
        if (string.Equals(currentExtension, ".zip", StringComparison.OrdinalIgnoreCase)
            || string.Equals(currentExtension, ".7z", StringComparison.OrdinalIgnoreCase))
        {
            OutputNameBox.Text = Path.GetFileNameWithoutExtension(name) + extension;
        }
        else
        {
            OutputNameBox.Text = name + extension;
        }
    }

    private async void AddFilesButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                ViewMode = PickerViewMode.List,
            };
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

            var files = await picker.PickMultipleFilesAsync();
            if (files is not null)
            {
                foreach (var file in files)
                {
                    AddSource(file.Path);
                }
            }
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private async void AddFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            };
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
            {
                AddSource(folder.Path);
            }
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void RemoveSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = SourcesList.SelectedItems.OfType<string>().ToList();
        foreach (var item in selected)
        {
            _sources.Remove(item);
        }

        UpdateSourceState();
    }

    private void SourcesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSourceState();
    }

    private async void BrowseFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            };
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
            {
                OutputFolderBox.Text = folder.Path;
            }
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        OptionsInfoBar.IsOpen = false;

        if (_sources.Count == 0)
        {
            ShowError(Loc.Compression_ErrorNoSources);
            return;
        }

        var folderText = OutputFolderBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(folderText))
        {
            ShowError(Loc.Compression_ErrorFolderMissing);
            return;
        }

        var name = OutputNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            ShowError(Loc.Compression_ErrorNoOutputName);
            return;
        }

        if (!string.Equals(name, Path.GetFileName(name), StringComparison.Ordinal)
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name.EndsWith('.') || name.EndsWith(' '))
        {
            ShowError(Loc.Compression_ErrorInvalidName);
            return;
        }

        var format = GetSelectedFormat();
        var extension = CompressionDefaults.GetExtension(format);
        if (string.IsNullOrWhiteSpace(Path.GetExtension(name)))
        {
            name += extension;
        }
        else if (!string.Equals(Path.GetExtension(name), extension, StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(Path.GetExtension(name), ".zip", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetExtension(name), ".7z", StringComparison.OrdinalIgnoreCase))
            {
                name = Path.GetFileNameWithoutExtension(name) + extension;
            }
            else
            {
                name += extension;
            }
        }

        string folder;
        try
        {
            folder = Path.GetFullPath(folderText.Trim('"'));
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex)
        {
            ShowError(Loc.Compression_ErrorFolderCreate(ex.Message));
            return;
        }

        var outputPath = Path.Combine(folder, name);
        var password = PasswordBox.Password;
        var request = new CompressionRequest(
            _sources.ToList(),
            outputPath,
            new CompressionOptions
            {
                Format = format,
                Level = GetSelectedLevel(),
                Password = string.IsNullOrEmpty(password) ? null : password,
                ZipNameEncoding = GetSelectedZipEncoding(),
                Overwrite = false,
            });

        var settings = SettingsService.Load();
        settings.LastCompressionDirectory = folder;
        SettingsService.Save(settings);

        CompressionRequested?.Invoke(this, request);
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ShowError(string message)
    {
        OptionsInfoBar.Severity = InfoBarSeverity.Error;
        OptionsInfoBar.Title = Loc.Compression_ErrorTitle;
        OptionsInfoBar.Message = message;
        OptionsInfoBar.IsOpen = true;
    }
}
