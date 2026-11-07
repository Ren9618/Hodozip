using System.Diagnostics;
using Hodozip.Core;
using Microsoft.Win32;

namespace Hodozip.Services;

/// <summary>
/// unpackaged 版の「プログラムから開く」候補（OpenWithProgids）を管理する。
/// 既定アプリ（UserChoice）は Windows が保護しているため変更しない。
/// </summary>
internal static class AssociationService
{
    public const string ProgId = "Hodozip.Archive";

    /// <summary>実際に登録できる単一拡張子（.tar.gz は .gz として扱う）。</summary>
    public static IReadOnlyList<string> Extensions =>
        ArchiveFormats.SupportedExtensions
            .Select(x => Path.GetExtension(x))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static bool IsSupported => !AppInfo.IsPackaged;

    public static bool IsEnabled(string extension)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                $@"Software\Classes\{extension}\OpenWithProgids");
            return key?.GetValueNames().Any(x => string.Equals(x, ProgId, StringComparison.OrdinalIgnoreCase)) == true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 指定形式の候補登録を ON/OFF し、書き込み後に読み直して確認する（冪等）。
    /// </summary>
    public static bool SetEnabled(string extension, bool enabled, out string message)
    {
        if (!IsSupported)
        {
            message = Loc.Settings_AssociationsPackaged;
            return false;
        }

        try
        {
            if (enabled)
            {
                EnsureProgId();
            }

            using var key = Registry.CurrentUser.CreateSubKey(
                $@"Software\Classes\{extension}\OpenWithProgids",
                writable: true);
            if (key is null)
            {
                message = Loc.Settings_AssociationFailed(extension);
                return false;
            }

            if (enabled)
            {
                // OpenWithProgids は値名だけを見るため、データは空（REG_NONE）にする。
                key.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
            }
            else
            {
                key.DeleteValue(ProgId, throwOnMissingValue: false);
            }

            if (IsEnabled(extension) != enabled)
            {
                message = Loc.Settings_AssociationVerifyFailed(extension);
                return false;
            }

            NotifyShell();
            message = enabled
                ? Loc.Settings_AssociationEnabled(extension)
                : Loc.Settings_AssociationDisabled(extension);
            return true;
        }
        catch (Exception ex)
        {
            message = Loc.Settings_AssociationFailedDetail(extension, ex.Message);
            return false;
        }
    }

    /// <summary>設定画面から Windows の「既定のアプリ」画面を開く。</summary>
    public static bool OpenDefaultAppsSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:defaultapps")
            {
                UseShellExecute = true,
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// ポータブル版でも「プログラムから開く」が機能するよう、ProgID を毎回作り直す。
    /// </summary>
    private static void EnsureProgId()
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "Hodozip.exe");
        using var progIdKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}", writable: true);
        progIdKey?.SetValue(null, "Hodozip Archive");

        using var iconKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}\DefaultIcon", writable: true);
        iconKey?.SetValue(null, executable + ",0");

        using var commandKey = Registry.CurrentUser.CreateSubKey(
            $@"Software\Classes\{ProgId}\shell\open\command",
            writable: true);
        commandKey?.SetValue(null, $"\"{executable}\" \"%1\"");
    }

    private static void NotifyShell()
    {
        try
        {
            SHChangeNotify(0x08000000, 0x3000, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
            // 通知に失敗しても登録自体は完了している。
        }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
