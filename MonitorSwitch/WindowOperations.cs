using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Windows.Forms;

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
        return screen != null && Move(hwnd, screen.DeviceName);
    }

    internal static Screen FindTarget(string deviceName, Screen[] available)
    {
        return available.FirstOrDefault(s => string.Equals(s.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
    }

    public static bool Move(IntPtr hwnd, string deviceName)
    {
        var screen = FindTarget(deviceName, Screen.AllScreens);
        if (screen == null || !Native.IsWindow(hwnd)) return false;
        IntPtr target = Native.MonitorFromPoint(new Native.Point(screen.Bounds.Left, screen.Bounds.Top), 0);
        if (target == IntPtr.Zero) return false;
        if (Native.MonitorFromWindow(hwnd, 2) == target)
        {
            if (Native.IsIconic(hwnd)) Native.ShowWindow(hwnd, 9);
            return true;
        }
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
