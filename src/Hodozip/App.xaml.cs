using Microsoft.UI.Xaml;
using Hodozip.Core;
using Hodozip.Services;

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
        if (HandleContextMenuCommand())
        {
            return;
        }

        var launchRequest = GetLaunchRequestFromCommandLine();

        if (launchRequest is not null)
        {
            // 関連付けやダブルクリックから起動された場合: すぐに解凍を開始する。
            var progressWindow = new ProgressWindow(
                launchRequest.ArchivePath,
                isCommandLine: true,
                launchRequest.DestinationMode);
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

    /// <summary>
    /// 設定画面と同じ登録処理をコマンドラインから実行する（E2E・トラブルシューティング用）。
    /// </summary>
    private bool HandleContextMenuCommand()
    {
        var arguments = Environment.GetCommandLineArgs();
        var register = arguments.Any(x =>
            string.Equals(x, "--register-context-menu", StringComparison.OrdinalIgnoreCase));
        var unregister = arguments.Any(x =>
            string.Equals(x, "--unregister-context-menu", StringComparison.OrdinalIgnoreCase));

        if (!register && !unregister)
        {
            return false;
        }

        var succeeded = register
            ? LegacyContextMenuService.Register(out _)
            : LegacyContextMenuService.Unregister(out _);

        Environment.ExitCode = succeeded ? 0 : 1;
        Exit();
        return true;
    }

    private static LaunchRequest? GetLaunchRequestFromCommandLine()
    {
        var arguments = Environment.GetCommandLineArgs();
        var destinationMode = ExtractionDestinationMode.Auto;
        string? archivePath = null;

        for (var index = 1; index < arguments.Length; index++)
        {
            var argument = arguments[index];
            if (string.IsNullOrWhiteSpace(argument))
            {
                continue;
            }

            if (string.Equals(argument, "--extract-folder", StringComparison.OrdinalIgnoreCase))
            {
                destinationMode = ExtractionDestinationMode.ForceArchiveFolder;
                continue;
            }

            if (string.Equals(argument, "--extract-here", StringComparison.OrdinalIgnoreCase))
            {
                destinationMode = ExtractionDestinationMode.Auto;
                continue;
            }

            if (argument.StartsWith('-'))
            {
                continue;
            }

            if (archivePath is not null)
            {
                continue;
            }

            try
            {
                var fullPath = Path.GetFullPath(argument.Trim('"'));
                if (File.Exists(fullPath))
                {
                    archivePath = fullPath;
                }
            }
            catch
            {
                // 不正な引数は無視して通常起動へフォールバックする。
            }
        }

        return archivePath is null
            ? null
            : new LaunchRequest(archivePath, destinationMode);
    }

    private sealed record LaunchRequest(
        string ArchivePath,
        ExtractionDestinationMode DestinationMode);
}
