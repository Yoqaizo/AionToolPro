using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace AionToolPro.Desktop.Services
{
    public sealed class TrayIconManager : IDisposable
    {
        private const int WM_DPICHANGED = 0x02E0;
        private const int WM_TRAYICON = 0x8000 + 42;
        private const int WM_LBUTTONDBLCLK = 0x0203;
        private const int WM_RBUTTONUP = 0x0205;

        private const uint NIM_ADD = 0x00000000;
        private const uint NIM_MODIFY = 0x00000001;
        private const uint NIM_DELETE = 0x00000002;
        private const uint NIF_MESSAGE = 0x00000001;
        private const uint NIF_ICON = 0x00000002;
        private const uint NIF_TIP = 0x00000004;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATA
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public uint uTimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATA lpData);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern uint ExtractIconExW(string lpszFile, int nIconIndex, IntPtr[]? phiconLarge, IntPtr[]? phiconSmall, uint nIcons);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint RegisterWindowMessageW(string lpString);

        private readonly Window _window;
        private readonly ContextMenu _contextMenu;
        private readonly Action _onRestoreRequested;
        private readonly Action? _onDpiOrDisplayChanged;
        private HwndSource? _hwndSource;
        private IntPtr _hwnd;
        private IntPtr _iconHandle;
        private uint _wmTaskbarCreated;
        private string _lastTooltip = "AionToolPro";
        private bool _isAdded;
        private bool _disposed;

        public TrayIconManager(Window window, ContextMenu contextMenu, Action onRestoreRequested, Action? onDpiOrDisplayChanged = null)
        {
            _window = window;
            _contextMenu = contextMenu;
            _onRestoreRequested = onRestoreRequested;
            _onDpiOrDisplayChanged = onDpiOrDisplayChanged;
        }

        public void Initialize()
        {
            var helper = new WindowInteropHelper(_window);
            _hwnd = helper.Handle;
            if (_hwnd == IntPtr.Zero) return;

            _wmTaskbarCreated = RegisterWindowMessageW("TaskbarCreated");
            _hwndSource = HwndSource.FromHwnd(_hwnd);
            _hwndSource?.AddHook(WndProc);

            LoadAppIcon();
            _lastTooltip = $"AionToolPro {AioNetworkService.GetAppVersionString()}";
            AddTrayIcon(_lastTooltip);
        }

        private void LoadAppIcon()
        {
            try
            {
                string? exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath))
                {
                    using var p = Process.GetCurrentProcess();
                    exePath = p.MainModule?.FileName;
                }

                if (!string.IsNullOrEmpty(exePath))
                {
                    var smallIcons = new IntPtr[1];
                    if (ExtractIconExW(exePath, 0, null, smallIcons, 1) > 0 && smallIcons[0] != IntPtr.Zero)
                    {
                        _iconHandle = smallIcons[0];
                    }
                }
            }
            catch { }
        }

        private void AddTrayIcon(string tooltip)
        {
            if (_hwnd == IntPtr.Zero || _isAdded) return;

            var nid = new NOTIFYICONDATA
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = 1001,
                uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
                uCallbackMessage = WM_TRAYICON,
                hIcon = _iconHandle,
                szTip = TruncateTip(tooltip)
            };

            _isAdded = Shell_NotifyIconW(NIM_ADD, ref nid);
        }

        public void UpdateTooltip(string tooltip)
        {
            _lastTooltip = TruncateTip(tooltip);
            if (_disposed || !_isAdded || _hwnd == IntPtr.Zero) return;

            var nid = new NOTIFYICONDATA
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = 1001,
                uFlags = NIF_TIP,
                szTip = _lastTooltip
            };

            Shell_NotifyIconW(NIM_MODIFY, ref nid);
        }

        private static string TruncateTip(string tip)
        {
            if (string.IsNullOrEmpty(tip)) return "AionToolPro";
            return tip.Length <= 126 ? tip : tip[..126];
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // Automatically recover Tray icon if Windows Explorer restarts
            if (_wmTaskbarCreated != 0 && (uint)msg == _wmTaskbarCreated)
            {
                _isAdded = false;
                AddTrayIcon(_lastTooltip);
                return IntPtr.Zero;
            }

            // Re-sync monitor Hz, responsive layout & maximize frame padding when moving across DPI monitors
            if (msg == WM_DPICHANGED)
            {
                if (_onDpiOrDisplayChanged != null)
                    _window.Dispatcher.InvokeAsync(_onDpiOrDisplayChanged);
                return IntPtr.Zero;
            }

            if (msg == WM_TRAYICON)
            {
                int mouseMsg = lParam.ToInt32() & 0xFFFF;
                if (mouseMsg == WM_LBUTTONDBLCLK)
                {
                    _window.Dispatcher.InvokeAsync(_onRestoreRequested);
                    handled = true;
                }
                else if (mouseMsg == WM_RBUTTONUP)
                {
                    _window.Dispatcher.InvokeAsync(() =>
                    {
                        SetForegroundWindow(_hwnd);
                        _contextMenu.IsOpen = true;
                    });
                    handled = true;
                }
            }

            return IntPtr.Zero;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                if (_isAdded && _hwnd != IntPtr.Zero)
                {
                    var nid = new NOTIFYICONDATA
                    {
                        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
                        hWnd = _hwnd,
                        uID = 1001
                    };
                    Shell_NotifyIconW(NIM_DELETE, ref nid);
                    _isAdded = false;
                }
            }
            catch { }

            try { _hwndSource?.RemoveHook(WndProc); } catch { }
            try
            {
                if (_iconHandle != IntPtr.Zero)
                {
                    DestroyIcon(_iconHandle);
                    _iconHandle = IntPtr.Zero;
                }
            }
            catch { }
        }
    }
}
