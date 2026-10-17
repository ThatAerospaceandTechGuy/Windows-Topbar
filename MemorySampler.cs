using System;

namespace TopBar
{
    /// <summary>
    /// Memory load sampled once per second on a background thread and cached,
    /// so the bar and the Memory popup read the identical value every tick
    /// (same pattern as <see cref="CpuSampler"/> and <see cref="DiskSampler"/>).
    /// </summary>
    internal static class MemorySampler
    {
        private static readonly object Sync = new();
        private static (ulong Total, ulong Avail, uint LoadPct) _latest;
        private static System.Threading.Timer? _timer;

        /// <summary>Latest cached (total, available, load %). Starts the
        /// background sampler on first call.</summary>
        public static (ulong Total, ulong Avail, uint LoadPct) Sample()
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
                _latest = SystemStats.GetMemory();
                _timer = new System.Threading.Timer(_ => Update(), null, 0, 1000);
            }
        }

        private static void Update()
        {
            try
            {
                var v = SystemStats.GetMemory();
                lock (Sync) _latest = v;
            }
            catch { }
        }
    }
}
