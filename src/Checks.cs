using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Text;
using Microsoft.Win32;

namespace GpuMonitor
{
    class CheckItem
    {
        public string Key;
        public string Name;
        public string Value;
        public string Status; // ok, info, warn, bad
        public string Note;
    }

    // One-time look at the configuration that matters for these symptoms.
    static class Checks
    {
        // AGESA 1.2.0.7, which contains the AMD fTPM stutter fix, reached
        // motherboard BIOS releases around May 2022.
        static readonly DateTime FtpmFixDate = new DateTime(2022, 5, 1);

        public static List<CheckItem> Run()
        {
            var list = new List<CheckItem>();
            string cpu = Reg(Registry.LocalMachine, @"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString").Trim();
            bool ryzen = cpu.IndexOf("Ryzen", StringComparison.OrdinalIgnoreCase) >= 0;

            Add(list, "cpu", "CPU", cpu, "info", "");
            string board = (Reg(Registry.LocalMachine, @"HARDWARE\DESCRIPTION\System\BIOS", "BaseBoardManufacturer") + " " +
                            Reg(Registry.LocalMachine, @"HARDWARE\DESCRIPTION\System\BIOS", "BaseBoardProduct")).Trim();
            Add(list, "board", "Motherboard", board, "info", "");

            // BIOS age versus the fTPM fix.
            string biosVer = Reg(Registry.LocalMachine, @"HARDWARE\DESCRIPTION\System\BIOS", "BIOSVersion");
            string biosDateText = Reg(Registry.LocalMachine, @"HARDWARE\DESCRIPTION\System\BIOS", "BIOSReleaseDate");
            DateTime biosDate;
            bool haveDate = DateTime.TryParseExact(biosDateText, "MM/dd/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out biosDate);
            string biosValue = biosVer + (haveDate ? " (" + biosDate.ToString("yyyy-MM-dd") + ")" : "");
            if (ryzen && haveDate && biosDate < FtpmFixDate)
                Add(list, "bios", "BIOS", biosValue, "bad",
                    "This BIOS predates AGESA 1.2.0.7, so it still has the AMD fTPM bug that causes random 1 to 3 second " +
                    "system stutters with looping audio. Updating to the newest BIOS for this board is the first fix to try.");
            else if (ryzen && haveDate)
                Add(list, "bios", "BIOS", biosValue, "ok", "Recent enough to include the AMD fTPM stutter fix.");
            else
                Add(list, "bios", "BIOS", biosValue, "info", "");

            // TPM type.
            string tpm = TpmManufacturer();
            if (tpm == "AMD")
                Add(list, "tpm", "TPM", "AMD firmware TPM (fTPM)", ryzen && haveDate && biosDate < FtpmFixDate ? "bad" : "info",
                    "The fTPM is the part affected by the stutter bug on older BIOS versions.");
            else if (tpm.Length > 0)
                Add(list, "tpm", "TPM", tpm, "info", "");
            else
                Add(list, "tpm", "TPM", "unknown", "info", "Reading the TPM type may require running the check as administrator.");

            // GPU.
            if (Nvml.Init())
            {
                var s = new Sample();
                Nvml.Fill(s);
                Add(list, "gpu", "GPU", Nvml.GpuName, "info", "");
                Add(list, "driver", "NVIDIA driver", Nvml.DriverVersion, "info", "");
                if (s.GpuPowerLimitW > 0 && Nvml.DefaultPowerLimitW > 0)
                {
                    bool changed = Math.Abs(s.GpuPowerLimitW - Nvml.DefaultPowerLimitW) > 2;
                    Add(list, "powerlimit", "GPU power limit",
                        string.Format(CultureInfo.InvariantCulture, "{0:0} W (default {1:0} W)", s.GpuPowerLimitW, Nvml.DefaultPowerLimitW),
                        changed ? "warn" : "ok",
                        changed ? "The power limit was changed from the default, usually by MSI Afterburner." : "");
                }
                if (s.PcieGen > 0)
                    Add(list, "pcie", "PCIe link (now)", "Gen " + s.PcieGen + " x" + s.PcieWidth, "info",
                        "A low generation at idle is normal power saving; the report shows it under load.");
            }
            else
            {
                Add(list, "gpu", "GPU", "NVML unavailable", "warn", Nvml.InitError);
            }

            // Driver resets and hardware errors in the last 90 days.
            var tdr = EventWatch.Query("System", EventWatch.WithinDays("Provider[@Name='nvlddmkm' or @Name='Display']", 90), 200);
            Add(list, "tdr", "GPU driver errors (90 days)", tdr.Count + (tdr.Count > 0 ? ", last " + tdr[0].Time.ToString("yyyy-MM-dd HH:mm") : ""),
                tdr.Count == 0 ? "ok" : "warn",
                tdr.Count == 0 ? "" : "The NVIDIA driver logged errors or timeout resets (TDR). Latest: " + Trim(tdr[0].Message, 160));

            var whea = EventWatch.Query("System", EventWatch.WithinDays("Provider[@Name='Microsoft-Windows-WHEA-Logger']", 90), 200);
            Add(list, "whea", "WHEA hardware errors (90 days)", whea.Count.ToString(), whea.Count == 0 ? "ok" : "bad",
                whea.Count == 0 ? "" : "Hardware errors point at CPU, memory or PCIe instability. Latest: " + Trim(whea[0].Message, 160));

            var kp = EventWatch.Query("System", EventWatch.WithinDays("Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=41", 90), 200);
            Add(list, "kp41", "Unexpected restarts (90 days)", kp.Count + (kp.Count > 0 ? ", last " + kp[0].Time.ToString("yyyy-MM-dd HH:mm") : ""),
                kp.Count == 0 ? "ok" : "warn", kp.Count == 0 ? "" : "Kernel-Power 41 means the PC restarted without shutting down cleanly.");

            // Windows graphics settings.
            object hags = RegValue(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode");
            string hagsText = hags == null ? "default" : (Convert.ToInt32(hags) == 2 ? "on" : "off");
            Add(list, "hags", "Hardware-accelerated GPU scheduling", hagsText, "info", "");

            object mpo = RegValue(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\Dwm", "OverlayTestMode");
            bool mpoOff = mpo != null && Convert.ToInt32(mpo) == 5;
            Add(list, "mpo", "Multiplane overlay (MPO)", mpoOff ? "disabled" : "enabled (Windows default)", "info",
                mpoOff ? "" : "MPO is a known cause of black flicker during video playback on NVIDIA cards with several monitors.");

            Add(list, "power", "Power plan", PowerPlan(), "info", "");

            // Monitors.
            var modes = DisplayInfo.Modes();
            foreach (var m in modes)
            {
                Add(list, "monitor", "Monitor " + m.Device.Replace(@"\\.\", ""),
                    string.Format("{0} [{1}] {2}x{3} @ {4} Hz{5}", m.Monitor, m.MonitorId, m.Width, m.Height, m.Hz, m.Primary ? ", primary" : ""),
                    "info", "");
            }
            if (modes.Select(m => m.Hz).Distinct().Count() > 1)
                Add(list, "refresh", "Refresh rates", "different across monitors", "warn",
                    "Different refresh rates can make the GPU memory clock switch visible as a flicker when video starts.");

            // Software that is known to matter.
            var running = new List<string>();
            foreach (var name in new string[] { "MSIAfterburner", "RTSS", "ArmourySocketServer", "wallpaper64", "wallpaper32", "iCUE", "SignalRgb", "voicemeeter8x64", "voicemeeterpro", "voicemeeter", "obs64" })
            {
                Process[] ps = Process.GetProcessesByName(name);
                if (ps.Length > 0) running.Add(name);
                foreach (var p in ps) p.Dispose();
            }
            bool afterburner = running.Contains("MSIAfterburner");
            bool wallpaper = running.Contains("wallpaper32") || running.Contains("wallpaper64");
            string note = "";
            if (afterburner) note += "If an overclock, undervolt or custom curve is applied in Afterburner, reset it to default while testing, since that is a common cause of TDRs and black screens. ";
            if (wallpaper) note += "Wallpaper Engine is running; it uses the GPU and video overlays on the desktop, so pause or close it once while testing the flicker.";
            Add(list, "software", "Relevant software running", running.Count == 0 ? "none" : string.Join(", ", running.ToArray()),
                afterburner || wallpaper ? "warn" : "info", note.Trim());

            return list;
        }

        public static bool BiosPredatesFix(List<CheckItem> checks)
        {
            return checks.Any(c => c.Key == "bios" && c.Status == "bad");
        }

        public static bool AfterburnerRunning(List<CheckItem> checks)
        {
            return checks.Any(c => c.Key == "software" && c.Value.Contains("MSIAfterburner"));
        }

        public static string WriteTextReport(string baseDir)
        {
            var checks = Run();
            var sb = new StringBuilder();
            sb.AppendLine("GPU Monitor health check, " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
            sb.AppendLine(new string('=', 60));
            foreach (var c in checks)
            {
                string mark = c.Status == "bad" ? "[!!]" : c.Status == "warn" ? "[! ]" : c.Status == "ok" ? "[ok]" : "[  ]";
                sb.AppendLine(string.Format("{0} {1,-38} {2}", mark, c.Name, c.Value));
                if (!string.IsNullOrEmpty(c.Note)) sb.AppendLine("       " + c.Note);
            }
            sb.AppendLine();
            sb.AppendLine("[!!] likely related to the problem   [! ] worth checking   [ok] fine");
            string dir = Path.Combine(baseDir, "reports");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "check-" + DateTime.Now.ToString("yyyy-MM-dd_HHmm") + ".txt");
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            return path;
        }

        static void Add(List<CheckItem> list, string key, string name, string value, string status, string note)
        {
            var c = new CheckItem();
            c.Key = key; c.Name = name; c.Value = value ?? ""; c.Status = status; c.Note = note ?? "";
            list.Add(c);
        }

        static string Trim(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length > max ? s.Substring(0, max) + "..." : s;
        }

        static string Reg(RegistryKey root, string path, string name)
        {
            object v = RegValue(root, path, name);
            return v == null ? "" : Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        static object RegValue(RegistryKey root, string path, string name)
        {
            try
            {
                using (var k = root.OpenSubKey(path))
                {
                    return k == null ? null : k.GetValue(name);
                }
            }
            catch { return null; }
        }

        static string TpmManufacturer()
        {
            try
            {
                var scope = new ManagementScope(@"root\CIMV2\Security\MicrosoftTpm");
                using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT ManufacturerIdTxt FROM Win32_Tpm")))
                {
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        using (mo)
                        {
                            object v = mo["ManufacturerIdTxt"];
                            if (v != null) return Convert.ToString(v).Trim();
                        }
                    }
                }
            }
            catch { }
            return "";
        }

        static string PowerPlan()
        {
            string guid = Reg(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes", "ActivePowerScheme").ToLowerInvariant();
            switch (guid)
            {
                case "381b4222-f694-41f0-9685-ff5bb260df2e": return "Balanced";
                case "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c": return "High performance";
                case "a1841308-3541-4fab-bc81-f71556f20b4a": return "Power saver";
                case "e9a42b02-d5df-448d-aa00-03f14749eb61": return "Ultimate performance";
                case "": return "unknown";
                default:
                    string name = Reg(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes\" + guid, "FriendlyName");
                    if (name.Contains(",")) name = name.Substring(name.LastIndexOf(',') + 1);
                    return name.Length > 0 ? name : "custom (" + guid + ")";
            }
        }
    }
}
