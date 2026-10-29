using System;
using System.Collections.Generic;
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

        public SolidColorBrush StatusBrush => new((Color)ColorConverter.ConvertFromString(StatusColorHex));

        public override string ToString() => Name;
    }

    public sealed record DnsProviderModel(
        int Index,
        string Name,
        string PrimaryIp,
        string SecondaryIp,
        DnsGroupColor CategoryColor,
        string Description,
        string? DohTemplateUrl = null)
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
        string ConsoleColorCode,
        string AccentHex,
        string SecondaryHex,
        string Subtitle,
        bool IsLightSurface = false)
    {
        public override string ToString() => $"{Name} — {Subtitle}";
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
        string Password);

    public sealed record DnsBenchmarkRow(
        int Index,
        string Name,
        string PrimaryIp,
        string SecondaryIp,
        string Category,
        long PingMs,
        string StatusText);

    public sealed class PingChannelTracker
    {
        private readonly Queue<long> _samples = new();
        private long _lastValidPing = -1;

        public int MaxSamples { get; } = 25;
        public long CurrentMs { get; private set; } = 9999;
        public long JitterMs { get; private set; }
        public long MinMs { get; private set; }
        public long AvgMs { get; private set; }
        public long MaxMs { get; private set; }

        public IReadOnlyCollection<long> History => _samples;

        public void PushSample(long ms)
        {
            CurrentMs = ms;
            if (ms < 9999)
            {
                if (_lastValidPing >= 0)
                    JitterMs = Math.Abs(ms - _lastValidPing);
                _lastValidPing = ms;
            }

            _samples.Enqueue(ms);
            while (_samples.Count > MaxSamples)
                _samples.Dequeue();

            var valid = new List<long>();
            foreach (var s in _samples)
            {
                if (s < 9999) valid.Add(s);
            }

            if (valid.Count > 0)
            {
                long min = long.MaxValue;
                long max = long.MinValue;
                long sum = 0;
                foreach (var v in valid)
                {
                    if (v < min) min = v;
                    if (v > max) max = v;
                    sum += v;
                }
                MinMs = min;
                MaxMs = max;
                AvgMs = sum / valid.Count;
            }
        }
    }

    public static class AioCatalog
    {
                public static IReadOnlyList<DnsProviderModel> DnsProviders { get; } = new List<DnsProviderModel>
        {
            new(1,  "Shecan",        "178.22.122.100", "185.51.200.2",   DnsGroupColor.OrangeAntiFilter, "Iran anti-sanction smart DNS"),
            new(2,  "Electro",       "78.157.42.100",  "78.157.42.101",  DnsGroupColor.OrangeAntiFilter, "Low-jitter gaming & anti-sanction DNS"),
            new(3,  "Radar Game",    "10.202.10.10",   "10.202.10.11",   DnsGroupColor.OrangeAntiFilter, "Iran gaming routing & latency booster"),
            new(4,  "403 Online",    "10.202.10.202",  "10.202.10.102",  DnsGroupColor.OrangeAntiFilter, "Developer registry & sanction bypass"),
            new(5,  "Begzar",        "185.55.226.26",  "185.55.225.25",  DnsGroupColor.OrangeAntiFilter, "Anti-sanction web & app resolver"),
            new(6,  "Shelter",       "91.92.255.16",   "91.92.255.24",   DnsGroupColor.OrangeAntiFilter, "Iran gaming & download stabilizer"),
            new(7,  "Shatel",        "85.15.1.14",     "85.15.1.15",     DnsGroupColor.OrangeAntiFilter, "Shatel domestic fast DNS resolver"),
            new(8,  "HostIran",      "172.29.2.100",   "172.29.0.100",   DnsGroupColor.OrangeAntiFilter, "HostIran data-center DNS"),
            
            new(9,  "Cloudflare",    "1.1.1.1",        "1.0.0.1",        DnsGroupColor.BlueGeneral,    "Ultra-fast global edge DNS", "https://cloudflare-dns.com/dns-query"),
            new(10, "Google",        "8.8.8.8",        "8.8.4.4",        DnsGroupColor.BlueGeneral,    "Global high-availability public DNS", "https://dns.google/dns-query"),
            new(11, "Quad9",         "9.9.9.9",        "149.112.112.112",DnsGroupColor.YellowSecurity, "Swiss malware & threat blocking DNS", "https://dns.quad9.net/dns-query"),
            new(12, "AdGuard",       "94.140.14.14",   "94.140.15.15",   DnsGroupColor.YellowSecurity, "Network-level ad & tracker blocker", "https://dns.adguard-dns.com/dns-query"),
            new(13, "OpenDNS",       "208.67.222.222", "208.67.220.220", DnsGroupColor.BlueGeneral,    "Cisco enterprise recursive DNS"),
            new(14, "ControlD",      "76.76.2.0",      "76.76.10.0",     DnsGroupColor.BlueGeneral,    "Modern low-latency anycast resolver"),
            new(15, "CleanBrowsing", "185.228.168.9",  "185.228.169.9",  DnsGroupColor.YellowSecurity, "Security filter blocking malicious hosts"),
            new(16, "Yandex",        "77.88.8.8",      "77.88.8.1",      DnsGroupColor.BlueGeneral,    "Fast regional Eurasia recursive DNS"),
            
            new(17, "Comodo Secure", "8.26.56.26",     "8.20.247.20",    DnsGroupColor.YellowSecurity, "Cloud-based web filtering and malware protection"),
            new(18, "Alternate DNS", "76.76.19.19",    "76.223.122.150", DnsGroupColor.YellowSecurity, "Ad-blocking and adult content filtering"),
            new(19, "dns0.eu",       "193.110.81.0",   "185.253.5.0",    DnsGroupColor.YellowSecurity, "European sovereign privacy and security DNS"),
            new(20, "Mullvad DNS",   "194.242.2.4",    "193.19.108.2",   DnsGroupColor.BlueGeneral,    "Privacy-first non-logging resolver by Mullvad"),
            new(21, "AliDNS",        "223.5.5.5",      "223.6.6.6",      DnsGroupColor.BlueGeneral,    "Alibaba Cloud global resolution"),
            new(22, "114DNS",        "114.114.114.114","114.114.115.115",DnsGroupColor.BlueGeneral,    "Reliable Asian & Pacific routing")
        };

        public static IReadOnlyList<ThemeDefinition> Themes { get; } = new List<ThemeDefinition>
        {
            new(1,  "Discord Blurple",      "09", "#5865F2", "#38BDF8", "Signature Discord Blurple & Cyan"),
            new(2,  "Discord Nitro Pink",   "0D", "#EB459E", "#A855F7", "Nitro Fuchsia Pink & Neon Purple"),
            new(3,  "Cyberpunk Sky Blue",   "0B", "#38BDF8", "#818CF8", "Electric Sky Blue & Indigo"),
            new(4,  "Matrix Emerald",       "0A", "#10B981", "#34D399", "High-Contrast Phosphor Emerald"),
            new(5,  "Crimson Tactical",     "0C", "#F43F5E", "#FB7185", "ROG-Inspired Low-Glare Crimson"),
            new(6,  "HyperViolet Amethyst", "05", "#A855F7", "#6366F1", "Deep Amethyst & Royal Violet"),
            new(7,  "Solar Amber Gold",     "0E", "#F59E0B", "#FBBF24", "Industrial Gold Hardware Analyzer"),
            new(8,  "Deep Ocean Aqua",      "03", "#06B6D4", "#2DD4BF", "Crisp Cyan-Teal Engineering"),
            new(9,  "Titanium Stealth",     "0F", "#E2E8F0", "#94A3B8", "Minimalist Monochrome Slate"),
            new(10, "Daylight Clean Light", "F0", "#4F46E5", "#0284C7", "High-Contrast Light Surface", IsLightSurface: true)
        };

        public static IReadOnlyList<BlocklistDefinition> Blocklists { get; } = new List<BlocklistDefinition>
        {
            new(1, "StevenBlack-Base",        "StevenBlack Ads & Malware",           "Ads + Malware",     "https://raw.githubusercontent.com/StevenBlack/hosts/master/hosts"),
            new(2, "StevenBlack-Social",      "StevenBlack Ads, Malware & Social",   "Social + Trackers", "https://raw.githubusercontent.com/StevenBlack/hosts/master/alternates/social/hosts"),
            new(3, "StevenBlack-Gambling",    "StevenBlack Ads, Malware & Gambling", "Content Filter",    "https://raw.githubusercontent.com/StevenBlack/hosts/master/alternates/gambling/hosts"),
            new(4, "StevenBlack-Porn",        "StevenBlack Ads, Malware & Adult",    "Family Shield",     "https://raw.githubusercontent.com/StevenBlack/hosts/master/alternates/porn/hosts"),
            new(5, "PeterLowe-AdServers",     "Peter Lowe Ad & Tracker Servers",     "Lightweight Ads",   "https://pgl.yoyo.org/adservers/serverlist.php?hostformat=hosts&showintro=0&mimetype=plaintext"),
            new(6, "Pollock-SomeoneWhoCares", "Dan Pollock someonewhocares.org",     "Zero-Route Shield", "https://someonewhocares.org/hosts/zero/hosts"),
            new(7, "TeamSpeak-Blocked",       "TeamSpeak Blocked Rules",             "Custom Rule",       "local://teamspeak", IsBuiltInCustom: true)
        };
    }
}
