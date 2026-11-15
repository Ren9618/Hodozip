using System.Diagnostics;

namespace Hodozip.Services;

internal static class UiHelpers
{
    public static string FormatBytes(long bytes)
    {
        if (bytes < 0)
        {
            return "-";
        }

        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes:N0} B"
            : $"{value:N1} {units[unit]}";
    }

    public static string FormatDateTime(DateTimeOffset? value)
    {
        return value is null ? "-" : value.Value.LocalDateTime.ToString("yyyy/MM/dd HH:mm:ss");
    }

    public static string FormatDateTime(DateTime? value)
    {
        return value is null ? "-" : value.Value.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss");
    }

    public static void OpenFolder(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = "\"" + path.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"",
            UseShellExecute = true,
        });
    }

    /// <summary>
    /// 既存のファイル・フォルダーと衝突しない連番付きのフォルダー パスを返す。
    /// 実際の作成は行わない。
    /// </summary>
    public static string GetUniqueFolderPath(string baseDirectory, string folderName)
    {
        var candidate = Path.Combine(baseDirectory, folderName);
        if (!File.Exists(candidate) && !Directory.Exists(candidate))
        {
            return candidate;
        }

        for (var number = 2; number < 10000; number++)
        {
            var numbered = Path.Combine(baseDirectory, $"{folderName} ({number})");
            if (!File.Exists(numbered) && !Directory.Exists(numbered))
            {
                return numbered;
            }
        }

        return candidate;
    }

    public static void OpenUrl(string url)
    {
        // E2E 用: ブラウザを起動せず、起動要求の URL だけを記録する。
        var logPath = Environment.GetEnvironmentVariable("HODOZIP_OPEN_URL_LOG");
        if (!string.IsNullOrWhiteSpace(logPath))
        {
            try
            {
                File.AppendAllText(logPath, url + Environment.NewLine);
            }
            catch
            {
                // 記録できない場合もブラウザは起動しない（テスト用フックを優先する）。
            }

            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true,
        });
    }
}
