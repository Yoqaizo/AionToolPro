using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AionToolPro.Desktop.Models;

namespace AionToolPro.Desktop.Services
{
    public sealed partial class AioNetworkService : IDisposable
    {
        private const long MaxLogFileBytes = 512 * 1024;
        private const long MaxBlocklistBytes = 8 * 1024 * 1024;
        private const int MaxBlocklistLines = 200_000;
        private const int MaxHostsBackupsToKeep = 10;

        private readonly string _cfgDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AionToolPro");
        private readonly string _hostsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), @"drivers\etc\hosts");

        private static readonly HttpClient SharedHttp = CreateSharedHttpClient();
        private readonly IProcessRunner _runner;
        private readonly object _ioLock = new();
        private readonly object _opLock = new();

        private CancellationTokenSource? _activeOperationCts;

        private static readonly byte[] FastPingPayload16 = new byte[16];
        private readonly ConcurrentDictionary<string, (IPAddress Ip, DateTime ExpiresUtc)> _dnsResolveCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<int, (string Name, string Path, DateTime CachedUtc)> _pidCache = new();

        private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

        private static readonly HashSet<string> ProtectedProcesses = new(StringComparer.OrdinalIgnoreCase)
        {
            "lsass", "csrss", "wininit", "winlogon", "services", "smss", "svchost",
            "System", "Idle", "Registry", "Memory Compression", "MsMpEng", "NisSrv",
            "SecurityHealthService", "dwm", "fontdrvhost", "sihost", "spoolsv",
            "taskmgr", "AionToolPro"
        };

        private static readonly string[] IgnoredAdapterKeywords =
        {
            "Loopback", "vEthernet", "WSL", "Hyper-V", "VMware", "VirtualBox",
            "Teredo", "ISATAP", "6to4", "Bluetooth", "Miniport", "Filter", "Kernel Debug"
        };

        public AioNetworkService(IProcessRunner? runner = null)
        {
            _runner = runner ?? new ProcessRunner();
        }

        public static string GetAppVersionString()
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            if (v == null) return "0.8.3";
            return $"{Math.Max(0, v.Major)}.{Math.Max(0, v.Minor)}.{Math.Max(0, v.Build)}";
        }

        public static bool IsReparsePoint(string path)
        {
            try
            {
                if (!File.Exists(path) && !Directory.Exists(path)) return false;
                return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
            }
            catch
            {
                return true;
            }
        }

        private static HttpClient CreateSharedHttpClient()
        {
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                AutomaticDecompression = DecompressionMethods.All,
                SslOptions = new System.Net.Security.SslClientAuthenticationOptions
                {
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                }
            };
            var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(60)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"AionToolPro/{GetAppVersionString()}");
            return client;
        }

        public string ConfigFilePath => Path.Combine(_cfgDir, "settings.json");
        public string LegacyConfigFilePath => Path.Combine(_cfgDir, "settings.cfg");
        public string CustomDnsFilePath => Path.Combine(_cfgDir, "custom_dns.json");
        public string LogFilePath => Path.Combine(_cfgDir, "actions.log");
        public string DnsBackupFilePath => Path.Combine(_cfgDir, "dns_backup.json");
        public string DnsOriginalBackupFilePath => Path.Combine(_cfgDir, "dns_backup.original.json");
        public string HostsOriginalBackupPath => Path.Combine(_cfgDir, "hosts.backup.original");
        public string DohStateFilePath => Path.Combine(_cfgDir, "doh_rules.json");
        public string BlockedAppsFilePath => Path.Combine(_cfgDir, "blocked_apps.json");
        public string GameBoosterStateFilePath => Path.Combine(_cfgDir, "gamebooster_state.json");
        public string HostsPath => _hostsPath;

        public bool EnableActionLog { get; set; } = true;
        public bool RememberSettings { get; set; } = true;

        public event Action<string>? CommandOutputReceived;

        public static bool ValidateIpv4(string? input) => AioCatalog.ValidateIpv4(input);
        public static bool IsSafeDnsIpv4(string? input) => AioCatalog.IsSafeDnsIpv4(input);
        public static bool IsValidSubnetMask(string? input) => AioCatalog.IsValidSubnetMask(input);
        public static bool ValidateHostOrIp(string? input) => AioCatalog.ValidateHostOrIp(input);

        private void Emit(string msg) => CommandOutputReceived?.Invoke(msg);

        // ==================== OPERATION LIFECYCLE & SAFE CANCELLATION ====================
        private sealed class OperationLease : IDisposable
        {
            private readonly AioNetworkService _owner;
            private readonly CancellationTokenSource _cts;
            public CancellationToken Token => _cts.Token;

            public OperationLease(AioNetworkService owner, CancellationTokenSource cts)
            {
                _owner = owner;
                _cts = cts;
            }

            public void Dispose() => _owner.ReleaseTrackedOperationCts(_cts);
        }

        private OperationLease CreateTrackedOperationCts(TimeSpan? maxDuration = null)
        {
            var cts = maxDuration.HasValue
                ? new CancellationTokenSource(maxDuration.Value)
                : new CancellationTokenSource();

            CancellationTokenSource? previous;
            lock (_opLock)
            {
                previous = _activeOperationCts;
                _activeOperationCts = cts;
            }

            if (previous != null)
            {
                try { previous.Cancel(); } catch { }
            }

            return new OperationLease(this, cts);
        }

        private void ReleaseTrackedOperationCts(CancellationTokenSource cts)
        {
            lock (_opLock)
            {
                if (ReferenceEquals(_activeOperationCts, cts))
                    _activeOperationCts = null;
            }
            try { cts.Dispose(); } catch { }
        }

        public bool CancelCurrentOperation()
        {
            bool stopped = false;
            lock (_opLock)
            {
                if (_activeOperationCts != null && !_activeOperationCts.IsCancellationRequested)
                {
                    try
                    {
                        _activeOperationCts.Cancel();
                        stopped = true;
                    }
                    catch { }
                }
            }

            Emit(stopped
                ? "[STOPPED] Active operation cancelled by user."
                : "[INFO] No active cancellable operation is currently running.");
            return stopped;
        }

        private async Task<ProcessResult> ExecAsync(
            string exe,
            IEnumerable<string> args,
            CancellationToken token = default,
            bool silent = false,
            Action<string>? onLine = null,
            TimeSpan? timeout = null)
        {
            if (!silent)
            {
                Emit($"> {exe} {string.Join(" ", args)}");
            }

            Action<string>? combined = null;
            if (!silent || onLine != null)
            {
                combined = line =>
                {
                    if (!silent) Emit(line);
                    onLine?.Invoke(line);
                };
            }

            return await _runner.RunAsync(exe, args, token, combined, silent, timeout).ConfigureAwait(false);
        }

        private static string ValidateInterfaceName(string interfaceName)
        {
            if (string.IsNullOrWhiteSpace(interfaceName))
                throw new ArgumentException("Network interface name cannot be empty.");

            string trimmed = interfaceName.Trim();
            if (trimmed.Contains('\r') || trimmed.Contains('\n') || trimmed.Contains('"') || trimmed.Length > 128)
                throw new ArgumentException("Invalid characters in network interface name.");

            return trimmed;
        }

        // ==================== ADAPTER SCANNER & BACKGROUND TELEMETRY ====================
        public Task<List<NetworkAdaptorItem>> ScanSystemAdaptorsAsync(CancellationToken token = default)
        {
            return Task.Run(async () =>
            {
                var map = new Dictionary<string, NetworkAdaptorItem>(StringComparer.OrdinalIgnoreCase);

                try
                {
                    foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                    {
                        if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                            nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                            continue;

                        if (IgnoredAdapterKeywords.Any(k =>
                                nic.Name.Contains(k, StringComparison.OrdinalIgnoreCase) ||
                                nic.Description.Contains(k, StringComparison.OrdinalIgnoreCase)))
                            continue;

                        bool isUp = nic.OperationalStatus == OperationalStatus.Up;
                        map[nic.Name] = new NetworkAdaptorItem
                        {
                            Name = nic.Name,
                            AdminState = "Enabled",
                            ConnectionState = isUp ? "Connected" : "Disconnected",
                            HealthState = isUp ? AdaptorHealthState.ConnectedGreen : AdaptorHealthState.DisconnectedOrange
                        };
                    }
                }
                catch { }

                try
                {
                    var res = await _runner.RunAsync(
                        "netsh",
                        new[] { "interface", "show", "interface" },
                        token,
                        silent: true,
                        timeout: TimeSpan.FromSeconds(4)).ConfigureAwait(false);

                    if (res.IsSuccess && !string.IsNullOrWhiteSpace(res.StandardOutput))
                    {
                        foreach (var rawLine in res.StandardOutput.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries).Skip(2))
                        {
                            var line = rawLine.Trim();
                            if (line.Length == 0 || line.StartsWith("---")) continue;

                            var m = Regex.Match(line, @"^(Enabled|Disabled)\s+(Connected|Disconnected)\s+\S+\s+(.+)$", RegexOptions.IgnoreCase);
                            if (!m.Success) continue;

                            string adminState = m.Groups[1].Value;
                            string connState = m.Groups[2].Value;
                            string ifaceName = m.Groups[3].Value.Trim();

                            if (IgnoredAdapterKeywords.Any(k => ifaceName.Contains(k, StringComparison.OrdinalIgnoreCase)))
                                continue;

                            if (!map.ContainsKey(ifaceName) && adminState.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
                            {
                                map[ifaceName] = new NetworkAdaptorItem
                                {
                                    Name = ifaceName,
                                    AdminState = "Disabled",
                                    ConnectionState = connState,
                                    HealthState = AdaptorHealthState.DisabledRed
                                };
                            }
                        }
                    }
                }
                catch { }

                return map.Values
                    .OrderBy(a => (int)a.HealthState)
                    .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }, token);
        }

        public Task<string> DetectRouterGatewayIpAsync(string preferredInterfaceName) =>
            Task.Run(() => DetectRouterGatewayIp(preferredInterfaceName));

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
                        .FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.Any.Equals(g.Address));
                    if (gw != null) return gw.Address.ToString();
                }

                foreach (var nic in allNics.Where(n => n.OperationalStatus == OperationalStatus.Up))
                {
                    var gw = nic.GetIPProperties().GatewayAddresses
                        .FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.Any.Equals(g.Address));
                    if (gw != null) return gw.Address.ToString();
                }
            }
            catch { }
            return string.Empty;
        }

        public Task<AdapterTelemetrySnapshot> CaptureAdapterTelemetryAsync(string interfaceName)
        {
            return Task.Run(() =>
            {
                try
                {
                    var nic = NetworkInterface.GetAllNetworkInterfaces()
                        .FirstOrDefault(n => n.Name.Equals(interfaceName, StringComparison.OrdinalIgnoreCase));
                    if (nic == null)
                    {
                        return new AdapterTelemetrySnapshot(interfaceName, false, "Unavailable", 0, 0, 0, 0, 0, 0, new List<string>(), "N/A");
                    }

                    var stats = nic.GetIPStatistics();
                    var ipProps = nic.GetIPProperties();
                    var dnsServers = ipProps.DnsAddresses
                        .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                        .Select(a => a.ToString())
                        .ToList();

                    string ipv4 = ipProps.UnicastAddresses
                        .FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString() ?? "N/A";

                    return new AdapterTelemetrySnapshot(
                        nic.Name,
                        true,
                        nic.OperationalStatus.ToString(),
                        stats.BytesReceived,
                        stats.BytesSent,
                        stats.UnicastPacketsReceived,
                        stats.IncomingPacketsWithErrors,
                        stats.UnicastPacketsSent,
                        stats.OutgoingPacketsWithErrors,
                        dnsServers,
                        ipv4);
                }
                catch
                {
                    return new AdapterTelemetrySnapshot(interfaceName, false, "Error", 0, 0, 0, 0, 0, 0, new List<string>(), "N/A");
                }
            });
        }

        // ==================== FAST PING & NATIVE RFC 1035 UDP/53 DNS QUERY ====================
        private async Task<IPAddress?> ResolveHostCachedAsync(string hostOrIp)
        {
            if (IPAddress.TryParse(hostOrIp, out var directIp))
                return directIp;

            if (_dnsResolveCache.TryGetValue(hostOrIp, out var cached) && DateTime.UtcNow < cached.ExpiresUtc)
                return cached.Ip;

            try
            {
                using var cts = new CancellationTokenSource(1200);
                var addresses = await Dns.GetHostAddressesAsync(hostOrIp, cts.Token).ConfigureAwait(false);
                var ipv4 = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                if (ipv4 != null)
                {
                    _dnsResolveCache[hostOrIp] = (ipv4, DateTime.UtcNow.AddMinutes(5));
                    return ipv4;
                }
            }
            catch { }

            if (_dnsResolveCache.TryGetValue(hostOrIp, out var stale))
                return stale.Ip;

            if (hostOrIp.Contains("arvancloud.ir", StringComparison.OrdinalIgnoreCase))
            {
                var fallbackArvan = IPAddress.Parse("185.143.232.253");
                _dnsResolveCache[hostOrIp] = (fallbackArvan, DateTime.UtcNow.AddMinutes(1));
                return fallbackArvan;
            }

            return null;
        }

        private static async Task<long?> MeasureTcpHandshakeMsAsync(IPAddress targetIp, int port, int timeoutMs)
        {
            if (port <= 0) return null;
            try
            {
                using var tcp = new Socket(targetIp.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                using var cts = new CancellationTokenSource(timeoutMs);
                var sw = Stopwatch.StartNew();
                await tcp.ConnectAsync(new IPEndPoint(targetIp, port), cts.Token).ConfigureAwait(false);
                sw.Stop();
                return sw.ElapsedMilliseconds;
            }
            catch
            {
                return null;
            }
        }

        public async Task<long?> PingSingleHostFastAsync(string hostOrIp, int timeoutMs = 1200, int fallbackTcpPort = 0)
        {
            if (string.IsNullOrWhiteSpace(hostOrIp)) return null;
            var targetIp = await ResolveHostCachedAsync(hostOrIp).ConfigureAwait(false);
            if (targetIp == null) return null;

            var opts = new PingOptions(64, false);
            try
            {
                using var pinger = new Ping();
                var reply = await pinger.SendPingAsync(targetIp, timeoutMs, FastPingPayload16, opts).ConfigureAwait(false);
                if (reply.Status == IPStatus.Success) return reply.RoundtripTime;
            }
            catch { }

            await Task.Delay(35).ConfigureAwait(false);
            try
            {
                using var pingerRetry = new Ping();
                int retryTimeout = Math.Max(700, timeoutMs - 250);
                var reply2 = await pingerRetry.SendPingAsync(targetIp, retryTimeout, FastPingPayload16, opts).ConfigureAwait(false);
                if (reply2.Status == IPStatus.Success) return reply2.RoundtripTime;
            }
            catch { }

            if (fallbackTcpPort > 0)
            {
                return await MeasureTcpHandshakeMsAsync(targetIp, fallbackTcpPort, 750).ConfigureAwait(false);
            }

            return null;
        }

        public static async Task<long?> QueryDnsUdpLatencyMsAsync(string dnsServerIp, string domain, int timeoutMs, CancellationToken token = default)
        {
            if (!IPAddress.TryParse(dnsServerIp, out var serverIp))
                return null;

            byte[] query = BuildDnsAQueryPacket(domain, out ushort txId);

            try
            {
                using var udp = new UdpClient(serverIp.AddressFamily);
                var endpoint = new IPEndPoint(serverIp, 53);

                using var timeoutCts = new CancellationTokenSource(timeoutMs);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);

                var sw = Stopwatch.StartNew();
                await udp.SendAsync(query.AsMemory(), endpoint, linked.Token).ConfigureAwait(false);
                var result = await udp.ReceiveAsync(linked.Token).ConfigureAwait(false);
                sw.Stop();

                byte[] resp = result.Buffer;
                if (resp.Length < 12) return null;
                ushort respTxId = (ushort)((resp[0] << 8) | resp[1]);
                bool isResponse = (resp[2] & 0x80) != 0;
                int rcode = resp[3] & 0x0F;

                if (respTxId == txId && isResponse && (rcode == 0 || rcode == 3))
                    return sw.ElapsedMilliseconds;
            }
            catch { }

            return null;
        }

        private static byte[] BuildDnsAQueryPacket(string domain, out ushort transactionId)
        {
            transactionId = (ushort)RandomNumberGenerator.GetInt32(1, 65535);
            using var ms = new MemoryStream(64);
            ms.WriteByte((byte)(transactionId >> 8));
            ms.WriteByte((byte)(transactionId & 0xFF));
            ms.WriteByte(0x01);
            ms.WriteByte(0x00);
            ms.WriteByte(0x00); ms.WriteByte(0x01);
            ms.WriteByte(0x00); ms.WriteByte(0x00);
            ms.WriteByte(0x00); ms.WriteByte(0x00);
            ms.WriteByte(0x00); ms.WriteByte(0x00);

            foreach (var label in domain.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                byte[] bytes = Encoding.ASCII.GetBytes(label);
                ms.WriteByte((byte)bytes.Length);
                ms.Write(bytes, 0, bytes.Length);
            }
            ms.WriteByte(0x00);
            ms.WriteByte(0x00); ms.WriteByte(0x01);
            ms.WriteByte(0x00); ms.WriteByte(0x01);
            return ms.ToArray();
        }

        // ==================== SETTINGS, EXPORT/IMPORT & ACTION LOG ====================
        public void SaveSettings(int themeId, string interfaceName, bool isDarkMode, string languageCode, int surfacePaletteId = 0, bool minimizeToTray = false)
        {
            lock (_ioLock)
            {
                try
                {
                    if (!RememberSettings)
                    {
                        if (File.Exists(ConfigFilePath)) File.Delete(ConfigFilePath);
                        if (File.Exists(LegacyConfigFilePath)) File.Delete(LegacyConfigFilePath);
                        return;
                    }

                    Directory.CreateDirectory(_cfgDir);
                    var model = new AppSettingsModel(themeId, interfaceName, EnableActionLog, isDarkMode, languageCode, surfacePaletteId, minimizeToTray);
                    string tmp = ConfigFilePath + ".tmp";
                    File.WriteAllText(tmp, JsonSerializer.Serialize(model, JsonOpts), new UTF8Encoding(false));
                    if (File.Exists(ConfigFilePath))
                        File.Replace(tmp, ConfigFilePath, null, true);
                    else
                        File.Move(tmp, ConfigFilePath);
                }
                catch { }
            }
        }

        public AppSettingsModel? LoadSavedSettings()
        {
            lock (_ioLock)
            {
                try
                {
                    if (File.Exists(ConfigFilePath))
                    {
                        string json = File.ReadAllText(ConfigFilePath);
                        return JsonSerializer.Deserialize<AppSettingsModel>(json);
                    }

                    if (File.Exists(LegacyConfigFilePath))
                    {
                        int theme = 1; string? iface = null; bool log = true; bool dark = true; string lang = "en"; int surface = 0;
                        foreach (var line in File.ReadAllLines(LegacyConfigFilePath))
                        {
                            int idx = line.IndexOf('=');
                            if (idx <= 0) continue;
                            string k = line[..idx].Trim();
                            string v = line[(idx + 1)..].Trim();
                            if (k.Equals("theme", StringComparison.OrdinalIgnoreCase) && int.TryParse(v, out int t)) theme = t;
                            else if (k.Equals("surface", StringComparison.OrdinalIgnoreCase) && int.TryParse(v, out int s)) surface = s;
                            else if (k.Equals("interface", StringComparison.OrdinalIgnoreCase)) iface = v;
                            else if (k.Equals("log", StringComparison.OrdinalIgnoreCase)) log = v == "1";
                            else if (k.Equals("dark", StringComparison.OrdinalIgnoreCase)) dark = v != "0";
                            else if (k.Equals("lang", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(v)) lang = v;
                        }
                        return new AppSettingsModel(theme, iface, log, dark, lang, surface, false);
                    }
                }
                catch { }
                return null;
            }
        }

        public async Task<OperationResult> ExportSettingsBundleAsync(string destinationPath, AppSettingsModel currentSettings)
        {
            try
            {
                var bundle = new SettingsBundleModel(currentSettings, GetCustomDnsProfiles());
                string json = JsonSerializer.Serialize(bundle, JsonOpts);
                await File.WriteAllTextAsync(destinationPath, json, new UTF8Encoding(false)).ConfigureAwait(false);
                WriteActionLog("Settings", $"OK Exported settings bundle to {Path.GetFileName(destinationPath)}");
                return new OperationResult(true, $"Settings and {bundle.CustomDns.Count} custom DNS profile(s) exported to:\n{destinationPath}");
            }
            catch (Exception ex)
            {
                return new OperationResult(false, $"Export failed: {ex.Message}");
            }
        }

        public async Task<(bool Success, SettingsBundleModel? Bundle, string Message)> ImportSettingsBundleAsync(string sourcePath)
        {
            try
            {
                if (!File.Exists(sourcePath))
                    return (false, null, "Selected file does not exist.");

                string json = await File.ReadAllTextAsync(sourcePath).ConfigureAwait(false);
                var bundle = JsonSerializer.Deserialize<SettingsBundleModel>(json);
                if (bundle?.Settings == null)
                    return (false, null, "Invalid AionToolPro settings file.");

                var validProfiles = (bundle.CustomDns ?? new List<CustomDnsProfile>())
                    .Where(p => !string.IsNullOrWhiteSpace(p.Name) && IsSafeDnsIpv4(p.PrimaryIp) &&
                                (string.IsNullOrWhiteSpace(p.SecondaryIp) || IsSafeDnsIpv4(p.SecondaryIp)))
                    .ToList();

                SaveCustomDnsProfilesList(validProfiles);
                WriteActionLog("Settings", $"OK Imported settings bundle ({validProfiles.Count} custom DNS)");
                return (true, bundle with { CustomDns = validProfiles }, $"Imported settings and {validProfiles.Count} custom DNS profile(s) successfully.");
            }
            catch (Exception ex)
            {
                return (false, null, $"Import failed: {ex.Message}");
            }
        }

        public string ReadActionLog()
        {
            lock (_ioLock)
            {
                try
                {
                    return !File.Exists(LogFilePath) ? "The action log is empty." : File.ReadAllText(LogFilePath);
                }
                catch (Exception ex)
                {
                    return $"Error reading action log: {ex.Message}";
                }
            }
        }

        public void ClearActionLog()
        {
            lock (_ioLock)
            {
                try
                {
                    if (File.Exists(LogFilePath)) File.Delete(LogFilePath);
                }
                catch { }
            }
        }

        private static string SanitizeLogField(string? text) =>
            string.IsNullOrEmpty(text) ? string.Empty : text.Replace("\r", " ").Replace("\n", " ").Trim();

        public void WriteActionLog(string interfaceName, string actionText)
        {
            if (!EnableActionLog) return;
            lock (_ioLock)
            {
                try
                {
                    Directory.CreateDirectory(_cfgDir);
                    if (File.Exists(LogFilePath))
                    {
                        var fi = new FileInfo(LogFilePath);
                        if (fi.Length > MaxLogFileBytes)
                        {
                            var tailLines = File.ReadAllLines(LogFilePath).TakeLast(250).ToArray();
                            File.WriteAllLines(LogFilePath, tailLines, new UTF8Encoding(false));
                        }
                    }

                    string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  [{SanitizeLogField(interfaceName)}]  {SanitizeLogField(actionText)}{Environment.NewLine}";
                    File.AppendAllText(LogFilePath, line, new UTF8Encoding(false));
                }
                catch { }
            }
        }

        public void Dispose()
        {
            lock (_opLock)
            {
                try { _activeOperationCts?.Cancel(); } catch { }
                try { _activeOperationCts?.Dispose(); } catch { }
                _activeOperationCts = null;
            }
        }
    }
}
