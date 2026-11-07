// Hodozip context-menu COM server.
//
// This DLL implements the shell's IExplorerCommand interface (Windows 11 new
// menu) and the legacy IContextMenu interface (unpackaged HKCU registration).
// It does not contain extraction or compression logic. Invoke() launches
// Hodozip.exe with either --extract-here/--extract-folder and one archive path,
// or --compress and every selected path.

#include <windows.h>
#include <shobjidl.h>
#include <shlobj.h>
#include <shellapi.h>
#include <shlwapi.h>
#include <strsafe.h>

#include <new>
#include <string>
#include <vector>

#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "shlwapi.lib")
#pragma comment(lib, "user32.lib")

namespace
{
    HINSTANCE g_instance = nullptr;
    volatile LONG g_liveObjects = 0;

    // Extract here: {4F4C6D2A-71B9-4E4E-9A7D-8D9A0B5E6C01}
    const CLSID CLSID_HodozipExtractHere =
        { 0x4f4c6d2a, 0x71b9, 0x4e4e, { 0x9a, 0x7d, 0x8d, 0x9a, 0x0b, 0x5e, 0x6c, 0x01 } };

    // Extract to folder: {4F4C6D2A-71B9-4E4E-9A7D-8D9A0B5E6C02}
    const CLSID CLSID_HodozipExtractFolder =
        { 0x4f4c6d2a, 0x71b9, 0x4e4e, { 0x9a, 0x7d, 0x8d, 0x9a, 0x0b, 0x5e, 0x6c, 0x02 } };

    // Legacy IContextMenu extract handler: {4F4C6D2A-71B9-4E4E-9A7D-8D9A0B5E6C03}
    const CLSID CLSID_HodozipLegacyMenu =
        { 0x4f4c6d2a, 0x71b9, 0x4e4e, { 0x9a, 0x7d, 0x8d, 0x9a, 0x0b, 0x5e, 0x6c, 0x03 } };

    // Compress (Windows 11 menu): {4F4C6D2A-71B9-4E4E-9A7D-8D9A0B5E6C04}
    const CLSID CLSID_HodozipCompress =
        { 0x4f4c6d2a, 0x71b9, 0x4e4e, { 0x9a, 0x7d, 0x8d, 0x9a, 0x0b, 0x5e, 0x6c, 0x04 } };

    // Legacy IContextMenu compress handler: {4F4C6D2A-71B9-4E4E-9A7D-8D9A0B5E6C05}
    const CLSID CLSID_HodozipLegacyCompress =
        { 0x4f4c6d2a, 0x71b9, 0x4e4e, { 0x9a, 0x7d, 0x8d, 0x9a, 0x0b, 0x5e, 0x6c, 0x05 } };

    bool IsSupportedArchivePath(const std::wstring& path)
    {
        const wchar_t* extension = PathFindExtensionW(path.c_str());
        static const wchar_t* supportedExtensions[] = {
            L".zip",
            L".7z",
            L".rar",
            L".msi",
            L".tar",
            L".gz",
            L".tgz",
            L".bz2",
            L".tbz2",
            L".xz",
            L".txz",
            L".zst",
            L".tzst",
        };

        for (const auto* supported : supportedExtensions)
        {
            if (_wcsicmp(extension, supported) == 0)
            {
                return true;
            }
        }

        return false;
    }

    // シェル拡張の表示言語は、アプリ設定ではなく OS の UI 言語だけで決める。
    // （エクスプローラーは別プロセスで動くため、アプリ側の .resw を参照できない。）
    bool IsJapaneseUiLanguage()
    {
        return PRIMARYLANGID(GetUserDefaultUILanguage()) == LANG_JAPANESE;
    }

    const wchar_t* ExtractHereMenuText()
    {
        return IsJapaneseUiLanguage() ? L"Hodozip で解凍" : L"Extract with Hodozip";
    }

    const wchar_t* ExtractFolderMenuText()
    {
        return IsJapaneseUiLanguage() ? L"Hodozip でフォルダに解凍" : L"Extract to folder with Hodozip";
    }

    const wchar_t* ExtractHereHelpText()
    {
        return IsJapaneseUiLanguage()
            ? L"アーカイブと同じ場所に解凍します"
            : L"Extract to the same folder as the archive";
    }

    const wchar_t* ExtractFolderHelpText()
    {
        return IsJapaneseUiLanguage()
            ? L"アーカイブ名のフォルダに解凍します"
            : L"Extract into a folder named after the archive";
    }

    const wchar_t* CompressMenuText()
    {
        return IsJapaneseUiLanguage() ? L"Hodozip で圧縮" : L"Compress with Hodozip";
    }

    const wchar_t* CompressHelpText()
    {
        return IsJapaneseUiLanguage()
            ? L"選択したファイルとフォルダを ZIP または 7z にまとめます"
            : L"Combine the selected files and folders into a ZIP or 7z archive";
    }

    std::wstring GetModulePath()
    {
        std::wstring buffer(MAX_PATH, L'\0');

        for (;;)
        {
            const DWORD length = GetModuleFileNameW(
                g_instance,
                buffer.data(),
                static_cast<DWORD>(buffer.size()));

            if (length == 0)
            {
                return {};
            }

            if (length < buffer.size())
            {
                buffer.resize(length);
                return buffer;
            }

            buffer.resize(buffer.size() * 2);
        }
    }

    std::wstring GetHodozipExecutablePath()
    {
        auto path = GetModulePath();
        if (path.empty())
        {
            return {};
        }

        const auto separator = path.find_last_of(L"\\/");
        if (separator == std::wstring::npos)
        {
            return {};
        }

        path.resize(separator + 1);
        path += L"Hodozip.exe";
        return path;
    }

    HRESULT LaunchHodozipProcess(const std::wstring& arguments)
    {
        const auto executablePath = GetHodozipExecutablePath();
        if (executablePath.empty())
        {
            return HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND);
        }

        std::wstring workingDirectory = executablePath;
        const auto separator = workingDirectory.find_last_of(L"\\/");
        if (separator != std::wstring::npos)
        {
            workingDirectory.resize(separator);
        }

        SHELLEXECUTEINFOW info = {};
        info.cbSize = sizeof(info);
        info.fMask = SEE_MASK_NOASYNC | SEE_MASK_FLAG_NO_UI;
        info.lpVerb = L"open";
        info.lpFile = executablePath.c_str();
        info.lpParameters = arguments.c_str();
        info.lpDirectory = workingDirectory.empty() ? nullptr : workingDirectory.c_str();
        info.nShow = SW_SHOWNORMAL;

        if (!ShellExecuteExW(&info))
        {
            const DWORD error = GetLastError();
            return HRESULT_FROM_WIN32(error == ERROR_SUCCESS ? ERROR_FILE_NOT_FOUND : error);
        }

        if (info.hProcess != nullptr)
        {
            CloseHandle(info.hProcess);
        }

        return S_OK;
    }

    HRESULT LaunchHodozip(const std::wstring& archivePath, bool forceArchiveFolder)
    {
        std::wstring arguments = forceArchiveFolder
            ? L"--extract-folder \""
            : L"--extract-here \"";
        arguments += archivePath;
        arguments += L'"';
        return LaunchHodozipProcess(arguments);
    }

    HRESULT LaunchHodozipCompress(const std::vector<std::wstring>& paths)
    {
        if (paths.empty())
        {
            return S_OK;
        }

        std::wstring arguments = L"--compress";
        for (const auto& path : paths)
        {
            arguments += L" \"";
            arguments += path;
            arguments += L'"';
        }

        return LaunchHodozipProcess(arguments);
    }

    HBITMAP IconToBitmap(HICON icon, int width, int height)
    {
        if (icon == nullptr)
        {
            return nullptr;
        }

        BITMAPINFO info = {};
        info.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
        info.bmiHeader.biWidth = width;
        info.bmiHeader.biHeight = -height;
        info.bmiHeader.biPlanes = 1;
        info.bmiHeader.biBitCount = 32;
        info.bmiHeader.biCompression = BI_RGB;

        void* bits = nullptr;
        HDC deviceContext = CreateCompatibleDC(nullptr);
        if (deviceContext == nullptr)
        {
            return nullptr;
        }

        HBITMAP bitmap = CreateDIBSection(
            deviceContext,
            &info,
            DIB_RGB_COLORS,
            &bits,
            nullptr,
            0);
        if (bitmap != nullptr)
        {
            const HGDIOBJ previous = SelectObject(deviceContext, bitmap);
            DrawIconEx(deviceContext, 0, 0, icon, width, height, 0, nullptr, DI_NORMAL);
            SelectObject(deviceContext, previous);
        }

        DeleteDC(deviceContext);
        return bitmap;
    }

    // The legacy menu needs an HBITMAP rather than an HICON. Extract the icon
    // from Hodozip.exe once and keep it for the lifetime of this small DLL.
    HBITMAP GetMenuBitmap()
    {
        static HBITMAP cached = nullptr;
        if (cached != nullptr)
        {
            return cached;
        }

        const auto executable = GetHodozipExecutablePath();
        if (executable.empty())
        {
            return nullptr;
        }

        HICON largeIcon = nullptr;
        HICON smallIcon = nullptr;
        const UINT extracted = ExtractIconExW(
            executable.c_str(),
            0,
            &largeIcon,
            &smallIcon,
            1);


        if (extracted == 0)
        {
            return nullptr;
        }

        cached = IconToBitmap(
            smallIcon != nullptr ? smallIcon : largeIcon,
            GetSystemMetrics(SM_CXSMICON),
            GetSystemMetrics(SM_CYSMICON));


        if (smallIcon != nullptr)
        {
            DestroyIcon(smallIcon);
        }

        if (largeIcon != nullptr)
        {
            DestroyIcon(largeIcon);
        }

        return cached;
    }


    // Associate the menu icon. SetMenuItemInfo(MIIM_BITMAP) also lets tests
    // read the bitmap back with GetMenuItemInfo, which SetMenuItemBitmaps
    // does not expose reliably.
    void ApplyMenuIcon(HMENU hmenu, UINT id, HBITMAP icon)
    {
        if (icon == nullptr)
        {
            return;
        }

        MENUITEMINFOW info = {};
        info.cbSize = sizeof(info);
        info.fMask = MIIM_BITMAP;
        info.hbmpItem = icon;
        SetMenuItemInfoW(hmenu, id, FALSE, &info);
    }

    class CExplorerCommand final : public IExplorerCommand
    {
    public:
        explicit CExplorerCommand(bool forceArchiveFolder, bool compress = false)
            : _forceArchiveFolder(forceArchiveFolder),
              _compress(compress)
        {
            InterlockedIncrement(&g_liveObjects);
        }

        IFACEMETHODIMP QueryInterface(REFIID riid, void** ppvObject) override
        {
            if (ppvObject == nullptr)
            {
                return E_POINTER;
            }

            *ppvObject = nullptr;

            if (IsEqualIID(riid, IID_IUnknown) || IsEqualIID(riid, IID_IExplorerCommand))
            {
                *ppvObject = static_cast<IExplorerCommand*>(this);
                AddRef();
                return S_OK;
            }

            return E_NOINTERFACE;
        }

        IFACEMETHODIMP_(ULONG) AddRef() override
        {
            InterlockedIncrement(&g_liveObjects);
            return static_cast<ULONG>(InterlockedIncrement(&_referenceCount));
        }

        IFACEMETHODIMP_(ULONG) Release() override
        {
            const LONG count = InterlockedDecrement(&_referenceCount);
            if (count == 0)
            {
                delete this;
            }

            InterlockedDecrement(&g_liveObjects);
            return static_cast<ULONG>(count);
        }

        IFACEMETHODIMP GetTitle(IShellItemArray*, LPWSTR* ppszName) override
        {
            if (ppszName == nullptr)
            {
                return E_POINTER;
            }

            *ppszName = nullptr;
            return SHStrDupW(
                _compress
                    ? CompressMenuText()
                    : (_forceArchiveFolder ? ExtractFolderMenuText() : ExtractHereMenuText()),
                ppszName);
        }

        IFACEMETHODIMP GetIcon(IShellItemArray*, LPWSTR* ppszIcon) override
        {
            if (ppszIcon == nullptr)
            {
                return E_POINTER;
            }

            *ppszIcon = nullptr;
            const auto executable = GetHodozipExecutablePath();
            if (executable.empty())
            {
                return E_FAIL;
            }

            const std::wstring iconReference = executable + L",0";
            return SHStrDupW(iconReference.c_str(), ppszIcon);
        }

        IFACEMETHODIMP GetToolTip(IShellItemArray*, LPWSTR* ppszInfotip) override
        {
            if (ppszInfotip != nullptr)
            {
                *ppszInfotip = nullptr;
            }

            return E_NOTIMPL;
        }

        IFACEMETHODIMP GetCanonicalName(GUID* pguidCommandName) override
        {
            if (pguidCommandName == nullptr)
            {
                return E_POINTER;
            }

            *pguidCommandName = _compress
                ? CLSID_HodozipCompress
                : (_forceArchiveFolder ? CLSID_HodozipExtractFolder : CLSID_HodozipExtractHere);
            return S_OK;
        }

        IFACEMETHODIMP GetState(IShellItemArray*, BOOL, EXPCMDSTATE* pCmdState) override
        {
            if (pCmdState == nullptr)
            {
                return E_POINTER;
            }

            *pCmdState = ECS_ENABLED;
            return S_OK;
        }

        IFACEMETHODIMP Invoke(IShellItemArray* psiItemArray, IBindCtx*) override
        {
            if (psiItemArray == nullptr)
            {
                return S_OK;
            }

            if (_compress)
            {
                return InvokeCompress(psiItemArray);
            }

            DWORD count = 0;
            HRESULT result = psiItemArray->GetCount(&count);
            if (FAILED(result))
            {
                return result;
            }

            HRESULT firstFailure = S_OK;

            for (DWORD index = 0; index < count; ++index)
            {
                IShellItem* item = nullptr;
                result = psiItemArray->GetItemAt(index, &item);
                if (FAILED(result) || item == nullptr)
                {
                    if (firstFailure == S_OK)
                    {
                        firstFailure = result;
                    }
                    continue;
                }

                LPWSTR fileSystemPath = nullptr;
                result = item->GetDisplayName(SIGDN_FILESYSPATH, &fileSystemPath);
                item->Release();

                if (FAILED(result) || fileSystemPath == nullptr)
                {
                    if (firstFailure == S_OK)
                    {
                        firstFailure = result;
                    }
                    continue;
                }

                if (IsSupportedArchivePath(fileSystemPath))
                {
                    const HRESULT launchResult = LaunchHodozip(fileSystemPath, _forceArchiveFolder);
                    if (FAILED(launchResult) && firstFailure == S_OK)
                    {
                        firstFailure = launchResult;
                    }
                }

                CoTaskMemFree(fileSystemPath);
            }

            return firstFailure;
        }

        IFACEMETHODIMP GetFlags(EXPCMDFLAGS* pFlags) override
        {
            if (pFlags == nullptr)
            {
                return E_POINTER;
            }

            *pFlags = ECF_DEFAULT;
            return S_OK;
        }

        IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** ppEnum) override
        {
            if (ppEnum != nullptr)
            {
                *ppEnum = nullptr;
            }

            return E_NOTIMPL;
        }

    private:
        ~CExplorerCommand() = default;

        HRESULT InvokeCompress(IShellItemArray* psiItemArray)
        {
            DWORD count = 0;
            HRESULT result = psiItemArray->GetCount(&count);
            if (FAILED(result))
            {
                return result;
            }

            std::vector<std::wstring> paths;
            HRESULT firstFailure = S_OK;

            for (DWORD index = 0; index < count; ++index)
            {
                IShellItem* item = nullptr;
                result = psiItemArray->GetItemAt(index, &item);
                if (FAILED(result) || item == nullptr)
                {
                    if (firstFailure == S_OK)
                    {
                        firstFailure = result;
                    }
                    continue;
                }

                LPWSTR fileSystemPath = nullptr;
                result = item->GetDisplayName(SIGDN_FILESYSPATH, &fileSystemPath);
                item->Release();

                if (FAILED(result) || fileSystemPath == nullptr)
                {
                    if (firstFailure == S_OK)
                    {
                        firstFailure = result;
                    }
                    continue;
                }

                paths.emplace_back(fileSystemPath);
                CoTaskMemFree(fileSystemPath);
            }

            if (paths.empty())
            {
                return firstFailure;
            }

            const HRESULT launchResult = LaunchHodozipCompress(paths);
            return FAILED(launchResult) ? launchResult : firstFailure;
        }

        LONG _referenceCount = 1;
        const bool _forceArchiveFolder;
        const bool _compress;
    };

    // Legacy (Windows 10 style) context menu.
    //
    // A single handler inserts all three Hodozip commands consecutively for
    // archives, and only "Compress" for other files and folders. Registering
    // one CLSID avoids the commands being scattered by separate handlers.
    class CLegacyContextMenu final : public IShellExtInit, public IContextMenu
    {
    public:
        CLegacyContextMenu()
        {
            InterlockedIncrement(&g_liveObjects);
        }

        IFACEMETHODIMP QueryInterface(REFIID riid, void** ppvObject) override
        {
            if (ppvObject == nullptr)
            {
                return E_POINTER;
            }

            *ppvObject = nullptr;

            if (IsEqualIID(riid, IID_IUnknown) || IsEqualIID(riid, IID_IShellExtInit))
            {
                *ppvObject = static_cast<IShellExtInit*>(this);
                AddRef();
                return S_OK;
            }

            if (IsEqualIID(riid, IID_IContextMenu))
            {
                *ppvObject = static_cast<IContextMenu*>(this);
                AddRef();
                return S_OK;
            }

            return E_NOINTERFACE;
        }

        IFACEMETHODIMP_(ULONG) AddRef() override
        {
            InterlockedIncrement(&g_liveObjects);
            return static_cast<ULONG>(InterlockedIncrement(&_referenceCount));
        }

        IFACEMETHODIMP_(ULONG) Release() override
        {
            const LONG count = InterlockedDecrement(&_referenceCount);
            if (count == 0)
            {
                delete this;
            }

            InterlockedDecrement(&g_liveObjects);
            return static_cast<ULONG>(count);
        }

        IFACEMETHODIMP Initialize(PCIDLIST_ABSOLUTE, IDataObject* dataObject, HKEY) override
        {
            _paths.clear();

            if (dataObject == nullptr)
            {
                return E_INVALIDARG;
            }

            FORMATETC format = {};
            format.cfFormat = CF_HDROP;
            format.dwAspect = DVASPECT_CONTENT;
            format.lindex = -1;
            format.tymed = TYMED_HGLOBAL;

            STGMEDIUM medium = {};
            if (FAILED(dataObject->GetData(&format, &medium)))
            {
                return S_OK;
            }

            if (medium.tymed == TYMED_HGLOBAL && medium.hGlobal != nullptr)
            {
                auto* drop = static_cast<HDROP>(GlobalLock(medium.hGlobal));
                if (drop != nullptr)
                {
                    const UINT count = DragQueryFileW(drop, 0xFFFFFFFF, nullptr, 0);
                    for (UINT index = 0; index < count; ++index)
                    {
                        const UINT length = DragQueryFileW(drop, index, nullptr, 0);
                        if (length == 0)
                        {
                            continue;
                        }

                        std::wstring path(length + 1, L'\0');
                        const UINT copied = DragQueryFileW(
                            drop,
                            index,
                            path.data(),
                            static_cast<UINT>(path.size()));
                        if (copied > 0)
                        {
                            path.resize(copied);
                            _paths.push_back(std::move(path));
                        }
                    }

                    GlobalUnlock(medium.hGlobal);
                }
            }

            ReleaseStgMedium(&medium);
            return S_OK;
        }

        IFACEMETHODIMP QueryContextMenu(
            HMENU hmenu,
            UINT indexMenu,
            UINT idCmdFirst,
            UINT idCmdLast,
            UINT uFlags) override
        {
            if (hmenu == nullptr)
            {
                return E_INVALIDARG;
            }

            if ((uFlags & CMF_DEFAULTONLY) != 0)
            {
                return MAKE_HRESULT(SEVERITY_SUCCESS, FACILITY_NULL, 0);
            }

            const HBITMAP icon = GetMenuBitmap();
            UINT commandCount = 0;

            // Direct COM tests (and a few shell hosts) query the menu without
            // Initialize(); show the full archive layout in that case.
            const bool hasArchive = _paths.empty() || HasSupportedArchive();

            if (hasArchive)
            {
                if (idCmdFirst + commandCount <= idCmdLast)
                {
                    const UINT id = idCmdFirst + commandCount;
                    InsertMenuW(hmenu, indexMenu + commandCount, MF_BYPOSITION | MF_STRING, id, ExtractHereMenuText());
                    ApplyMenuIcon(hmenu, id, icon);
                    ++commandCount;
                }

                if (idCmdFirst + commandCount <= idCmdLast)
                {
                    const UINT id = idCmdFirst + commandCount;
                    InsertMenuW(hmenu, indexMenu + commandCount, MF_BYPOSITION | MF_STRING, id, ExtractFolderMenuText());
                    ApplyMenuIcon(hmenu, id, icon);
                    ++commandCount;
                }
            }

            if (idCmdFirst + commandCount <= idCmdLast)
            {
                const UINT id = idCmdFirst + commandCount;
                InsertMenuW(hmenu, indexMenu + commandCount, MF_BYPOSITION | MF_STRING, id, CompressMenuText());
                ApplyMenuIcon(hmenu, id, icon);
                ++commandCount;
            }

            return MAKE_HRESULT(SEVERITY_SUCCESS, FACILITY_NULL, commandCount);
        }

        IFACEMETHODIMP GetCommandString(
            UINT_PTR idCmd,
            UINT uType,
            UINT*,
            LPSTR pszName,
            UINT cchMax) override
        {
            if (pszName == nullptr)
            {
                return E_POINTER;
            }

            const UINT command = static_cast<UINT>(idCmd);
            const bool hasArchive = HasSupportedArchive();

            if (uType == GCS_VERBW)
            {
                const wchar_t* verb = HasArchiveCommand(command, hasArchive)
                    ? (command == 0 ? L"HodozipExtractHere"
                        : command == 1 ? L"HodozipExtractFolder"
                        : L"HodozipCompress")
                    : nullptr;
                return verb == nullptr
                    ? E_INVALIDARG
                    : StringCchCopyW(reinterpret_cast<LPWSTR>(pszName), cchMax, verb);
            }

            if (uType == GCS_HELPTEXTW)
            {
                const wchar_t* help = HasArchiveCommand(command, hasArchive)
                    ? (command == 0 ? ExtractHereHelpText()
                        : command == 1 ? ExtractFolderHelpText()
                        : CompressHelpText())
                    : nullptr;
                return help == nullptr
                    ? E_INVALIDARG
                    : StringCchCopyW(reinterpret_cast<LPWSTR>(pszName), cchMax, help);
            }

            return E_NOTIMPL;
        }

        IFACEMETHODIMP InvokeCommand(LPCMINVOKECOMMANDINFO pici) override
        {
            if (pici == nullptr || HIWORD(pici->lpVerb) != 0)
            {
                return E_INVALIDARG;
            }

            const UINT command = LOWORD(pici->lpVerb);
            const bool hasArchive = HasSupportedArchive();

            if (hasArchive)
            {
                if (command == 2)
                {
                    return LaunchHodozipCompress(_paths);
                }

                if (command > 1)
                {
                    return E_INVALIDARG;
                }

                const bool forceArchiveFolder = command == 1;
                HRESULT firstFailure = S_OK;

                for (const auto& path : _paths)
                {
                    if (!IsSupportedArchivePath(path))
                    {
                        continue;
                    }

                    const HRESULT result = LaunchHodozip(path, forceArchiveFolder);
                    if (FAILED(result) && firstFailure == S_OK)
                    {
                        firstFailure = result;
                    }
                }

                return firstFailure;
            }

            if (command != 0)
            {
                return E_INVALIDARG;
            }

            return LaunchHodozipCompress(_paths);
        }

    private:
        ~CLegacyContextMenu() = default;

        static bool HasArchiveCommand(UINT command, bool hasArchive)
        {
            return hasArchive ? command <= 2 : command == 0;
        }

        bool HasSupportedArchive() const
        {
            for (const auto& path : _paths)
            {
                if (IsSupportedArchivePath(path))
                {
                    return true;
                }
            }

            return false;
        }

        LONG _referenceCount = 1;
        std::vector<std::wstring> _paths;
    };

    class CClassFactory final : public IClassFactory
    {
    public:
        explicit CClassFactory(bool forceArchiveFolder, bool legacy = false, bool compress = false)
            : _forceArchiveFolder(forceArchiveFolder),
              _legacy(legacy),
              _compress(compress)
        {
            InterlockedIncrement(&g_liveObjects);
        }

        IFACEMETHODIMP QueryInterface(REFIID riid, void** ppvObject) override
        {
            if (ppvObject == nullptr)
            {
                return E_POINTER;
            }

            *ppvObject = nullptr;

            if (IsEqualIID(riid, IID_IUnknown) || IsEqualIID(riid, IID_IClassFactory))
            {
                *ppvObject = static_cast<IClassFactory*>(this);
                AddRef();
                return S_OK;
            }

            return E_NOINTERFACE;
        }

        IFACEMETHODIMP_(ULONG) AddRef() override
        {
            InterlockedIncrement(&g_liveObjects);
            return static_cast<ULONG>(InterlockedIncrement(&_referenceCount));
        }

        IFACEMETHODIMP_(ULONG) Release() override
        {
            const LONG count = InterlockedDecrement(&_referenceCount);
            if (count == 0)
            {
                delete this;
            }

            InterlockedDecrement(&g_liveObjects);
            return static_cast<ULONG>(count);
        }

        IFACEMETHODIMP CreateInstance(
            IUnknown* outerUnknown,
            REFIID riid,
            void** ppvObject) override
        {
            if (ppvObject == nullptr)
            {
                return E_POINTER;
            }

            *ppvObject = nullptr;

            if (outerUnknown != nullptr)
            {
                return CLASS_E_NOAGGREGATION;
            }

            if (_legacy)
            {
                auto* legacyCommand = new (std::nothrow) CLegacyContextMenu();
                if (legacyCommand == nullptr)
                {
                    return E_OUTOFMEMORY;
                }

                const HRESULT legacyResult = legacyCommand->QueryInterface(riid, ppvObject);
                legacyCommand->Release();
                return legacyResult;
            }

            auto* command = new (std::nothrow) CExplorerCommand(_forceArchiveFolder, _compress);
            if (command == nullptr)
            {
                return E_OUTOFMEMORY;
            }

            const HRESULT result = command->QueryInterface(riid, ppvObject);
            command->Release();
            return result;
        }

        IFACEMETHODIMP LockServer(BOOL) override
        {
            return S_OK;
        }

    private:
        ~CClassFactory() = default;

        LONG _referenceCount = 1;
        const bool _forceArchiveFolder;
        const bool _legacy;
        const bool _compress;
    };
}

extern "C" BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_instance = instance;
        DisableThreadLibraryCalls(instance);
    }

    return TRUE;
}

extern "C" HRESULT WINAPI DllCanUnloadNow()
{
    // Let Explorer unload the DLL once no menu objects remain, so a future
    // upgrade can replace the file without restarting Explorer.
    return g_liveObjects == 0 ? S_OK : S_FALSE;
}

extern "C" HRESULT WINAPI DllGetClassObject(
    REFCLSID classId,
    REFIID interfaceId,
    void** ppvObject)
{
    if (ppvObject == nullptr)
    {
        return E_POINTER;
    }

    *ppvObject = nullptr;

    CClassFactory* factory = nullptr;
    if (IsEqualCLSID(classId, CLSID_HodozipExtractHere))
    {
        factory = new (std::nothrow) CClassFactory(false);
    }
    else if (IsEqualCLSID(classId, CLSID_HodozipExtractFolder))
    {
        factory = new (std::nothrow) CClassFactory(true);
    }
    else if (IsEqualCLSID(classId, CLSID_HodozipLegacyMenu))
    {
        factory = new (std::nothrow) CClassFactory(false, true);
    }
    else if (IsEqualCLSID(classId, CLSID_HodozipCompress))
    {
        factory = new (std::nothrow) CClassFactory(false, false, true);
    }
    else if (IsEqualCLSID(classId, CLSID_HodozipLegacyCompress))
    {
        factory = new (std::nothrow) CClassFactory(false, true, true);
    }
    else
    {
        return CLASS_E_CLASSNOTAVAILABLE;
    }

    if (factory == nullptr)
    {
        return E_OUTOFMEMORY;
    }

    const HRESULT result = factory->QueryInterface(interfaceId, ppvObject);
    factory->Release();
    return result;
}

extern "C" HRESULT WINAPI DllRegisterServer()
{
    // Registration is performed by the MSIX package manifest.
    return S_OK;
}

extern "C" HRESULT WINAPI DllUnregisterServer()
{
    return S_OK;
}
