using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Hodozip.Dialogs;
using Hodozip.Services;

namespace Hodozip;

public sealed partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();

        Title = $"{AppInfo.ProductName} - 設定";
        WindowHelpers.Configure(this, 660, 720);

        var settings = SettingsService.Load();
        OpenFolderToggle.IsOn = settings.OpenFolderAfterExtraction;
        CloseAfterExtractToggle.IsOn = settings.CloseAfterAutoExtract;
        DestinationRuleComboBox.SelectedIndex = 0;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var settings = SettingsService.Load();
        settings.DestinationRule = "SameAsArchive";
        settings.OpenFolderAfterExtraction = OpenFolderToggle.IsOn;
        settings.CloseAfterAutoExtract = CloseAfterExtractToggle.IsOn;
        SettingsService.Save(settings);
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void DonateButton_Click(object sender, RoutedEventArgs e)
    {
        if (RootGrid.XamlRoot is { } xamlRoot)
        {
            await DonationDialog.ShowAsync(xamlRoot);
        }
    }
}
