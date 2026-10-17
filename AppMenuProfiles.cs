using System;
using System.Collections.Generic;

namespace TopBar
{
    /// <summary>
    /// Curated per-app File / Edit / View menus for apps that expose no Win32
    /// menu to mirror (Chromium browsers, Firefox, Electron apps, VS Code,
    /// Windows Terminal, Notepad). Every row fires that app's real shortcut, so
    /// the menu is correct and consistent — the macOS idea of an app-specific
    /// menu bar. Unknown apps fall through to the existing generic fallbacks.
    /// </summary>
    internal static class AppMenuProfiles
    {
        private enum Kind { None, Chromium, Firefox, VSCode, Terminal, Notepad, Electron }

        private static BarMenuPopup.MenuEntry E(string label, string hint, IntPtr hwnd, string chord) =>
            new() { Label = label, Hint = hint, Action = () => KeySend.To(hwnd, chord) };

        private static BarMenuPopup.MenuEntry Sep() => new() { IsSeparator = true };

        /// <summary>True when we produced a tailored menu for this app + tag.</summary>
        public static bool TryBuild(IntPtr hwnd, string? processName, string tag,
            out List<BarMenuPopup.MenuEntry> entries)
        {
            entries = new List<BarMenuPopup.MenuEntry>();
            var kind = KindOf(processName);
            if (kind == Kind.None) return false;

            var list = tag switch
            {
                "File" => File(kind, hwnd),
                "Edit" => Edit(kind, hwnd),
                "View" => View(kind, hwnd),
                _ => new List<BarMenuPopup.MenuEntry>()
            };
            if (list.Count == 0) return false;
            entries = list;
            return true;
        }

        private static Kind KindOf(string? processName)
        {
            var p = (processName ?? "").Trim().ToLowerInvariant();
            return p switch
            {
                "chrome" or "msedge" or "brave" or "vivaldi" or "opera" or
                "chromium" or "thorium" or "iridium" or "epicbrowser" => Kind.Chromium,

                "firefox" or "waterfox" or "librewolf" or "floorp" or
                "firefox developer edition" => Kind.Firefox,

                "code" or "code - insiders" or "cursor" or "vscodium" or "windsurf" => Kind.VSCode,

                "windowsterminal" or "wt" or "openconsole" => Kind.Terminal,

                "notepad" => Kind.Notepad,

                "slack" or "discord" or "whatsapp" or "notion" or "obsidian" or
                "spotify" or "postman" or "signal" or "figma" or "teams" or
                "ms-teams" or "gitkraken" or "github desktop" or "todoist" or
                "linear" or "clickup" or "mailspring" or "1password" => Kind.Electron,

                _ => Kind.None
            };
        }

        // ── File ────────────────────────────────────────────────────────

        private static List<BarMenuPopup.MenuEntry> File(Kind kind, IntPtr h)
        {
            var m = new List<BarMenuPopup.MenuEntry>();
            switch (kind)
            {
                case Kind.Chromium:
                    m.Add(E("New Tab", "Ctrl+T", h, "Ctrl+T"));
                    m.Add(E("New Window", "Ctrl+N", h, "Ctrl+N"));
                    m.Add(E("New Private Window", "Ctrl+Shift+N", h, "Ctrl+Shift+N"));
                    m.Add(Sep());
                    m.Add(E("Open File…", "Ctrl+O", h, "Ctrl+O"));
                    m.Add(E("Save Page As…", "Ctrl+S", h, "Ctrl+S"));
                    m.Add(E("Print…", "Ctrl+P", h, "Ctrl+P"));
                    m.Add(Sep());
                    m.Add(E("Close Tab", "Ctrl+W", h, "Ctrl+W"));
                    m.Add(E("Close Window", "Ctrl+Shift+W", h, "Ctrl+Shift+W"));
                    break;

                case Kind.Firefox:
                    m.Add(E("New Tab", "Ctrl+T", h, "Ctrl+T"));
                    m.Add(E("New Window", "Ctrl+N", h, "Ctrl+N"));
                    m.Add(E("New Private Window", "Ctrl+Shift+P", h, "Ctrl+Shift+P"));
                    m.Add(Sep());
                    m.Add(E("Open File…", "Ctrl+O", h, "Ctrl+O"));
                    m.Add(E("Save Page As…", "Ctrl+S", h, "Ctrl+S"));
                    m.Add(E("Print…", "Ctrl+P", h, "Ctrl+P"));
                    m.Add(Sep());
                    m.Add(E("Close Tab", "Ctrl+W", h, "Ctrl+W"));
                    m.Add(E("Close Window", "Ctrl+Shift+W", h, "Ctrl+Shift+W"));
                    break;

                case Kind.VSCode:
                    m.Add(E("New Text File", "Ctrl+N", h, "Ctrl+N"));
                    m.Add(E("New Window", "Ctrl+Shift+N", h, "Ctrl+Shift+N"));
                    m.Add(Sep());
                    m.Add(E("Open File…", "Ctrl+O", h, "Ctrl+O"));
                    m.Add(E("Open Folder…", "Ctrl+K Ctrl+O", h, "Ctrl+K Ctrl+O"));
                    m.Add(Sep());
                    m.Add(E("Save", "Ctrl+S", h, "Ctrl+S"));
                    m.Add(E("Save As…", "Ctrl+Shift+S", h, "Ctrl+Shift+S"));
                    m.Add(E("Save All", "Ctrl+K S", h, "Ctrl+K S"));
                    m.Add(Sep());
                    m.Add(E("Close Editor", "Ctrl+W", h, "Ctrl+W"));
                    break;

                case Kind.Terminal:
                    m.Add(E("New Tab", "Ctrl+Shift+T", h, "Ctrl+Shift+T"));
                    m.Add(E("New Window", "Ctrl+Shift+N", h, "Ctrl+Shift+N"));
                    m.Add(Sep());
                    m.Add(E("Close Pane", "Ctrl+Shift+W", h, "Ctrl+Shift+W"));
                    m.Add(E("Settings", "Ctrl+,", h, "Ctrl+,"));
                    break;

                case Kind.Notepad:
                    m.Add(E("New Tab", "Ctrl+N", h, "Ctrl+N"));
                    m.Add(E("New Window", "Ctrl+Shift+N", h, "Ctrl+Shift+N"));
                    m.Add(Sep());
                    m.Add(E("Open…", "Ctrl+O", h, "Ctrl+O"));
                    m.Add(E("Save", "Ctrl+S", h, "Ctrl+S"));
                    m.Add(E("Save As…", "Ctrl+Shift+S", h, "Ctrl+Shift+S"));
                    m.Add(Sep());
                    m.Add(E("Print…", "Ctrl+P", h, "Ctrl+P"));
                    break;

                case Kind.Electron:
                    m.Add(E("New Window", "Ctrl+N", h, "Ctrl+N"));
                    m.Add(Sep());
                    m.Add(E("Close Window", "Ctrl+W", h, "Ctrl+W"));
                    break;
            }
            return m;
        }

        // ── Edit ────────────────────────────────────────────────────────

        private static List<BarMenuPopup.MenuEntry> Edit(Kind kind, IntPtr h)
        {
            var m = new List<BarMenuPopup.MenuEntry>();
            switch (kind)
            {
                case Kind.Chromium:
                case Kind.Electron:
                    m.Add(E("Undo", "Ctrl+Z", h, "Ctrl+Z"));
                    m.Add(E("Redo", "Ctrl+Shift+Z", h, "Ctrl+Shift+Z"));
                    m.Add(Sep());
                    m.Add(E("Cut", "Ctrl+X", h, "Ctrl+X"));
                    m.Add(E("Copy", "Ctrl+C", h, "Ctrl+C"));
                    m.Add(E("Paste", "Ctrl+V", h, "Ctrl+V"));
                    m.Add(Sep());
                    m.Add(E("Select All", "Ctrl+A", h, "Ctrl+A"));
                    m.Add(E("Find…", "Ctrl+F", h, "Ctrl+F"));
                    break;

                case Kind.Firefox:
                    m.Add(E("Undo", "Ctrl+Z", h, "Ctrl+Z"));
                    m.Add(E("Redo", "Ctrl+Shift+Z", h, "Ctrl+Shift+Z"));
                    m.Add(Sep());
                    m.Add(E("Cut", "Ctrl+X", h, "Ctrl+X"));
                    m.Add(E("Copy", "Ctrl+C", h, "Ctrl+C"));
                    m.Add(E("Paste", "Ctrl+V", h, "Ctrl+V"));
                    m.Add(Sep());
                    m.Add(E("Select All", "Ctrl+A", h, "Ctrl+A"));
                    m.Add(E("Find…", "Ctrl+F", h, "Ctrl+F"));
                    m.Add(E("Find Again", "Ctrl+G", h, "Ctrl+G"));
                    break;

                case Kind.VSCode:
                    m.Add(E("Undo", "Ctrl+Z", h, "Ctrl+Z"));
                    m.Add(E("Redo", "Ctrl+Y", h, "Ctrl+Y"));
                    m.Add(Sep());
                    m.Add(E("Cut", "Ctrl+X", h, "Ctrl+X"));
                    m.Add(E("Copy", "Ctrl+C", h, "Ctrl+C"));
                    m.Add(E("Paste", "Ctrl+V", h, "Ctrl+V"));
                    m.Add(Sep());
                    m.Add(E("Find", "Ctrl+F", h, "Ctrl+F"));
                    m.Add(E("Replace", "Ctrl+H", h, "Ctrl+H"));
                    m.Add(E("Find in Files", "Ctrl+Shift+F", h, "Ctrl+Shift+F"));
                    m.Add(Sep());
                    m.Add(E("Toggle Line Comment", "Ctrl+/", h, "Ctrl+/"));
                    m.Add(E("Select All", "Ctrl+A", h, "Ctrl+A"));
                    break;

                case Kind.Terminal:
                    m.Add(E("Copy", "Ctrl+Shift+C", h, "Ctrl+Shift+C"));
                    m.Add(E("Paste", "Ctrl+Shift+V", h, "Ctrl+Shift+V"));
                    m.Add(Sep());
                    m.Add(E("Find", "Ctrl+Shift+F", h, "Ctrl+Shift+F"));
                    m.Add(E("Select All", "Ctrl+Shift+A", h, "Ctrl+Shift+A"));
                    break;

                case Kind.Notepad:
                    m.Add(E("Undo", "Ctrl+Z", h, "Ctrl+Z"));
                    m.Add(E("Redo", "Ctrl+Y", h, "Ctrl+Y"));
                    m.Add(Sep());
                    m.Add(E("Cut", "Ctrl+X", h, "Ctrl+X"));
                    m.Add(E("Copy", "Ctrl+C", h, "Ctrl+C"));
                    m.Add(E("Paste", "Ctrl+V", h, "Ctrl+V"));
                    m.Add(E("Delete", "Del", h, "Del"));
                    m.Add(Sep());
                    m.Add(E("Find…", "Ctrl+F", h, "Ctrl+F"));
                    m.Add(E("Find Next", "F3", h, "F3"));
                    m.Add(E("Replace…", "Ctrl+H", h, "Ctrl+H"));
                    m.Add(E("Go To…", "Ctrl+G", h, "Ctrl+G"));
                    m.Add(Sep());
                    m.Add(E("Select All", "Ctrl+A", h, "Ctrl+A"));
                    m.Add(E("Time/Date", "F5", h, "F5"));
                    break;
            }
            return m;
        }

        // ── View ────────────────────────────────────────────────────────

        private static List<BarMenuPopup.MenuEntry> View(Kind kind, IntPtr h)
        {
            var m = new List<BarMenuPopup.MenuEntry>();
            switch (kind)
            {
                case Kind.Chromium:
                    m.Add(E("Reload", "Ctrl+R", h, "Ctrl+R"));
                    m.Add(E("Hard Reload", "Ctrl+Shift+R", h, "Ctrl+Shift+R"));
                    m.Add(E("Reopen Closed Tab", "Ctrl+Shift+T", h, "Ctrl+Shift+T"));
                    m.Add(Sep());
                    m.Add(E("Zoom In", "Ctrl+=", h, "Ctrl+="));
                    m.Add(E("Zoom Out", "Ctrl+-", h, "Ctrl+-"));
                    m.Add(E("Reset Zoom", "Ctrl+0", h, "Ctrl+0"));
                    m.Add(Sep());
                    m.Add(E("Full Screen", "F11", h, "F11"));
                    m.Add(E("Developer Tools", "Ctrl+Shift+I", h, "Ctrl+Shift+I"));
                    m.Add(E("View Source", "Ctrl+U", h, "Ctrl+U"));
                    break;

                case Kind.Firefox:
                    m.Add(E("Reload", "Ctrl+R", h, "Ctrl+R"));
                    m.Add(Sep());
                    m.Add(E("Zoom In", "Ctrl+=", h, "Ctrl+="));
                    m.Add(E("Zoom Out", "Ctrl+-", h, "Ctrl+-"));
                    m.Add(E("Reset Zoom", "Ctrl+0", h, "Ctrl+0"));
                    m.Add(Sep());
                    m.Add(E("Full Screen", "F11", h, "F11"));
                    m.Add(E("Page Source", "Ctrl+U", h, "Ctrl+U"));
                    m.Add(E("Reader View", "Ctrl+Alt+R", h, "Ctrl+Alt+R"));
                    break;

                case Kind.VSCode:
                    m.Add(E("Command Palette…", "Ctrl+Shift+P", h, "Ctrl+Shift+P"));
                    m.Add(Sep());
                    m.Add(E("Explorer", "Ctrl+Shift+E", h, "Ctrl+Shift+E"));
                    m.Add(E("Source Control", "Ctrl+Shift+G", h, "Ctrl+Shift+G"));
                    m.Add(E("Run and Debug", "Ctrl+Shift+D", h, "Ctrl+Shift+D"));
                    m.Add(E("Extensions", "Ctrl+Shift+X", h, "Ctrl+Shift+X"));
                    m.Add(E("Terminal", "Ctrl+`", h, "Ctrl+`"));
                    m.Add(E("Toggle Sidebar", "Ctrl+B", h, "Ctrl+B"));
                    m.Add(Sep());
                    m.Add(E("Zoom In", "Ctrl+=", h, "Ctrl+="));
                    m.Add(E("Zoom Out", "Ctrl+-", h, "Ctrl+-"));
                    m.Add(E("Reset Zoom", "Ctrl+0", h, "Ctrl+0"));
                    m.Add(E("Full Screen", "F11", h, "F11"));
                    break;

                case Kind.Terminal:
                    m.Add(E("Command Palette…", "Ctrl+Shift+P", h, "Ctrl+Shift+P"));
                    m.Add(Sep());
                    m.Add(E("Zoom In", "Ctrl+=", h, "Ctrl+="));
                    m.Add(E("Zoom Out", "Ctrl+-", h, "Ctrl+-"));
                    m.Add(E("Reset Zoom", "Ctrl+0", h, "Ctrl+0"));
                    m.Add(Sep());
                    m.Add(E("Toggle Full Screen", "F11", h, "F11"));
                    break;

                case Kind.Notepad:
                    m.Add(E("Zoom In", "Ctrl+=", h, "Ctrl+="));
                    m.Add(E("Zoom Out", "Ctrl+-", h, "Ctrl+-"));
                    m.Add(E("Restore Default Zoom", "Ctrl+0", h, "Ctrl+0"));
                    break;

                case Kind.Electron:
                    m.Add(E("Reload", "Ctrl+R", h, "Ctrl+R"));
                    m.Add(E("Force Reload", "Ctrl+Shift+R", h, "Ctrl+Shift+R"));
                    m.Add(Sep());
                    m.Add(E("Zoom In", "Ctrl+=", h, "Ctrl+="));
                    m.Add(E("Zoom Out", "Ctrl+-", h, "Ctrl+-"));
                    m.Add(E("Reset Zoom", "Ctrl+0", h, "Ctrl+0"));
                    m.Add(Sep());
                    m.Add(E("Toggle Full Screen", "F11", h, "F11"));
                    m.Add(E("Toggle Developer Tools", "Ctrl+Shift+I", h, "Ctrl+Shift+I"));
                    break;
            }
            return m;
        }
    }
}
