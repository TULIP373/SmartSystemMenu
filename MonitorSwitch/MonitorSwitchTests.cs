using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

internal static class MonitorSwitchTests
{
    private static int checks;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        checks++;
        Console.WriteLine("PASS: " + message);
    }

    [STAThread]
    private static void Main()
    {
        Native.SetProcessDpiAwarenessContext(new IntPtr(-4));
        var packaged = new WindowInfo { Title = "Test app", ProcessName = "ApplicationFrameHost", Product = "Host", ClassName = "ApplicationFrameWindow" };
        packaged.AppIds.Add("Test.Package!App");
        var unrelated = new WindowInfo { Title = "Test app", ProcessName = "Other", Product = "Other", ClassName = "Other" };
        var windows = new List<WindowInfo> { packaged, unrelated };
        var button = new TaskbarButton { AppId = "Test.Package!App", Label = "Test app" };
        Check(TaskbarResolver.Match(button, windows).SequenceEqual(new[] { packaged }), "Package identity wins over a colliding title");
        var second = new WindowInfo { Title = "Second window", ProcessName = "ApplicationFrameHost", Product = "Host", ClassName = "ApplicationFrameWindow" };
        second.AppIds.Add("Test.Package!App");
        windows.Add(second);
        Check(TaskbarResolver.Match(button, windows).Count == 2, "Grouped taskbar button returns all matching windows");
        Check(TaskbarResolver.Match(new TaskbarButton { AppId = "Unknown!App", Label = "Unknown" }, windows).Count == 0, "Unknown apps never select an unrelated foreground window");
        Check(TaskbarResolver.Match(new TaskbarButton { AppId = "", Label = "Second window" }, windows).Single() == second, "Exact window-title fallback works");
        CheckMouseGestures();
        Check(TaskbarPreviewSuppressor.IsTaskbarPreview("TaskListThumbnailWnd", 10, 20, 10, 20), "Classic taskbar thumbnails are recognized");
        Check(TaskbarPreviewSuppressor.IsTaskbarPreview("XamlExplorerHostIslandWindow", 10, 20, 10, 20), "Windows 11 taskbar preview hosts are recognized");
        Check(!TaskbarPreviewSuppressor.IsTaskbarPreview("XamlExplorerHostIslandWindow", 10, 21, 10, 20), "Alt-Tab and Task View on other Explorer threads are excluded");
        Check(!TaskbarPreviewSuppressor.IsTaskbarPreview("TaskListThumbnailWnd", 11, 20, 10, 20), "Other processes cannot be mistaken for taskbar previews");
        Check(!TaskbarPreviewSuppressor.IsTaskbarPreview("XamlExplorerHostIslandWindow_WASDK", 10, 20, 10, 20), "Desktop context-menu hosts are excluded");
        Check(!TaskbarPreviewSuppressor.IsTaskbarPreview("Shell_TrayWnd", 10, 20, 10, 20), "The taskbar itself is never hidden");
        Check(!TaskbarPreviewSuppressor.IsTaskbarPreview("XamlExplorerHostIslandWindow", 0, 0, 0, 0), "Invalid window identities are excluded");

        using (var form = new MenuLessForm())
        {
            form.Text = "MonitorSwitch synthetic test";
            form.Width = 420;
            form.Height = 240;
            form.Show();
            Application.DoEvents();
            var screens = Screen.AllScreens;
            Check(screens.Length >= 2, "Two connected displays are available for the movement check");
            Check(WindowInfo.Enumerate().Any(w => w.Handle == form.Handle), "A window without a system menu is discoverable");
            Check(WindowMover.Move(form.Handle, screens[0]), "Normal window moves to first display");
            Check(WindowMover.Move(form.Handle, screens[1]), "Normal window moves to second display");
            Check(Native.MonitorFromWindow(form.Handle, 2) == Native.MonitorFromPoint(new Native.Point(screens[1].Bounds.Left, screens[1].Bounds.Top), 2), "Native monitor identity confirms the move");
            form.WindowState = FormWindowState.Maximized;
            Application.DoEvents();
            Check(WindowMover.Move(form.Handle, screens[0]) && Native.IsZoomed(form.Handle), "Maximized window moves while remaining maximized");
            form.WindowState = FormWindowState.Minimized;
            Application.DoEvents();
            Check(WindowMover.Move(form.Handle, screens[1]) && !Native.IsIconic(form.Handle), "Minimized window is restored onto the chosen display");
            form.Close();
        }
        Console.WriteLine("Verified " + checks + " checks.");
    }

    private static void CheckMouseGestures()
    {
        var hit = new TaskbarButton { AppId = "Test.Package!App", Bounds = new System.Drawing.Rectangle(10, 10, 40, 40) };
        var point = new System.Drawing.Point(20, 20);
        var gesture = new TaskbarMouseGesture();
        Check(gesture.Handle(0x204, hit, point, false, false) == TaskbarMouseAction.Consume, "A taskbar right press waits for a possible chord");
        Check(gesture.Handle(0x205, hit, point, false, false) == TaskbarMouseAction.ReplayRightClick, "An ordinary right click requests the native menu");
        Check(gesture.Handle(0x205, hit, point, false, false) == TaskbarMouseAction.Forward, "Native replay is requested only once");

        foreach (bool leftReleasedFirst in new[] { true, false })
        {
            gesture = new TaskbarMouseGesture();
            gesture.Handle(0x204, hit, point, false, false);
            Check(gesture.Handle(0x201, hit, point, false, false) == TaskbarMouseAction.OpenMenu && gesture.OpenButton == hit,
                "Right then left opens the selected taskbar app menu");
            Check(gesture.Handle(leftReleasedFirst ? 0x202 : 0x205, hit, point, false, false) == TaskbarMouseAction.Consume,
                "The first chord release cannot activate a taskbar app or open a native menu");
            Check(gesture.Handle(leftReleasedFirst ? 0x205 : 0x202, hit, point, false, false) == TaskbarMouseAction.Consume,
                "The other chord release is also consumed");
            Check(gesture.Handle(0x201, null, point, false, false) == TaskbarMouseAction.Forward,
                "Subsequent left clicks reach menu items and other windows normally");
        }

        gesture = new TaskbarMouseGesture();
        Check(gesture.Handle(0x204, hit, point, true, false) == TaskbarMouseAction.OpenMenu, "Shift plus right click remains available");
        Check(gesture.Handle(0x205, hit, point, false, false) == TaskbarMouseAction.Consume, "Shift gesture release never opens the native menu too");

        gesture = new TaskbarMouseGesture();
        Check(gesture.Handle(0x204, null, point, false, false) == TaskbarMouseAction.Forward, "Right clicks outside taskbar app icons are untouched");
        Check(gesture.Handle(0x201, hit, point, false, false) == TaskbarMouseAction.Forward, "Moving onto an icon after pressing right elsewhere does not trigger a chord");
        Check(gesture.Handle(0x204, hit, point, false, true) == TaskbarMouseAction.Forward, "Left then right is not mistaken for right then left");

        gesture = new TaskbarMouseGesture();
        gesture.Handle(0x204, hit, point, false, false);
        Check(gesture.Handle(0x201, null, new System.Drawing.Point(80, 80), false, false) == TaskbarMouseAction.Forward,
            "An outside left click cancels the candidate and still reaches its target");
        Check(gesture.Handle(0x205, hit, point, false, false) == TaskbarMouseAction.Consume, "A cancelled chord does not reopen a menu on release");

        gesture = new TaskbarMouseGesture();
        gesture.Handle(0x204, hit, point, false, false);
        var another = new TaskbarButton { AppId = "Another!App", Bounds = hit.Bounds };
        Check(gesture.Handle(0x201, another, point, false, false) == TaskbarMouseAction.Forward, "A changed taskbar app is never selected by a stale press");

        gesture = new TaskbarMouseGesture();
        gesture.Handle(0x204, hit, point, false, false);
        var refreshed = new TaskbarButton { AppId = hit.AppId, Bounds = hit.Bounds };
        Check(gesture.Handle(0x201, refreshed, point, false, false) == TaskbarMouseAction.OpenMenu,
            "Refreshing the taskbar cache while right is held does not break the chord");
        Check(System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.Input)) == 40,
            "Native INPUT layout matches the x64 SendInput API");
    }

    private sealed class MenuLessForm : Form
    {
        protected override CreateParams CreateParams
        {
            get { var value = base.CreateParams; value.Style &= ~0x80000; return value; }
        }
    }
}
