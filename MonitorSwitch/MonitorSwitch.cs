using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;

// Taskbar-owned menu: no code is injected into the application being moved.
internal static class MonitorSwitch
{
    [STAThread]
    private static void Main(string[] args)
    {
        try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); }
        catch (EntryPointNotFoundException) { Native.SetProcessDPIAware(); }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (args.Contains("--diagnose"))
        {
            File.WriteAllLines(args[Array.IndexOf(args, "--diagnose") + 1], TaskbarResolver.Diagnose(), Encoding.UTF8);
            return;
        }
        bool created;
        using (var mutex = new Mutex(true, "Local\\SmartSystemMenu.MonitorSwitch", out created))
        {
            if (!created) return;
            using (var form = new SwitchForm()) Application.Run(form);
        }
    }
}

internal sealed class SwitchForm : Form
{
    private readonly Native.MouseProc callback;
    private readonly NotifyIcon tray;
    private readonly ContextMenuStrip menu = new ContextMenuStrip();
    private readonly System.Windows.Forms.Timer scanTimer = new System.Windows.Forms.Timer { Interval = 1200 };
    private volatile TaskbarButton[] buttons = new TaskbarButton[0];
    private int scanning;
    private IntPtr hook;
    private bool swallowUp;
    private bool pending;

    public SwitchForm()
    {
        ShowInTaskbar = false;
        Opacity = 0;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        Text = "显示器切换";
        callback = MouseHook;
        scanTimer.Tick += delegate { RefreshButtons(); };
        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("Shift + 右键任务栏图标 → 选择显示器", null, delegate { });
        trayMenu.Items.Add("退出", null, delegate { Close(); });
        tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "显示器切换 · Shift + 右键任务栏图标", ContextMenuStrip = trayMenu, Visible = true };
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Hide();
        RefreshButtons();
        scanTimer.Start();
        bool hotkey = Native.RegisterHotKey(Handle, 1, 0x1 | 0x2 | 0x4 | 0x4000, (uint)Keys.M);
        Status("Started; hotkey=" + hotkey);
        using (var process = Process.GetCurrentProcess())
        using (var module = process.MainModule)
            hook = Native.SetWindowsHookEx(14, callback, Native.GetModuleHandle(module.ModuleName), 0);
        if (hook == IntPtr.Zero)
        {
            MessageBox.Show("无法启用任务栏鼠标监听。错误码：" + Marshal.GetLastWin32Error(), Text);
            Close();
        }
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x312 && message.WParam == new IntPtr(1))
        {
            IntPtr foreground = Native.GetForegroundWindow();
            var window = WindowInfo.Enumerate().FirstOrDefault(w => w.Handle == foreground);
            Status("Hotkey; matched=" + (window != null));
            if (window != null)
            {
                menu.Items.Clear();
                AddMonitors(menu.Items, window);
                Native.SetForegroundWindow(Handle);
                menu.Show(Cursor.Position);
            }
        }
        base.WndProc(ref message);
    }

    private IntPtr MouseHook(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            int kind = message.ToInt32();
            if (kind == 0x205 && swallowUp)
            {
                swallowUp = false;
                return new IntPtr(1);
            }
            if (kind == 0x204 && (Native.GetAsyncKeyState(0x10) & 0x8000) != 0)
            {
                var point = ((Native.MouseData)Marshal.PtrToStructure(data, typeof(Native.MouseData))).Point;
                var button = buttons.FirstOrDefault(b => b.Bounds.Contains(point.X, point.Y));
                if (button != null && TaskbarResolver.IsTaskbar(point))
                {
                    swallowUp = true;
                    if (!pending)
                    {
                        pending = true;
                        // UI Automation must never run inside the low-level mouse hook.
                        BeginInvoke((Action)delegate { ResolveAndShow(point, button); });
                    }
                    return new IntPtr(1);
                }
            }
        }
        return Native.CallNextHookEx(hook, code, message, data);
    }

    private void RefreshButtons()
    {
        if (Interlocked.Exchange(ref scanning, 1) != 0) return;
        ThreadPool.QueueUserWorkItem(delegate
        {
            try { buttons = TaskbarResolver.Buttons(); }
            catch (ElementNotAvailableException) { buttons = new TaskbarButton[0]; }
            catch (InvalidOperationException) { buttons = new TaskbarButton[0]; }
            catch (COMException) { buttons = new TaskbarButton[0]; }
            finally { Interlocked.Exchange(ref scanning, 0); }
        });
    }

    private void ResolveAndShow(Native.Point point, TaskbarButton button)
    {
        ThreadPool.QueueUserWorkItem(delegate
        {
            List<WindowInfo> windows = null;
            string error = null;
            try
            {
                windows = TaskbarResolver.Match(button, WindowInfo.Enumerate());
            }
            catch (Exception ex) { error = "无法读取任务栏窗口：" + ex.Message; }
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke((Action)delegate
            {
                pending = false;
                Status("Taskbar menu; matched=" + (windows == null ? 0 : windows.Count) + "; error=" + (error != null));
                menu.Items.Clear();
                if (error != null) menu.Items.Add(error).Enabled = false;
                else if (windows.Count == 1) AddMonitors(menu.Items, windows[0]);
                else if (windows.Count > 1)
                {
                    foreach (var window in windows)
                    {
                        var item = new ToolStripMenuItem(window.Title);
                        AddMonitors(item.DropDownItems, window);
                        menu.Items.Add(item);
                    }
                }
                else
                {
                    menu.Items.Add("未识别到“" + button.Label + "”的打开窗口").Enabled = false;
                    menu.Items.Add("固定图标需先打开程序；窗口匹配不明确时不会移动。").Enabled = false;
                }
                if (menu.Items.Count == 0) return;
                Native.SetForegroundWindow(Handle);
                menu.Show(new Point(point.X, point.Y));
            });
        });
    }

    private static void Status(string message)
    {
        try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MonitorSwitch.status.txt"), message, Encoding.UTF8); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void AddMonitors(ToolStripItemCollection items, WindowInfo window)
    {
        IntPtr current = Native.MonitorFromWindow(window.Handle, 2);
        foreach (var screen in Screen.AllScreens)
        {
            var target = screen;
            string number = new string(screen.DeviceName.Where(char.IsDigit).ToArray());
            var item = new ToolStripMenuItem("移到显示器 " + number + (screen.Primary ? "（主屏）" : ""));
            item.Checked = current == Native.MonitorFromPoint(new Native.Point(screen.Bounds.Left, screen.Bounds.Top), 2);
            item.Click += delegate
            {
                if (!Native.IsWindow(window.Handle) || WindowInfo.ReadProcessId(window.Handle) != window.ProcessId)
                {
                    MessageBox.Show("该窗口已关闭，请重新选择。", Text);
                    return;
                }
                if (!WindowMover.Move(window.Handle, target))
                    MessageBox.Show("窗口未能移到目标显示器。若目标软件以管理员身份运行，请以管理员身份启动显示器切换。", Text);
            };
            items.Add(item);
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (hook != IntPtr.Zero) Native.UnhookWindowsHookEx(hook);
        Native.UnregisterHotKey(Handle, 1);
        scanTimer.Stop();
        scanTimer.Dispose();
        tray.Visible = false;
        tray.ContextMenuStrip.Dispose();
        tray.Dispose();
        menu.Dispose();
        base.OnFormClosed(e);
    }
}

internal sealed class TaskbarButton
{
    public string AppId;
    public string Label;
    public Rectangle Bounds;
}

internal static class TaskbarResolver
{
    public static bool IsTaskbar(Native.Point point)
    {
        IntPtr root = Native.GetAncestor(Native.WindowFromPoint(point), 2);
        string name = Native.ClassName(root);
        return name == "Shell_TrayWnd" || name == "Shell_SecondaryTrayWnd";
    }

    public static TaskbarButton[] Buttons()
    {
        var result = new List<TaskbarButton>();
        Native.EnumWindows(delegate(IntPtr hwnd, IntPtr ignored)
        {
            string name = Native.ClassName(hwnd);
            if (name != "Shell_TrayWnd" && name != "Shell_SecondaryTrayWnd") return true;
            var elements = AutomationElement.FromHandle(hwnd).FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            foreach (AutomationElement element in elements)
            {
                var current = element.Current;
                if (!current.AutomationId.StartsWith("Appid:", StringComparison.OrdinalIgnoreCase)) continue;
                var rect = current.BoundingRectangle;
                if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0) continue;
                result.Add(new TaskbarButton { AppId = current.AutomationId.Substring(6).Trim(), Label = Label(current.Name),
                    Bounds = new Rectangle((int)rect.X, (int)rect.Y, (int)rect.Width, (int)rect.Height) });
            }
            return true;
        }, IntPtr.Zero);
        return result.ToArray();
    }

    private static string Label(string name)
    {
        int separator = name.LastIndexOf(" - ", StringComparison.Ordinal);
        return separator >= 0 ? name.Substring(0, separator) : name;
    }

    public static List<WindowInfo> Match(TaskbarButton button, List<WindowInfo> windows)
    {
        // Prefer the same AppUserModelID used by Explorer, including packaged apps
        // hosted by ApplicationFrameHost. Never activate a window just to identify it.
        var exact = windows.Where(w => !string.IsNullOrEmpty(button.AppId) && w.AppIds.Contains(button.AppId, StringComparer.OrdinalIgnoreCase)).ToList();
        if (exact.Count > 0) return exact;
        return windows.Where(w => string.Equals(w.Title, button.Label, StringComparison.OrdinalIgnoreCase)
            || string.Equals(w.ProcessName, button.Label, StringComparison.OrdinalIgnoreCase)
            || string.Equals(w.Product, button.Label, StringComparison.OrdinalIgnoreCase)
            || (button.AppId == "Microsoft.Windows.Explorer" && w.ClassName == "CabinetWClass")).ToList();
    }

    public static string[] Diagnose()
    {
        var lines = new List<string>();
        var windows = WindowInfo.Enumerate();
        Native.EnumWindows(delegate(IntPtr hwnd, IntPtr data)
        {
            string name = Native.ClassName(hwnd);
            if (name != "Shell_TrayWnd" && name != "Shell_SecondaryTrayWnd") return true;
            var buttons = AutomationElement.FromHandle(hwnd).FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            foreach (AutomationElement element in buttons)
            {
                var c = element.Current;
                if (!c.AutomationId.StartsWith("Appid:", StringComparison.OrdinalIgnoreCase)) continue;
                var button = new TaskbarButton { AppId = c.AutomationId.Substring(6).Trim(), Label = Label(c.Name) };
                var matches = Match(button, windows);
                lines.Add(button.Label + " | " + button.AppId + " | matched=" + matches.Count + " | "
                    + string.Join(", ", matches.Select(w => w.ProcessName + ":" + w.Handle)));
            }
            return true;
        }, IntPtr.Zero);
        return lines.ToArray();
    }
}

internal sealed class WindowInfo
{
    public IntPtr Handle;
    public int ProcessId;
    public string Title, ProcessName, Product, ClassName;
    public readonly HashSet<string> AppIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public static int ReadProcessId(IntPtr handle)
    {
        int id;
        Native.GetWindowThreadProcessId(handle, out id);
        return id;
    }

    public static List<WindowInfo> Enumerate()
    {
        var result = new List<WindowInfo>();
        Native.EnumWindows(delegate(IntPtr hwnd, IntPtr data)
        {
            if (!Native.IsWindowVisible(hwnd) || Native.GetWindow(hwnd, 4) != IntPtr.Zero) return true;
            if ((Native.GetWindowLong(hwnd, -20) & 0x80) != 0) return true;
            int cloaked;
            if (Native.DwmGetWindowAttribute(hwnd, 14, out cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            var title = new StringBuilder(1024);
            Native.GetWindowText(hwnd, title, title.Capacity);
            if (title.Length == 0) return true;
            string cls = Native.ClassName(hwnd);
            if (cls == "Progman" || cls == "WorkerW" || cls.StartsWith("Shell_")) return true;
            var window = new WindowInfo { Handle = hwnd, ProcessId = ReadProcessId(hwnd), Title = title.ToString(), ClassName = cls, ProcessName = "", Product = "" };
            try
            {
                using (var process = Process.GetProcessById(window.ProcessId))
                {
                    window.ProcessName = process.ProcessName;
                    using (var module = process.MainModule) window.Product = module.FileVersionInfo.ProductName ?? "";
                }
            }
            catch (System.ComponentModel.Win32Exception) { }
            catch (InvalidOperationException) { }
            catch (ArgumentException) { }
            AddAppIds(window, hwnd);
            if (cls == "ApplicationFrameWindow")
                Native.EnumChildWindows(hwnd, delegate(IntPtr child, IntPtr ignored) { AddAppIds(window, child); return true; }, IntPtr.Zero);
            result.Add(window);
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static void AddAppIds(WindowInfo window, IntPtr hwnd)
    {
        string id = Native.WindowAppId(hwnd);
        if (!string.IsNullOrEmpty(id)) window.AppIds.Add(id);
        IntPtr process = Native.OpenProcess(0x1000, false, ReadProcessId(hwnd));
        if (process == IntPtr.Zero) return;
        try
        {
            uint length = 512;
            var value = new StringBuilder((int)length);
            if (Native.GetApplicationUserModelId(process, ref length, value) == 0 && value.Length > 0) window.AppIds.Add(value.ToString());
        }
        finally { Native.CloseHandle(process); }
    }
}

internal static class WindowMover
{
    public static bool Move(IntPtr hwnd, Screen screen)
    {
        IntPtr target = Native.MonitorFromPoint(new Native.Point(screen.Bounds.Left, screen.Bounds.Top), 2);
        if (Native.MonitorFromWindow(hwnd, 2) == target) return true;
        bool maximized = Native.IsZoomed(hwnd);
        if (maximized || Native.IsIconic(hwnd)) Native.ShowWindow(hwnd, 9);
        Native.Rect rect;
        if (!Native.GetWindowRect(hwnd, out rect)) return false;
        var source = Screen.FromHandle(hwnd).WorkingArea;
        var destination = screen.WorkingArea;
        int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
        int x = destination.Left + rect.Left - source.Left;
        int y = destination.Top + rect.Top - source.Top;
        x = Math.Max(destination.Left, Math.Min(x, destination.Right - Math.Min(width, destination.Width)));
        y = Math.Max(destination.Top, Math.Min(y, destination.Bottom - Math.Min(height, destination.Height)));
        bool moved = Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, 0x1 | 0x4 | 0x10);
        if (maximized) Native.ShowWindow(hwnd, 3);
        return moved && Native.MonitorFromWindow(hwnd, 2) == target;
    }
}

internal static class Native
{
    internal delegate bool EnumProc(IntPtr hwnd, IntPtr data);
    internal delegate IntPtr MouseProc(int code, IntPtr message, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; public Point(int x, int y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct MouseData { public Point Point; public uint Mouse, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] internal struct PropertyKey { public Guid Format; public uint Id; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] internal struct PropVariant { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public IntPtr Value; }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface PropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }
    internal static string ClassName(IntPtr hwnd) { var value = new StringBuilder(256); GetClassName(hwnd, value, value.Capacity); return value.ToString(); }
    internal static string WindowAppId(IntPtr hwnd)
    {
        PropertyStore store;
        Guid iid = typeof(PropertyStore).GUID;
        if (SHGetPropertyStoreForWindow(hwnd, ref iid, out store) != 0) return "";
        try
        {
            var key = new PropertyKey { Format = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = 5 };
            PropVariant value;
            if (store.GetValue(ref key, out value) != 0) return "";
            try { return value.Type == 31 ? Marshal.PtrToStringUni(value.Value) : ""; }
            finally { PropVariantClear(ref value); }
        }
        finally { Marshal.ReleaseComObject(store); }
    }
    [DllImport("shell32.dll")] private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out PropertyStore store);
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern int GetApplicationUserModelId(IntPtr process, ref uint count, StringBuilder id);
    [DllImport("kernel32.dll")] internal static extern IntPtr OpenProcess(uint access, bool inherit, int id);
    [DllImport("kernel32.dll")] internal static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr SetWindowsHookEx(int kind, MouseProc callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] internal static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] internal static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] internal static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumProc proc, IntPtr data);
    [DllImport("user32.dll")] internal static extern bool EnumChildWindows(IntPtr hwnd, EnumProc proc, IntPtr data);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll")] internal static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(IntPtr hwnd, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] internal static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] internal static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] internal static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] internal static extern IntPtr MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] internal static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}
