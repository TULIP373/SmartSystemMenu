# 仅切换显示器：MonitorSwitch

本 fork 增加了 `MonitorSwitch`，用于从任务栏图标选择窗口所在的显示器。
它提供自己的菜单，不再依赖目标程序的传统系统菜单，也不向目标程序注入 DLL。
因此也能识别使用自定义标题栏的桌面程序，以及由 `ApplicationFrameHost` 托管的商店应用。

## 使用

1. 启动 `MonitorSwitch.exe`。
2. 按住 **Shift**，右键任务栏上已经打开的程序图标。
3. 选择 **移到显示器 1 / 2 / …**。当前所在显示器会打勾。

程序有多个窗口时，菜单先列出窗口标题，再选择显示器；不会猜测你要移动哪一个。
任务栏里的开始按钮、托盘和空白区域维持原来的操作。
固定但未打开的程序没有可移动窗口。

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

测试包括按 AppUserModelID 精确匹配、同名窗口冲突、多窗口分组，以及无系统菜单的测试窗口在双屏之间普通、最大化和最小化状态下的移动。
测试会短暂打开一个专用测试窗口，不移动用户的工作窗口。
双屏移动测试要求连接两个显示器。

还可读取任务栏图标与窗口的匹配结果（包含本地程序名和窗口句柄，不含聊天正文）：

```powershell
.\MonitorSwitch\bin\MonitorSwitch.exe --diagnose "$env:TEMP\MonitorSwitch-diagnose.txt"
```

## 本机部署

运行文件位于 `D:\Programs\SmartSystemMenu\MonitorSwitch.exe`。
旧的 SmartSystemMenu 后台进程应退出，避免两套菜单同时响应 Shift + 右键。
原版 EXE、DLL 和配置备份保留在该目录。

## 范围与限制

- 目前针对 Windows 11 的 `Appid:` 任务栏按钮实现；其他任务栏替代软件尚未验证。
- 识别采用任务栏 AppUserModelID、窗口属性、包进程及其宿主子窗口。精确匹配失败时仅尝试完整标题、进程名或产品名；匹配不到时提示，不移动别的窗口。
- 任务栏按钮位置定期刷新，新出现的图标可能需要等待约 1 秒再操作。
- 管理员窗口可能需要以管理员身份运行 MonitorSwitch。
- 只移动当前已打开的窗口，不改变以后新窗口的默认显示器。

原有 `SmartSystemMenu/SmartSystemMenu.xml` 仍保留只显示 `move_to` 的精简配置，供传统系统菜单模式使用。

上游项目：https://github.com/AlexanderPro/SmartSystemMenu （MIT）
