using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
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

        private static readonly byte[] StandardPingPayload32 = Encoding.ASCII.GetBytes("abcdefghijklmnopqrstuvwabcdefghi");
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
            "Teredo", "ISATAP", "6to4", "Bluetooth", "Miniport", "Filter", "Kernel Debug",
            "Wi-Fi Direct", "Local Area Connection*", "KM-TEST", "Npcap", "Pseudo",
            "Virtual", "TAP-Windows", "Wintun", "WireGuard", "OpenVPN", "ZeroTier",
            "Hamachi", "Radmin", "Tailscale", "NordLynx", "CloudflareWARP", "WARP",
            "Clash", "sing-tun", "sing-box", "utun", "Nekoray", "NekoBox", "mihomo",
            "v2ray", "xray", "Mullvad", "Proton", "Windscribe", "Async", "RAS", "QoS"
        };

        private static readonly string[] VpnTunKeywords =
        {
            "Wintun", "WireGuard", "TAP-Windows", "OpenVPN", "sing-tun", "sing-box",
            "Clash", "mihomo", "Nekoray", "NekoBox", "v2ray", "xray", "WARP",
            "CloudflareWARP", "NordLynx", "Tailscale", "Mullvad", "Proton", "Windscribe", "utun"
        };

        [StructLayout(LayoutKind.Sequential)]
        private struct IP_OPTION_INFORMATION
        {
            public byte Ttl;
            public byte Tos;
            public byte Flags;
            public byte OptionsSize;
            public IntPtr OptionsData;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ICMP_ECHO_REPLY
        {
            public uint Address;
            public uint Status;
            public uint RoundTripTime;
            public ushort DataSize;
            public ushort Reserved;
            public IntPtr Data;
            public IP_OPTION_INFORMATION Options;
        }

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetBestInterface(uint dwDestAddr, out uint pdwBestIfIndex);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern IntPtr IcmpCreateFile();

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern bool IcmpCloseHandle(IntPtr icmpHandle);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint IcmpSendEcho2Ex(
            IntPtr icmpHandle,
            IntPtr eventHandle,
            IntPtr apcRoutine,
            IntPtr apcContext,
            uint sourceAddress,
            uint destinationAddress,
            byte[] requestData,
            ushort requestSize,
            ref IP_OPTION_INFORMATION requestOptions,
            IntPtr replyBuffer,
            uint replySize,
            uint timeout);

        public AioNetworkService(IProcessRunner? runner = null)
        {
            _runner = runner ?? new ProcessRunner();
        }

        public static string GetAppVersionString()
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            if (v == null) return "0.9.2";
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
        public string DiagnosticLogFilePath => Path.Combine(_cfgDir, "diagnostics.log");
        public string DnsBackupFilePath => Path.Combine(_cfgDir, "dns_backup.json");
        public string DnsOriginalBackupFilePath => Path.Combine(_cfgDir, "dns_backup.original.json");
        public string HostsOriginalBackupPath => Path.Combine(_cfgDir, "hosts.backup.original");
        public string DohStateFilePath => Path.Combine(_cfgDir, "doh_rules.json");
        public string BlockedAppsFilePath => Path.Combine(_cfgDir, "blocked_apps.json");
        public string GameBoosterStateFilePath => Path.Combine(_cfgDir, "gamebooster_state.json");
        public string HostsPath => _hostsPath;

        public bool EnableActionLog { get; set; } = true;
        public bool RememberSettings { get; set; } = true;
        public bool EnableDiagnosticLogs { get; set; } = false;

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

        // ==================== ADAPTER SCANNER, ACTIVE ROUTE DETECTION & PER-ADAPTER TELEMETRY ====================
        private static bool IsPhysicalCleanAdapter(NetworkInterface nic)
        {
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Unknown)
                return false;

            bool allowedType = nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
                               nic.NetworkInterfaceType == NetworkInterfaceType.GigabitEthernet ||
                               nic.NetworkInterfaceType == NetworkInterfaceType.FastEthernetT ||
                               nic.NetworkInterfaceType == NetworkInterfaceType.FastEthernetFx ||
                               nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet3Megabit ||
                               nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ||
                               nic.NetworkInterfaceType == NetworkInterfaceType.Ppp ||
                               nic.NetworkInterfaceType == NetworkInterfaceType.Wwanpp ||
                               nic.NetworkInterfaceType == NetworkInterfaceType.Wwanpp2;
            if (!allowedType) return false;

            if (IgnoredAdapterKeywords.Any(k =>
                    nic.Name.Contains(k, StringComparison.OrdinalIgnoreCase) ||
                    nic.Description.Contains(k, StringComparison.OrdinalIgnoreCase)))
                return false;

            if (nic.NetworkInterfaceType != NetworkInterfaceType.Ppp &&
                nic.NetworkInterfaceType != NetworkInterfaceType.Wwanpp &&
                nic.NetworkInterfaceType != NetworkInterfaceType.Wwanpp2)
            {
                var mac = nic.GetPhysicalAddress()?.GetAddressBytes();
                if (mac == null || mac.Length < 6 || mac.All(b => b == 0))
                    return false;
            }

            return true;
        }

        private static bool HasActiveVpnTunAdapter(NetworkInterface[] allNics)
        {
            foreach (var nic in allNics)
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (VpnTunKeywords.Any(k =>
                        nic.Name.Contains(k, StringComparison.OrdinalIgnoreCase) ||
                        nic.Description.Contains(k, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
            return false;
        }

        private static uint TryGetBestRouteInterfaceIndex()
        {
            foreach (string probeIp in new[] { "1.1.1.1", "8.8.8.8" })
            {
                try
                {
                    uint dest = BitConverter.ToUInt32(IPAddress.Parse(probeIp).GetAddressBytes(), 0);
                    if (GetBestInterface(dest, out uint ifIdx) == 0 && ifIdx != 0)
                        return ifIdx;
                }
                catch { }
            }
            return 0;
        }

        private static bool TryGetNicIpv4Index(NetworkInterface nic, out int ifIndex)
        {
            ifIndex = -1;
            try
            {
                if (!nic.Supports(NetworkInterfaceComponent.IPv4)) return false;
                var ipv4Props = nic.GetIPProperties().GetIPv4Properties();
                if (ipv4Props != null)
                {
                    ifIndex = ipv4Props.Index;
                    return true;
                }
            }
            catch { }
            return false;
        }

        private static IPAddress? GetValidUnicastIpv4(NetworkInterface nic)
        {
            try
            {
                foreach (var u in nic.GetIPProperties().UnicastAddresses)
                {
                    if (u.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    byte[] b = u.Address.GetAddressBytes();
                    if (b[0] == 0 || b[0] == 127 || (b[0] == 169 && b[1] == 254)) continue;
                    return u.Address;
                }
            }
            catch { }
            return null;
        }

        private static bool HasIpv4Gateway(NetworkInterface nic)
        {
            try
            {
                return nic.GetIPProperties().GatewayAddresses
                    .Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.Any.Equals(g.Address));
            }
            catch { return false; }
        }

        private static string FormatLinkSpeed(long bitsPerSec)
        {
            if (bitsPerSec <= 0) return "N/A";
            if (bitsPerSec >= 1_000_000_000) return $"{bitsPerSec / 1_000_000_000.0:0.#} Gbps";
            if (bitsPerSec >= 1_000_000) return $"{bitsPerSec / 1_000_000.0:0} Mbps";
            return $"{bitsPerSec / 1_000.0:0} Kbps";
        }

        public Task<List<NetworkAdaptorItem>> ScanSystemAdaptorsAsync(CancellationToken token = default)
        {
            return Task.Run(async () =>
            {
                var map = new Dictionary<string, (NetworkAdaptorItem Item, int PriorityScore)>(StringComparer.OrdinalIgnoreCase);
                uint bestIfIndex = TryGetBestRouteInterfaceIndex();

                try
                {
                    var allNics = NetworkInterface.GetAllNetworkInterfaces();
                    foreach (var nic in allNics)
                    {
                        if (!IsPhysicalCleanAdapter(nic))
                            continue;

                        bool isUp = nic.OperationalStatus == OperationalStatus.Up;
                        bool hasGw = isUp && HasIpv4Gateway(nic);
                        bool hasIp = isUp && GetValidUnicastIpv4(nic) != null;
                        bool matchesBestRoute = bestIfIndex != 0 && TryGetNicIpv4Index(nic, out int idx) && idx == (int)bestIfIndex;

                        int score = 0;
                        if (matchesBestRoute && isUp) score += 1000;
                        if (isUp && hasGw && hasIp) score += 500;
                        else if (isUp && hasIp) score += 200;
                        else if (isUp) score += 100;

                        if (nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
                            nic.NetworkInterfaceType == NetworkInterfaceType.GigabitEthernet)
                            score += 20;
                        else if (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                            score += 10;

                        var item = new NetworkAdaptorItem
                        {
                            Name = nic.Name,
                            AdminState = "Enabled",
                            ConnectionState = isUp ? "Connected" : "Disconnected",
                            HealthState = isUp ? AdaptorHealthState.ConnectedGreen : AdaptorHealthState.DisconnectedOrange,
                            IsActiveInternetRoute = matchesBestRoute || (isUp && hasGw && hasIp)
                        };
                        map[nic.Name] = (item, score);
                    }
                }
                catch (Exception ex)
                {
                    WriteDiagnosticLog("AdapterScan", $"Managed NIC scan error: {ex.Message}");
                }

                // Include disabled physical ("Dedicated") adapters cleanly so user can see/manage them
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

                            var m = Regex.Match(line, @"^(Enabled|Disabled)\s+(Connected|Disconnected)\s+(Dedicated)\s+(.+)$", RegexOptions.IgnoreCase);
                            if (!m.Success) continue;

                            string adminState = m.Groups[1].Value;
                            string connState = m.Groups[2].Value;
                            string ifaceName = m.Groups[4].Value.Trim();

                            if (IgnoredAdapterKeywords.Any(k => ifaceName.Contains(k, StringComparison.OrdinalIgnoreCase)))
                                continue;

                            if (!map.ContainsKey(ifaceName) && adminState.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
                            {
                                var item = new NetworkAdaptorItem
                                {
                                    Name = ifaceName,
                                    AdminState = "Disabled",
                                    ConnectionState = connState,
                                    HealthState = AdaptorHealthState.DisabledRed,
                                    IsActiveInternetRoute = false
                                };
                                map[ifaceName] = (item, -100);
                            }
                        }
                    }
                }
                catch { }

                var ordered = map.Values
                    .OrderBy(x => (int)x.Item.HealthState)
                    .ThenByDescending(x => x.PriorityScore)
                    .ThenBy(x => x.Item.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(x => x.Item)
                    .ToList();

                WriteDiagnosticLog("AdapterScan", $"Found {ordered.Count} clean adapter(s): {string.Join(", ", ordered.Select(a => $"{a.Name}[{a.ConnectionState}{(a.IsActiveInternetRoute ? ",Active" : "")}]"))}");
                return ordered;
            }, token);
        }

        public Task<string> DetectRouterGatewayIpAsync(string interfaceName) =>
            Task.Run(() => DetectRouterGatewayIp(interfaceName));

        /// <summary>
        /// Strictly detects the default IPv4 gateway for the specified adapter ONLY (never borrows another adapter's router).
        /// </summary>
        public string DetectRouterGatewayIp(string interfaceName)
        {
            try
            {
                var selectedNic = NetworkInterface.GetAllNetworkInterfaces()
                    .FirstOrDefault(n => n.Name.Equals(interfaceName, StringComparison.OrdinalIgnoreCase));

                if (selectedNic != null && selectedNic.OperationalStatus == OperationalStatus.Up)
                {
                    var gw = selectedNic.GetIPProperties().GatewayAddresses
                        .FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.Any.Equals(g.Address));
                    if (gw != null) return gw.Address.ToString();
                }
            }
            catch { }
            return string.Empty;
        }

        /// <summary>
        /// Captures telemetry strictly for the selected network adapter (zero cross-adapter leakage).
        /// </summary>
        public Task<AdapterTelemetrySnapshot> CaptureAdapterTelemetryAsync(string interfaceName)
        {
            return Task.Run(() =>
            {
                try
                {
                    var allNics = NetworkInterface.GetAllNetworkInterfaces();
                    var nic = allNics.FirstOrDefault(n => n.Name.Equals(interfaceName, StringComparison.OrdinalIgnoreCase));

                    if (nic == null)
                    {
                        return new AdapterTelemetrySnapshot(interfaceName, false, false, "Disabled / Unavailable", 0, 0, 0, 0, 0, 0, 0, 0, new List<string>(), "N/A", string.Empty, "N/A");
                    }

                    bool isUp = nic.OperationalStatus == OperationalStatus.Up;
                    var stats = nic.GetIPStatistics();
                    var ipProps = nic.GetIPProperties();
                    var dnsServers = ipProps.DnsAddresses
                        .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                        .Select(a => a.ToString())
                        .ToList();

                    string ipv4 = GetValidUnicastIpv4(nic)?.ToString()
                        ?? ipProps.UnicastAddresses.FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString()
                        ?? "N/A";

                    string gatewayIp = string.Empty;
                    if (isUp)
                    {
                        var gw = ipProps.GatewayAddresses
                            .FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.Any.Equals(g.Address));
                        if (gw != null) gatewayIp = gw.Address.ToString();
                    }

                    string speedLabel = isUp ? FormatLinkSpeed(nic.Speed) : "Offline";

                    return new AdapterTelemetrySnapshot(
                        nic.Name,
                        true,
                        isUp,
                        isUp ? "Connected" : nic.OperationalStatus.ToString(),
                        stats.BytesReceived,
                        stats.BytesSent,
                        stats.UnicastPacketsReceived,
                        stats.IncomingPacketsWithErrors,
                        stats.IncomingPacketsDiscarded,
                        stats.UnicastPacketsSent,
                        stats.OutgoingPacketsWithErrors,
                        stats.OutgoingPacketsDiscarded,
                        dnsServers,
                        ipv4,
                        gatewayIp,
                        speedLabel);
                }
                catch
                {
                    return new AdapterTelemetrySnapshot(interfaceName, false, false, "Error", 0, 0, 0, 0, 0, 0, 0, 0, new List<string>(), "N/A", string.Empty, "N/A");
                }
            });
        }

        // ==================== FAST PER-ADAPTER PING & NATIVE RFC 1035 UDP/53 DNS QUERY ====================
        public void ClearDnsResolveCache() => _dnsResolveCache.Clear();

        private async Task<IPAddress?> ResolveHostCachedAsync(string hostOrIp)
        {
            if (IPAddress.TryParse(hostOrIp, out var directIp))
                return directIp;

            if (_dnsResolveCache.TryGetValue(hostOrIp, out var cached) && DateTime.UtcNow < cached.ExpiresUtc)
                return cached.Ip;

            try
            {
                using var cts = new CancellationTokenSource(2000);
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

        private static long? SendBoundIcmpEchoSync(IPAddress sourceIp, IPAddress destIp, int timeoutMs)
        {
            IntPtr handle = IcmpCreateFile();
            if (handle == IntPtr.Zero || handle == new IntPtr(-1))
                return null;

            int replyStructSize = Marshal.SizeOf<ICMP_ECHO_REPLY>();
            int replySize = replyStructSize + StandardPingPayload32.Length + 64;
            IntPtr replyBuf = Marshal.AllocHGlobal(replySize);
            try
            {
                uint srcAddr = BitConverter.ToUInt32(sourceIp.GetAddressBytes(), 0);
                uint dstAddr = BitConverter.ToUInt32(destIp.GetAddressBytes(), 0);
                var opts = new IP_OPTION_INFORMATION { Ttl = 128 };

                uint replies = IcmpSendEcho2Ex(
                    handle,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    srcAddr,
                    dstAddr,
                    StandardPingPayload32,
                    (ushort)StandardPingPayload32.Length,
                    ref opts,
                    replyBuf,
                    (uint)replySize,
                    (uint)Math.Max(250, timeoutMs));

                if (replies > 0)
                {
                    var reply = Marshal.PtrToStructure<ICMP_ECHO_REPLY>(replyBuf);
                    if (reply.Status == 0)
                        return reply.RoundTripTime;
                }
            }
            catch { }
            finally
            {
                Marshal.FreeHGlobal(replyBuf);
                IcmpCloseHandle(handle);
            }

            return null;
        }

        private static async Task<long?> MeasureTcpReachabilityMsAsync(
            IPAddress targetIp,
            int port,
            int timeoutMs,
            bool treatRefusedAsAlive = false,
            IPAddress? bindLocalIp = null)
        {
            if (port <= 0) return null;
            var sw = Stopwatch.StartNew();
            try
            {
                using var tcp = new Socket(targetIp.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                if (bindLocalIp != null && bindLocalIp.AddressFamily == targetIp.AddressFamily)
                {
                    tcp.Bind(new IPEndPoint(bindLocalIp, 0));
                }
                using var cts = new CancellationTokenSource(timeoutMs);
                await tcp.ConnectAsync(new IPEndPoint(targetIp, port), cts.Token).ConfigureAwait(false);
                sw.Stop();
                return sw.ElapsedMilliseconds;
            }
            catch (SocketException sex) when (treatRefusedAsAlive && sex.SocketErrorCode == SocketError.ConnectionRefused)
            {
                sw.Stop();
                return sw.ElapsedMilliseconds;
            }
            catch
            {
                return null;
            }
        }

        public async Task<PingProbeResult> ProbeHostOnAdapterAsync(
            string interfaceName,
            string hostOrIp,
            int timeoutMs = 2000,
            int[]? fallbackTcpPorts = null,
            bool allowTcpFallback = false,
            bool treatTcpRefusedAsAlive = false)
        {
            if (string.IsNullOrWhiteSpace(hostOrIp))
                return new PingProbeResult(null, ProbeTransport.Timeout);

            IPAddress? sourceIp = null;
            bool allowVpnRouteFallback = false;

            if (!string.IsNullOrWhiteSpace(interfaceName))
            {
                try
                {
                    var allNics = NetworkInterface.GetAllNetworkInterfaces();
                    var selectedNic = allNics.FirstOrDefault(n => n.Name.Equals(interfaceName, StringComparison.OrdinalIgnoreCase));

                    if (selectedNic == null || selectedNic.OperationalStatus != OperationalStatus.Up)
                        return new PingProbeResult(null, ProbeTransport.Timeout);

                    sourceIp = GetValidUnicastIpv4(selectedNic);
                    if (sourceIp == null)
                        return new PingProbeResult(null, ProbeTransport.Timeout);

                    allowVpnRouteFallback = HasIpv4Gateway(selectedNic) && HasActiveVpnTunAdapter(allNics);
                }
                catch
                {
                    return new PingProbeResult(null, ProbeTransport.Timeout);
                }
            }

            var targetIp = await ResolveHostCachedAsync(hostOrIp).ConfigureAwait(false);
            if (targetIp == null)
                return new PingProbeResult(null, ProbeTransport.Timeout);

            if (sourceIp != null && targetIp.AddressFamily == AddressFamily.InterNetwork)
            {
                long? boundMs = await Task.Run(() => SendBoundIcmpEchoSync(sourceIp, targetIp, timeoutMs)).ConfigureAwait(false);
                if (boundMs.HasValue)
                    return new PingProbeResult(boundMs.Value, ProbeTransport.Icmp);

                if (allowVpnRouteFallback && !AioCatalog.IsPrivateIpv4(targetIp))
                {
                    var opts = new PingOptions(128, false);
                    try
                    {
                        using var pinger = new Ping();
                        var reply = await pinger.SendPingAsync(targetIp, Math.Min(1500, timeoutMs), StandardPingPayload32, opts).ConfigureAwait(false);
                        if (reply.Status == IPStatus.Success)
                            return new PingProbeResult(reply.RoundtripTime, ProbeTransport.Icmp);
                    }
                    catch { }
                }
            }
            else
            {
                var opts = new PingOptions(128, false);
                try
                {
                    using var pinger = new Ping();
                    var reply = await pinger.SendPingAsync(targetIp, timeoutMs, StandardPingPayload32, opts).ConfigureAwait(false);
                    if (reply.Status == IPStatus.Success)
                        return new PingProbeResult(reply.RoundtripTime, ProbeTransport.Icmp);
                }
                catch { }
            }

            if (allowTcpFallback && fallbackTcpPorts != null && fallbackTcpPorts.Length > 0)
            {
                int tcpTimeout = Math.Min(1200, Math.Max(600, timeoutMs / 2));
                foreach (int port in fallbackTcpPorts)
                {
                    long? tcpMs = await MeasureTcpReachabilityMsAsync(targetIp, port, tcpTimeout, treatTcpRefusedAsAlive, bindLocalIp: sourceIp).ConfigureAwait(false);
                    if (tcpMs.HasValue)
                        return new PingProbeResult(tcpMs.Value, ProbeTransport.TcpFallback);

                    if (allowVpnRouteFallback && sourceIp != null && !AioCatalog.IsPrivateIpv4(targetIp))
                    {
                        long? vpnTcpMs = await MeasureTcpReachabilityMsAsync(targetIp, port, tcpTimeout, treatTcpRefusedAsAlive, bindLocalIp: null).ConfigureAwait(false);
                        if (vpnTcpMs.HasValue)
                            return new PingProbeResult(vpnTcpMs.Value, ProbeTransport.TcpFallback);
                    }
                }
            }

            return new PingProbeResult(null, ProbeTransport.Timeout);
        }

        public Task<PingProbeResult> ProbeHostAccurateAsync(
            string hostOrIp,
            int timeoutMs = 2000,
            int[]? fallbackTcpPorts = null,
            bool allowTcpFallback = false,
            bool treatTcpRefusedAsAlive = false) =>
            ProbeHostOnAdapterAsync(string.Empty, hostOrIp, timeoutMs, fallbackTcpPorts, allowTcpFallback, treatTcpRefusedAsAlive);

        public async Task<long?> PingSingleHostFastAsync(string hostOrIp, int timeoutMs = 2000, int fallbackTcpPort = 0)
        {
            int[]? ports = fallbackTcpPort > 0 ? new[] { fallbackTcpPort } : null;
            var res = await ProbeHostAccurateAsync(hostOrIp, timeoutMs, ports, allowTcpFallback: fallbackTcpPort > 0, treatTcpRefusedAsAlive: false).ConfigureAwait(false);
            return res.RttMs;
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
        public void SaveSettings(int themeId, string interfaceName, bool isDarkMode, string languageCode, int surfacePaletteId = 0, bool minimizeToTray = true, string globalPingTarget = "1.1.1.1", string intranetPingTarget = "www.arvancloud.ir", bool glassMode = false, int transparencyPercent = 100, double brightnessLevel = 1.0, double contrastLevel = 1.0, string customAccentHex = "#5865F2", string customSecondaryHex = "#38BDF8", bool enableDiagnosticLogs = false, bool enableWindowsDoh = false)
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
                    string safeGlobal = ValidateHostOrIp(globalPingTarget) ? globalPingTarget.Trim() : "1.1.1.1";
                    string safeIntranet = ValidateHostOrIp(intranetPingTarget) ? intranetPingTarget.Trim() : "www.arvancloud.ir";
                    var model = new AppSettingsModel(themeId, interfaceName, EnableActionLog, isDarkMode, languageCode, surfacePaletteId, minimizeToTray, safeGlobal, safeIntranet, glassMode, Math.Clamp(transparencyPercent, 35, 100), Math.Clamp(brightnessLevel, 0.75, 1.30), Math.Clamp(contrastLevel, 0.80, 1.35), string.IsNullOrWhiteSpace(customAccentHex) ? "#5865F2" : customAccentHex, string.IsNullOrWhiteSpace(customSecondaryHex) ? "#38BDF8" : customSecondaryHex, enableDiagnosticLogs, enableWindowsDoh);
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
                        return new AppSettingsModel(theme, iface, log, dark, lang, surface, true, "1.1.1.1", "www.arvancloud.ir", false, 100, 1.0, 1.0, "#5865F2", "#38BDF8", false, false);
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

        public void WriteDiagnosticLog(string category, string message)
        {
            if (!EnableDiagnosticLogs) return;
            lock (_ioLock)
            {
                try
                {
                    Directory.CreateDirectory(_cfgDir);
                    if (File.Exists(DiagnosticLogFilePath))
                    {
                        var fi = new FileInfo(DiagnosticLogFilePath);
                        if (fi.Length > 1024 * 1024)
                        {
                            var tail = File.ReadAllLines(DiagnosticLogFilePath).TakeLast(400).ToArray();
                            File.WriteAllLines(DiagnosticLogFilePath, tail, new UTF8Encoding(false));
                        }
                    }
                    string line = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z [{SanitizeLogField(category)}] {SanitizeLogField(message)}{Environment.NewLine}";
                    File.AppendAllText(DiagnosticLogFilePath, line, new UTF8Encoding(false));
                }
                catch { }
            }
        }

        public async Task<(bool Success, string ZipPath, string ClipboardSummary, string Message)> ExportBugReportAsync(
            string destinationZipPath,
            string selectedInterface,
            string activeSettingsSummary)
        {
            try
            {
                var snap = await CaptureAdapterTelemetryAsync(selectedInterface).ConfigureAwait(false);
                var adaptors = await ScanSystemAdaptorsAsync().ConfigureAwait(false);

                var sb = new StringBuilder();
                sb.AppendLine("=== AionToolPro Diagnostic Bug Report ===");
                sb.AppendLine($"Generated (UTC) : {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z");
                sb.AppendLine($"App Version     : v{GetAppVersionString()}");
                sb.AppendLine($"OS Version      : {Environment.OSVersion} (Build {Environment.OSVersion.Version.Build})");
                sb.AppendLine($"Runtime         : {RuntimeInformation.FrameworkDescription} ({RuntimeInformation.ProcessArchitecture})");
                sb.AppendLine($"Install Mode    : {(IsInstalledCopy() ? "Installed" : "Portable")}");
                sb.AppendLine($"Settings        : {activeSettingsSummary}");
                sb.AppendLine();
                sb.AppendLine("=== Selected Adapter Snapshot (Sanitized) ===");
                sb.AppendLine($"Name            : {snap.InterfaceName}");
                sb.AppendLine($"Status          : {snap.OperationalStatus} (Up={snap.IsUp}, Link={snap.LinkSpeedLabel})");
                sb.AppendLine($"DNS Servers     : {(snap.DnsServers.Count > 0 ? string.Join(", ", snap.DnsServers) : "Automatic DHCP")}");
                sb.AppendLine($"Has Gateway     : {!string.IsNullOrWhiteSpace(snap.GatewayIp)}");
                sb.AppendLine($"NIC Drops       : InErr={snap.IncomingPacketsWithErrors}, InDisc={snap.IncomingPacketsDiscarded}, OutErr={snap.OutgoingPacketsWithErrors}, OutDisc={snap.OutgoingPacketsDiscarded}");
                sb.AppendLine();
                sb.AppendLine($"=== Clean Adapters Detected ({adaptors.Count}) ===");
                foreach (var a in adaptors)
                    sb.AppendLine($" - {a.Name} | Admin={a.AdminState} | Conn={a.ConnectionState} | ActiveRoute={a.IsActiveInternetRoute}");

                string sysInfoText = sb.ToString();

                await Task.Run(() =>
                {
                    if (File.Exists(destinationZipPath))
                        File.Delete(destinationZipPath);

                    using var archive = ZipFile.Open(destinationZipPath, ZipArchiveMode.Create);

                    void AddTextEntry(string entryName, string content)
                    {
                        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                        writer.Write(content);
                    }

                    AddTextEntry("system_info.txt", sysInfoText);

                    lock (_ioLock)
                    {
                        if (File.Exists(DiagnosticLogFilePath))
                            AddTextEntry("diagnostics.log", File.ReadAllText(DiagnosticLogFilePath));
                        if (File.Exists(LogFilePath))
                            AddTextEntry("actions.log", File.ReadAllText(LogFilePath));

                        string crashLog = Path.Combine(_cfgDir, "crash.log");
                        if (File.Exists(crashLog))
                            AddTextEntry("crash.log", File.ReadAllText(crashLog));
                    }
                }).ConfigureAwait(false);

                string clipboardSummary =
                    $"**AionToolPro Bug Report (v{GetAppVersionString()})**\n" +
                    $"- OS: Windows Build {Environment.OSVersion.Version.Build} ({RuntimeInformation.ProcessArchitecture})\n" +
                    $"- Adapter: {snap.InterfaceName} ({snap.OperationalStatus}, {snap.LinkSpeedLabel})\n" +
                    $"- DNS: {(snap.DnsServers.Count > 0 ? string.Join(", ", snap.DnsServers) : "DHCP")}\n" +
                    $"- Settings: {activeSettingsSummary}\n\n" +
                    $"*(Attach `{Path.GetFileName(destinationZipPath)}` for full logs)*";

                WriteActionLog("Diagnostics", $"OK Exported bug report bundle: {Path.GetFileName(destinationZipPath)}");
                return (true, destinationZipPath, clipboardSummary, $"Bug report saved successfully to:\n{destinationZipPath}");
            }
            catch (Exception ex)
            {
                return (false, string.Empty, string.Empty, $"Failed to generate bug report: {ex.Message}");
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
