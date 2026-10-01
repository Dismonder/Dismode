#include <windows.h>
#include <shlobj_core.h>
#include <shlwapi.h>
#include <shellapi.h>

#include <atomic>
#include <new>
#include <string>
#include <utility>
#include <vector>

namespace
{
constexpr CLSID LaunchThroughDismodeClsid = {
    0x1dd2a4b8,
    0x88b4,
    0x4f02,
    {0x9e, 0x22, 0x4a, 0x7a, 0x3a, 0x54, 0x1c, 0x2f}};

constexpr wchar_t CommandTitle[] = L"Uruchom przez Dismode";
constexpr wchar_t CommandTooltip[] =
    L"Uruchamia grę przez bezpieczną sesję optymalizacji Dismode";
constexpr wchar_t LauncherFileName[] = L"Dismode.exe";

HINSTANCE moduleInstance = nullptr;
std::atomic<long> liveObjectCount = 0;

std::wstring GetModulePath()
{
    std::vector<wchar_t> buffer(512);
    for (;;)
    {
        DWORD length = GetModuleFileNameW(
            moduleInstance,
            buffer.data(),
            static_cast<DWORD>(buffer.size()));
        if (length == 0)
        {
            return {};
        }

        if (length < buffer.size() - 1)
        {
            return std::wstring(buffer.data(), length);
        }

        if (buffer.size() >= 32768)
        {
            return {};
        }

        buffer.resize(buffer.size() * 2);
    }
}

std::wstring GetInstallationDirectory()
{
    std::wstring modulePath = GetModulePath();
    const std::wstring::size_type separator = modulePath.find_last_of(L"\\/");
    if (separator == std::wstring::npos)
    {
        return {};
    }

    modulePath.resize(separator);
    return modulePath;
}

bool TryGetSingleExecutable(
    IShellItemArray* items,
    std::wstring& executablePath) noexcept
{
    if (items == nullptr)
    {
        return false;
    }

    DWORD count = 0;
    if (FAILED(items->GetCount(&count)) || count != 1)
    {
        return false;
    }

    IShellItem* item = nullptr;
    if (FAILED(items->GetItemAt(0, &item)) || item == nullptr)
    {
        return false;
    }

    PWSTR rawPath = nullptr;
    const HRESULT displayNameResult =
        item->GetDisplayName(SIGDN_FILESYSPATH, &rawPath);
    item->Release();
    if (FAILED(displayNameResult) || rawPath == nullptr)
    {
        return false;
    }

    std::wstring candidate(rawPath);
    CoTaskMemFree(rawPath);

    const wchar_t* extension = PathFindExtensionW(candidate.c_str());
    if (extension == nullptr || _wcsicmp(extension, L".exe") != 0)
    {
        return false;
    }

    const DWORD attributes = GetFileAttributesW(candidate.c_str());
    if (attributes == INVALID_FILE_ATTRIBUTES ||
        (attributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
    {
        return false;
    }

    executablePath = std::move(candidate);
    return true;
}

HRESULT LaunchThroughDismode(const std::wstring& executablePath) noexcept
{
    if (executablePath.find(L'"') != std::wstring::npos)
    {
        return E_INVALIDARG;
    }

    const std::wstring installationDirectory = GetInstallationDirectory();
    if (installationDirectory.empty())
    {
        return HRESULT_FROM_WIN32(ERROR_PATH_NOT_FOUND);
    }

    const std::wstring launcherPath =
        installationDirectory + L"\\" + LauncherFileName;
    const DWORD launcherAttributes = GetFileAttributesW(launcherPath.c_str());
    if (launcherAttributes == INVALID_FILE_ATTRIBUTES ||
        (launcherAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
    {
        return HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND);
    }

    const std::wstring parameters =
        L"--launch-through-dismode \"" + executablePath +
        L"\" --background";

    SHELLEXECUTEINFOW executeInfo{};
    executeInfo.cbSize = sizeof(executeInfo);
    executeInfo.fMask = SEE_MASK_FLAG_NO_UI | SEE_MASK_NOASYNC;
    executeInfo.lpVerb = L"open";
    executeInfo.lpFile = launcherPath.c_str();
    executeInfo.lpParameters = parameters.c_str();
    executeInfo.lpDirectory = installationDirectory.c_str();
    executeInfo.nShow = SW_SHOWNORMAL;

    if (!ShellExecuteExW(&executeInfo))
    {
        return HRESULT_FROM_WIN32(GetLastError());
    }

    return S_OK;
}

class LaunchThroughDismodeCommand final : public IExplorerCommand
{
public:
    LaunchThroughDismodeCommand() noexcept
    {
        ++liveObjectCount;
    }

    LaunchThroughDismodeCommand(const LaunchThroughDismodeCommand&) = delete;
    LaunchThroughDismodeCommand& operator=(
        const LaunchThroughDismodeCommand&) = delete;

    IFACEMETHODIMP QueryInterface(REFIID interfaceId, void** object) override
    {
        if (object == nullptr)
        {
            return E_POINTER;
        }

        *object = nullptr;
        if (IsEqualIID(interfaceId, IID_IUnknown) ||
            IsEqualIID(interfaceId, IID_IExplorerCommand))
        {
            *object = static_cast<IExplorerCommand*>(this);
            AddRef();
            return S_OK;
        }

        return E_NOINTERFACE;
    }

    IFACEMETHODIMP_(ULONG) AddRef() override
    {
        return static_cast<ULONG>(++referenceCount_);
    }

    IFACEMETHODIMP_(ULONG) Release() override
    {
        const ULONG remaining = static_cast<ULONG>(--referenceCount_);
        if (remaining == 0)
        {
            delete this;
        }

        return remaining;
    }

    IFACEMETHODIMP GetTitle(
        IShellItemArray*,
        PWSTR* title) override
    {
        return SHStrDupW(CommandTitle, title);
    }

    IFACEMETHODIMP GetIcon(
        IShellItemArray*,
        PWSTR* icon) override
    {
        if (icon == nullptr)
        {
            return E_POINTER;
        }

        const std::wstring modulePath = GetModulePath();
        if (modulePath.empty())
        {
            *icon = nullptr;
            return HRESULT_FROM_WIN32(ERROR_PATH_NOT_FOUND);
        }

        const std::wstring iconLocation = modulePath + L",-101";
        return SHStrDupW(iconLocation.c_str(), icon);
    }

    IFACEMETHODIMP GetToolTip(
        IShellItemArray*,
        PWSTR* tooltip) override
    {
        return SHStrDupW(CommandTooltip, tooltip);
    }

    IFACEMETHODIMP GetCanonicalName(GUID* canonicalName) override
    {
        if (canonicalName == nullptr)
        {
            return E_POINTER;
        }

        *canonicalName = LaunchThroughDismodeClsid;
        return S_OK;
    }

    IFACEMETHODIMP GetState(
        IShellItemArray* items,
        BOOL,
        EXPCMDSTATE* state) override
    {
        if (state == nullptr)
        {
            return E_POINTER;
        }

        std::wstring executablePath;
        *state = TryGetSingleExecutable(items, executablePath)
            ? ECS_ENABLED
            : ECS_HIDDEN;
        return S_OK;
    }

    IFACEMETHODIMP Invoke(
        IShellItemArray* items,
        IBindCtx*) override
    {
        std::wstring executablePath;
        if (!TryGetSingleExecutable(items, executablePath))
        {
            return E_INVALIDARG;
        }

        return LaunchThroughDismode(executablePath);
    }

    IFACEMETHODIMP GetFlags(EXPCMDFLAGS* flags) override
    {
        if (flags == nullptr)
        {
            return E_POINTER;
        }

        *flags = ECF_DEFAULT;
        return S_OK;
    }

    IFACEMETHODIMP EnumSubCommands(
        IEnumExplorerCommand** commands) override
    {
        if (commands == nullptr)
        {
            return E_POINTER;
        }

        *commands = nullptr;
        return E_NOTIMPL;
    }

private:
    ~LaunchThroughDismodeCommand()
    {
        --liveObjectCount;
    }

    std::atomic<long> referenceCount_ = 1;
};

class CommandClassFactory final : public IClassFactory
{
public:
    CommandClassFactory() noexcept
    {
        ++liveObjectCount;
    }

    CommandClassFactory(const CommandClassFactory&) = delete;
    CommandClassFactory& operator=(const CommandClassFactory&) = delete;

    IFACEMETHODIMP QueryInterface(REFIID interfaceId, void** object) override
    {
        if (object == nullptr)
        {
            return E_POINTER;
        }

        *object = nullptr;
        if (IsEqualIID(interfaceId, IID_IUnknown) ||
            IsEqualIID(interfaceId, IID_IClassFactory))
        {
            *object = static_cast<IClassFactory*>(this);
            AddRef();
            return S_OK;
        }

        return E_NOINTERFACE;
    }

    IFACEMETHODIMP_(ULONG) AddRef() override
    {
        return static_cast<ULONG>(++referenceCount_);
    }

    IFACEMETHODIMP_(ULONG) Release() override
    {
        const ULONG remaining = static_cast<ULONG>(--referenceCount_);
        if (remaining == 0)
        {
            delete this;
        }

        return remaining;
    }

    IFACEMETHODIMP CreateInstance(
        IUnknown* outer,
        REFIID interfaceId,
        void** object) override
    {
        if (object == nullptr)
        {
            return E_POINTER;
        }

        *object = nullptr;
        if (outer != nullptr)
        {
            return CLASS_E_NOAGGREGATION;
        }

        auto* command = new (std::nothrow) LaunchThroughDismodeCommand();
        if (command == nullptr)
        {
            return E_OUTOFMEMORY;
        }

        const HRESULT result = command->QueryInterface(interfaceId, object);
        command->Release();
        return result;
    }

    IFACEMETHODIMP LockServer(BOOL lock) override
    {
        if (lock)
        {
            ++liveObjectCount;
        }
        else
        {
            --liveObjectCount;
        }

        return S_OK;
    }

private:
    ~CommandClassFactory()
    {
        --liveObjectCount;
    }

    std::atomic<long> referenceCount_ = 1;
};
}

STDAPI DllGetClassObject(
    REFCLSID classId,
    REFIID interfaceId,
    void** object)
{
    if (!IsEqualCLSID(classId, LaunchThroughDismodeClsid))
    {
        return CLASS_E_CLASSNOTAVAILABLE;
    }

    auto* factory = new (std::nothrow) CommandClassFactory();
    if (factory == nullptr)
    {
        return E_OUTOFMEMORY;
    }

    const HRESULT result = factory->QueryInterface(interfaceId, object);
    factory->Release();
    return result;
}

STDAPI DllCanUnloadNow()
{
    return liveObjectCount.load() == 0 ? S_OK : S_FALSE;
}

BOOL APIENTRY DllMain(
    HMODULE module,
    DWORD reason,
    LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        moduleInstance = module;
        DisableThreadLibraryCalls(module);
    }

    return TRUE;
}
