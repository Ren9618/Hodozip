using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Hodozip.Services;

namespace Hodozip.Dialogs;

internal static class DonationDialog
{
    public static async Task ShowAsync(XamlRoot xamlRoot)
    {
        var panel = new StackPanel { Spacing = 12 };

        panel.Children.Add(new TextBlock
        {
            Text = Loc.Donation_Message,
            TextWrapping = TextWrapping.Wrap,
        });

        panel.Children.Add(new TextBlock
        {
            Text = Loc.Donation_Note,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = Loc.Donation_Title,
            Content = panel,
            PrimaryButtonText = Loc.Donation_GitHub,
            SecondaryButtonText = Loc.Donation_Stripe,
            CloseButtonText = Loc.Donation_Cancel,
            DefaultButton = ContentDialogButton.Primary,
        };

        var result = await dialog.ShowAsync();
        var url = result switch
        {
            ContentDialogResult.Primary => AppInfo.GitHubSponsorsUrl,
            ContentDialogResult.Secondary => AppInfo.StripeDonationUrl,
            _ => null,
        };

        if (url is null)
        {
            return;
        }

        try
        {
            UiHelpers.OpenUrl(url);
        }
        catch
        {
            // ブラウザを開けない場合は何もしない。アプリの動作を止めない。
        }
    }
}
