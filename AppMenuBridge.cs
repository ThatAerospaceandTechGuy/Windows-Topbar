using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using static TopBar.NativeMethods;

namespace TopBar
{
    /// <summary>
    /// Per-app tailored menus via UI Automation — no hooks, everything runs
    /// on click only:
    /// - Generic: expand/invoke the app's own native File/Edit/… control
    ///   (Office ribbon tabs, WPF menus, …) so the app opens its real menu.
    /// - Explorer (CabinetWClass): mirror the command-bar "New"/"View"
    ///   dropdowns and the Cut/Copy/Paste/Rename/Share/Delete buttons, so
    ///   File shows New folder / New file…, exactly like the app itself.
    ///   Dropdowns render in separate popup windows (not the app tree), so
    ///   they are found by owning process among top-level windows.
    /// Name matching is English-locale; anything unfound falls through to
    /// the next mechanism (Alt-mnemonic, then generic shortcuts).
    /// </summary>
    internal static class AppMenuBridge
    {
        public sealed class ActionItem
        {
            public string Label = "";
            public string Hint = "";
            public bool Enabled = true;
            public Action Invoke = () => { };
        }

        /// <summary>Flyout rows can be MenuItems, ListItems or buttons
        /// depending on the WinUI version — accept them all.</summary>
        private static OrCondition MenuLikeCondition() => new OrCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.SplitButton),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));

        private static int PidOf(IntPtr hwnd)
        {
            try { GetWindowThreadProcessId(hwnd, out uint pid); return (int)pid; }
            catch { return 0; }
        }

        /// <summary>Find a named clickable control, preferring on-screen hits.</summary>
        private static AutomationElement? FindNamed(AutomationElement root, string name)
        {
            try
            {
                var cond = new AndCondition(
                    new PropertyCondition(AutomationElement.NameProperty, name),
                    new OrCondition(
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem),
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem),
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.SplitButton),
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)));
                var found = new List<AutomationElement>();
                try
                {
                    foreach (AutomationElement el in root.FindAll(TreeScope.Children, cond))
                        found.Add(el);
                }
                catch { }
                if (found.Count == 0)
                {
                    try
                    {
                        foreach (AutomationElement el in root.FindAll(TreeScope.Descendants, cond))
                            found.Add(el);
                    }
                    catch { }
                }
                AutomationElement? fallback = null;
                foreach (var el in found)
                {
                    bool off = true;
                    try { off = el.Current.IsOffscreen; } catch { continue; }
                    if (!off) return el;
                    fallback ??= el;
                }
                return fallback;
            }
            catch { return null; }
        }

        private static bool SupportsExpand(AutomationElement el)
        {
            try { return el.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out _); }
            catch { return false; }
        }

        private static void CollapseDrop(AutomationElement drop)
        {
            try
            {
                if (drop.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out object? o) &&
                    o is ExpandCollapsePattern exp &&
                    exp.Current.ExpandCollapseState == ExpandCollapseState.Expanded)
                    exp.Collapse();
            }
            catch { }
        }

        private static bool Act(AutomationElement el)
        {
            try
            {
                if (el.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out object? expObj) &&
                    expObj is ExpandCollapsePattern exp)
                {
                    try
                    {
                        if (exp.Current.ExpandCollapseState != ExpandCollapseState.Expanded)
                            exp.Expand();
                    }
                    catch { exp.Expand(); }
                    return true;
                }
            }
            catch { }
            try
            {
                if (el.TryGetCurrentPattern(InvokePattern.Pattern, out object? invObj) &&
                    invObj is InvokePattern inv)
                {
                    inv.Invoke();
                    return true;
                }
            }
            catch { }
            try
            {
                if (el.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object? selObj) &&
                    selObj is SelectionItemPattern sel)
                {
                    sel.Select(); // ribbon tabs (Office File → Backstage)
                    return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>Open the app's own native control named e.g. "File".</summary>
        public static bool TryExpandNativeControl(IntPtr hwnd, string label)
        {
            try
            {
                var root = AutomationElement.FromHandle(hwnd);
                if (root == null) return false;
                var el = FindNamed(root, label);
                if (el == null) return false;
                return Act(el);
            }
            catch { return false; }
        }

        public static bool IsExplorerWindow(IntPtr hwnd)
        {
            try
            {
                var sb = new StringBuilder(256);
                if (GetClassName(hwnd, sb, sb.Capacity) <= 0) return false;
                if (sb.ToString() != "CabinetWClass") return false;
                try
                {
                    using var p = Process.GetProcessById(PidOf(hwnd));
                    return p.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase);
                }
                catch { return false; }
            }
            catch { return false; }
        }

        /// <summary>Tailored Explorer dropdowns. File = New-folder/file
        /// templates, Edit = command-bar buttons, View = view dropdown.</summary>
        public static List<ActionItem> GetExplorerItems(IntPtr hwnd, string tag)
        {
            return tag switch
            {
                "File" => ExpandAndRead(hwnd, "New", folderHint: true),
                "View" => ExpandAndRead(hwnd, "View", folderHint: false),
                "Edit" => ReadCommandButtons(hwnd),
                _ => new List<ActionItem>()
            };
        }

        private static readonly (string Name, string Hint)[] EditButtons =
        {
            ("Cut", "Ctrl+X"), ("Copy", "Ctrl+C"), ("Paste", "Ctrl+V"),
            ("Rename", "F2"), ("Share", ""), ("Delete", "Del")
        };

        private static List<ActionItem> ReadCommandButtons(IntPtr hwnd)
        {
            var items = new List<ActionItem>();
            try
            {
                var root = AutomationElement.FromHandle(hwnd);
                if (root == null) return items;
                foreach (var (name, hint) in EditButtons)
                {
                    var el = FindNamed(root, name);
                    if (el == null) continue;
                    bool enabled = true;
                    try { enabled = el.Current.IsEnabled; } catch { }
                    string n = name;
                    items.Add(new ActionItem
                    {
                        Label = n,
                        Hint = hint,
                        Enabled = enabled,
                        Invoke = enabled ? () => InvokeExplorerItem(hwnd, null, n) : () => { }
                    });
                }
            }
            catch { }
            return items;
        }

        /// <summary>Visible top-level windows owned by a process, excluding one.</summary>
        private static Dictionary<IntPtr, (bool Onscreen, System.Windows.Rect Rect)> SnapshotPopups(int pid)
        {
            var d = new Dictionary<IntPtr, (bool, System.Windows.Rect)>();
            try
            {
                AutomationElementCollection tops;
                try { tops = AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition); }
                catch { return d; }
                foreach (AutomationElement w in tops)
                {
                    int wpid;
                    IntPtr wh;
                    try
                    {
                        wpid = w.Current.ProcessId;
                        wh = new IntPtr(w.Current.NativeWindowHandle);
                    }
                    catch { continue; }
                    if (wpid != pid || wh == IntPtr.Zero) continue;
                    bool off = true;
                    System.Windows.Rect r = System.Windows.Rect.Empty;
                    try { off = w.Current.IsOffscreen; r = w.Current.BoundingRectangle; }
                    catch { continue; }
                    d[wh] = (!off, r);
                }
            }
            catch { }
            return d;
        }

        /// <summary>
        /// Popups that appeared, reshown or moved since the snapshot — i.e.
        /// the dropdown WE just opened. Pre-existing shell chrome (taskbar,
        /// Start menu, desktop — all explorer.exe) is excluded by construction.
        /// </summary>
        private static List<AutomationElement> ChangedPopups(
            int pid, IntPtr exclude,
            Dictionary<IntPtr, (bool Onscreen, System.Windows.Rect Rect)> before)
        {
            var r = new List<AutomationElement>();
            try
            {
                AutomationElementCollection tops;
                try { tops = AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition); }
                catch { return r; }
                foreach (AutomationElement w in tops)
                {
                    int wpid;
                    IntPtr wh;
                    try
                    {
                        wpid = w.Current.ProcessId;
                        wh = new IntPtr(w.Current.NativeWindowHandle);
                    }
                    catch { continue; }
                    if (wpid != pid || wh == IntPtr.Zero || wh == exclude) continue;
                    bool off = true;
                    System.Windows.Rect rect = System.Windows.Rect.Empty;
                    try { off = w.Current.IsOffscreen; rect = w.Current.BoundingRectangle; }
                    catch { continue; }
                    if (off) continue;
                    if (before.TryGetValue(wh, out var prev) && prev.Onscreen && prev.Rect == rect)
                        continue; // pre-existing and unchanged (taskbar, desktop, …)
                    r.Add(w);
                }
            }
            catch { }
            return r;
        }

        private static List<string> PopupMenuItemNames(List<AutomationElement> popups)
        {
            var names = new List<string>();
            var like = MenuLikeCondition();
            foreach (var w in popups)
            {
                AutomationElementCollection items;
                try { items = w.FindAll(TreeScope.Descendants, like); }
                catch { continue; }
                foreach (AutomationElement el in items)
                {
                    string n;
                    try { n = (el.Current.Name ?? "").Trim(); } catch { continue; }
                    if (n.Length == 0) continue;
                    try { if (el.Current.IsOffscreen || !el.Current.IsEnabled) continue; }
                    catch { continue; }
                    if (!names.Contains(n)) names.Add(n);
                }
            }
            return names;
        }

        private static AutomationElement? FindPopupMenuItem(List<AutomationElement> popups, string name)
        {
            foreach (var w in popups)
            {
                AutomationElement? el;
                try
                {
                    el = w.FindFirst(TreeScope.Descendants, new AndCondition(
                        new PropertyCondition(AutomationElement.NameProperty, name), MenuLikeCondition()));
                }
                catch { continue; }
                if (el == null) continue;
                try { if (el.Current.IsOffscreen) continue; } catch { continue; }
                return el;
            }
            return null;
        }

        private static bool SameSet(List<string> a, List<string> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        private static List<ActionItem> ExpandAndRead(IntPtr hwnd, string dropdown, bool folderHint)
        {
            var items = new List<ActionItem>();
            try
            {
                int pid = PidOf(hwnd);
                if (pid == 0) return items;
                var root = AutomationElement.FromHandle(hwnd);
                if (root == null) return items;
                var drop = FindNamed(root, dropdown);
                // Never Invoke() here: on a plain button that would run its
                // default action (e.g. create a folder) just from opening File.
                if (drop == null || !SupportsExpand(drop)) return items;
                var before = SnapshotPopups(pid);
                bool expanded = false;
                try
                {
                    if (!Act(drop)) return items;
                    expanded = true;

                // The flyout populates asynchronously (Folder first, templates
                // after the shell query) — poll until the set stops changing.
                // Only freshly appeared/changed popups count, so taskbar and
                // Start menu (same process) can never leak in.
                List<string> names = new(), prev = new();
                for (int attempt = 0; attempt < 12; attempt++)
                {
                    if (attempt > 0) Thread.Sleep(100);
                    names = PopupMenuItemNames(ChangedPopups(pid, hwnd, before));
                    if (names.Count > 0 && SameSet(names, prev)) break;
                    prev = names;
                }
                // Note: opening our popup moves activation, which usually
                // dismisses Explorer's dropdown by itself — but collapse it
                // explicitly so it can never get stuck on screen.
                foreach (string n in names)
                {
                    string hint = (folderHint && n.Equals("Folder", StringComparison.OrdinalIgnoreCase))
                        ? "Ctrl+Shift+N" : "";
                    string d = dropdown;
                    items.Add(new ActionItem
                    {
                        Label = n,
                        Hint = hint,
                        Invoke = () => InvokeExplorerItem(hwnd, d, n)
                    });
                }
                }
                finally
                {
                    if (expanded) CollapseDrop(drop);
                }
            }
            catch { }
            return items;
        }

        /// <summary>Re-find by name at click time (trees go stale) and invoke.</summary>
        public static void InvokeExplorerItem(IntPtr hwnd, string? dropdown, string name)
        {
            try
            {
                if (ForegroundMenu.ValidateTarget(hwnd) == IntPtr.Zero) return;
                int pid = PidOf(hwnd);
                if (pid == 0) return;
                if (dropdown != null)
                {
                    var root = AutomationElement.FromHandle(hwnd);
                    var drop = root == null ? null : FindNamed(root, dropdown);
                    if (drop == null || !SupportsExpand(drop)) return;
                    var before = SnapshotPopups(pid);
                    bool expanded = false;
                    try
                    {
                        if (!Act(drop)) return;
                        expanded = true;
                        AutomationElement? item = null;
                        for (int i = 0; i < 10 && item == null; i++)
                        {
                            if (i > 0) Thread.Sleep(100);
                            item = FindPopupMenuItem(ChangedPopups(pid, hwnd, before), name);
                        }
                        if (item == null) return;
                        Act(item);
                    }
                    finally
                    {
                        if (expanded) CollapseDrop(drop);
                    }
                }
                else
                {
                    var root = AutomationElement.FromHandle(hwnd);
                    var el = root == null ? null : FindNamed(root, name);
                    if (el == null) return;
                    Act(el);
                }
                try { SetForegroundWindow(hwnd); } catch { }
            }
            catch { }
        }
    }
}
