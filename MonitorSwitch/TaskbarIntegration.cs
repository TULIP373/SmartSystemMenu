using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Automation;

internal sealed class TaskbarPreviewSuppressor
{
    private readonly Dictionary<uint, int> taskbarThreads = new Dictionary<uint, int>();

    public void Begin()
    {
        taskbarThreads.Clear();
        Native.EnumWindows(delegate(IntPtr hwnd, IntPtr data)
        {
            string name = Native.ClassName(hwnd);
            if (name != "Shell_TrayWnd" && name != "Shell_SecondaryTrayWnd") return true;
            int process;
            uint thread = Native.GetWindowThreadProcessId(hwnd, out process);
            if (thread == 0) return true;
            taskbarThreads[thread] = process;
            // Cancel the old hover before Explorer's delayed preview can reappear.
            Native.PostMessage(hwnd, Native.WmMouseLeave, IntPtr.Zero, IntPtr.Zero); // WM_MOUSELEAVE
            Native.EnumChildWindows(hwnd, delegate(IntPtr child, IntPtr ignored)
            {
                int childProcess;
                if (Native.GetWindowThreadProcessId(child, out childProcess) == thread && childProcess == process)
                    Native.PostMessage(child, Native.WmMouseLeave, IntPtr.Zero, IntPtr.Zero);
                return true;
            }, IntPtr.Zero);
            return true;
        }, IntPtr.Zero);
        Dismiss();
    }

    public void Dismiss()
    {
        // New Windows 11 preview hosts can be omitted by EnumWindows even while
        // visible. FindWindowEx also sees these taskbar-owned XAML top-level hosts.
        foreach (string name in new[] { "TaskListThumbnailWnd", "XamlExplorerHostIslandWindow" })
        {
            IntPtr hwnd = IntPtr.Zero;
            while ((hwnd = Native.FindWindowEx(IntPtr.Zero, hwnd, name, null)) != IntPtr.Zero)
            {
                if (!Native.IsWindowVisible(hwnd)) continue;
                int process, expected;
                uint thread = Native.GetWindowThreadProcessId(hwnd, out process);
                if (taskbarThreads.TryGetValue(thread, out expected) && IsTaskbarPreview(name, process, thread, expected, thread))
                    Native.ShowWindowAsync(hwnd, 0); // SW_HIDE; never wait for Explorer's UI thread.
            }
        }
    }

    internal static bool IsTaskbarPreview(string name, int process, uint thread, int taskbarProcess, uint taskbarThread)
    {
        // Alt+Tab and Task View can use the same XAML class on other threads.
        return (name == "TaskListThumbnailWnd" || name == "XamlExplorerHostIslandWindow") &&
            process != 0 && thread != 0 && process == taskbarProcess && thread == taskbarThread;
    }
}

internal sealed class MenuRequestState
{
    internal const int TimeoutMilliseconds = 5000;
    private volatile int version;
    private volatile bool pending;
    private int started;
    public int Version { get { return version; } }
    public bool Pending { get { return pending; } }

    public int Begin(int now)
    {
        pending = false;
        started = now;
        version = unchecked(version + 1);
        pending = true;
        return version;
    }

    public bool IsCurrent(int request, int now)
    {
        return pending && version == request && unchecked((uint)(now - started)) < TimeoutMilliseconds;
    }

    public bool TryComplete(int request, int now)
    {
        if (!IsCurrent(request, now)) return false;
        pending = false;
        return true;
    }

    public void Cancel() { pending = false; version = unchecked(version + 1); }
}

internal enum TaskbarMouseAction { Forward, Consume, OpenMenu, ReplayRightClick }

internal sealed class TaskbarMouseGesture
{
    private TaskbarButton rightButton;
    private bool consumeRightUp;
    private bool consumeLeftUp;
    public bool AwaitingChord { get { return rightButton != null; } }
    public TaskbarButton OpenButton { get; private set; }

    public TaskbarMouseAction Handle(int message, TaskbarButton hit, Point point, bool shift, bool leftDown)
    {
        OpenButton = null;
        if (message == Native.WmRightButtonDown)
        {
            if (hit == null || leftDown) return TaskbarMouseAction.Forward;
            consumeRightUp = true;
            if (shift)
            {
                OpenButton = hit;
                return TaskbarMouseAction.OpenMenu;
            }
            rightButton = hit;
            return TaskbarMouseAction.Consume;
        }
        if (message == Native.WmLeftButtonDown && rightButton != null)
        {
            var candidate = rightButton;
            rightButton = null;
            if (!SameButton(candidate, hit, point)) return TaskbarMouseAction.Forward;
            consumeLeftUp = true;
            OpenButton = candidate;
            return TaskbarMouseAction.OpenMenu;
        }
        if (message == Native.WmRightButtonUp && consumeRightUp)
        {
            consumeRightUp = false;
            var candidate = rightButton;
            rightButton = null;
            return candidate != null && SameButton(candidate, hit, point)
                ? TaskbarMouseAction.ReplayRightClick : TaskbarMouseAction.Consume;
        }
        if (message == Native.WmLeftButtonUp && consumeLeftUp)
        {
            consumeLeftUp = false;
            return TaskbarMouseAction.Consume;
        }
        return TaskbarMouseAction.Forward;
    }

    private static bool SameButton(TaskbarButton candidate, TaskbarButton hit, Point point)
    {
        return hit != null && candidate.Bounds.Contains(point) && hit.Bounds.Contains(point) &&
            string.Equals(candidate.AppId, hit.AppId, StringComparison.OrdinalIgnoreCase);
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
    public static TaskbarButton ButtonAt(Native.Point point)
    {
        IntPtr root = Native.GetAncestor(Native.WindowFromPoint(point), 2);
        string name = Native.ClassName(root);
        if (name != "Shell_TrayWnd" && name != "Shell_SecondaryTrayWnd") return null;
        // FromPoint doesn't expose taskbar buttons on some Windows 11 builds.
        // Read a fresh snapshot of this taskbar instead of trusting the idle cache.
        var live = new List<TaskbarButton>();
        AddButtons(root, live);
        return live.FirstOrDefault(b => b.Bounds.Contains(point.X, point.Y));
    }

    internal static bool SameLiveButton(TaskbarButton expected, TaskbarButton actual, Point point)
    {
        return expected != null && actual != null && !string.IsNullOrEmpty(actual.AppId) && actual.Bounds.Contains(point) &&
            string.Equals(expected.AppId, actual.AppId, StringComparison.OrdinalIgnoreCase);
    }

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
            AddButtons(hwnd, result);
            return true;
        }, IntPtr.Zero);
        return result.ToArray();
    }

    private static void AddButtons(IntPtr hwnd, List<TaskbarButton> result)
    {
        var elements = AutomationElement.FromHandle(hwnd).FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
        foreach (AutomationElement element in elements)
        {
            var current = element.Current;
            if (!(current.AutomationId ?? "").StartsWith("Appid:", StringComparison.OrdinalIgnoreCase)) continue;
            var rect = current.BoundingRectangle;
            if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0) continue;
            result.Add(new TaskbarButton { AppId = current.AutomationId.Substring(6).Trim(), Label = Label(current.Name),
                Bounds = new Rectangle((int)rect.X, (int)rect.Y, (int)rect.Width, (int)rect.Height) });
        }
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
        return windows.Where(w =>
            (string.IsNullOrEmpty(button.AppId) || w.AppIds.Count == 0) &&
            (string.Equals(w.Title, button.Label, StringComparison.OrdinalIgnoreCase)
            || string.Equals(w.ProcessName, button.Label, StringComparison.OrdinalIgnoreCase)
            || string.Equals(w.Product, button.Label, StringComparison.OrdinalIgnoreCase))
            || (button.AppId == "Microsoft.Windows.Explorer" && w.ClassName == "CabinetWClass")).ToList();
    }

    public static string[] Diagnose()
    {
        var lines = new List<string>();
        var windows = WindowInfo.Enumerate();
        foreach (var button in Buttons())
        {
            var matches = Match(button, windows);
            lines.Add(button.Label + " | " + button.AppId + " | matched=" + matches.Count + " | "
                + string.Join(", ", matches.Select(w => w.ProcessName + ":" + w.Handle)));
        }
        return lines.ToArray();
    }
}
