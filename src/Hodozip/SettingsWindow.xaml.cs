using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Hodozip.Core;
using Hodozip.Dialogs;
using Hodozip.Services;

namespace Hodozip;

public sealed partial class SettingsWindow : Window
{
    private bool _suppressLegacyToggle;

    public SettingsWindow()
    {
        InitializeComponent();

        Title = $"{AppInfo.ProductName} - 設定";
        WindowHelpers.Configure(this, 700, 860);

        var settings = SettingsService.Load();
        OpenFolderToggle.IsOn = settings.OpenFolderAfterExtraction;
        CloseAfterExtractToggle.IsOn = settings.CloseAfterAutoExtract;
        DestinationRuleComboBox.SelectedIndex = 0;
        SelectByTag(FolderCollisionComboBox, settings.FolderCollisionPolicy.ToString());
        SelectByTag(OverwritePolicyComboBox, settings.OverwritePolicy.ToString());

        RefreshLegacyContextMenuStatus();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var settings = SettingsService.Load();
        settings.DestinationRule = "SameAsArchive";
        settings.OpenFolderAfterExtraction = OpenFolderToggle.IsOn;
        settings.CloseAfterAutoExtract = CloseAfterExtractToggle.IsOn;
        settings.FolderCollisionPolicy = ParsePolicy(
            SelectedTag(FolderCollisionComboBox),
            FolderCollisionPolicy.Ask);
        settings.OverwritePolicy = ParsePolicy(
            SelectedTag(OverwritePolicyComboBox),
            OverwritePolicy.Ask);
        SettingsService.Save(settings);
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void LegacyContextMenuToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressLegacyToggle)
        {
            return;
        }

        var shouldRegister = LegacyContextMenuToggle.IsOn;
        var succeeded = shouldRegister
            ? LegacyContextMenuService.Register(out var message)
            : LegacyContextMenuService.Unregister(out message);

        if (!succeeded)
        {
            _suppressLegacyToggle = true;
            LegacyContextMenuToggle.IsOn = !shouldRegister;
            _suppressLegacyToggle = false;
        }

        RefreshLegacyContextMenuStatus();
        SettingsInfoBar.Severity = succeeded ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        SettingsInfoBar.Title = succeeded ? "コンテキストメニュー" : "登録できませんでした";
        SettingsInfoBar.Message = message;
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
