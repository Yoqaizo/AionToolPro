using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using AionToolPro.Desktop.Models;

namespace AionToolPro.Desktop.Services
{
    public sealed partial class AioNetworkService
    {
        // ==================== CUSTOM DNS PROFILES (IN-MEMORY CACHED) ====================
        private List<CustomDnsProfile>? _customDnsCache;
        private List<DnsProviderModel>? _allProvidersCache;

        public List<CustomDnsProfile> GetCustomDnsProfiles()
        {
            lock (_ioLock)
            {
                if (_customDnsCache != null)
                    return new List<CustomDnsProfile>(_customDnsCache);

                try
                {
                    if (!File.Exists(CustomDnsFilePath))
                    {
                        _customDnsCache = new List<CustomDnsProfile>();
                    }
                    else
                    {
                        _customDnsCache = JsonSerializer.Deserialize<List<CustomDnsProfile>>(File.ReadAllText(CustomDnsFilePath)) ?? new List<CustomDnsProfile>();
                    }
                }
                catch
                {
                    _customDnsCache = new List<CustomDnsProfile>();
                }
                return new List<CustomDnsProfile>(_customDnsCache);
            }
        }

        private void SaveCustomDnsProfilesList(List<CustomDnsProfile> profiles)
        {
            lock (_ioLock)
            {
                _customDnsCache = new List<CustomDnsProfile>(profiles);
                _allProvidersCache = null;
                try
                {
                    Directory.CreateDirectory(_cfgDir);
                    File.WriteAllText(CustomDnsFilePath, JsonSerializer.Serialize(profiles, JsonOpts), new UTF8Encoding(false));
                }
                catch { }
            }
        }

        public OperationResult SaveCustomDnsProfile(string name, string primaryIp, string secondaryIp)
        {
            name = (name ?? string.Empty).Trim();
            primaryIp = (primaryIp ?? string.Empty).Trim();
            secondaryIp = (secondaryIp ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(name) || name.Length > 40)
                return new OperationResult(false, "Please enter a profile name (1–40 characters).");
            if (!IsSafeDnsIpv4(primaryIp))
                return new OperationResult(false, "Primary DNS is not a valid IPv4 address.");
            if (!string.IsNullOrEmpty(secondaryIp) && !IsSafeDnsIpv4(secondaryIp))
                return new OperationResult(false, "Secondary DNS is not a valid IPv4 address.");

            var list = GetCustomDnsProfiles();
            list.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            list.Add(new CustomDnsProfile(name, primaryIp, secondaryIp, "Saved custom DNS profile"));
            SaveCustomDnsProfilesList(list);
            WriteActionLog("CustomDNS", $"OK Saved custom DNS '{name}' ({primaryIp})");
            return new OperationResult(true, $"Saved custom DNS profile '{name}'.");
        }

        public OperationResult DeleteCustomDnsProfile(string name)
        {
            var list = GetCustomDnsProfiles();
            int removed = list.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (removed == 0)
                return new OperationResult(false, $"Custom DNS profile '{name}' was not found (built-in providers cannot be deleted).");

            SaveCustomDnsProfilesList(list);
            WriteActionLog("CustomDNS", $"OK Deleted custom DNS '{name}'");
            return new OperationResult(true, $"Deleted custom DNS profile '{name}'.");
        }

        public List<DnsProviderModel> GetAllDnsProviders()
        {
            lock (_ioLock)
            {
                if (_allProvidersCache != null)
                    return _allProvidersCache;

                var custom = GetCustomDnsProfiles();
                var all = new List<DnsProviderModel>(AioCatalog.DnsProviders.Count + custom.Count);
                all.AddRange(AioCatalog.DnsProviders);
                int nextIndex = all.Count + 1;
                foreach (var c in custom)
                {
                    all.Add(new DnsProviderModel(
                        nextIndex++,
                        c.Name,
                        c.PrimaryIp,
                        string.IsNullOrWhiteSpace(c.SecondaryIp) ? c.PrimaryIp : c.SecondaryIp,
                        DnsGroupColor.OrangeAntiFilter,
                        string.IsNullOrWhiteSpace(c.Description) ? "Saved custom DNS profile" : c.Description));
                }
                _allProvidersCache = all;
                return all;
            }
        }

        // ==================== DNS CONFIGURATION (WITH IPV6 LEAK/STICKING FIX) ====================
        public async Task<OperationResult> ApplyDnsAsync(
            string interfaceName,
            string primaryDns,
            string? secondaryDns,
            string providerLabel,
            string? primaryIpv6 = null,
            string? secondaryIpv6 = null,
            CancellationToken token = default)
        {
            interfaceName = ValidateInterfaceName(interfaceName);
            if (!IsSafeDnsIpv4(primaryDns))
                return new OperationResult(false, $"Invalid or unsafe primary IPv4 DNS address: {primaryDns}", -1);

            if (!string.IsNullOrWhiteSpace(secondaryDns) && !IsSafeDnsIpv4(secondaryDns))
                return new OperationResult(false, $"Invalid or unsafe secondary IPv4 DNS address: {secondaryDns}", -1);

            if (!File.Exists(DnsOriginalBackupFilePath))
            {
                try { await BackupDnsAsync(interfaceName, saveAsOriginalOnly: true, token: token).ConfigureAwait(false); } catch { }
            }

            var res1 = await ExecAsync("netsh", new[]
            {
                "interface", "ip", "set", "dnsservers",
                $"name={interfaceName}", "source=static", $"address={primaryDns}", "validate=no"
            }, token).ConfigureAwait(false);

            if (!res1.IsSuccess)
            {
                string err = string.IsNullOrWhiteSpace(res1.StandardError) ? res1.StandardOutput : res1.StandardError;
                WriteActionLog(interfaceName, $"FAILED Set DNS {providerLabel} {primaryDns} (exit={res1.ExitCode})");
                return new OperationResult(false, $"Failed to apply primary DNS ({primaryDns}): {err}", res1.ExitCode);
            }

            if (!string.IsNullOrWhiteSpace(secondaryDns) && !secondaryDns.Equals(primaryDns, StringComparison.OrdinalIgnoreCase))
            {
                var res2 = await ExecAsync("netsh", new[]
                {
                    "interface", "ip", "add", "dnsservers",
                    $"name={interfaceName}", $"address={secondaryDns}", "index=2", "validate=no"
                }, token).ConfigureAwait(false);

                if (!res2.IsSuccess)
                {
                    Emit($"[DNS WARN] Primary DNS applied, but secondary DNS ({secondaryDns}) returned code {res2.ExitCode}.");
                }
            }

            // FIX: Apply matching IPv6 DNS if provider supplies it; otherwise reset IPv6 DNS to DHCP
            // so switching from Cloudflare/Google to IPv4-only anti-sanction DNS (Shecan/Electro) never leaves stale static IPv6 DNS behind!
            if (!string.IsNullOrWhiteSpace(primaryIpv6) && IPAddress.TryParse(primaryIpv6, out _))
            {
                await ExecAsync("netsh", new[]
                {
                    "interface", "ipv6", "set", "dnsservers",
                    $"name={interfaceName}", "source=static", $"address={primaryIpv6}", "validate=no"
                }, token, silent: true).ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(secondaryIpv6) && IPAddress.TryParse(secondaryIpv6, out _))
                {
                    await ExecAsync("netsh", new[]
                    {
                        "interface", "ipv6", "add", "dnsservers",
                        $"name={interfaceName}", $"address={secondaryIpv6}", "index=2", "validate=no"
                    }, token, silent: true).ConfigureAwait(false);
                }
            }
            else
            {
                await ExecAsync("netsh", new[]
                {
                    "interface", "ipv6", "set", "dnsservers",
                    $"name={interfaceName}", "source=dhcp"
                }, token, silent: true).ConfigureAwait(false);
            }

            await ExecAsync("ipconfig", new[] { "/flushdns" }, token, silent: true).ConfigureAwait(false);
            ClearDnsResolveCache();

            long? verifyMs = await QueryDnsUdpLatencyMsAsync(primaryDns, "google.com", 3000, token).ConfigureAwait(false);
            string verifyNote = verifyMs.HasValue
                ? $"Verified resolution in {verifyMs.Value} ms."
                : "Note: resolver did not answer UDP/53 verification within 3s (may be filtered on this ISP).";

            Emit($"[DNS] Applied {providerLabel} ({primaryDns}{(string.IsNullOrWhiteSpace(secondaryDns) ? "" : ", " + secondaryDns)}). {verifyNote}");
            WriteActionLog(interfaceName, $"OK Set DNS {providerLabel} {primaryDns} {secondaryDns ?? ""}");
            return new OperationResult(true, verifyNote, 0);
        }

        public async Task<string> ScanCurrentDnsAsync(string interfaceName, CancellationToken token = default)
        {
            interfaceName = ValidateInterfaceName(interfaceName);
            var sb = new StringBuilder();
            sb.AppendLine($"=== IPv4 DNS Configuration — {interfaceName} ===");
            var v4 = await ExecAsync("netsh", new[] { "interface", "ip", "show", "dnsservers", $"name={interfaceName}" }, token).ConfigureAwait(false);
            sb.AppendLine(v4.StandardOutput);
            sb.AppendLine($"=== IPv6 DNS Configuration — {interfaceName} ===");
            var v6 = await ExecAsync("netsh", new[] { "interface", "ipv6", "show", "dnsservers", $"name={interfaceName}" }, token).ConfigureAwait(false);
            sb.AppendLine(v6.StandardOutput);
            return sb.ToString();
        }

        public async Task<OperationResult> ResetDnsToDhcpAsync(string interfaceName, CancellationToken token = default)
        {
            interfaceName = ValidateInterfaceName(interfaceName);
            var r1 = await ExecAsync("netsh", new[] { "interface", "ip", "set", "dnsservers", $"name={interfaceName}", "source=dhcp" }, token).ConfigureAwait(false);
            await ExecAsync("netsh", new[] { "interface", "ipv6", "set", "dnsservers", $"name={interfaceName}", "source=dhcp" }, token, silent: true).ConfigureAwait(false);
            await ExecAsync("ipconfig", new[] { "/flushdns" }, token, silent: true).ConfigureAwait(false);
            ClearDnsResolveCache();
            await ExecAsync("ipconfig", new[] { "/registerdns" }, token, silent: true).ConfigureAwait(false);
            WriteActionLog(interfaceName, $"{(r1.IsSuccess ? "OK" : "FAILED")} Reset DNS to automatic DHCP (exit={r1.ExitCode})");
            return new OperationResult(r1.IsSuccess, r1.IsSuccess ? "DNS reset to automatic DHCP." : r1.StandardOutput, r1.ExitCode);
        }

        public async Task<OperationResult> FlushDnsCacheAsync(string interfaceName, CancellationToken token = default)
        {
            var res = await ExecAsync("ipconfig", new[] { "/flushdns" }, token).ConfigureAwait(false);
            ClearDnsResolveCache();
            WriteActionLog(interfaceName, $"{(res.IsSuccess ? "OK" : "FAILED")} Flushed DNS cache");
            return new OperationResult(res.IsSuccess, res.StandardOutput, res.ExitCode);
        }

        public async Task<OperationResult> FullAdapterResetProtocolAsync(string interfaceName)
        {
            interfaceName = ValidateInterfaceName(interfaceName);
            using var lease = CreateTrackedOperationCts(TimeSpan.FromMinutes(3));
            var token = lease.Token;

            try
            {
                await BackupDnsAsync(interfaceName, saveAsOriginalOnly: false, token: token).ConfigureAwait(false);

                var steps = new (string Exe, string[] Args, int DelayMs)[]
                {
                    ("ipconfig", new[] { "/flushdns" }, 0),
                    ("netsh", new[] { "int", "ip", "reset" }, 0),
                    ("netsh", new[] { "interface", "set", "interface", $"name={interfaceName}", "admin=disabled" }, 2000),
                    ("netsh", new[] { "interface", "set", "interface", $"name={interfaceName}", "admin=enabled" }, 2000),
                    ("ipconfig", new[] { "/release", interfaceName }, 0),
                    ("ipconfig", new[] { "/renew", interfaceName }, 0),
                    ("ipconfig", new[] { "/registerdns" }, 0),
                    ("nbtstat", new[] { "-R" }, 0),
                    ("nbtstat", new[] { "-RR" }, 0)
                };

                foreach (var (exe, args, delayMs) in steps)
                {
                    token.ThrowIfCancellationRequested();
                    var res = await ExecAsync(exe, args, token).ConfigureAwait(false);
                    if (res.WasCancelled)
                    {
                        WriteActionLog(interfaceName, "CANCELLED Full adapter reset");
                        return new OperationResult(false, "Adapter reset cancelled by user.", -1);
                    }
                    if (delayMs > 0)
                        await Task.Delay(delayMs, token).ConfigureAwait(false);
                }

                WriteActionLog(interfaceName, "OK Full adapter reset completed (reboot recommended)");
                return new OperationResult(true, "Full adapter reset completed. Restart Windows to finalize TCP/IP stack reset.");
            }
            catch (OperationCanceledException)
            {
                try { await ExecAsync("netsh", new[] { "interface", "set", "interface", $"name={interfaceName}", "admin=enabled" }, CancellationToken.None, silent: true).ConfigureAwait(false); } catch { }
                WriteActionLog(interfaceName, "CANCELLED Full adapter reset");
                return new OperationResult(false, "Adapter reset was cancelled.", -1);
            }
        }

        public async Task<List<DnsBenchmarkRow>> BenchmarkAllDnsAsync()
        {
            using var lease = CreateTrackedOperationCts(TimeSpan.FromSeconds(45));
            var token = lease.Token;

            var providers = GetAllDnsProviders();
            Emit($"[DNS BENCH] Sending real UDP/53 DNS queries (2 rounds x 2 domains) to {providers.Count} providers...");
            string[] testDomains = { "google.com", "cloudflare.com" };

            using var throttler = new SemaphoreSlim(8);
            var tasks = providers.Select(async provider =>
            {
                await throttler.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    bool isPrivate = AioCatalog.IsPrivateIpv4(provider.PrimaryIp);
                    var samples = new List<long>();
                    int totalQueries = 0;

                    for (int round = 0; round < 2; round++)
                    {
                        foreach (var dom in testDomains)
                        {
                            token.ThrowIfCancellationRequested();
                            totalQueries++;
                            long? ms = await QueryDnsUdpLatencyMsAsync(provider.PrimaryIp, dom, 1400, token).ConfigureAwait(false);
                            if (ms.HasValue) samples.Add(ms.Value);
                        }
                    }

                    if (samples.Count == 0)
                    {
                        string status = isPrivate ? "Unreachable (ISP-only)" : "Timeout / Blocked";
                        return new DnsBenchmarkRow(provider.Index, provider.Name, provider.PrimaryIp, provider.SecondaryIp, provider.CategoryLabel, null, status, isPrivate);
                    }

                    samples.Sort();
                    long median = samples[samples.Count / 2];
                    int failed = totalQueries - samples.Count;
                    string okStatus = failed > 0
                        ? $"{median} ms ({failed}/{totalQueries} failed)"
                        : (isPrivate ? $"{median} ms (ISP-routed)" : $"{median} ms");

                    return new DnsBenchmarkRow(provider.Index, provider.Name, provider.PrimaryIp, provider.SecondaryIp, provider.CategoryLabel, median, okStatus, isPrivate);
                }
                catch (OperationCanceledException)
                {
                    return new DnsBenchmarkRow(provider.Index, provider.Name, provider.PrimaryIp, provider.SecondaryIp, provider.CategoryLabel, null, "Cancelled", AioCatalog.IsPrivateIpv4(provider.PrimaryIp));
                }
                finally
                {
                    throttler.Release();
                }
            });

            var rows = await Task.WhenAll(tasks).ConfigureAwait(false);
            return rows.OrderBy(r => r.SortKey).ToList();
        }

        // ==================== DNS BACKUP / RESTORE ====================
        private sealed record DnsBackupModel(
            string Mode,
            string Adapter,
            string? Dns1,
            string? Dns2,
            string SavedAt);

        public Task<string> BackupDnsAsync(string interfaceName, bool saveAsOriginalOnly = false, CancellationToken token = default)
        {
            return Task.Run(() =>
            {
                interfaceName = ValidateInterfaceName(interfaceName);
                Directory.CreateDirectory(_cfgDir);

                var nic = NetworkInterface.GetAllNetworkInterfaces()
                    .FirstOrDefault(n => n.Name.Equals(interfaceName, StringComparison.OrdinalIgnoreCase));

                bool isDhcp = true;
                string? d1 = null;
                string? d2 = null;

                if (nic != null)
                {
                    try
                    {
                        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{nic.Id}", false);
                        string? staticNs = key?.GetValue("NameServer") as string;
                        if (!string.IsNullOrWhiteSpace(staticNs))
                        {
                            isDhcp = false;
                            var ips = staticNs.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                                .Where(ValidateIpv4)
                                .ToList();
                            if (ips.Count > 0) d1 = ips[0];
                            if (ips.Count > 1) d2 = ips[1];
                        }
                    }
                    catch { }

                    if (d1 == null)
                    {
                        var activeDns = nic.GetIPProperties().DnsAddresses
                            .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                            .Select(a => a.ToString())
                            .ToList();
                        if (activeDns.Count > 0) d1 = activeDns[0];
                        if (activeDns.Count > 1) d2 = activeDns[1];
                    }
                }

                var model = new DnsBackupModel(
                    isDhcp ? "dhcp" : "static",
                    interfaceName,
                    d1,
                    d2,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

                string json = JsonSerializer.Serialize(model, JsonOpts);
                lock (_ioLock)
                {
                    if (!File.Exists(DnsOriginalBackupFilePath))
                    {
                        File.WriteAllText(DnsOriginalBackupFilePath, json, new UTF8Encoding(false));
                    }
                    if (!saveAsOriginalOnly)
                    {
                        File.WriteAllText(DnsBackupFilePath, json, new UTF8Encoding(false));
                        string stamped = Path.Combine(_cfgDir, $"dns_backup.{DateTime.Now:yyyyMMdd_HHmmss}.json");
                        File.WriteAllText(stamped, json, new UTF8Encoding(false));
                    }
                }

                if (!saveAsOriginalOnly)
                    WriteActionLog(interfaceName, $"OK Backed up DNS ({model.Mode}) {d1 ?? "-"} {d2 ?? ""}");

                return $"Mode: {model.Mode}\r\nAdapter: {model.Adapter}\r\nPrimary: {model.Dns1 ?? "Automatic"}\r\nSecondary: {model.Dns2 ?? "-"}\r\nDate: {model.SavedAt}";
            }, token);
        }

        public string ReadSavedDnsBackup()
        {
            lock (_ioLock)
            {
                string target = File.Exists(DnsBackupFilePath)
                    ? DnsBackupFilePath
                    : (File.Exists(DnsOriginalBackupFilePath) ? DnsOriginalBackupFilePath : string.Empty);

                if (string.IsNullOrEmpty(target))
                    return "No DNS backup saved yet.";

                return File.ReadAllText(target);
            }
        }

        public async Task<OperationResult> RestoreDnsBackupAsync(string interfaceName, CancellationToken token = default)
        {
            interfaceName = ValidateInterfaceName(interfaceName);
            string target;
            lock (_ioLock)
            {
                target = File.Exists(DnsBackupFilePath)
                    ? DnsBackupFilePath
                    : (File.Exists(DnsOriginalBackupFilePath) ? DnsOriginalBackupFilePath : string.Empty);
            }

            if (string.IsNullOrEmpty(target))
                return new OperationResult(false, "No DNS backup file found.");

            DnsBackupModel? model;
            try
            {
                string json = await File.ReadAllTextAsync(target, token).ConfigureAwait(false);
                model = JsonSerializer.Deserialize<DnsBackupModel>(json);
            }
            catch (Exception ex)
            {
                return new OperationResult(false, $"Failed to read DNS backup: {ex.Message}");
            }

            if (model == null)
                return new OperationResult(false, "DNS backup file is empty or invalid.");

            if (model.Mode.Equals("dhcp", StringComparison.OrdinalIgnoreCase))
            {
                var r = await ResetDnsToDhcpAsync(interfaceName, token).ConfigureAwait(false);
                WriteActionLog(interfaceName, "OK Restored DNS backup (DHCP)");
                return r;
            }

            if (!string.IsNullOrWhiteSpace(model.Dns1) && IsSafeDnsIpv4(model.Dns1))
            {
                var r = await ApplyDnsAsync(interfaceName, model.Dns1, model.Dns2, "RestoredBackup", token: token).ConfigureAwait(false);
                WriteActionLog(interfaceName, $"Restored DNS backup ({model.Dns1})");
                return r;
            }

            return new OperationResult(false, "DNS backup did not contain a valid IPv4 DNS address.");
        }

        // ==================== DoH ====================
        public async Task<OperationResult> EnableDnsOverHttpsAsync(string interfaceName, DnsProviderModel provider, CancellationToken token = default)
        {
            interfaceName = ValidateInterfaceName(interfaceName);

            if (string.IsNullOrWhiteSpace(provider.DohTemplateUrl))
                return new OperationResult(false, $"{provider.Name} does not have a DoH template URL configured.");

            if (Environment.OSVersion.Version.Build < 22000)
            {
                string msg = $"Encrypted DoH via netsh requires Windows 11 (Build 22000+). Detected build: {Environment.OSVersion.Version.Build}.";
                Emit($"[DoH ERR] {msg}");
                return new OperationResult(false, msg, -1);
            }

            var r1 = await ExecAsync("netsh", new[]
            {
                "dns", "add", "encryption",
                $"server={provider.PrimaryIp}",
                $"dohtemplate={provider.DohTemplateUrl}",
                "autoupgrade=yes",
                "udpfallback=yes"
            }, token).ConfigureAwait(false);

            var r2 = await ExecAsync("netsh", new[]
            {
                "dns", "add", "encryption",
                $"server={provider.SecondaryIp}",
                $"dohtemplate={provider.DohTemplateUrl}",
                "autoupgrade=yes",
                "udpfallback=yes"
            }, token).ConfigureAwait(false);

            if (!r1.IsSuccess && !r2.IsSuccess)
            {
                WriteActionLog(interfaceName, $"FAILED DoH {provider.Name} (exit={r1.ExitCode})");
                return new OperationResult(false, $"Failed to register DoH encryption rule for {provider.Name}: {r1.StandardOutput}", r1.ExitCode);
            }

            TrackManagedDohServers(new[] { provider.PrimaryIp, provider.SecondaryIp });

            var applyRes = await ApplyDnsAsync(
                interfaceName,
                provider.PrimaryIp,
                provider.SecondaryIp,
                $"DoH-{provider.Name}",
                provider.PrimaryIpv6,
                provider.SecondaryIpv6,
                token).ConfigureAwait(false);

            WriteActionLog(interfaceName, $"{(applyRes.Success ? "OK" : "FAILED")} DoH enabled {provider.Name} (udpfallback=yes)");
            return applyRes;
        }

        private void TrackManagedDohServers(IEnumerable<string> ips)
        {
            lock (_ioLock)
            {
                try
                {
                    Directory.CreateDirectory(_cfgDir);
                    var existing = File.Exists(DohStateFilePath)
                        ? JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(DohStateFilePath)) ?? new HashSet<string>()
                        : new HashSet<string>();

                    foreach (var ip in ips.Where(ValidateIpv4))
                        existing.Add(ip);

                    File.WriteAllText(DohStateFilePath, JsonSerializer.Serialize(existing, JsonOpts), new UTF8Encoding(false));
                }
                catch { }
            }
        }

        public async Task<string> ShowDohStatusAsync(CancellationToken token = default)
        {
            if (Environment.OSVersion.Version.Build < 22000)
                return $"Windows 11 (Build 22000+) is required for native netsh DoH management. Current build: {Environment.OSVersion.Version.Build}.";

            var res = await ExecAsync("netsh", new[] { "dns", "show", "encryption" }, token).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(res.StandardOutput) ? res.StandardError : res.StandardOutput;
        }

        public async Task<OperationResult> RemoveManagedDohSettingsAsync(string interfaceName, CancellationToken token = default)
        {
            if (Environment.OSVersion.Version.Build < 22000)
                return new OperationResult(false, "Native DoH rules require Windows 11 (Build 22000+).");

            HashSet<string> managedServers;
            lock (_ioLock)
            {
                managedServers = File.Exists(DohStateFilePath)
                    ? JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(DohStateFilePath)) ?? new HashSet<string>()
                    : new HashSet<string>();
            }

            if (managedServers.Count == 0)
            {
                Emit("[DoH] No DoH rules created by AionToolPro were found. Your manual/corporate DoH rules were left untouched.");
                return new OperationResult(true, "No AionToolPro-managed DoH rules to remove.");
            }

            int removed = 0;
            foreach (var ip in managedServers)
            {
                var r = await ExecAsync("netsh", new[] { "dns", "delete", "encryption", $"server={ip}" }, token, silent: true).ConfigureAwait(false);
                if (r.IsSuccess) removed++;
            }

            lock (_ioLock)
            {
                try { if (File.Exists(DohStateFilePath)) File.Delete(DohStateFilePath); } catch { }
            }

            Emit($"[DoH] Removed {removed} DoH encryption rule(s) previously added by AionToolPro.");
            WriteActionLog(interfaceName, $"OK Removed {removed} managed DoH rule(s)");
            return new OperationResult(true, $"Removed {removed} managed DoH rule(s).");
        }
    }
}
