using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace GpuMonitor
{
    // Reads NVIDIA GPU telemetry straight from nvml.dll, which is the same
    // library nvidia-smi uses, so that no child process is started each second.
    static class Nvml
    {
        const string Dll = "nvml.dll";
        const int Success = 0;

        [StructLayout(LayoutKind.Sequential)]
        struct Utilization { public uint Gpu; public uint Memory; }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr LoadLibraryW(string path);

        [DllImport(Dll)] static extern int nvmlInit_v2();
        [DllImport(Dll)] static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
        [DllImport(Dll, CharSet = CharSet.Ansi)] static extern int nvmlDeviceGetName(IntPtr device, StringBuilder name, uint length);
        [DllImport(Dll, CharSet = CharSet.Ansi)] static extern int nvmlSystemGetDriverVersion(StringBuilder version, uint length);
        [DllImport(Dll)] static extern int nvmlDeviceGetTemperature(IntPtr device, int sensor, out uint temp);
        [DllImport(Dll)] static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);
        [DllImport(Dll)] static extern int nvmlDeviceGetEnforcedPowerLimit(IntPtr device, out uint milliwatts);
        [DllImport(Dll)] static extern int nvmlDeviceGetPowerManagementDefaultLimit(IntPtr device, out uint milliwatts);
        [DllImport(Dll)] static extern int nvmlDeviceGetClockInfo(IntPtr device, int type, out uint mhz);
        [DllImport(Dll)] static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out Utilization u);
        [DllImport(Dll)] static extern int nvmlDeviceGetPerformanceState(IntPtr device, out int pstate);
        [DllImport(Dll)] static extern int nvmlDeviceGetCurrPcieLinkGeneration(IntPtr device, out uint gen);
        [DllImport(Dll)] static extern int nvmlDeviceGetCurrPcieLinkWidth(IntPtr device, out uint width);
        [DllImport(Dll)] static extern int nvmlDeviceGetPcieReplayCounter(IntPtr device, out uint value);
        [DllImport(Dll)] static extern int nvmlDeviceGetEncoderStats(IntPtr device, out uint sessions, out uint fps, out uint latency);
        [DllImport(Dll)] static extern int nvmlDeviceGetFanSpeed(IntPtr device, out uint pct);
        [DllImport(Dll)] static extern int nvmlDeviceGetCurrentClocksEventReasons(IntPtr device, out ulong reasons);
        [DllImport(Dll)] static extern int nvmlDeviceGetCurrentClocksThrottleReasons(IntPtr device, out ulong reasons);

        static readonly object initLock = new object();
        static bool tried;
        static bool ok;
        static IntPtr device;
        static bool useEventReasons = true;

        public static string GpuName = "";
        public static string DriverVersion = "";
        public static double DefaultPowerLimitW = -1;
        public static string InitError = "";

        public static bool Available { get { return ok; } }

        public static bool Init()
        {
            lock (initLock)
            {
                if (tried) return ok;
                tried = true;
                try
                {
                    // Newer drivers put nvml.dll in System32; older ones use NVSMI.
                    string sys32 = Path.Combine(Environment.SystemDirectory, Dll);
                    string nvsmi = @"C:\Program Files\NVIDIA Corporation\NVSMI\nvml.dll";
                    if (File.Exists(sys32)) LoadLibraryW(sys32);
                    else if (File.Exists(nvsmi)) LoadLibraryW(nvsmi);

                    int rc = nvmlInit_v2();
                    if (rc != Success) { InitError = "nvmlInit failed with code " + rc; return false; }
                    rc = nvmlDeviceGetHandleByIndex_v2(0, out device);
                    if (rc != Success) { InitError = "No NVIDIA GPU at index 0 (code " + rc + ")"; return false; }

                    var sb = new StringBuilder(96);
                    if (nvmlDeviceGetName(device, sb, (uint)sb.Capacity) == Success) GpuName = sb.ToString();
                    sb = new StringBuilder(96);
                    if (nvmlSystemGetDriverVersion(sb, (uint)sb.Capacity) == Success) DriverVersion = sb.ToString();
                    uint mw;
                    if (nvmlDeviceGetPowerManagementDefaultLimit(device, out mw) == Success) DefaultPowerLimitW = mw / 1000.0;
                    ok = true;
                }
                catch (Exception ex)
                {
                    InitError = ex.GetType().Name + ": " + ex.Message;
                    ok = false;
                }
                return ok;
            }
        }

        public static void Fill(Sample s)
        {
            if (!ok) return;
            uint v;
            if (nvmlDeviceGetTemperature(device, 0, out v) == Success) s.GpuTempC = v;
            if (nvmlDeviceGetPowerUsage(device, out v) == Success) s.GpuPowerW = Math.Round(v / 1000.0, 1);
            if (nvmlDeviceGetEnforcedPowerLimit(device, out v) == Success) s.GpuPowerLimitW = Math.Round(v / 1000.0, 1);
            if (nvmlDeviceGetClockInfo(device, 0, out v) == Success) s.GpuClockMHz = (int)v;
            if (nvmlDeviceGetClockInfo(device, 2, out v) == Success) s.MemClockMHz = (int)v;
            Utilization u;
            if (nvmlDeviceGetUtilizationRates(device, out u) == Success) s.GpuUtil = (int)u.Gpu;
            int p;
            if (nvmlDeviceGetPerformanceState(device, out p) == Success) s.PState = p;
            if (nvmlDeviceGetCurrPcieLinkGeneration(device, out v) == Success) s.PcieGen = (int)v;
            if (nvmlDeviceGetCurrPcieLinkWidth(device, out v) == Success) s.PcieWidth = (int)v;
            if (nvmlDeviceGetPcieReplayCounter(device, out v) == Success) s.PcieReplays = v;
            uint fps, lat;
            if (nvmlDeviceGetEncoderStats(device, out v, out fps, out lat) == Success) s.EncoderSessions = (int)v;
            if (nvmlDeviceGetFanSpeed(device, out v) == Success) s.FanPct = (int)v;
            s.ThrottleMask = ReadReasons();
        }

        static long ReadReasons()
        {
            ulong r;
            if (useEventReasons)
            {
                try
                {
                    if (nvmlDeviceGetCurrentClocksEventReasons(device, out r) == Success) return (long)r;
                    return -1;
                }
                catch (EntryPointNotFoundException)
                {
                    // Older drivers only export the deprecated name.
                    useEventReasons = false;
                }
            }
            try
            {
                if (nvmlDeviceGetCurrentClocksThrottleReasons(device, out r) == Success) return (long)r;
            }
            catch (EntryPointNotFoundException) { }
            return -1;
        }

        public const long ReasonHwSlowdown = 0x8;
        public const long ReasonSwThermal = 0x20;
        public const long ReasonHwThermal = 0x40;
        public const long ReasonHwPowerBrake = 0x80;
        public const long ReasonSwPowerCap = 0x4;

        public static string DescribeReasons(long mask)
        {
            if (mask < 0) return "n/a";
            if (mask == 0) return "none";
            var sb = new StringBuilder();
            Append(sb, mask, 0x1, "idle");
            Append(sb, mask, 0x2, "app-clocks");
            Append(sb, mask, 0x4, "sw-power-cap");
            Append(sb, mask, 0x8, "hw-slowdown");
            Append(sb, mask, 0x10, "sync-boost");
            Append(sb, mask, 0x20, "sw-thermal");
            Append(sb, mask, 0x40, "hw-thermal");
            Append(sb, mask, 0x80, "hw-power-brake");
            Append(sb, mask, 0x100, "display-clock");
            return sb.Length == 0 ? "0x" + mask.ToString("x") : sb.ToString();
        }

        static void Append(StringBuilder sb, long mask, long bit, string name)
        {
            if ((mask & bit) == 0) return;
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(name);
        }
    }
}
