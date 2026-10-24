# SmartSystemMenu：仅切换显示器

本 fork 将默认窗口扩展菜单精简为 **移动到 → 显示器 1 / 显示器 2 / …**。
Windows 自带的还原、最小化、最大化和关闭菜单不受影响。

## 使用

1. 启动 `SmartSystemMenu.exe`。
2. 按住 **Shift**，右键任务栏上已经打开的窗口图标。
3. 选择 **移动到**，再选择目标显示器。

同一个程序有多个窗口时，需要先选择具体窗口。本功能移动现有窗口，不设置以后新窗口的默认显示器。

## 修改与部署

精简配置位于 `SmartSystemMenu/SmartSystemMenu.xml`。仅保留 `move_to` 菜单组，移除了其他扩展菜单及其快捷键，也清空了不再使用的尺寸和启动程序列表。原有切换显示器快捷键保留。

程序原生支持读取此配置，因此可直接配合上游 2.34.0 官方发布版使用，无需重新编译 EXE 或 DLL。
退出正在运行的 SmartSystemMenu 后，将此 XML 复制到 EXE 所在目录并重新启动即可。
本机运行目录为 `D:\Programs\SmartSystemMenu`，原配置备份在该目录的 `backups` 子目录。

上游项目：https://github.com/AlexanderPro/SmartSystemMenu
