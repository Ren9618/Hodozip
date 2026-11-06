using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Hodozip.Core;
using Hodozip.Services;

namespace Hodozip.Dialogs;

internal static class OverwriteDialog
{
    public static async Task<(OverwriteDecision Decision, bool ApplyToAll)> ShowAsync(
        XamlRoot xamlRoot,
        OverwriteConflict conflict)
    {
        var panel = new StackPanel { Spacing = 14 };

        panel.Children.Add(new TextBlock
        {
            Text = $"「{conflict.EntryPath}」は既に存在します。",
            TextWrapping = TextWrapping.Wrap,
        });

        var details = new Grid
        {
            ColumnSpacing = 18,
            RowSpacing = 6,
        };
        details.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        details.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        details.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        details.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        details.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        details.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        details.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        AddDetail(details, 0, "項目", "既存ファイル", "新しいファイル");
        AddDetail(details, 1, "サイズ", UiHelpers.FormatBytes(conflict.ExistingSize), UiHelpers.FormatBytes(conflict.NewSize));
        AddDetail(details, 2, "更新日時", UiHelpers.FormatDateTime(conflict.ExistingLastWriteTime), UiHelpers.FormatDateTime(conflict.NewLastWriteTime));

        panel.Children.Add(details);

        var applyToAll = new CheckBox
        {
            Content = "以降すべてに適用",
        };
        panel.Children.Add(applyToAll);

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "上書きの確認",
            Content = panel,
            PrimaryButtonText = "上書き",
            SecondaryButtonText = "スキップ",
            CloseButtonText = "リネーム",
            DefaultButton = ContentDialogButton.Primary,
        };

        var result = await dialog.ShowAsync();
        var decision = result switch
        {
            ContentDialogResult.Primary => OverwriteDecision.Overwrite,
            ContentDialogResult.Secondary => OverwriteDecision.Skip,
            _ => OverwriteDecision.AutoRename,
        };

        return (decision, applyToAll.IsChecked == true);
    }

    private static void AddDetail(Grid grid, int row, string label, string existing, string incoming)
    {
        var labelBlock = new TextBlock
        {
            Text = label,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
        Grid.SetRow(labelBlock, row);
        Grid.SetColumn(labelBlock, 0);
        grid.Children.Add(labelBlock);

        var valueBlock = new TextBlock
        {
            Text = $"{existing}  →  {incoming}",
            TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetRow(valueBlock, row);
        Grid.SetColumn(valueBlock, 1);
        grid.Children.Add(valueBlock);
    }
}
