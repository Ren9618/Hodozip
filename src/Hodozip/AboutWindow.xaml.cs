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

        Title = Loc.About_WindowTitle(AppInfo.ProductName);
        WindowHelpers.Configure(this, 740, 760);

        AboutTitleText.Text = Loc.About_Title;
        AboutSubtitleText.Text = Loc.About_Subtitle;
        AboutTaglineText.Text = Loc.About_Tagline;
        SupportedFormatsHeaderText.Text = Loc.About_SupportedFormats;
        SupportedFormatsNoteText.Text = Loc.About_SupportedFormatsNote;
        LicenseHeaderText.Text = Loc.About_License;
        LicenseExpander.Header = Loc.About_LicenseFull;
        CopyrightText.Text = Loc.About_Copyright;
        LibrariesHeaderText.Text = Loc.About_Libraries;
        SharpCompressLicenseText.Text = Loc.About_SharpCompress;
        WindowsAppSdkLicenseText.Text = Loc.About_WindowsAppSdk;
        DotNetLicenseText.Text = Loc.About_DotNet;
        PrivacyHeaderText.Text = Loc.About_Privacy;
        PrivacyText.Text = Loc.About_PrivacyText;
        DonateButton.Content = Loc.Common_Support;
        CloseButton.Content = Loc.Common_Close;

        VersionText.Text = Loc.About_Version(AppInfo.Version);
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
