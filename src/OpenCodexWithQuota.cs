using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace SafeCodexQuotaWidget
{
    internal static class OpenCodexWithQuota
    {
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                string widgetPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SafeCodexQuotaWidget.exe");
                if (!File.Exists(widgetPath))
                    throw new FileNotFoundException("没有找到额度悬浮窗。", widgetPath);

                if (!Process.GetProcessesByName("SafeCodexQuotaWidget").Any())
                {
                    ProcessStartInfo widget = new ProcessStartInfo();
                    widget.FileName = widgetPath;
                    widget.UseShellExecute = false;
                    widget.WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory;
                    Process.Start(widget);
                    Thread.Sleep(250);
                }

                string uri = args.Length > 0 ? args[0] : "codex://";
                if (!uri.StartsWith("codex://", StringComparison.OrdinalIgnoreCase) || uri.IndexOfAny(new[] { '\r', '\n', '"' }) >= 0)
                    uri = "codex://";

                string explorerPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                ProcessStartInfo codex = new ProcessStartInfo();
                codex.FileName = explorerPath;
                codex.Arguments = "\"" + uri + "\"";
                codex.UseShellExecute = false;
                Process.Start(codex);
                return 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "无法打开 Codex 和额度窗", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
    }
}
