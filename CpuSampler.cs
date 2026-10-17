using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace TopBar
{
    /// <summary>
    /// Per-core CPU usage via NtQuerySystemInformation(SystemProcessorPerformanceInformation)
    /// — a single fast syscall per sample (same source Task Manager uses).
    /// Replaces per-tick WMI queries, which cost ~100-300ms each.
    /// Falls back to WMI transparently if the syscall is unavailable.
    ///
    /// Sampling runs once per second on a background thread and the latest
    /// result is cached, so reading it (bar or popup) is instant and there is
    /// never a "0% for the first second" flash on open.
    /// </summary>
    internal static class CpuSampler
    {
        [DllImport("ntdll.dll")]
        private static extern int NtQuerySystemInformation(
            int systemInformationClass, IntPtr systemInformation,
            int systemInformationLength, out int returnLength);

        private const int SystemProcessorPerformanceInformation = 8;

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessorPerformance
        {
            public long IdleTime;
            public long KernelTime;
            public long UserTime;
            public long DpcTime;
            public long InterruptTime;
            public uint InterruptCount;
        }

        private static readonly object Sync = new();
        private static long[] _prevIdle = Array.Empty<long>();
        private static long[] _prevTotal = Array.Empty<long>();
        private static bool _primed;
        private static (double Total, List<(string Core, double Pct)> Cores) _latest =
            (0, new List<(string, double)>());
        private static System.Threading.Timer? _timer;

        /// <summary>
        /// Latest cached total + per-core usage. Starts the background sampler
        /// on first call, so the first value after launch is available within
        /// a second and every later read is immediate.
        /// </summary>
        public static (double Total, List<(string Core, double Pct)> Cores) Sample()
        {
            EnsureRunning();
            lock (Sync) return _latest;
        }

        private static void EnsureRunning()
        {
            if (_timer != null) return;
            lock (Sync)
            {
                if (_timer != null) return;
                _timer = new System.Threading.Timer(_ => Update(), null, 0, 1000);
            }
        }

        private static void Update()
        {
            try
            {
                var v = Compute();
                lock (Sync) _latest = v;
            }
            catch { }
        }

        private static (double, List<(string, double)>) Compute()
        {
            try
            {
                return ComputeNative();
            }
            catch
            {
                return ComputeWmiFallback();
            }
        }

        private static (double, List<(string, double)>) ComputeNative()
        {
            int cores = Environment.ProcessorCount;
            int stride = Marshal.SizeOf<ProcessorPerformance>();
            int size = cores * stride;
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                int status = NtQuerySystemInformation(
                    SystemProcessorPerformanceInformation, buf, size, out int needed);
                if (status != 0)
                {
                    if (needed <= size) return ComputeWmiFallback();
                    Marshal.FreeHGlobal(buf);
                    buf = Marshal.AllocHGlobal(needed);
                    if (NtQuerySystemInformation(SystemProcessorPerformanceInformation, buf, needed, out _) != 0)
                        return ComputeWmiFallback();
                    cores = needed / stride;
                }

                if (_prevIdle.Length != cores)
                {
                    _prevIdle = new long[cores];
                    _prevTotal = new long[cores];
                    _primed = false;
                }

                var list = new List<(string, double)>(cores);
                long sumBusy = 0, sumTotalAll = 0;
                for (int i = 0; i < cores; i++)
                {
                    var p = Marshal.PtrToStructure<ProcessorPerformance>(buf + i * stride);
                    long idle = p.IdleTime;
                    long total = p.KernelTime + p.UserTime; // kernel time includes idle
                    double pct = 0;
                    if (_primed)
                    {
                        long dTotal = total - _prevTotal[i];
                        long dBusy = (total - idle) - (_prevTotal[i] - _prevIdle[i]);
                        if (dTotal > 0)
                        {
                            pct = Math.Clamp(100.0 * dBusy / dTotal, 0, 100);
                            sumBusy += Math.Max(0, dBusy);
                            sumTotalAll += dTotal;
                        }
                    }
                    _prevIdle[i] = idle;
                    _prevTotal[i] = total;
                    list.Add((i.ToString(), pct));
                }

                bool first = !_primed;
                _primed = true;
                double totalPct = first || sumTotalAll <= 0
                    ? 0
                    : Math.Clamp(100.0 * sumBusy / sumTotalAll, 0, 100);
                return (totalPct, list);
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }

        private static (double, List<(string, double)>) ComputeWmiFallback()
        {
            double total = SystemStats.GetCpuUsage();
            var cores = SystemStats.GetPerCoreUsage();
            return (total, cores);
        }
    }
}
