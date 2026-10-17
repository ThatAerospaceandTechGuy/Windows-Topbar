using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using static TopBar.NativeMethods;

namespace TopBar
{
    /// <summary>
    /// Generic macOS-style dropdown menu. Used for the active-app menu and the
    /// File / Edit / View / Window / Help menus. Leaf rows run actions (real
    /// menu commands or keystrokes); parent rows expand one nested level.
    /// </summary>
    public partial class BarMenuPopup : Window
    {
        public sealed class MenuEntry
        {
            public string Label = "";
            public string Hint = "";
            public bool Enabled = true;
            public bool IsSeparator;
            public Action? Action;
            public Func<List<MenuEntry>>? Submenu;
        }

        private bool _ready, _closing;
        private DispatcherTimer? _leaveTimer;
        private readonly BarMenuPopup? _parent;
        private BarMenuPopup? _child;
        private string? _childLabel;

        public BarMenuPopup(string title, List<(string Label, string Hint, Action Action)> items)
            : this(title, Convert(items), null)
        {
        }

        public BarMenuPopup(string title, List<MenuEntry> entries)
            : this(title, entries, null)
        {
        }

        private BarMenuPopup(string title, List<MenuEntry> entries, BarMenuPopup? parent)
        {
            InitializeComponent();
            _parent = parent;
            Loaded += (_, _) => MenuChrome.Apply(this);
            ContentRendered += (_, _) => _ready = true;

            MenuTitle.Text = title;

            foreach (var e in entries)
            {
                if (e.IsSeparator) { ItemHost.Children.Add(MenuChrome.Separator()); continue; }
                var entry = e;
                if (entry.Submenu != null && entry.Enabled)
                {
                    string hint = string.IsNullOrEmpty(entry.Hint) ? "›" : entry.Hint;
                    Border row = null!;
                    row = MenuChrome.MenuRow(entry.Label, hint, () => OpenChild(row, entry));
                    ItemHost.Children.Add(row);
                }
                else
                {
                    ItemHost.Children.Add(MenuChrome.MenuRow(entry.Label, entry.Hint, () =>
                    {
                        try { entry.Action?.Invoke(); } catch { }
                        CloseChain();
                    }, entry.Enabled));
                }
            }
        }

        private static List<MenuEntry> Convert(List<(string Label, string Hint, Action Action)> items)
        {
            var r = new List<MenuEntry>(items.Count);
            foreach (var (label, hint, action) in items)
                r.Add(new MenuEntry { Label = label, Hint = hint, Action = action });
            return r;
        }

        private void OpenChild(Border row, MenuEntry entry)
        {
            if (_childLabel == entry.Label) { CloseChild(); return; }
            CloseChild();
            List<MenuEntry> sub;
            try { sub = entry.Submenu!(); }
            catch { return; }
            if (sub.Count == 0) return;

            var child = new BarMenuPopup(entry.Label, sub, this)
            {
                Owner = null,
                ShowActivated = false,
                Topmost = true
            };
            var p = row.PointToScreen(new Point(row.ActualWidth - 2, -6));
            var wa = SystemParameters.WorkArea;
            child.Left = p.X + 284 > wa.Right ? p.X - row.ActualWidth - 284 + 4 : p.X;
            child.Top = Math.Max(wa.Top + 4, Math.Min(p.Y, wa.Bottom - 120));
            child.Show();
            // Clamp vertically once real height is known.
            child.Loaded += (_, _) =>
            {
                if (child.Top + child.ActualHeight > wa.Bottom)
                    child.Top = Math.Max(wa.Top + 4, wa.Bottom - child.ActualHeight - 8);
            };
            _child = child;
            _childLabel = entry.Label;
        }

        private void CloseChild()
        {
            _childLabel = null;
            var c = _child;
            _child = null;
            try { c?.CloseDirect(); } catch { }
        }

        private void CloseDirect()
        {
            if (_closing) return;
            _closing = true;
            _leaveTimer?.Stop();
            try { Close(); } catch { }
        }

        /// <summary>Run from a leaf: close this popup and everything above it.</summary>
        private void CloseChain()
        {
            if (_parent != null) _parent.SafeClose();
            else SafeClose();
        }

        private IntPtr Hwnd
        {
            get
            {
                try { return new WindowInteropHelper(this).Handle; }
                catch { return IntPtr.Zero; }
            }
        }

        // ── Close logic ─────────────────────────────────────────────────────
        // Clicking between parent/child moves activation between our own
        // windows — only close when focus actually leaves the whole chain.
        private void Window_Deactivated(object sender, EventArgs e)
        {
            if (!_ready) return;
            try
            {
                IntPtr fg = GetForegroundWindow();
                if (_child != null && fg == _child.Hwnd) return;
                if (_parent != null && fg == _parent.Hwnd) return;
            }
            catch { }
            SafeClose();
        }

        private void Window_MouseLeave(object sender, MouseEventArgs e)
        {
            if (_leaveTimer == null)
            {
                _leaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                _leaveTimer.Tick += (_, _) =>
                {
                    _leaveTimer.Stop();
                    // Still hovering the other half of the chain? Stay open.
                    if ((_child != null && _child.IsMouseOver) ||
                        (_parent != null && _parent.IsMouseOver)) return;
                    SafeClose();
                };
            }
            _leaveTimer.Start();
        }
        private void Window_MouseEnter(object sender, MouseEventArgs e) { _leaveTimer?.Stop(); }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.Key == Key.Escape) SafeClose(); base.OnKeyDown(e); }
        private void SafeClose()
        {
            if (_closing) return;
            _closing = true;
            _leaveTimer?.Stop();
            CloseChild();
            try { Close(); } catch { }
        }
    }
}
