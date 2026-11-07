using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Hodozip.Core;
using Hodozip.Dialogs;
using Hodozip.Services;

namespace Hodozip;

public sealed partial class SettingsWindow : Window
{
    private bool _suppressLegacyToggle;
    private bool _legacyToggleBusy;

    public SettingsWindow()
    {
        InitializeComponent();

        Title = Loc.Settings_WindowTitle(AppInfo.ProductName);
        WindowHelpers.Configure(this, 700, 900);

        ApplyLocalization();

        var settings = SettingsService.Load();
        OpenFolderToggle.IsOn = settings.OpenFolderAfterExtraction;
        CloseAfterExtractToggle.IsOn = settings.CloseAfterAutoExtract;
        CheckUpdatesToggle.IsOn = AppInfo.SupportsAutoUpdate && settings.CheckForUpdatesOnStartup;
        CheckUpdatesToggle.IsEnabled = AppInfo.SupportsAutoUpdate;
        DestinationRuleComboBox.SelectedIndex = 0;
        SelectByTag(FolderCollisionComboBox, settings.FolderCollisionPolicy.ToString());
        SelectByTag(OverwritePolicyComboBox, settings.OverwritePolicy.ToString());
        SelectByTag(LanguageComboBox, settings.Language.ToString());
        SelectParallelism(settings.MaxDegreeOfParallelism);

        RefreshLegacyContextMenuStatus();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var settings = SettingsService.Load();
        settings.DestinationRule = "SameAsArchive";
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

        BehaviorSectionText.Text = Loc.Settings_Behavior;
        OpenFolderToggle.Header = Loc.Settings_OpenFolderAfter;
        OpenFolderToggle.OnContent = Loc.Settings_LegacyOn;
        OpenFolderToggle.OffContent = Loc.Settings_LegacyOff;
        CloseAfterExtractToggle.Header = Loc.Settings_CloseAfter;
        CloseAfterExtractToggle.OnContent = Loc.Settings_LegacyOn;
        CloseAfterExtractToggle.OffContent = Loc.Settings_LegacyOff;
        CloseAfterNoteText.Text = Loc.Settings_CloseAfterNote;

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

    private static FolderCollisionPolicy ParsePolicy(string tag, FolderCollisionPolicy fallback)
    {
        return Enum.TryParse<FolderCollisionPolicy>(tag, ignoreCase: true, out var value) ? value : fallback;
    }

    private static OverwritePolicy ParsePolicy(string tag, OverwritePolicy fallback)
    {
        return Enum.TryParse<OverwritePolicy>(tag, ignoreCase: true, out var value) ? value : fallback;
    }

    private async void DonateButton_Click(object sender, RoutedEventArgs e)
    {
        if (RootGrid.XamlRoot is { } xamlRoot)
        {
            await DonationDialog.ShowAsync(xamlRoot);
        }
    }
}
