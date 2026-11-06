using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Hodozip.Core;
using Hodozip.Services;

namespace Hodozip.Dialogs;

internal static class PasswordDialog
{
    public static async Task<string?> ShowAsync(XamlRoot xamlRoot, PasswordRequest request)
    {
        var panel = new StackPanel { Spacing = 12 };

        panel.Children.Add(new TextBlock
        {
            Text = Loc.Password_Message,
            TextWrapping = TextWrapping.Wrap,
        });

        if (!string.IsNullOrEmpty(request.ErrorMessage))
        {
            panel.Children.Add(new TextBlock
            {
                Text = Loc.Password_ErrorWithAttempt(LocalizationService.GetPasswordError(request), request.Attempt),
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
                TextWrapping = TextWrapping.Wrap,
            });
        }
        else if (request.Attempt > 1)
        {
            panel.Children.Add(new TextBlock
            {
                Text = Loc.Password_Attempt(request.Attempt),
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
        }

        var passwordBox = new PasswordBox
        {
            PlaceholderText = Loc.Password_Placeholder,
            PasswordRevealMode = PasswordRevealMode.Peek,
            Width = 320,
        };
        panel.Children.Add(passwordBox);

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = Loc.Password_Title,
            Content = panel,
            PrimaryButtonText = Loc.Password_Ok,
            CloseButtonText = Loc.Password_Cancel,
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
        };

        passwordBox.PasswordChanged += (_, _) =>
        {
            dialog.IsPrimaryButtonEnabled = passwordBox.Password.Length > 0;
        };

        passwordBox.Loaded += (_, _) => passwordBox.Focus(FocusState.Programmatic);

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary ? passwordBox.Password : null;
    }
}
