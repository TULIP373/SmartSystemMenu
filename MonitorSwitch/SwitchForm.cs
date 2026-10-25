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

internal sealed class SwitchForm : Form
{
    private readonly Native.MouseProc callback;
    private readonly NotifyIcon tray;
    private readonly ContextMenuStrip menu = new ContextMenuStrip();
    private readonly System.Windows.Forms.Timer scanTimer = new System.Windows.Forms.Timer { Interval = 1200 };
    private readonly System.Windows.Forms.Timer previewTimer = new System.Windows.Forms.Timer { Interval = 100 };
    private readonly TaskbarPreviewSuppressor previews = new TaskbarPreviewSuppressor();
    private volatile TaskbarButton[] buttons = new TaskbarButton[0];
    private readonly TaskbarMouseGesture gesture = new TaskbarMouseGesture();
    private readonly MenuRequestState requests = new MenuRequestState();
    private readonly Native.MouseProc keyboardCallback;
    private int scanning;
    private int resolving;
    private IntPtr hook;
    private IntPtr keyboardHook;
    private volatile bool stopping;

    public SwitchForm()
    {
        ShowInTaskbar = false;
        Opacity = 0;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        Text = "显示器切换";
        callback = MouseHook;
        keyboardCallback = KeyboardHook;
        menu.Opened += delegate { previews.Dismiss(); previewTimer.Start(); Status("Menu opened"); };
        menu.Closed += delegate(object sender, ToolStripDropDownClosedEventArgs e) { requests.Cancel(); previewTimer.Stop(); Status("Menu closed; reason=" + e.CloseReason); };
        previewTimer.Tick += delegate
        {
            if (requests.Pending && !requests.IsCurrent(requests.Version, Environment.TickCount)) CancelMenu();
            if (requests.Pending || menu.Visible) previews.Dismiss();
            else previewTimer.Stop();
        };
        scanTimer.Tick += delegate { RefreshButtons(); };
        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("任务栏图标：按住右键，再点左键 → 选择显示器", null, delegate { });
        trayMenu.Items.Add("也可使用 Shift + 右键", null, delegate { });
        trayMenu.Items.Add("退出", null, delegate { Close(); });
        tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "显示器切换 · 按住右键再点左键 / Shift + 右键", ContextMenuStrip = trayMenu, Visible = true };
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Hide();
        RefreshButtons();
        scanTimer.Start();
        bool hotkey = Native.RegisterHotKey(Handle, 1, 0x1 | 0x2 | 0x4 | 0x4000, (uint)Keys.M);
        using (var process = Process.GetCurrentProcess())
        using (var module = process.MainModule)
        {
            hook = Native.SetWindowsHookEx(Native.HookLowLevelMouse, callback, Native.GetModuleHandle(module.ModuleName), 0);
            keyboardHook = Native.SetWindowsHookEx(Native.HookLowLevelKeyboard, keyboardCallback, Native.GetModuleHandle(module.ModuleName), 0);
        }
        if (hook == IntPtr.Zero || keyboardHook == IntPtr.Zero)
        {
            MessageBox.Show("无法启用任务栏鼠标监听。错误码：" + Marshal.GetLastWin32Error(), Text);
            Close();
            return;
        }
        Status("Started; hotkey=" + hotkey + "; mouseHook=True; escapeHook=True");
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == Native.WmHotkey && message.WParam == new IntPtr(1))
        {
            IntPtr foreground = Native.GetForegroundWindow();
            ResolveAndShow(new Native.Point(Cursor.Position.X, Cursor.Position.Y), null, foreground);
        }
        if (message.Msg == Native.WmDisplayChange) { buttons = new TaskbarButton[0]; CancelMenu(); RefreshButtons(); } // WM_DISPLAYCHANGE
        base.WndProc(ref message);
    }

    private IntPtr KeyboardHook(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && (message.ToInt32() == Native.WmKeyDown || message.ToInt32() == Native.WmSysKeyDown) && Marshal.ReadInt32(data) == (int)Keys.Escape)
            if (requests.Pending || menu.Visible) CancelMenu();
        return Native.CallNextHookEx(keyboardHook, code, message, data);
    }

    private void Post(Action action)
    {
        if (stopping || IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke((Action)delegate { if (!stopping) action(); }); }
        catch (InvalidOperationException) { } // The handle can disappear between checking and posting.
    }

    private void CancelMenu()
    {
        requests.Cancel();
        previewTimer.Stop();
        if (menu.Visible) menu.Close(ToolStripDropDownCloseReason.AppClicked);
    }

    private IntPtr MouseHook(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            int kind = message.ToInt32();
            if (kind != Native.WmLeftButtonDown && kind != Native.WmLeftButtonUp && kind != Native.WmRightButtonDown && kind != Native.WmRightButtonUp && kind != Native.WmMiddleButtonDown && kind != Native.WmXButtonDown)
                return Native.CallNextHookEx(hook, code, message, data);
            var mouse = (Native.MouseData)Marshal.PtrToStructure(data, typeof(Native.MouseData));
            // Our replay must reach Explorer without becoming another gesture.
            if ((mouse.Flags & 1) != 0 && mouse.Extra == Native.ReplayTag)
                return Native.CallNextHookEx(hook, code, message, data);
            var point = mouse.Point;
            if ((requests.Pending || menu.Visible) && (kind == Native.WmLeftButtonDown || kind == Native.WmRightButtonDown || kind == Native.WmMiddleButtonDown || kind == Native.WmXButtonDown))
                CloseMenuOutside(new Point(point.X, point.Y));

            TaskbarButton button = null;
            if (kind == Native.WmRightButtonDown || ((kind == Native.WmLeftButtonDown || kind == Native.WmRightButtonUp) && gesture.AwaitingChord))
            {
                button = buttons.FirstOrDefault(b => b.Bounds.Contains(point.X, point.Y));
                if (button != null && !TaskbarResolver.IsTaskbar(point)) button = null;
            }
            var action = gesture.Handle(kind, button, new Point(point.X, point.Y),
                (Native.GetAsyncKeyState(0x10) & 0x8000) != 0,
                (Native.GetAsyncKeyState(0x01) & 0x8000) != 0);
            if (action == TaskbarMouseAction.OpenMenu)
            {
                var selected = gesture.OpenButton;
                // UI Automation must never run inside the low-level mouse hook.
                // Start the request now so a following outside click can cancel it before dispatch.
                ResolveAndShow(point, selected, IntPtr.Zero);
            }
            if (action == TaskbarMouseAction.ReplayRightClick)
            {
                var selected = button;
                Post(delegate
                {
                    var cursor = Cursor.Position;
                    if (selected != null && selected.Bounds.Contains(cursor) &&
                        TaskbarResolver.IsTaskbar(new Native.Point(cursor.X, cursor.Y)))
                        Status("Native right click; replayed=" + Native.ReplayRightClick());
                });
            }
            if (action != TaskbarMouseAction.Forward) return new IntPtr(1);
        }
        return Native.CallNextHookEx(hook, code, message, data);
    }

    private void CloseMenuOutside(Point point)
    {
        if (!MenuContainsPoint(menu, point))
            // Invalidate pending results immediately; the click still reaches its target.
            CancelMenu();
    }

    private static bool MenuContainsPoint(ToolStripDropDown dropdown, Point point)
    {
        if (!dropdown.Visible) return false;
        if (dropdown.Bounds.Contains(point)) return true;
        foreach (ToolStripItem item in dropdown.Items)
        {
            var child = item as ToolStripDropDownItem;
            if (child != null && child.HasDropDownItems && MenuContainsPoint(child.DropDown, point)) return true;
        }
        return false;
    }

    private void RefreshButtons()
    {
        if (Interlocked.Exchange(ref scanning, 1) != 0) return;
        ThreadPool.QueueUserWorkItem(delegate
        {
            try { var fresh = TaskbarResolver.Buttons(); if (!stopping) buttons = fresh; }
            catch (ElementNotAvailableException) { buttons = new TaskbarButton[0]; }
            catch (InvalidOperationException) { buttons = new TaskbarButton[0]; }
            catch (COMException) { buttons = new TaskbarButton[0]; }
            finally { Interlocked.Exchange(ref scanning, 0); }
        });
    }

    private void ResolveAndShow(Native.Point point, TaskbarButton button, IntPtr foreground)
    {
        CancelMenu();
        if (stopping || Interlocked.CompareExchange(ref resolving, 1, 0) != 0) return;
        int request = requests.Begin(Environment.TickCount);
        Post(delegate { if (requests.IsCurrent(request, Environment.TickCount)) PrepareTaskbarMenu(); });
        ThreadPool.QueueUserWorkItem(delegate
        {
            List<WindowInfo> windows = null;
            string error = null;
            try
            {
                TaskbarButton actual = null;
                if (button != null)
                {
                    actual = TaskbarResolver.ButtonAt(point);
                    if (!TaskbarResolver.SameLiveButton(button, actual, new Point(point.X, point.Y)))
                        error = "任务栏图标位置已变化，请重新选择。";
                }
                if (error == null && requests.IsCurrent(request, Environment.TickCount))
                {
                    var all = WindowInfo.Enumerate();
                    windows = button != null ? TaskbarResolver.Match(actual, all)
                        : all.Where(w => w.Handle == foreground).ToList();
                }
            }
            catch (Exception ex) { error = "无法读取任务栏窗口：" + ex.Message; }
            finally { Interlocked.Exchange(ref resolving, 0); }
            Post(delegate
            {
                if (!requests.TryComplete(request, Environment.TickCount)) return;
                Status("Taskbar menu; matched=" + (windows == null ? 0 : windows.Count) + "; error=" + (error != null));
                ShowWindowMenu(windows ?? new List<WindowInfo>(), new Point(point.X, point.Y), error);
            });
        });
    }

    private void ShowWindowMenu(List<WindowInfo> windows, Point point, string error)
    {
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
        else menu.Items.Add("未识别到打开的窗口，请重新选择。").Enabled = false;
        Native.SetForegroundWindow(Handle);
        menu.Show(point);
    }

    private void PrepareTaskbarMenu()
    {
        previews.Begin();
        previewTimer.Start();
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
            string targetName = screen.DeviceName;
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
                if (!WindowMover.Move(window.Handle, targetName))
                    MessageBox.Show("窗口未能移到目标显示器，请确认窗口仍在运行、目标显示器仍已连接，然后重新选择。", Text);
            };
            items.Add(item);
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        stopping = true;
        requests.Cancel();
        if (hook != IntPtr.Zero) Native.UnhookWindowsHookEx(hook);
        if (keyboardHook != IntPtr.Zero) Native.UnhookWindowsHookEx(keyboardHook);
        Native.UnregisterHotKey(Handle, 1);
        scanTimer.Stop();
        scanTimer.Dispose();
        previewTimer.Stop();
        previewTimer.Dispose();
        tray.Visible = false;
        tray.ContextMenuStrip.Dispose();
        tray.Dispose();
        menu.Dispose();
        base.OnFormClosed(e);
    }
}
