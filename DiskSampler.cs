using System;
using System.Runtime.InteropServices;

namespace TopBar
{
    /// <summary>
    /// Disk active time + read/write throughput, sampled once per second on a
    /// background thread via PDH (\PhysicalDisk(_Total)\...) — the same source
    /// Task Manager uses. PDH is a fraction of the cost of a WMI query, so the
    /// bar and the Storage popup can both read it every tick and stay in sync.
    /// Uses the English counter API so it works on any display language.
    /// Falls back to the WMI queries if PDH is unavailable.
    /// </summary>
    internal static class DiskSampler
    {
        private const uint PDH_FMT_DOUBLE = 0x00000200;
        private const uint PDH_CSTATUS_VALID_DATA = 0x00000000;
        private const uint PDH_CSTATUS_NEW_DATA = 0x00000001;

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);

        [DllImport("pdh.dll")]
        private static extern uint PdhCollectQueryData(IntPtr query);

        [DllImport("pdh.dll")]
        private static extern uint PdhCloseQuery(IntPtr query);

        [StructLayout(LayoutKind.Explicit, Size = 16)]
        private struct PDH_FMT_COUNTERVALUE
        {
            [FieldOffset(0)] public uint CStatus;
            [FieldOffset(8)] public double DoubleValue;
        }

        [DllImport("pdh.dll")]
        private static extern uint PdhGetFormattedCounterValue(
            IntPtr counter, uint format, out uint type, out PDH_FMT_COUNTERVALUE value);

        private static readonly object Sync = new();
        private static (double Active, double ReadMBs, double WriteMBs) _latest;
        private static System.Threading.Timer? _timer;

        private static IntPtr _query, _active, _read, _write;
        private static bool _pdhOpen, _pdhFailed, _primed;

        /// <summary>Latest cached (active %, read MB/s, write MB/s). Starts the
        /// background sampler on first call.</summary>
        public static (double Active, double ReadMBs, double WriteMBs) Sample()
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
                lock (Sync)
                {
                    if (!_pdhFailed)
                    {
                        if (CollectPdh()) return;
                        _pdhFailed = true; // fall through to WMI from now on
                    }
                    _latest = (
                        SystemStats.GetDiskActiveTime(),
                        SystemStats.GetDiskIo().ReadMBs,
                        SystemStats.GetDiskIo().WriteMBs);
                }
            }
            catch { }
        }

        private static bool CollectPdh()
        {
            if (!_pdhOpen)
            {
                if (PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0) return false;
                if (PdhAddEnglishCounterW(_query, @"\PhysicalDisk(_Total)\% Disk Time", IntPtr.Zero, out _active) != 0 ||
                    PdhAddEnglishCounterW(_query, @"\PhysicalDisk(_Total)\Disk Read Bytes/sec", IntPtr.Zero, out _read) != 0 ||
                    PdhAddEnglishCounterW(_query, @"\PhysicalDisk(_Total)\Disk Write Bytes/sec", IntPtr.Zero, out _write) != 0)
                {
                    PdhCloseQuery(_query);
                    _query = IntPtr.Zero;
                    return false;
                }
                _pdhOpen = true;
            }

            if (PdhCollectQueryData(_query) != 0) return false;

            // Rate/% counters need a previous sample; the first collect primes.
            if (!_primed) { _primed = true; return true; }

            double active = Math.Clamp(Read(_active), 0, 100);
            double read = Math.Max(0, Read(_read)) / 1048576.0;
            double write = Math.Max(0, Read(_write)) / 1048576.0;
            _latest = (active, read, write);
            return true;
        }

        private static double Read(IntPtr counter)
        {
            if (PdhGetFormattedCounterValue(counter, PDH_FMT_DOUBLE, out _, out var v) != 0) return 0;
            if (v.CStatus != PDH_CSTATUS_VALID_DATA && v.CStatus != PDH_CSTATUS_NEW_DATA) return 0;
            return v.DoubleValue;
        }
    }
}
