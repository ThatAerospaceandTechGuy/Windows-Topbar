using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace TopBar
{
    /// <summary>
    /// Lightweight system stats. Hot-path methods (CPU delta, memory) use raw
    /// Win32 calls so the bar can poll them every few seconds at ~0% CPU.
    /// Heavier WMI queries run only when a monitor popup is open.
    /// </summary>
    internal static class SystemStats
    {
        // ── CPU (GetSystemTimes delta) ────────────────────────────────────

        [DllImport("kernel32.dll")]
        private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

        [StructLayout(LayoutKind.Sequential)]
        private struct FILETIME { public uint Low; public uint High; }

        private static ulong ToUlong(FILETIME ft) => ((ulong)ft.High << 32) | ft.Low;

        private static ulong _prevIdle, _prevTotal;
        private static bool _cpuPrimed;

        /// <summary>Total CPU % since last call. First call primes and returns 0.</summary>
        public static double GetCpuUsage()
        {
            try
            {
                if (!GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
                ulong idleT = ToUlong(idle);
                ulong total = ToUlong(kernel) + ToUlong(user);
                if (!_cpuPrimed)
                {
                    _prevIdle = idleT; _prevTotal = total; _cpuPrimed = true;
                    return 0;
                }
                ulong idleDelta = idleT - _prevIdle, totalDelta = total - _prevTotal;
                _prevIdle = idleT; _prevTotal = total;
                if (totalDelta == 0) return 0;
                return Math.Clamp(100.0 * (totalDelta - idleDelta) / totalDelta, 0, 100);
            }
            catch { return 0; }
        }

        // ── Memory (GlobalMemoryStatusEx) ─────────────────────────────────

        [DllImport("kernel32.dll")]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhys;
            public ulong AvailPhys;
            public ulong TotalPageFile;
            public ulong AvailPageFile;
            public ulong TotalVirtual;
            public ulong AvailVirtual;
            public ulong AvailExtendedVirtual;
        }

        public static (ulong Total, ulong Avail, uint LoadPct) GetMemory()
        {
            try
            {
                var st = new MEMORYSTATUSEX { Length = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
                if (!GlobalMemoryStatusEx(ref st)) return (0, 0, 0);
                return (st.TotalPhys, st.AvailPhys, st.MemoryLoad);
            }
            catch { return (0, 0, 0); }
        }

        // ── Disk ──────────────────────────────────────────────────────────

        /// <summary>Live disk busyness (Task Manager "Active time"). WMI perf
        /// query — call at most every few seconds, never on a fast tick.</summary>
        public static double GetDiskActiveTime()
        {
            try
            {
                using var s = new System.Management.ManagementObjectSearcher(
                    "SELECT Name, PercentDiskTime FROM Win32_PerfFormattedData_PerfDisk_PhysicalDisk");
                foreach (System.Management.ManagementObject o in s.Get())
                {
                    if ((o["Name"]?.ToString() ?? "") != "_Total") continue;
                    if (double.TryParse(o["PercentDiskTime"]?.ToString(), out double v))
                        return Math.Clamp(v, 0, 100);
                }
            }
            catch { }
            return 0;
        }

        public static (string Name, double UsedPct, string Free, string Total) GetSystemDisk()
        {
            try
            {
                var sys = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
                foreach (var d in DriveInfo.GetDrives())
                {
                    if (!d.IsReady) continue;
                    if (string.Equals(d.Name, sys, StringComparison.OrdinalIgnoreCase))
                        return (d.Name, 100.0 * (d.TotalSize - d.AvailableFreeSpace) / d.TotalSize,
                            FormatBytes(d.AvailableFreeSpace), FormatBytes(d.TotalSize));
                }
                var first = DriveInfo.GetDrives().FirstOrDefault(d => d.IsReady);
                if (first != null)
                    return (first.Name, 100.0 * (first.TotalSize - first.AvailableFreeSpace) / first.TotalSize,
                        FormatBytes(first.AvailableFreeSpace), FormatBytes(first.TotalSize));
            }
            catch { }
            return ("—", 0, "—", "—");
        }

        // ── WMI info (popup-only, heavier) ────────────────────────────────

        private static Dictionary<string, string>? _cpuInfoCache;

        /// <summary>Pre-fetch the (static) CPU info so the first popup paint is
        /// instant. Safe to call repeatedly; runs off the UI thread.</summary>
        public static void WarmCpuInfo() { try { _ = GetCpuInfo(); } catch { } }

        public static Dictionary<string, string> GetCpuInfo()
        {
            if (_cpuInfoCache != null) return _cpuInfoCache;
            var r = new Dictionary<string, string>();
            try
            {
                using var s = new System.Management.ManagementObjectSearcher(
                    "SELECT Name, MaxClockSpeed, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor");
                foreach (System.Management.ManagementObject o in s.Get())
                {
                    r["Processor"] = (o["Name"]?.ToString() ?? "—").Trim();
                    r["Cores"] = $"{o["NumberOfCores"]} cores / {o["NumberOfLogicalProcessors"]} threads";
                    if (uint.TryParse(o["MaxClockSpeed"]?.ToString(), out uint mhz))
                        r["Max speed"] = $"{mhz / 1000.0:F2} GHz";
                    break;
                }
            }
            catch { }
            if (r.Count > 0) _cpuInfoCache = r;
            return r;
        }

        public static List<(string Core, double Pct)> GetPerCoreUsage()
        {
            var r = new List<(string, double)>();
            try
            {
                using var s = new System.Management.ManagementObjectSearcher(
                    "SELECT Name, PercentProcessorTime FROM Win32_PerfFormattedData_PerfOS_Processor");
                foreach (System.Management.ManagementObject o in s.Get())
                {
                    var name = o["Name"]?.ToString() ?? "";
                    if (name == "_Total") continue;
                    if (double.TryParse(o["PercentProcessorTime"]?.ToString(), out double pct))
                        r.Add((name, pct));
                }
            }
            catch { }
            return r;
        }

        public static double? GetGpuUsage()
        {
            // Sum 3D-engine utilization across GPU engine instances (Win10+).
            try
            {
                using var s = new System.Management.ManagementObjectSearcher(
                    "SELECT Name, UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine");
                double sum = 0;
                foreach (System.Management.ManagementObject o in s.Get())
                {
                    var name = o["Name"]?.ToString() ?? "";
                    if (!name.Contains("engtype_3D")) continue;
                    if (ulong.TryParse(o["UtilizationPercentage"]?.ToString(), out ulong v))
                        sum += v;
                }
                return sum;
            }
            catch { return null; }
        }

        public static Dictionary<string, string> GetGpuInfo()
        {
            var r = new Dictionary<string, string>();
            try
            {
                using var s = new System.Management.ManagementObjectSearcher(
                    "SELECT Name, AdapterRAM, DriverVersion FROM Win32_VideoController");
                foreach (System.Management.ManagementObject o in s.Get())
                {
                    r["GPU"] = (o["Name"]?.ToString() ?? "—").Trim();
                    if (ulong.TryParse(o["AdapterRAM"]?.ToString(), out ulong ram) && ram > 0)
                        r["VRAM"] = FormatBytes(ram);
                    var drv = o["DriverVersion"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(drv)) r["Driver"] = drv;
                    break;
                }
            }
            catch { }
            return r;
        }

        public static (double ReadMBs, double WriteMBs) GetDiskIo()
        {
            try
            {
                using var s = new System.Management.ManagementObjectSearcher(
                    "SELECT Name, DiskReadBytesPersec, DiskWriteBytesPersec FROM Win32_PerfFormattedData_PerfDisk_PhysicalDisk");
                foreach (System.Management.ManagementObject o in s.Get())
                {
                    if ((o["Name"]?.ToString() ?? "") != "_Total") continue;
                    double r = double.TryParse(o["DiskReadBytesPersec"]?.ToString(), out double rv) ? rv : 0;
                    double w = double.TryParse(o["DiskWriteBytesPersec"]?.ToString(), out double wv) ? wv : 0;
                    return (r / 1048576.0, w / 1048576.0);
                }
            }
            catch { }
            return (0, 0);
        }

        public static Dictionary<string, string> GetBatteryInfo()
        {
            var r = new Dictionary<string, string>();
            try
            {
                var ps = System.Windows.Forms.SystemInformation.PowerStatus;
                r["Power source"] = ps.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online ? "AC adapter" : "Battery";
                if (ps.BatteryLifePercent >= 0)
                    r["Charge"] = $"{(int)(ps.BatteryLifePercent * 100)}%";
                if (ps.BatteryLifeRemaining > 0)
                    r["Time remaining"] = $"{ps.BatteryLifeRemaining / 3600}h {(ps.BatteryLifeRemaining % 3600) / 60}m";
            }
            catch { }
            try
            {
                using var s = new System.Management.ManagementObjectSearcher(
                    "SELECT DesignCapacity, FullChargeCapacity, EstimatedChargeRemaining FROM Win32_Battery");
                foreach (System.Management.ManagementObject o in s.Get())
                {
                    if (uint.TryParse(o["DesignCapacity"]?.ToString(), out uint design) && design > 0 &&
                        uint.TryParse(o["FullChargeCapacity"]?.ToString(), out uint full) && full > 0)
                        r["Health"] = $"{100.0 * full / design:F0}% of design capacity";
                    break;
                }
            }
            catch { }
            return r;
        }

        // ── Processes ─────────────────────────────────────────────────────

        public static List<(string Name, string Detail)> TopMemoryProcesses(int n = 6)
        {
            try
            {
                return Process.GetProcesses()
                    .OrderByDescending(p => { try { return p.WorkingSet64; } catch { return 0; } })
                    .Take(n)
                    .Select(p =>
                    {
                        string mem;
                        try { mem = FormatBytes((ulong)p.WorkingSet64); } catch { mem = "—"; }
                        return (SafeName(p), mem);
                    }).ToList();
            }
            catch { return new List<(string, string)>(); }
        }

        /// <summary>One process enumeration yielding both the system thread
        /// count and the top memory consumers (avoids scanning twice).</summary>
        public static (int Threads, List<(string Name, string Detail)> Top) GetProcessSnapshot(int topN = 6)
        {
            try
            {
                var procs = Process.GetProcesses();
                int threads = 0;
                var list = new List<(string Name, long Mem)>(procs.Length);
                foreach (var p in procs)
                {
                    try { threads += p.Threads.Count; } catch { }
                    long mem = 0;
                    try { mem = p.WorkingSet64; } catch { }
                    list.Add((SafeName(p), mem));
                }
                var top = list
                    .OrderByDescending(t => t.Mem)
                    .Take(topN)
                    .Select(t => (t.Name, FormatBytes((ulong)Math.Max(0, t.Mem))))
                    .ToList();
                return (threads, top);
            }
            catch { return (0, new List<(string, string)>()); }
        }

        private static string SafeName(Process p)
        {
            try { return string.IsNullOrWhiteSpace(p.ProcessName) ? "?" : p.ProcessName; }
            catch { return "?"; }
        }

        // ── Misc ──────────────────────────────────────────────────────────

        public static string GetUptime()
        {
            try
            {
                var ts = TimeSpan.FromMilliseconds(Environment.TickCount64);
                return ts.Days > 0 ? $"{ts.Days}d {ts.Hours}h {ts.Minutes}m" : $"{ts.Hours}h {ts.Minutes}m";
            }
            catch { return "—"; }
        }

        public static int GetProcessThreadCount() => GetProcessSnapshot(0).Threads;

        public static string FormatBytes(double bytes) => FormatBytes((ulong)Math.Max(0, bytes));

        public static string FormatBytes(ulong bytes)
        {
            string[] u = { "B", "KB", "MB", "GB", "TB" };
            double v = bytes;
            int i = 0;
            while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            return $"{v:F1} {u[i]}";
        }
    }
}
