using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using static TopBar.NativeMethods;

namespace TopBar
{
    /// <summary>
    /// Polls the foreground window title + app name every 500ms (macOS-style:
    /// bold app name on the left, window title next to it).
    /// </summary>
    internal sealed class ActiveWindowTracker : IDisposable
    {
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        private readonly DispatcherTimer _timer;
        private string _lastTitle = string.Empty;
        private string _lastApp = string.Empty;
        private IntPtr _lastHwnd = IntPtr.Zero;

        public event Action<string>? TitleChanged;
        public event Action<string>? AppChanged;

        public ActiveWindowTracker()
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
            _timer.Tick += (_, _) => Poll();
        }

        public void Start() => _timer.Start();
        public void Stop() => _timer.Stop();

        /// <summary>
        /// Last foreground window outside our own process. Clicking the bar
        /// activates our window, so menu handlers must target this handle —
        /// never GetForegroundWindow() at click time.
        /// </summary>
        public IntPtr LastHwnd => _lastHwnd;

        public void Dispose()
        {
            _timer.Stop();
        }

        private void Poll()
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return;

            // Ignore our own bar + popups: clicking them focuses our process,
            // but the menu bar must keep showing the app underneath (macOS does
            // not show "TopBar" as the active app when a menu is open).
            GetWindowThreadProcessId(hwnd, out uint fgPid);
            if (fgPid == (uint)Environment.ProcessId) return;

            int len = GetWindowTextLength(hwnd);
            string title = string.Empty;
            if (len > 0)
            {
                var sb = new StringBuilder(len + 1);
                GetWindowText(hwnd, sb, sb.Capacity);
                title = sb.ToString();
            }
            if (title != _lastTitle)
            {
                _lastTitle = title;
                TitleChanged?.Invoke(title);
            }

            // Active app name (macOS-style bold app menu) — resolve only
            // when the foreground window actually changed (Process +
            // MainModule access is the most expensive call in this poll).
            if (hwnd != _lastHwnd)
            {
                _lastHwnd = hwnd;
                string app = GetActiveAppName(hwnd);
                if (app != _lastApp)
                {
                    _lastApp = app;
                    AppChanged?.Invoke(app);
                }
            }
        }

        private static string GetActiveAppName(IntPtr hwnd)
        {
            try
            {
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == 0) return string.Empty;
                using var proc = System.Diagnostics.Process.GetProcessById((int)pid);
                // Prefer FileDescription ("Google Chrome") over process name ("chrome")
                try
                {
                    var desc = proc.MainModule?.FileVersionInfo.FileDescription;
                    if (!string.IsNullOrWhiteSpace(desc))
                        return desc.Trim();
                }
                catch { }
                return proc.ProcessName;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
