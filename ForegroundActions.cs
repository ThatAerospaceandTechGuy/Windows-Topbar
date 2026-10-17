using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using static TopBar.NativeMethods;

namespace TopBar
{
    /// <summary>
    /// Actions on the foreground window + system power actions.
    /// Backs the Apple menu and the File/Edit/View/Window/Help menus.
    /// </summary>
    internal static class ForegroundActions
    {
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        private const int SW_MINIMIZE = 6;
        private const uint WM_CLOSE = 0x10;
        private const byte VK_CONTROL = 0x11;
        private const byte VK_MENU = 0x12;
        private const byte VK_SHIFT = 0x10;
        private const byte VK_F1 = 0x70;
        private const byte VK_F4 = 0x73;
        private const byte VK_F11 = 0x7A;

        public static IntPtr ForegroundHwnd()
        {
            try { return GetForegroundWindow(); }
            catch { return IntPtr.Zero; }
        }

        public static string ForegroundAppName()
        {
            try
            {
                GetWindowThreadProcessId(ForegroundHwnd(), out uint pid);
                if (pid == 0) return "";
                using var proc = Process.GetProcessById((int)pid);
                try
                {
                    var desc = proc.MainModule?.FileVersionInfo.FileDescription;
                    if (!string.IsNullOrWhiteSpace(desc)) return desc.Trim();
                }
                catch { }
                return proc.ProcessName;
            }
            catch { return ""; }
        }

        private static void Key(byte vk, bool ctrl = false, bool alt = false, bool shift = false)
        {
            try
            {
                var hwnd = ForegroundHwnd();
                if (hwnd != IntPtr.Zero) SetForegroundWindow(hwnd);
                if (ctrl) keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
                if (alt) keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
                if (shift) keybd_event(VK_SHIFT, 0, 0, UIntPtr.Zero);
                keybd_event(vk, 0, 0, UIntPtr.Zero);
                keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                if (shift) keybd_event(VK_SHIFT, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                if (alt) keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                if (ctrl) keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            }
            catch { }
        }

        private static void Ctrl(char c) => Key((byte)char.ToUpperInvariant(c), ctrl: true);

        // ── App menu ──────────────────────────────────────────────────
        public static void HideApp()
        {
            try
            {
                var hwnd = ForegroundHwnd();
                if (hwnd != IntPtr.Zero) ShowWindowAsync(hwnd, SW_MINIMIZE);
            }
            catch { }
        }

        /// <summary>Minimize a known window (clicking our bar steals focus,
        /// so menu handlers must pass the pre-captured handle).</summary>
        public static void HideWindow(IntPtr hwnd)
        {
            try { if (hwnd != IntPtr.Zero) ShowWindowAsync(hwnd, SW_MINIMIZE); }
            catch { }
        }

        public static void QuitApp()
        {
            try
            {
                var hwnd = ForegroundHwnd();
                if (hwnd != IntPtr.Zero) PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }
            catch { }
        }

        /// <summary>Close a known window (see HideWindow).</summary>
        public static void QuitWindow(IntPtr hwnd)
        {
            try { if (hwnd != IntPtr.Zero) PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero); }
            catch { }
        }

        // ── File ──────────────────────────────────────────────────────
        public static void FileNew() => Ctrl('N');
        public static void FileOpen() => Ctrl('O');
        public static void FileSave() => Ctrl('S');
        public static void FilePrint() => Ctrl('P');

        // ── Edit ──────────────────────────────────────────────────────
        public static void Undo() => Ctrl('Z');
        public static void Redo() => Ctrl('Y');
        public static void Cut() => Ctrl('X');
        public static void Copy() => Ctrl('C');
        public static void Paste() => Ctrl('V');
        public static void SelectAll() => Ctrl('A');
        public static void Find() => Ctrl('F');

        // ── View ──────────────────────────────────────────────────────
        public static void FullScreen() => Key(VK_F11);
        public static void ZoomIn() => Key(0xBB, ctrl: true);   // Ctrl + =
        public static void ZoomOut() => Key(0xBD, ctrl: true);  // Ctrl + -

        // ── Window ────────────────────────────────────────────────────
        public static void Minimize() => HideApp();
        public static void CloseWindow() => Key(VK_F4, alt: true);
        public static void SwitchWindow() => Key(0x09, alt: true); // Alt+Tab

        // ── Help ──────────────────────────────────────────────────────
        public static void HelpKey() => Key(VK_F1);

        // ── System ────────────────────────────────────────────────────
        public static void OpenSettings(string page = "")
        {
            try { Process.Start(new ProcessStartInfo("ms-settings:" + page) { UseShellExecute = true }); }
            catch { }
        }

        public static void OpenStore()
        {
            try { Process.Start(new ProcessStartInfo("ms-windows-store:") { UseShellExecute = true }); }
            catch { }
        }

        public static void ForceQuit()
        {
            try { Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true }); }
            catch { }
        }

        public static void Sleep()
        {
            try { Process.Start(new ProcessStartInfo("rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0") { UseShellExecute = true }); }
            catch { }
        }

        public static void Restart()
        {
            try { Process.Start(new ProcessStartInfo("shutdown", "/r /t 0") { UseShellExecute = true }); }
            catch { }
        }

        public static void ShutDown()
        {
            try { Process.Start(new ProcessStartInfo("shutdown", "/s /t 0") { UseShellExecute = true }); }
            catch { }
        }

        public static void Lock()
        {
            try { Process.Start(new ProcessStartInfo("rundll32.exe", "user32.dll,LockWorkStation") { UseShellExecute = true }); }
            catch { }
        }

        public static void LogOut()
        {
            try { Process.Start(new ProcessStartInfo("shutdown", "/l") { UseShellExecute = true }); }
            catch { }
        }

        public static List<(string Name, string Path)> RecentItems(int n = 8)
        {
            var r = new List<(string, string)>();
            try
            {
                var recent = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
                if (!Directory.Exists(recent)) return r;
                r = new DirectoryInfo(recent).GetFiles("*.lnk")
                    .OrderByDescending(f => f.LastWriteTime)
                    .Take(n)
                    .Select(f => (Path.GetFileNameWithoutExtension(f.Name), f.FullName))
                    .ToList();
            }
            catch { }
            return r;
        }

        public static void OpenPath(string path)
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch { }
        }
    }
}
