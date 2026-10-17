using System;
using System.Threading;
using static TopBar.NativeMethods;

namespace TopBar
{
    /// <summary>
    /// Sends keyboard chords to a specific window (focusing it first). Chords
    /// are readable strings like "Ctrl+Shift+N", "F11", "Ctrl+K Ctrl+O" — a
    /// space separates quick sequences. Used by the per-app File/Edit/View
    /// menus since menu-less apps (Chromium, Electron, VS Code, terminals)
    /// expose no Win32 menu to mirror.
    /// </summary>
    internal static class KeySend
    {
        private const byte VK_CONTROL = 0x11;
        private const byte VK_MENU = 0x12;
        private const byte VK_SHIFT = 0x10;
        private const byte VK_LWIN = 0x5B;

        public static void To(IntPtr hwnd, string chord)
        {
            try
            {
                if (hwnd != IntPtr.Zero && ForegroundMenu.ValidateTarget(hwnd) != IntPtr.Zero)
                {
                    if (IsIconic(hwnd)) ShowWindowAsync(hwnd, SW_RESTORE);
                    SetForegroundWindow(hwnd);
                    Thread.Sleep(40); // let activation settle before typing
                }
                var seq = chord.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                for (int i = 0; i < seq.Length; i++)
                {
                    if (i > 0) Thread.Sleep(80);
                    SendOne(seq[i]);
                }
            }
            catch { }
        }

        private static void SendOne(string chord)
        {
            bool ctrl = false, alt = false, shift = false, win = false;
            byte vk = 0;
            foreach (var raw in chord.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                switch (raw.ToLowerInvariant())
                {
                    case "ctrl": case "control": ctrl = true; break;
                    case "alt": alt = true; break;
                    case "shift": shift = true; break;
                    case "win": case "meta": case "super": win = true; break;
                    default: vk = ToVk(raw); break;
                }
            }
            if (vk == 0) return;

            if (ctrl) keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
            if (alt) keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
            if (shift) keybd_event(VK_SHIFT, 0, 0, UIntPtr.Zero);
            if (win) keybd_event(VK_LWIN, 0, 0, UIntPtr.Zero);
            keybd_event(vk, 0, 0, UIntPtr.Zero);
            keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            if (win) keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            if (shift) keybd_event(VK_SHIFT, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            if (alt) keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            if (ctrl) keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        private static byte ToVk(string key)
        {
            if (key.Length == 1)
            {
                char c = char.ToUpperInvariant(key[0]);
                if (c >= 'A' && c <= 'Z') return (byte)c;
                if (c >= '0' && c <= '9') return (byte)c;
                switch (c)
                {
                    case '=': case '+': return 0xBB; // VK_OEM_PLUS
                    case '-': case '_': return 0xBD; // VK_OEM_MINUS
                    case ',': case '<': return 0xBC;
                    case '.': case '>': return 0xBE;
                    case '`': case '~': return 0xC0;
                    case '/': case '?': return 0xBF;
                    case ';': case ':': return 0xBA;
                    case '\'': case '"': return 0xDE;
                    case '[': case '{': return 0xDB;
                    case ']': case '}': return 0xDD;
                    case '\\': case '|': return 0xDC;
                }
            }
            string k = key.ToLowerInvariant();
            if (k.Length >= 2 && k[0] == 'f' && int.TryParse(k.Substring(1), out int fn) && fn >= 1 && fn <= 24)
                return (byte)(0x70 + fn - 1);
            return k switch
            {
                "tab" => 0x09,
                "enter" or "return" => 0x0D,
                "esc" or "escape" => 0x1B,
                "space" => 0x20,
                "del" or "delete" => 0x2E,
                "backspace" => 0x08,
                "insert" => 0x2D,
                "home" => 0x24,
                "end" => 0x23,
                "pageup" => 0x21,
                "pagedown" => 0x22,
                "left" => 0x25,
                "up" => 0x26,
                "right" => 0x27,
                "down" => 0x28,
                "plus" => 0xBB,
                "minus" => 0xBD,
                "comma" => 0xBC,
                "period" => 0xBE,
                "backtick" or "grave" => 0xC0,
                "slash" => 0xBF,
                _ => 0
            };
        }
    }
}
