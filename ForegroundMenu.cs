using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using static TopBar.NativeMethods;

namespace TopBar
{
    /// <summary>
    /// Drives the foreground app's REAL menus. Preferred path: read the app's
    /// Win32 menu (HMENU) and mirror its items, firing WM_COMMAND on click —
    /// exact labels, states and shortcuts with zero guessing. Fallback for
    /// menu-less apps (Chrome, Electron, UWP): Alt+mnemonic opens the app's
    /// own native menu. Everything runs on click; no hooks or polling.
    /// </summary>
    internal static class ForegroundMenu
    {
        public sealed class NativeMenuItem
        {
            public int Id;
            public string Label = "";
            public string Hint = "";
            public bool Enabled = true;
            public bool IsSeparator;
            public IntPtr Submenu = IntPtr.Zero;
        }

        /// <summary>Validate a candidate target window (non-zero, live, not ours).</summary>
        public static IntPtr ValidateTarget(IntPtr hwnd)
        {
            try
            {
                if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return IntPtr.Zero;
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == 0 || pid == (uint)Environment.ProcessId) return IntPtr.Zero;
                return hwnd;
            }
            catch { return IntPtr.Zero; }
        }

        public static string AppNameFor(IntPtr hwnd)
        {
            try
            {
                GetWindowThreadProcessId(hwnd, out uint pid);
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

        /// <summary>Raw process (image) name — used to pick an app menu profile.</summary>
        public static string ProcessNameFor(IntPtr hwnd)
        {
            try
            {
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == 0) return "";
                using var proc = Process.GetProcessById((int)pid);
                return proc.ProcessName;
            }
            catch { return ""; }
        }

        private static string CleanLabel(string raw)
        {
            string t = raw.Replace("&", "");
            int tab = t.IndexOf('\t');
            return (tab >= 0 ? t.Substring(0, tab) : t).Trim();
        }

        private static string ShortcutHint(string raw)
        {
            int tab = raw.IndexOf('\t');
            return tab >= 0 ? raw.Substring(tab + 1).Trim() : "";
        }

        /// <summary>Restore only if minimized — never un-maximize or
        /// exit fullscreen. Unconditional SW_RESTORE was knocking Explorer
        /// out of maximized/fullscreen on every menu click.</summary>
        public static void EnsureVisible(IntPtr hwnd)
        {
            try { if (hwnd != IntPtr.Zero && IsIconic(hwnd)) ShowWindowAsync(hwnd, SW_RESTORE); }
            catch { }
        }

        /// <summary>Find the top-level submenu whose caption matches (e.g. "File").</summary>
        public static bool TryGetSubmenu(IntPtr hwnd, string caption, out IntPtr hSub)
        {
            hSub = IntPtr.Zero;
            try
            {
                IntPtr hMenu = GetMenu(hwnd);
                if (hMenu == IntPtr.Zero) return false;
                int n = GetMenuItemCount(hMenu);
                if (n <= 0) return false;
                var sb = new StringBuilder(256);
                for (int i = 0; i < n; i++)
                {
                    sb.Clear();
                    if (GetMenuString(hMenu, (uint)i, sb, sb.Capacity, MF_BYPOSITION) <= 0) continue;
                    if (!string.Equals(CleanLabel(sb.ToString()), caption, StringComparison.OrdinalIgnoreCase))
                        continue;
                    hSub = GetSubMenu(hMenu, i);
                    return hSub != IntPtr.Zero && GetMenuItemCount(hSub) > 0;
                }
            }
            catch { }
            return false;
        }

        /// <summary>Read one menu level (submenu handles kept for lazy expansion).</summary>
        public static List<NativeMenuItem> ReadItems(IntPtr hMenu)
        {
            var items = new List<NativeMenuItem>();
            try
            {
                int n = GetMenuItemCount(hMenu);
                if (n <= 0) return items;
                var sb = new StringBuilder(256);
                for (int i = 0; i < n; i++)
                {
                    uint state;
                    try { state = GetMenuState(hMenu, (uint)i, MF_BYPOSITION); }
                    catch { continue; }
                    if ((state & 0xFFFF & MF_SEPARATOR) != 0)
                    {
                        items.Add(new NativeMenuItem { IsSeparator = true });
                        continue;
                    }
                    bool isPopup = (state & 0xFFFF & MF_POPUP) != 0;
                    sb.Clear();
                    string raw = GetMenuString(hMenu, (uint)i, sb, sb.Capacity, MF_BYPOSITION) > 0
                        ? sb.ToString() : "";
                    string label = CleanLabel(raw);
                    if (string.IsNullOrEmpty(label))
                    {
                        if (!isPopup) continue; // owner-drawn leaf: can't display
                        label = "More…";
                    }
                    var item = new NativeMenuItem
                    {
                        Label = label,
                        Hint = ShortcutHint(raw),
                        Enabled = (state & 0xFFFF & MFS_DISABLED_MASK) == 0
                    };
                    if (isPopup)
                    {
                        IntPtr hSub = GetSubMenu(hMenu, i);
                        if (hSub == IntPtr.Zero || GetMenuItemCount(hSub) <= 0) continue;
                        item.Submenu = hSub;
                    }
                    else
                    {
                        item.Id = unchecked((int)GetMenuItemID(hMenu, i));
                    }
                    items.Add(item);
                }
            }
            catch { }
            return items;
        }

        /// <summary>Fire a real menu command at the app (restores/focuses it first).</summary>
        public static void Activate(IntPtr hwnd, int id)
        {
            try
            {
                if (ValidateTarget(hwnd) == IntPtr.Zero) return;
                EnsureVisible(hwnd);
                SetForegroundWindow(hwnd);
                PostMessage(hwnd, WM_COMMAND, (IntPtr)id, IntPtr.Zero);
            }
            catch { }
        }

        /// <summary>Focus the app and press Alt+letter (opens its native menu).</summary>
        public static void SendMnemonic(IntPtr hwnd, string topLabel)
        {
            try
            {
                if (ValidateTarget(hwnd) == IntPtr.Zero) return;
                byte vk = topLabel switch
                {
                    "File" => 0x46,   // F
                    "Edit" => 0x45,   // E
                    "View" => 0x56,   // V
                    "Window" => 0x57, // W
                    "Help" => 0x48,   // H
                    _ => 0
                };
                EnsureVisible(hwnd);
                SetForegroundWindow(hwnd);
                if (vk == 0) return;
                keybd_event(0x12, 0, 0, UIntPtr.Zero); // Alt down
                keybd_event(vk, 0, 0, UIntPtr.Zero);
                keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                keybd_event(0x12, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            }
            catch { }
        }

        /// <summary>Focus the app and press F1 (Help fallback).</summary>
        public static void SendHelpKey(IntPtr hwnd)
        {
            try
            {
                if (ValidateTarget(hwnd) == IntPtr.Zero) return;
                EnsureVisible(hwnd);
                SetForegroundWindow(hwnd);
                keybd_event(0x70, 0, 0, UIntPtr.Zero);
                keybd_event(0x70, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            }
            catch { }
        }
    }
}
