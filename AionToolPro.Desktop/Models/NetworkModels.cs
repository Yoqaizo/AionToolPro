using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace AionToolPro.Desktop.Models
{
    public enum DnsGroupColor { OrangeAntiFilter, BlueGeneral, YellowSecurity }

    public enum AdaptorHealthState
    {
        ConnectedGreen = 0,
        DisconnectedOrange = 1,
        DisabledRed = 2
    }

    public sealed class NetworkAdaptorItem
    {
        private static readonly SolidColorBrush FrozenGreenBrush = CreateFrozen("#10B981");
        private static readonly SolidColorBrush FrozenOrangeBrush = CreateFrozen("#FB923C");
        private static readonly SolidColorBrush FrozenRedBrush = CreateFrozen("#F43F5E");

        private static SolidColorBrush CreateFrozen(string hex)
        {
            var c = ColorConverter.ConvertFromString(hex) is Color parsed ? parsed : Colors.White;
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        public string Name { get; init; } = string.Empty;
        public string AdminState { get; init; } = "Enabled";
        public string ConnectionState { get; init; } = "Connected";
        public AdaptorHealthState HealthState { get; init; } = AdaptorHealthState.ConnectedGreen;

        public string StatusColorHex => HealthState switch
        {
            AdaptorHealthState.ConnectedGreen => "#10B981",
            AdaptorHealthState.DisconnectedOrange => "#FB923C",
            _ => "#F43F5E"
        };

        public SolidColorBrush StatusBrush => HealthState switch
        {
            AdaptorHealthState.ConnectedGreen => FrozenGreenBrush,
            AdaptorHealthState.DisconnectedOrange => FrozenOrangeBrush,
            _ => FrozenRedBrush
        };

        public override string ToString() => Name;
    }

    public sealed record DnsProviderModel(
        int Index,
        string Name,
        string PrimaryIp,
        string SecondaryIp,
        DnsGroupColor CategoryColor,
        string Description,
        string? DohTemplateUrl = null,
        string? PrimaryIpv6 = null,
        string? SecondaryIpv6 = null)
    {
        public string CategoryLabel => CategoryColor switch
        {
            DnsGroupColor.OrangeAntiFilter => "IRAN / GAME",
            DnsGroupColor.YellowSecurity => "SECURITY",
            _ => "GLOBAL"
        };

        public string BadgeHex => CategoryColor switch
        {
            DnsGroupColor.OrangeAntiFilter => "#FB923C",
            DnsGroupColor.YellowSecurity => "#FACC15",
            _ => "#38BDF8"
        };

        public override string ToString() => $"{Name}  —  {PrimaryIp} , {SecondaryIp}";
    }

    public sealed record ThemeDefinition(
        int Id,
        string Name,
        string AccentHex,
        string SecondaryHex,
        string Subtitle,
        string CanvasHex = "#0B0E14",
        string SidebarHex = "#11151F",
        string CardHex = "#171C28",
        string CardHoverHex = "#1F2637",
        string InputHex = "#0D111A",
        string BorderHex = "#263044",
        bool IsLightSurface = false)
    {
        public override string ToString() => $"{Name} — {Subtitle}";
    }

    public sealed record AppSurfacePalette(
        int Id,
        string Name,
        string CanvasHex,
        string SidebarHex,
        string CardHex,
        string CardHoverHex,
        string InputHex,
        string BorderHex,
        bool UseThemeDefault = false,
        bool IsLightSurface = false)
    {
        public override string ToString() => Name;
    }

    public sealed record BlocklistDefinition(
        int Id,
        string Tag,
        string Name,
        string Category,
        string DownloadUrl,
        bool IsBuiltInCustom = false)
    {
        public override string ToString() => $"{Name} — {Category}";
    }

    public sealed record WifiCredentialItem(
        string Ssid,
        string? Password,
        string Status)
    {
        public string MaskedPassword => string.IsNullOrEmpty(Password) ? Status : "••••••••••••";
    }

    public sealed record DnsBenchmarkRow(
        int Index,
        string Name,
        string PrimaryIp,
        string SecondaryIp,
        string Category,
        long? LatencyMs,
        string StatusText,
        bool IsPrivateRange = false)
    {
        public long SortKey => LatencyMs ?? long.MaxValue;
    }

    public sealed record AppNetworkUsageRow(
        string ProcessName,
        int Pid,
        int ActiveConnections,
        double MemoryMb,
        string RemoteEndpoints,
        string ExecutablePath);

    public sealed record FirewallBlockedApp(
        string RuleBaseName,
        string ProcessName,
        string ExecutablePath,
        string BlockedAtUtc);

    public sealed record OperationResult(
        bool Success,
        string Message,
        int ExitCode = 0);

    public enum ProbeTransport
    {
        Icmp = 0,
        TcpFallback = 1,
        Timeout = 2
    }

    public sealed record PingProbeResult(
        long? RttMs,
        ProbeTransport Transport)
    {
        public bool IsReachable => RttMs.HasValue && Transport != ProbeTransport.Timeout;
    }

    public sealed record AppSettingsModel(
        int ThemeId = 1,
        string? InterfaceName = null,
        bool LogEnabled = true,
        bool IsDarkMode = true,
        string LanguageCode = "en",
        int SurfacePaletteId = 0,
        bool MinimizeToTray = false,
        string GlobalPingTarget = "1.1.1.1",
        string IntranetPingTarget = "www.arvancloud.ir");

    public sealed record CustomDnsProfile(
        string Name,
        string PrimaryIp,
        string SecondaryIp,
        string Description = "User custom DNS");

    public sealed record SettingsBundleModel(
        AppSettingsModel Settings,
        List<CustomDnsProfile> CustomDns);

    public sealed record UpdateCheckResult(
        bool HasUpdate,
        string CurrentVersion,
        string LatestVersion,
        string ReleaseNotes,
        string? DownloadUrl,
        string? ChecksumUrl,
        string? AssetName,
        string Message);

    public sealed record AdapterTelemetrySnapshot(
        string InterfaceName,
        bool Found,
        string OperationalStatus,
        long BytesReceived,
        long BytesSent,
        long UnicastPacketsReceived,
        long IncomingPacketsWithErrors,
        long IncomingPacketsDiscarded,
        long UnicastPacketsSent,
        long OutgoingPacketsWithErrors,
        long OutgoingPacketsDiscarded,
        List<string> DnsServers,
        string Ipv4Address,
        string GatewayIp);

    /// <summary>
    /// Accurate sliding-window telemetry tracker:
    /// - Records every single probe honestly (never masks transient packet loss).
    /// - Uses RFC 3550 smoothed jitter (J = J + (|D| - J) / 16) for stable, realistic readings.
    /// - Distinguishes pure ICMP loss from ICMP-blocked networks where TCP is healthy.
    /// </summary>
    public sealed class PingChannelTracker
    {
        private readonly Queue<long?> _samples = new();
        private long? _lastValidPing;
        private double _rfcJitter;
        private bool _hasJitter;

        public int MaxSamples { get; } = 40;
        public bool HasSample { get; private set; }
        public long? CurrentMs { get; private set; }
        public ProbeTransport CurrentTransport { get; private set; } = ProbeTransport.Timeout;
        public int ConsecutiveTimeouts { get; private set; }
        public long? LastValidMs => _lastValidPing;
        public long? JitterMs => _hasJitter ? (long)Math.Round(_rfcJitter) : null;
        public long? MinMs { get; private set; }
        public long? AvgMs { get; private set; }
        public long? MaxMs { get; private set; }
        public int WindowSamples => _samples.Count;
        public int LostSamples { get; private set; }
        public double LossPercent { get; private set; }

        public void Reset()
        {
            _samples.Clear();
            _lastValidPing = null;
            _rfcJitter = 0;
            _hasJitter = false;
            HasSample = false;
            CurrentMs = null;
            CurrentTransport = ProbeTransport.Timeout;
            ConsecutiveTimeouts = 0;
            MinMs = null;
            AvgMs = null;
            MaxMs = null;
            LostSamples = 0;
            LossPercent = 0.0;
        }

        public void PushProbe(PingProbeResult probe)
        {
            HasSample = true;
            CurrentTransport = probe.Transport;

            if (probe.IsReachable && probe.RttMs.HasValue && probe.RttMs.Value >= 0)
            {
                long ms = probe.RttMs.Value;
                ConsecutiveTimeouts = 0;
                CurrentMs = ms;

                if (_lastValidPing.HasValue)
                {
                    double diff = Math.Abs(ms - _lastValidPing.Value);
                    _rfcJitter = _hasJitter ? _rfcJitter + (diff - _rfcJitter) / 16.0 : diff;
                    _hasJitter = true;
                }

                _lastValidPing = ms;
                _samples.Enqueue(ms);
            }
            else
            {
                ConsecutiveTimeouts++;
                CurrentMs = null;
                _samples.Enqueue(null);
            }

            while (_samples.Count > MaxSamples)
                _samples.Dequeue();

            long min = long.MaxValue;
            long max = long.MinValue;
            long sum = 0;
            int validCount = 0;
            int lost = 0;

            foreach (var s in _samples)
            {
                if (s.HasValue)
                {
                    long v = s.Value;
                    if (v < min) min = v;
                    if (v > max) max = v;
                    sum += v;
                    validCount++;
                }
                else
                {
                    lost++;
                }
            }

            LostSamples = lost;
            LossPercent = _samples.Count > 0 ? Math.Round(lost * 100.0 / _samples.Count, 1) : 0.0;

            if (validCount > 0)
            {
                MinMs = min;
                MaxMs = max;
                AvgMs = sum / validCount;
            }
            else
            {
                MinMs = null;
                MaxMs = null;
                AvgMs = null;
                _hasJitter = false;
                _rfcJitter = 0;
            }
        }
    }

    public static class AioCatalog
    {
        private static readonly Regex HostnameRegex = new(
            @"^([a-zA-Z0-9]|[a-zA-Z0-9][a-zA-Z0-9\-]{0,61}[a-zA-Z0-9])(\.([a-zA-Z0-9]|[a-zA-Z0-9][a-zA-Z0-9\-]{0,61}[a-zA-Z0-9]))*\z",
            RegexOptions.Compiled);

        public static bool ValidateIpv4(string? input)
        {
            if (string.IsNullOrEmpty(input)) return false;
            if (input.Length < 7 || input.Length > 15) return false;
            if (!IPAddress.TryParse(input, out var ip)) return false;
            if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
            return string.Equals(ip.ToString(), input, StringComparison.Ordinal);
        }

        public static bool IsSafeDnsIpv4(string? input)
        {
            if (!ValidateIpv4(input)) return false;
            byte[] b = IPAddress.Parse(input!).GetAddressBytes();
            if (b[0] == 0 || b[0] == 127 || b[0] >= 224) return false;
            if (b[0] == 255 && b[1] == 255 && b[2] == 255 && b[3] == 255) return false;
            return true;
        }

        public static bool IsPrivateIpv4(string? input)
        {
            if (!ValidateIpv4(input)) return false;
            return IsPrivateIpv4(IPAddress.Parse(input!));
        }

        public static bool IsPrivateIpv4(IPAddress ip)
        {
            if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
            byte[] b = ip.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254);
        }

        public static bool IsCgNatIpv4(IPAddress ip)
        {
            if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
            byte[] b = ip.GetAddressBytes();
            return b[0] == 100 && (b[1] & 0xC0) == 64;
        }

        public static bool IsValidSubnetMask(string? input)
        {
            if (!ValidateIpv4(input)) return false;
            byte[] b = IPAddress.Parse(input!).GetAddressBytes();
            uint mask = ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
            if (mask == 0 || mask == uint.MaxValue) return false;
            uint inv = ~mask;
            return (inv & (inv + 1)) == 0;
        }

        public static bool ValidateHostOrIp(string? input)
        {
            if (string.IsNullOrEmpty(input)) return false;
            if (input.Length > 253) return false;
            if (input[0] == '-' || input[0] == '/' || input[0] == '.' || input[^1] == '.' || input.Contains(".."))
                return false;

            if (IPAddress.TryParse(input, out var ip))
            {
                return ip.AddressFamily == AddressFamily.InterNetwork || ip.AddressFamily == AddressFamily.InterNetworkV6;
            }

            return HostnameRegex.IsMatch(input);
        }

        public static IReadOnlyList<DnsProviderModel> DnsProviders { get; } = new List<DnsProviderModel>
        {
            new(1,  "Shecan",        "178.22.122.100", "185.51.200.2",   DnsGroupColor.OrangeAntiFilter, "Iran anti-sanction smart DNS"),
            new(2,  "Electro",       "78.157.42.100",  "78.157.42.101",  DnsGroupColor.OrangeAntiFilter, "Low-jitter gaming & anti-sanction DNS"),
            new(3,  "Radar Game",    "10.202.10.10",   "10.202.10.11",   DnsGroupColor.OrangeAntiFilter, "Iran ISP-routed gaming DNS (private range)"),
            new(4,  "403 Online",    "10.202.10.202",  "10.202.10.102",  DnsGroupColor.OrangeAntiFilter, "Developer sanction bypass (private range)"),
            new(5,  "Begzar",        "185.55.226.26",  "185.55.225.25",  DnsGroupColor.OrangeAntiFilter, "Anti-sanction web & app resolver"),
            new(6,  "Shelter",       "91.92.255.16",   "91.92.255.24",   DnsGroupColor.OrangeAntiFilter, "Iran gaming & download stabilizer"),
            new(7,  "Shatel",        "85.15.1.14",     "85.15.1.15",     DnsGroupColor.OrangeAntiFilter, "Shatel domestic fast DNS resolver"),
            new(8,  "HostIran",      "172.29.2.100",   "172.29.0.100",   DnsGroupColor.OrangeAntiFilter, "HostIran internal DNS (private range)"),

            new(9,  "Cloudflare",    "1.1.1.1",        "1.0.0.1",        DnsGroupColor.BlueGeneral,    "Ultra-fast global edge DNS", "https://cloudflare-dns.com/dns-query", "2606:4700:4700::1111", "2606:4700:4700::1001"),
            new(10, "Google",        "8.8.8.8",        "8.8.4.4",        DnsGroupColor.BlueGeneral,    "Global high-availability public DNS", "https://dns.google/dns-query", "2001:4860:4860::8888", "2001:4860:4860::8844"),
            new(11, "Quad9",         "9.9.9.9",        "149.112.112.112",DnsGroupColor.YellowSecurity, "Swiss malware & threat blocking DNS", "https://dns.quad9.net/dns-query", "2620:fe::fe", "2620:fe::9"),
            new(12, "AdGuard",       "94.140.14.14",   "94.140.15.15",   DnsGroupColor.YellowSecurity, "Network-level ad & tracker blocker", "https://dns.adguard-dns.com/dns-query", "2a10:50c0::ad1:ff", "2a10:50c0::ad2:ff"),
            new(13, "OpenDNS",       "208.67.222.222", "208.67.220.220", DnsGroupColor.BlueGeneral,    "Cisco enterprise recursive DNS", "https://doh.opendns.com/dns-query"),
            new(14, "ControlD",      "76.76.2.0",      "76.76.10.0",     DnsGroupColor.BlueGeneral,    "Unfiltered modern anycast resolver", "https://freedns.controld.com/p0"),
            new(15, "CleanBrowsing", "185.228.168.9",  "185.228.169.9",  DnsGroupColor.YellowSecurity, "Security filter blocking malicious hosts"),
            new(16, "Yandex",        "77.88.8.8",      "77.88.8.1",      DnsGroupColor.BlueGeneral,    "Fast regional Eurasia recursive DNS"),

            new(17, "Comodo Secure", "8.26.56.26",     "8.20.247.20",    DnsGroupColor.YellowSecurity, "Cloud-based web filtering and malware protection"),
            new(18, "Alternate DNS", "76.76.19.19",    "76.223.122.150", DnsGroupColor.YellowSecurity, "Ad-blocking and tracker filtering"),
            new(19, "dns0.eu",       "193.110.81.0",   "185.253.5.0",    DnsGroupColor.YellowSecurity, "European sovereign privacy & security DNS", "https://dns0.eu/"),
            new(20, "Mullvad DNS",   "194.242.2.2",    "194.242.2.3",    DnsGroupColor.BlueGeneral,    "Privacy-first non-logging resolver by Mullvad", "https://dns.mullvad.net/dns-query"),
            new(21, "AliDNS",        "223.5.5.5",      "223.6.6.6",      DnsGroupColor.BlueGeneral,    "Alibaba Cloud global resolution"),
            new(22, "114DNS",        "114.114.114.114","114.114.115.115",DnsGroupColor.BlueGeneral,    "Reliable Asian & Pacific routing")
        };

        public static IReadOnlyList<ThemeDefinition> Themes { get; } = new List<ThemeDefinition>
        {
            new(1,  "Discord Blurple",      "#5865F2", "#38BDF8", "Signature Discord Blurple & Cyan",    "#0B0E14", "#11151F", "#171C28", "#1F2637", "#0D111A", "#263044"),
            new(2,  "Discord Nitro Pink",   "#EB459E", "#A855F7", "Nitro Fuchsia Pink & Neon Purple",   "#120916", "#1A0D20", "#24132C", "#2F193A", "#150A1A", "#3B2048"),
            new(3,  "Cyberpunk Sky Blue",   "#38BDF8", "#818CF8", "Electric Sky Blue & Deep Navy",      "#070D19", "#0C1526", "#122038", "#182B49", "#09111F", "#1E3658"),
            new(4,  "Matrix Emerald",       "#10B981", "#34D399", "High-Contrast Phosphor Emerald",     "#05120E", "#091C16", "#0F2920", "#16382C", "#071611", "#1D4738"),
            new(5,  "Crimson Tactical",     "#F43F5E", "#FB7185", "ROG-Inspired Low-Glare Crimson",     "#14070A", "#1E0B10", "#291017", "#36151F", "#18080C", "#451C28"),
            new(6,  "HyperViolet Amethyst", "#A855F7", "#6366F1", "Deep Amethyst & Royal Violet",       "#0E0717", "#150B24", "#1F1133", "#291744", "#11091C", "#351E54"),
            new(7,  "Solar Amber Gold",     "#F59E0B", "#FBBF24", "Industrial Gold Hardware Analyzer",  "#130E05", "#1C1508", "#271E0C", "#332811", "#161106", "#423316"),
            new(8,  "Deep Ocean Aqua",      "#06B6D4", "#2DD4BF", "Crisp Cyan-Teal Engineering",        "#051216", "#081C22", "#0E2730", "#143440", "#06151A", "#1B4352"),
            new(9,  "Titanium Stealth",     "#E2E8F0", "#94A3B8", "Minimalist Monochrome Slate",        "#090B0E", "#101318", "#171B22", "#20252E", "#0C0E12", "#2B323D"),
            new(10, "Daylight Clean Light", "#4F46E5", "#0284C7", "High-Contrast Light Surface",        "#F1F5F9", "#FFFFFF", "#FFFFFF", "#F8FAFC", "#F8FAFC", "#CBD5E1", IsLightSurface: true)
        };

        public static IReadOnlyList<AppSurfacePalette> SurfacePalettes { get; } = new List<AppSurfacePalette>
        {
            new(0, "Auto (Match Theme Surface)", "#0B0E14", "#11151F", "#171C28", "#1F2637", "#0D111A", "#263044", UseThemeDefault: true),
            new(1, "Discord Obsidian (Default)", "#0B0E14", "#11151F", "#171C28", "#1F2637", "#0D111A", "#263044"),
            new(2, "AMOLED Pitch Black",         "#000000", "#07080C", "#0E1017", "#161924", "#050608", "#1E2433"),
            new(3, "Midnight Cyber Navy",        "#060C18", "#0B1426", "#111F38", "#172A4A", "#080F1E", "#1F3760"),
            new(4, "Deep Amethyst Void",         "#0D0714", "#150B21", "#1E1030", "#281640", "#100819", "#341D52"),
            new(5, "Matrix Forest Dark",         "#05100C", "#081913", "#0E261D", "#143529", "#06140F", "#1C4737"),
            new(6, "Crimson Carbon Dark",        "#120609", "#1B0A0E", "#260F15", "#33141D", "#15070B", "#421B26"),
            new(7, "Slate Graphite",             "#12151B", "#181C24", "#202631", "#29303E", "#14181F", "#343D4E"),
            new(8, "Clean Daylight White",       "#F1F5F9", "#FFFFFF", "#FFFFFF", "#F8FAFC", "#F8FAFC", "#CBD5E1", IsLightSurface: true)
        };

        public static IReadOnlyList<BlocklistDefinition> Blocklists { get; } = new List<BlocklistDefinition>
        {
            new(1, "StevenBlack-Base",        "StevenBlack Ads & Malware",           "Ads + Malware",     "https://raw.githubusercontent.com/StevenBlack/hosts/master/hosts"),
            new(2, "StevenBlack-Social",      "StevenBlack Ads, Malware & Social",   "Social + Trackers", "https://raw.githubusercontent.com/StevenBlack/hosts/master/alternates/social/hosts"),
            new(3, "StevenBlack-Gambling",    "StevenBlack Ads, Malware & Gambling", "Content Filter",    "https://raw.githubusercontent.com/StevenBlack/hosts/master/alternates/gambling/hosts"),
            new(4, "StevenBlack-Porn",        "StevenBlack Ads, Malware & Adult",    "Family Shield",     "https://raw.githubusercontent.com/StevenBlack/hosts/master/alternates/porn/hosts"),
            new(5, "PeterLowe-AdServers",     "Peter Lowe Ad & Tracker Servers",     "Lightweight Ads",   "https://pgl.yoyo.org/adservers/serverlist.php?hostformat=hosts&showintro=0&mimetype=plaintext"),
            new(6, "Pollock-SomeoneWhoCares", "Dan Pollock someonewhocares.org",     "Zero-Route Shield", "https://someonewhocares.org/hosts/zero/hosts"),
            new(7, "TeamSpeak-Blocked",       "TeamSpeak Telemetry/License Block",   "Custom Rule",       "local://teamspeak", IsBuiltInCustom: true)
        };
    }
}
