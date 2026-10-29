using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using Microsoft.Win32;
using AionToolPro.Desktop.Models;

namespace AionToolPro.Desktop.Services
{
    public sealed class AioNetworkService
    {
        private readonly string _cfgDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AionToolPro");
        private readonly string _hostsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), @"drivers\etc\hosts");
        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };

        public string ConfigFilePath => Path.Combine(_cfgDir, "settings.cfg");
        public string LogFilePath => Path.Combine(_cfgDir, "actions.log");
        public string DnsBackupFilePath => Path.Combine(_cfgDir, "dns_backup.txt");
        public string HostsBackupFilePath => Path.Combine(_cfgDir, "hosts.backup");
        public string HostsPath => _hostsPath;

        public bool EnableActionLog { get; set; } = true;
        public bool RememberSettings { get; set; } = true;

        public event Action<string>? CommandOutputReceived;

        public static bool ValidateIpv4(string? input)
        {
            if (string.IsNullOrWhiteSpace(input)) return false;
            var cleaned = input.Replace("\"", "").Trim();
            var parts = cleaned.Split('.');
            if (parts.Length != 4) return false;
            foreach (var part in parts)
            {
                if (part.Length == 0 || (part.Length > 1 && part.StartsWith('0')))
                    return false;
                if (!int.TryParse(part, out int octet) || octet < 0 || octet > 255)
                    return false;
            }
            return string.Join(".", parts) == cleaned;
        }

        public static bool ValidateHostOrIp(string? input)
        {
            if (string.IsNullOrWhiteSpace(input)) return false;
            var cleaned = input.Replace("\"", "").Trim();
            if (cleaned.StartsWith('.') || cleaned.EndsWith('.') || cleaned.Contains(".."))
                return false;
            return Regex.IsMatch(cleaned, @"^[a-zA-Z0-9.-]+$");
        }

        // ---- Real Hardware & Active System Adaptor Scanner ----
        // Filters out fake/virtual miniports and assigns Green (Connected), Orange (Enabled/Disconnected), Red (Disabled)
        private static readonly string[] IgnoredAdapterKeywords =
        {
            "Loopback", "vEthernet", "WSL", "Hyper-V", "VMware", "VirtualBox",
            "Teredo", "ISATAP", "6to4", "Bluetooth", "Miniport", "Filter", "Kernel Debug"
        };

        public List<NetworkAdaptorItem> ScanSystemAdaptors()
        {
            var results = new List<NetworkAdaptorItem>();
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = "interface show interface",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    string output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(3000);

                    var lines = output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var rawLine in lines.Skip(2))
                    {
                        var line = rawLine.Trim();
                        if (string.IsNullOrEmpty(line) || line.StartsWith("---"))
                            continue;

                        // Format: Admin State    State          Type             Interface Name
                        var m = Regex.Match(line, @"^(Enabled|Disabled)\s+(Connected|Disconnected)\s+\S+\s+(.+)$", RegexOptions.IgnoreCase);
                        if (!m.Success) continue;

                        string adminState = m.Groups[1].Value;
                        string connState  = m.Groups[2].Value;
                        string ifaceName  = m.Groups[3].Value.Trim();

                        if (IgnoredAdapterKeywords.Any(k => ifaceName.Contains(k, StringComparison.OrdinalIgnoreCase)))
                            continue;

                        AdaptorHealthState health =
                            adminState.Equals("Disabled", StringComparison.OrdinalIgnoreCase)
                                ? AdaptorHealthState.DisabledRed
                                : connState.Equals("Connected", StringComparison.OrdinalIgnoreCase)
                                    ? AdaptorHealthState.ConnectedGreen
                                    : AdaptorHealthState.DisconnectedOrange;

                        results.Add(new NetworkAdaptorItem
                        {
                            Name = ifaceName,
                            AdminState = adminState,
                            ConnectionState = connState,
                            HealthState = health
                        });
                    }
                }
            }
            catch
            {
                // Fallback below if netsh fails
            }

            // Fallback to .NET NetworkInterface if netsh returned nothing
            if (results.Count == 0)
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                        nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                        continue;

                    if (IgnoredAdapterKeywords.Any(k => nic.Name.Contains(k, StringComparison.OrdinalIgnoreCase) ||
                                                        nic.Description.Contains(k, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    bool isUp = nic.OperationalStatus == OperationalStatus.Up;
                    results.Add(new NetworkAdaptorItem
                    {
                        Name = nic.Name,
                        AdminState = "Enabled",
                        ConnectionState = isUp ? "Connected" : "Disconnected",
                        HealthState = isUp ? AdaptorHealthState.ConnectedGreen : AdaptorHealthState.DisconnectedOrange
                    });
                }
            }

            return results.OrderBy(a => (int)a.HealthState).ThenBy(a => a.Name).ToList();
        }

        public string DetectRouterGatewayIp(string preferredInterfaceName)
        {
            try
            {
                var allNics = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    .ToList();

                var selectedNic = allNics.FirstOrDefault(n => n.Name.Equals(preferredInterfaceName, StringComparison.OrdinalIgnoreCase));
                if (selectedNic != null)
                {
                    var gw = selectedNic.GetIPProperties().GatewayAddresses
                        .FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.ToString().Equals("0.0.0.0"));
                    if (gw != null)
                        return gw.Address.ToString();
                }

                foreach (var nic in allNics.Where(n => n.OperationalStatus == OperationalStatus.Up))
                {
                    var gw = nic.GetIPProperties().GatewayAddresses
                        .FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.ToString().Equals("0.0.0.0"));
                    if (gw != null)
                        return gw.Address.ToString();
                }
            }
            catch { }
            return string.Empty;
        }

        public async Task<long> PingSingleHostFastAsync(string hostOrIp, int timeoutMs = 950)
        {
            if (string.IsNullOrWhiteSpace(hostOrIp)) return 9999;
            try
            {
                using var pinger = new Ping();
                var reply = await pinger.SendPingAsync(hostOrIp, timeoutMs);
                if (reply.Status == IPStatus.Success)
                    return reply.RoundtripTime;
            }
            catch { }
            return 9999;
        }

        public async Task<bool> ApplyDnsAsync(string interfaceName, string primaryDns, string? secondaryDns, string providerLabel)
        {
            if (!ValidateIpv4(primaryDns)) throw new ArgumentException("Invalid primary IPv4 address.");
            int fails = 0;
            if (await RunProcessAsync("netsh", $"interface ip set dnsservers name=\"{interfaceName}\" source=static address={primaryDns} validate=no") != 0) fails++;
            if (!string.IsNullOrWhiteSpace(secondaryDns) && ValidateIpv4(secondaryDns))
            {
                if (await RunProcessAsync("netsh", $"interface ip add dnsservers name=\"{interfaceName}\" address={secondaryDns} index=2 validate=no") != 0) fails++;
            }
            await RunProcessAsync("ipconfig", "/flushdns");
            WriteActionLog(interfaceName, $"Set DNS {providerLabel} {primaryDns} {secondaryDns}");
            return fails == 0;
        }

        public async Task<string> ScanCurrentDnsAsync(string interfaceName)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== IPv4 DNS Configuration - {interfaceName} ===");
            sb.AppendLine(await RunProcessCaptureAsync("netsh", $"interface ip show dnsservers name=\"{interfaceName}\""));
            sb.AppendLine($"=== IPv6 DNS Configuration - {interfaceName} ===");
            sb.AppendLine(await RunProcessCaptureAsync("netsh", $"interface ipv6 show dnsservers name=\"{interfaceName}\""));
            return sb.ToString();
        }

        public async Task ResetDnsToDhcpAsync(string interfaceName)
        {
            await RunProcessAsync("netsh", $"interface ip set dnsservers name=\"{interfaceName}\" source=dhcp");
            await RunProcessAsync("netsh", $"interface ipv6 set dnsservers name=\"{interfaceName}\" source=dhcp");
            await RunProcessAsync("ipconfig", "/flushdns");
            await RunProcessAsync("ipconfig", $"/renew \"{interfaceName}\"");
            await RunProcessAsync("ipconfig", "/registerdns");
            WriteActionLog(interfaceName, "Reset DNS to automatic DHCP");
        }

        public async Task FlushDnsCacheAsync(string interfaceName)
        {
            await RunProcessAsync("ipconfig", "/flushdns");
            WriteActionLog(interfaceName, "Flushed DNS cache");
        }

        public async Task FullAdapterResetProtocolAsync(string interfaceName)
        {
            await RunProcessAsync("ipconfig", "/flushdns");
            await RunProcessAsync("netsh", "int ip reset");
            await RunProcessAsync("netsh", $"interface set interface name=\"{interfaceName}\" admin=disabled");
            await Task.Delay(2000);
            await RunProcessAsync("netsh", $"interface set interface name=\"{interfaceName}\" admin=enabled");
            await Task.Delay(2000);
            await RunProcessAsync("ipconfig", $"/release \"{interfaceName}\"");
            await RunProcessAsync("ipconfig", $"/renew \"{interfaceName}\"");
            await RunProcessAsync("ipconfig", $"/release6 \"{interfaceName}\"");
            await RunProcessAsync("ipconfig", $"/renew6 \"{interfaceName}\"");
            await RunProcessAsync("ipconfig", "/registerdns");
            await RunProcessAsync("nbtstat", "-R");
            await RunProcessAsync("nbtstat", "-RR");
            WriteActionLog(interfaceName, "Reset adapter full");
        }

        public async Task<List<DnsBenchmarkRow>> BenchmarkAllDnsAsync()
        {
            var tasks = AioCatalog.DnsProviders.Select(async provider =>
            {
                long avg = await PingAverageMsAsync(provider.PrimaryIp, count: 2, timeoutMs: 1000);
                string status = avg >= 9999 ? "Timeout" : $"{avg} ms";
                return new DnsBenchmarkRow(provider.Index, provider.Name, provider.PrimaryIp, provider.SecondaryIp, provider.CategoryLabel, avg, status);
            });
            var rows = await Task.WhenAll(tasks);
            return rows.OrderBy(r => r.PingMs).ToList();
        }

        public async Task<long> PingAverageMsAsync(string host, int count = 2, int timeoutMs = 1000)
        {
            using var pinger = new Ping();
            long sum = 0;
            int ok = 0;
            for (int i = 0; i < count; i++)
            {
                try
                {
                    var reply = await pinger.SendPingAsync(host, timeoutMs);
                    if (reply.Status == IPStatus.Success)
                    {
                        sum += reply.RoundtripTime;
                        ok++;
                    }
                }
                catch { }
            }
            return ok == 0 ? 9999 : sum / ok;
        }

        public async Task<string> BackupDnsAsync(string interfaceName)
        {
            Directory.CreateDirectory(_cfgDir);
            string output = await RunProcessCaptureAsync("netsh", $"interface ip show dnsservers name=\"{interfaceName}\"");
            bool isDhcp = output.Contains("DHCP", StringComparison.OrdinalIgnoreCase);
            string mode = isDhcp ? "dhcp" : "static";

            var matches = Regex.Matches(output, @"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b");
            string d1 = matches.Count > 0 ? matches[0].Value : "";
            string d2 = matches.Count > 1 ? matches[1].Value : "";

            var lines = new[] { $"mode={mode}", $"adapter={interfaceName}", $"dns1={d1}", $"dns2={d2}", $"date={DateTime.Now:yyyy-MM-dd HH:mm:ss}" };
            await File.WriteAllLinesAsync(DnsBackupFilePath, lines);
            WriteActionLog(interfaceName, $"Backed up DNS {mode} {d1} {d2}");
            return string.Join(Environment.NewLine, lines);
        }

        public string ReadSavedDnsBackup()
        {
            if (!File.Exists(DnsBackupFilePath)) return "No backup saved yet.";
            return File.ReadAllText(DnsBackupFilePath);
        }

        public async Task<bool> RestoreDnsBackupAsync(string interfaceName)
        {
            if (!File.Exists(DnsBackupFilePath)) return false;
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in await File.ReadAllLinesAsync(DnsBackupFilePath))
            {
                var idx = line.IndexOf('=');
                if (idx > 0) dict[line[..idx].Trim()] = line[(idx + 1)..].Trim();
            }
            if (dict.TryGetValue("mode", out var mode) && mode.Equals("dhcp", StringComparison.OrdinalIgnoreCase))
            {
                await ResetDnsToDhcpAsync(interfaceName);
                return true;
            }
            if (dict.TryGetValue("dns1", out var dns1) && ValidateIpv4(dns1))
            {
                dict.TryGetValue("dns2", out var dns2);
                await ApplyDnsAsync(interfaceName, dns1, dns2, "RestoredBackup");
                WriteActionLog(interfaceName, "Restored DNS backup");
                return true;
            }
            return false;
        }

        public async Task EnableDnsOverHttpsAsync(string interfaceName, string primary, string secondary, string dohUrl, string name)
        {
            await RunProcessAsync("netsh", $"dns add encryption server={primary} dohtemplate={dohUrl} autoupgrade=yes udpfallback=no");
            await RunProcessAsync("netsh", $"dns add encryption server={secondary} dohtemplate={dohUrl} autoupgrade=yes udpfallback=no");
            await ApplyDnsAsync(interfaceName, primary, secondary, $"DoH-{name}");
            WriteActionLog(interfaceName, $"DoH enabled {name}");
        }

        public Task<string> ShowDohStatusAsync() => RunProcessCaptureAsync("netsh", "dns show encryption");

        public async Task RemoveAllDohSettingsAsync(string interfaceName)
        {
            string[] servers = { "1.1.1.1", "1.0.0.1", "8.8.8.8", "8.8.4.4", "9.9.9.9", "149.112.112.112" };
            foreach (var s in servers)
                await RunProcessAsync("netsh", $"dns delete encryption server={s}");
            WriteActionLog(interfaceName, "Removed DoH settings");
        }

        public async Task<string> GetIpAndMacInfoAsync(string interfaceName)
        {
            var sb = new StringBuilder();
            sb.AppendLine(await RunProcessCaptureAsync("netsh", $"interface ip show config name=\"{interfaceName}\""));
            sb.AppendLine("=== Physical MAC Address ===");
            sb.AppendLine(await RunProcessCaptureAsync("getmac", "/v /fo list"));
            return sb.ToString();
        }

        public async Task<(string Ip, string Country, string Org)> GetPublicIpInfoAsync()
        {
            string ip = (await _http.GetStringAsync("https://ipinfo.io/ip")).Trim();
            string country = (await _http.GetStringAsync("https://ipinfo.io/country")).Trim();
            string org = (await _http.GetStringAsync("https://ipinfo.io/org")).Trim();
            return (ip, country, org);
        }

        public Task<string> RunPingAsync(string target, int count = 10) => RunProcessCaptureAsync("ping", $"-n {count} {target}");
        public Task<string> RunNslookupAsync(string domain) => RunProcessCaptureAsync("nslookup", domain);
        public Task<string> RunTracerouteAsync(string target) => RunProcessCaptureAsync("tracert", $"-d -h 20 -w 1000 {target}");

        public async Task<bool> CheckTcpPortAsync(string host, int port, int timeoutMs = 3000)
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(timeoutMs);
            try
            {
                await client.ConnectAsync(host, port, cts.Token);
                return client.Connected;
            }
            catch { return false; }
        }

        public Task<string> GetActiveTcpConnectionsReportAsync() =>
            RunProcessCaptureAsync("powershell", "-NoProfile -Command \"Get-NetTCPConnection -State Established | Select-Object LocalAddress,LocalPort,RemoteAddress,RemotePort,@{n='Process';e={(Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue).ProcessName}} | Sort-Object Process | Format-Table -AutoSize | Out-String -Width 120\"");

        public string GetAdapterDataUsageReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"{"Adaptor Name",-32} {"Status",-15} {"Received MB",-16} {"Sent MB",-16}");
            sb.AppendLine(new string('-', 82));
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
            {
                var stats = nic.GetIPStatistics();
                double rx = Math.Round(stats.BytesReceived / 1048576.0, 1);
                double tx = Math.Round(stats.BytesSent / 1048576.0, 1);
                sb.AppendLine($"{nic.Name,-32} {nic.OperationalStatus,-15} {rx,-16:N1} {tx,-16:N1}");
            }
            return sb.ToString();
        }

        public Task<string> GetWifiStatusAsync() => RunProcessCaptureAsync("netsh", "wlan show interfaces");

        public async Task<List<WifiCredentialItem>> GetSavedWifiPasswordsAsync()
        {
            var result = new List<WifiCredentialItem>();
            string profilesOutput = await RunProcessCaptureAsync("netsh", "wlan show profiles");
            var profileNames = new List<string>();

            foreach (var rawLine in profilesOutput.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (rawLine.StartsWith("    ") && !rawLine.StartsWith("     ") && rawLine.Contains(" : "))
                {
                    int colon = rawLine.IndexOf(':');
                    if (colon > 0 && colon + 1 < rawLine.Length)
                    {
                        string name = rawLine[(colon + 1)..].Trim();
                        if (!string.IsNullOrEmpty(name) && !profileNames.Contains(name))
                            profileNames.Add(name);
                    }
                }
            }

            foreach (var ssid in profileNames)
            {
                string detail = await RunProcessCaptureAsync("netsh", $"wlan show profile name=\"{ssid}\" key=clear");
                string? password = null;

                foreach (var line in detail.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (line.Contains("Key Content", StringComparison.OrdinalIgnoreCase))
                    {
                        int c = line.IndexOf(':');
                        if (c > 0 && c + 1 < line.Length) password = line[(c + 1)..].Trim();
                        break;
                    }
                }

                if (string.IsNullOrEmpty(password))
                {
                    string tempXmlDir = Path.Combine(Path.GetTempPath(), "aio_wx");
                    try
                    {
                        if (Directory.Exists(tempXmlDir)) Directory.Delete(tempXmlDir, true);
                        Directory.CreateDirectory(tempXmlDir);
                        await RunProcessAsync("netsh", $"wlan export profile name=\"{ssid}\" key=clear folder=\"{tempXmlDir}\"");
                        foreach (var xmlFile in Directory.GetFiles(tempXmlDir, "*.xml"))
                        {
                            var doc = XDocument.Load(xmlFile);
                            var keyMat = doc.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals("keyMaterial", StringComparison.OrdinalIgnoreCase));
                            if (keyMat != null && !string.IsNullOrEmpty(keyMat.Value)) { password = keyMat.Value; break; }
                        }
                    }
                    catch { }
                    finally { try { if (Directory.Exists(tempXmlDir)) Directory.Delete(tempXmlDir, true); } catch { } }
                }
                result.Add(new WifiCredentialItem(ssid, password ?? "Open Network"));
            }
            return result;
        }

        public async Task<bool> ForgetWifiNetworkAsync(string ssid, string interfaceName)
        {
            int code = await RunProcessAsync("netsh", $"wlan delete profile name=\"{ssid}\"");
            if (code == 0) WriteActionLog(interfaceName, $"Forgot Wi-Fi {ssid}");
            return code == 0;
        }

        public async Task<bool> ApplyStaticIpAsync(string interfaceName, string ip, string subnetMask, string? gateway)
        {
            if (!ValidateIpv4(ip) || !ValidateIpv4(subnetMask)) throw new ArgumentException("Invalid IPv4 address or subnet mask.");
            string args = !string.IsNullOrWhiteSpace(gateway) && ValidateIpv4(gateway)
                ? $"interface ip set address name=\"{interfaceName}\" source=static address={ip} mask={subnetMask} gateway={gateway}"
                : $"interface ip set address name=\"{interfaceName}\" source=static address={ip} mask={subnetMask}";
            int code = await RunProcessAsync("netsh", args);
            WriteActionLog(interfaceName, $"Static IP {ip} mask {subnetMask} gw {gateway}");
            return code == 0;
        }

        public async Task SwitchIpToDhcpAsync(string interfaceName)
        {
            await RunProcessAsync("netsh", $"interface ip set address name=\"{interfaceName}\" source=dhcp");
            await RunProcessAsync("ipconfig", $"/renew \"{interfaceName}\"");
            WriteActionLog(interfaceName, "IP set to DHCP");
        }

        public string GetCustomHostsEntries()
        {
            if (!File.Exists(_hostsPath)) return "hosts file not found.";
            var custom = File.ReadAllLines(_hostsPath).Select(l => l.Trim()).Where(l => !string.IsNullOrEmpty(l) && !l.StartsWith('#')).ToList();
            return custom.Count == 0 ? "No custom entries found." : string.Join(Environment.NewLine, custom);
        }

        public void OpenHostsInNotepad() => Process.Start(new ProcessStartInfo("notepad.exe", $"\"{_hostsPath}\"") { UseShellExecute = true });

        public void BackupHostsFile(string interfaceName)
        {
            Directory.CreateDirectory(_cfgDir);
            File.Copy(_hostsPath, HostsBackupFilePath, overwrite: true);
            WriteActionLog(interfaceName, "Backed up hosts file");
        }

        public async Task<bool> RestoreHostsFileAsync(string interfaceName)
        {
            if (!File.Exists(HostsBackupFilePath)) return false;
            File.Copy(HostsBackupFilePath, _hostsPath, overwrite: true);
            await RunProcessAsync("ipconfig", "/flushdns");
            WriteActionLog(interfaceName, "Restored hosts file");
            return true;
        }

        public async Task ResetHostsToDefaultAsync(string interfaceName)
        {
            BackupHostsFile(interfaceName);
            string defaultHosts = "# Copyright 1993-2009 Microsoft Corp.\r\n#\r\n# This is a sample HOSTS file used by Microsoft TCP/IP for Windows.\r\n# localhost name resolution is handled within DNS itself.\r\n#\t127.0.0.1       localhost\r\n#\t::1             localhost\r\n";
            await File.WriteAllTextAsync(_hostsPath, defaultHosts);
            await RunProcessAsync("ipconfig", "/flushdns");
            WriteActionLog(interfaceName, "Reset hosts file to default");
        }

        public async Task<int> ApplyBlocklistAsync(BlocklistDefinition list, string interfaceName)
        {
            Directory.CreateDirectory(_cfgDir);
            if (File.Exists(_hostsPath)) File.Copy(_hostsPath, Path.Combine(_cfgDir, "hosts.auto_backup"), overwrite: true);
            RemoveBlocklistByTag(list.Tag);
            var linesToWrite = new List<string>();

            if (list.IsBuiltInCustom && list.Tag == "TeamSpeak-Blocked")
            {
                linesToWrite.AddRange(new[] { "0.0.0.0 accounting.teamspeak.com", "0.0.0.0 backupaccounting.teamspeak.com", "0.0.0.0 blacklist.teamspeak.com", "0.0.0.0 blacklist2.teamspeak.com", "0.0.0.0 ipcheck.teamspeak.com", "0.0.0.0 ocsp.digicert.com", "0.0.0.0 hardy.teamspeak.4players.de", "0.0.0.0 weblist.teamspeak.com", "0.0.0.0 reportpiracy-env.elasticbeanstalk.com", "0.0.0.0 versions.teamspeak.com" });
            }
            else
            {
                string raw = await _http.GetStringAsync(list.DownloadUrl);
                var ipLineRegex = new Regex(@"^\d+\.\d+\.\d+\.\d+\s+");
                linesToWrite.AddRange(raw.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries).Where(l => ipLineRegex.IsMatch(l)));
            }
            if (linesToWrite.Count == 0) return 0;

            var sb = new StringBuilder();
            sb.AppendLine($"# AIONTOOL-BEGIN:{list.Tag}:{list.Name}:{DateTime.Now:yyyy-MM-dd}");
            foreach (var entry in linesToWrite) sb.AppendLine(entry);
            sb.AppendLine($"# AIONTOOL-END:{list.Tag}");
            await File.AppendAllTextAsync(_hostsPath, Environment.NewLine + sb);
            await RunProcessAsync("ipconfig", "/flushdns");
            WriteActionLog(interfaceName, $"Added blocklist {list.Name} - {linesToWrite.Count} entries");
            return linesToWrite.Count;
        }

        public void RemoveBlocklistByTag(string tag)
        {
            if (!File.Exists(_hostsPath)) return;
            var lines = File.ReadAllLines(_hostsPath);
            var output = new List<string>();
            bool skip = false;
            foreach (var line in lines)
            {
                if (line.StartsWith($"# AIONTOOL-BEGIN:{tag}:", StringComparison.OrdinalIgnoreCase) || line.StartsWith($"# AIOTOOL-BEGIN:{tag}:", StringComparison.OrdinalIgnoreCase))
                {
                    skip = true;
                    continue;
                }
                if (line.StartsWith($"# AIONTOOL-END:{tag}", StringComparison.OrdinalIgnoreCase) || line.StartsWith($"# AIOTOOL-END:{tag}", StringComparison.OrdinalIgnoreCase))
                {
                    skip = false;
                    continue;
                }
                if (!skip) output.Add(line);
            }
            File.WriteAllLines(_hostsPath, output);
        }

        public async Task<string> GetProxyStatusAsync()
        {
            var sb = new StringBuilder();
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", false);
            sb.AppendLine("=== Current User Proxy Settings ===");
            sb.AppendLine($"ProxyEnable   : {key?.GetValue("ProxyEnable") ?? 0}");
            sb.AppendLine($"ProxyServer   : {key?.GetValue("ProxyServer") ?? "None"}");
            sb.AppendLine($"AutoConfigURL : {key?.GetValue("AutoConfigURL") ?? "None"}");
            sb.AppendLine();
            sb.AppendLine("=== System WinHTTP Proxy ===");
            sb.AppendLine(await RunProcessCaptureAsync("netsh", "winhttp show proxy"));
            return sb.ToString();
        }

        public async Task DisableSystemProxyAsync(string interfaceName)
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", writable: true);
            key?.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
            await RunProcessAsync("netsh", "winhttp reset proxy");
            WriteActionLog(interfaceName, "Disabled proxy");
        }

        public async Task ResetFirewallAsync(string interfaceName)
        {
            await RunProcessAsync("netsh", "advfirewall reset");
            WriteActionLog(interfaceName, "Reset firewall");
        }

        public async Task ResetArpAndRoutesAsync(string interfaceName)
        {
            await RunProcessAsync("netsh", "interface ip delete arpcache");
            await RunProcessAsync("route", "-f");
            await RunProcessAsync("ipconfig", $"/renew \"{interfaceName}\"");
            WriteActionLog(interfaceName, "Reset ARP cache and routes");
        }

        public async Task FullNetworkRepairAsync(string interfaceName)
        {
            await RunProcessAsync("netsh", "winsock reset");
            await RunProcessAsync("netsh", "int ip reset");
            await RunProcessAsync("ipconfig", "/flushdns");
            WriteActionLog(interfaceName, "Full network repair");
        }

        public async Task<string> CleanTempFilesAsync(string interfaceName)
        {
            int deletedFiles = 0;
            int skippedFiles = 0;
            await Task.Run(() =>
            {
                string[] targets = { Path.GetTempPath(), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp") };
                foreach (var folder in targets.Where(Directory.Exists))
                {
                    foreach (var file in Directory.GetFiles(folder)) { try { File.Delete(file); deletedFiles++; } catch { skippedFiles++; } }
                    foreach (var dir in Directory.GetDirectories(folder)) { try { Directory.Delete(dir, true); deletedFiles++; } catch { skippedFiles++; } }
                }
            });
            WriteActionLog(interfaceName, "Cleaned temp files");
            string msg = $"Temp Cleanup Finished: {deletedFiles} items removed, {skippedFiles} locked items skipped.";
            CommandOutputReceived?.Invoke(msg);
            return msg;
        }

        public async Task RunSystemRepairDismSfcAsync(string interfaceName)
        {
            await RunProcessAsync("DISM.exe", "/Online /Cleanup-Image /RestoreHealth");
            await RunProcessAsync("sfc.exe", "/scannow");
            WriteActionLog(interfaceName, "Ran DISM and SFC");
        }

        public async Task ResetWindowsUpdateAsync(string interfaceName)
        {
            string[] services = { "wuauserv", "cryptSvc", "bits", "msiserver" };
            foreach (var s in services) await RunProcessAsync("net", $"stop {s}");
            string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string softDist = Path.Combine(winDir, "SoftwareDistribution");
            string catroot2 = Path.Combine(winDir, "System32", "catroot2");
            await RunProcessAsync("cmd.exe", $"/c rd /s /q \"{softDist}.old\" 2>nul & ren \"{softDist}\" SoftwareDistribution.old 2>nul");
            await RunProcessAsync("cmd.exe", $"/c rd /s /q \"{catroot2}.old\" 2>nul & ren \"{catroot2}\" catroot2.old 2>nul");
            foreach (var s in services) await RunProcessAsync("net", $"start {s}");
            WriteActionLog(interfaceName, "Reset Windows Update");
        }

        public void SaveSettings(int themeId, string interfaceName, bool isDarkMode, string languageCode)
        {
            if (!RememberSettings) { if (File.Exists(ConfigFilePath)) File.Delete(ConfigFilePath); return; }
            Directory.CreateDirectory(_cfgDir);
            var lines = new[]
            {
                $"theme={themeId}",
                $"interface={interfaceName}",
                $"log={(EnableActionLog ? "1" : "0")}",
                $"dark={(isDarkMode ? "1" : "0")}",
                $"lang={languageCode}"
            };
            File.WriteAllLines(ConfigFilePath, lines);
        }

        public (int ThemeId, string? InterfaceName, bool LogEnabled, bool IsDarkMode, string LanguageCode)? LoadSavedSettings()
        {
            if (!File.Exists(ConfigFilePath)) return null;
            int theme = 1; string? iface = null; bool log = true; bool dark = true; string lang = "en";
            foreach (var line in File.ReadAllLines(ConfigFilePath))
            {
                int idx = line.IndexOf('=');
                if (idx <= 0) continue;
                string k = line[..idx].Trim();
                string v = line[(idx + 1)..].Trim();
                if (k.Equals("theme", StringComparison.OrdinalIgnoreCase) && int.TryParse(v, out int t)) theme = t;
                else if (k.Equals("interface", StringComparison.OrdinalIgnoreCase)) iface = v;
                else if (k.Equals("log", StringComparison.OrdinalIgnoreCase)) log = v == "1";
                else if (k.Equals("dark", StringComparison.OrdinalIgnoreCase)) dark = v != "0";
                else if (k.Equals("lang", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(v)) lang = v;
            }
            return (theme, iface, log, dark, lang);
        }

        public string ReadActionLog() => !File.Exists(LogFilePath) ? "The action log is empty." : File.ReadAllText(LogFilePath);
        public void ClearActionLog() { if (File.Exists(LogFilePath)) File.Delete(LogFilePath); }

        private CancellationTokenSource? _activeProcessCts;
        private Process? _activeProcess;

        public void CancelCurrentOperation()
        {
            try
            {
                _activeProcessCts?.Cancel();
                if (_activeProcess != null && !_activeProcess.HasExited)
                {
                    _activeProcess.Kill(entireProcessTree: true);
                    CommandOutputReceived?.Invoke("[STOPPED] Process cancelled by user.");
                }
            }
            catch { }
        }

                // ==================== NEW FEATURES: SPEED TEST, NAT ANALYZER, GAME BOOSTER ====================
        
        [DllImport("psapi.dll")]
        private static extern int EmptyWorkingSet(IntPtr hwProc);

        public async Task OptimizeForGamingAsync()
        {
            CommandOutputReceived?.Invoke("[GAME BOOSTER] Initializing Gaming Mode Optimization...");
            
            // 1. Stop unnecessary Windows services that cause lag/disk usage
            string[] servicesToStop = { "SysMain", "DiagTrack", "WSearch", "wuauserv" };
            foreach (var srv in servicesToStop)
            {
                await RunProcessAsync("net", $"stop {srv}");
            }
            CommandOutputReceived?.Invoke("[GAME BOOSTER] Background services suspended (SysMain, Telemetry, Windows Update).");

            // 2. Kill unnecessary background apps
            string[] appsToKill = { "OneDrive", "Cortana", "msedge", "chrome" };
            int killed = 0;
            foreach (var app in appsToKill)
            {
                foreach (var proc in Process.GetProcessesByName(app))
                {
                    try { proc.Kill(); killed++; } catch { }
                }
            }
            CommandOutputReceived?.Invoke($"[GAME BOOSTER] Terminated {killed} unnecessary background application processes.");

            // 3. Clear Standby Memory / RAM Working Set
            int cleared = 0;
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    EmptyWorkingSet(proc.Handle);
                    cleared++;
                }
                catch { }
            }
            CommandOutputReceived?.Invoke($"[GAME BOOSTER] Emptied working set RAM for {cleared} active processes.");
            CommandOutputReceived?.Invoke("[GAME BOOSTER] System optimized for maximum gaming performance! (Note: Settings will revert after reboot).");
        }

        public async Task<string> RunSpeedTestAsync()
        {
            CommandOutputReceived?.Invoke("[SPEED TEST] Connecting to high-speed CDN test server (10MB payload)...");
            try
            {
                var stopwatch = Stopwatch.StartNew();
                
                // Using a standard 10MB test file from Cloudflare / Tele2 or similar reliable source
                string testUrl = "http://speedtest.tele2.net/10MB.zip";
                
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
                var response = await client.GetAsync(testUrl, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();
                
                var stream = await response.Content.ReadAsStreamAsync();
                var buffer = new byte[8192];
                long totalRead = 0;
                int read;
                
                while ((read = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    totalRead += read;
                    if (_activeProcessCts?.IsCancellationRequested == true)
                    {
                        CommandOutputReceived?.Invoke("[SPEED TEST] Cancelled by user.");
                        return "Cancelled";
                    }
                }
                
                stopwatch.Stop();
                double seconds = stopwatch.Elapsed.TotalSeconds;
                double mbps = (totalRead * 8.0) / 1000000.0 / seconds;
                double mbytes = totalRead / 1048576.0;
                
                string result = $"[SPEED TEST] Downloaded {mbytes:F1} MB in {seconds:F1}s. Estimated Speed: {mbps:F2} Mbps";
                CommandOutputReceived?.Invoke(result);
                return result;
            }
            catch (Exception ex)
            {
                CommandOutputReceived?.Invoke($"[SPEED TEST ERR] {ex.Message}");
                return "Failed";
            }
        }

        public async Task<string> AnalyzeNatTypeAsync()
        {
            CommandOutputReceived?.Invoke("[NAT ANALYZER] Analyzing Network Address Translation (NAT) status...");
            try
            {
                // 1. Get Public IP
                var (pubIp, _, _) = await GetPublicIpInfoAsync();
                if (string.IsNullOrEmpty(pubIp)) return "Could not determine Public IP.";

                // 2. Get Local IPs
                var localIps = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => a.Address.ToString())
                    .ToList();

                // 3. Analyze
                CommandOutputReceived?.Invoke($"[NAT ANALYZER] Public IP: {pubIp}");
                CommandOutputReceived?.Invoke($"[NAT ANALYZER] Local IP(s): {string.Join(", ", localIps)}");

                bool isDirect = localIps.Contains(pubIp);
                if (isDirect)
                {
                    string msg = "NAT Type 1 (Open): Your PC is directly connected to the internet with a public IP. Port Forwarding is not required.";
                    CommandOutputReceived?.Invoke($"[NAT ANALYZER] {msg}");
                    return msg;
                }

                // Check for CGNAT (Carrier-Grade NAT) usually in 100.64.0.0/10 or if the ISP hides the true public IP completely
                if (pubIp.StartsWith("100.") || localIps.Any(ip => ip.StartsWith("100.")))
                {
                    string msg = "NAT Type 3 (Strict / CGNAT): Your ISP is using Carrier-Grade NAT. You cannot host servers or port forward on this connection.";
                    CommandOutputReceived?.Invoke($"[NAT ANALYZER] {msg}");
                    return msg;
                }

                string msg2 = "NAT Type 2 (Moderate): You are behind a standard router (Private IP). UPnP or manual Port Forwarding in your router is required to host servers.";
                CommandOutputReceived?.Invoke($"[NAT ANALYZER] {msg2}");
                return msg2;
            }
            catch (Exception ex)
            {
                CommandOutputReceived?.Invoke($"[NAT ANALYZER ERR] {ex.Message}");
                return "Error";
            }
        }

        public async Task<int> RunProcessAsync(string fileName, string arguments)
        {
            CommandOutputReceived?.Invoke($"> {fileName} {arguments}");
            _activeProcessCts?.Dispose();
            _activeProcessCts = new CancellationTokenSource();
            var token = _activeProcessCts.Token;

            bool isSfc = fileName.Equals("sfc.exe", StringComparison.OrdinalIgnoreCase) || fileName.Equals("sfc", StringComparison.OrdinalIgnoreCase);

            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = isSfc ? Encoding.Unicode : Encoding.UTF8,
                StandardErrorEncoding = isSfc ? Encoding.Unicode : Encoding.UTF8
            };

            using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _activeProcess = proc;

            proc.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) CommandOutputReceived?.Invoke(e.Data.TrimEnd()); };
            proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) CommandOutputReceived?.Invoke("[ERR] " + e.Data.TrimEnd()); };

            try
            {
                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                await proc.WaitForExitAsync(token);
                return proc.ExitCode;
            }
            catch (OperationCanceledException)
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                return -1;
            }
            finally
            {
                _activeProcess = null;
            }
        }

        public async Task<string> RunProcessCaptureAsync(string fileName, string arguments)
        {
            CommandOutputReceived?.Invoke($"> {fileName} {arguments}");
            var psi = new ProcessStartInfo { FileName = fileName, Arguments = arguments, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            using var proc = Process.Start(psi)!;
            string stdout = await proc.StandardOutput.ReadToEndAsync();
            string stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();
            string combined = (stdout + Environment.NewLine + stderr).Trim();
            if (!string.IsNullOrWhiteSpace(combined)) CommandOutputReceived?.Invoke(combined);
            return combined;
        }

        public void WriteActionLog(string interfaceName, string actionText)
        {
            if (!EnableActionLog) return;
            Directory.CreateDirectory(_cfgDir);
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  [{interfaceName}]  {actionText}{Environment.NewLine}";
            File.AppendAllText(LogFilePath, line);
        }
    }
}
