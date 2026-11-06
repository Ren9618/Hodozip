using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Hodozip.Core;
using Hodozip.Services;

namespace Hodozip.Dialogs;

internal static class OverwriteDialog
{
    public static async Task<(OverwriteDecision Decision, bool ApplyToAll)> ShowAsync(
        XamlRoot xamlRoot,
        OverwriteConflict conflict)
    {
        var panel = new StackPanel { Spacing = 14, MinWidth = 520 };

        panel.Children.Add(new TextBlock
        {
            Text = Loc.Overwrite_Exists(conflict.EntryPath),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });

        var details = new Grid
        {
            ColumnSpacing = 18,
            RowSpacing = 6,
        };
        details.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        details.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        details.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        for (var row = 0; row < 4; row++)
        {
            details.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        var secondaryBrush = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        AddCell(details, 0, 0, Loc.Overwrite_Item, secondaryBrush);
        AddCell(details, 0, 1, Loc.Overwrite_ExistingFile, secondaryBrush);
        AddCell(details, 0, 2, Loc.Overwrite_NewFile, secondaryBrush);
        AddCell(details, 1, 0, Loc.Overwrite_Size, secondaryBrush);
        AddCell(details, 1, 1, UiHelpers.FormatBytes(conflict.ExistingSize));
        AddCell(details, 1, 2, UiHelpers.FormatBytes(conflict.NewSize));
        AddCell(details, 2, 0, Loc.Overwrite_Modified, secondaryBrush);
        AddCell(details, 2, 1, UiHelpers.FormatDateTime(conflict.ExistingLastWriteTime));
        AddCell(details, 2, 2, UiHelpers.FormatDateTime(conflict.NewLastWriteTime));
        AddCell(details, 3, 0, Loc.Overwrite_Destination, secondaryBrush);
        AddCell(
            details,
            3,
            1,
            MiddleEllipsis.CompactMiddle(conflict.DestinationPath, 62),
            secondaryBrush,
            columnSpan: 2);

        panel.Children.Add(details);

        var buttons = new Grid
        {
            ColumnSpacing = 8,
            RowSpacing = 8,
            Margin = new Thickness(0, 4, 0, 0),
        };
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var row = 0; row < 2; row++)
        {
            buttons.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        var chosen = OverwriteDecision.Cancel;
        var applyToAll = false;
        var closed = false;

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = Loc.Overwrite_Title,
            Content = panel,
        };
        AutomationProperties.SetAutomationId(dialog, "OverwriteDialog");

        void Choose(OverwriteDecision decision, bool applyToAllFiles)
        {
            chosen = decision;
            applyToAll = applyToAllFiles;
            closed = true;
            dialog.Hide();
        }

        AddButton(buttons, 0, 0, Loc.Overwrite_Action, "OverwriteButton", () => Choose(OverwriteDecision.Overwrite, false), isPrimary: true);
        AddButton(buttons, 0, 1, Loc.Overwrite_Skip, "SkipButton", () => Choose(OverwriteDecision.Skip, false));
        AddButton(buttons, 0, 2, Loc.Overwrite_Rename, "RenameButton", () => Choose(OverwriteDecision.AutoRename, false));
        AddButton(buttons, 1, 0, Loc.Overwrite_All, "OverwriteAllButton", () => Choose(OverwriteDecision.Overwrite, true));
        AddButton(buttons, 1, 1, Loc.Overwrite_SkipAll, "SkipAllButton", () => Choose(OverwriteDecision.Skip, true));
        AddButton(buttons, 1, 2, Loc.Common_Cancel, "CancelButton", () => Choose(OverwriteDecision.Cancel, false));

        panel.Children.Add(buttons);

        dialog.Closing += (_, _) =>
        {
            if (!closed)
            {
                // Esc キーやタイトルバーの閉じる操作はキャンセルとして扱う。
                chosen = OverwriteDecision.Cancel;
                applyToAll = false;
            }
        };

        await dialog.ShowAsync();
        return (chosen, applyToAll);
    }

    private static void AddButton(
        Grid grid,
        int row,
        int column,
        string text,
        string automationId,
        Action onClick,
        bool isPrimary = false)
    {
        var button = new Button
        {
            Content = text,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        if (isPrimary)
        {
            button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        }

        AutomationProperties.SetAutomationId(button, automationId);
        button.Click += (_, _) => onClick();
        Grid.SetRow(button, row);
        Grid.SetColumn(button, column);
        grid.Children.Add(button);
    }

    private static void AddCell(
        Grid grid,
        int row,
        int column,
        string text,
        Brush? foreground = null,
        int columnSpan = 1)
    {
        var block = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
        };

        if (foreground is not null)
        {
            block.Foreground = foreground;
        }

        Grid.SetRow(block, row);
        Grid.SetColumn(block, column);
        Grid.SetColumnSpan(block, columnSpan);
        grid.Children.Add(block);
    }
}
