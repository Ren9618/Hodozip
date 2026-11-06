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
}
