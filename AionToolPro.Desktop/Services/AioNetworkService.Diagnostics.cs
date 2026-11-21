using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using AionToolPro.Desktop.Models;

namespace AionToolPro.Desktop.Services
{
    public sealed partial class AioNetworkService
    {
        public async Task<string> GetIpAndMacInfoAsync(string interfaceName, CancellationToken token = default)
        {
            interfaceName = ValidateInterfaceName(interfaceName);
            var sb = new StringBuilder();
            var ipRes = await ExecAsync("netsh", new[] { "interface", "ip", "show", "config", $"name={interfaceName}" }, token).ConfigureAwait(false);
            sb.AppendLine(ipRes.StandardOutput);
            sb.AppendLine("=== Physical MAC Address ===");
            var macRes = await ExecAsync("getmac", new[] { "/v", "/fo", "list" }, token).ConfigureAwait(false);
            sb.AppendLine(macRes.StandardOutput);
            return sb.ToString();
        }

        public async Task<(string Ip, string Country, string Org)> GetPublicIpInfoAsync(CancellationToken token = default)
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);

            using var req = new HttpRequestMessage(HttpMethod.Get, "https://ipinfo.io/json");
            using var resp = await SharedHttp.SendAsync(req, linked.Token).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            string json = await resp.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string ip = root.TryGetProperty("ip", out var ipEl) ? ipEl.GetString() ?? "" : "";
            string country = root.TryGetProperty("country", out var cEl) ? cEl.GetString() ?? "" : "";
            string org = root.TryGetProperty("org", out var oEl) ? oEl.GetString() ?? "" : "";

            return (ip.Trim(), country.Trim(), org.Trim());
        }

        // ==================== REAL RFC 5389 UDP STUN NAT ANALYZER ====================
        private static async Task<(IPAddress? MappedIp, int MappedPort)> QueryStunServerAsync(UdpClient udp, string host, int port, CancellationToken token)
        {
            try
            {
                var addrs = await Dns.GetHostAddressesAsync(host, token).ConfigureAwait(false);
                var ip = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                if (ip == null) return (null, 0);

                // RFC 5389 STUN Binding Request (20 bytes): Type=0x0001, Length=0x0000, MagicCookie=0x2112A442, 12-byte TxId
                byte[] req = new byte[20];
                req[0] = 0x00; req[1] = 0x01;
                req[2] = 0x00; req[3] = 0x00;
                req[4] = 0x21; req[5] = 0x12; req[6] = 0xA4; req[7] = 0x42;
                byte[] txId = RandomNumberGenerator.GetBytes(12);
                Buffer.BlockCopy(txId, 0, req, 8, 12);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                cts.CancelAfter(TimeSpan.FromSeconds(3));

                await udp.SendAsync(req.AsMemory(), new IPEndPoint(ip, port), cts.Token).ConfigureAwait(false);
                var result = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
                byte[] buf = result.Buffer;
                if (buf.Length < 20 || buf[0] != 0x01 || buf[1] != 0x01)
                    return (null, 0);

                int offset = 20;
                while (offset + 4 <= buf.Length)
                {
                    ushort attrType = (ushort)((buf[offset] << 8) | buf[offset + 1]);
                    ushort attrLen = (ushort)((buf[offset + 2] << 8) | buf[offset + 3]);
                    offset += 4;
                    if (offset + attrLen > buf.Length) break;

                    // 0x0020 = XOR-MAPPED-ADDRESS, 0x0001 = MAPPED-ADDRESS
                    if ((attrType == 0x0020 || attrType == 0x0001) && attrLen >= 8 && buf[offset + 1] == 0x01)
                    {
                        ushort rawPort = (ushort)((buf[offset + 2] << 8) | buf[offset + 3]);
                        int mappedPort = attrType == 0x0020 ? (rawPort ^ 0x2112) : rawPort;
                        byte[] ipBytes = new byte[4];
                        if (attrType == 0x0020)
                        {
                            ipBytes[0] = (byte)(buf[offset + 4] ^ 0x21);
                            ipBytes[1] = (byte)(buf[offset + 5] ^ 0x12);
                            ipBytes[2] = (byte)(buf[offset + 6] ^ 0xA4);
                            ipBytes[3] = (byte)(buf[offset + 7] ^ 0x42);
                        }
                        else
                        {
                            Buffer.BlockCopy(buf, offset + 4, ipBytes, 0, 4);
                        }
                        return (new IPAddress(ipBytes), mappedPort);
                    }

                    int padded = (attrLen + 3) & ~3;
                    offset += padded;
                }
            }
            catch { }

            return (null, 0);
        }

        public async Task<string> AnalyzePublicLocalIpStatusAsync(CancellationToken token = default)
        {
            Emit("[NAT / STUN] Running RFC 5389 UDP STUN test + Public/Local IP inspection...");
            var sb = new StringBuilder();
            try
            {
                // 1. Run dual STUN binding test from the SAME local UDP socket to detect Endpoint-Independent (Cone) vs Symmetric NAT
                IPAddress? stunIp1 = null, stunIp2 = null;
                int stunPort1 = 0, stunPort2 = 0;
                int localUdpPort = 0;

                try
                {
                    using var udp = new UdpClient(0);
                    localUdpPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
                    (stunIp1, stunPort1) = await QueryStunServerAsync(udp, "stun.l.google.com", 19302, token).ConfigureAwait(false);
                    (stunIp2, stunPort2) = await QueryStunServerAsync(udp, "stun1.l.google.com", 19302, token).ConfigureAwait(false);
                }
                catch { }

                // 2. Also fetch HTTP public IP metadata
                string pubIpStr = stunIp1?.ToString() ?? "";
                string country = "", org = "";
                try
                {
                    var info = await GetPublicIpInfoAsync(token).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(info.Ip)) pubIpStr = info.Ip;
                    country = info.Country;
                    org = info.Org;
                }
                catch { }

                var physicalLocalIps = new List<IPAddress>();
                var vpnCgNatIps = new List<string>();

                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces()
                             .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
                {
                    bool looksLikeTunnel = nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel
                        || nic.Name.Contains("Tailscale", StringComparison.OrdinalIgnoreCase)
                        || nic.Name.Contains("WireGuard", StringComparison.OrdinalIgnoreCase)
                        || nic.Description.Contains("TAP", StringComparison.OrdinalIgnoreCase)
                        || nic.Description.Contains("TUN", StringComparison.OrdinalIgnoreCase);

                    foreach (var ua in nic.GetIPProperties().UnicastAddresses
                                 .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
                    {
                        if (looksLikeTunnel && AioCatalog.IsCgNatIpv4(ua.Address))
                            vpnCgNatIps.Add($"{nic.Name}: {ua.Address}");
                        else
                            physicalLocalIps.Add(ua.Address);
                    }
                }

                sb.AppendLine($"Public IP                : {pubIpStr}{(string.IsNullOrEmpty(country) ? "" : $" ({country} — {org})")}");
                sb.AppendLine($"Local IPv4 Address(es)   : {string.Join(", ", physicalLocalIps)}");
                if (vpnCgNatIps.Count > 0)
                    sb.AppendLine($"VPN/Tunnel 100.64/10     : {string.Join(", ", vpnCgNatIps)} (excluded from ISP CGNAT)");

                if (stunIp1 != null)
                {
                    sb.AppendLine($"STUN #1 (Google:19302)   : {stunIp1}:{stunPort1}  (local UDP :{localUdpPort})");
                    if (stunIp2 != null)
                        sb.AppendLine($"STUN #2 (Google1:19302)  : {stunIp2}:{stunPort2}");
                }
                else
                {
                    sb.AppendLine("STUN UDP Test            : Blocked / No response on UDP 19302");
                }
                sb.AppendLine();

                IPAddress.TryParse(pubIpStr, out var pubIp);
                if (pubIp != null && physicalLocalIps.Any(ip => ip.Equals(pubIp)))
                {
                    sb.AppendLine("Result: Open / Direct Public IPv4 (NAT Type 1)");
                    sb.AppendLine("Your PC holds the public IP directly. Best possible connectivity for P2P gaming and hosting.");
                }
                else if ((pubIp != null && AioCatalog.IsCgNatIpv4(pubIp)) || physicalLocalIps.Any(AioCatalog.IsCgNatIpv4))
                {
                    sb.AppendLine("Result: Carrier-Grade NAT (RFC 6598 — 100.64.0.0/10)");
                    sb.AppendLine("Your ISP uses shared CGNAT. Inbound port-forwarding will not work without a static IP or VPN tunnel.");
                }
                else if (stunIp1 != null && stunIp2 != null)
                {
                    if (stunIp1.Equals(stunIp2) && stunPort1 == stunPort2)
                    {
                        sb.AppendLine("Result: Endpoint-Independent / Cone NAT (NAT Type 2 — Moderate/Good)");
                        sb.AppendLine("Your router preserves the same external port across destinations. UPnP, STUN hole-punching, and P2P gaming work well.");
                    }
                    else
                    {
                        sb.AppendLine("Result: Symmetric NAT (NAT Type 3 — Strict)");
                        sb.AppendLine($"Your router/ISP assigned different external ports ({stunPort1} vs {stunPort2}) for the same local socket. P2P voice/gaming may require TURN relays or port forwarding.");
                    }
                }
                else
                {
                    sb.AppendLine("Result: Behind Router NAT (STUN UDP unavailable)");
                    sb.AppendLine("Private LAN IP detected behind a router, and UDP STUN probes were blocked or timed out.");
                }

                string report = sb.ToString().TrimEnd();
                Emit("[NAT / STUN] Completed NAT & CGNAT analysis.");
                return report;
            }
            catch (Exception ex)
            {
                string err = $"[NAT / STUN ERR] {ex.Message}";
                Emit(err);
                return err;
            }
        }

        // ==================== GAME SERVER & GLOBAL PING MATRIX ====================
        public async Task<string> RunGameServerPingTestAsync()
        {
            using var lease = CreateTrackedOperationCts(TimeSpan.FromSeconds(15));
            var targets = new (string Name, string Host, int TcpFallback)[]
            {
                ("Steam (Valve)",        "steampowered.com",  443),
                ("Riot / Valorant EU",   "162.249.72.1",      443),
                ("Discord Gateway",      "discord.com",       443),
                ("Epic Games",           "epicgames.com",     443),
                ("Cloudflare Edge",      "1.1.1.1",           443),
                ("Google Global",        "8.8.8.8",           53),
                ("ArvanCloud (Iran)",    "www.arvancloud.ir", 443)
            };

            Emit("[GAME PING] Testing latency to major gaming & edge servers in parallel...");
            var tasks = targets.Select(async t =>
            {
                long? ms = await PingSingleHostFastAsync(t.Host, 1400, t.TcpFallback).ConfigureAwait(false);
                return (t.Name, t.Host, Ms: ms);
            });

            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            var sb = new StringBuilder();
            sb.AppendLine("=== Game & Edge Server Latency Check ===");
            sb.AppendLine($"{"Service",-22} {"Target",-20} {"Latency",-12} {"Quality"}");
            sb.AppendLine(new string('-', 68));
            foreach (var r in results.OrderBy(x => x.Ms ?? long.MaxValue))
            {
                string lat = r.Ms.HasValue ? $"{r.Ms.Value} ms" : "Timeout";
                string qual = !r.Ms.HasValue ? "Unreachable / Filtered"
                    : r.Ms.Value <= 50 ? "Excellent"
                    : r.Ms.Value <= 95 ? "Good"
                    : r.Ms.Value <= 150 ? "Fair"
                    : "High Latency";
                sb.AppendLine($"{r.Name,-22} {r.Host,-20} {lat,-12} {qual}");
            }

            string report = sb.ToString().TrimEnd();
            Emit("[GAME PING] Game server latency test complete.");
            return report;
        }

        // ==================== PARALLEL TIME-BOXED SPEED TEST (FIXED STALL TIMEOUT) ====================
        public async Task<string> RunSpeedTestAsync()
        {
            using var lease = CreateTrackedOperationCts(TimeSpan.FromSeconds(20));
            var token = lease.Token;

            Emit("[SPEED TEST] Starting 2-stream download test from Cloudflare CDN (max 8s / 25 MB)...");
            try
            {
                const int streamCount = 2;
                const int bytesPerStream = 12_500_000;
                const double maxSeconds = 8.0;
                long totalBytes = 0;
                var transferSw = new Stopwatch();
                object swLock = new();

                // FIX: Hard 10-second wall-clock cap on timeBoxCts so a stalled stream cancels immediately
                using var timeBoxCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeBoxCts.CancelAfter(TimeSpan.FromSeconds(10));

                var workers = Enumerable.Range(0, streamCount).Select(async _ =>
                {
                    try
                    {
                        string url = $"https://speed.cloudflare.com/__down?bytes={bytesPerStream}";
                        using var req = new HttpRequestMessage(HttpMethod.Get, url);
                        using var resp = await SharedHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeBoxCts.Token).ConfigureAwait(false);
                        resp.EnsureSuccessStatusCode();

                        using var stream = await resp.Content.ReadAsStreamAsync(timeBoxCts.Token).ConfigureAwait(false);
                        byte[] buffer = new byte[65536];
                        int read;
                        while ((read = await stream.ReadAsync(buffer.AsMemory(), timeBoxCts.Token).ConfigureAwait(false)) > 0)
                        {
                            lock (swLock)
                            {
                                if (!transferSw.IsRunning)
                                    transferSw.Start();
                            }
                            Interlocked.Add(ref totalBytes, read);
                            if (transferSw.Elapsed.TotalSeconds >= maxSeconds)
                                break;
                        }
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        // Normal time-box cap reached
                    }
                });

                await Task.WhenAll(workers).ConfigureAwait(false);
                transferSw.Stop();

                if (token.IsCancellationRequested)
                {
                    string msg = "[SPEED TEST] Cancelled by user.";
                    Emit(msg);
                    return msg;
                }

                if (totalBytes == 0)
                {
                    string err = "[SPEED TEST] Timed out without receiving data (Cloudflare CDN may be unreachable).";
                    Emit(err);
                    return err;
                }

                double seconds = Math.Max(0.1, transferSw.Elapsed.TotalSeconds);
                double mbps = (totalBytes * 8.0) / 1_000_000.0 / seconds;
                double mbytes = totalBytes / 1_048_576.0;

                string result = $"[SPEED TEST] Downloaded {mbytes:F1} MB in {seconds:F1}s (2 streams). Speed: {mbps:F2} Mbps ({mbps / 8.0:F2} MB/s)";
                Emit(result);
                return result;
            }
            catch (OperationCanceledException)
            {
                string msg = "[SPEED TEST] Cancelled by user.";
                Emit(msg);
                return msg;
            }
            catch (Exception ex)
            {
                string err = $"[SPEED TEST ERR] {ex.Message}";
                Emit(err);
                return err;
            }
        }

        // ==================== PING, TRACERT, NSLOOKUP, PORT, DATA USAGE ====================
        public async Task<string> RunPingAsync(string target, int count = 10, Action<string>? onLine = null)
        {
            if (!ValidateHostOrIp(target))
                throw new ArgumentException("Invalid host or IP address.");

            count = Math.Clamp(count, 1, 200);
            using var lease = CreateTrackedOperationCts(TimeSpan.FromMinutes(5));
            var res = await ExecAsync("ping", new[] { "-n", count.ToString(), target }, lease.Token, onLine: onLine, timeout: TimeSpan.FromMinutes(5)).ConfigureAwait(false);
            return res.WasCancelled ? res.StandardOutput + "\r\n[STOPPED] Ping cancelled by user." : res.StandardOutput;
        }

        public async Task<string> RunNslookupAsync(string domain, Action<string>? onLine = null)
        {
            if (!ValidateHostOrIp(domain))
                throw new ArgumentException("Invalid domain or IP address.");

            using var lease = CreateTrackedOperationCts(TimeSpan.FromSeconds(15));
            var res = await ExecAsync("nslookup", new[] { "-timeout=3", domain }, lease.Token, onLine: onLine, timeout: TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(res.StandardOutput) ? res.StandardError : res.StandardOutput;
        }

        public async Task<string> RunTracerouteAsync(string target, Action<string>? onLine = null)
        {
            if (!ValidateHostOrIp(target))
                throw new ArgumentException("Invalid host or IP address.");

            using var lease = CreateTrackedOperationCts(TimeSpan.FromMinutes(3));
            var res = await ExecAsync("tracert", new[] { "-d", "-h", "20", "-w", "1000", target }, lease.Token, onLine: onLine, timeout: TimeSpan.FromMinutes(3)).ConfigureAwait(false);
            return res.WasCancelled ? res.StandardOutput + "\r\n[STOPPED] Traceroute cancelled by user." : res.StandardOutput;
        }

        public async Task<bool> CheckTcpPortAsync(string host, int port, int timeoutMs = 3000)
        {
            if (!ValidateHostOrIp(host) || port < 1 || port > 65535) return false;
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(timeoutMs);
            try
            {
                await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
                return client.Connected;
            }
            catch
            {
                return false;
            }
        }

        public async Task<string> GetActiveTcpConnectionsReportAsync(CancellationToken token = default)
        {
            var res = await ExecAsync(
                "powershell",
                new[]
                {
                    "-NoProfile",
                    "-Command",
                    "Get-NetTCPConnection -State Established | Select-Object LocalAddress,LocalPort,RemoteAddress,RemotePort,@{n='Process';e={(Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue).ProcessName}} | Sort-Object Process | Format-Table -AutoSize | Out-String -Width 120"
                },
                token,
                timeout: TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(res.StandardOutput) ? res.StandardError : res.StandardOutput;
        }

        public Task<string> GetAdapterDataUsageReportAsync()
        {
            return Task.Run(() =>
            {
                var sb = new StringBuilder();
                sb.AppendLine($"{"Adaptor Name",-32} {"Status",-15} {"Received MiB",-16} {"Sent MiB",-16}");
                sb.AppendLine(new string('-', 82));

                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces()
                             .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
                {
                    try
                    {
                        var stats = nic.GetIPStatistics();
                        double rx = Math.Round(stats.BytesReceived / 1048576.0, 1);
                        double tx = Math.Round(stats.BytesSent / 1048576.0, 1);
                        sb.AppendLine($"{nic.Name,-32} {nic.OperationalStatus,-15} {rx,-16:N1} {tx,-16:N1}");
                    }
                    catch
                    {
                        sb.AppendLine($"{nic.Name,-32} {nic.OperationalStatus,-15} {"N/A",-16} {"N/A",-16}");
                    }
                }
                return sb.ToString();
            });
        }

        // ==================== ACTIVE CONNECTIONS BY APP (IPv4 + IPv6 Win32 IP Helper API) ====================
        private const int AF_INET = 2;
        private const int AF_INET6 = 23;
        private const int TCP_TABLE_OWNER_PID_ALL = 5;

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(
            IntPtr pTcpTable,
            ref int dwOutBufLen,
            bool sort,
            int ipVersion,
            int tblClass,
            uint reserved = 0);

        public Task<List<AppNetworkUsageRow>> GetPerAppNetworkUsageAsync()
        {
            return Task.Run(() =>
            {
                var byPid = new Dictionary<int, List<string>>();
                ReadTcpTable(AF_INET, byPid);
                ReadTcpTable(AF_INET6, byPid);

                var rows = new List<AppNetworkUsageRow>(byPid.Count);
                DateTime now = DateTime.UtcNow;

                foreach (var kvp in byPid)
                {
                    int pid = kvp.Key;
                    var endpoints = kvp.Value;

                    string procName = $"PID-{pid}";
                    string exePath = "System / Protected";
                    double memMb = 0;

                    try
                    {
                        using var proc = Process.GetProcessById(pid);
                        procName = proc.ProcessName;
                        memMb = Math.Round(proc.WorkingSet64 / 1048576.0, 1);

                        if (_pidCache.TryGetValue(pid, out var cached) &&
                            cached.Name.Equals(procName, StringComparison.OrdinalIgnoreCase) &&
                            (now - cached.CachedUtc).TotalSeconds < 30)
                        {
                            exePath = cached.Path;
                        }
                        else
                        {
                            try
                            {
                                exePath = proc.MainModule?.FileName ?? $"{procName}.exe";
                            }
                            catch
                            {
                                exePath = "Protected / Access Denied";
                            }
                            _pidCache[pid] = (procName, exePath, now);
                        }
                    }
                    catch
                    {
                        _pidCache.TryRemove(pid, out _);
                    }

                    var distinct = endpoints.Distinct().ToList();
                    string sampleRemote = string.Join(" , ", distinct.Take(3));
                    if (distinct.Count > 3)
                        sampleRemote += $" (+{distinct.Count - 3} more)";

                    rows.Add(new AppNetworkUsageRow(
                        procName,
                        pid,
                        endpoints.Count,
                        memMb,
                        sampleRemote,
                        exePath));
                }

                if (_pidCache.Count > 300)
                {
                    foreach (var k in _pidCache.Keys.ToList())
                    {
                        if (_pidCache.TryGetValue(k, out var val) && (now - val.CachedUtc).TotalMinutes > 2)
                            _pidCache.TryRemove(k, out _);
                    }
                }

                return rows
                    .OrderByDescending(r => r.ActiveConnections)
                    .ThenByDescending(r => r.MemoryMb)
                    .ToList();
            });
        }

        private static void ReadTcpTable(int ipVersion, Dictionary<int, List<string>> byPid)
        {
            int bufferSize = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref bufferSize, false, ipVersion, TCP_TABLE_OWNER_PID_ALL);
            if (bufferSize <= 0) return;

            IntPtr tablePtr = IntPtr.Zero;
            uint ret = 122;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                bufferSize += 4096;
                tablePtr = Marshal.AllocHGlobal(bufferSize);
                ret = GetExtendedTcpTable(tablePtr, ref bufferSize, false, ipVersion, TCP_TABLE_OWNER_PID_ALL);
                if (ret == 0) break;
                Marshal.FreeHGlobal(tablePtr);
                tablePtr = IntPtr.Zero;
                if (ret != 122) break;
            }

            if (tablePtr == IntPtr.Zero || ret != 0) return;

            try
            {
                int numEntries = Marshal.ReadInt32(tablePtr);
                IntPtr rowPtr = IntPtr.Add(tablePtr, 4);

                if (ipVersion == AF_INET)
                {
                    const int rowSize = 24;
                    for (int i = 0; i < numEntries; i++)
                    {
                        int state = Marshal.ReadInt32(rowPtr, 0);
                        int pid = Marshal.ReadInt32(rowPtr, 20);
                        if (pid > 4 && (state == 5 || state == 2 || state == 8))
                        {
                            uint remoteAddr = (uint)Marshal.ReadInt32(rowPtr, 12);
                            var ip = new IPAddress(remoteAddr);
                            if (!IPAddress.IsLoopback(ip) && !IPAddress.Any.Equals(ip))
                            {
                                byte pHi = Marshal.ReadByte(rowPtr, 16);
                                byte pLo = Marshal.ReadByte(rowPtr, 17);
                                int remotePort = (pHi << 8) | pLo;
                                if (!byPid.TryGetValue(pid, out var list))
                                {
                                    list = new List<string>();
                                    byPid[pid] = list;
                                }
                                list.Add($"{ip}:{remotePort}");
                            }
                        }
                        rowPtr = IntPtr.Add(rowPtr, rowSize);
                    }
                }
                else
                {
                    const int rowSize = 56;
                    byte[] addrBuf = new byte[16];
                    for (int i = 0; i < numEntries; i++)
                    {
                        int state = Marshal.ReadInt32(rowPtr, 48);
                        int pid = Marshal.ReadInt32(rowPtr, 52);
                        if (pid > 4 && (state == 5 || state == 2 || state == 8))
                        {
                            Marshal.Copy(IntPtr.Add(rowPtr, 24), addrBuf, 0, 16);
                            var ip = new IPAddress(addrBuf);
                            if (!IPAddress.IPv6Loopback.Equals(ip) && !IPAddress.IPv6None.Equals(ip) && !IPAddress.IPv6Any.Equals(ip))
                            {
                                byte pHi = Marshal.ReadByte(rowPtr, 44);
                                byte pLo = Marshal.ReadByte(rowPtr, 45);
                                int remotePort = (pHi << 8) | pLo;
                                if (!byPid.TryGetValue(pid, out var list))
                                {
                                    list = new List<string>();
                                    byPid[pid] = list;
                                }
                                list.Add($"[{ip}]:{remotePort}");
                            }
                        }
                        rowPtr = IntPtr.Add(rowPtr, rowSize);
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(tablePtr);
            }
        }

        // ==================== PROCESS TERMINATION & HASHED FIREWALL RULES ====================
        public static bool IsProtectedProcess(string processName) => ProtectedProcesses.Contains(processName);

        public OperationResult KillAppProcessById(int pid, string processName)
        {
            if (pid <= 4 || pid == Environment.ProcessId || IsProtectedProcess(processName))
            {
                string blockedMsg = $"Blocked: '{processName}' (PID {pid}) is a protected system process and cannot be terminated.";
                Emit($"[APP MONITOR] {blockedMsg}");
                return new OperationResult(false, blockedMsg, -1);
            }

            try
            {
                using var proc = Process.GetProcessById(pid);
                if (IsProtectedProcess(proc.ProcessName))
                    return new OperationResult(false, $"Blocked: '{proc.ProcessName}' is protected.", -1);

                proc.Kill(entireProcessTree: true);
                Emit($"[APP MONITOR] Terminated process {processName} (PID: {pid}).");
                WriteActionLog("AppMonitor", $"OK Killed process {processName} (PID {pid})");
                return new OperationResult(true, $"Terminated {processName} (PID {pid}).");
            }
            catch (Exception ex)
            {
                string err = $"Could not kill {processName}: {ex.Message}";
                Emit($"[APP MONITOR ERR] {err}");
                return new OperationResult(false, err, -1);
            }
        }

        private static string ComputePathHashRuleName(string exePath)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(exePath).ToLowerInvariant()));
            return $"AionToolPro_Block_{Convert.ToHexString(hash)[..16]}";
        }

        public List<FirewallBlockedApp> GetBlockedApps()
        {
            lock (_ioLock)
            {
                try
                {
                    if (!File.Exists(BlockedAppsFilePath)) return new List<FirewallBlockedApp>();
                    return JsonSerializer.Deserialize<List<FirewallBlockedApp>>(File.ReadAllText(BlockedAppsFilePath)) ?? new List<FirewallBlockedApp>();
                }
                catch
                {
                    return new List<FirewallBlockedApp>();
                }
            }
        }

        private void SaveBlockedApps(List<FirewallBlockedApp> apps)
        {
            lock (_ioLock)
            {
                try
                {
                    Directory.CreateDirectory(_cfgDir);
                    File.WriteAllText(BlockedAppsFilePath, JsonSerializer.Serialize(apps, JsonOpts), new UTF8Encoding(false));
                }
                catch { }
            }
        }

        public async Task<OperationResult> BlockAppInFirewallAsync(string exePath, string processName, CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                return new OperationResult(false, $"Executable file path is not accessible for '{processName}'.", -1);

            string fullPath = Path.GetFullPath(exePath);
            string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (fullPath.StartsWith(winDir, StringComparison.OrdinalIgnoreCase))
            {
                string err = $"Refused to block Windows system binary: {fullPath}";
                Emit($"[FIREWALL] {err}");
                return new OperationResult(false, err, -1);
            }

            string ruleBase = ComputePathHashRuleName(fullPath);
            await ExecAsync("netsh", new[] { "advfirewall", "firewall", "delete", "rule", $"name={ruleBase}_Out" }, token, silent: true).ConfigureAwait(false);
            await ExecAsync("netsh", new[] { "advfirewall", "firewall", "delete", "rule", $"name={ruleBase}_In" }, token, silent: true).ConfigureAwait(false);

            var rOut = await ExecAsync("netsh", new[]
            {
                "advfirewall", "firewall", "add", "rule",
                $"name={ruleBase}_Out", "dir=out", "action=block", $"program={fullPath}", "enable=yes"
            }, token).ConfigureAwait(false);

            var rIn = await ExecAsync("netsh", new[]
            {
                "advfirewall", "firewall", "add", "rule",
                $"name={ruleBase}_In", "dir=in", "action=block", $"program={fullPath}", "enable=yes"
            }, token).ConfigureAwait(false);

            if (!rOut.IsSuccess || !rIn.IsSuccess)
            {
                WriteActionLog("Firewall", $"FAILED Block app {processName} (out={rOut.ExitCode}, in={rIn.ExitCode})");
                return new OperationResult(false, $"netsh failed to add firewall block rule for {processName}.", rOut.ExitCode != 0 ? rOut.ExitCode : rIn.ExitCode);
            }

            var blocked = GetBlockedApps();
            blocked.RemoveAll(b => b.RuleBaseName.Equals(ruleBase, StringComparison.OrdinalIgnoreCase));
            blocked.Add(new FirewallBlockedApp(ruleBase, processName, fullPath, DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")));
            SaveBlockedApps(blocked);

            Emit($"[FIREWALL] Blocked internet access for {processName} ({fullPath}).");
            WriteActionLog("Firewall", $"OK Blocked app {processName} [{ruleBase}]");
            return new OperationResult(true, $"Blocked {processName} in Windows Firewall.");
        }

        public async Task<OperationResult> UnblockAppInFirewallAsync(string? exePath, string processName, string? ruleBaseOverride = null, CancellationToken token = default)
        {
            string ruleBase = !string.IsNullOrWhiteSpace(ruleBaseOverride)
                ? ruleBaseOverride
                : (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath)
                    ? ComputePathHashRuleName(exePath)
                    : $"AionToolPro_Block_{processName}");

            var rOut = await ExecAsync("netsh", new[] { "advfirewall", "firewall", "delete", "rule", $"name={ruleBase}_Out" }, token).ConfigureAwait(false);
            var rIn = await ExecAsync("netsh", new[] { "advfirewall", "firewall", "delete", "rule", $"name={ruleBase}_In" }, token).ConfigureAwait(false);

            if (!ruleBase.Equals($"AionToolPro_Block_{processName}", StringComparison.OrdinalIgnoreCase))
            {
                await ExecAsync("netsh", new[] { "advfirewall", "firewall", "delete", "rule", $"name=AionToolPro_Block_{processName}_Out" }, token, silent: true).ConfigureAwait(false);
                await ExecAsync("netsh", new[] { "advfirewall", "firewall", "delete", "rule", $"name=AionToolPro_Block_{processName}_In" }, token, silent: true).ConfigureAwait(false);
            }

            var blocked = GetBlockedApps();
            blocked.RemoveAll(b =>
                b.RuleBaseName.Equals(ruleBase, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(exePath) && b.ExecutablePath.Equals(exePath, StringComparison.OrdinalIgnoreCase)));
            SaveBlockedApps(blocked);

            bool ok = rOut.IsSuccess || rIn.IsSuccess;
            Emit($"[FIREWALL] Removed firewall block rules for {processName}.");
            WriteActionLog("Firewall", $"{(ok ? "OK" : "INFO")} Unblocked app {processName} [{ruleBase}]");
            return new OperationResult(true, $"Unblocked {processName}.");
        }

        // ==================== WI-FI ====================
        public async Task<string> GetWifiStatusAsync(CancellationToken token = default)
        {
            var res = await ExecAsync("netsh", new[] { "wlan", "show", "interfaces" }, token).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(res.StandardOutput) ? res.StandardError : res.StandardOutput;
        }

        public async Task<List<WifiCredentialItem>> GetSavedWifiPasswordsAsync(CancellationToken token = default)
        {
            var result = new List<WifiCredentialItem>();
            string tempXmlDir = Path.Combine(Path.GetTempPath(), "aion_wifi_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempXmlDir);

            try
            {
                await ExecAsync("netsh", new[] { "wlan", "export", "profile", "key=clear", $"folder={tempXmlDir}" }, token, silent: true, timeout: TimeSpan.FromSeconds(25)).ConfigureAwait(false);

                foreach (var xmlFile in Directory.GetFiles(tempXmlDir, "*.xml"))
                {
                    try
                    {
                        var doc = XDocument.Load(xmlFile);
                        string? ssid = doc.Descendants()
                            .FirstOrDefault(e => e.Name.LocalName.Equals("name", StringComparison.OrdinalIgnoreCase))?.Value?.Trim();
                        if (string.IsNullOrWhiteSpace(ssid)) continue;

                        string auth = doc.Descendants()
                            .FirstOrDefault(e => e.Name.LocalName.Equals("authentication", StringComparison.OrdinalIgnoreCase))?.Value?.Trim() ?? "";
                        string? keyMat = doc.Descendants()
                            .FirstOrDefault(e => e.Name.LocalName.Equals("keyMaterial", StringComparison.OrdinalIgnoreCase))?.Value;

                        if (!string.IsNullOrEmpty(keyMat))
                            result.Add(new WifiCredentialItem(ssid, keyMat, "Secured (Key Available)"));
                        else if (auth.Equals("open", StringComparison.OrdinalIgnoreCase))
                            result.Add(new WifiCredentialItem(ssid, null, "Open Network (No Password)"));
                        else
                            result.Add(new WifiCredentialItem(ssid, null, $"Not Available ({(string.IsNullOrEmpty(auth) ? "Protected / Enterprise" : auth)})"));
                    }
                    catch { }
                }
            }
            finally
            {
                SecureDeleteDirectory(tempXmlDir);
            }

            result = result.OrderBy(r => r.Ssid, StringComparer.OrdinalIgnoreCase).ToList();
            Emit($"[WI-FI] Extracted {result.Count} saved Wi-Fi profile(s) via secure temporary XML (wiped immediately).");
            return result;
        }

        private static void SecureDeleteDirectory(string dirPath)
        {
            try
            {
                if (!Directory.Exists(dirPath)) return;
                foreach (var file in Directory.GetFiles(dirPath, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        long len = new FileInfo(file).Length;
                        if (len > 0 && len < 1024 * 1024)
                            File.WriteAllBytes(file, new byte[len]);
                        File.Delete(file);
                    }
                    catch { }
                }
                Directory.Delete(dirPath, true);
            }
            catch { }
        }

        public async Task<OperationResult> ForgetWifiNetworkAsync(string ssid, string interfaceName, CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(ssid))
                return new OperationResult(false, "SSID cannot be empty.");

            var res = await ExecAsync("netsh", new[] { "wlan", "delete", "profile", $"name={ssid}" }, token).ConfigureAwait(false);
            WriteActionLog(interfaceName, $"{(res.IsSuccess ? "OK" : "FAILED")} Forgot Wi-Fi {ssid}");
            return new OperationResult(res.IsSuccess, res.IsSuccess ? $"Forgot Wi-Fi '{ssid}'." : res.StandardOutput, res.ExitCode);
        }

        // ==================== STATIC IP ====================
        public async Task<OperationResult> ApplyStaticIpAsync(string interfaceName, string ip, string subnetMask, string? gateway, CancellationToken token = default)
        {
            interfaceName = ValidateInterfaceName(interfaceName);
            if (!ValidateIpv4(ip))
                return new OperationResult(false, "Invalid IPv4 address.");
            if (!IsValidSubnetMask(subnetMask))
                return new OperationResult(false, "Invalid or non-contiguous IPv4 subnet mask (e.g. 255.255.255.0).");
            if (!string.IsNullOrWhiteSpace(gateway) && !ValidateIpv4(gateway))
                return new OperationResult(false, "Invalid IPv4 gateway address.");

            var args = new List<string>
            {
                "interface", "ip", "set", "address",
                $"name={interfaceName}", "source=static", $"address={ip}", $"mask={subnetMask}"
            };
            if (!string.IsNullOrWhiteSpace(gateway))
                args.Add($"gateway={gateway}");

            var res = await ExecAsync("netsh", args, token).ConfigureAwait(false);
            WriteActionLog(interfaceName, $"{(res.IsSuccess ? "OK" : "FAILED")} Static IP {ip} mask {subnetMask} gw {gateway ?? "-"}");
            return new OperationResult(res.IsSuccess, res.IsSuccess ? "Static IP applied." : res.StandardOutput, res.ExitCode);
        }

        public async Task<OperationResult> SwitchIpToDhcpAsync(string interfaceName, CancellationToken token = default)
        {
            interfaceName = ValidateInterfaceName(interfaceName);
            var res = await ExecAsync("netsh", new[] { "interface", "ip", "set", "address", $"name={interfaceName}", "source=dhcp" }, token).ConfigureAwait(false);
            await ExecAsync("ipconfig", new[] { "/renew", interfaceName }, token, silent: true).ConfigureAwait(false);
            WriteActionLog(interfaceName, $"{(res.IsSuccess ? "OK" : "FAILED")} IP set to DHCP");
            return new OperationResult(res.IsSuccess, res.IsSuccess ? "IPv4 switched to automatic DHCP." : res.StandardOutput, res.ExitCode);
        }
    }
}
