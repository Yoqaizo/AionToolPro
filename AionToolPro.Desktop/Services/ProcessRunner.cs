using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AionToolPro.Desktop.Services
{
    public sealed record ProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError,
        bool WasCancelled = false,
        bool TimedOut = false)
    {
        public bool IsSuccess => ExitCode == 0 && !WasCancelled && !TimedOut;
    }

    public interface IProcessRunner
    {
        string ResolveSystemExecutable(string executableName);

        Task<ProcessResult> RunAsync(
            string executableName,
            IEnumerable<string> arguments,
            CancellationToken cancellationToken = default,
            Action<string>? onOutputLine = null,
            bool silent = false,
            TimeSpan? timeout = null);
    }

    public sealed class ProcessRunner : IProcessRunner
    {
        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(90);

        static ProcessRunner()
        {
            // Register Windows OEM/ANSI code pages (437, 850, 720, 1256, etc.) in .NET 8 without extra NuGet packages
            try
            {
                var providerType = Type.GetType("System.Text.CodePagesEncodingProvider, System.Text.Encoding.CodePages");
                var instanceProp = providerType?.GetProperty("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (instanceProp?.GetValue(null) is EncodingProvider provider)
                {
                    Encoding.RegisterProvider(provider);
                }
            }
            catch { }
        }

        public string ResolveSystemExecutable(string executableName)
        {
            if (string.IsNullOrWhiteSpace(executableName))
                throw new ArgumentException("Executable name cannot be empty.", nameof(executableName));

            if (executableName.Contains(Path.DirectorySeparatorChar) ||
                executableName.Contains(Path.AltDirectorySeparatorChar) ||
                executableName.Contains(".."))
            {
                throw new ArgumentException($"Relative or custom paths are forbidden: {executableName}", nameof(executableName));
            }

            string sysDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

            string cleanName = executableName.Trim();
            string exeWithExt = cleanName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? cleanName
                : cleanName + ".exe";

            string fullPath;
            if (exeWithExt.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase))
            {
                fullPath = Path.Combine(sysDir, "WindowsPowerShell", "v1.0", "powershell.exe");
            }
            else if (exeWithExt.Equals("notepad.exe", StringComparison.OrdinalIgnoreCase))
            {
                string sysNotepad = Path.Combine(sysDir, "notepad.exe");
                fullPath = File.Exists(sysNotepad) ? sysNotepad : Path.Combine(winDir, "notepad.exe");
            }
            else
            {
                fullPath = Path.Combine(sysDir, exeWithExt);
            }

            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException(
                    $"Blocked execution: '{exeWithExt}' was not found in Windows System32 ({fullPath}).",
                    fullPath);
            }

            return fullPath;
        }

        private static Encoding ResolveConsoleEncoding(string exeName)
        {
            if (exeName.Equals("sfc", StringComparison.OrdinalIgnoreCase) ||
                exeName.Equals("sfc.exe", StringComparison.OrdinalIgnoreCase))
            {
                return Encoding.Unicode;
            }

            try
            {
                return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            }
            catch
            {
                return Encoding.UTF8;
            }
        }

        public async Task<ProcessResult> RunAsync(
            string executableName,
            IEnumerable<string> arguments,
            CancellationToken cancellationToken = default,
            Action<string>? onOutputLine = null,
            bool silent = false,
            TimeSpan? timeout = null)
        {
            string fullPath = ResolveSystemExecutable(executableName);
            Encoding consoleEncoding = ResolveConsoleEncoding(executableName);

            var psi = new ProcessStartInfo
            {
                FileName = fullPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = consoleEncoding,
                StandardErrorEncoding = consoleEncoding
            };

            foreach (var arg in arguments)
            {
                psi.ArgumentList.Add(arg);
            }

            using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            var outDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var errDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null)
                {
                    outDone.TrySetResult(true);
                    return;
                }

                string line = e.Data.TrimEnd();
                if (line.Length == 0) return;

                lock (stdout)
                {
                    stdout.AppendLine(line);
                }

                if (!silent)
                {
                    onOutputLine?.Invoke(line);
                }
            };

            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null)
                {
                    errDone.TrySetResult(true);
                    return;
                }

                string line = e.Data.TrimEnd();
                if (line.Length == 0) return;

                lock (stderr)
                {
                    stderr.AppendLine(line);
                }

                if (!silent)
                {
                    onOutputLine?.Invoke("[ERR] " + line);
                }
            };

            using var timeoutCts = new CancellationTokenSource(timeout ?? DefaultTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            try
            {
                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                await proc.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
                await Task.WhenAll(outDone.Task, errDone.Task)
                    .WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None)
                    .ConfigureAwait(false);

                return new ProcessResult(
                    proc.ExitCode,
                    stdout.ToString().TrimEnd(),
                    stderr.ToString().TrimEnd());
            }
            catch (OperationCanceledException)
            {
                bool timedOut = timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
                try
                {
                    if (!proc.HasExited)
                        proc.Kill(entireProcessTree: true);
                }
                catch { }

                return new ProcessResult(
                    -1,
                    stdout.ToString().TrimEnd(),
                    stderr.ToString().TrimEnd(),
                    WasCancelled: !timedOut,
                    TimedOut: timedOut);
            }
            catch (TimeoutException)
            {
                return new ProcessResult(
                    proc.HasExited ? proc.ExitCode : -1,
                    stdout.ToString().TrimEnd(),
                    stderr.ToString().TrimEnd());
            }
        }
    }
}
