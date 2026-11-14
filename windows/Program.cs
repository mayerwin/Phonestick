using System;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace Phonestick
{
    internal static class Program
    {
        /// <summary>
        /// Phonestick.exe          starts the tray icon.
        /// Phonestick.exe --open   opens the phone's files (starting the tray icon if needed),
        ///                       handy for a desktop or Start menu shortcut.
        /// </summary>
        [STAThread]
        private static int Main(string[] args)
        {
            // Internal: the "ssh" command rclone runs to reach the phone's file server.
            if (args.Length >= 2 && args[0] == "--pipe")
                return AdbPipe.Run(args[1], "sh " + Mount.ServerScript);

            // Internal: renders the tray glyph at every size to a PNG sheet, for design review.
            if (args.Length >= 2 && args[0] == "--tray-preview")
            {
                TrayPreview(args[1]);
                return 0;
            }

            // Never the .NET crash dialog: errors on other threads are logged too.
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Write("Fatal error: " + e.ExceptionObject);
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) => { Log.Write("Background error: " + e.Exception); e.SetObserved(); };

            // Internal: started by the updater. Wait for the previous process to exit (it holds the
            // tray mutex). The update folder that holds the previous exe is emptied below.
            int au = Array.IndexOf(args, "--after-update");
            if (au >= 0 && au + 1 < args.Length && int.TryParse(args[au + 1], out var oldPid))
            {
                try { using (var old = System.Diagnostics.Process.GetProcessById(oldPid)) old.WaitForExit(20000); } catch { }
            }

            bool open = args.Any(a => a.Equals("--open", StringComparison.OrdinalIgnoreCase));
            using (var openSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\" + AppInfo.Name + "-open"))
            using (var mutex = new Mutex(true, "Local\\" + AppInfo.Name + "-tray", out bool first))
            {
                if (!first)
                {
                    // Already running in the tray: ask it to open the phone.
                    if (open) openSignal.Set();
                    return 0;
                }
                // Only the instance that owns the tray: another start (a shortcut, autostart) must
                // not empty the update folder while this one downloads into it.
                Updater.Cleanup();
                // A drive left mounted by a Phonestick that was killed blocks the letter: end it
                // before anything can mount.
                Mount.KillStale();
                // A tray app must never show the .NET crash dialog: log a UI-thread error and keep going.
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (s, e) => Log.Write("Unexpected error: " + e.Exception);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                var tray = new TrayApp(openSignal, open);
                // --panel opens the panel at start; --settings opens it on settings.
                // --stay keeps it open when it loses focus (for screenshots).
                bool Has(string a) => args.Any(x => x.Equals(a, StringComparison.OrdinalIgnoreCase));
                Flyout.StayOpen = Has("--stay");
                TrayApp.Demo = Has("--demo");
                TrayApp.DemoIdle = Has("--demo-idle");
                if (Has("--panel") || Has("--settings")) tray.ShowPanelSoon(Has("--settings"));
                // --update-now: checks GitHub and installs a newer release, if any.
                if (Has("--update-now")) tray.UpdateNowSoon();
                Application.Run(tray);
            }
            return 0;
        }
    

        private static void TrayPreview(string path)
        {
            int[] sizes = { 16, 20, 24, 28, 32, 40 };
            const int zoom = 4;
            int cell = 40 * zoom + 16;
            using (var sheet = new System.Drawing.Bitmap(cell * sizes.Length * 3 + 16, (cell + 60) * 2))
            using (var g = System.Drawing.Graphics.FromImage(sheet))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                for (int row = 0; row < 2; row++)
                {
                    bool dark = row == 0;
                    var bg = dark ? System.Drawing.Color.FromArgb(0x20, 0x20, 0x20) : System.Drawing.Color.FromArgb(0xEE, 0xEE, 0xEE);
                    using (var b = new System.Drawing.SolidBrush(bg)) g.FillRectangle(b, 0, row * (cell + 60), sheet.Width, cell + 60);
                    int x = 8;
                    foreach (Icons.TrayState st in new[] { Icons.TrayState.Idle, Icons.TrayState.On, Icons.TrayState.Mounted })
                        foreach (var sz in sizes)
                            using (var bmp = Icons.TrayBitmap(st, sz, dark))
                            {
                                g.DrawImage(bmp, x, row * (cell + 60) + 8, sz * zoom, sz * zoom);
                                g.DrawImageUnscaled(bmp, x, row * (cell + 60) + 8 + cell);
                                x += sz * zoom + 12;
                            }
                }
                sheet.Save(path);
            }
        }
    }
}
