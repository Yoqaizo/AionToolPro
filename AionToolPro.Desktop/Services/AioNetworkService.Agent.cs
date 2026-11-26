using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AionToolPro.Desktop.Models;

namespace AionToolPro.Desktop.Services
{
    public sealed partial class AioNetworkService
    {
        // Known Iranian ISP DNS censorship / sinkhole IPs
        private static readonly HashSet<string> KnownSinkholeIps = new(StringComparer.OrdinalIgnoreCase)
        {
            "10.10.34.34", "10.10.34.35", "10.10.34.36", "10.10.34.37",
            "0.0.0.0", "127.0.0.1"
        };

        private static readonly string[] KnownHttpBlockSignatures =
        {
            "10.10.34.", "peyvandha.ir", "internet.ir", "blocked by"
        };

        public static string NormalizeTargetDomain(string? rawInput)
        {
            if (string.IsNullOrWhiteSpace(rawInput)) return string.Empty;
            string s = rawInput.Trim();
            if (s.Contains("://", StringComparison.Ordinal))
            {
                if (Uri.TryCreate(s, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
                    s = uri.Host;
            }
            int slash = s.IndexOfAny(new[] { '/', '?', '#' });
            if (slash > 0) s = s[..slash];
            int colon = s.IndexOf(':');
            if (colon > 0) s = s[..colon];
            s = s.Trim().Trim('.');
            return ValidateHostOrIp(s) ? s.ToLowerInvariant() : string.Empty;
        }

        private static bool IsExplicitHttpScheme(string? rawInput) =>
            !string.IsNullOrWhiteSpace(rawInput) &&
            rawInput.TrimStart().StartsWith("http://", StringComparison.OrdinalIgnoreCase);

        private static bool IsSinkholeOrBogusIp(IPAddress ip)
        {
            string s = ip.ToString();
            if (KnownSinkholeIps.Contains(s)) return true;
            byte[] b = ip.GetAddressBytes();
            if (b.Length != 4) return true;
            // Reject 0.x, 127.x, link-local 169.254.x, multicast >=224
            if (b[0] == 0 || b[0] == 127 || (b[0] == 169 && b[1] == 254) || b[0] >= 224) return true;
            // Reject 10.10.34.x ISP block range explicitly while still allowing anti-sanction 10.202.x.x if routed
            if (b[0] == 10 && b[1] == 10 && b[2] == 34) return true;
            return false;
        }

        /// <summary>
        /// Sends a native RFC 1035 UDP/53 A-record query to a specific DNS server (optionally bound to a specific adapter IPv4)
        /// and parses real IPv4 answers with full DNS label compression (0xC0) support.
        /// </summary>
        public static async Task<(long? LatencyMs, List<IPAddress> Addresses)> ResolveDomainViaDnsServerAsync(
            string dnsServerIp,
            string domain,
            int timeoutMs = 1800,
            CancellationToken token = default,
            IPAddress? bindLocalIp = null)
        {
            var results = new List<IPAddress>();
            if (!IPAddress.TryParse(dnsServerIp, out var serverIp))
                return (null, results);

            if (IPAddress.TryParse(domain, out var directIp))
            {
                results.Add(directIp);
                return (1, results);
            }

            byte[] query = BuildDnsAQueryPacket(domain, out ushort txId);
            try
            {
                using var udp = (bindLocalIp != null && bindLocalIp.AddressFamily == serverIp.AddressFamily)
                    ? new UdpClient(new IPEndPoint(bindLocalIp, 0))
                    : new UdpClient(serverIp.AddressFamily);
                var endpoint = new IPEndPoint(serverIp, 53);

                using var timeoutCts = new CancellationTokenSource(timeoutMs);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);

                var sw = Stopwatch.StartNew();
                await udp.SendAsync(query.AsMemory(), endpoint, linked.Token).ConfigureAwait(false);
                var resp = await udp.ReceiveAsync(linked.Token).ConfigureAwait(false);
                sw.Stop();

                byte[] buf = resp.Buffer;
                if (buf.Length < 12) return (null, results);

                ushort respTxId = (ushort)((buf[0] << 8) | buf[1]);
                bool isResponse = (buf[2] & 0x80) != 0;
                int rcode = buf[3] & 0x0F;
                if (respTxId != txId || !isResponse || rcode != 0)
                    return (null, results);

                int qdCount = (buf[4] << 8) | buf[5];
                int anCount = (buf[6] << 8) | buf[7];
                int offset = 12;

                for (int q = 0; q < qdCount && offset < buf.Length; q++)
                {
                    offset = SkipDnsName(buf, offset);
                    offset += 4;
                }

                for (int a = 0; a < anCount && offset + 10 <= buf.Length; a++)
                {
                    offset = SkipDnsName(buf, offset);
                    if (offset + 10 > buf.Length) break;

                    ushort rrType = (ushort)((buf[offset] << 8) | buf[offset + 1]);
                    ushort rrClass = (ushort)((buf[offset + 2] << 8) | buf[offset + 3]);
                    ushort rdLen = (ushort)((buf[offset + 8] << 8) | buf[offset + 9]);
                    offset += 10;

                    if (offset + rdLen > buf.Length) break;

                    if (rrType == 1 && rrClass == 1 && rdLen == 4)
                    {
                        var ip = new IPAddress(new ReadOnlySpan<byte>(buf, offset, 4));
                        if (!results.Contains(ip))
                            results.Add(ip);
                    }
                    offset += rdLen;
                }

                return (sw.ElapsedMilliseconds, results);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return (null, results);
            }
        }

        private static int SkipDnsName(byte[] buf, int offset)
        {
            int guard = 0;
            while (offset < buf.Length && guard++ < 128)
            {
                byte len = buf[offset];
                if (len == 0) return offset + 1;
                if ((len & 0xC0) == 0xC0) return offset + 2;
                offset += 1 + len;
            }
            return buf.Length;
        }

        private readonly record struct SiteBypassProbeOutcome(
            bool Connected,
            long? LatencyMs,
            int? HttpStatus,
            string Protocol,
            bool CertMismatch,
            bool HttpBlockPage);

        private static int? ParseHttpStatusCode(string responseSnippet)
        {
            if (string.IsNullOrEmpty(responseSnippet) || !responseSnippet.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase))
                return null;
            int firstSpace = responseSnippet.IndexOf(' ');
            if (firstSpace > 0 && firstSpace + 4 <= responseSnippet.Length &&
                int.TryParse(responseSnippet.AsSpan(firstSpace + 1, 3), out int code))
                return code;
            return null;
        }

        private static bool ContainsHttpBlockSignature(string responseSnippet)
        {
            if (string.IsNullOrEmpty(responseSnippet)) return false;
            return KnownHttpBlockSignatures.Any(sig => responseSnippet.Contains(sig, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Verifies real HTTPS (Port 443 with strict TLS certificate validation) OR HTTP (Port 80 fallback)
        /// directly against a candidate resolved IP so both HTTPS and HTTP-only sites work cleanly,
        /// while ISP MitM certs and HTTP censorship block pages are rejected.
        /// </summary>
        private static async Task<SiteBypassProbeOutcome> VerifySiteBypassOnIpAsync(
            IPAddress resolvedIp,
            string targetHost,
            int timeoutMs,
            bool preferHttpFirst,
            IPAddress? bindLocalIp,
            CancellationToken token)
        {
            if (preferHttpFirst)
            {
                var httpFirst = await ProbePlainHttpPort80Async(resolvedIp, targetHost, Math.Min(2000, timeoutMs), bindLocalIp, token).ConfigureAwait(false);
                if (httpFirst.Connected && !httpFirst.HttpBlockPage)
                    return httpFirst;
                var httpsBackup = await ProbeHttpsPort443Async(resolvedIp, targetHost, timeoutMs, bindLocalIp, token).ConfigureAwait(false);
                return httpsBackup.Connected ? httpsBackup : httpFirst;
            }

            var httpsRes = await ProbeHttpsPort443Async(resolvedIp, targetHost, timeoutMs, bindLocalIp, token).ConfigureAwait(false);
            if (httpsRes.Connected)
                return httpsRes;

            // If a MitM / invalid certificate was served on 443, do not downgrade to plain HTTP on a hijacked IP
            if (httpsRes.CertMismatch)
                return httpsRes;

            // Automatic HTTP (Port 80) fallback for HTTP-only sites!
            var httpRes = await ProbePlainHttpPort80Async(resolvedIp, targetHost, Math.Min(1800, timeoutMs), bindLocalIp, token).ConfigureAwait(false);
            return httpRes.Connected ? httpRes : httpsRes;
        }

        private static async Task<SiteBypassProbeOutcome> ProbeHttpsPort443Async(
            IPAddress resolvedIp,
            string targetHost,
            int timeoutMs,
            IPAddress? bindLocalIp,
            CancellationToken token)
        {
            using var timeoutCts = new CancellationTokenSource(timeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);
            var ct = linked.Token;

            bool certFailed = false;
            var sw = Stopwatch.StartNew();
            try
            {
                using var tcp = (bindLocalIp != null && bindLocalIp.AddressFamily == resolvedIp.AddressFamily)
                    ? new TcpClient(new IPEndPoint(bindLocalIp, 0)) { NoDelay = true }
                    : new TcpClient(resolvedIp.AddressFamily) { NoDelay = true };

                await tcp.ConnectAsync(resolvedIp, 443, ct).ConfigureAwait(false);

                using var ssl = new SslStream(
                    tcp.GetStream(),
                    leaveInnerStreamOpen: false,
                    userCertificateValidationCallback: (_, _, _, errors) =>
                    {
                        if (errors == SslPolicyErrors.None) return true;
                        certFailed = true;
                        return false;
                    });

                var sslOpts = new SslClientAuthenticationOptions
                {
                    TargetHost = targetHost,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                };

                await ssl.AuthenticateAsClientAsync(sslOpts, ct).ConfigureAwait(false);

                int? statusCode = await SendHttpProbeOverStreamAsync(ssl, targetHost, "HEAD", ct).ConfigureAwait(false);
                if (statusCode == 405)
                {
                    // Site rejects HEAD on root; verify cleanly with lightweight GET
                    using var tcpGet = (bindLocalIp != null && bindLocalIp.AddressFamily == resolvedIp.AddressFamily)
                        ? new TcpClient(new IPEndPoint(bindLocalIp, 0)) { NoDelay = true }
                        : new TcpClient(resolvedIp.AddressFamily) { NoDelay = true };
                    await tcpGet.ConnectAsync(resolvedIp, 443, ct).ConfigureAwait(false);
                    using var sslGet = new SslStream(tcpGet.GetStream(), false, (_, _, _, e) => e == SslPolicyErrors.None);
                    await sslGet.AuthenticateAsClientAsync(sslOpts, ct).ConfigureAwait(false);
                    statusCode = await SendHttpProbeOverStreamAsync(sslGet, targetHost, "GET", ct).ConfigureAwait(false);
                }

                sw.Stop();
                return new SiteBypassProbeOutcome(true, sw.ElapsedMilliseconds, statusCode, "HTTPS", false, false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return new SiteBypassProbeOutcome(false, null, null, "HTTPS", certFailed, false);
            }
        }

        private static async Task<SiteBypassProbeOutcome> ProbePlainHttpPort80Async(
            IPAddress resolvedIp,
            string targetHost,
            int timeoutMs,
            IPAddress? bindLocalIp,
            CancellationToken token)
        {
            using var timeoutCts = new CancellationTokenSource(timeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);
            var ct = linked.Token;

            var sw = Stopwatch.StartNew();
            try
            {
                using var tcp = (bindLocalIp != null && bindLocalIp.AddressFamily == resolvedIp.AddressFamily)
                    ? new TcpClient(new IPEndPoint(bindLocalIp, 0)) { NoDelay = true }
                    : new TcpClient(resolvedIp.AddressFamily) { NoDelay = true };

                await tcp.ConnectAsync(resolvedIp, 80, ct).ConfigureAwait(false);
                var stream = tcp.GetStream();

                string req = $"HEAD / HTTP/1.1\r\nHost: {targetHost}\r\nUser-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64)\r\nConnection: close\r\n\r\n";
                byte[] reqBytes = Encoding.ASCII.GetBytes(req);
                await stream.WriteAsync(reqBytes.AsMemory(), ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);

                byte[] respBuf = new byte[1024];
                int read = await stream.ReadAsync(respBuf.AsMemory(), ct).ConfigureAwait(false);
                sw.Stop();

                if (read <= 0)
                    return new SiteBypassProbeOutcome(false, null, null, "HTTP", false, false);

                string snippet = Encoding.ASCII.GetString(respBuf, 0, read);
                int? code = ParseHttpStatusCode(snippet);
                bool isBlockPage = ContainsHttpBlockSignature(snippet);
                if (isBlockPage)
                    return new SiteBypassProbeOutcome(false, sw.ElapsedMilliseconds, code, "HTTP", false, true);

                // If server rejected HEAD (405), still confirm HTTP server is genuinely alive via GET
                if (code == 405)
                {
                    using var tcp2 = (bindLocalIp != null && bindLocalIp.AddressFamily == resolvedIp.AddressFamily)
                        ? new TcpClient(new IPEndPoint(bindLocalIp, 0)) { NoDelay = true }
                        : new TcpClient(resolvedIp.AddressFamily) { NoDelay = true };
                    await tcp2.ConnectAsync(resolvedIp, 80, ct).ConfigureAwait(false);
                    var s2 = tcp2.GetStream();
                    byte[] getBytes = Encoding.ASCII.GetBytes($"GET / HTTP/1.1\r\nHost: {targetHost}\r\nUser-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64)\r\nConnection: close\r\n\r\n");
                    await s2.WriteAsync(getBytes.AsMemory(), ct).ConfigureAwait(false);
                    await s2.FlushAsync(ct).ConfigureAwait(false);
                    int r2 = await s2.ReadAsync(respBuf.AsMemory(), ct).ConfigureAwait(false);
                    if (r2 > 0)
                    {
                        string snip2 = Encoding.ASCII.GetString(respBuf, 0, r2);
                        if (ContainsHttpBlockSignature(snip2))
                            return new SiteBypassProbeOutcome(false, sw.ElapsedMilliseconds, ParseHttpStatusCode(snip2), "HTTP", false, true);
                        code = ParseHttpStatusCode(snip2) ?? code;
                    }
                }

                return new SiteBypassProbeOutcome(true, sw.ElapsedMilliseconds, code, "HTTP", false, false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return new SiteBypassProbeOutcome(false, null, null, "HTTP", false, false);
            }
        }

        private static async Task<int?> SendHttpProbeOverStreamAsync(Stream stream, string targetHost, string method, CancellationToken ct)
        {
            string req = $"{method} / HTTP/1.1\r\nHost: {targetHost}\r\nUser-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64)\r\nConnection: close\r\n\r\n";
            byte[] reqBytes = Encoding.ASCII.GetBytes(req);
            await stream.WriteAsync(reqBytes.AsMemory(), ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);

            byte[] respBuf = new byte[512];
            int bytesRead = await stream.ReadAsync(respBuf.AsMemory(), ct).ConfigureAwait(false);
            if (bytesRead <= 12) return null;
            return ParseHttpStatusCode(Encoding.ASCII.GetString(respBuf, 0, bytesRead));
        }

        /// <summary>
        /// Lightweight live reachability check against the user's active Agent target site using the current system DNS.
        /// Used by Auto-Failover Guard to detect when the target website itself stops opening even if 1.1.1.1 still pings.
        /// </summary>
        public async Task<bool> CheckLiveTargetSiteReachableAsync(AdapterProbeContext adapterCtx, string rawInput, CancellationToken token = default)
        {
            if (!adapterCtx.IsUp) return false;
            string domain = NormalizeTargetDomain(rawInput);
            if (string.IsNullOrEmpty(domain)) return true;

            bool preferHttp = IsExplicitHttpScheme(rawInput);
            IPAddress? bindIp = adapterCtx.AllowVpnRouteFallback ? null : adapterCtx.SourceIpv4;

            try
            {
                using var dnsCts = new CancellationTokenSource(2000);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, dnsCts.Token);
                var ips = (await Dns.GetHostAddressesAsync(domain, linked.Token).ConfigureAwait(false))
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                    .Take(2)
                    .ToList();

                if (ips.Count == 0 || ips.All(IsSinkholeOrBogusIp))
                    return false;

                foreach (var ip in ips.Where(a => !IsSinkholeOrBogusIp(a)))
                {
                    var outcome = await VerifySiteBypassOnIpAsync(ip, domain, 2400, preferHttp, bindIp, token).ConfigureAwait(false);
                    if (outcome.Connected && outcome.HttpStatus != 403 && outcome.HttpStatus != 451)
                        return true;
                }
            }
            catch { }

            return false;
        }

        /// <summary>
        /// Parallel Anti-Sanction Smart Agent Scanner:
        /// Queries all DNS providers simultaneously via the selected adapter, tests up to 2 resolved IPs,
        /// filters out ISP sinkholes & MitM certificates, supports both HTTPS (443) and HTTP (80) sites,
        /// and handles Stop/cancellation cleanly without applying partial results.
        /// </summary>
        public async Task<AgentScanSummary> RunSmartAgentSiteScanAsync(
            string interfaceName,
            string rawInput,
            Action<string>? onProgress = null,
            CancellationToken externalToken = default)
        {
            bool preferHttp = IsExplicitHttpScheme(rawInput);
            string domain = NormalizeTargetDomain(rawInput);
            if (string.IsNullOrEmpty(domain))
            {
                return new AgentScanSummary(rawInput ?? "", false, null, new List<AgentDnsProbeRow>(), false,
                    "Please enter a valid domain name (e.g., discord.com, epicgames.com, chatgpt.com).");
            }

            var adapterCtx = await CaptureAdapterProbeContextAsync(interfaceName).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(interfaceName) && !adapterCtx.IsUp)
            {
                return new AgentScanSummary(domain, false, null, new List<AgentDnsProbeRow>(), false,
                    $"Selected adapter '{interfaceName}' is currently offline/disconnected.");
            }

            // When a VPN TUN hijacks default route, avoid binding to physical NIC IP so traffic flows properly
            IPAddress? bindIp = adapterCtx.AllowVpnRouteFallback ? null : adapterCtx.SourceIpv4;

            using var lease = CreateTrackedOperationCts(TimeSpan.FromSeconds(30));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(lease.Token, externalToken);
            var ct = linked.Token;

            var providers = GetAllDnsProviders();
            Emit($"[SMART AGENT] Scanning {providers.Count} DNS servers in parallel for '{domain}' ({(preferHttp ? "HTTP/HTTPS" : "HTTPS + HTTP fallback")})...");
            WriteDiagnosticLog("SmartAgent", $"Parallel scan started: domain={domain}, adapter={interfaceName}, preferHttp={preferHttp}, providers={providers.Count}");
            onProgress?.Invoke($"Probing {providers.Count} DNS resolvers for {domain}...");

            using var throttler = new SemaphoreSlim(10);
            var tasks = providers.Select(async p =>
            {
                await throttler.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    var (dnsMs, ips) = await ResolveDomainViaDnsServerAsync(p.PrimaryIp, domain, 1600, ct, bindLocalIp: bindIp).ConfigureAwait(false);
                    if (!dnsMs.HasValue || ips.Count == 0)
                    {
                        return new AgentDnsProbeRow
                        {
                            ProviderName = p.Name,
                            PrimaryIp = p.PrimaryIp,
                            SecondaryIp = p.SecondaryIp,
                            Category = p.CategoryLabel,
                            ResolvedIp = "--",
                            DnsLatencyMs = dnsMs,
                            RankScore = 999_999,
                            StatusText = "DNS Timeout",
                            ProviderRef = p
                        };
                    }

                    var validIps = ips.Where(ip => !IsSinkholeOrBogusIp(ip)).Take(2).ToList();
                    if (validIps.Count == 0)
                    {
                        return new AgentDnsProbeRow
                        {
                            ProviderName = p.Name,
                            PrimaryIp = p.PrimaryIp,
                            SecondaryIp = p.SecondaryIp,
                            Category = p.CategoryLabel,
                            ResolvedIp = ips[0].ToString(),
                            DnsLatencyMs = dnsMs,
                            IsSinkhole = true,
                            RankScore = 900_000,
                            StatusText = "✗ Sinkhole / Filtered",
                            ProviderRef = p
                        };
                    }

                    // Test up to 2 resolved IPs (important for multi-A CDN endpoints)
                    IPAddress chosenIp = validIps[0];
                    SiteBypassProbeOutcome bestOutcome = default;
                    foreach (var candidateIp in validIps)
                    {
                        ct.ThrowIfCancellationRequested();
                        var outcome = await VerifySiteBypassOnIpAsync(candidateIp, domain, 2500, preferHttp, bindIp, ct).ConfigureAwait(false);
                        chosenIp = candidateIp;
                        bestOutcome = outcome;
                        if (outcome.Connected && outcome.HttpStatus != 403 && outcome.HttpStatus != 451)
                            break;
                    }

                    if (!bestOutcome.Connected || !bestOutcome.LatencyMs.HasValue)
                    {
                        string failText = bestOutcome.CertMismatch
                            ? "✗ TLS Cert Invalid / MitM"
                            : bestOutcome.HttpBlockPage
                                ? "✗ HTTP Block Page"
                                : "✗ Blocked / Unreachable";

                        return new AgentDnsProbeRow
                        {
                            ProviderName = p.Name,
                            PrimaryIp = p.PrimaryIp,
                            SecondaryIp = p.SecondaryIp,
                            Category = p.CategoryLabel,
                            ResolvedIp = chosenIp.ToString(),
                            DnsLatencyMs = dnsMs,
                            IsSinkhole = bestOutcome.CertMismatch || bestOutcome.HttpBlockPage,
                            RankScore = 800_000 + (int)Math.Min(dnsMs.Value, 9999),
                            StatusText = failText,
                            ProviderRef = p
                        };
                    }

                    bool isGeoBlocked = bestOutcome.HttpStatus == 403 || bestOutcome.HttpStatus == 451;
                    bool isCleanAccess = !isGeoBlocked;
                    bool isIranBypassGroup = p.CategoryColor == DnsGroupColor.OrangeAntiFilter;

                    int score;
                    string status;
                    if (isCleanAccess)
                    {
                        int groupBonus = isIranBypassGroup ? 0 : 18;
                        int protoBonus = bestOutcome.Protocol == "HTTPS" ? 0 : 25;
                        score = (int)Math.Min(bestOutcome.LatencyMs.Value, 50_000) + groupBonus + protoBonus;
                        status = bestOutcome.HttpStatus.HasValue
                            ? $"✓ {bestOutcome.Protocol} OK (HTTP {bestOutcome.HttpStatus.Value})"
                            : $"✓ {bestOutcome.Protocol} Connected OK";
                    }
                    else
                    {
                        score = 100_000 + (int)Math.Min(bestOutcome.LatencyMs.Value, 50_000);
                        status = $"⚠ Restricted ({bestOutcome.Protocol} {bestOutcome.HttpStatus})";
                    }

                    return new AgentDnsProbeRow
                    {
                        ProviderName = p.Name,
                        PrimaryIp = p.PrimaryIp,
                        SecondaryIp = p.SecondaryIp,
                        Category = p.CategoryLabel,
                        ResolvedIp = chosenIp.ToString(),
                        DnsLatencyMs = dnsMs,
                        ConnectLatencyMs = bestOutcome.LatencyMs,
                        HttpStatusCode = bestOutcome.HttpStatus,
                        IsBypassSuccess = isCleanAccess,
                        IsGeoRestricted = isGeoBlocked,
                        RankScore = score,
                        StatusText = status,
                        ProviderRef = p
                    };
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    return new AgentDnsProbeRow
                    {
                        ProviderName = p.Name,
                        PrimaryIp = p.PrimaryIp,
                        SecondaryIp = p.SecondaryIp,
                        Category = p.CategoryLabel,
                        ResolvedIp = "--",
                        RankScore = 999_999,
                        StatusText = "✗ Probe Error",
                        ProviderRef = p
                    };
                }
                finally
                {
                    throttler.Release();
                }
            }).ToList();

            List<AgentDnsProbeRow> rows;
            try
            {
                rows = (await Task.WhenAll(tasks).ConfigureAwait(false))
                    .OrderBy(r => r.RankScore)
                    .ToList();
            }
            catch (OperationCanceledException)
            {
                Emit($"[SMART AGENT] Scan for '{domain}' was cancelled.");
                WriteDiagnosticLog("SmartAgent", $"Scan cancelled for '{domain}'.");
                return new AgentScanSummary(domain, false, null, new List<AgentDnsProbeRow>(), false,
                    $"Scan for '{domain}' was cancelled.", WasCancelled: true);
            }

            bool sinkholeDetected = rows.Any(r => r.IsSinkhole);
            var cleanWinner = rows.FirstOrDefault(r => r.IsBypassSuccess);
            var partialMatch = cleanWinner ?? rows.FirstOrDefault(r => r.ConnectLatencyMs.HasValue && !r.IsSinkhole);

            string summaryMsg;
            if (cleanWinner != null)
            {
                summaryMsg = $"Best route for {domain}: {cleanWinner.ProviderName} ({cleanWinner.PrimaryIp}) -> {cleanWinner.ResolvedIp} in {cleanWinner.ConnectLatencyMs} ms ({cleanWinner.StatusText}).";
            }
            else if (partialMatch != null)
            {
                summaryMsg = $"All reachable DNS servers returned geo-restriction for '{domain}' (best: {partialMatch.ProviderName} — {partialMatch.StatusText}). DNS was NOT auto-switched.";
            }
            else
            {
                summaryMsg = sinkholeDetected
                    ? $"All tested DNS servers were sinkholed/MitM-blocked for '{domain}'. A VPN tunnel may be required."
                    : $"No DNS server could reach '{domain}' over HTTPS (443) or HTTP (80) right now.";
            }

            Emit($"[SMART AGENT] {summaryMsg}");
            WriteDiagnosticLog("SmartAgent", summaryMsg);
            return new AgentScanSummary(domain, cleanWinner != null, partialMatch, rows, sinkholeDetected, summaryMsg, WasCancelled: false);
        }

        // ==================== GRAPHICAL DNS SESSION TELEMETRY REPORT GENERATOR (.TXT) ====================
        private static string BuildAsciiMeter(double ratio, int width = 20)
        {
            ratio = Math.Clamp(ratio, 0.0, 1.0);
            int filled = (int)Math.Round(ratio * width);
            return new string('█', filled) + new string('░', width - filled);
        }

        private static string BuildAsciiSparkline(IReadOnlyList<long> samples, int maxCols = 54)
        {
            if (samples.Count == 0) return "(no latency samples collected yet)";
            const string blocks = "▁▂▃▄▅▆▇█";
            var slice = samples.Count > maxCols ? samples.Skip(samples.Count - maxCols).ToList() : samples.ToList();
            long min = slice.Min();
            long max = slice.Max();
            long span = Math.Max(1, max - min);

            var sb = new StringBuilder(slice.Count);
            foreach (long v in slice)
            {
                int idx = (int)Math.Clamp(Math.Round((v - min) * 7.0 / span), 0, 7);
                sb.Append(blocks[idx]);
            }
            return sb.ToString();
        }

        private static string FormatDataAmount(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1048576) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1073741824) return $"{bytes / 1048576.0:F2} MB";
            return $"{bytes / 1073741824.0:F3} GB";
        }

        private static (int Score, string Grade, string Label) ComputeNetworkQualityGrade(CumulativePingStats sessionGlobal, PingChannelTracker fallbackTracker)
        {
            long? avgOpt = sessionGlobal.HasSample ? sessionGlobal.AvgMs : fallbackTracker.AvgMs;
            long? jitOpt = sessionGlobal.HasSample ? sessionGlobal.JitterMs : fallbackTracker.JitterMs;
            double loss = sessionGlobal.HasSample ? sessionGlobal.LossPercent : fallbackTracker.LossPercent;

            if (!avgOpt.HasValue)
                return (0, "N/A", "Offline / No Samples");

            double score = 100.0;
            long avg = avgOpt.Value;
            long jitter = jitOpt ?? 0;

            if (avg > 40) score -= Math.Min(35.0, (avg - 40) * 0.25);
            if (jitter > 5) score -= Math.Min(25.0, (jitter - 5) * 0.8);
            score -= Math.Min(50.0, loss * 4.5);

            int finalScore = (int)Math.Clamp(Math.Round(score), 0, 100);
            return finalScore switch
            {
                >= 92 => (finalScore, "S+", "Tournament / Ultra-Stable"),
                >= 82 => (finalScore, "A",  "Excellent Gaming & Streaming"),
                >= 68 => (finalScore, "B",  "Good Everyday Connection"),
                >= 50 => (finalScore, "C",  "Fair (Noticeable Lag / Jitter)"),
                _     => (finalScore, "D",  "Poor / Unstable Line")
            };
        }

        public string BuildGraphicalDnsSessionReport(
            AdapterTelemetrySnapshot snap,
            DnsSessionTracker session,
            PingChannelTracker globalTracker,
            PingChannelTracker intranetTracker,
            PingChannelTracker routerTracker,
            string globalTarget,
            string intranetTarget,
            bool agentActive,
            string agentTargetDomain,
            long nicSessionDrops)
        {
            var (score, grade, gradeLabel) = ComputeNetworkQualityGrade(session.GlobalStats, globalTracker);
            var elapsed = session.Elapsed;
            string durationStr = $"{(int)elapsed.TotalHours:D2}h {elapsed.Minutes:D2}m {elapsed.Seconds:D2}s";

            // 86-char inner width guarantees ZERO truncation on Min/Avg/Max/Jitter/Loss columns!
            const int innerWidth = 86;
            string topBorder    = "╔" + new string('═', innerWidth) + "╗";
            string midBorder    = "╠" + new string('═', innerWidth) + "╣";
            string subBorder    = "╟" + new string('─', innerWidth) + "╢";
            string bottomBorder = "╚" + new string('═', innerWidth) + "╝";

            string Row(string text)
            {
                if (text.Length > innerWidth - 2)
                    text = text[..(innerWidth - 5)] + "...";
                return "║ " + text.PadRight(innerWidth - 2) + " ║";
            }

            string CenterRow(string text)
            {
                if (text.Length >= innerWidth - 2) return Row(text);
                int padLeft = (innerWidth - 2 - text.Length) / 2;
                int padRight = innerWidth - 2 - text.Length - padLeft;
                return "║ " + new string(' ', padLeft) + text + new string(' ', padRight) + " ║";
            }

            string FormatSessionStatsLine(string label, CumulativePingStats s, PingChannelTracker fb)
            {
                long? minV = s.HasSample ? s.MinMs : fb.MinMs;
                long? avgV = s.HasSample ? s.AvgMs : fb.AvgMs;
                long? maxV = s.HasSample ? s.MaxMs : fb.MaxMs;
                long? jitV = s.HasSample ? s.JitterMs : fb.JitterMs;
                double lossV = s.HasSample ? s.LossPercent : fb.LossPercent;

                string min = minV.HasValue ? $"{minV}ms" : "--";
                string avg = avgV.HasValue ? $"{avgV}ms" : "--";
                string max = maxV.HasValue ? $"{maxV}ms" : "--";
                string jit = jitV.HasValue ? $"±{jitV}ms" : "±0ms";
                return $"{label,-26} Min:{min,-6} Avg:{avg,-6} Max:{max,-6} J:{jit,-6} L:{lossV:F1}%";
            }

            string shortIntra = intranetTarget.Replace("www.", "", StringComparison.OrdinalIgnoreCase);

            var sb = new StringBuilder();
            sb.AppendLine(topBorder);
            sb.AppendLine(CenterRow($"AIONTOOLPRO v{GetAppVersionString()}  —  DNS & NETWORK TELEMETRY REPORT"));
            sb.AppendLine(CenterRow($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}   |   Quality Grade: [{grade}] {score}/100"));
            sb.AppendLine(midBorder);

            sb.AppendLine(Row(" [1] ACTIVE DNS & NETWORK ADAPTOR"));
            sb.AppendLine(subBorder);
            sb.AppendLine(Row($"  Active DNS      : {session.ActiveDnsDisplay}"));
            sb.AppendLine(Row($"  DNS Servers     : {(snap.DnsServers.Count > 0 ? string.Join(" , ", snap.DnsServers) : "Automatic DHCP")}"));
            sb.AppendLine(Row($"  Windows DoH     : {(session.DohActive ? "ENABLED (Encrypted DNS-over-HTTPS)" : "Standard DNS")}"));
            sb.AppendLine(Row($"  Adaptor         : {snap.InterfaceName} ({snap.OperationalStatus} · {snap.LinkSpeedLabel})"));
            sb.AppendLine(Row($"  Local IPv4 / GW : {snap.Ipv4Address}  /  {(string.IsNullOrWhiteSpace(snap.GatewayIp) ? "None" : snap.GatewayIp)}"));
            sb.AppendLine(Row($"  Session Active  : {durationStr}  (since {session.StartedAtLocal:HH:mm:ss})"));
            sb.AppendLine(midBorder);

            sb.AppendLine(Row(" [2] DATA DOWNLOADED & UPLOADED WITH THIS DNS"));
            sb.AppendLine(subBorder);
            long totalSessionBytes = session.SessionBytesReceived + session.SessionBytesSent;
            double dlShare = totalSessionBytes > 0 ? (double)session.SessionBytesReceived / totalSessionBytes : 0.0;
            sb.AppendLine(Row($"  Downloaded (Rx) : {FormatDataAmount(session.SessionBytesReceived),-14}  [{BuildAsciiMeter(dlShare, 24)}]"));
            sb.AppendLine(Row($"  Uploaded   (Tx) : {FormatDataAmount(session.SessionBytesSent),-14}  [{BuildAsciiMeter(totalSessionBytes > 0 ? 1.0 - dlShare : 0.0, 24)}]"));
            sb.AppendLine(Row($"  Total Session   : {FormatDataAmount(totalSessionBytes),-14}  NIC Lifetime Rx: {FormatDataAmount(snap.BytesReceived)}"));
            sb.AppendLine(Row($"  Peak Speeds     : Down {FormatDataAmount((long)session.PeakDownloadBytesPerSec)}/s  |  Up {FormatDataAmount((long)session.PeakUploadBytesPerSec)}/s"));
            sb.AppendLine(midBorder);

            sb.AppendLine(Row(" [3] FULL-SESSION PING, JITTER & PACKET LOSS ANALYTICS"));
            sb.AppendLine(subBorder);
            sb.AppendLine(Row($"  {FormatSessionStatsLine($"Global ({globalTarget})", session.GlobalStats, globalTracker)}"));
            sb.AppendLine(Row($"  {FormatSessionStatsLine($"Intranet ({shortIntra})", session.IntranetStats, intranetTracker)}"));
            sb.AppendLine(Row($"  {FormatSessionStatsLine("Router (Gateway)", session.RouterStats, routerTracker)}"));
            sb.AppendLine(Row($"  Quality Score   : [{BuildAsciiMeter(score / 100.0, 24)}]  {score}/100 ({gradeLabel})"));
            sb.AppendLine(Row($"  Session Packets : {session.GlobalStats.TotalSent} sent ({session.GlobalStats.TotalLost} lost)  |  Live 40-pkt Loss: {globalTracker.LossPercent:F1}%  |  NIC Drops: {nicSessionDrops}"));
            sb.AppendLine(subBorder);
            sb.AppendLine(Row($"  Latency Graph   : {BuildAsciiSparkline(session.GlobalLatencyHistory, 62)}"));
            sb.AppendLine(midBorder);

            sb.AppendLine(Row(" [4] SMART ANTI-SANCTION AGENT STATUS"));
            sb.AppendLine(subBorder);
            sb.AppendLine(Row($"  Agent State     : {(agentActive ? "ACTIVE (Live Protection & Auto-Routing)" : "STANDBY (OFF)")}"));
            sb.AppendLine(Row($"  Target Site     : {(string.IsNullOrWhiteSpace(agentTargetDomain) ? "None" : agentTargetDomain)}"));
            if (session.AgentEvents.Count > 0)
            {
                foreach (var ev in session.AgentEvents.TakeLast(6))
                    sb.AppendLine(Row($"  • {ev}"));
            }
            else
            {
                sb.AppendLine(Row("  • No automatic DNS switches recorded in this session."));
            }

            sb.AppendLine(bottomBorder);
            return sb.ToString();
        }
    }
}
