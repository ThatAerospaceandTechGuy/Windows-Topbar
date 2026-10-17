using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TopBar
{
    /// <summary>
    /// Shared macOS-menu chrome: uniform menu rows (label left, shortcut hint
    /// right, rounded hover) plus the frosted panel finish. Panels render as
    /// a flat opaque fill so the soft rounded corners always hold — the
    /// WCA blur-behind is deliberately not used (on recent Windows builds it
    /// degrades to an opaque black rectangle that swallows the corners).
    /// </summary>
    internal static class MenuChrome
    {
        public static readonly CornerRadius PopupRadius = new(10);
        public static readonly CornerRadius PillRadius = new(8);
        public static readonly CornerRadius RowRadius = new(6);
        public static readonly Thickness ContentPad = new(16, 14, 16, 14);

        private static readonly SolidColorBrush PanelFill =
            new(Color.FromArgb(0xFF, 0x1E, 0x1E, 0x22));

        /// <summary>
        /// Applies the frosted finish to a popup window. Call from Loaded.
        /// </summary>
        public static void Apply(Window w)
        {
            if (w.Content is Border root)
            {
                // Soft rounded corners only — no outline, no ring.
                root.Background = PanelFill;
                root.Margin = new Thickness(0);
                root.Effect = null;
                root.BorderThickness = new Thickness(0);
                root.BorderBrush = null;
            }
        }

        public static Border MenuRow(string label, string hint, Action onClick, bool enabled = true)
        {
            var hoverBg = Application.Current.Resources["PopupHoverBg"] as SolidColorBrush
                ?? new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF));
            var fg = Application.Current.Resources["PopupFgBright"] as SolidColorBrush
                ?? new SolidColorBrush(Colors.White);
            var dim = Application.Current.Resources["PopupFgLabel"] as SolidColorBrush
                ?? new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var l = new TextBlock
            {
                Text = label, Foreground = enabled ? fg : dim, FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(l, 0);
            grid.Children.Add(l);
            if (!string.IsNullOrEmpty(hint))
            {
                var h = new TextBlock
                {
                    Text = hint, Foreground = dim, FontSize = 11.5,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0)
                };
                Grid.SetColumn(h, 1);
                grid.Children.Add(h);
            }

            var border = new Border
            {
                Background = Brushes.Transparent,
                CornerRadius = RowRadius,
                Cursor = enabled ? Cursors.Hand : Cursors.Arrow,
                Padding = new Thickness(8, 6, 8, 6),
                Child = grid
            };
            if (enabled)
            {
                border.MouseEnter += (_, _) => border.Background = hoverBg;
                border.MouseLeave += (_, _) => border.Background = Brushes.Transparent;
                border.MouseLeftButtonUp += (_, _) => { try { onClick(); } catch { } };
            }
            return border;
        }

        /// <summary>Bold popup title shown at the top of a panel.</summary>
        public static TextBlock Header(string text) => new()
        {
            Text = text,
            Style = Application.Current.Resources["PopupHeader"] as Style
        };

        /// <summary>Small "pill" action button (Border + label).</summary>
        public static Border Pill(string text, Action onClick, string tooltip = "")
        {
            var tb = new TextBlock
            {
                Text = text,
                Foreground = Application.Current.Resources["PopupFgNormal"] as SolidColorBrush
                    ?? new SolidColorBrush(Colors.White),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            };
            var border = new Border
            {
                Style = Application.Current.Resources["Pill"] as Style,
                Child = tb,
                ToolTip = string.IsNullOrEmpty(tooltip) ? null : tooltip
            };
            border.MouseLeftButtonUp += (_, e) => { e.Handled = true; try { onClick(); } catch { } };
            return border;
        }

        public static TextBlock SectionLabel(string text)
        {
            var tb = new TextBlock { Text = text };
            if (Application.Current.Resources["PopupSection"] is Style s) tb.Style = s;
            else
            {
                tb.Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
                tb.FontSize = 11;
                tb.Margin = new Thickness(8, 2, 8, 6);
            }
            return tb;
        }

        public static Border Separator() => new()
        {
            Background = Application.Current.Resources["PopupSeparator"] as SolidColorBrush
                ?? new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            Height = 1,
            Margin = new Thickness(8, 6, 8, 6)
        };
    }
}
