using Microsoft.UI.Xaml;
using Hodozip.Core;
using Hodozip.Services;

namespace Hodozip;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        LocalizationService.Initialize(SettingsService.Load().Language);
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (HandleCompressionCommand())
        {
            return;
        }

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
    /// --compress のコマンドライン処理。--output 指定時は UI なしで圧縮し、
    /// 未指定時は圧縮オプション画面を開く（右クリックメニューや E2E 用）。
    /// 終了コード: 0 = 成功、1 = 圧縮失敗、2 = 引数エラー。
    /// </summary>
    private bool HandleCompressionCommand()
    {
        var arguments = Environment.GetCommandLineArgs();
        if (!arguments.Any(x => string.Equals(x, "--compress", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        string? outputPath = null;
        string? password = null;
        string? formatText = null;
        string? levelText = null;
        string? encodingText = null;
        var overwrite = false;
        var macOsCompatible = false;
        string? error = null;
        var sources = new List<string>();

        for (var index = 1; index < arguments.Length; index++)
        {
            var argument = arguments[index];
            switch (argument.ToLowerInvariant())
            {
                case "--compress":
                    break;
                case "--output":
                    outputPath = ReadValue(arguments, ref index, "--output", ref error);
                    break;
                case "--format":
                    formatText = ReadValue(arguments, ref index, "--format", ref error);
                    break;
                case "--level":
                    levelText = ReadValue(arguments, ref index, "--level", ref error);
                    break;
                case "--password":
                    password = ReadValue(arguments, ref index, "--password", ref error);
                    break;
                case "--zip-encoding":
                    encodingText = ReadValue(arguments, ref index, "--zip-encoding", ref error);
                    break;
                case "--overwrite":
                    overwrite = true;
                    break;
                case "--macos-compatible":
                    macOsCompatible = true;
                    break;
                default:
                    if (!string.IsNullOrWhiteSpace(argument) && !argument.StartsWith('-'))
                    {
                        try
                        {
                            var source = Path.TrimEndingDirectorySeparator(
                                Path.GetFullPath(argument.Trim().Trim('"')));
                            if (File.Exists(source) || Directory.Exists(source))
                            {
                                if (!sources.Any(x => string.Equals(x, source, StringComparison.OrdinalIgnoreCase)))
                                {
                                    sources.Add(source);
                                }
                            }
                            else
                            {
                                error ??= Loc.Compression_SourceMissing(source);
                            }
                        }
                        catch (Exception ex)
                        {
                            error ??= Loc.Compression_SourceMissing(argument + ": " + ex.Message);
                        }
                    }

                    break;
            }
        }

        if (error is not null)
        {
            WriteCommandLineError(error);
            Environment.ExitCode = 2;
            Exit();
            return true;
        }

        ArchiveCreationFormat? format = null;
        if (!string.IsNullOrWhiteSpace(formatText))
        {
            if (string.Equals(formatText, "zip", StringComparison.OrdinalIgnoreCase))
            {
                format = ArchiveCreationFormat.Zip;
            }
            else if (string.Equals(formatText, "7z", StringComparison.OrdinalIgnoreCase)
                || string.Equals(formatText, "sevenzip", StringComparison.OrdinalIgnoreCase))
            {
                format = ArchiveCreationFormat.SevenZip;
            }
            else
            {
                WriteCommandLineError(Loc.Compression_ErrorInvalidFormat(formatText));
                Environment.ExitCode = 2;
                Exit();
                return true;
            }
        }

        var level = ArchiveCompressionLevel.Normal;
        if (!string.IsNullOrWhiteSpace(levelText))
        {
            if (string.Equals(levelText, "store", StringComparison.OrdinalIgnoreCase)
                || string.Equals(levelText, "0", StringComparison.Ordinal))
            {
                level = ArchiveCompressionLevel.Store;
            }
            else if (string.Equals(levelText, "normal", StringComparison.OrdinalIgnoreCase)
                || string.Equals(levelText, "1", StringComparison.Ordinal))
            {
                level = ArchiveCompressionLevel.Normal;
            }
            else if (string.Equals(levelText, "maximum", StringComparison.OrdinalIgnoreCase)
                || string.Equals(levelText, "max", StringComparison.OrdinalIgnoreCase)
                || string.Equals(levelText, "2", StringComparison.Ordinal))
            {
                level = ArchiveCompressionLevel.Maximum;
            }
            else
            {
                WriteCommandLineError(Loc.Compression_ErrorInvalidLevel(levelText));
                Environment.ExitCode = 2;
                Exit();
                return true;
            }
        }

        var encoding = ZipNameEncoding.Utf8;
        if (!string.IsNullOrWhiteSpace(encodingText))
        {
            if (string.Equals(encodingText, "utf8", StringComparison.OrdinalIgnoreCase)
                || string.Equals(encodingText, "utf-8", StringComparison.OrdinalIgnoreCase))
            {
                encoding = ZipNameEncoding.Utf8;
            }
            else if (string.Equals(encodingText, "sjis", StringComparison.OrdinalIgnoreCase)
                || string.Equals(encodingText, "shift_jis", StringComparison.OrdinalIgnoreCase)
                || string.Equals(encodingText, "shift-jis", StringComparison.OrdinalIgnoreCase)
                || string.Equals(encodingText, "cp932", StringComparison.OrdinalIgnoreCase))
            {
                encoding = ZipNameEncoding.ShiftJis;
            }
            else
            {
                WriteCommandLineError(Loc.Compression_ErrorInvalidEncoding(encodingText));
                Environment.ExitCode = 2;
                Exit();
                return true;
            }
        }

        string? normalizedOutput = null;
        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            try
            {
                normalizedOutput = Path.GetFullPath(outputPath.Trim().Trim('"'));
            }
            catch (Exception ex)
            {
                WriteCommandLineError(ex.Message);
                Environment.ExitCode = 2;
                Exit();
                return true;
            }

            var extension = Path.GetExtension(normalizedOutput);
            if (format is null)
            {
                format = string.Equals(extension, ".7z", StringComparison.OrdinalIgnoreCase)
                    ? ArchiveCreationFormat.SevenZip
                    : ArchiveCreationFormat.Zip;
            }

            if (!string.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(extension, ".7z", StringComparison.OrdinalIgnoreCase))
            {
                normalizedOutput += CompressionDefaults.GetExtension(format.Value);
            }
        }

        var options = new CompressionOptions
        {
            Format = format ?? ArchiveCreationFormat.Zip,
            Level = level,
            Password = string.IsNullOrEmpty(password) ? null : password,
            ZipNameEncoding = encoding,
            MacOsCompatible = macOsCompatible,
            Overwrite = overwrite,
        };

        if (normalizedOutput is null)
        {
            ShowCompressionOptionsWindow(sources);
            return true;
        }

        if (sources.Count == 0)
        {
            WriteCommandLineError(Loc.Compression_ErrorNoSources);
            Environment.ExitCode = 2;
            Exit();
            return true;
        }

        RunHeadlessCompression(new CompressionCommand(sources, normalizedOutput, options));
        return true;
    }

    private static string? ReadValue(
        string[] arguments,
        ref int index,
        string option,
        ref string? error)
    {
        if (index + 1 >= arguments.Length)
        {
            error ??= Loc.Compression_ErrorOptionValue(option);
            return null;
        }

        index++;
        return arguments[index];
    }

    private static void WriteCommandLineError(string message)
    {
        try
        {
            Console.Error.WriteLine(message);
        }
        catch
        {
            // コンソールのない起動元では出力できない。
        }
    }

    private void ShowCompressionOptionsWindow(IReadOnlyList<string> sources)
    {
        var optionsWindow = new CompressionOptionsWindow(sources);
        _window = optionsWindow;
        ProgressWindow? progressWindow = null;
        optionsWindow.CompressionRequested += (_, request) =>
        {
            progressWindow = new ProgressWindow(request, isCommandLine: true);
            _window = progressWindow;
            progressWindow.Closed += (_, _) => Exit();
            progressWindow.Activate();
        };
        optionsWindow.Closed += (_, _) =>
        {
            if (progressWindow is null)
            {
                Exit();
            }
        };
        optionsWindow.Activate();
    }

    private async void RunHeadlessCompression(CompressionCommand command)
    {
        // WinUI のメッセージ ループを維持するための非表示ウィンドウ。
        var sentinel = new Window();
        _window = sentinel;

        var compressor = new SharpCompressCompressor();
        try
        {
            var result = await compressor.CompressAsync(
                new CompressionRequest(command.Sources, command.OutputPath, command.Options));
            if (result.Success)
            {
                try
                {
                    Console.Out.WriteLine(result.OutputPath);
                }
                catch
                {
                    // コンソールのない起動元では出力できない。
                }

                Environment.ExitCode = 0;
            }
            else
            {
                WriteCommandLineError(LocalizationService.GetCompressionError(result));
                Environment.ExitCode = 1;
            }
        }
        catch (OperationCanceledException)
        {
            Environment.ExitCode = 1;
        }
        catch (Exception ex)
        {
            WriteCommandLineError(ex.Message);
            Environment.ExitCode = 1;
        }
        finally
        {
            Exit();
        }
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

    private sealed record CompressionCommand(
        IReadOnlyList<string> Sources,
        string OutputPath,
        CompressionOptions Options);
}
