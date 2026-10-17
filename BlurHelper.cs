using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using static TopBar.NativeMethods;

namespace TopBar
{
    /// <summary>
    /// Applies the acrylic blur-behind effect to any WPF window.
    /// </summary>
    internal static class BlurHelper
    {
        /// <summary>Current bar tint color in AABBGGRR format. Popups use this automatically.</summary>
        public static uint CurrentColor = 0xBF000000;

        public static void EnableBlur(Window window) => EnableBlur(window, CurrentColor);

        /// <summary>
        /// Plain blur-behind (no tint). Unlike acrylic this respects per-pixel
        /// alpha, so rounded popup corners stay transparent instead of showing
        /// a sharp tinted rectangle. Pair with an opaque-ish panel fill.
        /// </summary>
        public static void EnableBlurBehind(Window window)
        {
            var hwnd = new WindowInteropHelper(window).EnsureHandle();

            var accent = new AccentPolicy
            {
                AccentState = ACCENT_ENABLE_BLURBEHIND
            };

            int accentSize = Marshal.SizeOf(accent);
            IntPtr accentPtr = Marshal.AllocHGlobal(accentSize);
            try
            {
                Marshal.StructureToPtr(accent, accentPtr, false);

                var data = new WindowCompositionAttributeData
                {
                    Attribute = WindowCompositionAttribute.WCA_ACCENT_POLICY,
                    Data = accentPtr,
                    SizeOfData = accentSize
                };

                SetWindowCompositionAttribute(hwnd, ref data);
            }
            finally
            {
                Marshal.FreeHGlobal(accentPtr);
            }
        }

        public static void EnableBlur(Window window, uint gradientColor)
        {
            var hwnd = new WindowInteropHelper(window).EnsureHandle();

            var accent = new AccentPolicy
            {
                AccentState = ACCENT_ENABLE_ACRYLICBLURBEHIND,
                GradientColor = gradientColor
            };

            int accentSize = Marshal.SizeOf(accent);
            IntPtr accentPtr = Marshal.AllocHGlobal(accentSize);
            try
            {
                Marshal.StructureToPtr(accent, accentPtr, false);

                var data = new WindowCompositionAttributeData
                {
                    Attribute = WindowCompositionAttribute.WCA_ACCENT_POLICY,
                    Data = accentPtr,
                    SizeOfData = accentSize
                };

                SetWindowCompositionAttribute(hwnd, ref data);
            }
            finally
            {
                Marshal.FreeHGlobal(accentPtr);
            }
        }
    }
}
