using System;
using System.Diagnostics;
using System.Threading;
using Microsoft.Win32;

namespace GpuMonitor
{
    // Freeze detector. A high priority thread wakes up every HeartbeatMs and
    // measures how late it woke up, using the hardware performance counter.
    // When the whole system stalls (as in the AMD fTPM bug, a driver hang or
    // a DPC storm) this thread stalls too, so the delay is the freeze length.
    class Heartbeat
    {
        public event Action<DateTime, double> Freeze;

        Thread thread;
        volatile bool running;
        volatile bool suspended;
        long ignoreUntilTicks;
        readonly object gapLock = new object();
        double maxGapSinceTake;

        public void Start()
        {
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            running = true;
            thread = new Thread(Run);
            thread.Name = "Heartbeat";
            thread.IsBackground = true;
            thread.Priority = ThreadPriority.Highest;
            thread.Start();
        }

        public void Stop()
        {
            running = false;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            if (thread != null) thread.Join(1000);
        }

        // Sleep and resume look like a huge freeze, so they are ignored.
        void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Suspend) suspended = true;
            else if (e.Mode == PowerModes.Resume)
            {
                suspended = false;
                Interlocked.Exchange(ref ignoreUntilTicks, DateTime.UtcNow.AddSeconds(10).Ticks);
            }
        }

        // Returns the longest extra delay since the previous call, then resets it.
        public double TakeMaxGap()
        {
            lock (gapLock)
            {
                double v = maxGapSinceTake;
                maxGapSinceTake = 0;
                return Math.Round(v);
            }
        }

        void Run()
        {
            var sw = Stopwatch.StartNew();
            double last = sw.Elapsed.TotalMilliseconds;
            while (running)
            {
                Thread.Sleep(Config.HeartbeatMs);
                double now = sw.Elapsed.TotalMilliseconds;
                double gap = now - last - Config.HeartbeatMs;
                last = now;

                if (suspended || DateTime.UtcNow.Ticks < Interlocked.Read(ref ignoreUntilTicks)) continue;
                if (gap < 0) gap = 0;

                lock (gapLock)
                {
                    if (gap > maxGapSinceTake) maxGapSinceTake = gap;
                }

                if (gap >= Config.FreezeThresholdMs && gap < Config.MaxPlausibleFreezeMs)
                {
                    DateTime start = DateTime.Now.AddMilliseconds(-gap);
                    var handler = Freeze;
                    if (handler != null)
                    {
                        try { handler(start, gap); }
                        catch (Exception ex) { Program.LogError("Heartbeat handler", ex); }
                    }
                    // The handler may take a moment; do not count that time.
                    last = sw.Elapsed.TotalMilliseconds;
                }
            }
        }
    }

    // Collects one Sample per second and hands it to the Recorder.
    class Sampler
    {
        readonly SystemStats sys;
        readonly Heartbeat heartbeat;
        readonly Recorder recorder;
        Thread thread;
        volatile bool running;

        public Sampler(SystemStats sys, Heartbeat heartbeat, Recorder recorder)
        {
            this.sys = sys;
            this.heartbeat = heartbeat;
            this.recorder = recorder;
        }

        public void Start()
        {
            running = true;
            thread = new Thread(Run);
            thread.Name = "Sampler";
            thread.IsBackground = true;
            thread.Priority = ThreadPriority.AboveNormal;
            thread.Start();
        }

        public void Stop()
        {
            running = false;
            if (thread != null) thread.Join(3000);
        }

        void Run()
        {
            var sw = Stopwatch.StartNew();
            long n = 0;
            while (running)
            {
                n++;
                const long I = Config.SampleIntervalMs;
                long target = n * I;
                long wait = target - sw.ElapsedMilliseconds;
                if (wait > 0) Thread.Sleep((int)wait);
                // After a stall (which may happen during the sleep itself),
                // skip the missed seconds instead of taking samples back to
                // back, and keep the next one at least half an interval away.
                long now = sw.ElapsedMilliseconds;
                if (now - target > I / 2) n = (now + I / 2 + I - 1) / I - 1;
                if (!running) break;

                try
                {
                    var s = new Sample();
                    s.Time = DateTime.Now;
                    try { Nvml.Fill(s); } catch (Exception ex) { Program.LogError("Nvml.Fill", ex); }
                    try { sys.Fill(s); } catch (Exception ex) { Program.LogError("SystemStats.Fill", ex); }
                    s.MaxGapMs = heartbeat.TakeMaxGap();
                    recorder.AddSample(s);
                }
                catch (Exception ex)
                {
                    Program.LogError("Sampler", ex);
                }
            }
        }
    }
}
