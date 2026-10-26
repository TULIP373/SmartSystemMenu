#include "Core.h"
#include <psapi.h>
#include <iostream>
#include <stdexcept>

using namespace ms;
namespace {
int checks{};
void Check(bool condition, const char* message) {
    if (!condition) throw std::runtime_error(message);
    ++checks; std::cout << "PASS: " << message << '\n';
}
void TestIdentity() {
    Window packaged; packaged.title = L"Test app"; packaged.appIds = {L"Test.Package!App"}; packaged.handle = reinterpret_cast<HWND>(1);
    Window unrelated; unrelated.title = packaged.title; unrelated.handle = reinterpret_cast<HWND>(2);
    Button button{L"test.package!app", L"Test app", {10, 10, 50, 50}, reinterpret_cast<HWND>(3)};
    auto result = Match(button, {packaged, unrelated});
    Check(result.size() == 1 && result[0].handle == packaged.handle, "Package identity wins over a colliding title, ignoring case");
    Window second = packaged; second.handle = reinterpret_cast<HWND>(4);
    Check(Match(button, {packaged, second, unrelated}).size() == 2, "Grouped button matches all windows of that application");
    Check(Match({L"Unknown!App", L"Unknown"}, {packaged, unrelated}).empty(), "Unknown app never selects an unrelated window");
    Check(Match({L"", L"Test app"}, {packaged}).size() == 1, "Legacy exact title fallback works");
    Window conflict; conflict.title = L"Shared title"; conflict.process = conflict.product = L"Other"; conflict.appIds = {L"Other.Package!App"};
    Check(Match({L"Clicked.Package!App", L"Shared title"}, {conflict}).empty(), "Conflicting explicit identity rejects same-title fallback");
    Check(Match({L"Clicked.Package!App", L"Other"}, {conflict}).empty(), "Conflicting identity rejects product and process fallback");
    conflict.appIds.clear();
    Check(Match({L"Legacy!App", L"Other"}, {conflict}).size() == 1, "Legacy process fallback remains available");
    Check(Match({L"Unknown", L""}, {Window{}}).empty(), "Empty strings cannot accidentally match");
    Window explorer; explorer.className = L"CabinetWClass";
    Check(Match({L"Microsoft.Windows.Explorer", L"Explorer"}, {explorer}).size() == 1, "Explorer taskbar grouping matches folder windows");
    Button live = button;
    Check(SameLiveButton(button, live, {20, 20}), "Live icon identity and position are revalidated");
    live.appId = L"Another!App";
    Check(!SameLiveButton(button, live, {20, 20}), "A reordered different app is rejected");
    live = button;
    Check(!SameLiveButton(button, live, {90, 90}), "An icon outside the clicked position is rejected");
    live.taskbar = reinterpret_cast<HWND>(5);
    Check(!SameLiveButton(button, live, {20, 20}), "An icon on a different taskbar is rejected");
    live = button; live.appId.clear();
    Check(!SameLiveButton(button, live, {20, 20}), "An empty application identity is rejected");
}
void TestGestures() {
    Button hit{L"Test.Package!App", L"Test", {10, 10, 50, 50}, reinterpret_cast<HWND>(3)};
    POINT point{20, 20}; Gesture gesture;
    Check(gesture.Handle(WM_RBUTTONDOWN, &hit, point, false, false) == MouseAction::Consume, "Right down waits for a possible chord");
    Check(gesture.Handle(WM_RBUTTONUP, &hit, point, false, false) == MouseAction::Replay, "Ordinary right click requests exactly one native replay");
    Check(gesture.Handle(WM_RBUTTONUP, &hit, point, false, false) == MouseAction::Forward, "Duplicate right up cannot replay again");
    for (bool leftFirst : {true, false}) {
        gesture = Gesture{};
        gesture.Handle(WM_RBUTTONDOWN, &hit, point, false, false);
        Check(gesture.Handle(WM_LBUTTONDOWN, &hit, point, false, false) == MouseAction::OpenMenu && Equal(gesture.selected.appId, hit.appId), "Right then left opens the clicked app menu");
        Check(gesture.Handle(leftFirst ? WM_LBUTTONUP : WM_RBUTTONUP, &hit, point, false, false) == MouseAction::Consume, "First chord release is consumed");
        Check(gesture.Handle(leftFirst ? WM_RBUTTONUP : WM_LBUTTONUP, &hit, point, false, false) == MouseAction::Consume, "Second chord release is consumed");
        Check(gesture.Handle(WM_LBUTTONDOWN, nullptr, point, false, false) == MouseAction::Forward, "Subsequent clicks reach their normal targets");
    }
    gesture = Gesture{};
    Check(gesture.Handle(WM_RBUTTONDOWN, &hit, point, true, false) == MouseAction::OpenMenu, "Shift plus right remains available");
    Check(gesture.Handle(WM_RBUTTONUP, &hit, point, false, false) == MouseAction::Consume, "Shift release cannot open a second menu");
    gesture = Gesture{};
    Check(gesture.Handle(WM_RBUTTONDOWN, nullptr, point, false, false) == MouseAction::Forward, "Taskbar blank space and tray right clicks pass through");
    Check(gesture.Handle(WM_LBUTTONDOWN, &hit, point, false, false) == MouseAction::Forward, "A right press elsewhere cannot initiate a chord");
    Check(gesture.Handle(WM_RBUTTONDOWN, &hit, point, false, true) == MouseAction::Forward, "Left then right does not initiate the chord");
    gesture = Gesture{}; gesture.Handle(WM_RBUTTONDOWN, &hit, point, false, false);
    Check(gesture.Handle(WM_LBUTTONDOWN, nullptr, {80, 80}, false, false) == MouseAction::Forward, "Outside left click cancels a chord and passes through");
    Check(gesture.Handle(WM_RBUTTONUP, &hit, point, false, false) == MouseAction::Consume, "Cancelled chord cannot reopen on right release");
    gesture = Gesture{}; gesture.Handle(WM_RBUTTONDOWN, &hit, point, false, false);
    auto other = hit; other.appId = L"Different!App";
    Check(gesture.Handle(WM_LBUTTONDOWN, &other, point, false, false) == MouseAction::Forward, "Changed cached identity cannot trigger the chord");
    gesture = Gesture{}; gesture.Handle(WM_RBUTTONDOWN, &hit, point, false, false);
    auto fresh = hit;
    Check(gesture.Handle(WM_LBUTTONDOWN, &fresh, point, false, false) == MouseAction::OpenMenu, "Cache object replacement preserves an unchanged identity");
    Check(sizeof(INPUT) == 40, "Native INPUT uses the x64 ABI");
}
void TestRequests() {
    RequestState state;
    auto first = state.Begin(100);
    Check(state.Current(first, 101), "New request is current");
    state.Cancel();
    Check(!state.Pending() && !state.Complete(first, 102), "Outside click or Escape rejects delayed work");
    auto old = state.Begin(200); auto current = state.Begin(201);
    Check(!state.Complete(old, 202) && state.Pending(), "Old response cannot complete the latest request");
    Check(state.Complete(current, 203) && !state.Pending(), "Current response completes once");
    Check(!state.Complete(current, 204), "Duplicate response cannot reopen the menu");
    auto timeout = state.Begin(300);
    Check(!state.Current(timeout, 300 + RequestTimeout), "Five-second request timeout is enforced");
    Check(!state.Complete(timeout, 301 + RequestTimeout), "Timed-out responses are rejected");
    auto wrapped = state.Begin(~ULONGLONG(0) - 10);
    Check(state.Current(wrapped, 10), "Elapsed time supports tick count wraparound");
    state.Cancel();
}
void TestPreviews() {
    Check(IsTaskbarPreview(L"TaskListThumbnailWnd", 10, 20, 10, 20), "Classic preview recognized");
    Check(IsTaskbarPreview(L"XamlExplorerHostIslandWindow", 10, 20, 10, 20), "Modern taskbar XAML preview recognized");
    Check(!IsTaskbarPreview(L"XamlExplorerHostIslandWindow", 10, 21, 10, 20), "Alt-Tab and Task View threads excluded");
    Check(!IsTaskbarPreview(L"TaskListThumbnailWnd", 11, 20, 10, 20), "Other process excluded");
    Check(!IsTaskbarPreview(L"XamlExplorerHostIslandWindow_WASDK", 10, 20, 10, 20), "Desktop context menu hosts excluded");
    Check(!IsTaskbarPreview(L"Shell_TrayWnd", 10, 20, 10, 20), "Taskbar itself cannot be hidden");
    Check(!IsTaskbarPreview(L"XamlExplorerHostIslandWindow", 0, 0, 0, 0), "Invalid preview identity excluded");
}
LRESULT CALLBACK TestWindow(HWND hwnd, UINT message, WPARAM wp, LPARAM lp) { return DefWindowProcW(hwnd, message, wp, lp); }
void Pump() { MSG message{}; while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) { TranslateMessage(&message); DispatchMessageW(&message); } }
void TestMoves() {
    WNDCLASSW cls{}; cls.lpfnWndProc = TestWindow; cls.lpszClassName = L"MonitorSwitch.Native.Test"; cls.hInstance = GetModuleHandleW(nullptr);
    RegisterClassW(&cls);
    HWND hwnd = CreateWindowExW(0, cls.lpszClassName, L"MonitorSwitch native synthetic test", WS_OVERLAPPED | WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX,
        100, 100, 420, 240, nullptr, nullptr, cls.hInstance, nullptr);
    Check(hwnd != nullptr, "Synthetic window without a system menu is created");
    ShowWindow(hwnd, SW_SHOWNOACTIVATE); Pump();
    auto windows = Windows();
    Check(std::any_of(windows.begin(), windows.end(), [&](const auto& w) { return w.handle == hwnd; }), "Window without a system menu remains discoverable");
    auto displays = Displays();
    DWORD pid = GetCurrentProcessId();
    Check(!MoveWindowTo(hwnd, pid + 1, displays.front().info.szDevice), "Changed window PID is rejected before moving");
    Check(!MoveWindowTo(hwnd, pid, L"Missing display"), "Disconnected display cannot move or report success");
    if (displays.size() >= 2) {
        Check(MoveWindowTo(hwnd, pid, displays[0].info.szDevice), "Normal window moves to first display");
        Check(MoveWindowTo(hwnd, pid, displays[1].info.szDevice), "Normal window moves to second display");
        Check(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST) == displays[1].handle, "Actual monitor confirms movement");
        ShowWindow(hwnd, SW_MAXIMIZE); Pump();
        Check(MoveWindowTo(hwnd, pid, displays[0].info.szDevice) && IsZoomed(hwnd), "Maximized window preserves maximized state");
        ShowWindow(hwnd, SW_MINIMIZE); Pump();
        Check(MoveWindowTo(hwnd, pid, displays[1].info.szDevice) && !IsIconic(hwnd), "Minimized window restores on chosen display");
        ShowWindow(hwnd, SW_MINIMIZE); Pump();
        Check(MoveWindowTo(hwnd, pid, displays[1].info.szDevice) && !IsIconic(hwnd), "Choosing current display restores minimized window");
    } else std::cout << "SKIP: cross-display movement requires two displays\n";
    DestroyWindow(hwnd); UnregisterClassW(cls.lpszClassName, cls.hInstance);
    Check(!MoveWindowTo(hwnd, pid, displays.front().info.szDevice), "Closed window cannot be moved");
}
PROCESS_MEMORY_COUNTERS_EX Memory() {
    PROCESS_MEMORY_COUNTERS_EX counters{}; counters.cb = sizeof(counters);
    GetProcessMemoryInfo(GetCurrentProcess(), reinterpret_cast<PROCESS_MEMORY_COUNTERS*>(&counters), sizeof(counters)); return counters;
}
void TestMenuResources() {
    DWORD before = GetGuiResources(GetCurrentProcess(), GR_USEROBJECTS);
    for (int i = 0; i < 1000; ++i) {
        HMENU root = CreatePopupMenu(), child = CreatePopupMenu();
        AppendMenuW(child, MF_STRING, 1, L"Move");
        AppendMenuW(root, MF_POPUP, reinterpret_cast<UINT_PTR>(child), L"Window");
        DestroyMenu(root);
    }
    Check(GetGuiResources(GetCurrentProcess(), GR_USEROBJECTS) <= before + 1, "One thousand menu rebuilds do not accumulate USER objects");
}
bool enteredMenu{}, cancelledMenu{};
LRESULT CALLBACK PopupWindow(HWND hwnd, UINT message, WPARAM wp, LPARAM lp) {
    if (message == WM_ENTERMENULOOP) enteredMenu = true;
    if (message == WM_TIMER) { cancelledMenu = true; EndMenu(); KillTimer(hwnd, 1); return 0; }
    return DefWindowProcW(hwnd, message, wp, lp);
}
void TestNativePopup() {
    WNDCLASSW cls{}; cls.lpfnWndProc = PopupWindow; cls.lpszClassName = L"MonitorSwitch.Native.PopupTest"; cls.hInstance = GetModuleHandleW(nullptr);
    RegisterClassW(&cls);
    HWND hwnd = CreateWindowExW(WS_EX_TOOLWINDOW, cls.lpszClassName, L"Native popup test", WS_POPUP, 0, 0, 0, 0, nullptr, nullptr, cls.hInstance, nullptr);
    HMENU menu = CreatePopupMenu(); AppendMenuW(menu, MF_STRING, 1, L"Native menu self-test");
    SetForegroundWindow(hwnd);
    SetTimer(hwnd, 1, 150, nullptr);
    UINT command = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_NOANIMATION, 100, 100, hwnd, nullptr);
    DestroyMenu(menu); DestroyWindow(hwnd); UnregisterClassW(cls.lpszClassName, cls.hInstance);
    Check(enteredMenu, "Hidden tool window can own a native popup menu");
    Check(cancelledMenu && command == 0, "Native menu message loop accepts asynchronous cancellation");
}
void TestAutomation(int iterations) {
    Automation automation;
    Check(automation.Ready(), "Native COM UI Automation initializes on MTA");
    auto bars = Taskbars(); Check(!bars.empty(), "Real taskbars are discovered");
    std::vector<Button> buttons;
    for (auto bar : bars) Check(automation.Buttons(bar, buttons), "Cached taskbar property snapshot succeeds");
    Check(!buttons.empty(), "Actual application buttons are identified");
    auto windows = Windows();
    for (const auto& button : buttons) {
        auto matches = Match(button, windows);
        std::wcout << L"INTEGRATION: " << button.appId << L" | matches=" << matches.size() << L'\n';
    }
    for (int i = 0; i < 20; ++i) { std::vector<Button> fresh; for (auto bar : bars) automation.Buttons(bar, fresh); }
    auto before = Memory(); DWORD handlesBefore{}; GetProcessHandleCount(GetCurrentProcess(), &handlesBefore);
    ULONGLONG started = GetTickCount64();
    for (int i = 0; i < iterations; ++i) {
        std::vector<Button> fresh;
        for (auto bar : bars) if (!automation.Buttons(bar, fresh)) throw std::runtime_error("UIA snapshot failed during stress");
        if ((i + 1) % 100 == 0) {
            auto counters = Memory();
            std::cout << "STRESS: reads=" << i + 1 << " privateKiB=" << counters.PrivateUsage / 1024 << " workingKiB=" << counters.WorkingSetSize / 1024 << '\n';
        }
    }
    auto after = Memory(); DWORD handlesAfter{}; GetProcessHandleCount(GetCurrentProcess(), &handlesAfter);
    std::cout << "RESOURCE: iterations=" << iterations << " elapsedMs=" << GetTickCount64() - started
        << " privateBeforeKiB=" << before.PrivateUsage / 1024 << " privateAfterKiB=" << after.PrivateUsage / 1024
        << " handlesBefore=" << handlesBefore << " handlesAfter=" << handlesAfter << '\n';
    Check(handlesAfter <= handlesBefore + 10, "Repeated UIA reads do not accumulate process handles");
    Check(after.PrivateUsage <= before.PrivateUsage + 32 * 1024 * 1024, "Warmed UIA stress stays within 32 MiB additional private commit");
}
}
int wmain(int argc, wchar_t** argv) {
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    try {
        int iterations = argc > 1 ? std::stoi(argv[1]) : 50;
        if (iterations < 1 || iterations > 100000) throw std::runtime_error("Invalid stress iteration count");
        TestIdentity(); TestGestures(); TestRequests(); TestPreviews(); TestMoves(); TestMenuResources(); TestNativePopup(); TestAutomation(iterations);
        std::cout << "Verified " << checks << " checks.\n"; return 0;
    } catch (const std::exception& error) { std::cerr << "FAIL: " << error.what() << '\n'; return 1; }
}
