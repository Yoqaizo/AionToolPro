using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
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

    /// <summary>
    /// Native Win32 codepage decoder using kernel32 MultiByteToWideChar so OEM console outputs
    /// decode accurately in self-contained .NET 8 single-file builds without extra NuGet packages.
    /// </summary>
    internal sealed class Win32CodePageEncoding : Encoding
    {
        private readonly uint _codePage;

        [DllImport("kernel32.dll")]
        private static extern uint GetOEMCP();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int MultiByteToWideChar(
            uint codePage,
            uint dwFlags,
            byte[] lpMultiByteStr,
            int cbMultiByte,
            char[]? lpWideCharStr,
            int cchWideChar);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int WideCharToMultiByte(
            uint codePage,
            uint dwFlags,
            char[] lpWideCharStr,
            int cchWideChar,
            byte[]? lpMultiByteStr,
            int cbMultiByte,
            IntPtr lpDefaultChar,
            IntPtr lpUsedDefaultChar);

        public static Encoding CreateSystemOem()
        {
            uint cp = 437;
            try { cp = GetOEMCP(); } catch { }
            return new Win32CodePageEncoding(cp == 0 ? 437u : cp);
        }

        public Win32CodePageEncoding(uint codePage) => _codePage = codePage;

        public override int GetByteCount(char[] chars, int index, int count)
        {
            if (chars == null || count <= 0) return 0;
            char[] slice = index == 0 && count == chars.Length ? chars : chars[index..(index + count)];
            int len = WideCharToMultiByte(_codePage, 0, slice, slice.Length, null, 0, IntPtr.Zero, IntPtr.Zero);
            return Math.Max(0, len);
        }

        public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex)
        {
            if (chars == null || bytes == null || charCount <= 0) return 0;
            char[] slice = charIndex == 0 && charCount == chars.Length ? chars : chars[charIndex..(charIndex + charCount)];
            int needed = WideCharToMultiByte(_codePage, 0, slice, slice.Length, null, 0, IntPtr.Zero, IntPtr.Zero);
            if (needed <= 0) return 0;
            byte[] buf = new byte[needed];
            int written = WideCharToMultiByte(_codePage, 0, slice, slice.Length, buf, needed, IntPtr.Zero, IntPtr.Zero);
            if (written <= 0) return 0;
            int copied = Math.Min(written, Math.Max(0, bytes.Length - byteIndex));
            if (copied > 0) Buffer.BlockCopy(buf, 0, bytes, byteIndex, copied);
            return copied;
        }

        public override int GetCharCount(byte[] bytes, int index, int count)
        {
            if (bytes == null || count <= 0) return 0;
            byte[] slice = index == 0 && count == bytes.Length ? bytes : bytes[index..(index + count)];
            int len = MultiByteToWideChar(_codePage, 0, slice, slice.Length, null, 0);
            return Math.Max(0, len);
        }

        public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
        {
            if (bytes == null || chars == null || byteCount <= 0) return 0;
            byte[] slice = byteIndex == 0 && byteCount == bytes.Length ? bytes : bytes[byteIndex..(byteIndex + byteCount)];
            int needed = MultiByteToWideChar(_codePage, 0, slice, slice.Length, null, 0);
            if (needed <= 0) return 0;
            char[] buf = new char[needed];
            int written = MultiByteToWideChar(_codePage, 0, slice, slice.Length, buf, needed);
            if (written <= 0) return 0;
            int copied = Math.Min(written, Math.Max(0, chars.Length - charIndex));
            if (copied > 0) Array.Copy(buf, 0, chars, charIndex, copied);
            return copied;
        }

        public override int GetMaxByteCount(int charCount) => (charCount + 1) * 4;
        public override int GetMaxCharCount(int byteCount) => (byteCount * 2) + 1;
    }

    public sealed class ProcessRunner : IProcessRunner
    {
        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(90);
        private static readonly Encoding OemConsoleEncoding = InitializeOemEncoding();

        private static Encoding InitializeOemEncoding()
        {
            try
            {
                var providerType = Type.GetType("System.Text.CodePagesEncodingProvider, System.Text.Encoding.CodePages");
                var instanceProp = providerType?.GetProperty("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (instanceProp?.GetValue(null) is EncodingProvider provider)
                {
                    Encoding.RegisterProvider(provider);
                }
                return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            }
            catch
            {
                try
                {
                    return Win32CodePageEncoding.CreateSystemOem();
                }
                catch
                {
                    return Encoding.UTF8;
                }
            }
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
            return OemConsoleEncoding;
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
