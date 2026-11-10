using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

namespace GpuMonitor
{
    class Verdict
    {
        public int Score;
        public string Title;
        public string Detail;
        public string Fix;
    }

    // Builds an HTML report from the saved incidents, the health check and
    // the CSV logs around any unexpected restart.
    static class Report
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string Build(string baseDir)
        {
            var checks = Checks.Run();
            bool biosOld = Checks.BiosPredatesFix(checks);
            bool afterburner = Checks.AfterburnerRunning(checks);
            var incidents = Load(Path.Combine(baseDir, "incidents"));
            var verdicts = new Dictionary<string, List<Verdict>>();
            foreach (var inc in incidents)
            {
                if (string.IsNullOrEmpty(inc.Id)) inc.Id = inc.T;
                if (verdicts.ContainsKey(inc.Id)) inc.Id += "-" + verdicts.Count;
                verdicts[inc.Id] = Diagnose(inc, biosOld, afterburner);
            }

            var sb = new StringBuilder();
            Head(sb);
            sb.Append("<header><h1>GPU Monitor report</h1><p class=\"muted\">Generated ")
              .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm")).Append(", covering the last ").Append(Config.ReportDays)
              .Append(" days. ").Append(incidents.Count).Append(" incident(s) recorded.</p></header>");

            Summary(sb, incidents, verdicts, checks);
            HealthTable(sb, checks);
            Shutdowns(sb, baseDir);
            IncidentCards(sb, incidents, verdicts);

            sb.Append("<footer class=\"muted\">Incident files are in the <code>incidents</code> folder and the per-second logs ")
              .Append("are in <code>logs</code>, next to GpuMonitor.exe.</footer></main></body></html>");

            string dir = Path.Combine(baseDir, "reports");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "report-" + DateTime.Now.ToString("yyyy-MM-dd_HHmm") + ".html");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            return path;
        }

        // ---------------------------------------------------------------- data

        static List<Incident> Load(string dir)
        {
            var list = new List<Incident>();
            if (!Directory.Exists(dir)) return list;
            var json = new JavaScriptSerializer();
            json.MaxJsonLength = int.MaxValue;
            DateTime cutoff = DateTime.Now.AddDays(-Config.ReportDays);
            foreach (var f in Directory.GetFiles(dir, "incident-*.json"))
            {
                try
                {
                    if (File.GetLastWriteTime(f) < cutoff) continue;
                    var inc = json.Deserialize<Incident>(File.ReadAllText(f));
                    if (inc != null) list.Add(inc);
                }
                catch (Exception ex) { Program.LogError("Load " + f, ex); }
            }
            return list.OrderByDescending(i => i.Time).ToList();
        }

        static double Rel(Incident inc, Sample s) { return (s.Time - inc.Time).TotalSeconds; }

        static double Max(IEnumerable<Sample> samples, Func<Sample, double> f)
        {
            double m = -1;
            foreach (var s in samples) { double v = f(s); if (v > m) m = v; }
            return m;
        }

        static double Min(IEnumerable<Sample> samples, Func<Sample, double> f)
        {
            double m = double.MaxValue;
            foreach (var s in samples) { double v = f(s); if (v >= 0 && v < m) m = v; }
            return m == double.MaxValue ? -1 : m;
        }

        // Median of the busiest core's DPC or interrupt time, from 60 to 10
        // seconds before the incident.
        public static double DpcBaseline(Incident inc)
        {
            var vals = inc.Samples.Where(s => Rel(inc, s) <= -10)
                .Select(s => Math.Max(s.DpcPct, s.IntPct)).Where(v => v >= 0).OrderBy(v => v).ToList();
            return vals.Count == 0 ? 0 : vals[vals.Count / 2];
        }

        static bool IsDrm(Incident inc)
        {
            string fg = inc.Foreground ?? "";
            string title = inc.ForegroundTitle ?? "";
            bool app = Config.DrmProcesses.Any(p => fg.Equals(p, StringComparison.OrdinalIgnoreCase));
            bool spotify = fg.Equals("Spotify", StringComparison.OrdinalIgnoreCase);
            bool videoTitle = Config.DrmTitles.Any(t => title.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0);
            return spotify || (app && videoTitle) || fg.Equals("PrimeVideo", StringComparison.OrdinalIgnoreCase) ||
                   fg.Equals("Netflix", StringComparison.OrdinalIgnoreCase);
        }

        // ------------------------------------------------------------ diagnosis

        public static List<Verdict> Diagnose(Incident inc, bool biosOld, bool afterburner)
        {
            var list = new List<Verdict>();
            bool flicker = inc.Type == "flicker-mark" || inc.Type == "display-change";
            bool freeze = inc.FreezeCount > 0;
            // A flicker is marked by hand a few seconds after it happened, so
            // look further back for those.
            double back = flicker ? 12 : 5;
            var win = inc.Samples.Where(s => Rel(inc, s) >= -back && Rel(inc, s) <= 5).ToList();
            var before = inc.Samples.Where(s => Rel(inc, s) >= -back && Rel(inc, s) <= 1).ToList();
            var evs = inc.Events.Where(e => Math.Abs((e.Time - inc.Time).TotalSeconds) <= 20).ToList();

            bool gpuEvt = evs.Any(e => e.Source == "System" &&
                (e.Provider == "nvlddmkm" || e.Provider == "Display" || (e.Provider == "Microsoft-Windows-Dxgkrnl" && e.Level <= 2)));
            bool whea = evs.Any(e => e.Provider == "Microsoft-Windows-WHEA-Logger");
            bool tpmEvt = evs.Any(e => e.Provider == "TPM" || e.Provider == "Microsoft-Windows-TPM-WMI");
            bool dispEvt = evs.Any(e => e.Source == "Display");
            var crash = evs.FirstOrDefault(e => e.Provider == "Application Error" || e.Provider == "Application Hang");

            double maxDpc = Math.Max(Max(win, s => s.DpcPct), Max(win, s => s.IntPct));
            // Some PCs normally show 15 to 25% DPC or interrupt time on their
            // busiest core, so a spike is judged against this incident's own
            // baseline from 60 to 10 seconds earlier.
            double dpcBaseline = DpcBaseline(inc);
            double dpcThreshold = Math.Max(40, dpcBaseline * 2);
            bool dpcSpike = maxDpc >= dpcThreshold;
            double maxPowerRatio = Max(before, s => s.GpuPowerW > 0 && s.GpuPowerLimitW > 0 ? s.GpuPowerW / s.GpuPowerLimitW : -1);
            long throttle = 0;
            foreach (var s in win) if (s.ThrottleMask > 0) throttle |= s.ThrottleMask;
            bool hwSlow = (throttle & (Nvml.ReasonHwSlowdown | Nvml.ReasonHwPowerBrake | Nvml.ReasonHwThermal)) != 0;
            double maxTemp = Max(win, s => s.GpuTempC);
            double maxCpu = Max(win, s => s.CpuPct);
            double minMem = Min(win, s => s.MemClockMHz);
            double maxMem = Max(win, s => s.MemClockMHz);
            bool memSwitch = minMem > 0 && maxMem >= minMem * 2;
            double minReplay = Min(win, s => s.PcieReplays);
            double maxReplay = Max(win, s => s.PcieReplays);
            bool replays = minReplay >= 0 && maxReplay > minReplay;
            bool drm = IsDrm(inc);

            if (gpuEvt)
                Add(list, 92, "GPU driver timeout or reset (TDR)",
                    "The NVIDIA driver or the Windows display driver logged an error within 20 seconds of this incident.",
                    afterburner
                        ? "MSI Afterburner is running; reset it to default clocks, voltage curve and power limit, and see whether these stop. If they continue, do a clean driver reinstall with DDU and check that each PCIe power connector on the card has its own cable from the PSU."
                        : "Do a clean driver reinstall with DDU, try the previous driver version, and check that each PCIe power connector on the card has its own cable from the PSU.");

            if (whea)
                Add(list, 90, "Hardware error (WHEA)",
                    "Windows logged a hardware error near this incident, which usually points at CPU, memory or PCIe instability.",
                    "Disable any CPU or memory overclock (including PBO and Curve Optimizer) and test with XMP off, then re-enable one at a time.");

            if (freeze)
            {
                string dur = string.Format(Inv, "{0:0} ms", inc.DurationMs);
                if (dpcSpike)
                    Add(list, 78, "Driver latency (DPC or interrupt storm)",
                        string.Format(Inv, "One CPU core spent up to {0:0}% of its time in DPCs or interrupts around the freeze, against a normal level of about {1:0}% before it, which means a driver held the CPU.", maxDpc, dpcBaseline),
                        "Run LatencyMon during normal use in order to see which driver has the highest execution time. On this PC the usual suspects are the audio and capture drivers (Voicemeeter, Elgato, Realtek, NVIDIA HD Audio), so also try disabling audio devices you do not use.");

                if (hwSlow || maxPowerRatio >= 0.97)
                    Add(list, 60, "GPU power or voltage limit",
                        string.Format(Inv, "Just before the freeze the GPU was at {0:0}% of its power limit{1}.",
                            Math.Max(0, maxPowerRatio * 100), hwSlow ? " and reported hardware slowdown (" + Nvml.DescribeReasons(throttle) + ")" : ""),
                        "Check that the GPU has separate PCIe power cables, remove any undervolt or overclock, and if the problem continues, consider whether the power supply is large enough for a 3080 Ti with transient spikes.");

                if (maxTemp >= 83)
                    Add(list, 50, "GPU temperature",
                        string.Format(Inv, "The GPU reached {0:0} C around this incident.", maxTemp),
                        "Check case airflow and the GPU fan curve.");

                if (maxCpu >= 95)
                    Add(list, 40, "CPU fully loaded",
                        string.Format(Inv, "Total CPU use reached {0:0}% around the freeze.", maxCpu),
                        "See which process was using the CPU; a stall under full load is less likely to be the fTPM bug.");

                bool explained = gpuEvt || whea || dpcSpike;
                if (!explained && inc.DurationMs >= 300 && inc.DurationMs <= 5000)
                {
                    int score = biosOld ? 88 : 55;
                    if (tpmEvt) score += 4;
                    Add(list, score, "AMD fTPM stutter",
                        "The whole system stalled for " + dur + " with no GPU driver error, no DPC spike and no hardware error. " +
                        "That is the pattern of the AMD firmware TPM bug, where the fTPM briefly halts the system and any playing audio repeats until it resumes." +
                        (biosOld ? " The health check shows the BIOS is older than the fix." : "") +
                        (tpmEvt ? " A TPM event was also logged at the same moment." : ""),
                        "Update the motherboard BIOS to the newest version (any release with AGESA 1.2.0.7 or newer contains the fix). Turning fTPM off in the BIOS also stops it, but Windows 11 needs a TPM, so the BIOS update is the better route.");
                }
                else if (!explained)
                {
                    Add(list, 30, "Unclassified stall",
                        "The system stalled for " + dur + ", which is outside the usual length of an fTPM stutter, and no other signal matched.",
                        "Compare with other incidents; if long stalls repeat, run LatencyMon and check the event log around this time.");
                }
            }

            if (flicker)
            {
                if (memSwitch)
                    Add(list, 70, "GPU memory clock switching",
                        string.Format(Inv, "The GPU memory clock jumped between {0:0} and {1:0} MHz around the flicker. With two monitors at high refresh rates, this switch can blank the screen for a moment, and video playback is what usually triggers it.", minMem, maxMem),
                        "In NVIDIA Control Panel, under Manage 3D settings and then Program settings, set Power management mode to Prefer maximum performance for the app (for example chrome.exe or Spotify.exe). Running both monitors at the same refresh rate also avoids it.");

                if (drm)
                    Add(list, 68, "Protected video (HDCP) or multiplane overlay",
                        "The foreground app was " + inc.Foreground + (inc.ForegroundTitle.Length > 0 ? " (\"" + inc.ForegroundTitle + "\")" : "") +
                        ", which plays DRM protected video. Starting protected playback makes the GPU re-negotiate HDCP with the monitors, and it also switches the video onto a hardware overlay (MPO); either can black out the screen briefly.",
                        "As a test, turn off hardware acceleration in the browser and in Spotify. If the flicker stops, try disabling MPO, and check that both monitors are on DisplayPort or HDMI 2.0 cables that support HDCP 2.2.");

                if (dispEvt)
                    Add(list, 55, "Monitor signal change",
                        "Windows reported a display mode change or a monitor connecting or disconnecting near this moment.",
                        "Check the cable and port of the monitor that went black; a different cable or port often fixes repeated reconnects.");

                if (!memSwitch && !drm && !dispEvt && !gpuEvt && !replays)
                    Add(list, 20, "No matching signal recorded",
                        "Nothing in the GPU, CPU, display or event data changed around this mark.",
                        "If this repeats, the problem is likely between the GPU and the monitor (cable, port or HDCP handshake inside the monitor), so swapping the cable or port is the next test.");
            }

            if (replays)
                Add(list, 62, "PCIe link errors",
                    string.Format(Inv, "The GPU PCIe replay counter rose from {0:0} to {1:0} around this incident, which means data had to be resent over the PCIe slot.", minReplay, maxReplay),
                    "Reseat the GPU, avoid riser cables, and try setting the PCIe slot to Gen 3 in the BIOS.");

            if (crash != null)
                Add(list, 40, "Application crash or hang", Trim(crash.Message, 300), "");

            return list.OrderByDescending(v => v.Score).ToList();
        }

        static void Add(List<Verdict> list, int score, string title, string detail, string fix)
        {
            var v = new Verdict();
            v.Score = Math.Min(score, 99); v.Title = title; v.Detail = detail; v.Fix = fix;
            list.Add(v);
        }

        static string Strength(int score)
        {
            return score >= 75 ? "likely" : score >= 50 ? "possible" : "weak";
        }

        // ------------------------------------------------------------- sections

        static void Summary(StringBuilder sb, List<Incident> incidents, Dictionary<string, List<Verdict>> verdicts, List<CheckItem> checks)
        {
            sb.Append("<section><h2>Summary</h2>");

            var bad = checks.Where(c => c.Status == "bad").ToList();
            foreach (var c in bad)
                sb.Append("<div class=\"callout bad\"><strong>").Append(H(c.Name)).Append(": ").Append(H(c.Value))
                  .Append("</strong><br>").Append(H(c.Note)).Append("</div>");

            if (incidents.Count == 0)
            {
                sb.Append("<p>No incidents have been recorded yet. Leave GPU Monitor running while you game, stream or watch video, ")
                  .Append("and press <kbd>").Append(Hotkey.Label).Append("</kbd> right after a flicker.</p></section>");
                return;
            }

            var byType = incidents.GroupBy(i => i.Type).OrderByDescending(g => g.Count());
            sb.Append("<div class=\"tiles\">");
            foreach (var g in byType)
                sb.Append("<div class=\"tile\"><div class=\"num\">").Append(g.Count()).Append("</div><div class=\"lbl\">")
                  .Append(H(TypeLabel(g.Key))).Append("</div></div>");
            var freezes = incidents.Where(i => i.FreezeCount > 0).ToList();
            if (freezes.Count > 0)
            {
                var d = freezes.Select(i => i.DurationMs).OrderBy(x => x).ToList();
                sb.Append("<div class=\"tile\"><div class=\"num\">").Append((d[d.Count / 2] / 1000.0).ToString("0.0", Inv))
                  .Append(" s</div><div class=\"lbl\">median freeze</div></div>");
                sb.Append("<div class=\"tile\"><div class=\"num\">").Append((d[d.Count - 1] / 1000.0).ToString("0.0", Inv))
                  .Append(" s</div><div class=\"lbl\">longest freeze</div></div>");
            }
            sb.Append("</div>");

            var causes = new Dictionary<string, int>();
            foreach (var inc in incidents)
            {
                var v = verdicts[inc.Id];
                string key = v.Count > 0 && v[0].Score >= 50 ? v[0].Title : "Unclear";
                int n; causes.TryGetValue(key, out n); causes[key] = n + 1;
            }
            sb.Append("<h3>Most likely cause per incident</h3><table><tr><th>Cause</th><th class=\"r\">Incidents</th></tr>");
            foreach (var kv in causes.OrderByDescending(k => k.Value))
                sb.Append("<tr><td>").Append(H(kv.Key)).Append("</td><td class=\"r\">").Append(kv.Value).Append("</td></tr>");
            sb.Append("</table></section>");
        }

        static void HealthTable(StringBuilder sb, List<CheckItem> checks)
        {
            sb.Append("<section><h2>Health check</h2><table><tr><th></th><th>Item</th><th>Value</th><th>Note</th></tr>");
            foreach (var c in checks)
                sb.Append("<tr><td><span class=\"dot ").Append(c.Status).Append("\"></span></td><td>").Append(H(c.Name))
                  .Append("</td><td>").Append(H(c.Value)).Append("</td><td class=\"muted\">").Append(H(c.Note)).Append("</td></tr>");
            sb.Append("</table></section>");
        }

        // For each unexpected restart, shows the last rows that were logged
        // before it, since a full freeze that needs a reset ends the log there.
        static void Shutdowns(StringBuilder sb, string baseDir)
        {
            var kp = EventWatch.Query("System",
                EventWatch.WithinDays("Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=41", Config.ReportDays), 10);
            if (kp.Count == 0) return;
            sb.Append("<section><h2>Unexpected restarts</h2><p class=\"muted\">Each restart below was logged as Kernel-Power 41. ")
              .Append("The rows are the last telemetry written before it, when GPU Monitor was running at the time.</p>");
            foreach (var e in kp)
            {
                sb.Append("<h3>Restart at ").Append(e.Time.ToString("yyyy-MM-dd HH:mm:ss")).Append("</h3>");
                var rows = LastRowsBefore(Path.Combine(baseDir, "logs"), e.Time, 8);
                if (rows.Count == 0)
                {
                    sb.Append("<p class=\"muted\">No telemetry was logged before this restart (GPU Monitor was not running, or the logs were already removed).</p>");
                    continue;
                }
                string[] header = Recorder.SamplesHeader.Split(',');
                int[] cols = new int[] { 0, 1, 2, 4, 5, 6, 12, 14, 17, 20, 21 };
                sb.Append("<div class=\"scroll\"><table class=\"small\"><tr>");
                foreach (int c in cols) sb.Append("<th>").Append(H(header[c])).Append("</th>");
                sb.Append("</tr>");
                foreach (var r in rows)
                {
                    var f = SplitCsv(r);
                    sb.Append("<tr>");
                    foreach (int c in cols) sb.Append("<td>").Append(c < f.Count ? H(f[c]) : "").Append("</td>");
                    sb.Append("</tr>");
                }
                sb.Append("</table></div>");
            }
            sb.Append("</section>");
        }

        static List<string> LastRowsBefore(string logDir, DateTime t, int n)
        {
            var q = new Queue<string>();
            foreach (var day in new DateTime[] { t.Date.AddDays(-1), t.Date })
            {
                string path = Path.Combine(logDir, "samples-" + day.ToString("yyyy-MM-dd") + ".csv");
                if (!File.Exists(path)) continue;
                try
                {
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var rd = new StreamReader(fs))
                    {
                        string line;
                        while ((line = rd.ReadLine()) != null)
                        {
                            if (line.Length < 19 || line.StartsWith("time")) continue;
                            DateTime lt;
                            if (!DateTime.TryParseExact(line.Substring(0, 19), "yyyy-MM-dd HH:mm:ss", Inv, DateTimeStyles.None, out lt)) continue;
                            if (lt >= t) break;
                            q.Enqueue(line);
                            if (q.Count > n) q.Dequeue();
                        }
                    }
                }
                catch (Exception ex) { Program.LogError("LastRowsBefore", ex); }
            }
            return q.ToList();
        }

        static List<string> SplitCsv(string line)
        {
            var result = new List<string>();
            var cur = new StringBuilder();
            bool quoted = false;
            for (int i = 0; i < line.Length; i++)
            {
                char ch = line[i];
                if (quoted)
                {
                    if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
                    else if (ch == '"') quoted = false;
                    else cur.Append(ch);
                }
                else if (ch == '"') quoted = true;
                else if (ch == ',') { result.Add(cur.ToString()); cur.Clear(); }
                else cur.Append(ch);
            }
            result.Add(cur.ToString());
            return result;
        }

        static void IncidentCards(StringBuilder sb, List<Incident> incidents, Dictionary<string, List<Verdict>> verdicts)
        {
            if (incidents.Count == 0) return;
            sb.Append("<section><h2>Incidents</h2>");
            if (incidents.Count > Config.MaxIncidentsInReport)
                sb.Append("<p class=\"muted\">Showing the newest ").Append(Config.MaxIncidentsInReport).Append(" of ").Append(incidents.Count).Append(".</p>");

            foreach (var inc in incidents.Take(Config.MaxIncidentsInReport))
            {
                sb.Append("<article class=\"card\"><div class=\"cardhead\"><span class=\"badge t-").Append(H(inc.Type)).Append("\">")
                  .Append(H(TypeLabel(inc.Type))).Append("</span><h3>").Append(inc.Time.ToString("ddd yyyy-MM-dd HH:mm:ss"));
                if (inc.FreezeCount > 0)
                    sb.Append(" &middot; ").Append((inc.DurationMs / 1000.0).ToString("0.00", Inv)).Append(" s stall")
                      .Append(inc.FreezeCount > 1 ? " (" + inc.FreezeCount + " stalls)" : "");
                sb.Append("</h3></div>");

                // Verdicts.
                var vs = verdicts[inc.Id];
                if (vs.Count > 0)
                {
                    sb.Append("<ol class=\"verdicts\">");
                    foreach (var v in vs.Take(4))
                    {
                        sb.Append("<li><span class=\"str ").Append(Strength(v.Score)).Append("\">").Append(Strength(v.Score))
                          .Append("</span> <strong>").Append(H(v.Title)).Append("</strong><div>").Append(H(v.Detail)).Append("</div>");
                        if (!string.IsNullOrEmpty(v.Fix)) sb.Append("<div class=\"fix\"><b>What to try:</b> ").Append(H(v.Fix)).Append("</div>");
                        sb.Append("</li>");
                    }
                    sb.Append("</ol>");
                }

                // Context.
                bool nvenc = inc.Samples.Any(s => s.EncoderSessions > 0);
                sb.Append("<ul class=\"ctx\">");
                sb.Append("<li><b>Foreground:</b> ").Append(H(inc.Foreground));
                if (!string.IsNullOrEmpty(inc.ForegroundTitle)) sb.Append(" &ldquo;").Append(H(inc.ForegroundTitle)).Append("&rdquo;");
                sb.Append("</li>");
                if (nvenc) sb.Append("<li><b>NVENC:</b> the GPU encoder was in use (streaming or recording).</li>");
                sb.Append("<li><b>Normal DPC / interrupt level on the busiest core:</b> ")
                  .Append(DpcBaseline(inc).ToString("0.#", Inv)).Append("%</li>");
                if (inc.RunningApps.Count > 0) sb.Append("<li><b>Running:</b> ").Append(H(string.Join(", ", inc.RunningApps.ToArray()))).Append("</li>");
                foreach (var d in inc.Displays) sb.Append("<li><b>Display:</b> ").Append(H(d)).Append("</li>");
                sb.Append("</ul>");

                // Charts.
                if (inc.Samples.Count >= 2)
                {
                    sb.Append("<div class=\"charts\">");
                    sb.Append(Chart("GPU power", "W", inc, s => s.GpuPowerW, s => s.GpuPowerLimitW, "limit"));
                    sb.Append(Chart("GPU core clock", "MHz", inc, s => s.GpuClockMHz, null, null));
                    sb.Append(Chart("GPU memory clock", "MHz", inc, s => s.MemClockMHz, null, null));
                    sb.Append(Chart("GPU temperature", "C", inc, s => s.GpuTempC, null, null));
                    sb.Append(Chart("CPU use", "%", inc, s => s.CpuPct, s => s.CpuMaxCorePct, "busiest core"));
                    sb.Append(Chart("DPC / interrupt time (busiest core)", "%", inc, s => s.DpcPct, s => s.IntPct, "interrupt"));
                    sb.Append(Chart("Heartbeat delay", "ms", inc, s => s.MaxGapMs, null, null));
                    sb.Append(Chart("GPU use", "%", inc, s => s.GpuUtil, null, null));
                    sb.Append("</div>");
                    var throttles = inc.Samples.Where(s => s.ThrottleMask > 1).Select(s => Nvml.DescribeReasons(s.ThrottleMask)).Distinct().ToList();
                    if (throttles.Count > 0)
                        sb.Append("<p class=\"muted small\">GPU clock limit reasons seen: ").Append(H(string.Join("; ", throttles.ToArray()))).Append("</p>");
                }

                // Triggers and events.
                sb.Append("<details><summary>Triggers and events (").Append(inc.Events.Count).Append(")</summary><ul class=\"mono\">");
                foreach (var t in inc.Triggers) sb.Append("<li>").Append(H(t)).Append("</li>");
                sb.Append("</ul><table class=\"small\"><tr><th>Time</th><th>Source</th><th>Event</th><th>Message</th></tr>");
                foreach (var e in inc.Events)
                    sb.Append("<tr><td>").Append(e.Time.ToString("HH:mm:ss.fff")).Append("</td><td>").Append(H(e.Source))
                      .Append("</td><td>").Append(H(e.Provider)).Append(" ").Append(e.Id).Append("</td><td>").Append(H(Trim(e.Message, 300))).Append("</td></tr>");
                sb.Append("</table></details></article>");
            }
            sb.Append("</section>");
        }

        // A small line chart of one or two series from -60 s to +10 s, with
        // the incident moment marked at 0.
        static string Chart(string title, string unit, Incident inc, Func<Sample, double> f, Func<Sample, double> f2, string name2)
        {
            const double W = 300, Hh = 120, L = 42, R = 8, T = 22, B = 18;
            double xMin = -Config.PreSeconds, xMax = Config.PostSeconds;
            double peak = Max(inc.Samples, f);
            double yMax = Math.Max(peak, f2 == null ? -1 : Max(inc.Samples, f2));
            if (unit == "%") yMax = Math.Max(yMax, 10);
            if (yMax <= 0) yMax = 1;
            yMax *= 1.1;

            Func<double, double> X = x => L + (Math.Max(xMin, Math.Min(xMax, x)) - xMin) / (xMax - xMin) * (W - L - R);
            Func<double, double> Y = y => T + (1 - y / yMax) * (Hh - T - B);

            var sb = new StringBuilder();
            sb.Append("<figure class=\"chart\"><svg viewBox=\"0 0 300 120\" role=\"img\" aria-label=\"").Append(H(title)).Append("\">");
            sb.Append("<text class=\"ct\" x=\"").Append(F(L)).Append("\" y=\"13\">").Append(H(title))
              .Append(peak >= 0 ? " &middot; peak " + peak.ToString("0.#", Inv) + " " + H(unit) : "").Append("</text>");
            sb.Append("<line class=\"ax\" x1=\"").Append(F(L)).Append("\" y1=\"").Append(F(Hh - B)).Append("\" x2=\"").Append(F(W - R)).Append("\" y2=\"").Append(F(Hh - B)).Append("\"/>");
            sb.Append("<line class=\"grid\" x1=\"").Append(F(L)).Append("\" y1=\"").Append(F(T)).Append("\" x2=\"").Append(F(W - R)).Append("\" y2=\"").Append(F(T)).Append("\"/>");
            sb.Append("<text class=\"tk\" x=\"").Append(F(L - 4)).Append("\" y=\"").Append(F(T + 4)).Append("\" text-anchor=\"end\">").Append((yMax / 1.1).ToString("0", Inv)).Append("</text>");
            sb.Append("<text class=\"tk\" x=\"").Append(F(L - 4)).Append("\" y=\"").Append(F(Hh - B + 4)).Append("\" text-anchor=\"end\">0</text>");
            foreach (int tick in new int[] { -60, -30, 0, 10 })
                sb.Append("<text class=\"tk\" x=\"").Append(F(X(tick))).Append("\" y=\"").Append(F(Hh - 4)).Append("\" text-anchor=\"middle\">")
                  .Append(tick > 0 ? "+" + tick : tick.ToString()).Append("s</text>");

            if (inc.DurationMs > 0)
            {
                double x0 = X(0), x1 = Math.Max(X(inc.DurationMs / 1000.0), x0 + 2);
                sb.Append("<rect class=\"stall\" x=\"").Append(F(x0)).Append("\" y=\"").Append(F(T)).Append("\" width=\"").Append(F(x1 - x0))
                  .Append("\" height=\"").Append(F(Hh - T - B)).Append("\"/>");
            }
            sb.Append("<line class=\"mark\" x1=\"").Append(F(X(0))).Append("\" y1=\"").Append(F(T)).Append("\" x2=\"").Append(F(X(0))).Append("\" y2=\"").Append(F(Hh - B)).Append("\"/>");

            if (f2 != null) sb.Append(Poly(inc, f2, X, Y, "s2"));
            sb.Append(Poly(inc, f, X, Y, "s1"));
            sb.Append("</svg>");
            if (f2 != null) sb.Append("<figcaption><span class=\"k1\"></span>").Append(H(title.Split(' ')[0])).Append(" <span class=\"k2\"></span>").Append(H(name2)).Append("</figcaption>");
            sb.Append("</figure>");
            return sb.ToString();
        }

        static string Poly(Incident inc, Func<Sample, double> f, Func<double, double> X, Func<double, double> Y, string cls)
        {
            var sb = new StringBuilder();
            var pts = new StringBuilder();
            foreach (var s in inc.Samples)
            {
                double v = f(s);
                double rel = Rel(inc, s);
                if (v < 0 || rel < -Config.PreSeconds || rel > Config.PostSeconds)
                {
                    Flush(sb, pts, cls);
                    continue;
                }
                pts.Append(F(X(rel))).Append(',').Append(F(Y(v))).Append(' ');
            }
            Flush(sb, pts, cls);
            return sb.ToString();
        }

        static void Flush(StringBuilder sb, StringBuilder pts, string cls)
        {
            if (pts.Length == 0) return;
            sb.Append("<polyline class=\"").Append(cls).Append("\" points=\"").Append(pts.ToString().Trim()).Append("\"/>");
            pts.Clear();
        }

        // ---------------------------------------------------------------- html

        static string TypeLabel(string type)
        {
            switch (type)
            {
                case "freeze": return "Freeze";
                case "flicker-mark": return "Flicker (marked)";
                case "display-change": return "Display change";
                case "gpu-driver": return "GPU driver error";
                case "whea": return "Hardware error";
                case "app-crash": return "App crash";
                default: return type ?? "";
            }
        }

        static string F(double v) { return v.ToString("0.#", Inv); }

        static string H(string s) { return WebUtility.HtmlEncode(s ?? ""); }

        static string Trim(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length > max ? s.Substring(0, max) + "..." : s;
        }

        static void Head(StringBuilder sb)
        {
            sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
            sb.Append("<title>GPU Monitor Report</title><style>");
            sb.Append(@"
:root{--bg:#f6f7f9;--panel:#fff;--text:#1d2228;--muted:#5f6b78;--line:#dde2e8;--accent:#2563eb;--s2:#9aa5b1;
--bad:#c62828;--warn:#d97706;--ok:#2e7d32;--info:#64748b;--stall:rgba(220,38,38,.18);--badbg:#fdecec}
@media (prefers-color-scheme:dark){:root{--bg:#111418;--panel:#1a1f25;--text:#e6e9ed;--muted:#97a3b0;--line:#2c333b;
--accent:#60a5fa;--s2:#6b7682;--bad:#f87171;--warn:#fbbf24;--ok:#4ade80;--info:#94a3b8;--stall:rgba(248,113,113,.2);--badbg:#3a1d1d}}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:15px/1.5 'Segoe UI',system-ui,sans-serif}
main{max-width:1100px;margin:0 auto;padding:24px 16px 48px}h1{margin:0 0 4px;font-size:26px}h2{margin:32px 0 12px;font-size:20px}
h3{margin:18px 0 8px;font-size:16px}.muted{color:var(--muted)}.small{font-size:13px}code,kbd,.mono{font-family:Consolas,monospace;font-size:13px}
kbd{border:1px solid var(--line);border-radius:4px;padding:1px 5px;background:var(--panel)}
table{border-collapse:collapse;width:100%;background:var(--panel);border:1px solid var(--line);border-radius:8px}
th,td{text-align:left;padding:6px 10px;border-bottom:1px solid var(--line);vertical-align:top}th{font-weight:600;font-size:13px;color:var(--muted)}
td.r,th.r{text-align:right}.scroll{overflow-x:auto}
.dot{display:inline-block;width:10px;height:10px;border-radius:50%;background:var(--info)}.dot.bad{background:var(--bad)}.dot.warn{background:var(--warn)}.dot.ok{background:var(--ok)}
.callout{padding:12px 14px;border-radius:8px;margin:10px 0;border:1px solid var(--line);background:var(--panel)}
.callout.bad{border-color:var(--bad);background:var(--badbg)}
.tiles{display:flex;flex-wrap:wrap;gap:10px;margin:12px 0}.tile{background:var(--panel);border:1px solid var(--line);border-radius:8px;padding:10px 14px;min-width:120px}
.tile .num{font-size:22px;font-weight:600}.tile .lbl{color:var(--muted);font-size:13px}
.card{background:var(--panel);border:1px solid var(--line);border-radius:10px;padding:14px 16px;margin:14px 0}
.cardhead{display:flex;align-items:center;gap:10px;flex-wrap:wrap}.cardhead h3{margin:0}
.badge{font-size:12px;font-weight:600;padding:2px 8px;border-radius:99px;border:1px solid var(--line)}
.t-freeze,.t-gpu-driver,.t-whea{color:var(--bad);border-color:var(--bad)}.t-flicker-mark,.t-display-change{color:var(--warn);border-color:var(--warn)}
.verdicts{padding-left:20px;margin:12px 0}.verdicts li{margin:8px 0}.fix{margin-top:3px;color:var(--muted)}
.str{font-size:11px;text-transform:uppercase;letter-spacing:.04em;font-weight:700;padding:1px 6px;border-radius:4px;border:1px solid currentColor}
.str.likely{color:var(--bad)}.str.possible{color:var(--warn)}.str.weak{color:var(--info)}
.ctx{list-style:none;padding:0;margin:8px 0;font-size:13px}.ctx li{margin:2px 0;overflow-wrap:anywhere}
.charts{display:grid;grid-template-columns:repeat(auto-fill,minmax(260px,1fr));gap:8px;margin:10px 0}
.chart{margin:0;background:var(--bg);border-radius:6px;padding:4px}.chart svg{width:100%;height:auto;display:block}
.chart figcaption{font-size:11px;color:var(--muted);padding:0 6px 2px}
.k1,.k2{display:inline-block;width:12px;height:2px;vertical-align:middle;margin-right:3px;background:var(--accent)}.k2{background:var(--s2);margin-left:8px}
svg .ct{font-size:11px;fill:var(--text);font-weight:600}svg .tk{font-size:9px;fill:var(--muted)}
svg .ax{stroke:var(--line)}svg .grid{stroke:var(--line);stroke-dasharray:2 3}
svg .s1{fill:none;stroke:var(--accent);stroke-width:1.6}svg .s2{fill:none;stroke:var(--s2);stroke-width:1.2;stroke-dasharray:3 2}
svg .mark{stroke:var(--bad);stroke-width:1}svg .stall{fill:var(--stall)}
details{margin-top:8px}summary{cursor:pointer;color:var(--muted);font-size:13px}footer{margin-top:40px;font-size:13px}
");
            sb.Append("</style></head><body><main>");
        }
    }
}
