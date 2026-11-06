using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Hodozip.Core;
using Hodozip.Services;

namespace Hodozip.Dialogs;

internal static class FolderCollisionDialog
{
    public static async Task<(FolderCollisionDecision Decision, bool SaveAsDefault)> ShowAsync(
        XamlRoot xamlRoot,
        FolderCollisionConflict conflict)
    {
        var panel = new StackPanel { Spacing = 12 };

        panel.Children.Add(new TextBlock
        {
            Text = $"解凍先のフォルダ「{conflict.FolderName}」は既に存在します。",
            TextWrapping = TextWrapping.Wrap,
        });

        panel.Children.Add(new TextBlock
        {
            Text = conflict.FolderPath,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap,
        });

        if (!conflict.ExistingIsDirectory)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "同じ名前のファイルが存在するため、既存フォルダへの解凍は選べません。",
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCautionBrush"],
                TextWrapping = TextWrapping.Wrap,
            });
        }

        var saveAsDefault = new CheckBox
        {
            Content = "この選択を既定にする",
        };
        panel.Children.Add(saveAsDefault);

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "フォルダの上書き確認",
            Content = panel,
            PrimaryButtonText = "既存のフォルダに解凍する",
            SecondaryButtonText = "新しいフォルダを作成する",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = conflict.ExistingIsDirectory,
        };
        AutomationProperties.SetAutomationId(dialog, "FolderCollisionDialog");

        var result = await dialog.ShowAsync();
        var decision = result switch
        {
            ContentDialogResult.Primary => FolderCollisionDecision.Merge,
            ContentDialogResult.Secondary => FolderCollisionDecision.AutoRename,
            _ => FolderCollisionDecision.Cancel,
        };

        return (decision, saveAsDefault.IsChecked == true);
    }
}
