using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace GpuMonitor
{
    static class Program
    {
        public static string BaseDir = AppDomain.CurrentDomain.BaseDirectory;
        static readonly object logLock = new object();

        [DllImport("kernel32.dll")]
        static extern bool AttachConsole(int processId);

        // Usage:
        //   GpuMonitor.exe                 run in the system tray
        //   GpuMonitor.exe check           one-time health check (text file)
        //   GpuMonitor.exe report          build the HTML report
        //   add --no-open to check or report in order to not open the result
        [STAThread]
        static int Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                LogError("Unhandled", e.ExceptionObject as Exception);
            };

            string cmd = args.Length > 0 ? args[0].ToLowerInvariant().TrimStart('-', '/') : "run";
            bool open = Array.IndexOf(args, "--no-open") < 0;

            if (cmd == "check")
            {
                string path = Checks.WriteTextReport(BaseDir);
                Print("Health check written to " + path);
                if (open) Process.Start("notepad.exe", "\"" + path + "\"");
                return 0;
            }
            if (cmd == "report")
            {
                string path = Report.Build(BaseDir);
                Print("Report written to " + path);
                if (open) Process.Start(path);
                return 0;
            }

            bool createdNew;
            using (var mutex = new Mutex(true, @"Local\GpuMonitor_SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show("GPU Monitor is already running in the system tray.", "GPU Monitor",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 1;
                }
                try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayContext(BaseDir));
            }
            return 0;
        }

        static void Print(string text)
        {
            // This is a GUI program, so console output only appears when it
            // was started from a terminal.
            if (AttachConsole(-1))
            {
                Console.WriteLine();
                Console.WriteLine(text);
            }
        }

        public static void LogError(string where, Exception ex)
        {
            try
            {
                lock (logLock)
                {
                    string dir = Path.Combine(BaseDir, "logs");
                    Directory.CreateDirectory(dir);
                    File.AppendAllText(Path.Combine(dir, "errors.txt"),
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + where + ": " +
                        (ex == null ? "(null)" : ex.ToString()) + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { }
        }
    }

    class TrayContext : ApplicationContext
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValue = "GpuMonitor";

        readonly string baseDir;
        readonly SystemStats sys;
        readonly Recorder recorder;
        readonly Heartbeat heartbeat;
        readonly Sampler sampler;
        readonly EventWatch events;
        readonly MessageWindow window;
        readonly NotifyIcon tray;
        readonly ToolStripMenuItem statusItem;
        readonly ToolStripMenuItem startupItem;
        readonly System.Windows.Forms.Timer uiTimer;
        readonly Icon iconOk;
        readonly Icon iconAlert;
        string lastDisplays;
        bool cleanedUp;

        public TrayContext(string baseDir)
        {
            this.baseDir = baseDir;
            Nvml.Init();
            sys = new SystemStats();
            recorder = new Recorder(baseDir, sys);
            recorder.CleanupOldLogs();

            heartbeat = new Heartbeat();
            heartbeat.Freeze += OnFreeze;
            heartbeat.Start();

            sampler = new Sampler(sys, heartbeat, recorder);
            sampler.Start();

            events = new EventWatch(recorder);
            events.Start();

            lastDisplays = DisplayInfo.DescribeJoined();
            window = new MessageWindow();
            window.DisplayChanged += OnDisplayChanged;
            window.MonitorDevice += OnMonitorDevice;
            window.HotkeyPressed += delegate { MarkFlicker(Hotkey.Label); };
            bool hotkeyOk = window.RegisterFlickerHotkey();

            iconOk = MakeIcon(Color.FromArgb(46, 160, 67));
            iconAlert = MakeIcon(Color.FromArgb(230, 140, 20));

            var menu = new ContextMenuStrip();
            statusItem = new ToolStripMenuItem("Starting...");
            statusItem.Enabled = false;
            menu.Items.Add(statusItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Mark flicker now (" + Hotkey.Label + ")", null, delegate { MarkFlicker("tray menu"); });
            menu.Items.Add("Build and open report", null, delegate { RunInBackground("report"); });
            menu.Items.Add("Run health check", null, delegate { RunInBackground("check"); });
            menu.Items.Add("Open data folder", null, delegate { Process.Start("explorer.exe", "\"" + baseDir + "\""); });
            menu.Items.Add(new ToolStripSeparator());
            startupItem = new ToolStripMenuItem("Start with Windows");
            startupItem.Checked = IsStartupEnabled();
            startupItem.Click += delegate { ToggleStartup(); };
            menu.Items.Add(startupItem);
            menu.Items.Add("Exit", null, delegate { ExitThread(); });

            tray = new NotifyIcon();
            tray.Icon = iconOk;
            tray.Text = "GPU Monitor";
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { RunInBackground("report"); };
            tray.Visible = true;

            uiTimer = new System.Windows.Forms.Timer();
            uiTimer.Interval = 3000;
            uiTimer.Tick += delegate { RefreshStatus(); };
            uiTimer.Start();
            RefreshStatus();

            SystemEvents.SessionEnding += OnSessionEnding;
            Application.ApplicationExit += delegate { Cleanup(); };

            string gpuNote = Nvml.Available ? Nvml.GpuName : "GPU data unavailable (" + Nvml.InitError + ")";
            tray.ShowBalloonTip(4000, "GPU Monitor is running",
                gpuNote + (hotkeyOk ? "\nPress " + Hotkey.Label + " right after a flicker in order to mark it."
                                    : "\nThe " + Hotkey.Label + " hotkey is used by another app; use the tray menu instead."),
                ToolTipIcon.Info);
        }

        void OnFreeze(DateTime start, double ms)
        {
            var e = new EventEntry();
            e.Time = start;
            e.Source = "Heartbeat";
            e.Provider = "Heartbeat";
            e.Level = 2;
            e.Message = string.Format("System stalled for {0:0} ms", ms);
            recorder.AddEvent(e);
            recorder.Trigger("freeze", start, ms, e.Message);
        }

        void OnDisplayChanged()
        {
            string now = DisplayInfo.DescribeJoined();
            var e = new EventEntry();
            e.Time = DateTime.Now;
            e.Source = "Display";
            e.Provider = "DisplayWatch";
            e.Level = 4;
            if (now != lastDisplays)
            {
                e.Message = "Display mode changed from [" + lastDisplays + "] to [" + now + "]";
                recorder.AddEvent(e);
                recorder.Trigger("display-change", e.Time, 0, "Display mode changed");
                lastDisplays = now;
            }
            else
            {
                e.Message = "Windows reported a display change, but the modes are the same: " + now;
                recorder.AddEvent(e);
            }
        }

        void OnMonitorDevice(bool arrived, string name)
        {
            var e = new EventEntry();
            e.Time = DateTime.Now;
            e.Source = "Display";
            e.Provider = "DisplayWatch";
            e.Level = 3;
            e.Message = (arrived ? "Monitor connected: " : "Monitor disconnected: ") + name;
            recorder.AddEvent(e);
            recorder.Trigger("display-change", e.Time, 0, e.Message);
        }

        void MarkFlicker(string how)
        {
            var e = new EventEntry();
            e.Time = DateTime.Now;
            e.Source = "User";
            e.Provider = "User";
            e.Level = 3;
            e.Message = "Flicker marked by the user (" + how + ")";
            recorder.AddEvent(e);
            recorder.Trigger("flicker-mark", e.Time, 0, e.Message);
            tray.ShowBalloonTip(2500, "Flicker marked",
                "The last 60 seconds will be saved in about 15 seconds.", ToolTipIcon.Info);
        }

        void RefreshStatus()
        {
            string text = recorder.IncidentsToday + " incident(s) today";
            if (recorder.LastIncidentText.Length > 0) text += ", last " + recorder.LastIncidentText;
            statusItem.Text = text;
            string tip = "GPU Monitor: " + text;
            tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
            bool recent = recorder.LastIncidentTime > DateTime.MinValue &&
                          (DateTime.Now - recorder.LastIncidentTime).TotalMinutes < 30;
            Icon want = recent ? iconAlert : iconOk;
            if (tray.Icon != want) tray.Icon = want;
        }

        void RunInBackground(string what)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    recorder.Flush();
                    if (what == "report") Process.Start(Report.Build(baseDir));
                    else Process.Start("notepad.exe", "\"" + Checks.WriteTextReport(baseDir) + "\"");
                }
                catch (Exception ex)
                {
                    Program.LogError("RunInBackground " + what, ex);
                    MessageBox.Show("Could not build the " + what + ":\n" + ex.Message, "GPU Monitor",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            });
        }

        static Icon MakeIcon(Color color)
        {
            using (var bmp = new Bitmap(16, 16))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (var b = new SolidBrush(color)) g.FillEllipse(b, 1, 1, 14, 14);
                    using (var p = new Pen(Color.White, 2f)) g.DrawLine(p, 4, 9, 7, 6);
                    using (var p = new Pen(Color.White, 2f)) g.DrawLine(p, 7, 6, 9, 9);
                    using (var p = new Pen(Color.White, 2f)) g.DrawLine(p, 9, 9, 12, 5);
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        static bool IsStartupEnabled()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
            {
                return key != null && key.GetValue(RunValue) != null;
            }
        }

        void ToggleStartup()
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (IsStartupEnabled()) key.DeleteValue(RunValue, false);
                else key.SetValue(RunValue, "\"" + Application.ExecutablePath + "\"");
            }
            startupItem.Checked = IsStartupEnabled();
        }

        void OnSessionEnding(object sender, SessionEndingEventArgs e)
        {
            Cleanup();
        }

        void Cleanup()
        {
            if (cleanedUp) return;
            cleanedUp = true;
            SystemEvents.SessionEnding -= OnSessionEnding;
            try { uiTimer.Stop(); } catch { }
            try { heartbeat.Stop(); } catch { }
            try { sampler.Stop(); } catch { }
            try { events.Dispose(); } catch { }
            try { window.Dispose(); } catch { }
            try { recorder.Dispose(); } catch { }
            try { sys.Dispose(); } catch { }
            try { tray.Visible = false; tray.Dispose(); } catch { }
        }

        protected override void ExitThreadCore()
        {
            Cleanup();
            base.ExitThreadCore();
        }
    }
}
