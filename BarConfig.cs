using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TopBar
{
    /// <summary>
    /// Persistent configuration for TopBar.  Saved as JSON next to the exe.
    /// </summary>
    public sealed class BarConfig
    {
        // ── General ─────────────────────────────────────────────────────────
        public string BarName { get; set; } = "TopBar";
        public bool PushWindows { get; set; } = true;
        public bool AlwaysVisible { get; set; } = true;
        public int RevealDelayMs { get; set; } = 500;
        public bool RunAtStartup { get; set; } = false;
        public string ToggleShortcut { get; set; } = "Ctrl+Shift+T";

        // ── Appearance ──────────────────────────────────────────────────────
        public string BarColor { get; set; } = "#B31E1E1E"; // macOS dark vibrancy
        public bool AnimatePopups { get; set; } = true;

        // ── Modules (which bar items are enabled) ───────────────────────────
        public bool ShowBattery { get; set; } = true;
        public bool ShowBrightness { get; set; } = false;
        public bool ShowBluetooth { get; set; } = true;
        public bool ShowWifi { get; set; } = true;
        public bool ShowSound { get; set; } = true;
        public bool ShowWindowTitle { get; set; } = true;
        public bool ShowClock { get; set; } = true;
        public bool ShowDesktopCorner { get; set; } = false;
        public bool ShowTrayIcons { get; set; } = true;
        public bool ShowSpotlight { get; set; } = true;
        public bool ShowCpu { get; set; } = true;
        public bool ShowMemory { get; set; } = true;
        public bool ShowDisk { get; set; } = true;
        public bool ShowGpu { get; set; } = true;
        public bool ShowControlCenter { get; set; } = true;

        // ── Layout order ────────────────────────────────────────────────────
        // Items on the right side of the bar, in display order (left-to-right).
        // "WindowTitle" is rendered in the centre column, not here.
        public List<string> RightItemOrder { get; set; } = new()
        {
            "TrayIcons",
            "Bluetooth",
            "Wifi",
            "Sound",
            "Battery",
            "Cpu",
            "Memory",
            "Disk",
            "Gpu",
            "ControlCenter",
            "Spotlight",
            "Clock"
        };

        // ── Persistence ─────────────────────────────────────────────────────

        private static readonly JsonSerializerOptions _jsonOpts = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };

        private static string ConfigPath =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "topbar-config.json");

        public static BarConfig Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var json = File.ReadAllText(ConfigPath);
                    var cfg = JsonSerializer.Deserialize<BarConfig>(json, _jsonOpts) ?? new BarConfig();

                    // Migrate: ensure newer items exist in the order list
                    var defaults = new BarConfig();
                    foreach (var key in defaults.RightItemOrder)
                    {
                        if (!cfg.RightItemOrder.Contains(key))
                        {
                            // Insert before Clock (or at end if Clock is absent)
                            int clockIdx = cfg.RightItemOrder.IndexOf("Clock");
                            if (clockIdx >= 0)
                                cfg.RightItemOrder.Insert(clockIdx, key);
                            else
                                cfg.RightItemOrder.Add(key);
                        }
                    }

                    return cfg;
                }
            }
            catch { }
            return new BarConfig();
        }

        public void Save()
        {
            try
            {
                var json = JsonSerializer.Serialize(this, _jsonOpts);
                File.WriteAllText(ConfigPath, json);
            }
            catch { }
        }
    }
}
