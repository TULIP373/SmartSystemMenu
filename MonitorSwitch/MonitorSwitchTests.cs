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

    private sealed class MenuLessForm : Form
    {
        protected override CreateParams CreateParams
        {
            get { var value = base.CreateParams; value.Style &= ~0x80000; return value; }
        }
    }
}
