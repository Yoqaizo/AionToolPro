using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace AionToolPro.Desktop
{
    public partial class App : Application
    {
        private static Mutex? _singleInstanceMutex;
        private static bool _ownsMutex;

        private const uint MB_OK = 0x00000000;
        private const uint MB_ICONERROR = 0x00000010;

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

        protected override void OnStartup(StartupEventArgs e)
        {
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
            AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;
            SessionEnding += OnSessionEnding;

            const string mutexName = @"Global\AionToolPro_SingleInstance_Mutex_v083";
            try
            {
                _singleInstanceMutex = new Mutex(true, mutexName, out bool createdNew);
                _ownsMutex = createdNew;
            }
            catch (AbandonedMutexException)
            {
                // Previous process terminated abnormally; this process now owns the mutex safely
                _ownsMutex = true;
            }
            catch (UnauthorizedAccessException)
            {
                _ownsMutex = false;
            }

            if (!_ownsMutex)
            {
                try
                {
                    using var current = Process.GetCurrentProcess();
                    foreach (var proc in Process.GetProcessesByName(current.ProcessName))
                    {
                        using (proc)
                        {
                            if (proc.Id != current.Id && proc.MainWindowHandle != IntPtr.Zero)
                            {
                                ShowWindow(proc.MainWindowHandle, 9); // SW_RESTORE
                                SetForegroundWindow(proc.MainWindowHandle);
                                break;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    WriteCrashLog("SingleInstanceActivate", ex);
                }

                Shutdown();
                return;
            }

            base.OnStartup(e);
        }

        private void OnSessionEnding(object sender, SessionEndingCancelEventArgs e)
        {
            if (MainWindow is MainWindow mw)
            {
                mw.RequestCleanShutdown();
            }
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            WriteCrashLog("DispatcherUnhandledException", e.Exception);
            try
            {
                MessageBox.Show(
                    $"An unexpected error occurred and was safely caught:\n\n{e.Exception.Message}\n\nDetails were saved to %APPDATA%\\AionToolPro\\crash.log.",
                    "AionToolPro - Unexpected Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch
            {
                try
                {
                    MessageBoxW(
                        IntPtr.Zero,
                        $"Unexpected error: {e.Exception.Message}\nSee %APPDATA%\\AionToolPro\\crash.log.",
                        "AionToolPro - Error",
                        MB_OK | MB_ICONERROR);
                }
                catch { }
            }
            e.Handled = true;
        }

        private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            WriteCrashLog("UnobservedTaskException", e.Exception);
            e.SetObserved();
        }

        private void OnCurrentDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
            {
                WriteCrashLog("AppDomainUnhandledException", ex);
                try
                {
                    string inner = ex.InnerException != null ? $"\nInner: {ex.InnerException.Message}" : string.Empty;
                    MessageBoxW(
                        IntPtr.Zero,
                        $"A fatal startup or runtime error occurred:\n\n{ex.GetType().Name}: {ex.Message}{inner}\n\nDetails were saved to %APPDATA%\\AionToolPro\\crash.log.",
                        "AionToolPro - Fatal Error",
                        MB_OK | MB_ICONERROR);
                }
                catch { }
            }
        }

        public static void WriteCrashLog(string source, Exception ex)
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "AionToolPro");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "crash.log");

                var sb = new StringBuilder();
                sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}] {ex.GetType().FullName}: {ex.Message}");
                sb.AppendLine(ex.StackTrace);

                Exception? inner = ex.InnerException;
                int depth = 1;
                while (inner != null && depth <= 5)
                {
                    sb.AppendLine($"  --- InnerException #{depth}: {inner.GetType().FullName}: {inner.Message}");
                    sb.AppendLine(inner.StackTrace);
                    inner = inner.InnerException;
                    depth++;
                }

                sb.AppendLine(new string('-', 70));
                File.AppendAllText(path, sb.ToString());
            }
            catch { }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                if (_ownsMutex && _singleInstanceMutex != null)
                {
                    _singleInstanceMutex.ReleaseMutex();
                }
                _singleInstanceMutex?.Dispose();
            }
            catch { }
            base.OnExit(e);
        }
    }
}
