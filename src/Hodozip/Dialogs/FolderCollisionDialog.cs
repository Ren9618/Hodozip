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
            Text = Loc.FolderCollision_Exists(conflict.FolderName),
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
                Text = Loc.FolderCollision_FileConflict,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCautionBrush"],
                TextWrapping = TextWrapping.Wrap,
            });
        }

        var saveAsDefault = new CheckBox
        {
            Content = Loc.FolderCollision_SaveDefault,
        };
        panel.Children.Add(saveAsDefault);

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = Loc.FolderCollision_Title,
            Content = panel,
            PrimaryButtonText = Loc.FolderCollision_Merge,
            SecondaryButtonText = Loc.FolderCollision_New,
            CloseButtonText = Loc.FolderCollision_Cancel,
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
