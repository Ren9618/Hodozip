// Hodozip context-menu COM server.
//
// This DLL implements the shell's IExplorerCommand interface (Windows 11 new
// menu) and the legacy IContextMenu interface (unpackaged HKCU registration).
// It does not contain extraction logic. Invoke() launches Hodozip.exe with
// either --extract-here or --extract-folder and the selected file path.

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

    // Extract here: {4F4C6D2A-71B9-4E4E-9A7D-8D9A0B5E6C01}
    const CLSID CLSID_HodozipExtractHere =
        { 0x4f4c6d2a, 0x71b9, 0x4e4e, { 0x9a, 0x7d, 0x8d, 0x9a, 0x0b, 0x5e, 0x6c, 0x01 } };

    // Extract to folder: {4F4C6D2A-71B9-4E4E-9A7D-8D9A0B5E6C02}
    const CLSID CLSID_HodozipExtractFolder =
        { 0x4f4c6d2a, 0x71b9, 0x4e4e, { 0x9a, 0x7d, 0x8d, 0x9a, 0x0b, 0x5e, 0x6c, 0x02 } };

    // Legacy IContextMenu handler: {4F4C6D2A-71B9-4E4E-9A7D-8D9A0B5E6C03}
    const CLSID CLSID_HodozipLegacyMenu =
        { 0x4f4c6d2a, 0x71b9, 0x4e4e, { 0x9a, 0x7d, 0x8d, 0x9a, 0x0b, 0x5e, 0x6c, 0x03 } };

    bool IsSupportedArchivePath(const std::wstring& path)
    {
        const wchar_t* extension = PathFindExtensionW(path.c_str());
        static const wchar_t* supportedExtensions[] = {
            L".zip",
            L".7z",
            L".rar",
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

    HRESULT LaunchHodozip(const std::wstring& archivePath, bool forceArchiveFolder)
    {
        const auto executablePath = GetHodozipExecutablePath();
        if (executablePath.empty())
        {
            return HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND);
        }

        std::wstring arguments = forceArchiveFolder
            ? L"--extract-folder \""
            : L"--extract-here \"";
        arguments += archivePath;
        arguments += L'"';

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

    class CExplorerCommand final : public IExplorerCommand
    {
    public:
        explicit CExplorerCommand(bool forceArchiveFolder)
            : _forceArchiveFolder(forceArchiveFolder)
        {
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
            return static_cast<ULONG>(InterlockedIncrement(&_referenceCount));
        }

        IFACEMETHODIMP_(ULONG) Release() override
        {
            const LONG count = InterlockedDecrement(&_referenceCount);
            if (count == 0)
            {
                delete this;
            }

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
                _forceArchiveFolder ? L"Hodozip でフォルダに解凍" : L"Hodozip で解凍",
                ppszName);
        }

        IFACEMETHODIMP GetIcon(IShellItemArray*, LPWSTR* ppszIcon) override
        {
            if (ppszIcon != nullptr)
            {
                *ppszIcon = nullptr;
            }

            return E_NOTIMPL;
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

            *pguidCommandName = _forceArchiveFolder
                ? CLSID_HodozipExtractFolder
                : CLSID_HodozipExtractHere;
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

        LONG _referenceCount = 1;
        const bool _forceArchiveFolder;
    };

    class CLegacyContextMenu final : public IShellExtInit, public IContextMenu
    {
    public:
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
            return static_cast<ULONG>(InterlockedIncrement(&_referenceCount));
        }

        IFACEMETHODIMP_(ULONG) Release() override
        {
            const LONG count = InterlockedDecrement(&_referenceCount);
            if (count == 0)
            {
                delete this;
            }

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

            UINT commandCount = 0;
            if (idCmdFirst <= idCmdLast)
            {
                InsertMenuW(
                    hmenu,
                    indexMenu,
                    MF_BYPOSITION | MF_STRING,
                    idCmdFirst,
                    L"Hodozip で解凍");
                ++commandCount;
            }

            if (idCmdFirst + 1 <= idCmdLast)
            {
                InsertMenuW(
                    hmenu,
                    indexMenu + 1,
                    MF_BYPOSITION | MF_STRING,
                    idCmdFirst + 1,
                    L"Hodozip でフォルダに解凍");
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

            const bool forceArchiveFolder = idCmd == 1;

            switch (uType)
            {
                case GCS_VERBW:
                    return StringCchCopyW(
                        reinterpret_cast<LPWSTR>(pszName),
                        cchMax,
                        forceArchiveFolder ? L"HodozipExtractFolder" : L"HodozipExtractHere");

                case GCS_HELPTEXTW:
                    return StringCchCopyW(
                        reinterpret_cast<LPWSTR>(pszName),
                        cchMax,
                        forceArchiveFolder ? L"アーカイブ名のフォルダに解凍します" : L"アーカイブと同じ場所に解凍します");

                default:
                    return E_NOTIMPL;
            }
        }

        IFACEMETHODIMP InvokeCommand(LPCMINVOKECOMMANDINFO pici) override
        {
            if (pici == nullptr)
            {
                return E_INVALIDARG;
            }

            if (HIWORD(pici->lpVerb) != 0)
            {
                return E_INVALIDARG;
            }

            const UINT command = LOWORD(pici->lpVerb);
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

    private:
        ~CLegacyContextMenu() = default;

        LONG _referenceCount = 1;
        std::vector<std::wstring> _paths;
    };

    class CClassFactory final : public IClassFactory
    {
    public:
        explicit CClassFactory(bool forceArchiveFolder, bool legacy = false)
            : _forceArchiveFolder(forceArchiveFolder),
              _legacy(legacy)
        {
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
            return static_cast<ULONG>(InterlockedIncrement(&_referenceCount));
        }

        IFACEMETHODIMP_(ULONG) Release() override
        {
            const LONG count = InterlockedDecrement(&_referenceCount);
            if (count == 0)
            {
                delete this;
            }

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

            auto* command = new (std::nothrow) CExplorerCommand(_forceArchiveFolder);
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
    // The surrogate server keeps this small DLL loaded while Explorer runs.
    return S_FALSE;
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
