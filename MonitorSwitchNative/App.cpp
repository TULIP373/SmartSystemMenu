#include "Core.h"
#include <shellapi.h>
#include <condition_variable>
#include <memory>
#include <mutex>
#include <optional>

namespace {
using namespace ms;
constexpr wchar_t WindowClass[] = L"MonitorSwitch.Native.Owner";
constexpr UINT Prepare = WM_APP + 1, Result = WM_APP + 2, Cancel = WM_APP + 3,
    Replay = WM_APP + 4, Tray = WM_APP + 5;
constexpr UINT_PTR MenuTimer = 1;
constexpr UINT ExitCommand = 9000;
struct Job { ULONGLONG token{}; POINT point{}; std::optional<Button> button; HWND foreground{}; };
struct Resolution { Job job; std::vector<Window> windows; std::wstring error; };
struct Command { Window window; std::wstring device; };

bool WriteUtf8(const std::wstring& file, const std::wstring& text) {
    HANDLE out = CreateFileW(file.c_str(), GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (out == INVALID_HANDLE_VALUE) return false;
    int size = WideCharToMultiByte(CP_UTF8, 0, text.c_str(), -1, nullptr, 0, nullptr, nullptr);
    std::string bytes(size, '\0'); WideCharToMultiByte(CP_UTF8, 0, text.c_str(), -1, bytes.data(), size, nullptr, nullptr);
    DWORD written{}; bool success = WriteFile(out, bytes.data(), static_cast<DWORD>(size - 1), &written, nullptr);
    CloseHandle(out); return success && written == static_cast<DWORD>(size - 1);
}
void Status(const std::wstring& text) {
    wchar_t path[32768]{}; GetModuleFileNameW(nullptr, path, 32768);
    std::wstring file = path; file.resize(file.find_last_of(L"\\/") + 1); file += L"MonitorSwitch.status.txt";
    WriteUtf8(file, text);
}
int Diagnose(const std::wstring& file) {
    Automation automation;
    if (!automation.Ready()) return 1;
    std::vector<Button> buttons;
    for (HWND root : Taskbars()) if (!automation.Buttons(root, buttons)) return 1;
    auto windows = Windows();
    std::wstring report = L"MonitorSwitch Native\r\n";
    for (const auto& button : buttons) {
        auto matches = Match(button, windows);
        report += button.appId + L" | matched=" + std::to_wstring(matches.size()) + L"\r\n";
    }
    return WriteUtf8(file, report) ? 0 : 1;
}

class App {
    HWND owner{};
    HANDLE worker{}, input{}, inputReady{};
    DWORD inputId{};
    HHOOK mouseHook{}, keyboardHook{};
    std::atomic<bool> stopping{false}, menuVisible{false};
    std::atomic<ULONGLONG> lastScan{0};
    std::mutex cacheMutex;
    std::vector<Button> buttons;
    std::mutex jobMutex;
    std::condition_variable wake;
    bool scanQueued{true}; HWND scanBar{};
    std::optional<Job> queued;
    RequestState requests;
    Gesture gesture; // Input-thread only.
    PreviewSuppressor previews; // UI-thread only.
    std::unique_ptr<Resolution> ready;
    NOTIFYICONDATAW tray{};
    UINT taskbarCreated{};
    static App* instance;

    void Scan(HWND bar, Automation& automation) {
        std::vector<Button> fresh;
        if (bar) {
            // A failed read invalidates the old geometry rather than retaining a wrong target.
            automation.Buttons(bar, fresh);
            std::lock_guard lock(cacheMutex);
            buttons.erase(std::remove_if(buttons.begin(), buttons.end(), [&](const auto& b) { return b.taskbar == bar || !IsWindow(b.taskbar); }), buttons.end());
            buttons.insert(buttons.end(), fresh.begin(), fresh.end());
        } else {
            for (HWND root : Taskbars()) automation.Buttons(root, fresh);
            std::lock_guard lock(cacheMutex); buttons.swap(fresh);
        }
    }
    void QueueScan(HWND bar, bool force = false) {
        ULONGLONG now = GetTickCount64();
        if (!force && now - lastScan.load() < 500) return;
        std::unique_lock lock(jobMutex, std::try_to_lock);
        if (!lock || stopping) return;
        if (!scanQueued) { scanQueued = true; scanBar = bar; }
        else if (!bar) scanBar = nullptr;
        lastScan = now;
        wake.notify_one();
    }
    void WorkerLoop() {
        Automation automation;
        if (!automation.Ready()) PostMessageW(owner, WM_APP + 6, 0, 0);
        for (;;) {
            std::optional<Job> job;
            bool scan{}; HWND bar{};
            {
                std::unique_lock lock(jobMutex);
                wake.wait(lock, [&] { return stopping || queued || scanQueued; });
                if (stopping) break;
                // A gesture takes priority over background refresh.
                if (queued) { job = std::move(queued); queued.reset(); }
                else { scan = scanQueued; bar = scanBar; scanQueued = false; scanBar = nullptr; }
            }
            if (scan) { Scan(bar, automation); continue; }
            if (!job || !requests.Current(job->token, GetTickCount64())) continue;
            auto response = std::make_unique<Resolution>(); response->job = *job;
            if (job->button) {
                std::vector<Button> live;
                bool read = automation.Buttons(job->button->taskbar, live);
                auto actual = std::find_if(live.begin(), live.end(), [&](const auto& b) { return Contains(b.bounds, job->point); });
                if (!read) response->error = L"无法读取任务栏，请稍后重试。";
                else if (actual == live.end() || !SameLiveButton(*job->button, *actual, job->point))
                    response->error = L"任务栏图标位置已变化，请重新选择。";
                else if (requests.Current(job->token, GetTickCount64())) response->windows = Match(*actual, Windows());
                // This live snapshot also replaces the cached buttons for that taskbar.
                if (read) {
                    std::lock_guard lock(cacheMutex);
                    buttons.erase(std::remove_if(buttons.begin(), buttons.end(), [&](const auto& b) { return b.taskbar == job->button->taskbar; }), buttons.end());
                    buttons.insert(buttons.end(), live.begin(), live.end());
                }
            } else {
                for (auto& window : Windows()) if (window.handle == job->foreground) response->windows.push_back(std::move(window));
            }
            if (stopping || !requests.Current(job->token, GetTickCount64())) continue;
            auto raw = response.release();
            if (!PostMessageW(owner, Result, 0, reinterpret_cast<LPARAM>(raw))) delete raw;
        }
    }
    void StartRequest(POINT point, std::optional<Button> button, HWND foreground) {
        if (stopping) return;
        auto token = requests.Begin(GetTickCount64());
        {
            std::lock_guard lock(jobMutex); queued = Job{token, point, std::move(button), foreground};
        }
        PostMessageW(owner, Prepare, static_cast<WPARAM>(token), 0);
        wake.notify_one();
    }
    void CancelRequest() {
        auto version = requests.Cancel();
        PostMessageW(owner, Cancel, static_cast<WPARAM>(version), 0);
    }
    std::optional<Button> Hit(POINT point) {
        std::unique_lock lock(cacheMutex, std::try_to_lock);
        if (!lock) return {};
        for (const auto& button : buttons) if (Contains(button.bounds, point)) return button;
        return {};
    }
    static LRESULT CALLBACK Mouse(int code, WPARAM message, LPARAM data) {
        auto& app = *instance;
        if (code < 0 || app.stopping) return CallNextHookEx(nullptr, code, message, data);
        auto mouse = reinterpret_cast<MSLLHOOKSTRUCT*>(data);
        if ((mouse->flags & LLMHF_INJECTED) && mouse->dwExtraInfo == ReplayTag) return CallNextHookEx(nullptr, code, message, data);
        if (message == WM_MOUSEMOVE) {
            // No idle timer scans the desktop. Hover refresh is throttled and never calls UIA here.
            static ULONGLONG checked{}; static HWND lastBar{}; static ULONGLONG refreshed{};
            auto now = GetTickCount64();
            if (now - checked >= 100) {
                checked = now;
                HWND bar = TaskbarAt(mouse->pt);
                if (bar && (bar != lastBar || now - refreshed >= 2000)) { app.QueueScan(bar); refreshed = now; }
                lastBar = bar;
            }
            return CallNextHookEx(nullptr, code, message, data);
        }
        if (message != WM_LBUTTONDOWN && message != WM_LBUTTONUP && message != WM_RBUTTONDOWN &&
            message != WM_RBUTTONUP && message != WM_MBUTTONDOWN && message != WM_XBUTTONDOWN)
            return CallNextHookEx(nullptr, code, message, data);
        bool down = message == WM_LBUTTONDOWN || message == WM_RBUTTONDOWN || message == WM_MBUTTONDOWN || message == WM_XBUTTONDOWN;
        if (down && (app.requests.Pending() || app.menuVisible)) {
            HWND hit = WindowFromPoint(mouse->pt);
            if (!app.menuVisible || ClassName(hit) != L"#32768") app.CancelRequest();
        }
        std::optional<Button> hit;
        if (message == WM_RBUTTONDOWN || ((message == WM_LBUTTONDOWN || message == WM_RBUTTONUP) && app.gesture.Waiting())) {
            hit = app.Hit(mouse->pt);
            HWND bar = TaskbarAt(mouse->pt);
            if (hit && hit->taskbar != bar) hit.reset();
            if (bar) app.QueueScan(bar);
        }
        auto action = app.gesture.Handle(static_cast<UINT>(message), hit ? &*hit : nullptr, mouse->pt,
            (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0, (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0);
        if (action == MouseAction::OpenMenu) app.StartRequest(mouse->pt, app.gesture.selected, nullptr);
        if (action == MouseAction::Replay && hit) {
            auto replay = new Button(*hit);
            if (!PostMessageW(app.owner, Replay, 0, reinterpret_cast<LPARAM>(replay))) delete replay;
        }
        return action == MouseAction::Forward ? CallNextHookEx(nullptr, code, message, data) : 1;
    }
    static LRESULT CALLBACK Keyboard(int code, WPARAM message, LPARAM data) {
        auto& app = *instance;
        if (code >= 0 && (message == WM_KEYDOWN || message == WM_SYSKEYDOWN) &&
            reinterpret_cast<KBDLLHOOKSTRUCT*>(data)->vkCode == VK_ESCAPE && (app.requests.Pending() || app.menuVisible)) app.CancelRequest();
        return CallNextHookEx(nullptr, code, message, data);
    }
    void InputLoop() {
        MSG message{}; PeekMessageW(&message, nullptr, 0, 0, PM_NOREMOVE);
        mouseHook = SetWindowsHookExW(WH_MOUSE_LL, Mouse, GetModuleHandleW(nullptr), 0);
        keyboardHook = SetWindowsHookExW(WH_KEYBOARD_LL, Keyboard, GetModuleHandleW(nullptr), 0);
        SetEvent(inputReady);
        if (mouseHook && keyboardHook) {
            while (GetMessageW(&message, nullptr, 0, 0) > 0) { TranslateMessage(&message); DispatchMessageW(&message); }
        }
        if (mouseHook) UnhookWindowsHookEx(mouseHook);
        if (keyboardHook) UnhookWindowsHookEx(keyboardHook);
    }
    void AddTray() {
        tray = {}; tray.cbSize = sizeof(tray); tray.hWnd = owner; tray.uID = 1;
        tray.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP; tray.uCallbackMessage = Tray;
        tray.hIcon = LoadIconW(nullptr, IDI_APPLICATION);
        wcscpy_s(tray.szTip, L"显示器切换 · 按住右键再点左键 / Shift + 右键");
        Shell_NotifyIconW(NIM_ADD, &tray);
    }
    static std::wstring MenuText(const std::wstring& text) {
        std::wstring result; result.reserve(text.size());
        for (wchar_t c : text) { result += c; if (c == L'&') result += c; }
        return result;
    }
    void ShowResolved() {
        if (menuVisible || !ready || !requests.Current(ready->job.token, GetTickCount64())) return;
        if ((GetAsyncKeyState(VK_LBUTTON) | GetAsyncKeyState(VK_RBUTTON)) & 0x8000) return;
        auto response = std::move(ready);
        if (!requests.Complete(response->job.token, GetTickCount64())) return;
        menuVisible = true; // Escape/outside clicks must also cancel while the menu is being built.
        HMENU menu = CreatePopupMenu();
        if (!menu) { menuVisible = false; KillTimer(owner, MenuTimer); return; }
        std::vector<Command> commands;
        auto displays = Displays();
        auto addMonitors = [&](HMENU target, const Window& window) {
            auto current = MonitorFromWindow(window.handle, MONITOR_DEFAULTTONEAREST);
            for (const auto& display : displays) {
                std::wstring number;
                for (wchar_t c : std::wstring(display.info.szDevice)) if (c >= L'0' && c <= L'9') number += c;
                auto label = L"移到显示器 " + number;
                if (display.info.dwFlags & MONITORINFOF_PRIMARY) label += L"（主屏）";
                commands.push_back({window, display.info.szDevice});
                AppendMenuW(target, MF_STRING | (current == display.handle ? MF_CHECKED : 0), commands.size(), label.c_str());
            }
            if (displays.empty()) AppendMenuW(target, MF_STRING | MF_GRAYED, 0, L"未找到显示器。");
        };
        if (!response->error.empty()) AppendMenuW(menu, MF_STRING | MF_GRAYED, 0, response->error.c_str());
        else if (response->windows.empty()) AppendMenuW(menu, MF_STRING | MF_GRAYED, 0, L"未识别到打开的窗口，请重新选择。");
        else if (response->windows.size() == 1) addMonitors(menu, response->windows[0]);
        else for (const auto& window : response->windows) {
            HMENU child = CreatePopupMenu();
            if (!child) continue;
            addMonitors(child, window);
            if (!AppendMenuW(menu, MF_POPUP, reinterpret_cast<UINT_PTR>(child), MenuText(window.title).c_str())) DestroyMenu(child);
        }
        // This is an ordinary native popup; Windows owns submenu navigation and dismissal.
        if (requests.Version() != response->job.token || stopping) {
            menuVisible = false; DestroyMenu(menu); return;
        }
        SetForegroundWindow(owner);
        previews.Dismiss();
        Status(L"Native menu opened; windows=" + std::to_wstring(response->windows.size()));
        UINT selected = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_NOANIMATION,
            response->job.point.x, response->job.point.y, owner, nullptr);
        menuVisible = false;
        DestroyMenu(menu); // Recursively releases every submenu.
        // A new request may have arrived inside TrackPopupMenuEx's nested message loop.
        if (requests.Version() == response->job.token) { requests.Cancel(); KillTimer(owner, MenuTimer); }
        PostMessageW(owner, WM_NULL, 0, 0);
        if (selected && selected <= commands.size()) {
            const auto& command = commands[selected - 1];
            bool moved = MoveWindowTo(command.window.handle, command.window.pid, command.device);
            Status(moved ? L"Native menu closed; moved=True" : L"Native menu closed; moved=False");
            if (!moved) MessageBoxW(owner, L"窗口未能移到目标显示器，请确认窗口仍在运行、显示器仍已连接，然后重试。", L"显示器切换", MB_OK | MB_ICONINFORMATION);
        } else Status(L"Native menu closed; cancelled=True");
    }
    LRESULT Message(UINT message, WPARAM wp, LPARAM lp) {
        if (message == taskbarCreated) { AddTray(); QueueScan(nullptr, true); return 0; }
        switch (message) {
        case Prepare:
            if (requests.Current(wp, GetTickCount64())) {
                if (menuVisible) EndMenu();
                ready.reset(); previews.Begin(); SetTimer(owner, MenuTimer, 100, nullptr);
            }
            return 0;
        case Result: {
            std::unique_ptr<Resolution> response(reinterpret_cast<Resolution*>(lp));
            if (requests.Current(response->job.token, GetTickCount64())) { ready = std::move(response); ShowResolved(); }
            return 0;
        }
        case Cancel:
            if (requests.Version() == wp) { ready.reset(); KillTimer(owner, MenuTimer); if (menuVisible) EndMenu(); }
            return 0;
        case Replay: {
            std::unique_ptr<Button> button(reinterpret_cast<Button*>(lp));
            POINT cursor{}; GetCursorPos(&cursor);
            if (!Contains(button->bounds, cursor) || TaskbarAt(cursor) != button->taskbar) return 0;
            INPUT clicks[2]{};
            for (auto& click : clicks) { click.type = INPUT_MOUSE; click.mi.dwExtraInfo = ReplayTag; }
            clicks[0].mi.dwFlags = MOUSEEVENTF_RIGHTDOWN; clicks[1].mi.dwFlags = MOUSEEVENTF_RIGHTUP;
            SendInput(2, clicks, sizeof(INPUT)); return 0;
        }
        case WM_TIMER:
            if (wp == MenuTimer) {
                if (requests.Pending() && !requests.Current(requests.Version(), GetTickCount64())) CancelRequest();
                if (requests.Pending() || menuVisible) previews.Dismiss();
                else KillTimer(owner, MenuTimer);
                if (!menuVisible) ShowResolved();
            }
            return 0;
        case WM_HOTKEY:
            if (wp == 1) { POINT point{}; GetCursorPos(&point); StartRequest(point, {}, GetForegroundWindow()); }
            return 0;
        case WM_DISPLAYCHANGE:
            CancelRequest();
            { std::lock_guard lock(cacheMutex); buttons.clear(); }
            QueueScan(nullptr, true); return 0;
        case Tray:
            if (lp == WM_RBUTTONUP) {
                HMENU menu = CreatePopupMenu(); if (!menu) return 0;
                AppendMenuW(menu, MF_STRING | MF_GRAYED, 0, L"任务栏图标：按住右键，再点左键");
                AppendMenuW(menu, MF_STRING | MF_GRAYED, 0, L"也可使用 Shift + 右键");
                AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
                AppendMenuW(menu, MF_STRING, ExitCommand, L"退出");
                POINT point{}; GetCursorPos(&point); SetForegroundWindow(owner);
                UINT selected = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, point.x, point.y, owner, nullptr);
                DestroyMenu(menu); PostMessageW(owner, WM_NULL, 0, 0);
                if (selected == ExitCommand) PostMessageW(owner, WM_CLOSE, 0, 0);
            }
            return 0;
        case WM_APP + 6:
            MessageBoxW(owner, L"UI Automation 初始化失败，任务栏识别不可用。", L"显示器切换", MB_OK | MB_ICONERROR);
            PostMessageW(owner, WM_CLOSE, 0, 0); return 0;
        case WM_CLOSE:
            stopping = true; requests.Cancel(); ready.reset(); KillTimer(owner, MenuTimer);
            if (menuVisible) EndMenu();
            DestroyWindow(owner); return 0;
        case WM_DESTROY: PostQuitMessage(0); return 0;
        }
        return DefWindowProcW(owner, message, wp, lp);
    }
    static LRESULT CALLBACK WindowProc(HWND hwnd, UINT message, WPARAM wp, LPARAM lp) {
        if (message == WM_NCCREATE) {
            auto self = reinterpret_cast<App*>(reinterpret_cast<CREATESTRUCTW*>(lp)->lpCreateParams);
            self->owner = hwnd; SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(self));
        }
        auto self = reinterpret_cast<App*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
        return self ? self->Message(message, wp, lp) : DefWindowProcW(hwnd, message, wp, lp);
    }
public:
    int Run(HINSTANCE module) {
        instance = this;
        taskbarCreated = RegisterWindowMessageW(L"TaskbarCreated");
        WNDCLASSW cls{}; cls.lpfnWndProc = WindowProc; cls.hInstance = module; cls.lpszClassName = WindowClass;
        if (!RegisterClassW(&cls)) return 1;
        owner = CreateWindowExW(WS_EX_TOOLWINDOW, WindowClass, L"显示器切换", WS_POPUP, 0, 0, 0, 0, nullptr, nullptr, module, this);
        if (!owner) return 1;
        // Explorer can recreate the taskbar while this process runs elevated.
        ChangeWindowMessageFilterEx(owner, taskbarCreated, MSGFLT_ALLOW, nullptr);
        inputReady = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        input = CreateThread(nullptr, 0, [](void* p) -> DWORD { static_cast<App*>(p)->InputLoop(); return 0; }, this, 0, &inputId);
        if (!inputReady || !input || WaitForSingleObject(inputReady, 5000) != WAIT_OBJECT_0 || !mouseHook || !keyboardHook) {
            MessageBoxW(nullptr, L"无法启用鼠标监听，程序将退出。", L"显示器切换", MB_OK | MB_ICONERROR); Shutdown(); return 1;
        }
        worker = CreateThread(nullptr, 0, [](void* p) -> DWORD { static_cast<App*>(p)->WorkerLoop(); return 0; }, this, 0, nullptr);
        if (!worker) { Shutdown(); return 1; }
        bool hotkey = RegisterHotKey(owner, 1, MOD_ALT | MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, 'M');
        AddTray();
        Status(std::wstring(L"Native started; mouseHook=True; escapeHook=True; hotkey=") + (hotkey ? L"True" : L"False"));
        MSG message{};
        while (GetMessageW(&message, nullptr, 0, 0) > 0) { TranslateMessage(&message); DispatchMessageW(&message); }
        Shutdown(); return 0;
    }
    void Shutdown() {
        stopping = true; requests.Cancel(); wake.notify_one();
        Shell_NotifyIconW(NIM_DELETE, &tray); UnregisterHotKey(owner, 1);
        if (inputId) PostThreadMessageW(inputId, WM_QUIT, 0, 0);
        if (input) { WaitForSingleObject(input, 1500); CloseHandle(input); input = nullptr; }
        if (worker) { WaitForSingleObject(worker, 2000); CloseHandle(worker); worker = nullptr; }
        if (inputReady) { CloseHandle(inputReady); inputReady = nullptr; }
        // A hung external UIA provider must not prevent process exit. App remains alive until ExitProcess.
        MSG message{};
        while (PeekMessageW(&message, owner, Result, Result, PM_REMOVE)) delete reinterpret_cast<Resolution*>(message.lParam);
        while (PeekMessageW(&message, owner, Replay, Replay, PM_REMOVE)) delete reinterpret_cast<Button*>(message.lParam);
    }
};
App* App::instance{};
}

int WINAPI wWinMain(HINSTANCE module, HINSTANCE, PWSTR, int) {
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    int count{}; auto args = CommandLineToArgvW(GetCommandLineW(), &count);
    if (args && count >= 2 && std::wstring(args[1]) == L"--diagnose") {
        int result = count == 3 && args[2][0] ? Diagnose(args[2]) : 1;
        if (result) MessageBoxW(nullptr, L"诊断失败。请在 --diagnose 后指定可写入的文件路径。", L"显示器切换", MB_OK | MB_ICONERROR);
        LocalFree(args); return result;
    }
    if (args && count == 2 && std::wstring(args[1]) == L"--quit") {
        if (HWND hwnd = FindWindowW(WindowClass, nullptr)) PostMessageW(hwnd, WM_CLOSE, 0, 0);
        LocalFree(args); return 0;
    }
    if (args) LocalFree(args);
    HANDLE mutex = CreateMutexW(nullptr, TRUE, L"Local\\SmartSystemMenu.MonitorSwitch");
    if (!mutex) return 1;
    if (GetLastError() == ERROR_ALREADY_EXISTS) { CloseHandle(mutex); return 0; }
    // One process-lifetime object keeps callbacks safe even if an external UIA call is hung during exit.
    auto app = new App;
    int result = app->Run(module);
    ReleaseMutex(mutex); CloseHandle(mutex);
    return result;
}
