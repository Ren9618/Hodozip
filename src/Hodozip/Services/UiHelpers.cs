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

    public static void OpenUrl(string url)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true,
        });
    }
}
