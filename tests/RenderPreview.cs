using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class RenderPreview
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length < 2 || args.Length > 5)
        {
            Console.Error.WriteLine("Usage: RenderPreview.exe <widget-exe> <output-png> [left|right] [expanded|collapsed] [hover]");
            return 2;
        }

        try
        {
            Assembly assembly = Assembly.LoadFrom(args[0]);
            Type windowType = assembly.GetType("SafeCodexQuotaWidget.WpfQuotaWindow", true);
            Application application = new Application();
            application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Window window = (Window)Activator.CreateInstance(windowType, true);
            bool expandsLeft = args.Length < 3 || !string.Equals(args[2], "left", StringComparison.OrdinalIgnoreCase);
            bool expanded = args.Length >= 4 && string.Equals(args[3], "expanded", StringComparison.OrdinalIgnoreCase);
            bool hover = args.Length >= 5 && string.Equals(args[4], "hover", StringComparison.OrdinalIgnoreCase);
            window.ShowInTaskbar = false;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = 10;
            window.Top = 10;

            DispatcherTimer timer = new DispatcherTimer();
            timer.Interval = TimeSpan.FromSeconds(7);
            timer.Tick += delegate
            {
                timer.Stop();
                FrameworkElement content = window.Content as FrameworkElement;
                if (content == null) throw new InvalidOperationException("WPF window content was not renderable.");
                if (hover)
                {
                    MethodInfo animateHover = windowType.GetMethod("AnimateExpandedHover",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    if (animateHover == null) throw new MissingMethodException(windowType.FullName, "AnimateExpandedHover");
                    animateHover.Invoke(window, new object[] { true, 0 });
                    FieldInfo gaugeField = windowType.GetField("_gauge",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    object gauge = gaugeField == null ? null : gaugeField.GetValue(window);
                    MethodInfo setGaugeHover = gauge == null ? null : gauge.GetType().GetMethod("SetHover");
                    if (setGaugeHover == null) throw new MissingMethodException(windowType.FullName, "_gauge.SetHover");
                    setGaugeHover.Invoke(gauge, new object[] { true, 0 });
                    content.UpdateLayout();
                }
                int width = Math.Max(1, (int)Math.Ceiling(content.ActualWidth));
                int height = Math.Max(1, (int)Math.Ceiling(content.ActualHeight));
                RenderTargetBitmap bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(content);
                PngBitmapEncoder encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (FileStream stream = File.Create(args[1]))
                {
                    encoder.Save(stream);
                }
                window.Close();
                application.Shutdown(0);
            };

            window.Loaded += delegate
            {
                MethodInfo setExpansionSide = windowType.GetMethod("SetExpansionSide",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (setExpansionSide == null) throw new MissingMethodException(windowType.FullName, "SetExpansionSide");
                setExpansionSide.Invoke(window, new object[] { expandsLeft, true });

                if (expanded)
                {
                    MethodInfo animateWindow = windowType.GetMethod("AnimateWindowAsync",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    if (animateWindow == null) throw new MissingMethodException(windowType.FullName, "AnimateWindowAsync");
                    animateWindow.Invoke(window, new object[] { true });
                }
                timer.Start();
            };
            application.Run(window);
            return 0;
        }
        catch (Exception ex)
        {
            Exception detail = ex.GetBaseException();
            Console.Error.WriteLine(detail.GetType().FullName + ": " + detail.Message);
            Console.Error.WriteLine(detail.StackTrace);
            return 1;
        }
    }
}
