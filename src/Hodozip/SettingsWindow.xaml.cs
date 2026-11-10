using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;
using Hodozip.Core;
using Hodozip.Dialogs;
using Hodozip.Services;

namespace Hodozip;

public sealed partial class SettingsWindow : Window
{
    private bool _suppressLegacyToggle;
    private bool _legacyToggleBusy;
    private bool _initializing = true;

    public SettingsWindow()
    {
        InitializeComponent();

        Title = Loc.Settings_WindowTitle(AppInfo.ProductName);
        WindowHelpers.Configure(this, 700, 900);

        ApplyLocalization();

        var settings = SettingsService.Load();
        OpenFolderToggle.IsOn = settings.OpenFolderAfterExtraction;
        CloseAfterExtractToggle.IsOn = settings.CloseAfterAutoExtract;
        SkipNewerToggle.IsOn = settings.SkipExistingIfNewer;
        CheckUpdatesToggle.IsOn = AppInfo.SupportsAutoUpdate && settings.CheckForUpdatesOnStartup;
        CheckUpdatesToggle.IsEnabled = AppInfo.SupportsAutoUpdate;
        SelectByTag(DestinationRuleComboBox, settings.ExtractionDestination.ToString());
        DestinationFolderBox.Text = settings.ExtractionDestinationFolder ?? string.Empty;
        SelectByTag(CompressionDestinationComboBox, settings.CompressionDestination.ToString());
        CompressionFolderBox.Text = settings.CompressionDestinationFolder ?? string.Empty;
        SelectByTag(FolderCollisionComboBox, settings.FolderCollisionPolicy.ToString());
        SelectByTag(OverwritePolicyComboBox, settings.OverwritePolicy.ToString());
        SelectByTag(LanguageComboBox, settings.Language.ToString());
        SelectParallelism(settings.MaxDegreeOfParallelism);
        NotifyToggle.IsOn = settings.NotifyOnCompletion;
        BackgroundToggle.IsOn = settings.RunInBackground;
        SelectByTag(SoundComboBox, settings.NotificationSound.ToString());
        SoundFileBox.Text = settings.NotificationSoundPath ?? string.Empty;
        ExcludeMacOsToggle.IsOn = settings.ExcludeMacOsMetadata;
        NormalizeNfcToggle.IsOn = settings.NormalizeFileNamesNfc;

        BuildAssociationList();
        UpdateDestinationRows();
        UpdateSoundRow();
        RefreshLegacyContextMenuStatus();
        _initializing = false;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var settings = SettingsService.Load();
        settings.DestinationRule = "SameAsArchive";
        settings.ExtractionDestination = ParseDestination(SelectedTag(DestinationRuleComboBox));
        settings.ExtractionDestinationFolder = NormalizeOptionalFolder(DestinationFolderBox.Text);
        settings.CompressionDestination = ParseDestination(SelectedTag(CompressionDestinationComboBox));
        settings.CompressionDestinationFolder = NormalizeOptionalFolder(CompressionFolderBox.Text);
        settings.SkipExistingIfNewer = SkipNewerToggle.IsOn;
        settings.NotifyOnCompletion = NotifyToggle.IsOn;
        settings.RunInBackground = BackgroundToggle.IsOn;
        settings.NotificationSound = ParseSound(SelectedTag(SoundComboBox));
        settings.NotificationSoundPath = string.IsNullOrWhiteSpace(SoundFileBox.Text)
            ? null
            : SoundFileBox.Text.Trim();
        settings.ExcludeMacOsMetadata = ExcludeMacOsToggle.IsOn;
        settings.NormalizeFileNamesNfc = NormalizeNfcToggle.IsOn;
        settings.OpenFolderAfterExtraction = OpenFolderToggle.IsOn;
        settings.CloseAfterAutoExtract = CloseAfterExtractToggle.IsOn;
        if (AppInfo.SupportsAutoUpdate)
        {
            settings.CheckForUpdatesOnStartup = CheckUpdatesToggle.IsOn;
        }
        settings.FolderCollisionPolicy = ParsePolicy(
            SelectedTag(FolderCollisionComboBox),
            FolderCollisionPolicy.Ask);
        settings.OverwritePolicy = ParsePolicy(
            SelectedTag(OverwritePolicyComboBox),
            OverwritePolicy.Ask);
        settings.Language = Enum.TryParse<AppLanguage>(SelectedTag(LanguageComboBox), ignoreCase: true, out var language)
            ? language
            : AppLanguage.System;
        settings.MaxDegreeOfParallelism = int.TryParse(SelectedTag(ParallelismComboBox), out var parallelism)
            ? Math.Max(0, parallelism)
            : 0;
        SettingsService.Save(settings);
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void LegacyContextMenuToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressLegacyToggle || _legacyToggleBusy)
        {
            return;
        }

        var shouldRegister = LegacyContextMenuToggle.IsOn;

        // 登録・解除はレジストリと Explorer への通知を伴うため、
        // UI スレッドでは実行しない。実行中はトグルを無効にして
        // 状態を正直に表示する。
        _legacyToggleBusy = true;
        LegacyContextMenuToggle.IsEnabled = false;
        SettingsInfoBar.IsOpen = false;
        LegacyContextMenuStatusText.Text = Loc.Settings_ContextMenuRegistering;

        LegacyContextMenuResult result;
        try
        {
            result = await (shouldRegister
                ? LegacyContextMenuService.RegisterAsync()
                : LegacyContextMenuService.UnregisterAsync());
        }
        catch (Exception ex)
        {
            result = new LegacyContextMenuResult(false, ex.Message);
        }
        finally
        {
            _legacyToggleBusy = false;
        }

        RefreshLegacyContextMenuStatus();

        SettingsInfoBar.Severity = result.Succeeded ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        SettingsInfoBar.Title = result.Succeeded ? Loc.Settings_ContextMenuTitle : Loc.Settings_RegisterFailedTitle;
        SettingsInfoBar.Message = result.Message;
        SettingsInfoBar.IsOpen = true;
    }

    private void RefreshLegacyContextMenuStatus()
    {
        var status = LegacyContextMenuService.GetStatus();

        _suppressLegacyToggle = true;
        LegacyContextMenuToggle.IsEnabled = status.IsSupported;
        LegacyContextMenuToggle.IsOn = status.IsRegistered;
        _suppressLegacyToggle = false;

        LegacyContextMenuStatusText.Text = status.Message;
    }

    private void ApplyLocalization()
    {
        SettingsTitleText.Text = Loc.Settings_Title;
        SettingsSubtitleText.Text = Loc.Settings_Subtitle;
        DestinationSectionText.Text = Loc.Settings_Destination;
        DestinationSameAsArchiveItem.Content = Loc.Settings_SameAsArchive;
        DestinationNoteText.Text = Loc.Settings_DestinationNote;
        FolderCollisionSectionText.Text = Loc.Settings_FolderCollision;
        FolderCollisionAskItem.Content = Loc.Settings_AskEveryTime;
        FolderCollisionMergeItem.Content = Loc.Settings_MergeExisting;
        FolderCollisionAutoRenameItem.Content = Loc.Settings_AutoRenameFolder;
        FolderCollisionNoteText.Text = Loc.Settings_FolderCollisionNote;
        OverwriteSectionText.Text = Loc.Settings_Overwrite;
        OverwriteAskItem.Content = Loc.Settings_AskEveryTime;
        OverwriteOverwriteItem.Content = Loc.Settings_AlwaysOverwrite;
        OverwriteSkipItem.Content = Loc.Settings_AlwaysSkip;
        OverwriteAutoRenameItem.Content = Loc.Settings_RenameSave;
        OverwriteNoteText.Text = Loc.Settings_OverwriteNote;

        CompressionDestinationSectionText.Text = Loc.Settings_CompressionDestination;
        SetItemContent(CompressionDestinationComboBox, Loc.Settings_SameAsArchive, Loc.Settings_FixedFolder, Loc.Settings_AskEveryTime);
        DestinationFixedFolderItem.Content = Loc.Settings_FixedFolder;
        DestinationAskItem.Content = Loc.Settings_AskEveryTime;
        DestinationFolderBrowseButton.Content = Loc.Settings_ChooseFolder;
        CompressionFolderBrowseButton.Content = Loc.Settings_ChooseFolder;

        BehaviorSectionText.Text = Loc.Settings_Behavior;
        OpenFolderToggle.Header = Loc.Settings_OpenFolderAfter;
        OpenFolderToggle.OnContent = Loc.Settings_LegacyOn;
        OpenFolderToggle.OffContent = Loc.Settings_LegacyOff;
        CloseAfterExtractToggle.Header = Loc.Settings_CloseAfter;
        CloseAfterExtractToggle.OnContent = Loc.Settings_LegacyOn;
        CloseAfterExtractToggle.OffContent = Loc.Settings_LegacyOff;
        CloseAfterNoteText.Text = Loc.Settings_CloseAfterNote;
        SkipNewerToggle.Header = Loc.Settings_SkipNewer;
        SkipNewerToggle.OnContent = Loc.Settings_LegacyOn;
        SkipNewerToggle.OffContent = Loc.Settings_LegacyOff;
        SkipNewerNoteText.Text = Loc.Settings_SkipNewerNote;

        AssociationsSectionText.Text = Loc.Settings_Associations;
        AssociationsNoteText.Text = Loc.Settings_AssociationsNote;
        AssociationsPackagedText.Text = Loc.Settings_AssociationsPackaged;
        DefaultAppsNoteText.Text = Loc.Settings_DefaultAppsNote;
        OpenDefaultAppsButton.Content = Loc.Settings_OpenDefaultApps;

        NotificationsSectionText.Text = Loc.Settings_Notifications;
        NotifyToggle.Header = Loc.Settings_NotifyOnCompletion;
        NotifyToggle.OnContent = Loc.Settings_LegacyOn;
        NotifyToggle.OffContent = Loc.Settings_LegacyOff;
        NotifyNoteText.Text = Loc.Settings_NotifyNote;
        BackgroundToggle.Header = Loc.Settings_Background;
        BackgroundToggle.OnContent = Loc.Settings_LegacyOn;
        BackgroundToggle.OffContent = Loc.Settings_LegacyOff;
        BackgroundNoteText.Text = Loc.Settings_BackgroundNote;
        SoundSectionText.Text = Loc.Settings_Sound;
        SetItemContent(SoundComboBox, Loc.Settings_SoundNone, Loc.Settings_SoundSystem, Loc.Settings_SoundFile);
        SoundBrowseButton.Content = Loc.Common_Browse;
        TestSoundButton.Content = Loc.Settings_TestSound;
        SoundNoteText.Text = Loc.Settings_SoundNote;

        MacOsSectionText.Text = Loc.Settings_MacOs;
        ExcludeMacOsToggle.Header = Loc.Settings_ExcludeMacOs;
        ExcludeMacOsToggle.OnContent = Loc.Settings_LegacyOn;
        ExcludeMacOsToggle.OffContent = Loc.Settings_LegacyOff;
        ExcludeMacOsNoteText.Text = Loc.Settings_ExcludeMacOsNote;
        NormalizeNfcToggle.Header = Loc.Settings_NormalizeNfc;
        NormalizeNfcToggle.OnContent = Loc.Settings_LegacyOn;
        NormalizeNfcToggle.OffContent = Loc.Settings_LegacyOff;
        NormalizeNfcNoteText.Text = Loc.Settings_NormalizeNfcNote;

        UpdatesSectionText.Text = Loc.Settings_Updates;
        CheckUpdatesToggle.Header = Loc.Settings_CheckUpdatesStartup;
        CheckUpdatesToggle.OnContent = Loc.Settings_LegacyOn;
        CheckUpdatesToggle.OffContent = Loc.Settings_LegacyOff;
        CheckUpdatesNoteText.Text = AppInfo.SupportsAutoUpdate
            ? Loc.Settings_CheckUpdatesNote
            : Loc.Settings_CheckUpdatesPackaged;

        LanguageSectionText.Text = Loc.Settings_Language;
        SetItemContent(LanguageComboBox, Loc.Settings_LanguageSystem, Loc.Settings_LanguageJapanese, Loc.Settings_LanguageEnglish);
        LanguageNoteText.Text = Loc.Settings_LanguageNote;

        ParallelismSectionText.Text = Loc.Settings_Parallelism;
        PopulateParallelismItems();
        ParallelismNoteText.Text = Loc.Settings_ParallelismNote;

        LegacySectionText.Text = Loc.Settings_LegacyMenu;
        LegacyNoteText.Text = Loc.Settings_LegacyNote;
        LegacyContextMenuToggle.Header = Loc.Settings_LegacyToggle;
        LegacyContextMenuToggle.OnContent = Loc.Settings_LegacyOn;
        LegacyContextMenuToggle.OffContent = Loc.Settings_LegacyOff;

        SupportSectionText.Text = Loc.Settings_Support;
        SupportNoteText.Text = Loc.Settings_SupportNote;
        FeedbackButton.Content = Loc.Settings_Feedback;
        DonateButton.Content = Loc.Common_Support;
        CancelButton.Content = Loc.Common_Cancel;
        SaveButton.Content = Loc.Settings_Save;
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

    private void PopulateParallelismItems()
    {
        var selected = SelectedTag(ParallelismComboBox);
        ParallelismComboBox.Items.Clear();

        AddParallelismItem("0", Loc.Settings_ParallelismAuto);
        AddParallelismItem("1", Loc.Settings_ParallelismSequential);
        foreach (var value in new[] { 2, 4, 8, 16, 32 })
        {
            AddParallelismItem(value.ToString(), Loc.Settings_ParallelismThreads(value));
        }

        SelectByTag(ParallelismComboBox, selected);
    }

    private void AddParallelismItem(string tag, string content)
    {
        ParallelismComboBox.Items.Add(new ComboBoxItem
        {
            Tag = tag,
            Content = content,
        });
    }

    private void SelectParallelism(int value)
    {
        var tag = Math.Max(0, value).ToString();
        if (ParallelismComboBox.Items.OfType<ComboBoxItem>().Any(x => string.Equals(x.Tag as string, tag, StringComparison.Ordinal)))
        {
            SelectByTag(ParallelismComboBox, tag);
            return;
        }

        // 手動で編集された設定値も失わないよう、候補として追加する。
        AddParallelismItem(tag, Loc.Settings_ParallelismThreads(Math.Max(0, value)));
        SelectByTag(ParallelismComboBox, tag);
    }

    private static void SelectByTag(ComboBox comboBox, string tag)
    {
        foreach (var item in comboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }

        comboBox.SelectedIndex = 0;
    }

    private static string SelectedTag(ComboBox comboBox)
    {
        return (comboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "Ask";
    }

    private void BuildAssociationList()
    {
        AssociationsPanel.Children.Clear();
        var supported = AssociationService.IsSupported;
        AssociationsPanel.Visibility = supported ? Visibility.Visible : Visibility.Collapsed;
        AssociationsPackagedText.Visibility = supported ? Visibility.Collapsed : Visibility.Visible;

        foreach (var extension in AssociationService.Extensions)
        {
            var checkBox = new CheckBox
            {
                Content = extension,
                Tag = extension,
                IsChecked = AssociationService.IsEnabled(extension),
                IsEnabled = supported,
                MinWidth = 88,
            };

            checkBox.Checked += AssociationCheckBox_Changed;
            checkBox.Unchecked += AssociationCheckBox_Changed;
            AssociationsPanel.Children.Add(checkBox);
        }
    }

    private void AssociationCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing || sender is not CheckBox checkBox || checkBox.Tag is not string extension)
        {
            return;
        }

        var enabled = checkBox.IsChecked == true;
        if (AssociationService.SetEnabled(extension, enabled, out var message))
        {
            SettingsInfoBar.IsOpen = false;
            return;
        }

        // 失敗したら実際の状態へ戻し、成功したように見せない。
        checkBox.IsChecked = AssociationService.IsEnabled(extension);
        SettingsInfoBar.Severity = InfoBarSeverity.Error;
        SettingsInfoBar.Title = Loc.Settings_Associations;
        SettingsInfoBar.Message = message;
        SettingsInfoBar.IsOpen = true;
    }

    private void OpenDefaultAppsButton_Click(object sender, RoutedEventArgs e)
    {
        if (AssociationService.OpenDefaultAppsSettings())
        {
            return;
        }

        SettingsInfoBar.Severity = InfoBarSeverity.Error;
        SettingsInfoBar.Title = Loc.Settings_Associations;
        SettingsInfoBar.Message = Loc.Settings_DefaultAppsNote;
        SettingsInfoBar.IsOpen = true;
    }

    private void DestinationRuleComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initializing)
        {
            UpdateDestinationRows();
        }
    }

    private void CompressionDestinationComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initializing)
        {
            UpdateDestinationRows();
        }
    }

    private void SoundComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initializing)
        {
            UpdateSoundRow();
        }
    }

    private void UpdateDestinationRows()
    {
        DestinationFolderRow.Visibility = ParseDestination(SelectedTag(DestinationRuleComboBox)) == DestinationSelectionMode.FixedFolder
            ? Visibility.Visible
            : Visibility.Collapsed;
        CompressionFolderRow.Visibility = ParseDestination(SelectedTag(CompressionDestinationComboBox)) == DestinationSelectionMode.FixedFolder
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void UpdateSoundRow()
    {
        SoundFileRow.Visibility = ParseSound(SelectedTag(SoundComboBox)) == NotificationSoundMode.File
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async void DestinationFolderBrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var folder = await PickFolderAsync();
        if (folder is not null)
        {
            DestinationFolderBox.Text = folder;
        }
    }

    private async void CompressionFolderBrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var folder = await PickFolderAsync();
        if (folder is not null)
        {
            CompressionFolderBox.Text = folder;
        }
    }

    private async Task<string?> PickFolderAsync()
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
            return folder?.Path;
        }
        catch (Exception ex)
        {
            SettingsInfoBar.Severity = InfoBarSeverity.Error;
            SettingsInfoBar.Title = Loc.Settings_FixedFolder;
            SettingsInfoBar.Message = ex.Message;
            SettingsInfoBar.IsOpen = true;
            return null;
        }
    }

    private async void SoundBrowseButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.MusicLibrary,
                ViewMode = PickerViewMode.List,
            };
            picker.FileTypeFilter.Add(".wav");
            picker.FileTypeFilter.Add(".mp3");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var file = await picker.PickSingleFileAsync();
            if (file is not null)
            {
                SoundFileBox.Text = file.Path;
            }
        }
        catch (Exception ex)
        {
            SettingsInfoBar.Severity = InfoBarSeverity.Error;
            SettingsInfoBar.Title = Loc.Settings_Sound;
            SettingsInfoBar.Message = ex.Message;
            SettingsInfoBar.IsOpen = true;
        }
    }

    private void TestSoundButton_Click(object sender, RoutedEventArgs e)
    {
        NotificationService.PlaySound(ParseSound(SelectedTag(SoundComboBox)), SoundFileBox.Text.Trim());
    }

    private static DestinationSelectionMode ParseDestination(string tag)
    {
        return Enum.TryParse<DestinationSelectionMode>(tag, ignoreCase: true, out var value)
            ? value
            : DestinationSelectionMode.SameAsArchive;
    }

    private static NotificationSoundMode ParseSound(string tag)
    {
        return Enum.TryParse<NotificationSoundMode>(tag, ignoreCase: true, out var value)
            ? value
            : NotificationSoundMode.None;
    }

    private static string? NormalizeOptionalFolder(string value)
    {
        var trimmed = value.Trim().Trim('"');
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static FolderCollisionPolicy ParsePolicy(string tag, FolderCollisionPolicy fallback)
    {
        return Enum.TryParse<FolderCollisionPolicy>(tag, ignoreCase: true, out var value) ? value : fallback;
    }

    private static OverwritePolicy ParsePolicy(string tag, OverwritePolicy fallback)
    {
        return Enum.TryParse<OverwritePolicy>(tag, ignoreCase: true, out var value) ? value : fallback;
    }

    private void FeedbackButton_Click(object sender, RoutedEventArgs e)
    {
        UiHelpers.OpenUrl(AppInfo.FeedbackUrl);
    }

    private async void DonateButton_Click(object sender, RoutedEventArgs e)
    {
        if (RootGrid.XamlRoot is { } xamlRoot)
        {
            await DonationDialog.ShowAsync(xamlRoot);
        }
    }
}
