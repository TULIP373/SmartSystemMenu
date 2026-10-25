# 仅切换显示器：MonitorSwitch

本 fork 增加了 `MonitorSwitch`，用于从任务栏图标选择窗口所在的显示器。
它提供自己的菜单，不再依赖目标程序的传统系统菜单，也不向目标程序注入 DLL。
因此也能识别使用自定义标题栏的桌面程序，以及由 `ApplicationFrameHost` 托管的商店应用。

## 使用

1. 启动 `MonitorSwitch.exe`，在 Windows 管理员权限提示中选择“是”（用于移动任务管理器等管理员窗口）。
2. 在任务栏上已经打开的程序图标处，**按住鼠标右键，再点一下左键**，然后松开鼠标。也可继续使用 **Shift + 右键**。
3. 选择 **移到显示器 1 / 2 / …**。当前所在显示器会打勾。

程序有多个窗口时，菜单先列出窗口标题，再选择显示器；不会猜测你要移动哪一个。
点击菜单及其展开的子菜单以外的任意位置，菜单会自动关闭，该次点击仍正常作用于目标窗口。
弹出切屏菜单时会收起任务栏缩略预览，并在菜单打开期间抑制预览遮挡；菜单关闭后正常悬停预览不受影响。
任务栏里的开始按钮、托盘和空白区域维持原来的操作。
固定但未打开的程序没有可移动窗口。
只点击右键时仍打开 Windows 原来的任务栏菜单；组合操作的左击不会激活或最小化目标程序。右键在图标外按下、先按左键再按右键、按下后移到别的图标，都不会触发切屏菜单。
识别窗口期间，点击其他位置或按 **Esc** 会取消本次请求；超过 5 秒的结果不会再弹出菜单。
程序会在后台重新读取实际点击位置的图标身份。若图标刚移动、缓存位置已过期，会要求重新选择，避免移动别的软件。
菜单打开后若显示器断开，旧菜单会取消；移动前也会再次核对显示器身份。

也可激活需要移动的窗口，按 **Ctrl + Alt + Shift + M** 打开相同的显示器菜单。
退出程序可右键托盘中的“显示器切换”图标，选择“退出”。

## 构建和验证

在 64 位 Windows 上运行：

```powershell
.\MonitorSwitch\build.ps1
.\MonitorSwitch\test.ps1
```

直接使用 Windows 自带的 .NET Framework 编译器，不需要下载 NuGet 包或安装 Visual Studio。
产物位于 `MonitorSwitch/bin/MonitorSwitch.exe`。

测试包括按 AppUserModelID 精确匹配、冲突身份拒绝同名回退、多窗口分组、过期图标复核、请求取消和超时，以及无系统菜单的测试窗口在双屏之间普通、最大化和最小化状态下的移动。也覆盖选择当前屏幕恢复最小化窗口，以及不存在的显示器拒绝移动。
鼠标组合测试覆盖普通右键回放、左右键释放顺序、Shift 兼容、任务栏缓存刷新及图标外操作；不会向用户窗口模拟鼠标点击。
测试会短暂打开一个专用测试窗口，不移动用户的工作窗口。
双屏移动测试要求连接两个显示器。

源代码按职责拆分：`Program.cs` 负责启动，`SwitchForm.cs` 负责菜单和请求生命周期，`TaskbarIntegration.cs` 负责任务栏识别、鼠标组合和预览抑制，`WindowOperations.cs` 负责窗口身份及移动，`NativeMethods.cs` 保存 Win32 声明和消息常量。构建脚本显式列出生产源码，不会编译 `bin` 下的诊断文件。

还可读取任务栏图标与窗口的匹配结果（包含本地程序名和窗口句柄，不含聊天正文）：

```powershell
Start-Process .\MonitorSwitch\bin\MonitorSwitch.exe -Verb RunAs `
    -ArgumentList "--diagnose `"$env:TEMP\MonitorSwitch-diagnose.txt`""
```

## 本机部署

运行文件位于 `D:\Programs\SmartSystemMenu\MonitorSwitch.exe`。
旧的 SmartSystemMenu 后台进程应退出，避免两套菜单同时响应 Shift + 右键。
原版 EXE、DLL 和配置备份保留在该目录。

## 范围与限制

- 目前针对 Windows 11 的 `Appid:` 任务栏按钮实现；其他任务栏替代软件尚未验证。
- 识别采用任务栏 AppUserModelID、窗口属性、包进程及其宿主子窗口。存在冲突的显式身份时不允许通过同名匹配；没有显式窗口身份的传统程序仍可按完整标题、进程名或产品名匹配。
- 任务栏按钮位置定期刷新，新出现的图标可能需要等待约 1 秒再操作。
- 程序启动时申请管理员权限；拒绝 Windows 权限提示则不会启动。运行期间移动窗口无需再次授权。
- 只移动当前已打开的窗口，不改变以后新窗口的默认显示器。

原有 `SmartSystemMenu/SmartSystemMenu.xml` 仍保留只显示 `move_to` 的精简配置，供传统系统菜单模式使用。

上游项目：https://github.com/AlexanderPro/SmartSystemMenu （MIT）
