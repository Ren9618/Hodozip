// Hodozip Windows 11 context-menu COM server.
//
// This DLL only implements the shell's IExplorerCommand interface. It does not
// contain extraction logic. Invoke() launches the packaged Hodozip.exe with
// either --extract-here or --extract-folder and the selected file path.

#include <windows.h>
#include <shobjidl.h>
#include <shellapi.h>
#include <shlwapi.h>

#include <new>
#include <string>

#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "shlwapi.lib")

namespace
{
    HINSTANCE g_instance = nullptr;

    // Extract here: {4F4C6D2A-71B9-4E4E-9A7D-8D9A0B5E6C01}
    const CLSID CLSID_HodozipExtractHere =
        { 0x4f4c6d2a, 0x71b9, 0x4e4e, { 0x9a, 0x7d, 0x8d, 0x9a, 0x0b, 0x5e, 0x6c, 0x01 } };

    // Extract to folder: {4F4C6D2A-71B9-4E4E-9A7D-8D9A0B5E6C02}
    const CLSID CLSID_HodozipExtractFolder =
        { 0x4f4c6d2a, 0x71b9, 0x4e4e, { 0x9a, 0x7d, 0x8d, 0x9a, 0x0b, 0x5e, 0x6c, 0x02 } };

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

                const bool isZip = _wcsicmp(PathFindExtensionW(fileSystemPath), L".zip") == 0;
                if (isZip)
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

    class CClassFactory final : public IClassFactory
    {
    public:
        explicit CClassFactory(bool forceArchiveFolder)
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
