using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
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
            int index = Array.IndexOf(args, "--diagnose") + 1;
            if (index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
            {
                MessageBox.Show("请在 --diagnose 后指定诊断文件路径。", "显示器切换");
                return;
            }
            File.WriteAllLines(args[index], TaskbarResolver.Diagnose(), Encoding.UTF8);
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
