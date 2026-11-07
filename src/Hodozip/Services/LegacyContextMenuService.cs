using System.Runtime.InteropServices;
using Hodozip.Core;
using Microsoft.Win32;

namespace Hodozip.Services;

internal sealed record LegacyContextMenuStatus(
    bool IsSupported,
    bool IsRegistered,
    string Message);

internal sealed record LegacyContextMenuResult(bool Succeeded, string Message);

/// <summary>
/// unpackaged 版の従来型（Windows 10 形式 / Windows 11 の「その他のオプションを表示」）
/// コンテキストメニューを HKCU に登録する。管理者権限は不要。
///
/// v0.6.0 からは解凍 2 項目と圧縮 1 項目を 1 つのハンドラーに統合した。
/// 旧バージョンの分割登録（解凍用 / 圧縮用の別 CLSID）は登録時に掃除する。
///
/// MSIX 版では従来型シェル拡張を登録できないため、この機能は unpackaged 版だけが対象。
/// </summary>
internal static class LegacyContextMenuService
{
    /// <summary>統合ハンドラーの CLSID。v0.5.x の解凍ハンドラーと同じ値を使う。</summary>
    public const string Clsid = "4F4C6D2A-71B9-4E4E-9A7D-8D9A0B5E6C03";

    /// <summary>v0.5.x まで圧縮メニューに使っていた CLSID。掃除のためだけに保持する。</summary>
    private const string LegacyCompressClsid = "4F4C6D2A-71B9-4E4E-9A7D-8D9A0B5E6C05";

    private const int ShcneAssocChanged = 0x08000000;
    private const uint ShcnfIdList = 0x0000;
    private const uint ShcnfFlushNoWait = 0x3000;

    /// <summary>
    /// shell\shellex\ContextMenuHandlers の既定値に入れる CLSID は、
    /// 必ず中かっこ付き（{...}）で書く。中かっこがない値は Explorer が
    /// ハンドラーとして認識せず、エラーも出さずに無視する。
    /// </summary>
    private static string HandlerClsid => $"{{{Clsid}}}";

    private static string ClsidKeyPath => $@"Software\Classes\CLSID\{{{Clsid}}}";

    private static string ClsidInprocKeyPath => ClsidKeyPath + @"\InprocServer32";

    private static string LegacyCompressClsidKeyPath =>
        $@"Software\Classes\CLSID\{{{LegacyCompressClsid}}}";

    /// <summary>統合ハンドラーを登録する場所。ファイルは *、フォルダーは Directory。</summary>
    private static readonly string[] UnifiedHandlerKeyPaths =
    {
        @"Software\Classes\*\shellex\ContextMenuHandlers\Hodozip",
        @"Software\Classes\Directory\shellex\ContextMenuHandlers\Hodozip",
    };

    /// <summary>v0.5.x の圧縮ハンドラー登録。統合版へ移行するときに削除する。</summary>
    private static readonly string[] OldCompressHandlerKeyPaths =
    {
        @"Software\Classes\*\shellex\ContextMenuHandlers\HodozipCompress",
        @"Software\Classes\Directory\shellex\ContextMenuHandlers\HodozipCompress",
    };

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
                Loc.Legacy_PackagedUnsupported);
        }

        if (!File.Exists(DllPath))
        {
            return new LegacyContextMenuStatus(
                IsSupported: false,
                IsRegistered: false,
                Loc.Legacy_DllMissing(DllPath));
        }

        var isRegistered = IsRegistered();
        return new LegacyContextMenuStatus(
            IsSupported: true,
            IsRegistered: isRegistered,
            isRegistered
                ? Loc.Legacy_Registered
                : Loc.Legacy_NotRegistered);
    }

    public static Task<LegacyContextMenuResult> RegisterAsync()
    {
        return Task.Run(() =>
        {
            var succeeded = Register(out var message);
            return new LegacyContextMenuResult(succeeded, message);
        });
    }

    public static Task<LegacyContextMenuResult> UnregisterAsync()
    {
        return Task.Run(() =>
        {
            var succeeded = Unregister(out var message);
            return new LegacyContextMenuResult(succeeded, message);
        });
    }

    public static bool Register(out string message)
    {
        if (AppInfo.IsPackaged)
        {
            message = Loc.Legacy_PackagedCannotRegister;
            return false;
        }

        if (!File.Exists(DllPath))
        {
            message = Loc.Legacy_DllMissing(DllPath);
            return false;
        }

        try
        {
            // 旧レイアウト（拡張子ごとの解凍ハンドラーと別 CLSID の圧縮ハンドラー）を
            // 先に掃除してから、統合ハンドラーを書き直す。冪等に何度でも実行できる。
            CleanupOldLayout();

            using (var clsidKey = Registry.CurrentUser.CreateSubKey(ClsidKeyPath, writable: true))
            {
                if (clsidKey is null)
                {
                    message = Loc.Legacy_ClsidCreateFailed;
                    return false;
                }

                clsidKey.SetValue(null, "Hodozip Context Menu");
            }

            using (var inprocKey = Registry.CurrentUser.CreateSubKey(ClsidInprocKeyPath, writable: true))
            {
                if (inprocKey is null)
                {
                    message = Loc.Legacy_InprocCreateFailed;
                    return false;
                }

                inprocKey.SetValue(null, DllPath);
                inprocKey.SetValue("ThreadingModel", "Apartment");
            }

            foreach (var handlerKeyPath in UnifiedHandlerKeyPaths)
            {
                using var handlerKey = Registry.CurrentUser.CreateSubKey(handlerKeyPath, writable: true);
                if (handlerKey is null)
                {
                    message = Loc.Legacy_HandlerCreateFailed(handlerKeyPath);
                    return false;
                }

                handlerKey.SetValue(null, HandlerClsid);
            }

            // 書き込み後に実際のレジストリ内容を読み直して確認する。
            // 一部のキーだけ書けた状態を成功として報告しない。
            if (!IsRegistered())
            {
                message = Loc.Legacy_VerificationFailed;
                return false;
            }

            NotifyShell();
            message = Loc.Legacy_RegisteredCount(Extensions.Count);
            return true;
        }
        catch (Exception ex)
        {
            message = Loc.Legacy_RegisterFailed(ex.Message);
            return false;
        }
    }

    public static bool Unregister(out string message)
    {
        if (AppInfo.IsPackaged)
        {
            message = Loc.Legacy_PackagedNotRegistered;
            return false;
        }

        try
        {
            CleanupOldLayout();

            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(ClsidKeyPath, throwOnMissingSubKey: false);
            }
            catch
            {
                // CLSID が既に存在しない場合は無視する。
            }

            foreach (var handlerKeyPath in UnifiedHandlerKeyPaths)
            {
                DeleteHandlerKey(handlerKeyPath);
            }

            // 解除後に残骸がないことまで確認する。削除に失敗した場合は
            // 成功として報告しない。
            if (HasRegistrationLeftovers())
            {
                message = Loc.Legacy_UnregisterIncomplete;
                return false;
            }

            NotifyShell();
            message = Loc.Legacy_Unregistered;
            return true;
        }
        catch (Exception ex)
        {
            message = Loc.Legacy_UnregisterFailed(ex.Message);
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

            foreach (var handlerKeyPath in UnifiedHandlerKeyPaths)
            {
                using var handlerKey = Registry.CurrentUser.OpenSubKey(handlerKeyPath);
                if (!string.Equals(handlerKey?.GetValue(null) as string, HandlerClsid, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            // 旧レイアウトが残っている間は「統合版として登録済み」と報告しない。
            return !HasOldLayout();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>拡張子ごとの旧解凍ハンドラー、旧圧縮ハンドラー、旧圧縮 CLSID を削除する。</summary>
    private static void CleanupOldLayout()
    {
        foreach (var extension in Extensions)
        {
            DeleteHandlerKey(
                $@"Software\Classes\SystemFileAssociations\{extension}\shellex\ContextMenuHandlers\Hodozip");
        }

        foreach (var handlerKeyPath in OldCompressHandlerKeyPaths)
        {
            DeleteHandlerKey(handlerKeyPath);
        }

        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(LegacyCompressClsidKeyPath, throwOnMissingSubKey: false);
        }
        catch
        {
            // 既に存在しない場合は無視する。
        }
    }

    private static void DeleteHandlerKey(string handlerKeyPath)
    {
        try
        {
            var parentPath = Path.GetDirectoryName(handlerKeyPath);
            var keyName = Path.GetFileName(handlerKeyPath);
            if (string.IsNullOrEmpty(parentPath) || string.IsNullOrEmpty(keyName))
            {
                return;
            }

            using var handlersKey = Registry.CurrentUser.OpenSubKey(parentPath, writable: true);
            handlersKey?.DeleteSubKeyTree(keyName, throwOnMissingSubKey: false);
        }
        catch
        {
            // 個別の削除失敗は、残りの解除を妨げない。
        }
    }

    private static bool HasOldLayout()
    {
        if (KeyExists(LegacyCompressClsidKeyPath))
        {
            return true;
        }

        if (OldCompressHandlerKeyPaths.Any(KeyExists))
        {
            return true;
        }

        return Extensions.Any(extension => KeyExists(
            $@"Software\Classes\SystemFileAssociations\{extension}\shellex\ContextMenuHandlers\Hodozip"));
    }

    private static bool HasRegistrationLeftovers()
    {
        if (KeyExists(ClsidKeyPath) || KeyExists(LegacyCompressClsidKeyPath))
        {
            return true;
        }

        if (HasOldLayout())
        {
            return true;
        }

        return UnifiedHandlerKeyPaths.Any(KeyExists);
    }

    private static bool KeyExists(string path)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path);
        return key is not null;
    }

    private static void NotifyShell()
    {
        try
        {
            // FLUSHNOWAIT を使い、Explorer の応答を待たずに通知する。
            // 同期フラッシュは Explorer がビジーなときに呼び出し元を
            // ブロックし、UI や登録処理が応答なしになる原因になる。
            SHChangeNotify(ShcneAssocChanged, ShcnfIdList | ShcnfFlushNoWait, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
            // 通知に失敗しても登録自体は完了している。
        }
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
