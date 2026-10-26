#include "Core.h"
#include <uiautomation.h>
#include <shobjidl.h>
#include <propkey.h>
#include <dwmapi.h>
#include <appmodel.h>
#include <map>
#include <memory>

namespace ms {
template<class T> class ComPtr {
    T* ptr{};
public:
    ~ComPtr() { if (ptr) ptr->Release(); }
    T* operator->() const { return ptr; }
    T* Get() const { return ptr; }
    T** Out() { return &ptr; }
};
struct BString {
    BSTR value{};
    ~BString() { SysFreeString(value); }
    std::wstring String() const { return value ? std::wstring(value, SysStringLen(value)) : L""; }
};
struct Handle {
    HANDLE value{};
    ~Handle() { if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value); }
};

std::wstring ClassName(HWND hwnd) {
    wchar_t name[256]{}; GetClassNameW(hwnd, name, 256); return name;
}
HWND TaskbarAt(POINT point) {
    HWND root = GetAncestor(WindowFromPoint(point), GA_ROOT);
    auto name = ClassName(root);
    return name == L"Shell_TrayWnd" || name == L"Shell_SecondaryTrayWnd" ? root : nullptr;
}
std::vector<HWND> Taskbars() {
    std::vector<HWND> result;
    EnumWindows([](HWND hwnd, LPARAM data) -> BOOL {
        auto name = ClassName(hwnd);
        if (name == L"Shell_TrayWnd" || name == L"Shell_SecondaryTrayWnd")
            reinterpret_cast<std::vector<HWND>*>(data)->push_back(hwnd);
        return TRUE;
    }, reinterpret_cast<LPARAM>(&result));
    return result;
}

struct Automation::Impl {
    HRESULT apartment = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    ComPtr<IUIAutomation> client;
    ComPtr<IUIAutomationCondition> condition;
    ComPtr<IUIAutomationCacheRequest> cache;
    bool ready{};
    Impl() {
        if (FAILED(apartment)) return;
        if (FAILED(CoCreateInstance(CLSID_CUIAutomation, nullptr, CLSCTX_INPROC_SERVER,
            IID_IUIAutomation, reinterpret_cast<void**>(client.Out())))) return;
        VARIANT value{}; value.vt = VT_I4; value.lVal = UIA_ButtonControlTypeId;
        if (FAILED(client->CreatePropertyCondition(UIA_ControlTypePropertyId, value, condition.Out()))) return;
        if (FAILED(client->CreateCacheRequest(cache.Out()))) return;
        if (FAILED(cache->put_TreeScope(TreeScope_Element)) ||
            FAILED(cache->put_AutomationElementMode(AutomationElementMode_None)) ||
            FAILED(cache->AddProperty(UIA_AutomationIdPropertyId)) ||
            FAILED(cache->AddProperty(UIA_NamePropertyId)) ||
            FAILED(cache->AddProperty(UIA_BoundingRectanglePropertyId))) return;
        ready = true;
    }
};
Automation::Automation() : impl(new Impl) {}
Automation::~Automation() {
    bool uninitialize = SUCCEEDED(impl->apartment);
    delete impl; // Release interfaces before ending the apartment.
    if (uninitialize) CoUninitialize();
}
bool Automation::Ready() const { return impl->ready; }
bool Automation::Buttons(HWND taskbar, std::vector<Button>& result) {
    if (!Ready() || !IsWindow(taskbar)) return false;
    ComPtr<IUIAutomationElement> root;
    ComPtr<IUIAutomationElementArray> found;
    if (FAILED(impl->client->ElementFromHandle(taskbar, root.Out())) ||
        FAILED(root->FindAllBuildCache(TreeScope_Descendants, impl->condition.Get(), impl->cache.Get(), found.Out()))) return false;
    int count{};
    if (FAILED(found->get_Length(&count))) return false;
    for (int i = 0; i < count; ++i) {
        ComPtr<IUIAutomationElement> element;
        if (FAILED(found->GetElement(i, element.Out()))) continue;
        BString id, name;
        RECT bounds{};
        if (FAILED(element->get_CachedAutomationId(&id.value))) continue;
        auto appId = id.String();
        if (appId.size() <= 6 || !Equal(appId.substr(0, 6), L"Appid:")) continue;
        if (FAILED(element->get_CachedBoundingRectangle(&bounds)) || IsRectEmpty(&bounds)) continue;
        if (FAILED(element->get_CachedName(&name.value))) continue;
        appId.erase(0, 6);
        auto first = appId.find_first_not_of(L" \t\r\n");
        if (first == std::wstring::npos) continue;
        appId = appId.substr(first, appId.find_last_not_of(L" \t\r\n") - first + 1);
        auto label = name.String();
        auto separator = label.rfind(L" - ");
        if (separator != std::wstring::npos) label.resize(separator);
        result.push_back({std::move(appId), std::move(label), bounds, taskbar});
    }
    return true;
}

bool SameLiveButton(const Button& expected, const Button& actual, POINT point) {
    return !actual.appId.empty() && Contains(actual.bounds, point) &&
        expected.taskbar == actual.taskbar && Equal(expected.appId, actual.appId);
}
std::vector<Window> Match(const Button& button, const std::vector<Window>& windows) {
    std::vector<Window> result;
    if (!button.appId.empty()) {
        for (const auto& w : windows)
            if (std::any_of(w.appIds.begin(), w.appIds.end(), [&](const auto& id) { return Equal(id, button.appId); })) result.push_back(w);
        if (!result.empty()) return result;
    }
    for (const auto& w : windows) {
        bool legacy = (button.appId.empty() || w.appIds.empty()) && !button.label.empty() &&
            (Equal(w.title, button.label) || Equal(w.process, button.label) || Equal(w.product, button.label));
        if (legacy || (Equal(button.appId, L"Microsoft.Windows.Explorer") && w.className == L"CabinetWClass")) result.push_back(w);
    }
    return result;
}

static void AddAppIds(Window& window, HWND hwnd) {
    ComPtr<IPropertyStore> store;
    if (SUCCEEDED(SHGetPropertyStoreForWindow(hwnd, IID_IPropertyStore, reinterpret_cast<void**>(store.Out())))) {
        PROPVARIANT value{};
        if (SUCCEEDED(store->GetValue(PKEY_AppUserModel_ID, &value)) && value.vt == VT_LPWSTR && value.pwszVal)
            window.appIds.emplace_back(value.pwszVal);
        PropVariantClear(&value);
    }
    DWORD pid{}; GetWindowThreadProcessId(hwnd, &pid);
    Handle process{OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid)};
    if (!process.value) return;
    UINT32 length = 512; wchar_t appId[512]{};
    if (GetApplicationUserModelId(process.value, &length, appId) == ERROR_SUCCESS)
        window.appIds.emplace_back(appId);
}
struct ProcessInfo { std::wstring name, product; };
static ProcessInfo ReadProcess(DWORD pid) {
    ProcessInfo result;
    Handle process{OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid)};
    if (!process.value) return result;
    wchar_t path[32768]{}; DWORD length = 32768;
    if (!QueryFullProcessImageNameW(process.value, 0, path, &length)) return result;
    std::wstring file(path, length);
    auto name = file.substr(file.find_last_of(L"\\/") + 1);
    auto dot = name.rfind(L'.');
    result.name = name.substr(0, dot);
    DWORD unused{}; DWORD size = GetFileVersionInfoSizeW(path, &unused);
    if (!size) return result;
    std::vector<BYTE> bytes(size);
    if (!GetFileVersionInfoW(path, 0, size, bytes.data())) return result;
    struct Translation { WORD language, codepage; };
    Translation* translations{}; UINT translationsSize{};
    if (!VerQueryValueW(bytes.data(), L"\\VarFileInfo\\Translation", reinterpret_cast<void**>(&translations), &translationsSize)) return result;
    for (UINT i = 0; i < translationsSize / sizeof(Translation); ++i) {
        wchar_t query[80]{};
        swprintf(query, 80, L"\\StringFileInfo\\%04x%04x\\ProductName", translations[i].language, translations[i].codepage);
        wchar_t* product{}; UINT productSize{};
        if (VerQueryValueW(bytes.data(), query, reinterpret_cast<void**>(&product), &productSize) && productSize && product) {
            result.product = product; break;
        }
    }
    return result;
}
std::vector<Window> Windows() {
    struct Context { std::vector<Window> result; std::map<DWORD, ProcessInfo> processes; } context;
    EnumWindows([](HWND hwnd, LPARAM data) -> BOOL {
        if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GW_OWNER) || (GetWindowLongPtrW(hwnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW)) return TRUE;
        DWORD cloaked{};
        if (SUCCEEDED(DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, &cloaked, sizeof(cloaked))) && cloaked) return TRUE;
        wchar_t title[1024]{};
        if (!GetWindowTextW(hwnd, title, 1024)) return TRUE;
        auto cls = ClassName(hwnd);
        if (cls == L"Progman" || cls == L"WorkerW" || cls.compare(0, 6, L"Shell_") == 0) return TRUE;
        auto& ctx = *reinterpret_cast<Context*>(data);
        Window window; window.handle = hwnd; window.title = title; window.className = cls;
        GetWindowThreadProcessId(hwnd, &window.pid);
        auto found = ctx.processes.find(window.pid);
        if (found == ctx.processes.end()) found = ctx.processes.emplace(window.pid, ReadProcess(window.pid)).first;
        window.process = found->second.name; window.product = found->second.product;
        AddAppIds(window, hwnd);
        if (cls == L"ApplicationFrameWindow") EnumChildWindows(hwnd, [](HWND child, LPARAM p) -> BOOL {
            AddAppIds(*reinterpret_cast<Window*>(p), child); return TRUE;
        }, reinterpret_cast<LPARAM>(&window));
        ctx.result.push_back(std::move(window));
        return TRUE;
    }, reinterpret_cast<LPARAM>(&context));
    return context.result;
}
std::vector<Display> Displays() {
    std::vector<Display> result;
    EnumDisplayMonitors(nullptr, nullptr, [](HMONITOR monitor, HDC, LPRECT, LPARAM data) -> BOOL {
        Display display; display.handle = monitor; display.info.cbSize = sizeof(display.info);
        if (GetMonitorInfoW(monitor, &display.info)) reinterpret_cast<std::vector<Display>*>(data)->push_back(display);
        return TRUE;
    }, reinterpret_cast<LPARAM>(&result));
    return result;
}
bool MoveWindowTo(HWND hwnd, DWORD pid, const std::wstring& device) {
    DWORD actual{}; GetWindowThreadProcessId(hwnd, &actual);
    if (!IsWindow(hwnd) || actual != pid) return false;
    auto displays = Displays();
    auto target = std::find_if(displays.begin(), displays.end(), [&](const auto& d) { return Equal(device, d.info.szDevice); });
    if (target == displays.end()) return false;
    if (MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST) == target->handle) {
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        return true;
    }
    bool maximized = IsZoomed(hwnd);
    if (maximized || IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
    RECT rect{}; if (!GetWindowRect(hwnd, &rect)) return false;
    MONITORINFO source{sizeof(source)};
    if (!GetMonitorInfoW(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), &source)) return false;
    RECT dest = target->info.rcWork;
    LONG width = rect.right - rect.left, height = rect.bottom - rect.top;
    LONG x = std::clamp(dest.left + rect.left - source.rcWork.left, dest.left, dest.right - std::min(width, dest.right - dest.left));
    LONG y = std::clamp(dest.top + rect.top - source.rcWork.top, dest.top, dest.bottom - std::min(height, dest.bottom - dest.top));
    bool moved = SetWindowPos(hwnd, nullptr, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    if (maximized) ShowWindow(hwnd, SW_MAXIMIZE);
    return moved && MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST) == target->handle;
}

MouseAction Gesture::Handle(UINT message, const Button* hit, POINT point, bool shift, bool leftDown) {
    if (message == WM_RBUTTONDOWN) {
        if (!hit || leftDown) return MouseAction::Forward;
        rightUp = true;
        if (shift) { waiting = false; selected = *hit; return MouseAction::OpenMenu; }
        candidate = *hit; waiting = true; return MouseAction::Consume;
    }
    if (message == WM_LBUTTONDOWN && waiting) {
        waiting = false;
        if (!hit || !Contains(candidate.bounds, point) || !SameLiveButton(candidate, *hit, point)) return MouseAction::Forward;
        leftUp = true; selected = candidate; return MouseAction::OpenMenu;
    }
    if (message == WM_RBUTTONUP && rightUp) {
        rightUp = false; bool wasWaiting = waiting; waiting = false;
        return wasWaiting && hit && Contains(candidate.bounds, point) && SameLiveButton(candidate, *hit, point) ? MouseAction::Replay : MouseAction::Consume;
    }
    if (message == WM_LBUTTONUP && leftUp) { leftUp = false; return MouseAction::Consume; }
    return MouseAction::Forward;
}
bool IsTaskbarPreview(const std::wstring& name, DWORD pid, DWORD thread, DWORD ownerPid, DWORD ownerThread) {
    return (name == L"TaskListThumbnailWnd" || name == L"XamlExplorerHostIslandWindow") &&
        pid && thread && pid == ownerPid && thread == ownerThread;
}
void PreviewSuppressor::Begin() {
    owners.clear();
    for (HWND bar : Taskbars()) {
        Owner owner{}; owner.thread = GetWindowThreadProcessId(bar, &owner.pid);
        if (!owner.thread) continue;
        owners.push_back(owner);
        PostMessageW(bar, WM_MOUSELEAVE, 0, 0);
        EnumChildWindows(bar, [](HWND child, LPARAM data) -> BOOL {
            auto owner = reinterpret_cast<Owner*>(data); DWORD pid{};
            if (GetWindowThreadProcessId(child, &pid) == owner->thread && pid == owner->pid) PostMessageW(child, WM_MOUSELEAVE, 0, 0);
            return TRUE;
        }, reinterpret_cast<LPARAM>(&owner));
    }
    Dismiss();
}
void PreviewSuppressor::Dismiss() {
    for (const wchar_t* name : {L"TaskListThumbnailWnd", L"XamlExplorerHostIslandWindow"}) {
        HWND hwnd{};
        while ((hwnd = FindWindowExW(nullptr, hwnd, name, nullptr))) {
            if (!IsWindowVisible(hwnd)) continue;
            DWORD pid{}; DWORD thread = GetWindowThreadProcessId(hwnd, &pid);
            for (const auto& owner : owners)
                if (IsTaskbarPreview(name, pid, thread, owner.pid, owner.thread)) { ShowWindowAsync(hwnd, SW_HIDE); break; }
        }
    }
}
}
