using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AionToolPro.Desktop.Models;
using AionToolPro.Desktop.Services;
using AionToolPro.Desktop.Themes;

namespace AionToolPro.Desktop
{
    public partial class MainWindow : Window
    {
        private readonly AioNetworkService _service = new();
        private readonly DispatcherTimer _telemetryTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
        private string _selectedInterface = "Ethernet";
        private string _detectedRouterIp = string.Empty;
        private string _lastDetectedPrimaryDns = string.Empty;
        private string _currentLang = "en";
        private ThemeDefinition _currentTheme = AioCatalog.Themes[0];
        private DnsBenchmarkRow? _fastestWinner;
        private bool _isInitialized;
        private bool _isTelemetryRunning;
        private long _lastRxBytes = 0;
        private long _lastTxBytes = 0;
        private DateTime _lastTick = DateTime.MinValue;

        private static string FormatSpeed(double bytesPerSec)
        {
            if (bytesPerSec <= 0) return "0 KB/s";
            if (bytesPerSec >= 1048576) return $"{bytesPerSec / 1048576.0:F1} MB/s";
            return $"{bytesPerSec / 1024.0:F0} KB/s";
        }

        // 3 Live Ping Channel Trackers
        private readonly PingChannelTracker _globalPingTracker   = new(); // 1. 4.2.2.4
        private readonly PingChannelTracker _intranetPingTracker = new(); // 2. www.arvancloud.ir
        private readonly PingChannelTracker _routerPingTracker   = new(); // 3. Auto-detected Router Gateway IP

        private readonly Dictionary<string, (Border Pill, TextBlock PillText)> _dnsCardBadges = new(StringComparer.OrdinalIgnoreCase);

        public MainWindow()
        {
            InitializeComponent();
            _service.CommandOutputReceived += AppendConsoleLine;

            LoadNetworkInterfaces();
            PopulateCatalogControls();
            ComboLanguage.SelectedIndex = 0;
            LoadPersistedSettingsIfAvailable();

            _isInitialized = true;
            ThemeManager.ApplyTheme(_currentTheme, ChkDarkMode.IsChecked == true);
            ApplyLanguage(_currentLang);

            NetworkChange.NetworkAddressChanged += (_, _) =>
            {
                Dispatcher.InvokeAsync(async () =>
                {
                    LoadNetworkInterfaces(preserveSelection: true);
                    await RefreshRealTimeTelemetryAsync();
                });
            };

            _telemetryTimer.Tick += async (_, _) => await RefreshRealTimeTelemetryAsync();
            _telemetryTimer.Start();
            _ = RefreshRealTimeTelemetryAsync();
        }

        // ==================== CUSTOM WINDOW CHROME HANDLERS ====================
        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                BtnMaximize_Click(sender, e);
                return;
            }
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void BtnMaximize_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        // ==================== INITIALIZATION & VISUAL CARDS ====================
        private void PopulateCatalogControls()
        {
            ComboDnsProviders.ItemsSource = AioCatalog.DnsProviders;
            ComboDnsProviders.SelectedIndex = 0;

            ComboBlocklists.ItemsSource = AioCatalog.Blocklists;
            ComboBlocklists.SelectedIndex = 0;

            ComboThemes.ItemsSource = AioCatalog.Themes;
            ComboThemes.SelectedIndex = 0;

            BuildDashboardDnsCards();
            BuildThemeSwatchCards();
        }

        private void BuildDashboardDnsCards()
        {
            DashboardDnsWrapPanel.Children.Clear();
            _dnsCardBadges.Clear();

            foreach (var p in AioCatalog.DnsProviders)
            {
                var badgeColor = (Color)ColorConverter.ConvertFromString(p.BadgeHex);
                var badgeBrush = new SolidColorBrush(badgeColor);
                var badgeBgBrush = new SolidColorBrush(Color.FromArgb(35, badgeColor.R, badgeColor.G, badgeColor.B));

                var cardStack = new StackPanel { FlowDirection = FlowDirection.LeftToRight };

                var topRow = new DockPanel();
                var titleTxt = new TextBlock
                {
                    Text = p.Name,
                    FontSize = 13,
                    FontWeight = FontWeights.Bold,
                    Foreground = (Brush)FindResource("PrimaryTextBrush")
                };

                var pillText = new TextBlock
                {
                    Text = p.CategoryLabel,
                    FontSize = 8.5,
                    FontWeight = FontWeights.Bold,
                    Foreground = badgeBrush
                };

                var catPill = new Border
                {
                    Background = badgeBgBrush,
                    BorderBrush = badgeBrush,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(5),
                    Padding = new Thickness(6, 1.5, 6, 1.5),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Child = pillText
                };

                _dnsCardBadges[p.PrimaryIp] = (catPill, pillText);

                topRow.Children.Add(titleTxt);
                topRow.Children.Add(catPill);

                var ipTxt = new TextBlock
                {
                    Text = $"{p.PrimaryIp}  ·  {p.SecondaryIp}",
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 11.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = (Brush)FindResource("AccentBrush"),
                    Margin = new Thickness(0, 5, 0, 2)
                };

                var descTxt = new TextBlock
                {
                    Text = p.Description,
                    FontSize = 10.5,
                    Foreground = (Brush)FindResource("MutedTextBrush"),
                    TextTrimming = TextTrimming.CharacterEllipsis
                };

                cardStack.Children.Add(topRow);
                cardStack.Children.Add(ipTxt);
                cardStack.Children.Add(descTxt);

                var btn = new Button
                {
                    Width = 246,
                    Height = 76,
                    Margin = new Thickness(0, 0, 10, 10),
                    Padding = new Thickness(12, 8, 12, 8),
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Style = (Style)FindResource("SecondaryBtnStyle"),
                    Content = cardStack,
                    Tag = p,
                    ToolTip = $"{p.Name} — {p.PrimaryIp} , {p.SecondaryIp}"
                };

                btn.Click += async (s, _) =>
                {
                    if (s is Button b && b.Tag is DnsProviderModel provider)
                        await ApplyDnsProviderWithUiFeedbackAsync(provider);
                };

                DashboardDnsWrapPanel.Children.Add(btn);
            }
        }

        private void UpdateActiveDnsCardHighlight(string currentPrimaryDns)
        {
            foreach (var p in AioCatalog.DnsProviders)
            {
                if (!_dnsCardBadges.TryGetValue(p.PrimaryIp, out var badgeInfo))
                    continue;

                bool isActive = string.Equals(p.PrimaryIp, currentPrimaryDns, StringComparison.OrdinalIgnoreCase);
                if (isActive)
                {
                    var activeCol = (Color)ColorConverter.ConvertFromString("#10B981");
                    badgeInfo.Pill.Background = new SolidColorBrush(Color.FromArgb(55, activeCol.R, activeCol.G, activeCol.B));
                    badgeInfo.Pill.BorderBrush = new SolidColorBrush(activeCol);
                    badgeInfo.PillText.Foreground = new SolidColorBrush(activeCol);
                    badgeInfo.PillText.Text = _currentLang == "fa" ? "✓ فعال" : "✓ ACTIVE";
                }
                else
                {
                    var badgeColor = (Color)ColorConverter.ConvertFromString(p.BadgeHex);
                    badgeInfo.Pill.Background = new SolidColorBrush(Color.FromArgb(35, badgeColor.R, badgeColor.G, badgeColor.B));
                    badgeInfo.Pill.BorderBrush = new SolidColorBrush(badgeColor);
                    badgeInfo.PillText.Foreground = new SolidColorBrush(badgeColor);
                    badgeInfo.PillText.Text = p.CategoryLabel;
                }
            }
        }

        private void BuildThemeSwatchCards()
        {
            ThemeSwatchesWrapPanel.Children.Clear();
            foreach (var t in AioCatalog.Themes)
            {
                var c1 = (Color)ColorConverter.ConvertFromString(t.AccentHex);
                var c2 = (Color)ColorConverter.ConvertFromString(t.SecondaryHex);
                var grad = new LinearGradientBrush(c1, c2, new Point(0, 0), new Point(1, 1));

                var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, FlowDirection = FlowDirection.LeftToRight };
                var swatch = new Border
                {
                    Width = 16,
                    Height = 16,
                    CornerRadius = new CornerRadius(8),
                    Background = grad,
                    Margin = new Thickness(0, 0, 8, 0)
                };
                var label = new TextBlock
                {
                    Text = t.Name,
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center
                };
                sp.Children.Add(swatch);
                sp.Children.Add(label);

                var btn = new Button
                {
                    Content = sp,
                    Style = (Style)FindResource("SecondaryBtnStyle"),
                    Tag = t,
                    Padding = new Thickness(12, 7, 12, 7)
                };
                btn.Click += (s, _) =>
                {
                    if (s is Button b && b.Tag is ThemeDefinition selectedTheme)
                    {
                        ComboThemes.SelectedItem = selectedTheme;
                    }
                };
                ThemeSwatchesWrapPanel.Children.Add(btn);
            }
        }

        private void LoadPersistedSettingsIfAvailable()
        {
            var saved = _service.LoadSavedSettings();
            if (saved.HasValue)
            {
                var (themeId, ifaceName, logEnabled, isDark, langCode) = saved.Value;
                ChkEnableLogging.IsChecked = logEnabled;
                ChkDarkMode.IsChecked = isDark;
                _service.EnableActionLog = logEnabled;

                _currentLang = langCode == "fa" ? "fa" : "en";
                ComboLanguage.SelectedIndex = _currentLang == "fa" ? 1 : 0;

                var themeObj = AioCatalog.Themes.FirstOrDefault(t => t.Id == themeId) ?? AioCatalog.Themes[0];
                _currentTheme = themeObj;
                ComboThemes.SelectedItem = themeObj;

                if (!string.IsNullOrWhiteSpace(ifaceName) && InterfaceCombo.ItemsSource is IEnumerable<NetworkAdaptorItem> list)
                {
                    var match = list.FirstOrDefault(x => x.Name.Equals(ifaceName, StringComparison.OrdinalIgnoreCase));
                    if (match != null)
                    {
                        InterfaceCombo.SelectedItem = match;
                        _selectedInterface = match.Name;
                    }
                }
            }
        }

        private void LoadNetworkInterfaces(bool preserveSelection = false)
        {
            string previousName = _selectedInterface;
            var adaptors = _service.ScanSystemAdaptors();

            InterfaceCombo.ItemsSource = adaptors;
            if (adaptors.Count > 0)
            {
                if (preserveSelection && !string.IsNullOrEmpty(previousName))
                {
                    var prevMatch = adaptors.FirstOrDefault(a => a.Name.Equals(previousName, StringComparison.OrdinalIgnoreCase));
                    if (prevMatch != null)
                    {
                        InterfaceCombo.SelectedItem = prevMatch;
                        _selectedInterface = prevMatch.Name;
                        return;
                    }
                }

                // Automatically select the first Connected (Green) adaptor
                var connectedGreen = adaptors.FirstOrDefault(a => a.HealthState == AdaptorHealthState.ConnectedGreen) ?? adaptors[0];
                InterfaceCombo.SelectedItem = connectedGreen;
                _selectedInterface = connectedGreen.Name;
            }
        }

        // ==================== BILINGUAL LANGUAGE ENGINE (ENGLISH & SIMPLE PERSIAN) ====================
        private void ApplyLanguage(string langCode)
        {
            _currentLang = langCode == "fa" ? "fa" : "en";
            bool fa = _currentLang == "fa";

            // Sidebar & Navigation (No numbers, no parentheses)
            LblNetworkAdaptor.Text = fa ? "کارت شبکه" : "Network Adaptor";
            NavDashboard.Content   = fa ? "⚡  داشبورد" : "⚡  Dashboard";
            NavDnsTools.Content    = fa ? "🌐  مدیریت دی‌ان‌اس" : "🌐  DNS Studio";
            NavNetworkInfo.Content = fa ? "📡  اطلاعات شبکه" : "📡  Network Info";
            NavWifi.Content        = fa ? "📶  مدیریت وای‌فای" : "📶  Wi-Fi Manager";
            NavFixes.Content       = fa ? "🛡️  تعمیر و هاست" : "🛡️  Fixes & Hosts";
            NavMaintenance.Content = fa ? "🔧  نگهداری ویندوز" : "🔧  Maintenance";
            NavSettings.Content    = fa ? "🎨  تنظیمات" : "🎨  Settings";
            NavHelp.Content        = fa ? "❓  راهنما" : "❓  Help";

            // Top Ping Cards
            LblPingCard1.Text      = fa ? "پینگ جهانی · 4.2.2.4" : "GLOBAL · 4.2.2.4";
            LblPingCard2.Text      = fa ? "اینترانت · آروان" : "INTRANET · ARVAN";
            LblActiveDnsCard.Text  = fa ? "دی‌ان‌اس فعال" : "ACTIVE DNS";

            // Dashboard View
            LblDashStatusHeader.Text  = fa ? "وضعیت لحظه‌ای شبکه سیستم" : "SYSTEM NETWORK STATUS";
            if (BtnGameBooster != null) BtnGameBooster.Content = fa ? "🎮 بهینه‌ساز گیمینگ" : "🎮 Game Booster";
            BtnDashFastestDns.Content = fa ? "⚡ سریع‌ترین دی‌ان‌اس" : "⚡ Fastest DNS";
            BtnDashAutoDns.Content    = fa ? "↺ دی‌ان‌اس خودکار" : "↺ Automatic DNS";
            BtnDashFlushDns.Content   = fa ? "🧹 پاکسازی کش" : "🧹 Flush Cache";
            LblDashQuickDnsTitle.Text = fa ? "تغییر سریع دی‌ان‌اس با یک کلیک" : "QUICK DNS SWITCHER";

            // DNS Studio View
            LblDnsConfigTitle.Text      = fa ? "تنظیم دی‌ان‌اس" : "DNS Configurator";
            BtnApplySelectedDns.Content = fa ? "اعمال دی‌ان‌اس انتخابی" : "Apply Selected DNS";
            LblCustomDnsTitle.Text      = fa ? "دی‌ان‌اس دستی" : "Custom IPv4 DNS";
            LblPrimaryDns.Text          = fa ? "اصلی:" : "Primary:";
            LblSecondaryDns.Text        = fa ? "فرعی:" : "Secondary:";
            BtnApplyCustomDns.Content   = fa ? "ثبت دی‌ان‌اس دستی" : "Apply Custom DNS";

            LblDnsBenchTitle.Text       = fa ? "تست سرعت و عملیات دی‌ان‌اس" : "DNS Benchmark & Actions";
            BtnBenchmarkDns.Content     = fa ? "⚡ تست پینگ همه سرورها" : "⚡ Benchmark All DNS";
            BtnApplyFastestWinner.Content = fa ? "✓ ثبت سریع‌ترین" : "✓ Apply Fastest";
            BtnScanCurrentDns.Content   = fa ? "بررسی دی‌ان‌اس فعلی" : "Scan Current DNS";
            BtnResetDns.Content         = fa ? "دی‌ان‌اس خودکار" : "Automatic DNS";
            BtnFlushCache.Content       = fa ? "پاکسازی کش دی‌ان‌اس" : "Flush DNS Cache";
            BtnFullAdapterReset.Content = fa ? "ریست کارت شبکه" : "Reset Adaptor";

            LblDohBackupTitle.Text  = fa ? "پشتیبان‌گیری و دی‌ان‌اس امن" : "DNS Backup & Encrypted DoH";
            BtnBackupDns.Content    = fa ? "پشتیبان‌گیری" : "Backup DNS";
            BtnRestoreDns.Content   = fa ? "بازگردانی" : "Restore Backup";
            BtnShowDnsBackup.Content = fa ? "نمایش پشتیبان" : "View Backup";
            BtnDohCloudflare.Content = fa ? "🔒 کلودفلر امن" : "🔒 Cloudflare DoH";
            BtnDohGoogle.Content     = fa ? "🔒 گوگل امن" : "🔒 Google DoH";
            BtnDohQuad9.Content      = fa ? "🔒 کواد۹ امن" : "🔒 Quad9 DoH";
            BtnDohShow.Content       = fa ? "وضعیت DoH" : "DoH Status";
            BtnDohRemove.Content     = fa ? "حذف DoH" : "Remove DoH";

            // Network Info View
            LblNetToolsTitle.Text        = fa ? "بررسی تخصصی شبکه" : "Network Inspection";
            if (BtnSpeedTest != null) BtnSpeedTest.Content = fa ? "🚀 تست سرعت دانلود" : "🚀 Speed Test";
            if (BtnNatAnalyzer != null) BtnNatAnalyzer.Content = fa ? "🌍 تحلیل‌گر نوع NAT" : "🌍 NAT Analyzer";
            BtnIpInfo.Content            = fa ? "اطلاعات آی‌پی و مک" : "IP & MAC Info";
            BtnPublicIp.Content          = fa ? "آی‌پی اینترنت" : "Public IP";
            BtnActiveConnections.Content = fa ? "اتصالات فعال" : "Active TCP Connections";
            BtnDataUsage.Content         = fa ? "حجم مصرفی" : "Data Usage";
            BtnDnsLookup.Content         = fa ? "تست پاسخ دی‌ان‌اس" : "DNS Lookup";
            BtnDnsLeakCheck.Content      = fa ? "تست نشت دی‌ان‌اس" : "DNS Leak Check";

            LblPingPortTitle.Text = fa ? "مانیتور پینگ، مسیر و بررسی پورت" : "Ping Monitor, Traceroute & Port Scanner";
            LblTargetHost.Text    = fa ? "آدرس:" : "Host:";
            LblPackets.Text       = fa ? "تعداد:" : "Packets:";
            BtnPingMonitor.Content = fa ? "شروع تست پینگ" : "Ping Monitor";
            BtnTraceroute.Content  = fa ? "ردیابی مسیر" : "Traceroute";
            LblPort.Text           = fa ? "پورت:" : "Port:";
            BtnPortCheck.Content   = fa ? "بررسی پورت" : "Check Port";

            // Wi-Fi View
            LblWifiVaultTitle.Text      = fa ? "وضعیت وای‌فای و رمزهای ذخیره‌شده" : "Wi-Fi Status & Saved Passwords";
            BtnWifiStatus.Content       = fa ? "وضعیت وای‌فای" : "Wi-Fi Status";
            BtnWifiPasswords.Content    = fa ? "🔑 نمایش رمزها" : "🔑 Saved Passwords";
            BtnWifiCopyPassword.Content = fa ? "📋 کپی رمز" : "📋 Copy Password";
            BtnWifiForget.Content       = fa ? "🗑️ فراموشی شبکه" : "🗑️ Forget Network";

            // Fixes & Hosts View
            LblStaticIpTitle.Text     = fa ? "تنظیم آی‌پی ثابت و خودکار" : "Static IPv4 & DHCP";
            BtnApplyStaticIp.Content  = fa ? "ثبت آی‌پی ثابت" : "Apply Static IP";
            BtnSwitchIpDhcp.Content   = fa ? "آی‌پی خودکار" : "Automatic DHCP";

            LblHostsStudioTitle.Text     = fa ? "مدیریت فایل هاست و مسدودساز" : "Hosts File & Blocklists";
            BtnHostsShow.Content         = fa ? "نمایش قوانین فعال" : "Show Custom Rules";
            BtnHostsNotepad.Content      = fa ? "باز کردن در نوت‌پد" : "Open in Notepad";
            BtnHostsBackup.Content       = fa ? "پشتیبان‌گیری" : "Backup Hosts";
            BtnHostsRestore.Content      = fa ? "بازگردانی" : "Restore Backup";
            BtnHostsResetDefault.Content = fa ? "بازگشت به پیش‌فرض" : "Reset Default";
            BtnApplyBlocklist.Content    = fa ? "➕ افزودن لیست مسدودساز" : "➕ Apply Blocklist";
            BtnRemoveBlocklist.Content   = fa ? "➖ حذف لیست" : "➖ Remove Blocklist";

            LblNetRepairTitle.Text   = fa ? "پروکسی، فایروال و تعمیر شبکه" : "Proxy, Firewall & Network Repair";
            BtnProxyView.Content     = fa ? "وضعیت پروکسی" : "Proxy Status";
            BtnProxyDisable.Content  = fa ? "خاموش کردن پروکسی" : "Disable Proxy";
            BtnFirewallReset.Content = fa ? "ریست فایروال" : "Reset Firewall";
            BtnArpReset.Content      = fa ? "پاکسازی کش ARP" : "Clear ARP & Routes";
            BtnFullNetRepair.Content = fa ? "تعمیر کامل شبکه" : "Full Network Repair";

            // Maintenance View
            LblMaintTempTitle.Text      = fa ? "پاکسازی فایل‌های موقت ویندوز" : "Temporary Files Cleaner";
            BtnCleanTemp.Content        = fa ? "🧹 پاکسازی فایل‌های اضافی" : "🧹 Clean Temp Files";
            LblMaintDismTitle.Text      = fa ? "تعمیر فایل‌های سیستمی ویندوز" : "Windows System File Repair";
            BtnDismSfc.Content          = fa ? "🛡️ شروع تعمیر سیستمی" : "🛡️ Run DISM & SFC Repair";
            LblMaintUpdateTitle.Text    = fa ? "ریست کش آپدیت ویندوز" : "Windows Update Cache Reset";
            BtnWindowsUpdateReset.Content = fa ? "🔄 ریست سرویس آپدیت" : "🔄 Reset Windows Update";

            // Settings View
            LblThemeStudioTitle.Text    = fa ? "پوسته، زبان و تنظیمات برنامه" : "Themes, Language & Preferences";
            LblThemeSelect.Text         = fa ? "پوسته:" : "Theme:";
            LblLanguageSelect.Text      = fa ? "زبان:" : "Language:";
            ChkDarkMode.Content         = fa ? "حالت تاریک" : "Dark Mode";
            ChkRememberSettings.Content = fa ? "ذخیره تنظیمات" : "Remember Settings";
            ChkEnableLogging.Content    = fa ? "ثبت گزارش" : "Save Action Log";
            LblActionLogTitle.Text      = fa ? "گزارش عملکرد" : "Action Log";
            BtnViewLog.Content          = fa ? "بروزرسانی گزارش" : "Refresh Log";
            BtnClearLog.Content         = fa ? "پاکسازی گزارش" : "Clear Log";

            // Bottom Console
            LblConsoleHeader.Text = fa ? "▶ خروجی زنده دستورات سیستم" : "▶ LIVE COMMAND STREAM";
            BtnClearConsole.Content = fa ? "پاکسازی" : "Clear";

            UpdateActiveDnsCardHighlight(_lastDetectedPrimaryDns);
        }

        // ==================== REAL-TIME SYSTEM SYNC & 3 LIVE PINGS ====================
        private async Task RefreshRealTimeTelemetryAsync()
        {
            if (_isTelemetryRunning) return;

            _isTelemetryRunning = true;
            try
            {
                // 1. Auto-detect PC-to-Router / Wi-Fi Default Gateway IP
                _detectedRouterIp = _service.DetectRouterGatewayIp(_selectedInterface);
                bool hasRouter = !string.IsNullOrWhiteSpace(_detectedRouterIp);

                TxtRouterCardTitle.Text = hasRouter
                    ? $"ROUTER · {_detectedRouterIp}"
                    : "PC ↔ ROUTER";

                // 2. Run all 3 live pings in parallel:
                var tGlobal   = _service.PingSingleHostFastAsync("4.2.2.4", 900);
                var tIntranet = _service.PingSingleHostFastAsync("www.arvancloud.ir", 900);
                var tRouter   = hasRouter
                    ? _service.PingSingleHostFastAsync(_detectedRouterIp, 600)
                    : Task.FromResult(9999L);

                await Task.WhenAll(tGlobal, tIntranet, tRouter);

                long msGlobal   = tGlobal.Result;
                long msIntranet = tIntranet.Result;
                long msRouter   = tRouter.Result;

                _globalPingTracker.PushSample(msGlobal);
                _intranetPingTracker.PushSample(msIntranet);
                _routerPingTracker.PushSample(msRouter);

                // Update Top-Left Internet Status LED (Green = Connected to Internet, Red = Disconnected)
                bool isInternetConnected = msGlobal < 9999 || msIntranet < 9999;
                Color ledColor = (Color)ColorConverter.ConvertFromString(isInternetConnected ? "#10B981" : "#F43F5E");
                HeaderStatusLed.Fill = new SolidColorBrush(ledColor);
                HeaderStatusGlow.Color = ledColor;

                UpdateLivePingCardUi(_globalPingTracker, TxtPingGlobal, TxtJitterGlobal, TxtStatsGlobal, TxtBadgeGlobal, BarPingGlobal, false, 250.0);
                UpdateLivePingCardUi(_intranetPingTracker, TxtPingIntranet, TxtJitterIntranet, TxtStatsIntranet, TxtBadgeIntranet, BarPingIntranet, false, 120.0);
                UpdateLivePingCardUi(_routerPingTracker, TxtPingRouter, TxtJitterRouter, TxtStatsRouter, TxtBadgeRouter, BarPingRouter, true, 50.0, _detectedRouterIp);

                string gStr = msGlobal >= 9999 ? "Timeout" : $"{msGlobal}ms";
                string iStr = msIntranet >= 9999 ? "Timeout" : $"{msIntranet}ms";
                string rStr = !hasRouter ? "N/A" : msRouter >= 9999 ? "Timeout" : msRouter == 0 ? "<1ms" : $"{msRouter}ms";

                // 3. Query Fresh Windows OS Network Adaptor State (Detects external manual DNS/IP changes!)
                var nic = NetworkInterface.GetAllNetworkInterfaces()
                    .FirstOrDefault(n => n.Name == _selectedInterface);
                if (nic != null)
                {
                    var stats = nic.GetIPStatistics();
                    double rxMb = Math.Round(stats.BytesReceived / 1048576.0, 1);
                    double txMb = Math.Round(stats.BytesSent / 1048576.0, 1);
                    long currentRx = stats.BytesReceived;
                    long currentTx = stats.BytesSent;
                    if (_lastTick != DateTime.MinValue)
                    {
                        double secs = (DateTime.Now - _lastTick).TotalSeconds;
                        if (secs > 0)
                        {
                            TxtSpeedDl.Text = $"⬇ {FormatSpeed((currentRx - _lastRxBytes) / secs)}";
                            TxtSpeedUl.Text = $"⬆ {FormatSpeed((currentTx - _lastTxBytes) / secs)}";
                        }
                    }
                    _lastRxBytes = currentRx;
                    _lastTxBytes = currentTx;
                    _lastTick = DateTime.Now;

                    TxtDataUsage.Text = $"Rx {rxMb:N1} MB  ·  Tx {txMb:N1} MB";
                    TxtAdapterState.Text = $"{nic.OperationalStatus}";

                    var ipProps = nic.GetIPProperties();
                    var dnsServers = ipProps.DnsAddresses
                        .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                        .Select(a => a.ToString())
                        .ToList();

                    string primaryDns = dnsServers.FirstOrDefault() ?? "DHCP";
                    var matchedProvider = AioCatalog.DnsProviders.FirstOrDefault(p => p.PrimaryIp == primaryDns);
                    TxtActiveDns.Text = matchedProvider != null
                        ? $"{primaryDns} · {matchedProvider.Name}"
                        : (dnsServers.Count > 0 ? string.Join(" , ", dnsServers) : "Automatic DHCP");

                    if (_lastDetectedPrimaryDns != primaryDns)
                    {
                        _lastDetectedPrimaryDns = primaryDns;
                        UpdateActiveDnsCardHighlight(primaryDns);
                    }

                    string ipv4 = ipProps.UnicastAddresses
                        .FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString() ?? "N/A";

                    TxtDashboardAdapterSummary.Text =
                        $"Adaptor: {nic.Name}   ·   IPv4: {ipv4}   ·   Router: {(hasRouter ? _detectedRouterIp : "None")} · {rStr}\r\n" +
                        $"System DNS: {(dnsServers.Count > 0 ? string.Join(" , ", dnsServers) : "Automatic DHCP")}" +
                        (matchedProvider != null ? $"   ·   {matchedProvider.Name}" : "");

                    TxtHeaderQuickSummary.Text = $"4.2.2.4: {gStr}  ·  arvancloud.ir: {iStr}  ·  Router: {rStr}  ·  DNS: {primaryDns}";
                }
            }
            catch { }
            finally
            {
                _isTelemetryRunning = false;
            }
        }

        private void UpdateLivePingCardUi(
            PingChannelTracker tracker,
            TextBlock txtMainPing,
            TextBlock txtJitter,
            TextBlock txtStats,
            TextBlock txtBadge,
            Border gaugeBar,
            bool isRouterChannel,
            double maxScaleMs,
            string routerIpLabel = "")
        {
            long ms = tracker.CurrentMs;
            if (ms >= 9999)
            {
                txtMainPing.Text = "TIMEOUT";
                txtJitter.Text = "Loss";
                txtBadge.Text = "OFFLINE";
                AnimateBarWidth(gaugeBar, 12);
            }
            else
            {
                txtMainPing.Text = (isRouterChannel && ms == 0) ? "<1 ms" : $"{ms} ms";
                txtJitter.Text = $"±{tracker.JitterMs} ms";
                if (isRouterChannel)
                    txtBadge.Text = ms <= 3 ? "ULTRA LOW" : ms <= 12 ? "WIFI OK" : "HIGH LAG";
                else
                    txtBadge.Text = ms <= 35 ? "EXCELLENT" : ms <= 85 ? "GOOD" : ms <= 150 ? "FAIR" : "HIGH PING";

                double parentWidth = (gaugeBar.Parent as FrameworkElement)?.ActualWidth ?? 200;
                if (parentWidth <= 10) parentWidth = 200;
                double ratio = Math.Clamp(ms / maxScaleMs, 0.08, 1.0);
                AnimateBarWidth(gaugeBar, parentWidth * ratio);
            }

            if (isRouterChannel && string.IsNullOrEmpty(routerIpLabel))
                txtStats.Text = "No active router gateway";
            else
                txtStats.Text = $"Min {tracker.MinMs}ms  ·  Avg {tracker.AvgMs}ms  ·  Max {tracker.MaxMs}ms";
        }

        private static void AnimateBarWidth(Border bar, double targetWidth)
        {
            var anim = new DoubleAnimation(targetWidth, TimeSpan.FromMilliseconds(320))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            bar.BeginAnimation(WidthProperty, anim);
        }

        // ==================== NAVIGATION & SETTINGS HANDLERS ====================
        private void Nav_Checked(object sender, RoutedEventArgs e)
        {
            if (!_isInitialized || sender is not RadioButton rb || rb.Tag is not string section) return;

            ViewDashboard.Visibility   = section == "Dashboard"   ? Visibility.Visible : Visibility.Collapsed;
            ViewDnsTools.Visibility    = section == "DnsTools"    ? Visibility.Visible : Visibility.Collapsed;
            ViewNetworkInfo.Visibility = section == "NetworkInfo" ? Visibility.Visible : Visibility.Collapsed;
            ViewWifi.Visibility        = section == "Wifi"        ? Visibility.Visible : Visibility.Collapsed;
            ViewFixes.Visibility       = section == "Fixes"       ? Visibility.Visible : Visibility.Collapsed;
            ViewMaintenance.Visibility = section == "Maintenance" ? Visibility.Visible : Visibility.Collapsed;
            ViewSettings.Visibility    = section == "Settings"    ? Visibility.Visible : Visibility.Collapsed;
            ViewHelp.Visibility        = section == "Help"        ? Visibility.Visible : Visibility.Collapsed;

            var fadeAnim = new DoubleAnimation(0.2, 1.0, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            var slideAnim = new DoubleAnimation(12.0, 0.0, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            WorkspaceAnimatedRoot.BeginAnimation(OpacityProperty, fadeAnim);
            WorkspaceSlideTransform.BeginAnimation(TranslateTransform.YProperty, slideAnim);
        }

        private void InterfaceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (InterfaceCombo.SelectedItem is NetworkAdaptorItem item)
            {
                _selectedInterface = item.Name;
                if (_isInitialized)
                {
                    _service.SaveSettings(_currentTheme.Id, _selectedInterface, ChkDarkMode.IsChecked == true, _currentLang);
                    _ = RefreshRealTimeTelemetryAsync();
                }
            }
        }

        private void BtnRefreshAdapters_Click(object sender, RoutedEventArgs e) => LoadNetworkInterfaces(preserveSelection: true);

        private void ChkDarkMode_Changed(object sender, RoutedEventArgs e)
        {
            if (!_isInitialized) return;
            bool isDark = ChkDarkMode.IsChecked == true;
            ThemeManager.ApplyTheme(_currentTheme, isDark);
            _service.SaveSettings(_currentTheme.Id, _selectedInterface, isDark, _currentLang);
        }

        private void ComboThemes_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isInitialized || ComboThemes.SelectedItem is not ThemeDefinition theme) return;
            _currentTheme = theme;
            ThemeManager.ApplyTheme(_currentTheme, ChkDarkMode.IsChecked == true);
            _service.SaveSettings(_currentTheme.Id, _selectedInterface, ChkDarkMode.IsChecked == true, _currentLang);
        }

        private void ComboLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isInitialized || ComboLanguage.SelectedItem is not ComboBoxItem item || item.Tag is not string langCode) return;
            ApplyLanguage(langCode);
            _service.SaveSettings(_currentTheme.Id, _selectedInterface, ChkDarkMode.IsChecked == true, _currentLang);
        }

        private void ChkSettings_Changed(object sender, RoutedEventArgs e)
        {
            if (!_isInitialized) return;
            _service.RememberSettings = ChkRememberSettings.IsChecked == true;
            _service.EnableActionLog = ChkEnableLogging.IsChecked == true;
            _service.SaveSettings(_currentTheme.Id, _selectedInterface, ChkDarkMode.IsChecked == true, _currentLang);
        }

        // ==================== DNS STUDIO HANDLERS ====================
        private async Task ApplyDnsProviderWithUiFeedbackAsync(DnsProviderModel provider)
        {
            try
            {
                AppendConsoleLine($"Applying DNS {provider.Name} {provider.PrimaryIp} , {provider.SecondaryIp} on {_selectedInterface}...");
                await _service.ApplyDnsAsync(_selectedInterface, provider.PrimaryIp, provider.SecondaryIp, provider.Name);
                await RefreshRealTimeTelemetryAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "DNS Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void BtnApplySelectedDns_Click(object sender, RoutedEventArgs e)
        {
            if (ComboDnsProviders.SelectedItem is DnsProviderModel provider)
                await ApplyDnsProviderWithUiFeedbackAsync(provider);
        }

        private async void BtnApplyCustomDns_Click(object sender, RoutedEventArgs e)
        {
            string d1 = TxtCustomDns1.Text.Trim();
            string d2 = TxtCustomDns2.Text.Trim();
            if (!AioNetworkService.ValidateIpv4(d1))
            {
                MessageBox.Show("Primary DNS is not a valid IPv4 address.", "Invalid IPv4", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!string.IsNullOrEmpty(d2) && !AioNetworkService.ValidateIpv4(d2))
            {
                MessageBox.Show("Secondary DNS is not a valid IPv4 address.", "Invalid IPv4", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            await _service.ApplyDnsAsync(_selectedInterface, d1, d2, "Custom");
            await RefreshRealTimeTelemetryAsync();
        }

        private async void BtnScanCurrentDns_Click(object sender, RoutedEventArgs e) => await _service.ScanCurrentDnsAsync(_selectedInterface);

        private async void BtnResetDnsDhcp_Click(object sender, RoutedEventArgs e)
        {
            await _service.ResetDnsToDhcpAsync(_selectedInterface);
            await RefreshRealTimeTelemetryAsync();
        }

        private async void BtnFlushDns_Click(object sender, RoutedEventArgs e) => await _service.FlushDnsCacheAsync(_selectedInterface);

        private async void BtnFullAdapterReset_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show($"Reset network adaptor {_selectedInterface}?", "Confirm Adaptor Reset", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                await _service.FullAdapterResetProtocolAsync(_selectedInterface);
                LoadNetworkInterfaces(preserveSelection: true);
                await RefreshRealTimeTelemetryAsync();
            }
        }

        private async void BtnBenchmarkDns_Click(object sender, RoutedEventArgs e)
        {
            AppendConsoleLine("Benchmarking all 16 DNS providers in parallel...");
            var results = await _service.BenchmarkAllDnsAsync();
            GridDnsBenchmark.ItemsSource = results;
            _fastestWinner = results.FirstOrDefault(r => r.PingMs < 9999);
            if (_fastestWinner != null)
            {
                AppendConsoleLine($"Fastest DNS Winner: {_fastestWinner.Name} {_fastestWinner.PrimaryIp} at {_fastestWinner.PingMs} ms");
                if (MessageBox.Show($"Fastest DNS found:\r\n{_fastestWinner.Name} — {_fastestWinner.PrimaryIp} — {_fastestWinner.PingMs} ms\r\n\r\nApply it now?",
                    "Fastest DNS Found", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                {
                    await _service.ApplyDnsAsync(_selectedInterface, _fastestWinner.PrimaryIp, _fastestWinner.SecondaryIp, _fastestWinner.Name);
                    await RefreshRealTimeTelemetryAsync();
                }
            }
        }

        private async void BtnApplyFastestWinner_Click(object sender, RoutedEventArgs e)
        {
            if (_fastestWinner == null)
            {
                MessageBox.Show("Run the benchmark first to find the fastest DNS provider.", "Benchmark Required", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            await _service.ApplyDnsAsync(_selectedInterface, _fastestWinner.PrimaryIp, _fastestWinner.SecondaryIp, _fastestWinner.Name);
            await RefreshRealTimeTelemetryAsync();
        }

        private async void BtnDnsLookup_Click(object sender, RoutedEventArgs e) =>
            TxtNetInfoReport.Text = await _service.RunNslookupAsync("google.com");

        private async void BtnDnsLeakCheck_Click(object sender, RoutedEventArgs e) =>
            TxtNetInfoReport.Text = await _service.RunNslookupAsync("whoami.akamai.net");

        private async void BtnBackupDns_Click(object sender, RoutedEventArgs e)
        {
            string info = await _service.BackupDnsAsync(_selectedInterface);
            MessageBox.Show($"DNS Backup saved to:\r\n{_service.DnsBackupFilePath}\r\n\r\n{info}", "DNS Backup Saved", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private async void BtnRestoreDns_Click(object sender, RoutedEventArgs e)
        {
            bool ok = await _service.RestoreDnsBackupAsync(_selectedInterface);
            if (!ok) MessageBox.Show("No valid DNS backup found. Use Backup first.", "Restore DNS", MessageBoxButton.OK, MessageBoxImage.Warning);
            else await RefreshRealTimeTelemetryAsync();
        }

        private void BtnShowDnsBackup_Click(object sender, RoutedEventArgs e) =>
            MessageBox.Show(_service.ReadSavedDnsBackup(), "Saved DNS Backup", MessageBoxButton.OK, MessageBoxImage.Information);

        private async void BtnDohCloudflare_Click(object sender, RoutedEventArgs e) =>
            await _service.EnableDnsOverHttpsAsync(_selectedInterface, "1.1.1.1", "1.0.0.1", "https://cloudflare-dns.com/dns-query", "Cloudflare");

        private async void BtnDohGoogle_Click(object sender, RoutedEventArgs e) =>
            await _service.EnableDnsOverHttpsAsync(_selectedInterface, "8.8.8.8", "8.8.4.4", "https://dns.google/dns-query", "Google");

        private async void BtnDohQuad9_Click(object sender, RoutedEventArgs e) =>
            await _service.EnableDnsOverHttpsAsync(_selectedInterface, "9.9.9.9", "149.112.112.112", "https://dns.quad9.net/dns-query", "Quad9");

        private async void BtnDohShow_Click(object sender, RoutedEventArgs e) => await _service.ShowDohStatusAsync();

        private async void BtnDohRemove_Click(object sender, RoutedEventArgs e) => await _service.RemoveAllDohSettingsAsync(_selectedInterface);

        // ==================== NETWORK INFO HANDLERS ====================
        private async void BtnGameBooster_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("This will clear RAM and stop background Windows updates/telemetry to boost gaming performance. Proceed?", "Game Booster", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                await _service.OptimizeForGamingAsync();
            }
        }

        private async void BtnSpeedTest_Click(object sender, RoutedEventArgs e)
        {
            TxtNetInfoReport.Text = "Running 10MB Download Speed Test... Please wait.\r\n";
            string res = await _service.RunSpeedTestAsync();
            TxtNetInfoReport.Text += res;
        }

        private async void BtnNatAnalyzer_Click(object sender, RoutedEventArgs e)
        {
            TxtNetInfoReport.Text = "Analyzing NAT Type & Port Forwarding capabilities...\r\n";
            string res = await _service.AnalyzeNatTypeAsync();
            TxtNetInfoReport.Text += res;
        }

        private async void BtnIpInfo_Click(object sender, RoutedEventArgs e) =>
            TxtNetInfoReport.Text = await _service.GetIpAndMacInfoAsync(_selectedInterface);

        private async void BtnPublicIp_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                TxtNetInfoReport.Text = "Querying ipinfo.io...";
                var (ip, country, org) = await _service.GetPublicIpInfoAsync();
                TxtNetInfoReport.Text = $"Public IP  : {ip}\r\nCountry    : {country}\r\nNetwork    : {org}";
            }
            catch (Exception ex)
            {
                TxtNetInfoReport.Text = $"Failed to fetch public IP: {ex.Message}";
            }
        }

        private async void BtnActiveConnections_Click(object sender, RoutedEventArgs e)
        {
            TxtNetInfoReport.Text = "Fetching active TCP connections...";
            TxtNetInfoReport.Text = await _service.GetActiveTcpConnectionsReportAsync();
        }

        private void BtnDataUsage_Click(object sender, RoutedEventArgs e) =>
            TxtNetInfoReport.Text = _service.GetAdapterDataUsageReport();

        private async void BtnPingMonitor_Click(object sender, RoutedEventArgs e)
        {
            string host = TxtNetTargetHost.Text.Trim();
            if (!AioNetworkService.ValidateHostOrIp(host))
            {
                MessageBox.Show("Invalid host or IPv4 address.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            int count = 20;
            if (ComboPingPackets.SelectedItem is ComboBoxItem item && int.TryParse(item.Content?.ToString(), out int c))
                count = c;

            TxtNetInfoReport.Text = $"Running {count}-packet Ping Monitor to {host}...";
            TxtNetInfoReport.Text = await _service.RunPingAsync(host, count);
        }

        private async void BtnTraceroute_Click(object sender, RoutedEventArgs e)
        {
            string host = TxtNetTargetHost.Text.Trim();
            if (!AioNetworkService.ValidateHostOrIp(host))
            {
                MessageBox.Show("Invalid host or IPv4 address.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            TxtNetInfoReport.Text = $"Tracing route to {host}...";
            TxtNetInfoReport.Text = await _service.RunTracerouteAsync(host);
        }

        private async void BtnPortCheck_Click(object sender, RoutedEventArgs e)
        {
            string host = TxtNetTargetHost.Text.Trim();
            if (!AioNetworkService.ValidateHostOrIp(host) || !int.TryParse(TxtPortNumber.Text.Trim(), out int port) || port < 1 || port > 65535)
            {
                MessageBox.Show("Enter a valid host and port number 1-65535.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            TxtNetInfoReport.Text = $"Testing TCP connection to {host}:{port}...";
            bool open = await _service.CheckTcpPortAsync(host, port);
            TxtNetInfoReport.Text = open
                ? $"OPEN — {host} port {port} accepts TCP connections."
                : $"CLOSED — {host} port {port} is closed or filtered.";
        }

        // ==================== WI-FI MANAGER HANDLERS ====================
        private async void BtnWifiStatus_Click(object sender, RoutedEventArgs e) =>
            TxtWifiDetails.Text = await _service.GetWifiStatusAsync();

        private async void BtnWifiPasswords_Click(object sender, RoutedEventArgs e)
        {
            TxtWifiDetails.Text = "Scanning saved WLAN profiles and extracting keys...";
            var items = await _service.GetSavedWifiPasswordsAsync();
            GridWifiPasswords.ItemsSource = items;
            TxtWifiDetails.Text = $"Extracted {items.Count} saved Wi-Fi profiles.";
        }

        private void BtnWifiCopyPassword_Click(object sender, RoutedEventArgs e)
        {
            if (GridWifiPasswords.SelectedItem is not WifiCredentialItem selected)
            {
                MessageBox.Show("Select a Wi-Fi profile from the table first.", "Copy Password", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            Clipboard.SetText(selected.Password);
            AppendConsoleLine($"Copied password for {selected.Ssid} to clipboard.");
        }

        private async void BtnWifiForget_Click(object sender, RoutedEventArgs e)
        {
            if (GridWifiPasswords.SelectedItem is not WifiCredentialItem selected)
            {
                MessageBox.Show("Select a Wi-Fi profile from the table first.", "Select Profile", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (MessageBox.Show($"Forget Wi-Fi network {selected.Ssid}?", "Confirm Forget", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                await _service.ForgetWifiNetworkAsync(selected.Ssid, _selectedInterface);
                GridWifiPasswords.ItemsSource = await _service.GetSavedWifiPasswordsAsync();
            }
        }

        // ==================== FIXES & HOSTS HANDLERS ====================
        private async void BtnApplyStaticIp_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await _service.ApplyStaticIpAsync(_selectedInterface, TxtStaticIp.Text.Trim(), TxtStaticMask.Text.Trim(), TxtStaticGateway.Text.Trim());
                await RefreshRealTimeTelemetryAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Static IP Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void BtnSwitchIpDhcp_Click(object sender, RoutedEventArgs e)
        {
            await _service.SwitchIpToDhcpAsync(_selectedInterface);
            await RefreshRealTimeTelemetryAsync();
        }

        private void BtnHostsShow_Click(object sender, RoutedEventArgs e) =>
            TxtHostsPreview.Text = _service.GetCustomHostsEntries();

        private void BtnHostsNotepad_Click(object sender, RoutedEventArgs e) =>
            _service.OpenHostsInNotepad();

        private void BtnHostsBackup_Click(object sender, RoutedEventArgs e)
        {
            _service.BackupHostsFile(_selectedInterface);
            TxtHostsPreview.Text = $"Backed up hosts file to: {_service.HostsBackupFilePath}";
        }

        private async void BtnHostsRestore_Click(object sender, RoutedEventArgs e)
        {
            bool ok = await _service.RestoreHostsFileAsync(_selectedInterface);
            TxtHostsPreview.Text = ok ? "Restored hosts file from backup." : "No backup file found.";
        }

        private async void BtnHostsResetDefault_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("Reset Windows hosts file to default?", "Reset Hosts", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                await _service.ResetHostsToDefaultAsync(_selectedInterface);
                TxtHostsPreview.Text = _service.GetCustomHostsEntries();
            }
        }

        private async void BtnApplyBlocklist_Click(object sender, RoutedEventArgs e)
        {
            if (ComboBlocklists.SelectedItem is not BlocklistDefinition list) return;
            try
            {
                TxtHostsPreview.Text = $"Applying blocklist {list.Name}...";
                int count = await _service.ApplyBlocklistAsync(list, _selectedInterface);
                TxtHostsPreview.Text = $"Added {count} rules from {list.Name}.\r\n\r\n" + _service.GetCustomHostsEntries();
            }
            catch (Exception ex)
            {
                TxtHostsPreview.Text = $"Error applying blocklist: {ex.Message}";
            }
        }

        private void BtnRemoveBlocklist_Click(object sender, RoutedEventArgs e)
        {
            if (ComboBlocklists.SelectedItem is not BlocklistDefinition list) return;
            _service.RemoveBlocklistByTag(list.Tag);
            TxtHostsPreview.Text = $"Removed blocklist {list.Name}.\r\n\r\n" + _service.GetCustomHostsEntries();
        }

        private async void BtnProxyView_Click(object sender, RoutedEventArgs e) =>
            TxtHostsPreview.Text = await _service.GetProxyStatusAsync();

        private async void BtnProxyDisable_Click(object sender, RoutedEventArgs e)
        {
            await _service.DisableSystemProxyAsync(_selectedInterface);
            TxtHostsPreview.Text = await _service.GetProxyStatusAsync();
        }

        private async void BtnFirewallReset_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("Reset Windows Defender Firewall to default rules?", "Confirm Firewall Reset", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                await _service.ResetFirewallAsync(_selectedInterface);
        }

        private async void BtnArpReset_Click(object sender, RoutedEventArgs e) =>
            await _service.ResetArpAndRoutesAsync(_selectedInterface);

        private async void BtnFullNetRepair_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("Reset Winsock catalog and TCP/IP stack? A PC restart is required afterwards.", "Full Network Repair", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                await _service.FullNetworkRepairAsync(_selectedInterface);
        }

        // ==================== MAINTENANCE & LOG HANDLERS ====================
        private async void BtnCleanTemp_Click(object sender, RoutedEventArgs e) =>
            MessageBox.Show(await _service.CleanTempFilesAsync(_selectedInterface), "Temp Cleaner", MessageBoxButton.OK, MessageBoxImage.Information);

        private async void BtnDismSfc_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("Start DISM RestoreHealth and SFC scannow?", "System Repair", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                await _service.RunSystemRepairDismSfcAsync(_selectedInterface);
        }

        private async void BtnWindowsUpdateReset_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("Stop Windows Update services and reset update cache folders?", "Windows Update Reset", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                await _service.ResetWindowsUpdateAsync(_selectedInterface);
        }

        private void BtnViewLog_Click(object sender, RoutedEventArgs e) =>
            TxtActionLogContent.Text = _service.ReadActionLog();

        private void BtnClearLog_Click(object sender, RoutedEventArgs e)
        {
            _service.ClearActionLog();
            TxtActionLogContent.Text = _service.ReadActionLog();
        }

        private void BtnCancelCommand_Click(object sender, RoutedEventArgs e) => _service.CancelCurrentOperation();
        private void BtnClearConsole_Click(object sender, RoutedEventArgs e) => ConsoleOutputBox.Clear();

        private void AppendConsoleLine(string line)
        {
            Dispatcher.Invoke(() =>
            {
                ConsoleOutputBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
                ConsoleOutputBox.ScrollToEnd();
            });
        }
    }
}
