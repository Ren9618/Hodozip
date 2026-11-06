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
            Text = "Hodozip の開発を支援していただけますか？ いただいた支援は、開発とメンテナンスに使わせていただきます。",
            TextWrapping = TextWrapping.Wrap,
        });

        panel.Children.Add(new TextBlock
        {
            Text = "ボタンを押すと、既定のブラウザで支援ページが開きます。アプリ内では決済を行いません。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "開発を支援する",
            Content = panel,
            PrimaryButtonText = "GitHub Sponsors",
            SecondaryButtonText = "Stripe で支援",
            CloseButtonText = "キャンセル",
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
