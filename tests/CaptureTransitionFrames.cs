using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

internal static class CaptureTransitionFrames
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width { get { return Right - Left; } }
        public int Height { get { return Bottom - Top; } }
    }

    private sealed class CapturedFrame : IDisposable
    {
        public Bitmap Image;
        public NativeRect WindowRect;
        public long ElapsedMilliseconds;

        public void Dispose()
        {
            if (Image != null) Image.Dispose();
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string windowName);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr window, IntPtr destination, uint flags);

    private static int Main(string[] args)
    {
        string crashDirectory = args.Length >= 1
            ? Path.GetFullPath(args[0])
            : AppDomain.CurrentDomain.BaseDirectory;
        try { Directory.CreateDirectory(crashDirectory); }
        catch { crashDirectory = AppDomain.CurrentDomain.BaseDirectory; }
        AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs eventArgs)
        {
            try
            {
                File.WriteAllText(
                    Path.Combine(crashDirectory, "capture-error.txt"),
                    Convert.ToString(eventArgs.ExceptionObject, CultureInfo.InvariantCulture),
                    Encoding.UTF8);
            }
            catch { }
        };

        if (args.Length != 2 || (args[1] != "expand" && args[1] != "collapse"))
        {
            Console.Error.WriteLine("Usage: CaptureTransitionFrames.exe <new-output-directory> <expand|collapse>");
            return 2;
        }
        bool expanding = args[1] == "expand";

        string outputDirectory = Path.GetFullPath(args[0]);
        if (Directory.Exists(outputDirectory) && Directory.GetFileSystemEntries(outputDirectory).Length != 0)
        {
            Console.Error.WriteLine("Output directory must be new or empty: " + outputDirectory);
            return 3;
        }
        Directory.CreateDirectory(outputDirectory);
        StreamWriter statusWriter = new StreamWriter(
            Path.Combine(outputDirectory, "capture-status.txt"),
            false,
            Encoding.UTF8);
        statusWriter.AutoFlush = true;
        Console.SetOut(statusWriter);
        Console.SetError(statusWriter);
        Console.WriteLine("Started " + DateTime.Now.ToString("O", CultureInfo.InvariantCulture) +
                          " mode=" + (expanding ? "expand" : "collapse"));

        SetProcessDPIAware();
        IntPtr window = WaitForWindow("安全版 Codex 额度", TimeSpan.FromSeconds(8));
        if (window == IntPtr.Zero)
        {
            Console.Error.WriteLine("Widget window was not found.");
            return 4;
        }

        NativeRect initialRect;
        if (!GetWindowRect(window, out initialRect))
        {
            Console.Error.WriteLine("Could not read the initial widget bounds.");
            return 5;
        }
        Console.WriteLine("InitialRect=" + initialRect.Left + "," + initialRect.Top + "," +
                          initialRect.Width + "," + initialRect.Height);

        const int frameIntervalMilliseconds = 16;
        const int preTriggerFrameCount = 42;
        const int postTriggerFrameCount = 32;
        TimeSpan maximumWait = TimeSpan.FromSeconds(15);
        List<CapturedFrame> frames = new List<CapturedFrame>();
        int triggerIndex = -1;
        Stopwatch stopwatch = Stopwatch.StartNew();
        long nextFrameAt = 0;

        try
        {
            while (stopwatch.Elapsed < maximumWait)
            {
                NativeRect currentRect;
                if (!GetWindowRect(window, out currentRect)) break;

                CapturedFrame frame = new CapturedFrame();
                frame.ElapsedMilliseconds = stopwatch.ElapsedMilliseconds;
                frame.WindowRect = currentRect;
                frame.Image = CaptureWindow(window, currentRect);
                frames.Add(frame);

                bool pixelsChanged = frames.Count >= 6 && ImageChanged(frames[frames.Count - 2].Image, frame.Image);
                if (triggerIndex < 0 && (RectChanged(initialRect, currentRect) || pixelsChanged))
                {
                    triggerIndex = frames.Count - 1;
                }

                if (triggerIndex < 0 && frames.Count > preTriggerFrameCount)
                {
                    frames[0].Dispose();
                    frames.RemoveAt(0);
                }
                else if (triggerIndex >= 0 && frames.Count - triggerIndex > postTriggerFrameCount)
                {
                    break;
                }

                nextFrameAt += frameIntervalMilliseconds;
                int delay = (int)Math.Max(0, nextFrameAt - stopwatch.ElapsedMilliseconds);
                if (delay > 0) Thread.Sleep(delay);
            }

            if (triggerIndex < 0)
            {
                Console.Error.WriteLine("No widget bounds change was detected within 15 seconds.");
                return 6;
            }

            SaveFrames(outputDirectory, frames, triggerIndex, expanding);
            Console.WriteLine("Mode=" + (expanding ? "expand" : "collapse"));
            Console.WriteLine("TriggerIndex=" + triggerIndex);
            Console.WriteLine("Frames=" + frames.Count);
            Console.WriteLine("ContactSheet=" + Path.Combine(outputDirectory, "contact-sheet.png"));
            return 0;
        }
        finally
        {
            foreach (CapturedFrame frame in frames) frame.Dispose();
        }
    }

    private static IntPtr WaitForWindow(string title, TimeSpan timeout)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            IntPtr window = FindWindow(null, title);
            if (window != IntPtr.Zero) return window;
            Thread.Sleep(100);
        }
        return IntPtr.Zero;
    }

    private static bool RectChanged(NativeRect first, NativeRect second)
    {
        return Math.Abs(first.Left - second.Left) > 1 ||
               Math.Abs(first.Top - second.Top) > 1 ||
               Math.Abs(first.Width - second.Width) > 1 ||
               Math.Abs(first.Height - second.Height) > 1;
    }

    private static Bitmap CaptureWindow(IntPtr window, NativeRect rect)
    {
        Bitmap bitmap = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            try
            {
                graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, bitmap.Size, CopyPixelOperation.SourceCopy);
            }
            catch
            {
                IntPtr destination = graphics.GetHdc();
                try { PrintWindow(window, destination, 2); }
                finally { graphics.ReleaseHdc(destination); }
            }
        }
        return bitmap;
    }

    private static bool ImageChanged(Bitmap previous, Bitmap current)
    {
        const int sampleStep = 8;
        long difference = 0;
        int samples = 0;
        int width = Math.Min(previous.Width, current.Width);
        int height = Math.Min(previous.Height, current.Height);
        Rectangle bounds = new Rectangle(0, 0, width, height);
        BitmapData previousData = null;
        BitmapData currentData = null;
        try
        {
            previousData = previous.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            currentData = current.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            int previousStride = Math.Abs(previousData.Stride);
            int currentStride = Math.Abs(currentData.Stride);
            byte[] previousBytes = new byte[previousStride * height];
            byte[] currentBytes = new byte[currentStride * height];
            Marshal.Copy(previousData.Scan0, previousBytes, 0, previousBytes.Length);
            Marshal.Copy(currentData.Scan0, currentBytes, 0, currentBytes.Length);
            for (int y = 0; y < height; y += sampleStep)
            {
                int previousRow = (previousData.Stride >= 0 ? y : height - 1 - y) * previousStride;
                int currentRow = (currentData.Stride >= 0 ? y : height - 1 - y) * currentStride;
                for (int x = 0; x < width; x += sampleStep)
                {
                    int previousOffset = previousRow + x * 4;
                    int currentOffset = currentRow + x * 4;
                    difference += Math.Abs(previousBytes[previousOffset] - currentBytes[currentOffset]) +
                                  Math.Abs(previousBytes[previousOffset + 1] - currentBytes[currentOffset + 1]) +
                                  Math.Abs(previousBytes[previousOffset + 2] - currentBytes[currentOffset + 2]);
                    samples++;
                }
            }
        }
        finally
        {
            if (previousData != null) previous.UnlockBits(previousData);
            if (currentData != null) current.UnlockBits(currentData);
        }
        return samples > 0 && difference / (double)(samples * 3) >= 2.2;
    }

    private static void SaveFrames(string outputDirectory, List<CapturedFrame> frames, int triggerIndex, bool expanding)
    {
        StringBuilder metadata = new StringBuilder();
        metadata.AppendLine("index,relative_to_trigger,elapsed_ms,left,top,width,height");
        for (int i = 0; i < frames.Count; i++)
        {
            string name = "frame_" + i.ToString("000", CultureInfo.InvariantCulture) + ".png";
            frames[i].Image.Save(Path.Combine(outputDirectory, name), ImageFormat.Png);
            NativeRect rect = frames[i].WindowRect;
            metadata.AppendLine(string.Join(",", new[]
            {
                i.ToString(CultureInfo.InvariantCulture),
                (i - triggerIndex).ToString(CultureInfo.InvariantCulture),
                frames[i].ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture),
                rect.Left.ToString(CultureInfo.InvariantCulture),
                rect.Top.ToString(CultureInfo.InvariantCulture),
                rect.Width.ToString(CultureInfo.InvariantCulture),
                rect.Height.ToString(CultureInfo.InvariantCulture)
            }));
        }
        File.WriteAllText(Path.Combine(outputDirectory, "frames.csv"), metadata.ToString(), Encoding.UTF8);

        int first = Math.Max(0, triggerIndex - 3);
        int last = Math.Min(frames.Count - 1, triggerIndex + 27);
        const int columns = 6;
        int count = last - first + 1;
        int rows = (count + columns - 1) / columns;
        int tileWidth = frames[0].Image.Width;
        int tileHeight = frames[0].Image.Height + 22;
        using (Bitmap sheet = new Bitmap(tileWidth * columns, tileHeight * rows, PixelFormat.Format32bppArgb))
        using (Graphics graphics = Graphics.FromImage(sheet))
        using (Font font = new Font("Segoe UI", 10, FontStyle.Regular, GraphicsUnit.Pixel))
        {
            graphics.Clear(Color.FromArgb(20, 24, 34));
            for (int i = first; i <= last; i++)
            {
                int slot = i - first;
                int x = (slot % columns) * tileWidth;
                int y = (slot / columns) * tileHeight;
                graphics.DrawImageUnscaled(frames[i].Image, x, y + 22);
                string label = "f" + i.ToString("000") + "  " +
                               (i - triggerIndex >= 0 ? "+" : "") +
                               (i - triggerIndex).ToString(CultureInfo.InvariantCulture) + "  " +
                               frames[i].ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + "ms";
                graphics.DrawString(label, font, Brushes.White, x + 4, y + 4);
            }
            sheet.Save(Path.Combine(outputDirectory, "contact-sheet.png"), ImageFormat.Png);
        }
    }
}
