using Microsoft.UI.Xaml;

namespace Hodozip;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var archivePath = GetArchivePathFromCommandLine();

        if (archivePath is not null)
        {
            // ZIP の関連付けやダブルクリックから起動された場合: すぐに解凍を開始する。
            var progressWindow = new ProgressWindow(archivePath, isCommandLine: true);
            _window = progressWindow;
            progressWindow.Closed += (_, _) => Exit();
            progressWindow.Activate();
            return;
        }

        var mainWindow = new MainWindow();
        _window = mainWindow;
        mainWindow.Closed += (_, _) => Exit();
        mainWindow.Activate();
    }

    private static string? GetArchivePathFromCommandLine()
    {
        var arguments = Environment.GetCommandLineArgs();

        for (var index = 1; index < arguments.Length; index++)
        {
            var argument = arguments[index];
            if (string.IsNullOrWhiteSpace(argument) || argument.StartsWith('-'))
            {
                continue;
            }

            try
            {
                var fullPath = Path.GetFullPath(argument.Trim('"'));
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
            }
            catch
            {
                // 不正な引数は無視して通常起動へフォールバックする。
            }
        }

        return null;
    }
}
