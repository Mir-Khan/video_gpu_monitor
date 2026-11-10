using System;
using System.Collections.Generic;
using System.Globalization;
using System.Web.Script.Serialization;

namespace GpuMonitor
{
    // One second of telemetry. Values that could not be read are left at -1.
    public class Sample
    {
        public Sample()
        {
            GpuTempC = -1; GpuPowerW = -1; GpuPowerLimitW = -1; GpuClockMHz = -1; MemClockMHz = -1;
            GpuUtil = -1; PState = -1; PcieGen = -1; PcieWidth = -1; PcieReplays = -1; EncoderSessions = -1;
            ThrottleMask = -1; FanPct = -1; CpuPct = -1; CpuMaxCorePct = -1; CpuMHz = -1; DpcPct = -1;
            IntPct = -1; AvailMemMB = -1; MaxGapMs = 0; Fg = "";
        }

        [ScriptIgnore]
        public DateTime Time { get; set; }

        public string T
        {
            get { return Time.ToString("o", CultureInfo.InvariantCulture); }
            set { Time = DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind); }
        }

        public double GpuTempC { get; set; }
        public double GpuPowerW { get; set; }
        public double GpuPowerLimitW { get; set; }
        public int GpuClockMHz { get; set; }
        public int MemClockMHz { get; set; }
        public int GpuUtil { get; set; }
        public int PState { get; set; }
        public int PcieGen { get; set; }
        public int PcieWidth { get; set; }
        public long PcieReplays { get; set; }
        public int EncoderSessions { get; set; }
        public long ThrottleMask { get; set; }
        public int FanPct { get; set; }
        public double CpuPct { get; set; }
        public double CpuMaxCorePct { get; set; }
        public double CpuMHz { get; set; }
        // Highest per-core DPC and interrupt time, since a DPC storm usually
        // hits a single core and disappears when averaged over all 24 threads.
        public double DpcPct { get; set; }
        public double IntPct { get; set; }
        public double AvailMemMB { get; set; }
        // Longest heartbeat delay seen during this second.
        public double MaxGapMs { get; set; }
        public string Fg { get; set; }
    }

    // An event log entry, a display change, a detected freeze or a user mark.
    public class EventEntry
    {
        [ScriptIgnore]
        public DateTime Time { get; set; }

        public string T
        {
            get { return Time.ToString("o", CultureInfo.InvariantCulture); }
            set { Time = DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind); }
        }

        public string Source { get; set; }
        public string Provider { get; set; }
        public int Id { get; set; }
        public int Level { get; set; }
        public string Message { get; set; }
    }

    // A saved snapshot of everything around a freeze, flicker or driver error.
    public class Incident
    {
        public Incident()
        {
            Triggers = new List<string>();
            RunningApps = new List<string>();
            Displays = new List<string>();
            Samples = new List<Sample>();
            Events = new List<EventEntry>();
            Foreground = "";
            ForegroundTitle = "";
        }

        public string Id { get; set; }
        // freeze, flicker-mark, display-change, gpu-driver, whea, app-crash
        public string Type { get; set; }

        [ScriptIgnore]
        public DateTime Time { get; set; }

        public string T
        {
            get { return Time.ToString("o", CultureInfo.InvariantCulture); }
            set { Time = DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind); }
        }

        [ScriptIgnore]
        public DateTime FinalizeAt { get; set; }

        public double DurationMs { get; set; }
        public int FreezeCount { get; set; }
        public List<string> Triggers { get; set; }
        public string Foreground { get; set; }
        public string ForegroundTitle { get; set; }
        public List<string> RunningApps { get; set; }
        public List<string> Displays { get; set; }
        public List<Sample> Samples { get; set; }
        public List<EventEntry> Events { get; set; }
    }
}
