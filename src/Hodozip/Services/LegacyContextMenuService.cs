using System.Runtime.InteropServices;
using Hodozip.Core;
using Microsoft.Win32;

namespace Hodozip.Services;

internal sealed record LegacyContextMenuStatus(
    bool IsSupported,
    bool IsRegistered,
    string Message);

/// <summary>
/// unpackaged 版の従来型（Windows 10 形式 / Windows 11 の「その他のオプションを表示」）
/// コンテキストメニューを HKCU に登録する。管理者権限は不要。
///
/// MSIX 版では従来型シェル拡張を登録できないため、この機能は unpackaged 版だけが対象。
/// </summary>
internal static class LegacyContextMenuService
{
    public const string Clsid = "4F4C6D2A-71B9-4E4E-9A7D-8D9A0B5E6C03";

    private const int ShcneAssocChanged = 0x08000000;
    private const uint ShcnfIdList = 0x0000;

    private static string ClsidKeyPath => $@"Software\Classes\CLSID\{{{Clsid}}}";

    private static string ClsidInprocKeyPath => ClsidKeyPath + @"\InprocServer32";

    public static string DllPath => Path.Combine(AppContext.BaseDirectory, "Hodozip.ContextMenu.dll");

    /// <summary>従来型メニュー登録の対象になる実拡張子（.tar.gz は .gz として登録）。</summary>
    public static IReadOnlyList<string> Extensions =>
        ArchiveFormats.SupportedExtensions
            .Select(x => Path.GetExtension(x))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static LegacyContextMenuStatus GetStatus()
    {
        if (AppInfo.IsPackaged)
        {
            return new LegacyContextMenuStatus(
                IsSupported: false,
                IsRegistered: false,
                "MSIX 版では、Windows の制約により従来型コンテキストメニューを登録できません。Windows 11 の新しい右クリックメニューだけが利用できます。");
        }

        if (!File.Exists(DllPath))
        {
            return new LegacyContextMenuStatus(
                IsSupported: false,
                IsRegistered: false,
                $"Hodozip.ContextMenu.dll が見つかりません: {DllPath}");
        }

        return new LegacyContextMenuStatus(
            IsSupported: true,
            IsRegistered: IsRegistered(),
            IsRegistered()
                ? "従来型コンテキストメニューに登録されています。"
                : "従来型コンテキストメニューには未登録です。");
    }

    public static bool Register(out string message)
    {
        if (AppInfo.IsPackaged)
        {
            message = "MSIX 版では従来型コンテキストメニューを登録できません。";
            return false;
        }

        if (!File.Exists(DllPath))
        {
            message = $"Hodozip.ContextMenu.dll が見つかりません: {DllPath}";
            return false;
        }

        try
        {
            using (var clsidKey = Registry.CurrentUser.CreateSubKey(ClsidKeyPath, writable: true))
            {
                if (clsidKey is null)
                {
                    message = "CLSID のレジストリ キーを作成できませんでした。";
                    return false;
                }

                clsidKey.SetValue(null, "Hodozip Legacy Context Menu");
            }

            using (var inprocKey = Registry.CurrentUser.CreateSubKey(ClsidInprocKeyPath, writable: true))
            {
                if (inprocKey is null)
                {
                    message = "InprocServer32 のレジストリ キーを作成できませんでした。";
                    return false;
                }

                inprocKey.SetValue(null, DllPath);
                inprocKey.SetValue("ThreadingModel", "Apartment");
            }

            foreach (var extension in Extensions)
            {
                var handlerKeyPath =
                    $@"Software\Classes\SystemFileAssociations\{extension}\shellex\ContextMenuHandlers\Hodozip";
                using var handlerKey = Registry.CurrentUser.CreateSubKey(handlerKeyPath, writable: true);
                if (handlerKey is null)
                {
                    message = $"コンテキストメニューの登録キーを作成できませんでした: {extension}";
                    return false;
                }

                handlerKey.SetValue(null, Clsid);
            }

            NotifyShell();
            message = $"従来型コンテキストメニューへ登録しました（{Extensions.Count} 拡張子）。";
            return true;
        }
        catch (Exception ex)
        {
            message = $"従来型コンテキストメニューの登録に失敗しました: {ex.Message}";
            return false;
        }
    }

    public static bool Unregister(out string message)
    {
        if (AppInfo.IsPackaged)
        {
            message = "MSIX 版では従来型コンテキストメニューを登録していません。";
            return false;
        }

        try
        {
            foreach (var extension in Extensions)
            {
                var handlerKeyPath =
                    $@"Software\Classes\SystemFileAssociations\{extension}\shellex\ContextMenuHandlers";
                try
                {
                    using var handlersKey = Registry.CurrentUser.OpenSubKey(handlerKeyPath, writable: true);
                    handlersKey?.DeleteSubKeyTree("Hodozip", throwOnMissingSubKey: false);
                }
                catch
                {
                    // 個別の拡張子の削除失敗は、残りの拡張子の解除を妨げない。
                }
            }

            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(ClsidKeyPath, throwOnMissingSubKey: false);
            }
            catch
            {
                // CLSID が既に存在しない場合は無視する。
            }

            NotifyShell();
            message = "従来型コンテキストメニューから解除しました。";
            return true;
        }
        catch (Exception ex)
        {
            message = $"従来型コンテキストメニューの解除に失敗しました: {ex.Message}";
            return false;
        }
    }

    public static bool IsRegistered()
    {
        try
        {
            using var inprocKey = Registry.CurrentUser.OpenSubKey(ClsidInprocKeyPath);
            var registeredPath = inprocKey?.GetValue(null) as string;
            if (!string.Equals(registeredPath, DllPath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            foreach (var extension in Extensions)
            {
                var handlerKeyPath =
                    $@"Software\Classes\SystemFileAssociations\{extension}\shellex\ContextMenuHandlers\Hodozip";
                using var handlerKey = Registry.CurrentUser.OpenSubKey(handlerKeyPath);
                if (!string.Equals(handlerKey?.GetValue(null) as string, Clsid, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return Extensions.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private static void NotifyShell()
    {
        try
        {
            SHChangeNotify(ShcneAssocChanged, ShcnfIdList, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
            // 通知に失敗しても登録自体は完了している。
        }
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
