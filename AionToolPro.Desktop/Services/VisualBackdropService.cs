using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace AionToolPro.Desktop.Services
{
    /// <summary>
    /// Native Win32 & DWM visual engine for Windows 10 & Windows 11:
    /// 1) Real frosted Acrylic/Mica Glass backdrop (Win11 22H2+ DWMWA_SYSTEMBACKDROP_TYPE, Win11 21H2 Mica, Win10 BlurBehind).
    /// 2) Automatic fallback to smooth WS_EX_LAYERED translucency if Windows Transparency Effects are disabled globally.
    /// 3) Hardware-accelerated whole-window transparency (35%–100%) via WS_EX_LAYERED in Solid Mode.
    /// 4) Exact DPI-calibrated maximize frame inset for SingleBorderWindow + WindowChrome.
    /// </summary>
    public static class VisualBackdropService
    {
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
        private const int DWMWA_MICA_EFFECT = 1029;

        private const int DWMWCP_DONOTROUND = 1;
        private const int DWMWCP_ROUND = 2;

        private const int DWMSBT_DISABLE = 1;
        private const int DWMSBT_MAINWINDOW = 2;      // Mica
        private const int DWMSBT_TRANSIENTWINDOW = 3; // Acrylic

        private const int GWL_STYLE = -16;
        private const int GWL_EXSTYLE = -20;
        private const long WS_SYSMENU = 0x00080000L;
        private const int WS_EX_LAYERED = 0x00080000;
        private const uint LWA_ALPHA = 0x00000002;

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_FRAMECHANGED = 0x0020;

        private const int SM_CXSIZEFRAME = 32;
        private const int SM_CYSIZEFRAME = 33;
        private const int SM_CXPADDEDBORDER = 92;

        [StructLayout(LayoutKind.Sequential)]
        private struct MARGINS
        {
            public int cxLeftWidth;
            public int cxRightWidth;
            public int cyTopHeight;
            public int cyBottomHeight;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ACCENT_POLICY
        {
            public int AccentState;
            public int AccentFlags;
            public uint GradientColor;
            public int AnimationId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOWCOMPOSITIONATTRIBDATA
        {
            public int Attribute;
            public IntPtr Data;
            public int SizeOfData;
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [DllImport("dwmapi.dll")]
        private static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS pMarInset);

        [DllImport("user32.dll")]
        private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WINDOWCOMPOSITIONATTRIBDATA data);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetricsForDpi(int nIndex, uint dpi);

        // Cached state so dragging Transparency/Brightness/Contrast sliders never spams DWM frame calls
        private static bool? _lastDwmGlassActive;
        private static bool? _lastDarkFrame;
        private static bool? _lastMaximized;
        private static bool _lastLayeredActive;

        private static long GetWindowStyle(IntPtr hwnd) =>
            IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, GWL_STYLE).ToInt64() : GetWindowLong32(hwnd, GWL_STYLE);

        private static void SetWindowStyle(IntPtr hwnd, long value)
        {
            if (IntPtr.Size == 8)
                SetWindowLongPtr64(hwnd, GWL_STYLE, new IntPtr(value));
            else
                SetWindowLong32(hwnd, GWL_STYLE, (int)value);
        }

        private static long GetWindowExStyle(IntPtr hwnd) =>
            IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, GWL_EXSTYLE).ToInt64() : GetWindowLong32(hwnd, GWL_EXSTYLE);

        private static void SetWindowExStyle(IntPtr hwnd, long value)
        {
            if (IntPtr.Size == 8)
                SetWindowLongPtr64(hwnd, GWL_EXSTYLE, new IntPtr(value));
            else
                SetWindowLong32(hwnd, GWL_EXSTYLE, (int)value);
        }

        /// <summary>
        /// Permanently hides native Windows 10/11 OS caption buttons (-, [], X) on Glass/SingleBorderWindow
        /// by removing WS_SYSMENU while preserving WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX
        /// for native Win11 rounded corners, DWM Acrylic Glass, Aero Snap, and drag-from-maximize.
        /// </summary>
        public static void HideNativeCaptionButtons(IntPtr hwnd)
        {
            try
            {
                if (hwnd == IntPtr.Zero) return;
                long style = GetWindowStyle(hwnd);
                if ((style & WS_SYSMENU) != 0)
                {
                    SetWindowStyle(hwnd, style & ~WS_SYSMENU);
                    SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                        SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
                }
            }
            catch { }
        }

        public static bool IsWindowsTransparencyEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", false);
                object? val = key?.GetValue("EnableTransparency");
                if (val is int i) return i != 0;
            }
            catch { }
            return true;
        }

        public static Thickness GetMaximizedFrameThickness(Window window)
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                var dpi = VisualTreeHelper.GetDpi(window);
                double scaleX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
                double scaleY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;

                int frameX, frameY, padded;
                if (hwnd != IntPtr.Zero && Environment.OSVersion.Version.Build >= 14393)
                {
                    uint winDpi = GetDpiForWindow(hwnd);
                    if (winDpi < 96) winDpi = 96;
                    frameX = GetSystemMetricsForDpi(SM_CXSIZEFRAME, winDpi);
                    frameY = GetSystemMetricsForDpi(SM_CYSIZEFRAME, winDpi);
                    padded = GetSystemMetricsForDpi(SM_CXPADDEDBORDER, winDpi);
                }
                else
                {
                    frameX = GetSystemMetrics(SM_CXSIZEFRAME);
                    frameY = GetSystemMetrics(SM_CYSIZEFRAME);
                    padded = GetSystemMetrics(SM_CXPADDEDBORDER);
                }

                double padX = Math.Clamp((frameX + padded) / scaleX, 6.0, 12.0);
                double padY = Math.Clamp((frameY + padded) / scaleY, 6.0, 12.0);
                return new Thickness(padX, padY, padX, padY);
            }
            catch
            {
                return new Thickness(7);
            }
        }

        /// <summary>
        /// Applies native DWM Glass or Layered Transparency cleanly.
        /// Returns true if true DWM frosted blur is active (so ThemeManager should use low-alpha frosted brushes),
        /// or false if opaque/layered mode is active.
        /// </summary>
        public static bool ApplyWindowEffects(Window window, bool glassMode, bool isDarkSurface, int transparencyPercent, Color solidCanvasColor, bool forceRefresh = false)
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return glassMode;

                transparencyPercent = Math.Clamp(transparencyPercent, 35, 100);
                int build = Environment.OSVersion.Version.Build;
                bool isMaximized = window.WindowState == WindowState.Maximized;
                var hwndSource = HwndSource.FromHwnd(hwnd);

                // 0. Guarantee native OS caption buttons (-, [], X) are NEVER drawn on top of custom chrome
                HideNativeCaptionButtons(hwnd);

                // 1. Immersive Dark Mode frame hint (only when changed)
                if (forceRefresh || _lastDarkFrame != isDarkSurface)
                {
                    int darkFlag = isDarkSurface ? 1 : 0;
                    if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkFlag, sizeof(int)) != 0)
                    {
                        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref darkFlag, sizeof(int));
                    }
                    _lastDarkFrame = isDarkSurface;
                }

                // 2. Native Windows 11 rounded corners (only when state changes)
                if (build >= 22000 && (forceRefresh || _lastMaximized != isMaximized))
                {
                    int cornerPref = isMaximized ? DWMWCP_DONOTROUND : DWMWCP_ROUND;
                    DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPref, sizeof(int));
                    _lastMaximized = isMaximized;
                }

                // 3. Determine whether true DWM Glass blur can run on this OS configuration
                bool osAllowsBlur = IsWindowsTransparencyEnabled();
                bool useDwmGlass = glassMode && osAllowsBlur;

                if (useDwmGlass)
                {
                    // Ensure WS_EX_LAYERED is OFF so DWM Acrylic blur is never blocked
                    ClearLayeredOpacity(hwnd);

                    // Make WPF swapchain transparent so DWM blur shines cleanly through low-alpha brushes
                    if (hwndSource?.CompositionTarget != null && hwndSource.CompositionTarget.BackgroundColor != Colors.Transparent)
                        hwndSource.CompositionTarget.BackgroundColor = Colors.Transparent;

                    if (forceRefresh || _lastDwmGlassActive != true)
                    {
                        var margins = new MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
                        DwmExtendFrameIntoClientArea(hwnd, ref margins);

                        if (build >= 22621)
                        {
                            // Windows 11 22H2+: Native frosted Acrylic backdrop (fallback to Mica)
                            int backdrop = DWMSBT_TRANSIENTWINDOW;
                            int hr = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
                            if (hr != 0)
                            {
                                int mica = DWMSBT_MAINWINDOW;
                                DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref mica, sizeof(int));
                            }
                        }
                        else if (build >= 22000)
                        {
                            int enableMica = 1;
                            DwmSetWindowAttribute(hwnd, DWMWA_MICA_EFFECT, ref enableMica, sizeof(int));
                            ApplyWin10Accent(hwnd, accentState: 3, gradientColor: 0);
                        }
                        else
                        {
                            // Windows 10: smooth BlurBehind (zero drag lag)
                            ApplyWin10Accent(hwnd, accentState: 3, gradientColor: 0);
                        }

                        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
                        _lastDwmGlassActive = true;
                    }

                    return true;
                }
                else
                {
                    // Disable DWM Glass cleanly when transitioning out of Glass Mode
                    if (forceRefresh || _lastDwmGlassActive != false)
                    {
                        if (build >= 22621)
                        {
                            int none = DWMSBT_DISABLE;
                            DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref none, sizeof(int));
                        }
                        else if (build >= 22000)
                        {
                            int disableMica = 0;
                            DwmSetWindowAttribute(hwnd, DWMWA_MICA_EFFECT, ref disableMica, sizeof(int));
                            ApplyWin10Accent(hwnd, accentState: 0, gradientColor: 0);
                        }
                        else
                        {
                            ApplyWin10Accent(hwnd, accentState: 0, gradientColor: 0);
                        }

                        // Keep DWM glass margins extended (-1) to cooperate cleanly with WindowChrome GlassFrameThickness="-1",
                        // while opaque RootWindowBorder paints crisp solid pixels with zero stray OS borders.
                        _lastDwmGlassActive = false;
                    }

                    if (hwndSource?.CompositionTarget != null)
                    {
                        var opaqueBg = Color.FromRgb(solidCanvasColor.R, solidCanvasColor.G, solidCanvasColor.B);
                        if (hwndSource.CompositionTarget.BackgroundColor != opaqueBg)
                            hwndSource.CompositionTarget.BackgroundColor = opaqueBg;
                    }

                    // Apply smooth whole-window transparency via WS_EX_LAYERED whenever slider < 100%
                    // (or if Glass Mode was requested while Windows system Transparency Effects are turned off)
                    int effectiveAlphaPct = (glassMode && !osAllowsBlur) ? Math.Min(transparencyPercent, 88) : transparencyPercent;
                    if (effectiveAlphaPct < 100)
                    {
                        byte alpha = (byte)Math.Clamp((int)Math.Round(effectiveAlphaPct * 255.0 / 100.0), 89, 255);
                        ApplyLayeredOpacity(hwnd, alpha);
                    }
                    else
                    {
                        ClearLayeredOpacity(hwnd);
                    }

                    return false;
                }
            }
            catch
            {
                return false;
            }
        }

        private static void ApplyLayeredOpacity(IntPtr hwnd, byte alpha)
        {
            long exStyle = GetWindowExStyle(hwnd);
            bool needAddStyle = (exStyle & WS_EX_LAYERED) == 0;
            if (needAddStyle)
            {
                SetWindowExStyle(hwnd, exStyle | WS_EX_LAYERED);
            }

            SetLayeredWindowAttributes(hwnd, 0, alpha, LWA_ALPHA);

            if (needAddStyle || !_lastLayeredActive)
            {
                SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
                _lastLayeredActive = true;
            }
        }

        private static void ClearLayeredOpacity(IntPtr hwnd)
        {
            long exStyle = GetWindowExStyle(hwnd);
            if ((exStyle & WS_EX_LAYERED) != 0)
            {
                SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA);
                SetWindowExStyle(hwnd, exStyle & ~WS_EX_LAYERED);
                SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            }
            _lastLayeredActive = false;
        }

        private static void ApplyWin10Accent(IntPtr hwnd, int accentState, uint gradientColor)
        {
            try
            {
                var accent = new ACCENT_POLICY
                {
                    AccentState = accentState,
                    AccentFlags = accentState != 0 ? 2 : 0,
                    GradientColor = gradientColor,
                    AnimationId = 0
                };
                int size = Marshal.SizeOf(accent);
                IntPtr ptr = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(accent, ptr, false);
                    var data = new WINDOWCOMPOSITIONATTRIBDATA
                    {
                        Attribute = 19, // WCA_ACCENT_POLICY
                        SizeOfData = size,
                        Data = ptr
                    };
                    SetWindowCompositionAttribute(hwnd, ref data);
                }
                finally
                {
                    Marshal.FreeHGlobal(ptr);
                }
            }
            catch { }
        }
    }
}
