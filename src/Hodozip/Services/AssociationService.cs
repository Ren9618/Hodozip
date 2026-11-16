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

    /// <summary>Windows の「既定のアプリ」一覧に表示するときのアプリ名。</summary>
    public const string RegisteredApplicationName = "Hodozip";

    private const string RegisteredApplicationsKeyPath = @"Software\RegisteredApplications";
    private const string CapabilitiesKeyPath = @"Software\Hodozip\Capabilities";
    private const string FileAssociationsKeyPath = CapabilitiesKeyPath + @"\FileAssociations";

    /// <summary>
    /// 実際に登録できる単一拡張子（.tar.gz は .gz として扱う）。
    /// インストーラー / アプリ パッケージは、Hodozip が既定のアプリとして
    /// 開いてしまわないよう候補から除外する。
    /// </summary>
    public static IReadOnlyList<string> Extensions =>
        ArchiveFormats.SupportedExtensions
            .Select(x => Path.GetExtension(x))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .Where(x => !ArchiveFormats.IsInstallerPackageExtension(x))
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
            RemoveInstallerAssociations();

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

            // チェック状態に合わせて「既定のアプリ」一覧への表示を更新する。
            RefreshRegisteredApplication();
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

    /// <summary>
    /// 旧バージョンで登録したインストーラー形式の「プログラムから開く」候補を削除する。
    /// 現在は設定画面に表示しないが、アップグレード後に登録が残らないようにする。
    /// </summary>
    public static void RemoveInstallerAssociations()
    {
        if (!IsSupported)
        {
            return;
        }

        var changed = false;
        foreach (var extension in ArchiveFormats.InstallerPackageExtensions)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    $@"Software\Classes\{extension}\OpenWithProgids",
                    writable: true);
                if (key is null
                    || !key.GetValueNames().Any(x => string.Equals(x, ProgId, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                key.DeleteValue(ProgId, throwOnMissingValue: false);
                changed = true;
            }
            catch
            {
                // 削除できなくても他の拡張子の処理は続ける。
            }
        }

        if (changed)
        {
            NotifyShell();
        }
    }

    /// <summary>
    /// Windows の「既定のアプリ」一覧に Hodozip を表示するための登録
    /// （RegisteredApplications / Capabilities）を、現在チェックされている
    /// 拡張子に合わせて更新する。既定アプリ自体は変更できない。
    /// </summary>
    public static void RefreshRegisteredApplication()
    {
        if (!IsSupported)
        {
            return;
        }

        try
        {
            var enabledExtensions = Extensions.Where(IsEnabled).ToList();
            if (enabledExtensions.Count == 0)
            {
                RemoveRegisteredApplication();
                return;
            }

            using (var capabilities = Registry.CurrentUser.CreateSubKey(CapabilitiesKeyPath, writable: true))
            {
                if (capabilities is null)
                {
                    return;
                }

                capabilities.SetValue("ApplicationName", RegisteredApplicationName);
                capabilities.SetValue("ApplicationDescription", Loc.Association_ApplicationDescription);
            }

            using (var fileAssociations = Registry.CurrentUser.CreateSubKey(FileAssociationsKeyPath, writable: true))
            {
                if (fileAssociations is null)
                {
                    return;
                }

                // 解除された拡張子が残らないよう、いったん全値を消してから作り直す。
                foreach (var valueName in fileAssociations.GetValueNames())
                {
                    fileAssociations.DeleteValue(valueName, throwOnMissingValue: false);
                }

                foreach (var extension in enabledExtensions)
                {
                    fileAssociations.SetValue(extension, ProgId, RegistryValueKind.String);
                }
            }

            using var registered = Registry.CurrentUser.CreateSubKey(RegisteredApplicationsKeyPath, writable: true);
            registered?.SetValue(RegisteredApplicationName, CapabilitiesKeyPath, RegistryValueKind.String);
        }
        catch
        {
            // 「既定のアプリ」一覧への登録は補助機能。
            // 失敗しても「プログラムから開く」の関連付け自体は維持する。
        }
    }

    /// <summary>設定画面から Windows の「既定のアプリ」画面を開く。</summary>
    public static bool OpenDefaultAppsSettings()
    {
        var uri = GetDefaultAppsSettingsUri();
        if (TryOpenSettings(uri))
        {
            return true;
        }

        return uri != "ms-settings:defaultapps" && TryOpenSettings("ms-settings:defaultapps");
    }

    /// <summary>
    /// Windows 11 では Hodozip 専用の既定アプリ ページを開き、
    /// Windows 10 では既定アプリの一覧ページを開く URI を返す。
    /// </summary>
    internal static string GetDefaultAppsSettingsUri()
    {
        if (IsSupported
            && Environment.OSVersion.Version.Build >= 22000
            && IsRegisteredApplicationPresent())
        {
            return "ms-settings:defaultapps?registeredAppUser="
                + Uri.EscapeDataString(RegisteredApplicationName);
        }

        return "ms-settings:defaultapps";
    }

    private static bool TryOpenSettings(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri)
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

    private static bool IsRegisteredApplicationPresent()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegisteredApplicationsKeyPath);
            return key?.GetValue(RegisteredApplicationName) is string path
                && string.Equals(path, CapabilitiesKeyPath, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void RemoveRegisteredApplication()
    {
        try
        {
            using (var registered = Registry.CurrentUser.OpenSubKey(
                RegisteredApplicationsKeyPath,
                writable: true))
            {
                registered?.DeleteValue(RegisteredApplicationName, throwOnMissingValue: false);
            }

            using var capabilitiesParent = Registry.CurrentUser.OpenSubKey(
                @"Software\Hodozip",
                writable: true);
            capabilitiesParent?.DeleteSubKeyTree("Capabilities", throwOnMissingSubKey: false);
        }
        catch
        {
            // 表示用の登録を消せなくても、関連付けの解除は完了している。
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
