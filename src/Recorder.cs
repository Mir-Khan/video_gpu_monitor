using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace GpuMonitor
{
    // Keeps the last RingSeconds of samples and recent events in memory,
    // writes the daily CSV logs, and turns triggers into incident files.
    class Recorder : IDisposable
    {
        readonly object sync = new object();
        readonly string logDir;
        readonly string incidentDir;
        readonly SystemStats sys;

        readonly Sample[] ring = new Sample[Config.RingSeconds];
        int head;
        int count;
        readonly List<EventEntry> recentEvents = new List<EventEntry>();
        readonly List<Incident> pending = new List<Incident>();

        StreamWriter samplesCsv;
        StreamWriter eventsCsv;
        string currentDay = "";
        int secondsSinceFlush;

        public int IncidentsToday { get; private set; }
        public DateTime LastIncidentTime { get; private set; }
        public string LastIncidentText { get; private set; }
        public event Action<Incident> IncidentSaved;

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public Recorder(string baseDir, SystemStats sys)
        {
            this.sys = sys;
            logDir = Path.Combine(baseDir, "logs");
            incidentDir = Path.Combine(baseDir, "incidents");
            Directory.CreateDirectory(logDir);
            Directory.CreateDirectory(incidentDir);
            LastIncidentText = "";
            string today = DateTime.Now.ToString("yyyyMMdd");
            try { IncidentsToday = Directory.GetFiles(incidentDir, "incident-" + today + "-*.json").Length; } catch { }
        }

        public void CleanupOldLogs()
        {
            try
            {
                DateTime cutoff = DateTime.Now.AddDays(-Config.LogRetentionDays);
                foreach (var f in Directory.GetFiles(logDir, "*.csv"))
                {
                    if (File.GetLastWriteTime(f) < cutoff) File.Delete(f);
                }
            }
            catch (Exception ex) { Program.LogError("CleanupOldLogs", ex); }
        }

        public void AddSample(Sample s)
        {
            List<Incident> due = null;
            lock (sync)
            {
                ring[head] = s;
                head = (head + 1) % ring.Length;
                if (count < ring.Length) count++;

                EnsureDay(s.Time);
                samplesCsv.WriteLine(CsvLine(s));
                if (++secondsSinceFlush >= Config.CsvFlushSeconds) FlushLocked();

                for (int i = pending.Count - 1; i >= 0; i--)
                {
                    if (pending[i].FinalizeAt <= s.Time)
                    {
                        if (due == null) due = new List<Incident>();
                        CollectLocked(pending[i]);
                        due.Add(pending[i]);
                        pending.RemoveAt(i);
                    }
                }
            }
            if (due != null) foreach (var inc in due) Save(inc);
        }

        public void AddEvent(EventEntry e)
        {
            lock (sync)
            {
                recentEvents.Add(e);
                if (recentEvents.Count > 400) recentEvents.RemoveRange(0, 100);
                EnsureDay(DateTime.Now);
                eventsCsv.WriteLine(string.Join(",", new string[]
                {
                    e.Time.ToString("yyyy-MM-dd HH:mm:ss.fff", Inv), Csv(e.Source), Csv(e.Provider),
                    e.Id.ToString(Inv), e.Level.ToString(Inv), Csv(e.Message)
                }));
            }
        }

        static int Priority(string type)
        {
            switch (type)
            {
                case "gpu-driver": return 6;
                case "whea": return 6;
                case "freeze": return 5;
                case "flicker-mark": return 4;
                case "display-change": return 3;
                case "app-crash": return 2;
                default: return 1;
            }
        }

        // Starts a new incident, or merges into one that is already pending
        // within MergeSeconds, so that a burst of related signals becomes a
        // single incident.
        public void Trigger(string type, DateTime time, double durationMs, string summary)
        {
            Incident inc = null;
            bool created = false;
            lock (sync)
            {
                foreach (var p in pending)
                {
                    if (Math.Abs((p.Time - time).TotalSeconds) <= Config.MergeSeconds) { inc = p; break; }
                }
                if (inc == null)
                {
                    inc = new Incident();
                    inc.Time = time;
                    inc.Type = type;
                    inc.FinalizeAt = time.AddSeconds(Config.FinalizeDelaySeconds);
                    inc.Id = time.ToString("yyyyMMdd-HHmmss-fff", Inv) + "-" + type;
                    pending.Add(inc);
                    created = true;
                    if (LastIncidentTime.Date != DateTime.Now.Date) IncidentsToday = 0;
                    IncidentsToday++;
                }
                else
                {
                    if (Priority(type) > Priority(inc.Type)) inc.Type = type;
                    DateTime extended = time.AddSeconds(Config.FinalizeDelaySeconds);
                    DateTime cap = inc.Time.AddSeconds(Config.PostSeconds + 30);
                    if (extended > inc.FinalizeAt) inc.FinalizeAt = extended < cap ? extended : cap;
                }
                if (type == "freeze")
                {
                    inc.FreezeCount++;
                    if (durationMs > inc.DurationMs) inc.DurationMs = Math.Round(durationMs);
                }
                inc.Triggers.Add(time.ToString("HH:mm:ss.fff", Inv) + "  " + type + ": " + summary);
                LastIncidentTime = DateTime.Now;
                LastIncidentText = time.ToString("HH:mm", Inv) + " " + type +
                    (type == "freeze" ? string.Format(Inv, " {0:0.0} s", durationMs / 1000.0) : "");
            }

            if (created)
            {
                // Done outside the lock because listing processes takes a few ms.
                string title = "";
                string fg = sys != null ? sys.Foreground(true, out title) : "";
                var apps = ScanApps();
                var displays = DisplayInfo.Describe();
                lock (sync)
                {
                    inc.Foreground = fg;
                    inc.ForegroundTitle = title.Length > 120 ? title.Substring(0, 120) : title;
                    inc.RunningApps = apps;
                    inc.Displays = displays;
                }
            }
        }

        static List<string> ScanApps()
        {
            var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            Process[] procs = null;
            try
            {
                procs = Process.GetProcesses();
                foreach (var p in procs)
                {
                    string name = p.ProcessName;
                    foreach (var prefix in Config.InterestingProcesses)
                    {
                        if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { found.Add(name); break; }
                    }
                }
            }
            catch (Exception ex) { Program.LogError("ScanApps", ex); }
            finally
            {
                if (procs != null) foreach (var p in procs) p.Dispose();
            }
            return found.ToList();
        }

        void CollectLocked(Incident inc)
        {
            DateTime from = inc.Time.AddSeconds(-Config.PreSeconds);
            DateTime to = inc.Time.AddSeconds(Config.PostSeconds);
            var samples = new List<Sample>();
            int start = (head - count + ring.Length) % ring.Length;
            for (int i = 0; i < count; i++)
            {
                var s = ring[(start + i) % ring.Length];
                if (s != null && s.Time >= from && s.Time <= to) samples.Add(s);
            }
            inc.Samples = samples;

            DateTime evFrom = inc.Time.AddSeconds(-Config.EventLookbackSeconds);
            DateTime evTo = inc.FinalizeAt;
            inc.Events = recentEvents.Where(e => e.Time >= evFrom && e.Time <= evTo).OrderBy(e => e.Time).ToList();
        }

        void Save(Incident inc)
        {
            try
            {
                var json = new JavaScriptSerializer();
                json.MaxJsonLength = int.MaxValue;
                string path = Path.Combine(incidentDir, "incident-" + inc.Id + ".json");
                File.WriteAllText(path, json.Serialize(inc), new UTF8Encoding(false));
                var handler = IncidentSaved;
                if (handler != null) handler(inc);
            }
            catch (Exception ex) { Program.LogError("Save incident", ex); }
        }

        void EnsureDay(DateTime t)
        {
            string day = t.ToString("yyyy-MM-dd", Inv);
            if (day == currentDay && samplesCsv != null) return;
            CloseWriters();
            currentDay = day;
            samplesCsv = OpenCsv(Path.Combine(logDir, "samples-" + day + ".csv"), SamplesHeader);
            eventsCsv = OpenCsv(Path.Combine(logDir, "events-" + day + ".csv"), "time,source,provider,id,level,message");
        }

        public const string SamplesHeader =
            "time,gpu_temp_c,gpu_power_w,gpu_power_limit_w,gpu_clock_mhz,mem_clock_mhz,gpu_util,pstate," +
            "pcie_gen,pcie_width,pcie_replays,nvenc_sessions,throttle,fan_pct,cpu_pct,cpu_max_core_pct,cpu_mhz," +
            "dpc_max_core_pct,int_max_core_pct,avail_mem_mb,max_gap_ms,foreground";

        static StreamWriter OpenCsv(string path, string header)
        {
            bool exists = File.Exists(path);
            var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            var w = new StreamWriter(fs, new UTF8Encoding(false), 16384);
            if (!exists) w.WriteLine(header);
            return w;
        }

        static string CsvLine(Sample s)
        {
            return string.Join(",", new string[]
            {
                s.Time.ToString("yyyy-MM-dd HH:mm:ss", Inv), N(s.GpuTempC), N(s.GpuPowerW), N(s.GpuPowerLimitW),
                N(s.GpuClockMHz), N(s.MemClockMHz), N(s.GpuUtil), N(s.PState), N(s.PcieGen), N(s.PcieWidth),
                N(s.PcieReplays), N(s.EncoderSessions), s.ThrottleMask < 0 ? "" : "0x" + s.ThrottleMask.ToString("x"),
                N(s.FanPct), N(s.CpuPct), N(s.CpuMaxCorePct), N(s.CpuMHz), N(s.DpcPct), N(s.IntPct),
                N(s.AvailMemMB), N(s.MaxGapMs), Csv(s.Fg)
            });
        }

        static string N(double v) { return v < 0 ? "" : v.ToString("0.#", Inv); }

        static string Csv(string v)
        {
            if (string.IsNullOrEmpty(v)) return "";
            v = v.Replace("\r", " ").Replace("\n", " ");
            if (v.IndexOf(',') >= 0 || v.IndexOf('"') >= 0) return "\"" + v.Replace("\"", "\"\"") + "\"";
            return v;
        }

        void FlushLocked()
        {
            secondsSinceFlush = 0;
            try
            {
                if (samplesCsv != null) samplesCsv.Flush();
                if (eventsCsv != null) eventsCsv.Flush();
            }
            catch (Exception ex) { Program.LogError("Flush", ex); }
        }

        public void Flush()
        {
            lock (sync) FlushLocked();
        }

        void CloseWriters()
        {
            if (samplesCsv != null) { samplesCsv.Dispose(); samplesCsv = null; }
            if (eventsCsv != null) { eventsCsv.Dispose(); eventsCsv = null; }
        }

        // Saves every pending incident right away, with whatever data exists.
        public void Dispose()
        {
            List<Incident> due;
            lock (sync)
            {
                foreach (var inc in pending) CollectLocked(inc);
                due = new List<Incident>(pending);
                pending.Clear();
                FlushLocked();
            }
            foreach (var inc in due) Save(inc);
            lock (sync) CloseWriters();
        }
    }
}
