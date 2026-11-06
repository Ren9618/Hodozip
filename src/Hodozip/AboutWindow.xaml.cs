using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Hodozip.Dialogs;
using Hodozip.Services;

namespace Hodozip;

public sealed partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();

        Title = $"{AppInfo.ProductName} - バージョン情報";
        WindowHelpers.Configure(this, 740, 760);

        VersionText.Text = $"Version {AppInfo.Version}";
        SupportedFormatsText.Text = AppInfo.SupportedFormatsText;
        LicenseText.Text = AppInfo.MitLicenseText;
    }

    private async void DonateButton_Click(object sender, RoutedEventArgs e)
    {
        if (RootGrid.XamlRoot is { } xamlRoot)
        {
            await DonationDialog.ShowAsync(xamlRoot);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
