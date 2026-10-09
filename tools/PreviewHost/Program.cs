using System.Diagnostics;
using System.Drawing.Imaging;
using System.Windows.Forms;

// PreviewHost: checks PHOSPHOR's /p (preview) mode the way the Control Panel uses it.
// Usage: PreviewHost <path to Phosphor.exe or .scr> <output png path>
// Steps: 152x112 monitor window -> launch "<exe> /p <HWND>" -> wait 4 s -> capture client area
//        -> close window -> check that the process exits by itself within 3 s.

internal static class Program
{
    private const int PreviewW = 152;
    private const int PreviewH = 112;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.WriteLine("Usage: PreviewHost <Phosphor.exe|.scr> <out.png>");
            return 2;
        }
        string exe = Path.GetFullPath(args[0]);
        string png = Path.GetFullPath(args[1]);
        if (!File.Exists(exe))
        {
            Console.WriteLine($"FAIL: exe not found: {exe}");
            return 2;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(png)!);
        if (File.Exists(png)) File.Delete(png);

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();

        var form = new Form
        {
            Text = "PreviewHost monitor",
            FormBorderStyle = FormBorderStyle.FixedSingle,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(100, 100),
            TopMost = true,
            BackColor = Color.Black,
            AutoScaleMode = AutoScaleMode.None,
        };
        form.ClientSize = new Size(PreviewW, PreviewH);
        form.Show();
        PumpFor(300);

        Process? proc = null;
        bool pngSaved = false;
        double brightness = -1;
        bool exited = false;
        long exitMs = -1;
        int exitCode = int.MinValue;
        int rc = 1;
        try
        {
            long hwnd = form.Handle.ToInt64();
            Console.WriteLine($"Monitor window: client {PreviewW}x{PreviewH} at screen {form.PointToScreen(Point.Empty)}, HWND={hwnd}");

            var psi = new ProcessStartInfo(exe, $"/p {hwnd}") { UseShellExecute = false };
            proc = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
            Console.WriteLine($"Started: {exe} /p {hwnd}  (pid {proc.Id})");

            PumpFor(4000);

            // Capture client area by screen coordinates.
            Point origin = form.PointToScreen(Point.Empty);
            using (var bmp = new Bitmap(PreviewW, PreviewH, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(origin, Point.Empty, new Size(PreviewW, PreviewH));
                }
                bmp.Save(png, ImageFormat.Png);
                pngSaved = File.Exists(png);
                brightness = AverageLuma(bmp);
            }

            // Close the monitor window; Phosphor must notice the parent is gone and exit.
            form.Close();
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 3000)
            {
                Application.DoEvents();
                if (proc.HasExited) break;
                Thread.Sleep(20);
            }
            exited = proc.HasExited;
            if (exited) { exitMs = sw.ElapsedMilliseconds; exitCode = proc.ExitCode; }
        }
        catch (Exception e)
        {
            Console.WriteLine("ERROR: " + e.Message);
        }
        finally
        {
            if (proc is not null && !proc.HasExited)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
            }
            if (!form.IsDisposed) form.Dispose();
        }

        Console.WriteLine();
        Console.WriteLine($"PNG saved       : {(pngSaved ? "YES" : "NO")}  {png}");
        Console.WriteLine($"Avg brightness  : {brightness:F1} / 255  {(brightness < 2 ? "(~0: preview draws nothing?)" : "")}");
        Console.WriteLine($"Process exited  : {(exited ? $"YES, by itself after ~{exitMs} ms, exit code {exitCode}" : "NO (killed after 3 s)")}");

        rc = (pngSaved && brightness >= 2 && exited) ? 0 : 1;
        Console.WriteLine($"RESULT          : {(rc == 0 ? "OK" : "PROBLEMS FOUND")}");
        PrintLogTail(15);
        return rc;
    }

    private static void PumpFor(int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }
    }

    private static double AverageLuma(Bitmap bmp)
    {
        double sum = 0;
        int n = 0;
        for (int y = 0; y < bmp.Height; y++)
        {
            for (int x = 0; x < bmp.Width; x++)
            {
                Color c = bmp.GetPixel(x, y);
                sum += 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
                n++;
            }
        }
        return n == 0 ? 0 : sum / n;
    }

    private static void PrintLogTail(int lines)
    {
        try
        {
            var log = Directory.GetFiles(Path.GetTempPath(), "phosphor*.log")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTime)
                .FirstOrDefault();
            if (log is null) { Console.WriteLine("No phosphor*.log in %TEMP%"); return; }
            Console.WriteLine($"--- tail of {log.FullName} ---");
            var all = File.ReadAllLines(log.FullName, System.Text.Encoding.UTF8);
            foreach (var l in all.Skip(Math.Max(0, all.Length - lines))) Console.WriteLine(l);
        }
        catch (Exception e)
        {
            Console.WriteLine("Could not read log: " + e.Message);
        }
    }
}
