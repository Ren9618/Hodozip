using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using WinRT.Interop;
using Hodozip.Services;

namespace Hodozip.Dialogs;

/// <summary>
/// 複数アーカイブを 1 つのフォルダへまとめて解凍するときの解凍先入力ダイアログ。
/// 指定したフォルダはそのまま使う（アーカイブ名のサブフォルダは作らない）。
/// </summary>
internal static class BatchExtractionDialog
{
    public static async Task<string?> ShowAsync(
        XamlRoot xamlRoot,
        nint windowHandle,
        int archiveCount,
        string defaultDestination)
    {
        var secondaryBrush = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        var criticalBrush = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];

        var panel = new StackPanel { Spacing = 12 };

        panel.Children.Add(new TextBlock
        {
            Text = Loc.Batch_Message(archiveCount),
            TextWrapping = TextWrapping.Wrap,
        });

        panel.Children.Add(new TextBlock
        {
            Text = Loc.Batch_Note,
            Foreground = secondaryBrush,
            TextWrapping = TextWrapping.Wrap,
        });

        var destinationBox = new TextBox
        {
            Header = Loc.Batch_Destination,
            Text = defaultDestination,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetAutomationId(destinationBox, "BatchDestinationBox");

        var browseButton = new Button
        {
            Content = Loc.Common_Browse,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        AutomationProperties.SetAutomationId(browseButton, "BatchBrowseButton");

        var destinationRow = new Grid { ColumnSpacing = 8 };
        destinationRow.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star),
        });
        destinationRow.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = GridLength.Auto,
        });
        Grid.SetColumn(browseButton, 1);
        destinationRow.Children.Add(destinationBox);
        destinationRow.Children.Add(browseButton);
        panel.Children.Add(destinationRow);

        var validationText = new TextBlock
        {
            Foreground = criticalBrush,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };
        panel.Children.Add(validationText);

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = Loc.Batch_Title,
            Content = panel,
            PrimaryButtonText = Loc.Batch_Extract,
            CloseButtonText = Loc.Common_Cancel,
            DefaultButton = ContentDialogButton.Primary,
        };
        AutomationProperties.SetAutomationId(dialog, "BatchExtractionDialog");

        bool Validate(out string? message)
        {
            var text = destinationBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                message = Loc.Batch_DestinationRequired;
                return false;
            }

            try
            {
                var fullPath = Path.GetFullPath(text);
                if (File.Exists(fullPath))
                {
                    message = Loc.Batch_DestinationIsFile;
                    return false;
                }
            }
            catch
            {
                message = Loc.Batch_DestinationInvalid;
                return false;
            }

            message = null;
            return true;
        }

        void RefreshValidation()
        {
            var valid = Validate(out var message);
            dialog.IsPrimaryButtonEnabled = valid;
            validationText.Text = message ?? string.Empty;
            validationText.Visibility = valid ? Visibility.Collapsed : Visibility.Visible;
        }

        destinationBox.TextChanged += (_, _) => RefreshValidation();
        dialog.PrimaryButtonClick += (sender, args) =>
        {
            if (!Validate(out _))
            {
                args.Cancel = true;
            }
        };

        browseButton.Click += async (_, _) =>
        {
            try
            {
                var picker = new FolderPicker
                {
                    SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                };
                picker.FileTypeFilter.Add("*");
                InitializeWithWindow.Initialize(picker, windowHandle);
                var folder = await picker.PickSingleFolderAsync();
                if (folder is not null)
                {
                    destinationBox.Text = folder.Path;
                }
            }
            catch
            {
                // フォルダー選択を開けない場合も、パスの手入力は続けられる。
            }
        };

        RefreshValidation();

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary ? destinationBox.Text.Trim() : null;
    }
}
