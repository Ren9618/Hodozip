using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace Hodozip.Services;

internal static class WindowHelpers
{
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
}
