using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Linq;

namespace GpuMonitor
{
    // Subscribes to the Windows event logs, so that new GPU driver, display,
    // hardware error and TPM events are pushed to the monitor as they happen
    // instead of the logs being read again and again.
    class EventWatch : IDisposable
    {
        public const string SystemXPath =
            "*[System[(Provider[@Name='nvlddmkm' or @Name='Display' or @Name='Microsoft-Windows-WHEA-Logger' " +
            "or @Name='TPM' or @Name='Microsoft-Windows-TPM-WMI' or @Name='Microsoft-Windows-Dxgkrnl'] " +
            "or (Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=41))]]";

        public const string AppXPath =
            "*[System[Provider[@Name='Application Error' or @Name='Application Hang']]]";

        // Application crashes of these apps become incidents; other crashes
        // are only logged.
        static readonly string[] CrashApps = new string[]
        {
            "dwm.exe", "obs64.exe", "obs.exe", "Spotify.exe", "chrome.exe", "msedge.exe", "firefox.exe",
            "Discord.exe", "nvcontainer.exe", "NVDisplay.Container.exe", "explorer.exe", "audiodg.exe"
        };

        readonly Recorder recorder;
        EventLogWatcher systemWatcher;
        EventLogWatcher appWatcher;

        public EventWatch(Recorder recorder)
        {
            this.recorder = recorder;
        }

        public void Start()
        {
            systemWatcher = Watch("System", SystemXPath, OnSystemEvent);
            appWatcher = Watch("Application", AppXPath, OnAppEvent);
        }

        static EventLogWatcher Watch(string log, string xpath, EventHandler<EventRecordWrittenEventArgs> handler)
        {
            try
            {
                var w = new EventLogWatcher(new EventLogQuery(log, PathType.LogName, xpath));
                w.EventRecordWritten += handler;
                w.Enabled = true;
                return w;
            }
            catch (Exception ex)
            {
                Program.LogError("EventLogWatcher " + log, ex);
                return null;
            }
        }

        void OnSystemEvent(object sender, EventRecordWrittenEventArgs e)
        {
            if (e.EventRecord == null) return;
            try
            {
                EventEntry entry;
                using (var r = e.EventRecord) entry = ToEntry(r, "System");
                recorder.AddEvent(entry);

                string p = entry.Provider;
                if (p == "nvlddmkm" || p == "Display" || (p == "Microsoft-Windows-Dxgkrnl" && entry.Level <= 2))
                    recorder.Trigger("gpu-driver", entry.Time, 0, p + " event " + entry.Id + ": " + Short(entry.Message));
                else if (p == "Microsoft-Windows-WHEA-Logger")
                    recorder.Trigger("whea", entry.Time, 0, "WHEA event " + entry.Id + ": " + Short(entry.Message));
                // TPM events are kept as context only, since some are routine.
            }
            catch (Exception ex) { Program.LogError("OnSystemEvent", ex); }
        }

        void OnAppEvent(object sender, EventRecordWrittenEventArgs e)
        {
            if (e.EventRecord == null) return;
            try
            {
                EventEntry entry;
                using (var r = e.EventRecord) entry = ToEntry(r, "Application");
                recorder.AddEvent(entry);
                string msg = entry.Message ?? "";
                if (CrashApps.Any(a => msg.IndexOf(a, StringComparison.OrdinalIgnoreCase) >= 0))
                    recorder.Trigger("app-crash", entry.Time, 0, entry.Provider + ": " + Short(msg));
            }
            catch (Exception ex) { Program.LogError("OnAppEvent", ex); }
        }

        static string Short(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length > 140 ? s.Substring(0, 140) + "..." : s;
        }

        public static EventEntry ToEntry(EventRecord r, string source)
        {
            var entry = new EventEntry();
            entry.Time = r.TimeCreated.HasValue ? r.TimeCreated.Value : DateTime.Now;
            entry.Source = source;
            entry.Provider = r.ProviderName ?? "";
            entry.Id = r.Id;
            entry.Level = r.Level.HasValue ? r.Level.Value : 0;
            string msg = null;
            try { msg = r.FormatDescription(); } catch { }
            if (string.IsNullOrEmpty(msg))
            {
                // nvlddmkm often has no message template; its data fields still
                // contain the useful text such as "Reset TDR occurred".
                try
                {
                    msg = string.Join(" | ", r.Properties.Where(p => !(p.Value is byte[])).Select(p => Convert.ToString(p.Value))
                        .Where(v => !string.IsNullOrEmpty(v) && v != "0").Take(8).ToArray());
                }
                catch { msg = ""; }
            }
            if (msg.Length > 600) msg = msg.Substring(0, 600);
            entry.Message = msg.Trim();
            return entry;
        }

        // Reads past events, newest first, for the health check and report.
        public static List<EventEntry> Query(string log, string xpath, int max)
        {
            var list = new List<EventEntry>();
            try
            {
                var q = new EventLogQuery(log, PathType.LogName, xpath);
                q.ReverseDirection = true;
                using (var reader = new EventLogReader(q))
                {
                    for (EventRecord r = reader.ReadEvent(); r != null; r = reader.ReadEvent())
                    {
                        using (r) list.Add(ToEntry(r, log));
                        if (list.Count >= max) break;
                    }
                }
            }
            catch (Exception ex) { Program.LogError("EventWatch.Query", ex); }
            return list;
        }

        public static string WithinDays(string providerPredicate, int days)
        {
            long ms = (long)days * 24 * 3600 * 1000;
            return "*[System[" + providerPredicate + " and TimeCreated[timediff(@SystemTime) <= " + ms + "]]]";
        }

        public void Dispose()
        {
            if (systemWatcher != null) { systemWatcher.Enabled = false; systemWatcher.Dispose(); systemWatcher = null; }
            if (appWatcher != null) { appWatcher.Enabled = false; appWatcher.Dispose(); appWatcher = null; }
        }
    }
}
