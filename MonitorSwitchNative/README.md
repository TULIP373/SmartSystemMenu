# MonitorSwitch Native

Windows 11 x64 的原生显示器切换小工具。C++17 + Win32 + 系统 UI Automation COM，无 .NET、Qt、Electron、NuGet 或附带运行库 DLL。独立 EXE 可以直接运行；不需要启动 Windhawk 或原版 SmartSystemMenu。

## 使用

- 在任务栏已打开的程序图标上，按住右键，再点一下左键，松开鼠标后选择显示器。
- 保留 Shift + 右键，以及 Ctrl + Alt + Shift + M（对当前前台窗口）。
- 普通右键仍打开 Windows 的原菜单；开始按钮、系统托盘和任务栏空白区域不拦截。
- 多窗口程序先选择窗口，再选择显示器。当前屏幕带勾，编号沿用 Windows 的设备编号。
- 菜单打开期间收起任务栏缩略预览，关闭后恢复正常悬停预览。
- 点击菜单外或按 Esc 取消。识别过程超过 5 秒后不再弹出过期结果。
- 右键托盘图标选择“退出”。

启动申请管理员权限，以便移动任务管理器等管理员窗口；Windows 的权限提示仍需用户正常确认。
只移动当前已打开的窗口，不改变以后新窗口的默认显示器。不创建开机启动项。

## 资源与线程

- 主线程：隐藏 Win32 窗口、原生 `TrackPopupMenuEx` 菜单、托盘和菜单期间的 100 ms 预览定时器。
- 输入线程：独立消息循环上的低级鼠标/键盘钩子；不调用 UI Automation、不写日志、不移动窗口。键盘钩子只判断 Esc，不记录输入。
- 工作线程：MTA 中的 UI Automation、窗口身份解析。批量缓存读取 AutomationId、Name 和 BoundingRectangle；COM 接口不跨线程传递。
- 初始化读取一次任务栏。之后鼠标进入任务栏时刷新，鼠标在任务栏内移动时最多每 2 秒请求一次刷新；没有空闲全局扫描定时器。任务栏重建、显示拓扑变化也会刷新。
- 鼠标移动时最多每 100 ms 判断一次任务栏位置；刷新请求另有 500 ms 节流。静止悬停不会反复扫描。
- 打开切屏菜单前重新读取实际位置的图标身份。过期缓存宁可提示重试，也不能移动另一个程序。
- 新出现的图标可能需要短暂悬停后重试。所有权和屏幕身份在移动前重新验证。
- 菜单及子菜单销毁时一并释放；进程信息只在单次枚举中按 PID 复用，不建立永久进程缓存。

`Core.h/.cpp` 是识别、手势、请求状态、预览和移动逻辑；`App.cpp` 是原生 UI 与线程协调；`Tests.cpp` 只编译进测试程序。

## 构建、测试与打包

使用 LLVM-MinGW 的 x64 clang++：

```powershell
.\MonitorSwitchNative\build.ps1 -Compiler 'D:\Programs\Windhawk\Compiler\bin\clang++.exe'
.\MonitorSwitchNative\test.ps1 -Iterations 1000
.\MonitorSwitchNative\package.ps1
```

编译器路径可替换为独立安装的 LLVM-MinGW；Windhawk 只是在本机提供了现成编译器，不是产品运行依赖。显式指定 x86_64 目标，静态链接 C++ 支持代码，并嵌入管理员和 PerMonitorV2 清单。输出在 `bin/`。

测试只移动专用测试窗口，不操作用户的工作窗口。覆盖身份冲突、任务栏位置复核、右键组合及释放顺序、取消/过期请求、预览过滤、双屏普通/最大化/最小化移动，以及菜单资源和真实任务栏批量读取压力测试。双屏移动检查在只有一块屏幕时跳过。原生菜单冒烟测试短暂显示一个测试菜单并自动取消。

压力测试的短期结果不能代替 24～48 小时的常驻观察。应同时观察工作集、私有提交、句柄和 USER 对象，不能仅以 EXE 大小判断资源占用。

诊断只包含应用身份及匹配数量，不包含窗口正文：

```powershell
Start-Process 'D:\Programs\MonitorSwitch\MonitorSwitch.exe' -Verb RunAs -Wait `
    -ArgumentList '--diagnose "D:\Programs\MonitorSwitch\diagnose.txt"'
```

`--quit` 请求关闭正在运行的原生版本；需要相同权限。关闭后才启动旧版回退，二者共用单实例互斥锁。

## 本机部署与回退

原生版：`D:\Programs\MonitorSwitch\MonitorSwitch.exe`。
旧 C# 版及原版资料保留在 `D:\Programs\SmartSystemMenu`，便于回退，不再作为日常启动目标。
开始菜单原有 `SmartSystemMenu.lnk` 指向原生版，避免同时创建重复入口。

Windows 11 的任务栏 UI Automation 结构和预览类名可能随系统更新变化；其他任务栏替代软件尚未验证。
本工具不向目标程序注入 DLL。MIT 许可证及上游版权声明保留，来源为 [SmartSystemMenu](https://github.com/AlexanderPro/SmartSystemMenu)。
