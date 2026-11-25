using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using AionToolPro.Desktop.Models;

namespace AionToolPro.Desktop.Services
{
    public sealed partial class AioNetworkService
    {
        // ==================== ATOMIC & SAFE HOSTS FILE OPERATIONS ====================
        private static void EnsureFileWritable(string filePath)
        {
            if (!File.Exists(filePath)) return;
            var attrs = File.GetAttributes(filePath);
            if ((attrs & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System)) != 0)
            {
                File.SetAttributes(filePath, FileAttributes.Normal);
            }
        }

        private async Task AtomicWriteHostsAsync(string content, CancellationToken token = default)
        {
            if (IsReparsePoint(_hostsPath))
                throw new UnauthorizedAccessException("Blocked: Windows hosts file is a symbolic link / reparse point.");
            string dir = Path.GetDirectoryName(_hostsPath)!;
            string tempPath = Path.Combine(dir, $"hosts.aion.{Guid.NewGuid():N}.tmp");
            var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    await File.WriteAllTextAsync(tempPath, content, utf8NoBom, token).ConfigureAwait(false);
                    if (File.Exists(_hostsPath))
                    {
                        EnsureFileWritable(_hostsPath);
                        File.Replace(tempPath, _hostsPath, null, ignoreMetadataErrors: true);
                    }
                    else
                    {
                        File.Move(tempPath, _hostsPath);
                    }
                    return;
                }
                catch when (attempt < 3)
                {
                    await Task.Delay(250, token).ConfigureAwait(false);
                }
                finally
                {
                    try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                }
            }

            await File.WriteAllTextAsync(tempPath, content, utf8NoBom, token).ConfigureAwait(false);
            EnsureFileWritable(_hostsPath);
            File.Replace(tempPath, _hostsPath, null, ignoreMetadataErrors: true);
        }

        public Task<string> GetCustomHostsSummaryAsync()
        {
            return Task.Run(() =>
            {
                if (!File.Exists(_hostsPath)) return "Windows hosts file not found.";
                int totalCustom = 0;
                var tagCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                string? activeTag = null;
                var sampleNonTagLines = new List<string>();

                foreach (var raw in File.ReadLines(_hostsPath))
                {
                    string line = raw.Trim();
                    if (line.Length == 0) continue;

                    if (line.StartsWith("# AIONTOOL-BEGIN:", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = line.Split(':');
                        activeTag = parts.Length >= 3 ? parts[2] : (parts.Length >= 2 ? parts[1] : "Unknown");
                        tagCounts.TryAdd(activeTag, 0);
                        continue;
                    }
                    if (line.StartsWith("# AIONTOOL-END:", StringComparison.OrdinalIgnoreCase))
                    {
                        activeTag = null;
                        continue;
                    }
                    if (line.StartsWith('#')) continue;

                    totalCustom++;
                    if (activeTag != null)
                        tagCounts[activeTag] = tagCounts.GetValueOrDefault(activeTag) + 1;
                    else if (sampleNonTagLines.Count < 40)
                        sampleNonTagLines.Add(line);
                }

                var sb = new StringBuilder();
                sb.AppendLine($"Hosts File Path    : {_hostsPath}");
                sb.AppendLine($"Total Active Rules : {totalCustom:N0}");
                if (totalCustom > 50_000)
                {
                    sb.AppendLine("Performance Note   : Very large hosts files (>50,000 entries) can increase Windows DNS Client (Dnscache) CPU usage.");
                }
                if (tagCounts.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("=== Installed AionToolPro Blocklists ===");
                    foreach (var kv in tagCounts)
                        sb.AppendLine($"  • {kv.Key}: {kv.Value:N0} entries");
                }
                if (sampleNonTagLines.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("=== Custom Manual Entries (up to 40 shown) ===");
                    foreach (var s in sampleNonTagLines)
                        sb.AppendLine("  " + s);
                }
                sb.AppendLine();
                sb.AppendLine("Tip: Click 'Open in Notepad' to inspect or edit the complete hosts file.");
                return sb.ToString();
            });
        }

        public void OpenHostsInNotepad()
        {
            string notepadPath = _runner.ResolveSystemExecutable("notepad.exe");
            var psi = new ProcessStartInfo { FileName = notepadPath, UseShellExecute = false };
            psi.ArgumentList.Add(_hostsPath);
            Process.Start(psi);
        }

        public Task<string> BackupHostsFileAsync(string interfaceName)
        {
            return Task.Run(() =>
            {
                lock (_ioLock)
                {
                    Directory.CreateDirectory(_cfgDir);
                    if (!File.Exists(_hostsPath))
                        throw new FileNotFoundException("Windows hosts file not found.", _hostsPath);

                    if (!File.Exists(HostsOriginalBackupPath))
                        File.Copy(_hostsPath, HostsOriginalBackupPath, overwrite: false);

                    string stamped = Path.Combine(_cfgDir, $"hosts.backup.{DateTime.Now:yyyyMMdd_HHmmss}");
                    File.Copy(_hostsPath, stamped, overwrite: true);
                    PruneOldHostsBackups();
                    WriteActionLog(interfaceName, $"OK Backed up hosts file to {Path.GetFileName(stamped)}");
                    return stamped;
                }
            });
        }

        private void PruneOldHostsBackups()
        {
            try
            {
                var backups = Directory.GetFiles(_cfgDir, "hosts.backup.*")
                    .Where(p => !p.EndsWith(".original", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .Skip(MaxHostsBackupsToKeep)
                    .ToList();

                foreach (var old in backups)
                {
                    try { File.Delete(old); } catch { }
                }
            }
            catch { }
        }

        public async Task<OperationResult> RestoreHostsFileAsync(string interfaceName, CancellationToken token = default)
        {
            string? backupToRestore = null;
            lock (_ioLock)
            {
                if (Directory.Exists(_cfgDir))
                {
                    backupToRestore = Directory.GetFiles(_cfgDir, "hosts.backup.*")
                        .Where(p => !p.EndsWith(".original", StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(File.GetLastWriteTimeUtc)
                        .FirstOrDefault();
                }
                if (backupToRestore == null && File.Exists(HostsOriginalBackupPath))
                    backupToRestore = HostsOriginalBackupPath;
            }

            if (backupToRestore == null)
                return new OperationResult(false, "No hosts backup file found.");

            string content = await File.ReadAllTextAsync(backupToRestore, token).ConfigureAwait(false);
            await AtomicWriteHostsAsync(content, token).ConfigureAwait(false);
            await ExecAsync("ipconfig", new[] { "/flushdns" }, token, silent: true).ConfigureAwait(false);
            WriteActionLog(interfaceName, $"OK Restored hosts from {Path.GetFileName(backupToRestore)}");
            return new OperationResult(true, $"Restored hosts file from {Path.GetFileName(backupToRestore)}.");
        }

        public async Task<OperationResult> ResetHostsToDefaultAsync(string interfaceName, CancellationToken token = default)
        {
            await BackupHostsFileAsync(interfaceName).ConfigureAwait(false);
            string defaultHosts =
                "# Copyright (c) 1993-2009 Microsoft Corp.\r\n" +
                "#\r\n" +
                "# This is a sample HOSTS file used by Microsoft TCP/IP for Windows.\r\n" +
                "# localhost name resolution is handled within DNS itself.\r\n" +
                "#\t127.0.0.1       localhost\r\n" +
                "#\t::1             localhost\r\n";

            await AtomicWriteHostsAsync(defaultHosts, token).ConfigureAwait(false);
            await ExecAsync("ipconfig", new[] { "/flushdns" }, token, silent: true).ConfigureAwait(false);
            WriteActionLog(interfaceName, "OK Reset hosts file to Windows default");
            return new OperationResult(true, "Hosts file reset to Windows default.");
        }

        public async Task<int> ApplyBlocklistAsync(BlocklistDefinition list, string interfaceName, CancellationToken token = default)
        {
            var validEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (list.IsBuiltInCustom && list.Tag == "TeamSpeak-Blocked")
            {
                string[] tsHosts =
                {
                    "0.0.0.0 accounting.teamspeak.com",
                    "0.0.0.0 backupaccounting.teamspeak.com",
                    "0.0.0.0 blacklist.teamspeak.com",
                    "0.0.0.0 blacklist2.teamspeak.com",
                    "0.0.0.0 ipcheck.teamspeak.com",
                    "0.0.0.0 hardy.teamspeak.4players.de",
                    "0.0.0.0 weblist.teamspeak.com",
                    "0.0.0.0 reportpiracy-env.elasticbeanstalk.com",
                    "0.0.0.0 versions.teamspeak.com"
                };
                foreach (var e in tsHosts) validEntries.Add(e);
            }
            else
            {
                if (!Uri.TryCreate(list.DownloadUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                    throw new InvalidOperationException("Only HTTPS blocklist URLs are permitted.");

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                cts.CancelAfter(TimeSpan.FromSeconds(45));

                using var req = new HttpRequestMessage(HttpMethod.Get, uri);
                using var resp = await SharedHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();

                if (resp.Content.Headers.ContentLength > MaxBlocklistBytes)
                    throw new InvalidOperationException($"Blocklist exceeds the {MaxBlocklistBytes / 1048576} MB safety cap.");

                using var stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
                using var reader = new StreamReader(stream, Encoding.UTF8);

                long bytesRead = 0;
                string? rawLine;
                while ((rawLine = await reader.ReadLineAsync(cts.Token).ConfigureAwait(false)) != null)
                {
                    bytesRead += rawLine.Length + 2;
                    if (bytesRead > MaxBlocklistBytes)
                        throw new InvalidOperationException("Downloaded blocklist exceeded 8 MB safety limit.");

                    string cleaned = rawLine.Trim();
                    if (cleaned.Length == 0 || cleaned[0] == '#') continue;

                    int commentIdx = cleaned.IndexOf('#');
                    if (commentIdx > 0) cleaned = cleaned[..commentIdx].Trim();

                    var parts = cleaned.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2) continue;

                    string ip = parts[0];
                    if (ip != "0.0.0.0" && ip != "127.0.0.1") continue;

                    // FIX: Parse ALL hostnames on multi-host lines (e.g. `0.0.0.0 a.com b.com`)
                    for (int k = 1; k < parts.Length; k++)
                    {
                        string host = parts[k].ToLowerInvariant();
                        if (AioCatalog.ValidateHostOrIp(host) &&
                            !IPAddress.TryParse(host, out _) &&
                            host != "localhost" &&
                            host != "localhost.localdomain" &&
                            host != "broadcasthost" &&
                            host != "ip6-localhost" &&
                            host != "ip6-loopback")
                        {
                            validEntries.Add($"0.0.0.0 {host}");
                            if (validEntries.Count > MaxBlocklistLines)
                                throw new InvalidOperationException($"Blocklist exceeded {MaxBlocklistLines:N0} entry safety cap.");
                        }
                    }
                }
            }

            if (validEntries.Count == 0)
                throw new InvalidOperationException("No valid hosts entries were found in the downloaded list.");

            if (validEntries.Count > 50_000)
            {
                Emit($"[HOSTS NOTE] Large blocklist ({validEntries.Count:N0} entries) — Windows DNS Client (Dnscache) may briefly use extra CPU while reloading.");
            }

            await BackupHostsFileAsync(interfaceName).ConfigureAwait(false);

            var existingLines = File.Exists(_hostsPath)
                ? (await File.ReadAllLinesAsync(_hostsPath, token).ConfigureAwait(false)).ToList()
                : new List<string>();

            var filtered = StripBlocklistTagSafe(existingLines, list.Tag);

            var sb = new StringBuilder(filtered.Count * 32 + validEntries.Count * 32);
            foreach (var l in filtered) sb.AppendLine(l);
            if (filtered.Count > 0 && !string.IsNullOrWhiteSpace(filtered[^1]))
                sb.AppendLine();

            sb.AppendLine($"# AIONTOOL-BEGIN:{list.Tag}:{list.Name}:{DateTime.Now:yyyy-MM-dd}");
            foreach (var entry in validEntries)
                sb.AppendLine(entry);
            sb.AppendLine($"# AIONTOOL-END:{list.Tag}");

            await AtomicWriteHostsAsync(sb.ToString(), token).ConfigureAwait(false);
            await ExecAsync("ipconfig", new[] { "/flushdns" }, token, silent: true).ConfigureAwait(false);

            WriteActionLog(interfaceName, $"OK Added blocklist {list.Name} ({validEntries.Count} entries)");
            return validEntries.Count;
        }

        public async Task<bool> RemoveBlocklistByTagAsync(string tag, string interfaceName, CancellationToken token = default)
        {
            if (!File.Exists(_hostsPath)) return false;
            var existingLines = (await File.ReadAllLinesAsync(_hostsPath, token).ConfigureAwait(false)).ToList();
            var filtered = StripBlocklistTagSafe(existingLines, tag);
            if (filtered.Count == existingLines.Count) return false;

            await AtomicWriteHostsAsync(string.Join(Environment.NewLine, filtered) + Environment.NewLine, token).ConfigureAwait(false);
            await ExecAsync("ipconfig", new[] { "/flushdns" }, token, silent: true).ConfigureAwait(false);
            WriteActionLog(interfaceName, $"OK Removed blocklist {tag}");
            return true;
        }

        public static List<string> StripBlocklistTagSafe(IReadOnlyList<string> lines, string tag)
        {
            string beginPrefix = $"# AIONTOOL-BEGIN:{tag}:";
            string endExact = $"# AIONTOOL-END:{tag}";
            string legacyBegin = $"# AIOTOOL-BEGIN:{tag}:";
            string legacyEnd = $"# AIOTOOL-END:{tag}";

            var output = new List<string>(lines.Count);
            int i = 0;
            while (i < lines.Count)
            {
                string trimmed = lines[i].Trim();
                if (trimmed.StartsWith(beginPrefix, StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith(legacyBegin, StringComparison.OrdinalIgnoreCase))
                {
                    int endIndex = -1;
                    for (int j = i + 1; j < lines.Count; j++)
                    {
                        string t2 = lines[j].Trim();
                        if (t2.Equals(endExact, StringComparison.OrdinalIgnoreCase) ||
                            t2.Equals(legacyEnd, StringComparison.OrdinalIgnoreCase))
                        {
                            endIndex = j;
                            break;
                        }
                    }

                    if (endIndex >= 0)
                    {
                        i = endIndex + 1;
                        continue;
                    }
                }

                output.Add(lines[i]);
                i++;
            }
            return output;
        }

        // ==================== PROXY, FIREWALL, ARP & REPAIR ====================
        [DllImport("wininet.dll", SetLastError = true)]
        private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

        private const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
        private const int INTERNET_OPTION_REFRESH = 37;

        public async Task<string> GetProxyStatusAsync(CancellationToken token = default)
        {
            var sb = new StringBuilder();
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", false);
            sb.AppendLine("=== Current User Proxy Settings ===");
            sb.AppendLine($"ProxyEnable   : {key?.GetValue("ProxyEnable") ?? 0}");
            sb.AppendLine($"ProxyServer   : {key?.GetValue("ProxyServer") ?? "None"}");
            sb.AppendLine($"AutoConfigURL : {key?.GetValue("AutoConfigURL") ?? "None"}");
            sb.AppendLine();
            sb.AppendLine("=== System WinHTTP Proxy ===");
            var res = await ExecAsync("netsh", new[] { "winhttp", "show", "proxy" }, token).ConfigureAwait(false);
            sb.AppendLine(res.StandardOutput);
            return sb.ToString();
        }

        public async Task<OperationResult> DisableSystemProxyAsync(string interfaceName, CancellationToken token = default)
        {
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", writable: true))
            {
                key?.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                if (key?.GetValue("AutoConfigURL") != null)
                    key.DeleteValue("AutoConfigURL", throwOnMissingValue: false);
            }

            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);

            var res = await ExecAsync("netsh", new[] { "winhttp", "reset", "proxy" }, token).ConfigureAwait(false);
            WriteActionLog(interfaceName, $"{(res.IsSuccess ? "OK" : "FAILED")} Disabled system proxy + refreshed WinINet");
            return new OperationResult(res.IsSuccess, "System proxy disabled and WinINet notified.", res.ExitCode);
        }

        public async Task<OperationResult> ResetFirewallAsync(string interfaceName, CancellationToken token = default)
        {
            Directory.CreateDirectory(_cfgDir);
            string backupFile = Path.Combine(_cfgDir, $"firewall_backup_{DateTime.Now:yyyyMMdd_HHmmss}.wfw");

            var exportRes = await ExecAsync("netsh", new[] { "advfirewall", "export", backupFile }, token).ConfigureAwait(false);
            if (exportRes.IsSuccess)
                Emit($"[FIREWALL] Current policy backed up to: {backupFile}");
            else
                Emit($"[FIREWALL WARN] Policy backup returned exit code {exportRes.ExitCode}.");

            var resetRes = await ExecAsync("netsh", new[] { "advfirewall", "reset" }, token).ConfigureAwait(false);
            if (resetRes.IsSuccess)
                SaveBlockedApps(new List<FirewallBlockedApp>());

            WriteActionLog(interfaceName, $"{(resetRes.IsSuccess ? "OK" : "FAILED")} Reset firewall (backup={Path.GetFileName(backupFile)})");
            return new OperationResult(
                resetRes.IsSuccess,
                resetRes.IsSuccess
                    ? $"Windows Firewall reset to defaults.\nBackup saved at:\n{backupFile}"
                    : $"Firewall reset failed: {resetRes.StandardOutput}",
                resetRes.ExitCode);
        }

        public async Task<OperationResult> ResetArpAndRoutesAsync(string interfaceName, CancellationToken token = default)
        {
            interfaceName = ValidateInterfaceName(interfaceName);
            await ExecAsync("netsh", new[] { "interface", "ip", "delete", "arpcache" }, token).ConfigureAwait(false);
            await ExecAsync("route", new[] { "-f" }, token).ConfigureAwait(false);
            var renewRes = await ExecAsync("ipconfig", new[] { "/renew", interfaceName }, token).ConfigureAwait(false);
            WriteActionLog(interfaceName, "OK Reset ARP cache and routing table");
            return new OperationResult(renewRes.IsSuccess, "ARP cache cleared and routing table reset. Reconnect VPNs if active.", renewRes.ExitCode);
        }

        public async Task<OperationResult> FullNetworkRepairAsync(string interfaceName)
        {
            using var lease = CreateTrackedOperationCts(TimeSpan.FromMinutes(2));
            var token = lease.Token;

            var r1 = await ExecAsync("netsh", new[] { "winsock", "reset" }, token).ConfigureAwait(false);
            if (r1.WasCancelled) return new OperationResult(false, "Network repair cancelled.", -1);

            var r2 = await ExecAsync("netsh", new[] { "int", "ip", "reset" }, token).ConfigureAwait(false);
            if (r2.WasCancelled) return new OperationResult(false, "Network repair cancelled.", -1);

            await ExecAsync("ipconfig", new[] { "/flushdns" }, token).ConfigureAwait(false);
            bool ok = r1.IsSuccess && r2.IsSuccess;
            WriteActionLog(interfaceName, $"{(ok ? "OK" : "PARTIAL")} Full network repair (reboot required)");
            return new OperationResult(ok, "Winsock & TCP/IP stack reset completed. Please restart Windows.", ok ? 0 : -1);
        }

        // ==================== SAFE MAINTENANCE ====================
        public Task<(int FileCount, double TotalMb)> EstimateCleanableTempFilesAsync()
        {
            return Task.Run(() =>
            {
                DateTime cutoff = DateTime.UtcNow.AddHours(-24);
                int count = 0;
                long bytes = 0;
                string[] targets = { Path.GetTempPath(), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp") };

                foreach (var folder in targets.Where(Directory.Exists))
                {
                    foreach (var file in Directory.GetFiles(folder))
                    {
                        try
                        {
                            var fi = new FileInfo(file);
                            if ((fi.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                            if (fi.LastWriteTimeUtc < cutoff)
                            {
                                count++;
                                bytes += fi.Length;
                            }
                        }
                        catch { }
                    }
                }
                return (count, Math.Round(bytes / 1048576.0, 2));
            });
        }

        public async Task<string> CleanTempFilesAsync(string interfaceName)
        {
            int deletedFiles = 0;
            int skippedRecent = 0;
            int skippedLocked = 0;
            long freedBytes = 0;
            DateTime cutoff = DateTime.UtcNow.AddHours(-24);

            await Task.Run(() =>
            {
                string[] targets = { Path.GetTempPath(), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp") };
                foreach (var folder in targets.Where(Directory.Exists))
                {
                    foreach (var file in Directory.GetFiles(folder))
                    {
                        try
                        {
                            var fi = new FileInfo(file);
                            if ((fi.Attributes & FileAttributes.ReparsePoint) != 0) { skippedLocked++; continue; }
                            if (fi.LastWriteTimeUtc >= cutoff) { skippedRecent++; continue; }
                            long len = fi.Length;
                            File.Delete(file);
                            deletedFiles++;
                            freedBytes += len;
                        }
                        catch { skippedLocked++; }
                    }
                    foreach (var dir in Directory.GetDirectories(folder))
                    {
                        try
                        {
                            var di = new DirectoryInfo(dir);
                            // CRITICAL: Never delete %TEMP%\.net (single-file .NET native extraction cache) or symlinks/junctions
                            if (di.Name.Equals(".net", StringComparison.OrdinalIgnoreCase) ||
                                (di.Attributes & FileAttributes.ReparsePoint) != 0)
                            {
                                skippedLocked++;
                                continue;
                            }
                            if (di.LastWriteTimeUtc >= cutoff) { skippedRecent++; continue; }
                            Directory.Delete(dir, true);
                            deletedFiles++;
                        }
                        catch { skippedLocked++; }
                    }
                }
            }).ConfigureAwait(false);

            double freedMb = Math.Round(freedBytes / 1048576.0, 2);
            WriteActionLog(interfaceName, $"OK Cleaned temp files ({deletedFiles} items, {freedMb} MB)");
            string msg = $"Temp Cleanup Finished: {deletedFiles} items removed (~{freedMb:N2} MB), {skippedRecent} recent (<24h) preserved, {skippedLocked} locked skipped.";
            Emit(msg);
            return msg;
        }

        public async Task<OperationResult> RunSystemRepairDismSfcAsync(string interfaceName)
        {
            using var lease = CreateTrackedOperationCts(TimeSpan.FromMinutes(45));
            var token = lease.Token;

            var dism = await ExecAsync("DISM.exe", new[] { "/Online", "/Cleanup-Image", "/RestoreHealth" }, token, timeout: TimeSpan.FromMinutes(30)).ConfigureAwait(false);
            if (dism.WasCancelled)
            {
                WriteActionLog(interfaceName, "CANCELLED DISM & SFC repair");
                return new OperationResult(false, "System repair cancelled during DISM.", -1);
            }

            var sfc = await ExecAsync("sfc.exe", new[] { "/scannow" }, token, timeout: TimeSpan.FromMinutes(20)).ConfigureAwait(false);
            if (sfc.WasCancelled)
            {
                WriteActionLog(interfaceName, "CANCELLED SFC scannow");
                return new OperationResult(false, "System repair cancelled during SFC.", -1);
            }

            bool ok = dism.IsSuccess && sfc.IsSuccess;
            WriteActionLog(interfaceName, $"{(ok ? "OK" : "PARTIAL")} DISM (exit={dism.ExitCode}) & SFC (exit={sfc.ExitCode})");
            return new OperationResult(ok, $"DISM exit={dism.ExitCode}, SFC exit={sfc.ExitCode}.", ok ? 0 : -1);
        }

        private async Task<bool> IsWindowsServiceRunningAsync(string serviceName, CancellationToken token)
        {
            var res = await _runner.RunAsync("sc", new[] { "query", serviceName }, token, silent: true, timeout: TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            return res.IsSuccess && (res.StandardOutput.Contains("RUNNING", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(res.StandardOutput, @":\s*4\s"));
        }

        public async Task<OperationResult> ResetWindowsUpdateAsync(string interfaceName)
        {
            using var lease = CreateTrackedOperationCts(TimeSpan.FromMinutes(5));
            var token = lease.Token;

            string[] services = { "wuauserv", "cryptSvc", "bits", "msiserver" };
            var previouslyRunning = new List<string>();

            try
            {
                foreach (var s in services)
                {
                    token.ThrowIfCancellationRequested();
                    if (await IsWindowsServiceRunningAsync(s, token).ConfigureAwait(false))
                    {
                        previouslyRunning.Add(s);
                        await ExecAsync("net", new[] { "stop", s }, token, timeout: TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                    }
                }

                token.ThrowIfCancellationRequested();
                string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                string softDist = Path.Combine(winDir, "SoftwareDistribution");
                string catroot2 = Path.Combine(winDir, "System32", "catroot2");

                SafeRenameDirectory(softDist, softDist + ".old");
                SafeRenameDirectory(catroot2, catroot2 + ".old");

                WriteActionLog(interfaceName, "OK Reset Windows Update cache");
                return new OperationResult(true, "Windows Update cache folders reset successfully.");
            }
            catch (OperationCanceledException)
            {
                WriteActionLog(interfaceName, "CANCELLED Reset Windows Update");
                return new OperationResult(false, "Windows Update reset was cancelled. Restarting services...", -1);
            }
            catch (Exception ex)
            {
                WriteActionLog(interfaceName, $"FAILED Reset Windows Update: {ex.Message}");
                return new OperationResult(false, $"Windows Update reset error: {ex.Message}", -1);
            }
            finally
            {
                foreach (var s in previouslyRunning)
                {
                    try { await ExecAsync("net", new[] { "start", s }, CancellationToken.None, timeout: TimeSpan.FromSeconds(30)).ConfigureAwait(false); } catch { }
                }
            }
        }

        private void SafeRenameDirectory(string source, string backupDest)
        {
            if (!Directory.Exists(source) || IsReparsePoint(source)) return;
            try
            {
                if (Directory.Exists(backupDest))
                    Directory.Delete(backupDest, true);
                Directory.Move(source, backupDest);
                Emit($"[WU RESET] Renamed {Path.GetFileName(source)} -> {Path.GetFileName(backupDest)}");
            }
            catch (Exception ex)
            {
                Emit($"[WU RESET WARN] Could not rename {Path.GetFileName(source)}: {ex.Message}");
            }
        }

        // ==================== SAFE GAME BOOSTER ====================
        public bool IsGameBoosterActive()
        {
            lock (_ioLock)
            {
                return File.Exists(GameBoosterStateFilePath);
            }
        }

        public async Task<OperationResult> OptimizeForGamingAsync()
        {
            using var lease = CreateTrackedOperationCts(TimeSpan.FromMinutes(2));
            var token = lease.Token;

            Emit("[GAME BOOSTER] Checking and safely suspending non-essential Windows services...");
            string[] candidateServices = { "SysMain", "DiagTrack", "WSearch", "wuauserv" };
            var stoppedByUs = new List<string>();

            try
            {
                foreach (var srv in candidateServices)
                {
                    token.ThrowIfCancellationRequested();
                    if (await IsWindowsServiceRunningAsync(srv, token).ConfigureAwait(false))
                    {
                        var res = await ExecAsync("net", new[] { "stop", srv }, token, timeout: TimeSpan.FromSeconds(25)).ConfigureAwait(false);
                        if (res.IsSuccess)
                            stoppedByUs.Add(srv);
                    }
                }

                lock (_ioLock)
                {
                    Directory.CreateDirectory(_cfgDir);
                    var existing = File.Exists(GameBoosterStateFilePath)
                        ? JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(GameBoosterStateFilePath)) ?? new HashSet<string>()
                        : new HashSet<string>();

                    foreach (var s in stoppedByUs) existing.Add(s);
                    File.WriteAllText(GameBoosterStateFilePath, JsonSerializer.Serialize(existing, JsonOpts), new UTF8Encoding(false));
                }

                string[] safeWidgets = { "WidgetService", "Widgets", "YourPhone", "PhoneExperienceHost" };
                int killed = 0;
                foreach (var app in safeWidgets)
                {
                    foreach (var proc in Process.GetProcessesByName(app))
                    {
                        using (proc)
                        {
                            try { proc.Kill(); killed++; } catch { }
                        }
                    }
                }

                string summary = $"Game Booster active: suspended {stoppedByUs.Count} service(s) ({(stoppedByUs.Count > 0 ? string.Join(", ", stoppedByUs) : "none were running")}), closed {killed} widget process(es).";
                Emit($"[GAME BOOSTER] {summary}");
                WriteActionLog("System", $"OK {summary}");
                return new OperationResult(true, summary);
            }
            catch (OperationCanceledException)
            {
                return new OperationResult(false, "Game Booster cancelled by user.", -1);
            }
        }

        public async Task<OperationResult> RestoreNormalModeAsync()
        {
            using var lease = CreateTrackedOperationCts(TimeSpan.FromMinutes(2));
            var token = lease.Token;

            HashSet<string> servicesToRestore;
            lock (_ioLock)
            {
                servicesToRestore = File.Exists(GameBoosterStateFilePath)
                    ? JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(GameBoosterStateFilePath)) ?? new HashSet<string>()
                    : new HashSet<string>();
            }

            if (servicesToRestore.Count == 0)
            {
                lock (_ioLock)
                {
                    try { if (File.Exists(GameBoosterStateFilePath)) File.Delete(GameBoosterStateFilePath); } catch { }
                }
                string msg = "Game Booster disabled. System returned to normal mode.";
                Emit($"[RESTORE MODE] {msg}");
                WriteActionLog("System", $"OK {msg}");
                return new OperationResult(true, msg);
            }

            Emit($"[RESTORE MODE] Restoring {servicesToRestore.Count} previously running service(s)...");
            int restarted = 0;
            foreach (var srv in servicesToRestore)
            {
                token.ThrowIfCancellationRequested();
                var res = await ExecAsync("net", new[] { "start", srv }, token, timeout: TimeSpan.FromSeconds(25)).ConfigureAwait(false);
                if (res.IsSuccess) restarted++;
            }

            lock (_ioLock)
            {
                try { if (File.Exists(GameBoosterStateFilePath)) File.Delete(GameBoosterStateFilePath); } catch { }
            }

            string done = $"Restored {restarted}/{servicesToRestore.Count} service(s) to their original running state.";
            Emit($"[RESTORE MODE] {done}");
            WriteActionLog("System", $"OK {done}");
            return new OperationResult(true, done);
        }

        // ==================== ELEVATED START WITH WINDOWS (TASK SCHEDULER) ====================
        private const string AutoStartTaskName = "AionToolPro_AutoStart";

        public async Task<bool> IsStartWithWindowsEnabledAsync(CancellationToken token = default)
        {
            try
            {
                var res = await _runner.RunAsync("schtasks", new[] { "/Query", "/TN", AutoStartTaskName }, token, silent: true, timeout: TimeSpan.FromSeconds(6)).ConfigureAwait(false);
                return res.IsSuccess;
            }
            catch
            {
                return false;
            }
        }

        public async Task<OperationResult> SetStartWithWindowsAsync(bool enable, CancellationToken token = default)
        {
            if (!enable)
            {
                var del = await ExecAsync("schtasks", new[] { "/Delete", "/TN", AutoStartTaskName, "/F" }, token, silent: true).ConfigureAwait(false);
                WriteActionLog("Startup", "OK Disabled Start with Windows");
                return new OperationResult(true, "Removed AionToolPro from Windows startup.");
            }

            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                return new OperationResult(false, "Could not determine AionToolPro executable path.");

            var create = await ExecAsync("schtasks", new[]
            {
                "/Create", "/TN", AutoStartTaskName,
                "/TR", $"\"{exePath}\"",
                "/SC", "ONLOGON",
                "/RL", "HIGHEST",
                "/F"
            }, token, silent: true).ConfigureAwait(false);

            WriteActionLog("Startup", $"{(create.IsSuccess ? "OK" : "FAILED")} Enabled Start with Windows");
            return new OperationResult(create.IsSuccess, create.IsSuccess ? "AionToolPro will now start automatically with Windows (elevated, no UAC prompt)." : create.StandardError, create.ExitCode);
        }
    }
}
