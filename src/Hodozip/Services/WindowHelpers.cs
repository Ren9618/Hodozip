using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;

namespace Hodozip.Services;

internal static class WindowHelpers
{
    private const int DwmwaUseImmersiveDarkMode = 20;

    /// <summary>ウィンドウサイズを設定し、プライマリ ディスプレイの中央へ移動する。</summary>
    public static void Configure(Window window, int width, int height)
    {
        var appWindow = window.AppWindow;
        ApplyIcon(appWindow);
        appWindow.Resize(new SizeInt32(width, height));

        try
        {
            var workArea = DisplayArea
                .GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary)
                .WorkArea;

            var x = workArea.X + Math.Max(0, (workArea.Width - width) / 2);
            var y = workArea.Y + Math.Max(0, (workArea.Height - height) / 2);
            appWindow.Move(new PointInt32(x, y));
        }
        catch
        {
            // 位置調整に失敗しても、既定位置で表示を続ける。
        }

        ApplyTitleBarTheme(window);
    }

    private static void ApplyIcon(AppWindow appWindow)
    {
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Hodozip.ico");
            if (File.Exists(iconPath))
            {
                appWindow.SetIcon(iconPath);
                return;
            }

            var processPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(processPath))
            {
                appWindow.SetIcon(processPath);
            }
        }
        catch
        {
            // アイコン設定に失敗しても、既定のアイコンで起動を続ける。
        }
    }

    /// <summary>サイズ固定の小型ウィンドウを設定する。</summary>
    public static void ConfigureFixed(Window window, int width, int height)
    {
        Configure(window, width, height);

        if (window.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }
    }

    /// <summary>
    /// タイトルバーをアプリの有効テーマ（ライト／ダーク）に合わせる。
    /// コンテンツをタイトルバーへ拡張せず、標準のキャプション ボタンと
    /// ドラッグ／リサイズ動作をそのまま残す。
    /// </summary>
    private static void ApplyTitleBarTheme(Window window)
    {
        if (window.Content is not FrameworkElement root)
        {
            return;
        }

        ApplyTitleBarTheme(window, root);
        root.Loaded += (_, _) => ApplyTitleBarTheme(window, root);
        root.ActualThemeChanged += (_, _) => ApplyTitleBarTheme(window, root);
    }

    private static void ApplyTitleBarTheme(Window window, FrameworkElement root)
    {
        try
        {
            var isDark = root.ActualTheme == ElementTheme.Dark;
            var background = (root is Panel panel && panel.Background is SolidColorBrush backgroundBrush)
                ? backgroundBrush.Color
                : GetThemeColor("ApplicationPageBackgroundThemeBrush", isDark);
            var foreground = GetThemeColor("TextFillColorPrimaryBrush", isDark);
            var secondary = BlendOver(GetThemeColor("TextFillColorSecondaryBrush", isDark), background);

            var titleBar = window.AppWindow?.TitleBar;
            if (titleBar is not null && AppWindowTitleBar.IsCustomizationSupported())
            {
                var hover = Shift(background, isDark ? 14 : -12);
                var pressed = Shift(background, isDark ? 26 : -24);

                titleBar.BackgroundColor = background;
                titleBar.InactiveBackgroundColor = background;
                titleBar.ForegroundColor = foreground;
                titleBar.InactiveForegroundColor = secondary;

                // キャプション ボタンはタイトルバーと同じ背景にし、ホバー／押下時だけ
                // 本文のカードに近い控えめな差を付ける。
                titleBar.ButtonBackgroundColor = background;
                titleBar.ButtonInactiveBackgroundColor = background;
                titleBar.ButtonForegroundColor = foreground;
                titleBar.ButtonInactiveForegroundColor = secondary;
                titleBar.ButtonHoverBackgroundColor = hover;
                titleBar.ButtonHoverForegroundColor = foreground;
                titleBar.ButtonPressedBackgroundColor = pressed;
                titleBar.ButtonPressedForegroundColor = foreground;
                return;
            }

            // Windows 10 では AppWindow.TitleBar の色カスタマイズが使えないため、
            // DWM のダーク モードだけをアプリのテーマに合わせる。
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            var useDark = isDark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref useDark, sizeof(int));
        }
        catch
        {
            // タイトルバーの色設定に失敗しても、標準のタイトルバーで起動を続ける。
        }
    }

    private static Color GetThemeColor(string key, bool isDark)
    {
        if (Application.Current.Resources.TryGetValue(key, out var value)
            && value is SolidColorBrush brush)
        {
            return brush.Color;
        }

        return isDark ? Color.FromArgb(255, 32, 32, 32) : Color.FromArgb(255, 243, 243, 243);
    }

    private static Color Shift(Color color, int amount)
    {
        return Color.FromArgb(
            255,
            (byte)Math.Clamp(color.R + amount, 0, 255),
            (byte)Math.Clamp(color.G + amount, 0, 255),
            (byte)Math.Clamp(color.B + amount, 0, 255));
    }

    private static Color BlendOver(Color color, Color background)
    {
        if (color.A == 255)
        {
            return color;
        }

        var alpha = color.A / 255d;
        return Color.FromArgb(
            255,
            (byte)Math.Round(color.R * alpha + background.R * (1 - alpha)),
            (byte)Math.Round(color.G * alpha + background.G * (1 - alpha)),
            (byte)Math.Round(color.B * alpha + background.B * (1 - alpha)));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
