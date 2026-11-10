using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace GpuMonitor
{
    // CPU, DPC, interrupt and memory counters through the PDH API, plus the
    // foreground app. PDH is used directly instead of PerformanceCounter
    // because it uses less memory and the English counter names work on any
    // Windows display language.
    class SystemStats : IDisposable
    {
        const uint PDH_FMT_DOUBLE = 0x00000200;
        const uint PDH_FMT_NOCAP100 = 0x00008000;
        const int PDH_MORE_DATA = unchecked((int)0x800007D2);
        const int ItemSize = 24; // PDH_FMT_COUNTERVALUE_ITEM_W on x64

        [StructLayout(LayoutKind.Explicit, Size = 16)]
        struct PdhValue
        {
            [FieldOffset(0)] public uint CStatus;
            [FieldOffset(8)] public double Value;
        }

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        static extern int PdhOpenQueryW(string source, IntPtr user, out IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        static extern int PdhAddEnglishCounterW(IntPtr query, string path, IntPtr user, out IntPtr counter);
        [DllImport("pdh.dll")]
        static extern int PdhCollectQueryData(IntPtr query);
        [DllImport("pdh.dll")]
        static extern int PdhGetFormattedCounterValue(IntPtr counter, uint format, IntPtr type, out PdhValue value);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        static extern int PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr buffer);
        [DllImport("pdh.dll")]
        static extern int PdhCloseQuery(IntPtr query);

        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int max);
        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder name, ref uint size);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);

        IntPtr query;
        IntPtr cpuTotal, cpuAll, dpcAll, intAll, perf, freq, mem;
        IntPtr arrayBuffer = IntPtr.Zero;
        uint arrayBufferSize;
        readonly System.Diagnostics.Stopwatch sinceCollect = System.Diagnostics.Stopwatch.StartNew();

        readonly object fgLock = new object();
        readonly Dictionary<uint, string> nameCache = new Dictionary<uint, string>();
        DateTime cacheClearedAt = DateTime.Now;

        public SystemStats()
        {
            if (PdhOpenQueryW(null, IntPtr.Zero, out query) != 0) { query = IntPtr.Zero; return; }
            cpuTotal = Add(@"\Processor(_Total)\% Processor Time");
            cpuAll = Add(@"\Processor(*)\% Processor Time");
            dpcAll = Add(@"\Processor(*)\% DPC Time");
            intAll = Add(@"\Processor(*)\% Interrupt Time");
            perf = Add(@"\Processor Information(_Total)\% Processor Performance");
            freq = Add(@"\Processor Information(_Total)\Processor Frequency");
            mem = Add(@"\Memory\Available MBytes");
            // Rate counters need a first collection before they return values.
            PdhCollectQueryData(query);
        }

        IntPtr Add(string path)
        {
            IntPtr c;
            if (PdhAddEnglishCounterW(query, path, IntPtr.Zero, out c) != 0) return IntPtr.Zero;
            return c;
        }

        public void Fill(Sample s)
        {
            // Rate counters over a very short interval give meaningless
            // values (such as 100% CPU), so skip them in that case.
            bool longEnough = sinceCollect.ElapsedMilliseconds >= 400;
            if (longEnough) sinceCollect.Restart();
            if (longEnough && query != IntPtr.Zero && PdhCollectQueryData(query) == 0)
            {
                s.CpuPct = Round(ReadSingle(cpuTotal));
                s.CpuMaxCorePct = Round(ReadMax(cpuAll));
                s.DpcPct = Round(ReadMax(dpcAll));
                s.IntPct = Round(ReadMax(intAll));
                double p = ReadSingle(perf);
                double f = ReadSingle(freq);
                if (p >= 0 && f > 0) s.CpuMHz = Math.Round(f * p / 100.0);
                s.AvailMemMB = Math.Round(ReadSingle(mem));
            }
            string title;
            s.Fg = Foreground(false, out title);
        }

        static double Round(double v) { return v < 0 ? v : Math.Round(v, 1); }

        double ReadSingle(IntPtr counter)
        {
            if (counter == IntPtr.Zero) return -1;
            PdhValue v;
            if (PdhGetFormattedCounterValue(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, IntPtr.Zero, out v) != 0) return -1;
            if (v.CStatus > 1) return -1;
            return v.Value;
        }

        // Highest value across all instances except _Total.
        double ReadMax(IntPtr counter)
        {
            if (counter == IntPtr.Zero) return -1;
            uint size = arrayBufferSize;
            uint count;
            int rc = PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out count, arrayBuffer);
            if (rc == PDH_MORE_DATA)
            {
                if (arrayBuffer != IntPtr.Zero) Marshal.FreeHGlobal(arrayBuffer);
                arrayBufferSize = size + 1024;
                arrayBuffer = Marshal.AllocHGlobal((int)arrayBufferSize);
                size = arrayBufferSize;
                rc = PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out count, arrayBuffer);
            }
            if (rc != 0) return -1;
            double max = -1;
            for (int i = 0; i < count; i++)
            {
                IntPtr item = new IntPtr(arrayBuffer.ToInt64() + i * ItemSize);
                IntPtr name = Marshal.ReadIntPtr(item);
                if (name != IntPtr.Zero && Marshal.ReadInt16(name) == '_') continue;
                uint status = (uint)Marshal.ReadInt32(item, 8);
                if (status > 1) continue;
                double v = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, 16));
                if (v > max) max = v;
            }
            return max;
        }

        // Returns the process name of the foreground window, and its title
        // when wantTitle is set (titles are only stored in incident files).
        public string Foreground(bool wantTitle, out string title)
        {
            title = "";
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return "";
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            if (wantTitle)
            {
                var sb = new StringBuilder(256);
                GetWindowTextW(hwnd, sb, sb.Capacity);
                title = sb.ToString();
            }
            lock (fgLock)
            {
                // Clear now and then so that a reused process id cannot keep
                // an old name for long.
                if ((DateTime.Now - cacheClearedAt).TotalMinutes > 5 || nameCache.Count > 256)
                {
                    nameCache.Clear();
                    cacheClearedAt = DateTime.Now;
                }
                string name;
                if (nameCache.TryGetValue(pid, out name)) return name;
                name = ProcessName(pid);
                nameCache[pid] = name;
                return name;
            }
        }

        static string ProcessName(uint pid)
        {
            IntPtr h = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
            if (h == IntPtr.Zero) return "pid" + pid;
            try
            {
                var sb = new StringBuilder(520);
                uint size = (uint)sb.Capacity;
                if (!QueryFullProcessImageNameW(h, 0, sb, ref size)) return "pid" + pid;
                return Path.GetFileNameWithoutExtension(sb.ToString());
            }
            finally { CloseHandle(h); }
        }

        public void Dispose()
        {
            if (query != IntPtr.Zero) { PdhCloseQuery(query); query = IntPtr.Zero; }
            if (arrayBuffer != IntPtr.Zero) { Marshal.FreeHGlobal(arrayBuffer); arrayBuffer = IntPtr.Zero; }
        }
    }
}
