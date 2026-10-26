#pragma once
#ifndef UNICODE
#define UNICODE
#endif
#include <windows.h>
#include <algorithm>
#include <atomic>
#include <string>
#include <vector>

namespace ms {
constexpr ULONG_PTR ReplayTag = 0x4D535743;
constexpr ULONGLONG RequestTimeout = 5000;
inline bool Equal(const std::wstring& a, const std::wstring& b) {
    return CompareStringOrdinal(a.c_str(), -1, b.c_str(), -1, TRUE) == CSTR_EQUAL;
}
inline bool Contains(const RECT& r, POINT p) { return PtInRect(&r, p) != FALSE; }
struct Button { std::wstring appId, label; RECT bounds{}; HWND taskbar{}; };
struct Window {
    HWND handle{}; DWORD pid{};
    std::wstring title, process, product, className;
    std::vector<std::wstring> appIds;
};
struct Display { HMONITOR handle{}; MONITORINFOEXW info{}; };
bool SameLiveButton(const Button& expected, const Button& actual, POINT point);
std::vector<Window> Match(const Button& button, const std::vector<Window>& windows);
std::wstring ClassName(HWND hwnd);
HWND TaskbarAt(POINT point);
std::vector<HWND> Taskbars();
std::vector<Window> Windows();
std::vector<Display> Displays();
bool MoveWindowTo(HWND hwnd, DWORD pid, const std::wstring& device);
bool IsTaskbarPreview(const std::wstring& name, DWORD pid, DWORD thread, DWORD ownerPid, DWORD ownerThread);

// Only plain data crosses threads. UI Automation interfaces stay on their MTA worker.
class Automation {
    struct Impl;
    Impl* impl{};
public:
    Automation();
    ~Automation();
    Automation(const Automation&) = delete;
    Automation& operator=(const Automation&) = delete;
    bool Ready() const;
    bool Buttons(HWND taskbar, std::vector<Button>& result);
};

enum class MouseAction { Forward, Consume, OpenMenu, Replay };
class Gesture {
    Button candidate;
    bool waiting{}, rightUp{}, leftUp{};
public:
    Button selected;
    bool Waiting() const { return waiting; }
    MouseAction Handle(UINT message, const Button* hit, POINT point, bool shift, bool leftDown);
};

class RequestState {
    std::atomic<ULONGLONG> version{0}, started{0};
    std::atomic<ULONGLONG> active{0};
public:
    ULONGLONG Begin(ULONGLONG now) {
        started = now; auto token = ++version; active = token; return token;
    }
    ULONGLONG Cancel() { auto token = ++version; active = 0; return token; }
    bool Current(ULONGLONG token, ULONGLONG now) const {
        return active == token && version == token && now - started.load() < RequestTimeout;
    }
    bool Complete(ULONGLONG token, ULONGLONG now) {
        if (!Current(token, now)) return false;
        return active.compare_exchange_strong(token, 0);
    }
    bool Pending() const { auto token = active.load(); return token && token == version; }
    ULONGLONG Version() const { return version; }
};

class PreviewSuppressor {
    struct Owner { DWORD pid, thread; };
    std::vector<Owner> owners;
public:
    void Begin();
    void Dismiss();
};
}
