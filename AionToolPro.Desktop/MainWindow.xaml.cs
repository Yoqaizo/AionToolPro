using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using AionToolPro.Desktop.Models;
using AionToolPro.Desktop.Services;
using AionToolPro.Desktop.Themes;

namespace AionToolPro.Desktop
{
    public partial class MainWindow : Window
    {
        private readonly AioNetworkService _service = new();
        private readonly DispatcherTimer _nicTimer = new() { Interval = TimeSpan.FromSeconds(1.0) };
        private readonly DispatcherTimer _pingTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
        private readonly DispatcherTimer _consoleFlushTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
        private readonly ConcurrentQueue<string> _consoleQueue = new();
        private readonly NetworkAddressChangedEventHandler _networkAddressChangedHandler;
        private CancellationTokenSource? _networkChangeDebounceCts;
        private TrayIconManager? _trayManager;

        private string _selectedInterface = "Ethernet";
        private string _detectedRouterIp = string.Empty;
        private string _lastDetectedPrimaryDns = string.Empty;
        private static readonly (string Host, string LabelEn, string LabelFa)[] GlobalPingTargets =
        {
            ("1.1.1.1", "1.1.1.1", "1.1.1.1"),
            ("8.8.8.8", "8.8.8.8", "8.8.8.8"),
            ("9.9.9.9", "9.9.9.9", "9.9.9.9"),
            ("4.2.2.4", "4.2.2.4", "4.2.2.4")
        };

        private static readonly (string Host, string LabelEn, string LabelFa)[] IntranetPingTargets =
        {
            ("www.arvancloud.ir",   "ARVAN",   "آروان"),
            ("www.salatingames.ir", "SALATIN", "سلاطین‌گیم"),
            ("parsvds.com",         "PARSVDS", "پارس‌وی‌دی‌اس"),
            ("iran.ir",             "IRAN.IR", "ایران")
        };

        private string _globalPingTarget = "1.1.1.1";
        private string _intranetPingTarget = "www.arvancloud.ir";
        private string _currentLang = "en";
        private ThemeDefinition _currentTheme = AioCatalog.Themes[0];
        private AppSurfacePalette _currentSurface = AioCatalog.SurfacePalettes[0];
        private DnsBenchmarkRow? _fastestWinner;
        private List<AppNetworkUsageRow> _allAppRows = new();

        private bool _isInitialized;
        private bool _isClosing;
        private bool _forceExit;
        private bool _isNicRunning;
        private bool _isPingRunning;
        private bool _pingRescanRequested;
        private bool _autoRefreshAppMonitor;
        private int _pingTickCounter;

        private long _lastRxBytes;
        private long _lastTxBytes;
        private DateTime _lastTick = DateTime.MinValue;

        private int _pingGeneration;
        private long _baseInDrops = -1;
        private long _baseOutDrops = -1;
        private long _lastNicDropCount;

        private readonly PingChannelTracker _globalPingTracker = new();
        private readonly PingChannelTracker _intranetPingTracker = new();
        private readonly PingChannelTracker _routerPingTracker = new();

        private readonly Dictionary<string, (Border Pill, TextBlock PillText)> _dnsCardBadges = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, Button> _themeSwatchButtons = new();
        private readonly Dictionary<int, Button> _surfaceSwatchButtons = new();

        private static Color ParseColor(string hex) => ColorConverter.ConvertFromString(hex) is Color c ? c : Colors.White;

        private static readonly Color GreenColor = ParseColor("#10B981");
        private static readonly Color AmberColor = ParseColor("#F59E0B");
        private static readonly Color RedColor = ParseColor("#F43F5E");
        private static readonly SolidColorBrush FrozenGreen = Freeze(new SolidColorBrush(GreenColor));
        private static readonly SolidColorBrush FrozenAmber = Freeze(new SolidColorBrush(AmberColor));
        private static readonly SolidColorBrush FrozenRed = Freeze(new SolidColorBrush(RedColor));
        private static readonly SolidColorBrush FrozenActivePillBg = Freeze(new SolidColorBrush(Color.FromArgb(55, GreenColor.R, GreenColor.G, GreenColor.B)));

        private static SolidColorBrush Freeze(SolidColorBrush b)
        {
            b.Freeze();
            return b;
        }

        private static string FormatSpeed(double bytesPerSec)
        {
            if (bytesPerSec <= 0) return "0 KB/s";
            if (bytesPerSec >= 1048576) return $"{bytesPerSec / 1048576.0:F1} MB/s";
            return $"{bytesPerSec / 1024.0:F0} KB/s";
        }

        public MainWindow()
        {
            InitializeComponent();
            _service.CommandOutputReceived += EnqueueConsoleLine;

            PopulateCatalogControls();
            ComboLanguage.SelectedIndex = 0;

            _consoleFlushTimer.Tick += (_, _) => FlushConsoleQueue();
            _consoleFlushTimer.Start();

            _networkAddressChangedHandler = (_, _) =>
            {
                if (_isClosing) return;
                Dispatcher.InvokeAsync(ScheduleDebouncedNetworkRefresh);
            };
            NetworkChange.NetworkAddressChanged += _networkAddressChangedHandler;

            _nicTimer.Tick += async (_, _) => await RefreshNicTelemetryAsync();
            _pingTimer.Tick += async (_, _) => await RefreshPingTelemetryAsync();
            Loaded += MainWindow_Loaded;
            StateChanged += MainWindow_StateChanged;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            _trayManager = new TrayIconManager(this, BuildTrayContextMenu(), RestoreFromTray);
            _trayManager.Initialize();
        }

        private ContextMenu BuildTrayContextMenu()
        {
            var menu = new ContextMenu();

            var miOpen = new MenuItem { Header = "⚡ Open AionToolPro", FontWeight = FontWeights.Bold };
            miOpen.Click += (_, _) => RestoreFromTray();
            menu.Items.Add(miOpen);
            menu.Items.Add(new Separator());

            foreach (var name in new[] { "Shecan", "Electro", "Cloudflare", "Google" })
            {
                var provider = AioCatalog.DnsProviders.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (provider != null)
                {
                    var miDns = new MenuItem { Header = $"🌐 DNS: {provider.Name} ({provider.PrimaryIp})" };
                    miDns.Click += async (_, _) => await ApplyDnsProviderWithUiFeedbackAsync(provider);
                    menu.Items.Add(miDns);
                }
            }

            var miDhcp = new MenuItem { Header = "↺ Automatic DNS (DHCP)" };
            miDhcp.Click += async (_, _) => await SafeRunAsync(async () =>
            {
                await _service.ResetDnsToDhcpAsync(_selectedInterface);
                await RefreshRealTimeTelemetryAsync();
            }, "TrayResetDNS");
            menu.Items.Add(miDhcp);

            menu.Items.Add(new Separator());
            var miExit = new MenuItem { Header = "✕ Exit AionToolPro" };
            miExit.Click += (_, _) =>
            {
                _forceExit = true;
                Close();
            };
            menu.Items.Add(miExit);
            return menu;
        }

        private void RestoreFromTray()
        {
            Show();
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            Activate();
            Focus();
            _ = RefreshRealTimeTelemetryAsync();
        }

        private void MainWindow_StateChanged(object? sender, EventArgs e)
        {
            if (_isInitialized && WindowState == WindowState.Minimized && ChkMinimizeToTray.IsChecked == true)
            {
                Hide();
            }
        }

        public void RequestCleanShutdown()
        {
            _forceExit = true;
            try { Close(); } catch { }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // External WM_CLOSE (Inno Setup upgrades, Task Manager, Windows shutdown) ALWAYS exits cleanly.
            // Minimize-to-Tray is handled explicitly when the user clicks the titlebar X button or Minimize.
            base.OnClosing(e);
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            await SafeRunAsync(async () =>
            {
                await LoadNetworkInterfacesAsync(preserveSelection: false);
                LoadPersistedSettingsIfAvailable();

                bool autoStartOn = await _service.IsStartWithWindowsEnabledAsync();
                ChkStartWithWindows.IsChecked = autoStartOn;

                TxtAppVersion.Text = AioNetworkService.GetAppVersionString();
                _isInitialized = true;
                ThemeManager.ApplyTheme(_currentTheme, ChkDarkMode.IsChecked == true, _currentSurface.Id);
                ApplyLanguage(_currentLang);

                _nicTimer.Start();
                _pingTimer.Start();
                await RefreshRealTimeTelemetryAsync();
            }, "Startup");
        }

        private async void ScheduleDebouncedNetworkRefresh()
        {
            try
            {
                _networkChangeDebounceCts?.Cancel();
                _networkChangeDebounceCts?.Dispose();
                _networkChangeDebounceCts = new CancellationTokenSource();
                var token = _networkChangeDebounceCts.Token;

                await Task.Delay(500, token);
                if (_isClosing) return;

                await LoadNetworkInterfacesAsync(preserveSelection: true);
                await RefreshRealTimeTelemetryAsync();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                App.WriteCrashLog("NetworkChangeDebounce", ex);
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _isClosing = true;
            try
            {
                _nicTimer.Stop();
                _pingTimer.Stop();
                _consoleFlushTimer.Stop();
                _networkChangeDebounceCts?.Cancel();
                _networkChangeDebounceCts?.Dispose();
                NetworkChange.NetworkAddressChanged -= _networkAddressChangedHandler;
                _service.CommandOutputReceived -= EnqueueConsoleLine;
                _trayManager?.Dispose();
                _service.Dispose();
            }
            catch { }
            base.OnClosed(e);
        }

        private async Task SafeRunAsync(Func<Task> action, string context, bool showErrorDialog = true)
        {
            try
            {
                await action();
            }
            catch (OperationCanceledException)
            {
                EnqueueConsoleLine($"[{context}] Operation cancelled.");
            }
            catch (Exception ex)
            {
                App.WriteCrashLog(context, ex);
                EnqueueConsoleLine($"[{context} ERR] {ex.Message}");
                if (showErrorDialog && !_isClosing)
                {
                    MessageBox.Show(ex.Message, $"AionToolPro — {context}", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
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
                try { DragMove(); } catch { }
            }
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void BtnMaximize_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            if (!_forceExit && _isInitialized && ChkMinimizeToTray.IsChecked == true)
            {
                Hide();
                return;
            }
            _forceExit = true;
            Close();
        }

        // ==================== INITIALIZATION & VISUAL CARDS ====================
        private void PopulateCatalogControls()
        {
            RefreshDnsCatalogUi();

            ComboBlocklists.ItemsSource = AioCatalog.Blocklists;
            ComboBlocklists.SelectedIndex = 0;

            BuildThemeSwatchCards();
            BuildSurfaceSwatchCards();
            UpdateSwatchSelectionHighlights();
        }

        private void RefreshDnsCatalogUi(string? searchFilter = null)
        {
            var allProviders = _service.GetAllDnsProviders();
            ComboDnsProviders.ItemsSource = allProviders;
            if (ComboDnsProviders.SelectedIndex < 0 && allProviders.Count > 0)
                ComboDnsProviders.SelectedIndex = 0;

            BuildDashboardDnsCards(allProviders, searchFilter ?? TxtSearchDns?.Text);
            UpdateActiveDnsCardHighlight(_lastDetectedPrimaryDns);
        }

        private void BuildDashboardDnsCards(IReadOnlyList<DnsProviderModel> providers, string? filter = null)
        {
            DashboardDnsWrapPanel.Children.Clear();
            _dnsCardBadges.Clear();

            string q = (filter ?? string.Empty).Trim();
            var visible = string.IsNullOrEmpty(q)
                ? providers
                : providers.Where(p =>
                    p.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    p.PrimaryIp.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    p.SecondaryIp.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    p.CategoryLabel.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();

            foreach (var p in visible)
            {
                var badgeColor = ParseColor(p.BadgeHex);
                var badgeBrush = Freeze(new SolidColorBrush(badgeColor));
                var badgeBgBrush = Freeze(new SolidColorBrush(Color.FromArgb(35, badgeColor.R, badgeColor.G, badgeColor.B)));

                var cardStack = new StackPanel { FlowDirection = FlowDirection.LeftToRight };

                var topRow = new DockPanel();
                var titleTxt = new TextBlock
                {
                    Text = p.Name,
                    FontSize = 13,
                    FontWeight = FontWeights.Bold
                };
                titleTxt.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryTextBrush");

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
                    Margin = new Thickness(0, 5, 0, 2)
                };
                ipTxt.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");

                var descTxt = new TextBlock
                {
                    Text = p.Description,
                    FontSize = 10.5,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                descTxt.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");

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

        private void TxtSearchDns_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_isInitialized) return;
            RefreshDnsCatalogUi(TxtSearchDns.Text);
        }

        private void UpdateActiveDnsCardHighlight(string currentPrimaryDns)
        {
            foreach (var p in _service.GetAllDnsProviders())
            {
                if (!_dnsCardBadges.TryGetValue(p.PrimaryIp, out var badgeInfo))
                    continue;

                bool isActive = string.Equals(p.PrimaryIp, currentPrimaryDns, StringComparison.OrdinalIgnoreCase);
                if (isActive)
                {
                    badgeInfo.Pill.Background = FrozenActivePillBg;
                    badgeInfo.Pill.BorderBrush = FrozenGreen;
                    badgeInfo.PillText.Foreground = FrozenGreen;
                    badgeInfo.PillText.Text = _currentLang == "fa" ? "✓ فعال" : "✓ ACTIVE";
                }
                else
                {
                    var badgeColor = ParseColor(p.BadgeHex);
                    var badgeBrush = Freeze(new SolidColorBrush(badgeColor));
                    var badgeBg = Freeze(new SolidColorBrush(Color.FromArgb(35, badgeColor.R, badgeColor.G, badgeColor.B)));
                    badgeInfo.Pill.Background = badgeBg;
                    badgeInfo.Pill.BorderBrush = badgeBrush;
                    badgeInfo.PillText.Foreground = badgeBrush;
                    badgeInfo.PillText.Text = p.CategoryLabel;
                }
            }
        }

        private void BuildThemeSwatchCards()
        {
            ThemeSwatchesWrapPanel.Children.Clear();
            _themeSwatchButtons.Clear();

            foreach (var t in AioCatalog.Themes)
            {
                var c1 = ParseColor(t.AccentHex);
                var c2 = ParseColor(t.SecondaryHex);
                var grad = new LinearGradientBrush(c1, c2, new Point(0, 0), new Point(1, 1));
                grad.Freeze();

                var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, FlowDirection = FlowDirection.LeftToRight };
                var swatch = new Border
                {
                    Width = 14,
                    Height = 14,
                    CornerRadius = new CornerRadius(7),
                    Background = grad,
                    Margin = new Thickness(0, 0, 7, 0)
                };
                var label = new TextBlock
                {
                    Text = t.Name,
                    FontSize = 11.5,
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
                    Padding = new Thickness(10, 5, 10, 5),
                    Margin = new Thickness(0, 0, 6, 6),
                    ToolTip = t.Subtitle
                };
                btn.Click += (s, _) =>
                {
                    if (s is Button b && b.Tag is ThemeDefinition selectedTheme)
                        SelectTheme(selectedTheme);
                };
                _themeSwatchButtons[t.Id] = btn;
                ThemeSwatchesWrapPanel.Children.Add(btn);
            }
        }

        private void BuildSurfaceSwatchCards()
        {
            SurfaceSwatchesWrapPanel.Children.Clear();
            _surfaceSwatchButtons.Clear();

            foreach (var s in AioCatalog.SurfacePalettes)
            {
                var c1 = ParseColor(s.CanvasHex);
                var c2 = ParseColor(s.CardHex);
                var borderCol = ParseColor(s.BorderHex);
                var grad = new LinearGradientBrush(c1, c2, new Point(0, 0), new Point(1, 1));
                grad.Freeze();

                var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, FlowDirection = FlowDirection.LeftToRight };
                var swatch = new Border
                {
                    Width = 14,
                    Height = 14,
                    CornerRadius = new CornerRadius(4),
                    Background = grad,
                    BorderBrush = Freeze(new SolidColorBrush(borderCol)),
                    BorderThickness = new Thickness(1),
                    Margin = new Thickness(0, 0, 7, 0)
                };
                var label = new TextBlock
                {
                    Text = s.Name,
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center
                };
                sp.Children.Add(swatch);
                sp.Children.Add(label);

                var btn = new Button
                {
                    Content = sp,
                    Style = (Style)FindResource("SecondaryBtnStyle"),
                    Tag = s,
                    Padding = new Thickness(9, 5, 9, 5),
                    Margin = new Thickness(0, 0, 6, 6)
                };
                btn.Click += (sender, _) =>
                {
                    if (sender is Button b && b.Tag is AppSurfacePalette selectedSurface)
                        SelectSurface(selectedSurface);
                };
                _surfaceSwatchButtons[s.Id] = btn;
                SurfaceSwatchesWrapPanel.Children.Add(btn);
            }
        }

        private void UpdateSwatchSelectionHighlights()
        {
            foreach (var kv in _themeSwatchButtons)
            {
                bool active = kv.Key == _currentTheme.Id;
                kv.Value.SetResourceReference(BorderBrushProperty, active ? "AccentBrush" : "BorderHairlineBrush");
                kv.Value.BorderThickness = active ? new Thickness(2) : new Thickness(1);
            }

            foreach (var kv in _surfaceSwatchButtons)
            {
                bool active = kv.Key == _currentSurface.Id;
                kv.Value.SetResourceReference(BorderBrushProperty, active ? "AccentBrush" : "BorderHairlineBrush");
                kv.Value.BorderThickness = active ? new Thickness(2) : new Thickness(1);
            }
        }

        private void SelectTheme(ThemeDefinition theme)
        {
            _currentTheme = theme;
            ThemeManager.ApplyTheme(_currentTheme, ChkDarkMode.IsChecked == true, _currentSurface.Id);
            UpdateSwatchSelectionHighlights();
            if (_isInitialized) SaveCurrentSettings();
        }

        private void SelectSurface(AppSurfacePalette surface)
        {
            _currentSurface = surface;
            ThemeManager.ApplyTheme(_currentTheme, ChkDarkMode.IsChecked == true, _currentSurface.Id);
            UpdateSwatchSelectionHighlights();
            if (_isInitialized) SaveCurrentSettings();
        }

        private void LoadPersistedSettingsIfAvailable()
        {
            var saved = _service.LoadSavedSettings();
            if (saved == null) return;
            ApplySettingsModelToUi(saved);
        }

        private void ApplySettingsModelToUi(AppSettingsModel saved)
        {
            ChkEnableLogging.IsChecked = saved.LogEnabled;
            ChkDarkMode.IsChecked = saved.IsDarkMode;
            ChkMinimizeToTray.IsChecked = saved.MinimizeToTray;
            _service.EnableActionLog = saved.LogEnabled;

            _currentLang = saved.LanguageCode == "fa" ? "fa" : "en";
            ComboLanguage.SelectedIndex = _currentLang == "fa" ? 1 : 0;

            _globalPingTarget = GlobalPingTargets.Any(g => g.Host.Equals(saved.GlobalPingTarget, StringComparison.OrdinalIgnoreCase))
                ? saved.GlobalPingTarget.Trim()
                : GlobalPingTargets[0].Host;

            _intranetPingTarget = IntranetPingTargets.Any(i => i.Host.Equals(saved.IntranetPingTarget, StringComparison.OrdinalIgnoreCase))
                ? saved.IntranetPingTarget.Trim()
                : IntranetPingTargets[0].Host;

            _currentTheme = AioCatalog.Themes.FirstOrDefault(t => t.Id == saved.ThemeId) ?? AioCatalog.Themes[0];
            _currentSurface = AioCatalog.SurfacePalettes.FirstOrDefault(s => s.Id == saved.SurfacePaletteId) ?? AioCatalog.SurfacePalettes[0];
            UpdateSwatchSelectionHighlights();

            if (!string.IsNullOrWhiteSpace(saved.InterfaceName) &&
                InterfaceCombo.ItemsSource is IEnumerable<NetworkAdaptorItem> list)
            {
                var match = list.FirstOrDefault(x => x.Name.Equals(saved.InterfaceName, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    InterfaceCombo.SelectedItem = match;
                    _selectedInterface = match.Name;
                }
            }
        }

        private string GetIntranetShortLabel(bool fa)
        {
            var entry = IntranetPingTargets.FirstOrDefault(x => x.Host.Equals(_intranetPingTarget, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrEmpty(entry.Host))
                return fa ? "آروان" : "ARVAN";
            return fa ? entry.LabelFa : entry.LabelEn;
        }

        private void UpdatePingCardHeaders()
        {
            bool fa = _currentLang == "fa";
            string intraLbl = GetIntranetShortLabel(fa);
            LblPingCard1.Text = fa ? $"جهانی · {_globalPingTarget} ↻" : $"GLOBAL · {_globalPingTarget} ↻";
            LblPingCard2.Text = fa ? $"داخلی · {intraLbl} ↻" : $"INTRANET · {intraLbl} ↻";
            CardGlobalPing.ToolTip = fa
                ? $"سرور فعلی: {_globalPingTarget} — برای تغییر منبع پینگ جهانی کلیک کنید (1.1.1.1 / 8.8.8.8 / 9.9.9.9 / 4.2.2.4)"
                : $"Active: {_globalPingTarget} — Click to switch Global ping server (1.1.1.1 / 8.8.8.8 / 9.9.9.9 / 4.2.2.4)";
            CardIntranetPing.ToolTip = fa
                ? $"سرور فعلی: {_intranetPingTarget} — برای تغییر منبع پینگ داخلی کلیک کنید (آروان / سلاطین‌گیم / پارس‌وی‌دی‌اس / ایران)"
                : $"Active: {_intranetPingTarget} — Click to switch Intranet ping server (arvancloud.ir / salatingames.ir / parsvds.com / iran.ir)";
        }

        private void ResetLiveTelemetryBaselines()
        {
            _pingGeneration++;
            _globalPingTracker.Reset();
            _intranetPingTracker.Reset();
            _routerPingTracker.Reset();
            _baseInDrops = -1;
            _baseOutDrops = -1;
            _lastNicDropCount = 0;
            _lastRxBytes = 0;
            _lastTxBytes = 0;
            _lastTick = DateTime.MinValue;
        }

        private void SaveCurrentSettings()
        {
            _service.SaveSettings(
                _currentTheme.Id,
                _selectedInterface,
                ChkDarkMode.IsChecked == true,
                _currentLang,
                _currentSurface.Id,
                ChkMinimizeToTray.IsChecked == true,
                _globalPingTarget,
                _intranetPingTarget);
        }

        private async Task LoadNetworkInterfacesAsync(bool preserveSelection = false)
        {
            string previousName = _selectedInterface;
            var adaptors = await _service.ScanSystemAdaptorsAsync();
            if (_isClosing) return;

            InterfaceCombo.ItemsSource = adaptors;
            if (adaptors.Count == 0) return;

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

            var connectedGreen = adaptors.FirstOrDefault(a => a.HealthState == AdaptorHealthState.ConnectedGreen) ?? adaptors[0];
            InterfaceCombo.SelectedItem = connectedGreen;
            _selectedInterface = connectedGreen.Name;
        }

        // ==================== BILINGUAL & FULL RTL LANGUAGE ENGINE ====================
        private void ApplyLanguage(string langCode)
        {
            _currentLang = langCode == "fa" ? "fa" : "en";
            bool fa = _currentLang == "fa";

            // True RTL workspace switching while keeping TitleBar, IPs, DataGrids & Console LeftToRight
            MainWorkspaceGrid.FlowDirection = fa ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

            LblNetworkAdaptor.Text = fa ? "کارت شبکه" : "Network Adaptor";
            NavDashboard.Content   = fa ? "⚡  داشبورد" : "⚡  Dashboard";
            NavDnsTools.Content    = fa ? "🌐  مدیریت دی‌ان‌اس" : "🌐  DNS Studio";
            NavNetworkInfo.Content = fa ? "📡  اطلاعات شبکه" : "📡  Network Info";
            NavWifi.Content        = fa ? "📶  مدیریت وای‌فای" : "📶  Wi-Fi Manager";
            NavFixes.Content       = fa ? "🛡️  تعمیر و هاست" : "🛡️  Fixes & Hosts";
            NavMaintenance.Content = fa ? "🔧  نگهداری ویندوز" : "🔧  Maintenance";
            NavSettings.Content    = fa ? "🎨  تنظیمات" : "🎨  Settings";
            NavHelp.Content        = fa ? "❓  راهنما" : "❓  Help";

            UpdatePingCardHeaders();
            LblActiveDnsCard.Text  = fa ? "دی‌ان‌اس فعال" : "ACTIVE DNS";

            LblDashStatusHeader.Text      = fa ? "وضعیت لحظه‌ای شبکه سیستم" : "SYSTEM NETWORK STATUS";
            BtnGameBooster.Content        = fa ? "🎮 بهینه‌ساز گیمینگ" : "🎮 Game Booster";
            BtnRestoreNormalMode.Content  = fa ? "🔄 بازگردانی حالت عادی" : "🔄 Restore Normal";
            BtnDashFastestDns.Content     = fa ? "⚡ سریع‌ترین دی‌ان‌اس" : "⚡ Fastest DNS";
            BtnDashAutoDns.Content        = fa ? "↺ دی‌ان‌اس خودکار" : "↺ Automatic DNS";
            BtnDashFlushDns.Content       = fa ? "🧹 پاکسازی کش" : "🧹 Flush Cache";
            LblDashQuickDnsTitle.Text     = fa ? "تغییر سریع دی‌ان‌اس با یک کلیک" : "QUICK DNS SWITCHER";
            TxtSearchDns.ToolTip          = fa ? "جستجوی DNS بر اساس نام یا آی‌پی..." : "Search DNS by name or IP...";

            LblDnsConfigTitle.Text        = fa ? "تنظیم دی‌ان‌اس" : "DNS Configurator";
            BtnApplySelectedDns.Content   = fa ? "اعمال دی‌ان‌اس انتخابی" : "Apply Selected DNS";
            LblCustomDnsTitle.Text        = fa ? "دی‌ان‌اس دستی و پروفایل‌های شخصی" : "Custom IPv4 DNS & Saved Profiles";
            LblPrimaryDns.Text            = fa ? "اصلی:" : "Primary:";
            LblSecondaryDns.Text          = fa ? "فرعی:" : "Secondary:";
            LblCustomDnsName.Text         = fa ? "نام:" : "Name:";
            BtnApplyCustomDns.Content     = fa ? "ثبت دی‌ان‌اس دستی" : "Apply Custom DNS";
            BtnSaveCustomDns.Content      = fa ? "💾 ذخیره پروفایل" : "💾 Save Profile";
            BtnDeleteCustomDns.Content    = fa ? "🗑️ حذف پروفایل" : "🗑️ Delete Saved";

            LblDnsBenchTitle.Text         = fa ? "بنچمارک واقعی DNS و عملیات" : "DNS Benchmark (UDP/53) & Actions";
            BtnBenchmarkDns.Content       = fa ? "⚡ تست سرعت همه سرورها" : "⚡ Benchmark All DNS";
            BtnApplyFastestWinner.Content = fa ? "✓ ثبت سریع‌ترین" : "✓ Apply Fastest";
            BtnScanCurrentDns.Content     = fa ? "بررسی دی‌ان‌اس فعلی" : "Scan Current DNS";
            BtnResetDns.Content           = fa ? "دی‌ان‌اس خودکار" : "Automatic DNS";
            BtnFlushCache.Content         = fa ? "پاکسازی کش دی‌ان‌اس" : "Flush DNS Cache";
            BtnFullAdapterReset.Content   = fa ? "ریست کارت شبکه" : "Reset Adaptor";

            LblDohBackupTitle.Text  = fa ? "پشتیبان‌گیری و دی‌ان‌اس امن (ویندوز ۱۱)" : "DNS Backup & Encrypted DoH (Win11)";
            BtnBackupDns.Content    = fa ? "پشتیبان‌گیری" : "Backup DNS";
            BtnRestoreDns.Content   = fa ? "بازگردانی" : "Restore Backup";
            BtnShowDnsBackup.Content = fa ? "نمایش پشتیبان" : "View Backup";
            BtnDohCloudflare.Content = fa ? "🔒 کلودفلر امن" : "🔒 Cloudflare DoH";
            BtnDohGoogle.Content     = fa ? "🔒 گوگل امن" : "🔒 Google DoH";
            BtnDohQuad9.Content      = fa ? "🔒 کواد۹ امن" : "🔒 Quad9 DoH";
            BtnDohShow.Content       = fa ? "وضعیت DoH" : "DoH Status";
            BtnDohRemove.Content     = fa ? "حذف DoH برنامه" : "Remove App DoH";

            LblNetToolsTitle.Text        = fa ? "بررسی تخصصی شبکه" : "Network Inspection";
            BtnSpeedTest.Content         = fa ? "🚀 تست سرعت دانلود" : "🚀 Speed Test";
            BtnNatAnalyzer.Content       = fa ? "🌍 تحلیل STUN و NAT" : "🌍 STUN NAT & CGNAT";
            BtnGameServerPings.Content   = fa ? "🎮 پینگ سرورهای بازی" : "🎮 Game Server Pings";
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

            LblAppNetMonitorTitle.Text = fa ? "اتصالات فعال برنامه‌ها (IPv4/IPv6)" : "Active Connections by App (IPv4/IPv6)";
            TxtSearchAppNet.ToolTip    = fa ? "فیلتر بر اساس نام برنامه، PID یا آی‌پی..." : "Filter by process name, PID, or IP...";
            BtnRefreshAppNet.Content   = fa ? "🔄 اسکن برنامه‌های متصل" : "🔄 Scan Active Apps";
            BtnAutoAppNet.Content      = _autoRefreshAppMonitor
                ? (fa ? "⏱️ بروزرسانی خودکار: روشن" : "⏱️ Auto-Refresh: ON")
                : (fa ? "⏱️ بروزرسانی خودکار: خاموش" : "⏱️ Auto-Refresh: OFF");
            BtnKillAppNet.Content       = fa ? "❌ بستن برنامه" : "❌ Kill Process";
            BtnBlockAppNet.Content      = fa ? "🚫 قطع اینترنت در فایروال" : "🚫 Block in Firewall";
            BtnUnblockAppNet.Content    = fa ? "✅ وصل اینترنت در فایروال" : "✅ Unblock Firewall";
            BtnViewBlockedApps.Content  = fa ? "📋 لیست مسدودشده‌ها" : "📋 Blocked List";

            LblWifiVaultTitle.Text        = fa ? "وضعیت وای‌فای و رمزهای ذخیره‌شده" : "Wi-Fi Status & Saved Passwords";
            BtnWifiStatus.Content         = fa ? "وضعیت وای‌فای" : "Wi-Fi Status";
            BtnWifiPasswords.Content      = fa ? "🔑 نمایش پروفایل‌ها" : "🔑 Saved Profiles";
            BtnWifiCopyPassword.Content   = fa ? "📋 کپی رمز" : "📋 Copy Password";
            BtnWifiRevealPassword.Content = fa ? "👁️ مشاهده رمز" : "👁️ Show Password";
            BtnWifiForget.Content         = fa ? "🗑️ فراموشی شبکه" : "🗑️ Forget Network";

            LblStaticIpTitle.Text     = fa ? "تنظیم آی‌پی ثابت و خودکار" : "Static IPv4 & DHCP";
            BtnApplyStaticIp.Content  = fa ? "ثبت آی‌پی ثابت" : "Apply Static IP";
            BtnSwitchIpDhcp.Content   = fa ? "آی‌پی خودکار" : "Automatic DHCP";

            LblHostsStudioTitle.Text     = fa ? "مدیریت فایل هاست و مسدودساز" : "Hosts File & Blocklists";
            BtnHostsShow.Content         = fa ? "خلاصه قوانین فعلی" : "Show Rules Summary";
            BtnHostsNotepad.Content      = fa ? "باز کردن در نوت‌پد" : "Open in Notepad";
            BtnHostsBackup.Content       = fa ? "پشتیبان‌گیری" : "Backup Hosts";
            BtnHostsRestore.Content      = fa ? "بازگردانی" : "Restore Backup";
            BtnHostsResetDefault.Content = fa ? "بازگشت به پیش‌فرض" : "Reset Default";
            BtnApplyBlocklist.Content    = fa ? "➕ افزودن لیست مسدودساز" : "➕ Apply Blocklist";
            BtnRemoveBlocklist.Content   = fa ? "➖ حذف لیست" : "➖ Remove Blocklist";

            LblNetRepairTitle.Text   = fa ? "پروکسی، فایروال و تعمیر شبکه" : "Proxy, Firewall & Network Repair";
            BtnProxyView.Content     = fa ? "وضعیت پروکسی" : "Proxy Status";
            BtnProxyDisable.Content  = fa ? "خاموش کردن پروکسی" : "Disable Proxy";
            BtnFirewallReset.Content = fa ? "ریست فایروال (با بکاپ)" : "Reset Firewall (With Backup)";
            BtnArpReset.Content      = fa ? "پاکسازی کش ARP و مسیرها" : "Clear ARP & Routes";
            BtnFullNetRepair.Content = fa ? "تعمیر کامل شبکه" : "Full Network Repair";

            LblMaintTempTitle.Text        = fa ? "پاکسازی فایل‌های موقت قدیمی‌تر از ۲۴ ساعت" : "Temporary Files Cleaner (>24h old)";
            BtnCleanTemp.Content          = fa ? "🧹 پاکسازی فایل‌های اضافی" : "🧹 Clean Temp Files";
            LblMaintDismTitle.Text        = fa ? "تعمیر فایل‌های سیستمی ویندوز" : "Windows System File Repair";
            BtnDismSfc.Content            = fa ? "🛡️ شروع تعمیر سیستمی" : "🛡️ Run DISM & SFC Repair";
            LblMaintUpdateTitle.Text      = fa ? "ریست کش آپدیت ویندوز" : "Windows Update Cache Reset";
            BtnWindowsUpdateReset.Content = fa ? "🔄 ریست سرویس آپدیت" : "🔄 Reset Windows Update";

            LblAppUpdateTitle.Text      = fa ? "بروزرسانی و پشتیبان تنظیمات" : "Updates & Settings Backup";
            BtnCheckUpdate.Content      = fa ? "🔄 بررسی بروزرسانی" : "🔄 Check for Updates";
            BtnExportSettings.Content   = fa ? "📤 خروجی" : "📤 Export";
            BtnImportSettings.Content   = fa ? "📥 ورود" : "📥 Import";

            LblThemeStudioTitle.Text    = fa ? "ظاهر و تنظیمات برنامه" : "Appearance & Preferences";
            LblThemeSelect.Text         = fa ? "تم رنگی" : "Accent Theme";
            LblAppBgPaletteTitle.Text   = fa ? "رنگ پس‌زمینه برنامه" : "Background Surface";
            LblLanguageSelect.Text      = fa ? "زبان:" : "Language:";
            ChkDarkMode.Content         = fa ? "حالت تاریک" : "Dark Mode";
            ChkRememberSettings.Content = fa ? "ذخیره تنظیمات" : "Remember Settings";
            ChkEnableLogging.Content    = fa ? "ثبت گزارش" : "Save Action Log";
            ChkMinimizeToTray.Content   = fa ? "کوچک شدن در سینی سیستم (Tray)" : "Minimize to System Tray";
            ChkStartWithWindows.Content = fa ? "اجرای خودکار با ویندوز" : "Start with Windows";
            LblActionLogTitle.Text      = fa ? "گزارش عملکرد" : "Action Log";
            BtnViewLog.Content          = fa ? "بروزرسانی" : "Refresh";
            BtnClearLog.Content         = fa ? "پاکسازی" : "Clear";

            // Complete Bilingual Help (English LTR + Full Persian RTL)
            ViewHelp.FlowDirection  = fa ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
            LblHelpTitle.Text       = fa ? "راهنمای کامل امکانات و بخش‌های AionToolPro" : "AionToolPro — Complete Feature & Button Guide";
            LblHelpSubtitle.Text    = fa ? "معرفی کامل نشانگرها، کارت‌های پینگ زنده، ابزارهای شبکه و تنظیمات برنامه." : "Visual reference explaining every indicator, button, and tool inside AionToolPro.";

            LblHelpCard1Title.Text  = fa ? "⚡ چراغ وضعیت و کارت شبکه" : "⚡ Status Light & Network Adaptor";
            TxtHelpCard1Body.Text   = fa
                ? "• چراغ وضعیت بالا: سبز = اینترنت پایدار، نارنجی = پکت لاست بالا (بیش از ۱۰٪)، قرمز = قطع اتصال.\n• نشانگر کارت شبکه: اسکن خودکار آداپتورهای واقعی (سبز = متصل، نارنجی = قطع، قرمز = غیرفعال).\n• سرعت‌سنج مستقل ۱ ثانیه‌ای: نمایش لحظه‌ای سرعت دانلود/آپلود، حجم مصرفی و خطاهای سخت‌افزاری کارت شبکه بدون فریز شدن هنگام تایم‌اوت."
                : "• Status LED: Green = healthy internet, Amber = high packet loss (>=10%), Red = offline.\n• Network Adaptor Dots: Scans real physical adapters. Green = Connected, Orange = Disconnected, Red = Disabled.\n• Live Sync & Speedometer: Independent 1-second NIC stats (⬇/⬆ speed, Rx/Tx, NIC drops) never freeze during ping timeouts.";

            LblHelpCard2Title.Text  = fa ? "📊 مانیتور پینگ زنده (قابل کلیک)" : "📊 Interactive Live Ping Monitors";
            TxtHelpCard2Body.Text   = fa
                ? "• تغییر پینگ جهانی با کلیک: روی کارت پینگ جهانی کلیک کنید تا بین 1.1.1.1 (کلودفلر)، 8.8.8.8 (گوگل)، 9.9.9.9 و 4.2.2.4 جابه‌جا شود.\n• تغییر پینگ داخلی با کلیک: روی کارت پینگ داخلی کلیک کنید تا بین arvancloud.ir، salatingames.ir، parsvds.com و iran.ir جابه‌جا شود.\n• دقت سیستمی: ارسال بسته استاندارد ۳۲ بایتی ویندوز (TTL=128)، جیتر استاندارد RFC 3550، پکت لاست واقعی ۴۰ بسته و حالت خودکار TCP ONLY در صورت مسدود بودن ICMP."
                : "• Click Global Card to Switch Server: Cycles between 1.1.1.1 (Cloudflare), 8.8.8.8 (Google), 9.9.9.9 (Quad9), and 4.2.2.4.\n• Click Intranet Card to Switch Server: Cycles between arvancloud.ir, salatingames.ir, parsvds.com, and iran.ir.\n• System-Grade Accuracy: Standard 32-byte TTL=128 ICMP, RFC 3550 jitter, honest 40-packet loss, and automatic TCP ONLY fallback if ICMP is blocked.";

            LblHelpCard3Title.Text  = fa ? "🌐 استودیو دی‌ان‌اس، پروفایل شخصی و DoH" : "🌐 DNS Studio, Profiles & DoH";
            TxtHelpCard3Body.Text   = fa
                ? "• تغییر سریع و جستجو: با یک کلیک روی هر کارت DNS در داشبورد، دی‌ان‌اس اعمال و کش پاک می‌شود؛ برای جلوگیری از نشت، IPv6 روی DNSهای تحریم‌شکن خودکار ریست می‌شود.\n• پروفایل شخصی و بنچمارک واقعی: دی‌ان‌اس‌های دلخواه خود را ذخیره کنید و همه سرورها را با کوئری واقعی UDP پورت ۵۳ تست سرعت بگیرید.\n• دی‌ان‌اس امن (DoH): فعال‌سازی رمزنگاری بومی ویندوز ۱۱ برای کلودفلر، گوگل و کواد۹ همراه با بکاپ و بازگردانی."
                : "• Quick DNS & Search: Click any DNS card on the Dashboard or filter by name/IP. Automatically resets IPv6 DNS to prevent leaks on IPv4-only anti-sanction DNS.\n• Custom Profiles & UDP/53 Benchmark: Save your own DNS profiles and benchmark all resolvers using real UDP port 53 queries.\n• Windows 11 DoH: Enables encrypted DNS-over-HTTPS (udpfallback=yes) with safe backup/restore.";

            LblHelpCard4Title.Text  = fa ? "📡 بررسی شبکه، STUN NAT و مانیتور برنامه‌ها" : "📡 Diagnostics, STUN NAT & App Monitor";
            TxtHelpCard4Body.Text   = fa
                ? "• تست STUN و سرعت: تشخیص دقیق نوع NAT (Open / Cone / Symmetric) و CGNAT با پروتکل RFC 5389، تست سرعت دانلود دوکاناله کلودفلر و پینگ سرورهای بازی.\n• مانیتور اتصالات (IPv4/IPv6): مشاهده برنامه‌های متصل با فیلتر جستجو، محافظت از پروسه‌های حیاتی ویندوز و مسدودسازی/آزادسازی اینترنت برنامه‌ها در فایروال.\n• ابزارهای زنده: مانیتور پینگ، ردیابی مسیر (Traceroute) و بررسی باز بودن پورت TCP با قابلیت توقف لحظه‌ای."
                : "• STUN NAT & Speed Test: Dual-server RFC 5389 UDP STUN detects Open/Cone/Symmetric NAT & CGNAT; 2-stream Cloudflare speed test & Game Server Pings.\n• Active Connections (IPv4/IPv6): Live socket monitor with search, protected process guard, and SHA-256 hashed Windows Firewall block/unblock.\n• Ping Monitor, Traceroute & Port Check: Live cancellable diagnostics.";

            LblHelpCard5Title.Text  = fa ? "📶 مدیریت وای‌فای و فایل هاست (Hosts)" : "📶 Wi-Fi Manager & Atomic Hosts";
            TxtHelpCard5Body.Text   = fa
                ? "• پروفایل‌های وای‌فای: استخراج ایمن و مستقل از زبان ویندوز؛ رمزها به صورت پیش‌فرض مخفی هستند و می‌توانید آن‌ها را مشاهده، کپی یا شبکه را فراموش کنید.\n• فایل هاست و مسدودساز تبلیغات: نوشتن اتمیک و ایمن بدون BOM همراه با بکاپ خودکار تاریخ‌دار برای لیست‌های مسدودساز تبلیغات، بدافزار و تیم‌اسپیک."
                : "• Wi-Fi Profiles: Language-independent extraction with passwords masked by default — reveal, copy, or forget any saved network safely.\n• Hosts Blocklists: Atomic UTF-8 (no BOM) writes with automatic timestamped backups, multi-host line parsing, and ad/malware/TeamSpeak filters.";

            LblHelpCard6Title.Text  = fa ? "🛡️ گیم‌بوستر ایمن، نگهداری و بروزرسانی" : "🛡️ Game Booster, Maintenance & Updates";
            TxtHelpCard6Body.Text   = fa
                ? "• گیم‌بوستر ایمن: فقط سرویس‌های غیرضروری در حال اجرا را موقتاً متوقف می‌کند و با دکمه «بازگردانی حالت عادی» دقیقاً همان سرویس‌ها را برمی‌گرداند.\n• نگهداری و تعمیرات: پاکسازی امن فایل‌های موقت قدیمی‌تر از ۲۴ ساعت (با محافظت از کش .NET)، تعمیر سیستمی DISM+SFC، ریست ایمن آپدیت ویندوز، پروکسی و بکاپ فایروال.\n• سینی سیستم و آپدیت خودکار: تغییر سریع DNS از منوی Tray، اجرای خودکار با ویندوز و آپدیت خاموش گیت‌هاب با تایید هش SHA-256."
                : "• Safe Game Booster: Pauses only currently running non-essential services and restores exact original state with Restore Normal.\n• Maintenance & Repair: 24h-guarded Temp cleaner (protects %TEMP%\\.net), DISM+SFC, safe Windows Update reset, Proxy & Firewall backup/reset.\n• System Tray & Auto-Updater: Quick DNS tray menu, elevated Start-with-Windows, and SHA-256 verified silent GitHub updates.";

            LblConsoleHeader.Text    = fa ? "▶ خروجی زنده دستورات سیستم" : "▶ LIVE COMMAND STREAM";
            BtnCancelCommand.Content = fa ? "⏹ توقف" : "⏹ Stop";
            BtnClearConsole.Content  = fa ? "پاکسازی" : "Clear";

            // Localize DataGrid column headers
            if (GridDnsBenchmark.Columns.Count >= 6)
            {
                GridDnsBenchmark.Columns[1].Header = fa ? "ارائه‌دهنده" : "Provider";
                GridDnsBenchmark.Columns[2].Header = fa ? "دی‌ان‌اس اصلی" : "Primary IP";
                GridDnsBenchmark.Columns[3].Header = fa ? "دی‌ان‌اس فرعی" : "Secondary IP";
                GridDnsBenchmark.Columns[4].Header = fa ? "دسته" : "Category";
                GridDnsBenchmark.Columns[5].Header = fa ? "تاخیر واقعی (UDP/53)" : "DNS Latency (UDP/53)";
            }
            if (GridAppNetworkMonitor.Columns.Count >= 6)
            {
                GridAppNetworkMonitor.Columns[0].Header = fa ? "برنامه" : "Process";
                GridAppNetworkMonitor.Columns[2].Header = fa ? "اتصالات" : "Conns";
                GridAppNetworkMonitor.Columns[3].Header = fa ? "رم (MB)" : "RAM (MB)";
                GridAppNetworkMonitor.Columns[4].Header = fa ? "آدرس‌های مقصد (IPv4/IPv6)" : "Remote Endpoints (IPv4/IPv6)";
                GridAppNetworkMonitor.Columns[5].Header = fa ? "مسیر فایل" : "Executable Path";
            }
            if (GridWifiPasswords.Columns.Count >= 3)
            {
                GridWifiPasswords.Columns[0].Header = fa ? "نام شبکه (SSID)" : "Wi-Fi SSID";
                GridWifiPasswords.Columns[1].Header = fa ? "رمز عبور" : "Password";
                GridWifiPasswords.Columns[2].Header = fa ? "وضعیت امنیت" : "Security Status";
            }

            UpdateActiveDnsCardHighlight(_lastDetectedPrimaryDns);
        }

        // ==================== REAL-TIME TELEMETRY (DECOUPLED 1s NIC + 1.5s PING) ====================
        private async Task RefreshRealTimeTelemetryAsync()
        {
            await RefreshNicTelemetryAsync();
            await RefreshPingTelemetryAsync();
        }

        private async Task RefreshNicTelemetryAsync()
        {
            if (_isNicRunning || _isClosing || WindowState == WindowState.Minimized || !IsVisible)
                return;

            _isNicRunning = true;
            try
            {
                var snap = await _service.CaptureAdapterTelemetryAsync(_selectedInterface);
                if (_isClosing) return;

                _detectedRouterIp = snap.GatewayIp;
                bool hasRouter = !string.IsNullOrWhiteSpace(_detectedRouterIp);
                TxtRouterCardTitle.Text = hasRouter ? $"ROUTER · {_detectedRouterIp}" : "PC ↔ ROUTER";

                if (snap.Found)
                {
                    double rxMb = Math.Round(snap.BytesReceived / 1048576.0, 1);
                    double txMb = Math.Round(snap.BytesSent / 1048576.0, 1);

                    if (_lastTick != DateTime.MinValue)
                    {
                        double secs = (DateTime.Now - _lastTick).TotalSeconds;
                        if (secs > 0.1)
                        {
                            double dlRate = Math.Max(0, snap.BytesReceived - _lastRxBytes) / secs;
                            double ulRate = Math.Max(0, snap.BytesSent - _lastTxBytes) / secs;
                            TxtSpeedDl.Text = $"⬇ {FormatSpeed(dlRate)}";
                            TxtSpeedUl.Text = $"⬆ {FormatSpeed(ulRate)}";
                        }
                    }
                    _lastRxBytes = snap.BytesReceived;
                    _lastTxBytes = snap.BytesSent;
                    _lastTick = DateTime.Now;

                    long totalInDrops = snap.IncomingPacketsWithErrors + snap.IncomingPacketsDiscarded;
                    long totalOutDrops = snap.OutgoingPacketsWithErrors + snap.OutgoingPacketsDiscarded;

                    if (_baseInDrops < 0)
                    {
                        _baseInDrops = totalInDrops;
                        _baseOutDrops = totalOutDrops;
                    }

                    long deltaInDrops = Math.Max(0, totalInDrops - _baseInDrops);
                    long deltaOutDrops = Math.Max(0, totalOutDrops - _baseOutDrops);
                    _lastNicDropCount = deltaInDrops + deltaOutDrops;

                    UpdateHeaderLossIndicator(deltaInDrops, deltaOutDrops);

                    TxtDataUsage.Text = $"Rx {rxMb:N1} MB  ·  Tx {txMb:N1} MB";
                    TxtAdapterState.Text = snap.OperationalStatus;

                    string primaryDns = snap.DnsServers.FirstOrDefault() ?? "DHCP";
                    var matchedProvider = _service.GetAllDnsProviders().FirstOrDefault(p => p.PrimaryIp == primaryDns);
                    TxtActiveDns.Text = matchedProvider != null
                        ? $"{primaryDns} · {matchedProvider.Name}"
                        : (snap.DnsServers.Count > 0 ? string.Join(" , ", snap.DnsServers) : "Automatic DHCP");

                    if (_lastDetectedPrimaryDns != primaryDns)
                    {
                        _lastDetectedPrimaryDns = primaryDns;
                        UpdateActiveDnsCardHighlight(primaryDns);
                    }

                    string rStr = FormatShortPing(_routerPingTracker, hasRouter);
                    TxtDashboardAdapterSummary.Text =
                        $"Adaptor: {snap.InterfaceName}   ·   IPv4: {snap.Ipv4Address}   ·   Router: {(hasRouter ? _detectedRouterIp : "None")} · {rStr}\r\n" +
                        $"System DNS: {(snap.DnsServers.Count > 0 ? string.Join(" , ", snap.DnsServers) : "Automatic DHCP")}" +
                        (matchedProvider != null ? $"   ·   {matchedProvider.Name}" : "");
                }

                if (_autoRefreshAppMonitor && ViewNetworkInfo.Visibility == Visibility.Visible)
                {
                    await RefreshAppNetworkGridAsync();
                }
            }
            catch (Exception ex)
            {
                App.WriteCrashLog("NicTelemetry", ex);
            }
            finally
            {
                _isNicRunning = false;
            }
        }

        private async Task RefreshPingTelemetryAsync(bool forceImmediate = false)
        {
            if (_isClosing) return;
            if (_isPingRunning)
            {
                if (forceImmediate) _pingRescanRequested = true;
                return;
            }

            bool isHiddenOrMinimized = WindowState == WindowState.Minimized || !IsVisible;
            _pingTickCounter++;

            // When minimized to tray, keep Tray tooltip alive at low overhead (every ~6s = every 4th tick)
            if (!forceImmediate && isHiddenOrMinimized && (_pingTickCounter % 4 != 0))
                return;

            _isPingRunning = true;
            try
            {
                if (string.IsNullOrEmpty(_detectedRouterIp))
                {
                    _detectedRouterIp = await _service.DetectRouterGatewayIpAsync(_selectedInterface);
                }

                int probeGen = _pingGeneration;
                string globalTarget = _globalPingTarget;
                string intranetTarget = _intranetPingTarget;
                string intranetShort = GetIntranetShortLabel(fa: false);
                string routerIp = _detectedRouterIp;
                bool hasRouter = !string.IsNullOrWhiteSpace(routerIp);

                // Enable TCP fallback cleanly when ICMP appears blocked (>=2 consecutive drops or already in TCP mode),
                // while still probing ICMP first on every tick so recovery is automatic.
                bool tryGlobalTcp = _globalPingTracker.ConsecutiveTimeouts >= 2 || _globalPingTracker.CurrentTransport == ProbeTransport.TcpFallback;
                bool tryIntranetTcp = _intranetPingTracker.ConsecutiveTimeouts >= 2 || _intranetPingTracker.CurrentTransport == ProbeTransport.TcpFallback;
                bool tryRouterTcp = _routerPingTracker.ConsecutiveTimeouts >= 1 || _routerPingTracker.CurrentTransport == ProbeTransport.TcpFallback;

                var tRouter = hasRouter
                    ? _service.ProbeHostAccurateAsync(routerIp, timeoutMs: 1200, fallbackTcpPorts: new[] { 80, 443 }, allowTcpFallback: tryRouterTcp, treatTcpRefusedAsAlive: true)
                    : Task.FromResult(new PingProbeResult(null, ProbeTransport.Timeout));

                var tIntranet = _service.ProbeHostAccurateAsync(intranetTarget, timeoutMs: 2000, fallbackTcpPorts: new[] { 443, 80 }, allowTcpFallback: tryIntranetTcp);
                var tGlobal = _service.ProbeHostAccurateAsync(globalTarget, timeoutMs: 2000, fallbackTcpPorts: new[] { 443, 53 }, allowTcpFallback: tryGlobalTcp);

                await Task.WhenAll(tGlobal, tIntranet, tRouter);
                if (_isClosing || probeGen != _pingGeneration) return;

                _globalPingTracker.PushProbe(tGlobal.Result);
                _intranetPingTracker.PushProbe(tIntranet.Result);
                if (hasRouter)
                    _routerPingTracker.PushProbe(tRouter.Result);

                string gStr = FormatShortPing(_globalPingTracker, true);
                string iStr = FormatShortPing(_intranetPingTracker, true);
                string rStr = FormatShortPing(_routerPingTracker, hasRouter);

                _trayManager?.UpdateTooltip($"AionToolPro | {globalTarget}: {gStr} | {intranetShort}: {iStr} | Router: {rStr}");

                if (isHiddenOrMinimized)
                    return;

                bool globalOnline = _globalPingTracker.CurrentMs.HasValue || (_globalPingTracker.ConsecutiveTimeouts == 1 && _globalPingTracker.LastValidMs.HasValue);
                bool intranetOnline = _intranetPingTracker.CurrentMs.HasValue || (_intranetPingTracker.ConsecutiveTimeouts == 1 && _intranetPingTracker.LastValidMs.HasValue);
                bool isConnected = globalOnline || intranetOnline;

                if (!isConnected)
                {
                    HeaderStatusLed.Fill = FrozenRed;
                    HeaderStatusGlow.Color = RedColor;
                }
                else if (_globalPingTracker.LossPercent >= 10.0)
                {
                    HeaderStatusLed.Fill = FrozenAmber;
                    HeaderStatusGlow.Color = AmberColor;
                }
                else
                {
                    HeaderStatusLed.Fill = FrozenGreen;
                    HeaderStatusGlow.Color = GreenColor;
                }

                UpdateLivePingCardUi(_globalPingTracker, TxtPingGlobal, TxtJitterGlobal, TxtStatsGlobal, TxtBadgeGlobal, BarPingGlobal, false, 250.0);
                UpdateLivePingCardUi(_intranetPingTracker, TxtPingIntranet, TxtJitterIntranet, TxtStatsIntranet, TxtBadgeIntranet, BarPingIntranet, false, 120.0);
                UpdateLivePingCardUi(_routerPingTracker, TxtPingRouter, TxtJitterRouter, TxtStatsRouter, TxtBadgeRouter, BarPingRouter, true, 50.0, routerIp);

                TxtHeaderQuickSummary.Text = $"{globalTarget}: {gStr}  ·  {intranetShort}: {iStr}  ·  Router: {rStr}";
                UpdateHeaderLossIndicator(_lastNicDropCount, 0, useCombinedCount: true);
            }
            catch (Exception ex)
            {
                App.WriteCrashLog("PingTelemetry", ex);
            }
            finally
            {
                _isPingRunning = false;
                if (_pingRescanRequested && !_isClosing)
                {
                    _pingRescanRequested = false;
                    _ = Dispatcher.InvokeAsync(() => RefreshPingTelemetryAsync(forceImmediate: true));
                }
            }
        }

        private void UpdateHeaderLossIndicator(long dropsA, long dropsB, bool useCombinedCount = false)
        {
            double netLoss = _globalPingTracker.HasSample ? _globalPingTracker.LossPercent : 0.0;
            long totalNicDrops = useCombinedCount ? dropsA : (dropsA + dropsB);

            TxtPacketLossIn.Text = $"Loss: {netLoss:F1}%";
            TxtPacketLossOut.Text = $"NIC: {totalNicDrops}d";

            TxtPacketLossIn.Foreground = netLoss <= 0.0 ? FrozenGreen : netLoss < 5.0 ? FrozenAmber : FrozenRed;
            TxtPacketLossOut.Foreground = totalNicDrops == 0 ? FrozenGreen : totalNicDrops < 10 ? FrozenAmber : FrozenRed;

            BdHeaderPacketLoss.ToolTip =
                $"Global ({_globalPingTarget}) Loss: {_globalPingTracker.LossPercent:F1}% ({_globalPingTracker.LostSamples}/{_globalPingTracker.WindowSamples})\n" +
                $"Intranet ({_intranetPingTarget}) Loss: {_intranetPingTracker.LossPercent:F1}% ({_intranetPingTracker.LostSamples}/{_intranetPingTracker.WindowSamples})\n" +
                $"Router Loss: {_routerPingTracker.LossPercent:F1}% ({_routerPingTracker.LostSamples}/{_routerPingTracker.WindowSamples})\n" +
                $"NIC Hardware/Driver Drops (Errors + Discards): {totalNicDrops}";
        }

        private static string FormatShortPing(PingChannelTracker tracker, bool hasTarget)
        {
            if (!hasTarget) return "N/A";
            if (!tracker.HasSample) return "--";
            if (!tracker.CurrentMs.HasValue) return "Timeout";
            long ms = tracker.CurrentMs.Value;
            string prefix = tracker.CurrentTransport == ProbeTransport.TcpFallback ? "TCP " : "";
            return ms == 0 ? $"{prefix}<1ms" : $"{prefix}{ms}ms";
        }

        private async Task RefreshAppNetworkGridAsync()
        {
            _allAppRows = await _service.GetPerAppNetworkUsageAsync();
            if (_isClosing) return;
            ApplyAppNetworkGridFilter();
        }

        private void ApplyAppNetworkGridFilter()
        {
            int? selectedPid = (GridAppNetworkMonitor.SelectedItem as AppNetworkUsageRow)?.Pid;
            string q = (TxtSearchAppNet?.Text ?? string.Empty).Trim();

            var filtered = string.IsNullOrEmpty(q)
                ? _allAppRows
                : _allAppRows.Where(r =>
                    r.ProcessName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    r.Pid.ToString().Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    r.RemoteEndpoints.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    r.ExecutablePath.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();

            GridAppNetworkMonitor.ItemsSource = filtered;
            if (selectedPid.HasValue)
            {
                var restored = filtered.FirstOrDefault(r => r.Pid == selectedPid.Value);
                if (restored != null)
                    GridAppNetworkMonitor.SelectedItem = restored;
            }
        }

        private void TxtSearchAppNet_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_isInitialized) return;
            ApplyAppNetworkGridFilter();
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
            if (isRouterChannel && string.IsNullOrEmpty(routerIpLabel))
            {
                txtMainPing.Text = "N/A";
                txtJitter.Text = "--";
                txtBadge.Text = "NO GATEWAY";
                txtStats.Text = "No active router gateway";
                AnimateBarWidth(gaugeBar, 12);
                return;
            }

            if (!tracker.HasSample)
            {
                txtMainPing.Text = "-- ms";
                txtJitter.Text = "±0 ms";
                txtBadge.Text = "WAITING";
                return;
            }

            if (!tracker.CurrentMs.HasValue)
            {
                txtMainPing.Text = tracker.ConsecutiveTimeouts == 1 ? "DROP" : "TIMEOUT";
                txtJitter.Text = $"Loss {tracker.LossPercent:F1}%";
                txtBadge.Text = tracker.ConsecutiveTimeouts == 1 ? "PACKET DROP" : "OFFLINE";
                AnimateBarWidth(gaugeBar, 12);
            }
            else
            {
                long ms = tracker.CurrentMs.Value;
                txtMainPing.Text = (isRouterChannel && ms == 0) ? "<1 ms" : $"{ms} ms";
                txtJitter.Text = $"±{tracker.JitterMs ?? 0}ms · L:{tracker.LossPercent:F1}%";

                if (tracker.CurrentTransport == ProbeTransport.TcpFallback)
                {
                    txtBadge.Text = "TCP ONLY";
                }
                else if (isRouterChannel)
                {
                    txtBadge.Text = ms <= 3 ? "ULTRA LOW" : ms <= 12 ? "WIFI OK" : "HIGH LAG";
                }
                else
                {
                    txtBadge.Text = ms <= 35 ? "EXCELLENT" : ms <= 85 ? "GOOD" : ms <= 150 ? "FAIR" : "HIGH PING";
                }

                double parentWidth = (gaugeBar.Parent as FrameworkElement)?.ActualWidth ?? 200;
                if (parentWidth <= 10) parentWidth = 200;
                double ratio = maxScaleMs > 0 ? Math.Clamp(ms / maxScaleMs, 0.08, 1.0) : 0.08;
                AnimateBarWidth(gaugeBar, parentWidth * ratio);
            }

            if (tracker.MinMs.HasValue)
                txtStats.Text = $"Loss {tracker.LossPercent:F1}% ({tracker.LostSamples}/{tracker.WindowSamples}) · Min {tracker.MinMs} · Avg {tracker.AvgMs} · Max {tracker.MaxMs}";
            else
                txtStats.Text = $"Loss {tracker.LossPercent:F1}% ({tracker.LostSamples}/{tracker.WindowSamples}) · Min -- · Avg -- · Max --";
        }

        private static void AnimateBarWidth(Border bar, double targetWidth)
        {
            if (double.IsNaN(targetWidth) || double.IsInfinity(targetWidth) || targetWidth < 0)
                targetWidth = 12;

            var anim = new DoubleAnimation(targetWidth, TimeSpan.FromMilliseconds(320))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            bar.BeginAnimation(WidthProperty, anim);
        }

        // ==================== NAVIGATION & SETTINGS HANDLERS ====================
        private async void Nav_Checked(object sender, RoutedEventArgs e)
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

            if (section == "NetworkInfo" && GridAppNetworkMonitor.ItemsSource == null)
            {
                await SafeRunAsync(RefreshAppNetworkGridAsync, "AppMonitor", showErrorDialog: false);
            }
        }

        private void InterfaceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (InterfaceCombo.SelectedItem is NetworkAdaptorItem item)
            {
                _selectedInterface = item.Name;
                ResetLiveTelemetryBaselines();
                if (_isInitialized)
                {
                    SaveCurrentSettings();
                    _ = RefreshRealTimeTelemetryAsync();
                }
            }
        }

        private async void BtnRefreshAdapters_Click(object sender, RoutedEventArgs e) =>
            await SafeRunAsync(() => LoadNetworkInterfacesAsync(preserveSelection: true), "RefreshAdapters");

        private void ChkDarkMode_Changed(object sender, RoutedEventArgs e)
        {
            if (!_isInitialized) return;
            ThemeManager.ApplyTheme(_currentTheme, ChkDarkMode.IsChecked == true, _currentSurface.Id);
            UpdateSwatchSelectionHighlights();
            SaveCurrentSettings();
        }

        private void CardGlobalPing_Click(object sender, MouseButtonEventArgs e)
        {
            if (!_isInitialized) return;
            int idx = Array.FindIndex(GlobalPingTargets, g => g.Host.Equals(_globalPingTarget, StringComparison.OrdinalIgnoreCase));
            int nextIdx = (idx + 1) % GlobalPingTargets.Length;
            _pingGeneration++;
            _globalPingTarget = GlobalPingTargets[nextIdx].Host;
            _globalPingTracker.Reset();
            TxtPingGlobal.Text = "-- ms";
            TxtJitterGlobal.Text = "±0 ms";
            TxtStatsGlobal.Text = "Loss 0.0% (0/0) · Min -- · Avg -- · Max --";
            TxtBadgeGlobal.Text = "SWITCHING";
            UpdatePingCardHeaders();
            SaveCurrentSettings();
            EnqueueConsoleLine($"[PING] Switched Global ping target to {_globalPingTarget}.");
            _ = RefreshPingTelemetryAsync(forceImmediate: true);
        }

        private void CardIntranetPing_Click(object sender, MouseButtonEventArgs e)
        {
            if (!_isInitialized) return;
            int idx = Array.FindIndex(IntranetPingTargets, i => i.Host.Equals(_intranetPingTarget, StringComparison.OrdinalIgnoreCase));
            int nextIdx = (idx + 1) % IntranetPingTargets.Length;
            _pingGeneration++;
            _intranetPingTarget = IntranetPingTargets[nextIdx].Host;
            _intranetPingTracker.Reset();
            TxtPingIntranet.Text = "-- ms";
            TxtJitterIntranet.Text = "±0 ms";
            TxtStatsIntranet.Text = "Loss 0.0% (0/0) · Min -- · Avg -- · Max --";
            TxtBadgeIntranet.Text = "SWITCHING";
            UpdatePingCardHeaders();
            SaveCurrentSettings();
            EnqueueConsoleLine($"[PING] Switched Intranet ping target to {_intranetPingTarget}.");
            _ = RefreshPingTelemetryAsync(forceImmediate: true);
        }

        private void ComboLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isInitialized || ComboLanguage.SelectedItem is not ComboBoxItem item || item.Tag is not string langCode) return;
            ApplyLanguage(langCode);
            SaveCurrentSettings();
        }

        private void ChkSettings_Changed(object sender, RoutedEventArgs e)
        {
            if (!_isInitialized) return;
            _service.RememberSettings = ChkRememberSettings.IsChecked == true;
            _service.EnableActionLog = ChkEnableLogging.IsChecked == true;
            SaveCurrentSettings();
        }

        private async void ChkStartWithWindows_Changed(object sender, RoutedEventArgs e)
        {
            if (!_isInitialized) return;
            bool enable = ChkStartWithWindows.IsChecked == true;
            await SafeRunAsync(async () =>
            {
                var res = await _service.SetStartWithWindowsAsync(enable);
                if (!res.Success)
                {
                    MessageBox.Show(res.Message, "Start with Windows", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }, "StartWithWindows");
        }

        private async void BtnExportSettings_Click(object sender, RoutedEventArgs e) =>
            await SafeRunAsync(async () =>
            {
                var dlg = new SaveFileDialog
                {
                    Title = "Export AionToolPro Settings",
                    Filter = "JSON Settings (*.json)|*.json",
                    FileName = $"AionToolPro_Settings_{DateTime.Now:yyyyMMdd}.json"
                };
                if (dlg.ShowDialog() != true) return;

                var current = new AppSettingsModel(
                    _currentTheme.Id,
                    _selectedInterface,
                    ChkEnableLogging.IsChecked == true,
                    ChkDarkMode.IsChecked == true,
                    _currentLang,
                    _currentSurface.Id,
                    ChkMinimizeToTray.IsChecked == true,
                    _globalPingTarget,
                    _intranetPingTarget);

                var res = await _service.ExportSettingsBundleAsync(dlg.FileName, current);
                MessageBox.Show(res.Message, "Export Settings", MessageBoxButton.OK, res.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }, "ExportSettings");

        private async void BtnImportSettings_Click(object sender, RoutedEventArgs e) =>
            await SafeRunAsync(async () =>
            {
                var dlg = new OpenFileDialog
                {
                    Title = "Import AionToolPro Settings",
                    Filter = "JSON Settings (*.json)|*.json"
                };
                if (dlg.ShowDialog() != true) return;

                var (ok, bundle, msg) = await _service.ImportSettingsBundleAsync(dlg.FileName);
                if (ok && bundle != null)
                {
                    _isInitialized = false;
                    ApplySettingsModelToUi(bundle.Settings);
                    _isInitialized = true;
                    ThemeManager.ApplyTheme(_currentTheme, ChkDarkMode.IsChecked == true, _currentSurface.Id);
                    UpdateSwatchSelectionHighlights();
                    ApplyLanguage(_currentLang);
                    RefreshDnsCatalogUi();
                    ResetLiveTelemetryBaselines();
                    SaveCurrentSettings();
                    _ = RefreshPingTelemetryAsync(forceImmediate: true);
                }
                MessageBox.Show(msg, "Import Settings", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }, "ImportSettings");

        // ==================== DNS STUDIO HANDLERS ====================
        private Task ApplyDnsProviderWithUiFeedbackAsync(DnsProviderModel provider) =>
            SafeRunAsync(async () =>
            {
                EnqueueConsoleLine($"Applying DNS {provider.Name} ({provider.PrimaryIp}, {provider.SecondaryIp}) on {_selectedInterface}...");
                var res = await _service.ApplyDnsAsync(
                    _selectedInterface,
                    provider.PrimaryIp,
                    provider.SecondaryIp,
                    provider.Name,
                    provider.PrimaryIpv6,
                    provider.SecondaryIpv6);

                if (!res.Success)
                {
                    MessageBox.Show(res.Message, "DNS Apply Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                await RefreshRealTimeTelemetryAsync();
            }, "ApplyDNS");

        private async void BtnApplySelectedDns_Click(object sender, RoutedEventArgs e)
        {
            if (ComboDnsProviders.SelectedItem is DnsProviderModel provider)
                await ApplyDnsProviderWithUiFeedbackAsync(provider);
        }

        private async void BtnApplyCustomDns_Click(object sender, RoutedEventArgs e) =>
            await SafeRunAsync(async () =>
            {
                string d1 = TxtCustomDns1.Text.Trim();
                string d2 = TxtCustomDns2.Text.Trim();
                if (!AioNetworkService.IsSafeDnsIpv4(d1))
                {
                    MessageBox.Show("Primary DNS is not a valid public/unicast IPv4 address.", "Invalid IPv4", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (!string.IsNullOrEmpty(d2) && !AioNetworkService.IsSafeDnsIpv4(d2))
                {
                    MessageBox.Show("Secondary DNS is not a valid public/unicast IPv4 address.", "Invalid IPv4", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                string label = string.IsNullOrWhiteSpace(TxtCustomDnsName.Text) ? "Custom" : TxtCustomDnsName.Text.Trim();
                var res = await _service.ApplyDnsAsync(_selectedInterface, d1, string.IsNullOrEmpty(d2) ? null : d2, label);
                if (!res.Success)
                    MessageBox.Show(res.Message, "Custom DNS Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                await RefreshRealTimeTelemetryAsync();
            }, "CustomDNS");

        private void BtnSaveCustomDns_Click(object sender, RoutedEventArgs e)
        {
            var res = _service.SaveCustomDnsProfile(TxtCustomDnsName.Text, TxtCustomDns1.Text, TxtCustomDns2.Text);
            if (res.Success)
                RefreshDnsCatalogUi();
            MessageBox.Show(res.Message, "Save Custom DNS", MessageBoxButton.OK, res.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }

        private void BtnDeleteCustomDns_Click(object sender, RoutedEventArgs e)
        {
            string targetName = (ComboDnsProviders.SelectedItem as DnsProviderModel)?.Name ?? TxtCustomDnsName.Text.Trim();
            var res = _service.DeleteCustomDnsProfile(targetName);
            if (res.Success)
                RefreshDnsCatalogUi();
            MessageBox.Show(res.Message, "Delete Custom DNS", MessageBoxButton.OK, res.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }

        private async void BtnScanCurrentDns_Click(object sender, RoutedEventArgs e) =>
            await SafeRunAsync(async () =>
            {
                await _service.ScanCurrentDnsAsync(_selectedInterface);
            }, "ScanDNS");

        private async void BtnResetDnsDhcp_Click(object sender, RoutedEventArgs e) =>
            await SafeRunAsync(async () =>
            {
                var res = await _service.ResetDnsToDhcpAsync(_selectedInterface);
                if (!res.Success)
                    MessageBox.Show(res.Message, "Reset DNS", MessageBoxButton.OK, MessageBoxImage.Warning);
                await RefreshRealTimeTelemetryAsync();
            }, "ResetDNS");

        private async void BtnFlushDns_Click(object sender, RoutedEventArgs e) =>
            await SafeRunAsync(async () =>
            {
                await _service.FlushDnsCacheAsync(_selectedInterface);
            }, "FlushDNS");

        private async void BtnFullAdapterReset_Click(object sender, RoutedEventArgs e) =>
            await SafeRunAsync(async () =>
            {
                if (MessageBox.Show(
                        $"This will briefly disconnect and reset network adaptor '{_selectedInterface}' (and run netsh int ip reset). Continue?",
                        "Confirm Adaptor Reset",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return;

                var res = await _service.FullAdapterResetProtocolAsync(_selectedInterface);
                await LoadNetworkInterfacesAsync(preserveSelection: true);
                await RefreshRealTimeTelemetryAsync();
                MessageBox.Show(res.Message, "Adaptor Reset", MessageBoxButton.OK, res.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }, "AdapterReset");

        private async void BtnBenchmarkDns_Click(object sender, RoutedEventArgs e) =>
            await SafeRunAsync(async () =>
            {
                BtnBenchmarkDns.IsEnabled = false;
                BtnDashFastestDns.IsEnabled = false;
                try
                {
                    var results = await _service.BenchmarkAllDnsAsync();
                    GridDnsBenchmark.ItemsSource = results;
                    _fastestWinner = results.FirstOrDefault(r => r.LatencyMs.HasValue);
                    if (_fastestWinner != null)
                    {
                        EnqueueConsoleLine($"Fastest DNS Winner (UDP/53): {_fastestWinner.Name} ({_fastestWinner.PrimaryIp}) at {_fastestWinner.LatencyMs} ms");
                        if (MessageBox.Show(
                                $"Fastest responding DNS (real UDP/53 query):\r\n{_fastestWinner.Name} — {_fastestWinner.PrimaryIp} — {_fastestWinner.LatencyMs} ms\r\n\r\nApply it now?",
                                "Fastest DNS Found",
                                MessageBoxButton.YesNo,
                                MessageBoxImage.Information) == MessageBoxResult.Yes)
                        {
                            var provider = _service.GetAllDnsProviders().FirstOrDefault(p => p.Index == _fastestWinner.Index);
                            await _service.ApplyDnsAsync(
                                _selectedInterface,
                                _fastestWinner.PrimaryIp,
                                _fastestWinner.SecondaryIp,
                                _fastestWinner.Name,
                                provider?.PrimaryIpv6,
                                provider?.SecondaryIpv6);
                            await RefreshRealTimeTelemetryAsync();
                        }
                    }
                    else
                    {
                        MessageBox.Show("No DNS server responded to UDP/53 queries. Check your internet connection or firewall.", "DNS Benchmark", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
                finally
                {
                    BtnBenchmarkDns.IsEnabled = true;
                    BtnDashFastestDns.IsEnabled = true;
                }
            }, "DnsBenchmark");

        private async void BtnApplyFastestWinner_Click(object sender, RoutedEventArgs e) =>
            await SafeRunAsync(async () =>
            {
                if (_fastestWinner == null)
                {
                    MessageBox.Show("Run the benchmark first to find the fastest DNS provider.", "Benchmark Required", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                var provider = _service.GetAllDnsProviders().FirstOrDefault(p => p.Index == _fastestWinner.Index);
                await _service.ApplyDnsAsync(
                    _selectedInterface,
                    _fastestWinner.PrimaryIp,
                    _fastestWinner.SecondaryIp,
                    _fastestWinner.Name,
                    provider?.PrimaryIpv6,
                    provider?.SecondaryIpv6);
                await RefreshRealTimeTelemetryAsync();
            }, "ApplyFastestDNS");

        private async void BtnDnsLookup_Click(object sender, RoutedEventArgs e) =>
            await SafeRunAsync(async () =>
            {
                TxtNetInfoReport.Text = "Running nslookup google.com...";
                TxtNetInfoReport.Text = await _service.RunNslookupAsync("google.com");
            }, "DnsLookup");

        private async void BtnDnsLeakCheck_Click(object sender, RoutedEventArgs e) =>
            await SafeRunAsync(async () =>
            {
                TxtNetInfoReport.Text = "Running resolver identity lookup (whoami.akamai.net)...";
                TxtNetInfoReport.Text = await _service.RunNslookupAsync("whoami.akamai.net");
            }, "DnsLeakCheck");

        private async void BtnBackupDns_Click(object sender, RoutedEventArgs e) =>
            await SafeRunAsync(async () =>
            {
                string info = await _service.BackupDnsAsync(_selectedInterface);
                MessageBox.Show($"DNS Backup saved to:\r\n{_service.DnsBackupFilePath}\r\n\r\n{info}", "DNS Backup Saved", MessageBoxButton.OK, MessageBoxImage.Information);
            }, "BackupDNS");

        private async void BtnRestoreDns_Click(object sender, RoutedEventArgs e) =>
            await SafeRunAsync(async () =>
            {
                var res = await _service.RestoreDnsBackupAsync(_selectedInterface);
                MessageBox.Show(res.Message, "Restore DNS", MessageBoxButton.OK, res.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
                if (res.Success) await RefreshRealTimeTelemetryAsync();
            }, "RestoreDNS");

        private void BtnShowDnsBackup_Click(object sender, RoutedEventArgs e) =>
            MessageBox.Show(_service.ReadSavedDnsBackup(), "Saved DNS Backup", MessageBoxButton.OK, MessageBoxImage.Information);

        private async void BtnDohCloudflare_Click(object sender, RoutedEventArgs e) =>
            await ApplyDohByNameAsync("Cloudflare");

        private async void BtnDohGoogle_Click(object sender, RoutedEventArgs e) =>
            await ApplyDohByNameAsync("Google");

        private async void BtnDohQuad9_Click(object sender, RoutedEventArgs e) =>
            await ApplyDohByNameAsync("Quad9");

        private Task ApplyDohByNameAsync(string name) =>
            SafeRunAsync(async () =>
            {
                var provider = AioCatalog.DnsProviders.First(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                var res = await _service.EnableDnsOverHttpsAsync(_selectedInterface, provider);
                if (!res.Success)
                    MessageBox.Show(res.Message, "Encrypted DoH", MessageBoxButton.OK, MessageBoxImage.Warning);
                await RefreshRealTimeTelemetryAsync();
            }, "EnableDoH");

        private async void BtnDohShow_Click(object sender, RoutedEventArgs e) =>
            await SafeRunAsync(async () =>
            {
                string status = await _service.ShowDohStatusAsync();
                if (Environment.OSVersion.Version.Build < 22000)
                    MessageBox.Show(status, "DoH Status", MessageBoxButton.OK, MessageBoxImage.Information);
            }, "ShowDoH");

        private async void BtnDohRemove_Click(object sender, RoutedEventArgs e) =>
            await SafeRunAsync(async () =>
            {
                var res = await _service.RemoveManagedDohSettingsAsync(_selectedInterface);
                MessageBox.Show(res.Message, "Remove DoH", MessageBoxButton.OK, res.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }, "RemoveDoH");

        // ==================== GAME BOOSTER & NETWORK INFO HANDLERS ====================
        private async void BtnGameBooster_Click(object sender, RoutedEventArgs e) =>
            await SafeRunAsync(async () =>
            {
                if (MessageBox.Show(
                        "This will safely pause currently running non-essential Windows services (SysMain, Telemetry, Search, Update) and close desktop widgets without wiping RAM or touching your browsers.\n\nUse 'Restore Normal' afterwards to re-enable only the services that were running. Proceed?",
                        "Game Booster",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question) != MessageBoxResult.Yes)
                    return;

                var res = await _service.OptimizeForGamingAsync();
                MessageBox.Show(res.Message, "Game Booster", MessageBoxButton.OK, res.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }, "GameBooster");

        private async void BtnRestoreNormalMode_Click(object sender, RoutedEventArgs e) =>
            await SafeRunAsync(async () =>
            {
                var res = await _service.RestoreNormalModeAsync();
                MessageBox.Show(res.Message, "Restore Normal Mode", MessageBoxButton.OK, res.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }, "RestoreNormalMode");

        private async void BtnSpeedTest_Click(object sender, RoutedEventArgs e) =>
            await SafeRunAsync(async () =>
            {
                BtnSpeedTest.IsEnabled = false;
                try
                {
                    TxtNetInfoReport.Text = "Running 2-stream Download Speed Test (Cloudflare CDN, max 8s)...\r\n";
                    string res = await _service.RunSpeedTestAsync();
                    TxtNetInfoReport.Text += res;
                }
                finally
                {
                    BtnSpeedTest.IsEnabled = true;
                }
            }, "SpeedTest");

        private async void BtnNatAnalyzer_Click(object sender, RoutedEventArgs e) =>
            await SafeRunAsync(async () =>
            {
                TxtNetInfoReport.Text = "Running RFC 5389 UDP STUN test & CGNAT check...\r\n";
                TxtNetInfoReport.Text = await _service.AnalyzePublicLocalIpStatusAsync();
            }, "StunNatCheck");

        private async void BtnGameServerPings_Click(object sender, RoutedEventArgs e) =>
            await SafeRunAsync(async () =>
            {
                BtnGameServerPings.IsEnabled = false;
                try
                {
                    TxtNetInfoReport.Text = "Pinging major gaming & edge servers in parallel...\r\n";
                    TxtNetInfoReport.Text = await _service.RunGameServerPingTestAsync();
                }
                finally
                {
                    BtnGameServerPings.IsEnabled = true;
                }
            }, "GameServerPings");

        private async void BtnIpInfo_Click(object sender, RoutedEventArgs e) =>
            await SafeRunAsync(async () =>
            {
                TxtNetInfoReport.Text = "Reading interface IP & MAC details...";
                TxtNetInfoReport.Text = await _service.GetIpAndMacInfoAsync(_selectedInterface);
            }, "IpMacInfo");

        private async void BtnPublicIp_Click(object sender, RoutedEventArgs e) =>
            await SafeRunAsync(async () =>
            {
                TxtNetInfoReport.Text = "Querying ipinfo.io...";
                try
                {
                    var (ip, country, org) = await _service.GetPublicIpInfoAsync();
                    TxtNetInfoReport.Text = $"Public IP  : {ip}\r\nCountry    : {country}\r\nNetwork    : {org}";
                }
                catch (Exception ex)
                {
                    TxtNetInfoReport.Text = $"Failed to fetch public IP: {ex.Message}";
                }
            }, "PublicIP", showErrorDialog: false);

        private async void BtnActiveConnections_Click(object sender, RoutedEventArgs e) =>
            await SafeRunAsync(async () =>
            {
                TxtNetInfoReport.Text = "Fetching active TCP connections...";
                TxtNetInfoReport.Text = await _service.GetActiveTcpConnectionsReportAsync();
            }, "ActiveConnections");

        private async void BtnDataUsage_Click(object sender, RoutedEventArgs e) =>
            await SafeRunAsync(async () =>
            {
                TxtNetInfoReport.Text = await _service.GetAdapterDataUsageReportAsync();
            }, "DataUsage");

        private async void BtnPingMonitor_Click(object sender, RoutedEventArgs e) =>
            await SafeRunAsync(async () =>
            {
                string host = TxtNetTargetHost.Text.Trim();
                if (!AioNetworkService.ValidateHostOrIp(host))
                {
                    MessageBox.Show("Please enter a valid hostname or IP address first.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                int count = 20;
                if (ComboPingPackets.SelectedItem is ComboBoxItem item && int.TryParse(item.Content?.ToString(), out int c))
                    count = c;

                TxtNetInfoReport.Text = $"Running {count}-packet Ping Monitor to {host} (Click 'Stop' below to cancel)...{Environment.NewLine}{Environment.NewLine}";
                await _service.RunPingAsync(host, count, line =>
                {
                    if (_isClosing) return;
                    Dispatcher.InvokeAsync(() =>
                    {
                        if (_isClosing) return;
                        TxtNetInfoReport.AppendText(line + Environment.NewLine);
                        TxtNetInfoReport.ScrollToEnd();
                    });
                });
            }, "PingMonitor");

        private async void BtnTraceroute_Click(object sender, RoutedEventArgs e) =>
            await SafeRunAsync(async () =>
            {
                string host = TxtNetTargetHost.Text.Trim();
                if (!AioNetworkService.ValidateHostOrIp(host))
                {
                    MessageBox.Show("Please enter a valid hostname or IP address first.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                TxtNetInfoReport.Text = $"Tracing route to {host} (Click 'Stop' below to cancel)...{Environment.NewLine}{Environment.NewLine}";
                await _service.RunTracerouteAsync(host, line =>
                {
                    if (_isClosing) return;
                    Dispatcher.InvokeAsync(() =>
                    {
                        if (_isClosing) return;
                        TxtNetInfoReport.AppendText(line + Environment.NewLine);
                        TxtNetInfoReport.ScrollToEnd();
                    });
                });
            }, "Traceroute");

        private async void BtnPortCheck_Click(object sender, RoutedEventArgs e) =>
            await SafeRunAsync(async () =>
            {
                string host = TxtNetTargetHost.Text.Trim();
                if (!AioNetworkService.ValidateHostOrIp(host) || !int.TryParse(TxtPortNumber.Text.Trim(), out int port) || port < 1 || port > 65535)
                {
                    MessageBox.Show("Enter a valid host and port number (1–65535).", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                TxtNetInfoReport.Text = $"Testing TCP connection to {host}:{port}...";
                bool open = await _service.CheckTcpPortAsync(host, port);
                TxtNetInfoReport.Text = open
                    ? $"OPEN — {host}:{port} accepts TCP connections."
                    : $"CLOSED / FILTERED — {host}:{port} did not accept TCP connection within 3s.";
            }, "PortCheck");

        // ==================== ACTIVE CONNECTIONS BY APP HANDLERS ====================
        private async void BtnRefreshAppNet_Click(object sender, RoutedEventArgs e) =>
            await SafeRunAsync(RefreshAppNetworkGridAsync, "ScanActiveApps");

        private async void BtnAutoAppNet_Click(object sender, RoutedEventArgs e) =>
            await SafeRunAsync(async () =>
            {
                _autoRefreshAppMonitor = !_autoRefreshAppMonitor;
                bool fa = _currentLang == "fa";
                BtnAutoAppNet.Content = _autoRefreshAppMonitor
                    ? (fa ? "⏱️ بروزرسانی خودکار: روشن" : "⏱️ Auto-Refresh: ON")
                    : (fa ? "⏱️ بروزرسانی خودکار: خاموش" : "⏱️ Auto-Refresh: OFF");

                if (_autoRefreshAppMonitor)
                    await RefreshAppNetworkGridAsync();
            }, "AutoRefreshApps");

        private async void BtnKillAppNet_Click(object sender, RoutedEventArgs e) =>
            await SafeRunAsync(async () =>
            {
                if (GridAppNetworkMonitor.SelectedItem is not AppNetworkUsageRow row)
                {
                    MessageBox.Show("Select an application from the table first.", "Select App", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                if (AioNetworkService.IsProtectedProcess(row.ProcessName) || row.Pid <= 4)
                {
                    MessageBox.Show($"'{row.ProcessName}' is a protected Windows process and cannot be terminated.", "Protected Process", MessageBoxButton.OK, MessageBoxImage.Stop);
                    return;
                }
                if (MessageBox.Show($"Terminate process '{row.ProcessName}' (PID: {row.Pid})?", "Confirm Kill Process", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    var res = _service.KillAppProcessById(row.Pid, row.ProcessName);
                    if (!res.Success)
                        MessageBox.Show(res.Message, "Kill Process", MessageBoxButton.OK, MessageBoxImage.Warning);
                    await RefreshAppNetworkGridAsync();
                }
            }, "KillProcess");

        private async void BtnBlockAppNet_Click(object sender, RoutedEventArgs e) =>
            await SafeRunAsync(async () =>
            {
                if (GridAppNetworkMonitor.SelectedItem is not AppNetworkUsageRow row)
                {
                    MessageBox.Show("Select an application from the table first.", "Select App", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                var res = await _service.BlockAppInFirewallAsync(row.ExecutablePath, row.ProcessName);
                MessageBox.Show(res.Message, "Firewall Block", MessageBoxButton.OK, res.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }, "BlockFirewall");

        private async void BtnUnblockAppNet_Click(object sender, RoutedEventArgs e) =>
            await SafeRunAsync(async () =>
            {
                if (GridAppNetworkMonitor.SelectedItem is not AppNetworkUsageRow row)
                {
                    MessageBox.Show("Select an application from the table first (or click 'Blocked List' to unblock closed apps).", "Select App", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                var res = await _service.UnblockAppInFirewallAsync(row.ExecutablePath, row.ProcessName);
                MessageBox.Show(res.Message, "Unblock Firewall", MessageBoxButton.OK, MessageBoxImage.Information);
            }, "UnblockFirewall");

        private async void BtnViewBlockedApps_Click(object sender, RoutedEventArgs e) =>
            await SafeRunAsync(async () =>
            {
                var blocked = _service.GetBlockedApps();
                if (blocked.Count == 0)
                {
                    MessageBox.Show("No applications are currently blocked by AionToolPro.", "Blocked Applications", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var sb = new StringBuilder();
                sb.AppendLine($"Applications blocked by AionToolPro ({blocked.Count}):\n");
                foreach (var b in blocked)
                    sb.AppendLine($"• {b.ProcessName} — {b.ExecutablePath}");
                sb.AppendLine("\nUnblock ALL of these applications in Windows Firewall now?");

                if (MessageBox.Show(sb.ToString(), "Blocked Applications", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                {
                    foreach (var b in blocked)
                    {
                        await _service.UnblockAppInFirewallAsync(b.ExecutablePath, b.ProcessName, b.RuleBaseName);
                    }
                    MessageBox.Show("All AionToolPro firewall block rules have been removed.", "Unblocked", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }, "BlockedAppsList");

        // ==================== WI-FI MANAGER HANDLERS ====================
        private async void BtnWifiStatus_Click(object sender, RoutedEventArgs e) =>
            await SafeRunAsync(async () =>
            {
                TxtWifiDetails.Text = "Querying WLAN interface status...";
                TxtWifiDetails.Text = await _service.GetWifiStatusAsync();
            }, "WifiStatus");

        private async void BtnWifiPasswords_Click(object sender, RoutedEventArgs e) =>
            await SafeRunAsync(async () =>
            {
                TxtWifiDetails.Text = "Scanning saved WLAN profiles securely...";
                var items = await _service.GetSavedWifiPasswordsAsync();
                GridWifiPasswords.ItemsSource = items;
                TxtWifiDetails.Text = $"Loaded {items.Count} saved Wi-Fi profile(s). Passwords are masked by default — select a row and click 'Show Password' or 'Copy Password'.";
            }, "WifiProfiles");

        private void BtnWifiRevealPassword_Click(object sender, RoutedEventArgs e)
        {
            if (GridWifiPasswords.SelectedItem is not WifiCredentialItem selected)
            {
                MessageBox.Show("Select a Wi-Fi profile from the table first.", "Show Password", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (string.IsNullOrEmpty(selected.Password))
            {
                MessageBox.Show($"SSID: {selected.Ssid}\nStatus: {selected.Status}", "Wi-Fi Credentials", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            MessageBox.Show($"SSID: {selected.Ssid}\nPassword: {selected.Password}", "Wi-Fi Password", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnWifiCopyPassword_Click(object sender, RoutedEventArgs e)
        {
            if (GridWifiPasswords.SelectedItem is not WifiCredentialItem selected)
            {
                MessageBox.Show("Select a Wi-Fi profile from the table first.", "Copy Password", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (string.IsNullOrEmpty(selected.Password))
            {
                MessageBox.Show($"No password available to copy for '{selected.Ssid}' ({selected.Status}).", "Copy Password", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            try
            {
                Clipboard.SetText(selected.Password);
                EnqueueConsoleLine($"[WI-FI] Copied password for '{selected.Ssid}' to clipboard.");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not access Windows clipboard: {ex.Message}", "Clipboard Busy", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void BtnWifiForget_Click(object sender, RoutedEventArgs e) =>
            await SafeRunAsync(async () =>
            {
                if (GridWifiPasswords.SelectedItem is not WifiCredentialItem selected)
                {
                    MessageBox.Show("Select a Wi-Fi profile from the table first.", "Select Profile", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                if (MessageBox.Show($"Forget saved Wi-Fi network '{selected.Ssid}'?", "Confirm Forget", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    var res = await _service.ForgetWifiNetworkAsync(selected.Ssid, _selectedInterface);
                    if (!res.Success)
                        MessageBox.Show(res.Message, "Forget Wi-Fi", MessageBoxButton.OK, MessageBoxImage.Warning);
                    GridWifiPasswords.ItemsSource = await _service.GetSavedWifiPasswordsAsync();
                }
            }, "WifiForget");

        // ==================== FIXES & HOSTS HANDLERS ====================
        private async void BtnApplyStaticIp_Click(object sender, RoutedEventArgs e) =>
            await SafeRunAsync(async () =>
            {
                var res = await _service.ApplyStaticIpAsync(_selectedInterface, TxtStaticIp.Text.Trim(), TxtStaticMask.Text.Trim(), TxtStaticGateway.Text.Trim());
                if (!res.Success)
                    MessageBox.Show(res.Message, "Static IP Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                await RefreshRealTimeTelemetryAsync();
            }, "StaticIP");

        private async void BtnSwitchIpDhcp_Click(object sender, RoutedEventArgs e) =>
            await SafeRunAsync(async () =>
            {
                var res = await _service.SwitchIpToDhcpAsync(_selectedInterface);
                if (!res.Success)
                    MessageBox.Show(res.Message, "Switch DHCP", MessageBoxButton.OK, MessageBoxImage.Warning);
                await RefreshRealTimeTelemetryAsync();
            }, "SwitchDHCP");

        private async void BtnHostsShow_Click(object sender, RoutedEventArgs e) =>
            await SafeRunAsync(async () =>
            {
                TxtHostsPreview.Text = "Analyzing hosts file...";
                TxtHostsPreview.Text = await _service.GetCustomHostsSummaryAsync();
            }, "ShowHosts");

        private void BtnHostsNotepad_Click(object sender, RoutedEventArgs e)
        {
            try { _service.OpenHostsInNotepad(); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Open Notepad", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }

        private async void BtnHostsBackup_Click(object sender, RoutedEventArgs e) =>
            await SafeRunAsync(async () =>
            {
                string path = await _service.BackupHostsFileAsync(_selectedInterface);
                TxtHostsPreview.Text = $"Backed up hosts file to:\r\n{path}";
            }, "BackupHosts");

        private async void BtnHostsRestore_Click(object sender, RoutedEventArgs e) =>
            await SafeRunAsync(async () =>
            {
                var res = await _service.RestoreHostsFileAsync(_selectedInterface);
                TxtHostsPreview.Text = res.Message;
            }, "RestoreHosts");

        private async void BtnHostsResetDefault_Click(object sender, RoutedEventArgs e) =>
            await SafeRunAsync(async () =>
            {
                if (MessageBox.Show("Reset Windows hosts file to default? A timestamped backup will be saved first.", "Reset Hosts", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return;

                await _service.ResetHostsToDefaultAsync(_selectedInterface);
                TxtHostsPreview.Text = await _service.GetCustomHostsSummaryAsync();
            }, "ResetHosts");

        private async void BtnApplyBlocklist_Click(object sender, RoutedEventArgs e) =>
            await SafeRunAsync(async () =>
            {
                if (ComboBlocklists.SelectedItem is not BlocklistDefinition list) return;
                BtnApplyBlocklist.IsEnabled = false;
                try
                {
                    TxtHostsPreview.Text = $"Downloading and validating {list.Name}...";
                    int count = await _service.ApplyBlocklistAsync(list, _selectedInterface);
                    TxtHostsPreview.Text = $"Applied {count:N0} validated rules from {list.Name}.\r\n\r\n" + await _service.GetCustomHostsSummaryAsync();
                }
                finally
                {
                    BtnApplyBlocklist.IsEnabled = true;
                }
            }, "ApplyBlocklist");

        private async void BtnRemoveBlocklist_Click(object sender, RoutedEventArgs e) =>
            await SafeRunAsync(async () =>
            {
                if (ComboBlocklists.SelectedItem is not BlocklistDefinition list) return;
                bool removed = await _service.RemoveBlocklistByTagAsync(list.Tag, _selectedInterface);
                TxtHostsPreview.Text = (removed ? $"Removed blocklist '{list.Name}'.\r\n\r\n" : $"Blocklist '{list.Name}' was not installed.\r\n\r\n")
                    + await _service.GetCustomHostsSummaryAsync();
            }, "RemoveBlocklist");

        private async void BtnProxyView_Click(object sender, RoutedEventArgs e) =>
            await SafeRunAsync(async () =>
            {
                TxtHostsPreview.Text = await _service.GetProxyStatusAsync();
            }, "ProxyStatus");

        private async void BtnProxyDisable_Click(object sender, RoutedEventArgs e) =>
            await SafeRunAsync(async () =>
            {
                await _service.DisableSystemProxyAsync(_selectedInterface);
                TxtHostsPreview.Text = await _service.GetProxyStatusAsync();
            }, "DisableProxy");

        private async void BtnFirewallReset_Click(object sender, RoutedEventArgs e) =>
            await SafeRunAsync(async () =>
            {
                if (MessageBox.Show(
                        "Reset Windows Defender Firewall to factory defaults?\n\nA backup (.wfw) of your current firewall policy will automatically be exported first.",
                        "Confirm Firewall Reset",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return;

                var res = await _service.ResetFirewallAsync(_selectedInterface);
                MessageBox.Show(res.Message, "Firewall Reset", MessageBoxButton.OK, res.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }, "FirewallReset");

        private async void BtnArpReset_Click(object sender, RoutedEventArgs e) =>
            await SafeRunAsync(async () =>
            {
                if (MessageBox.Show(
                        "Clear ARP cache and flush routing table (route -f)? Active VPNs or static routes will disconnect briefly.",
                        "Confirm ARP & Route Reset",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return;

                var res = await _service.ResetArpAndRoutesAsync(_selectedInterface);
                MessageBox.Show(res.Message, "ARP & Routes", MessageBoxButton.OK, MessageBoxImage.Information);
            }, "ArpReset");

        private async void BtnFullNetRepair_Click(object sender, RoutedEventArgs e) =>
            await SafeRunAsync(async () =>
            {
                if (MessageBox.Show(
                        "Reset Winsock catalog and TCP/IP stack? A PC restart is required afterwards.",
                        "Full Network Repair",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return;

                var res = await _service.FullNetworkRepairAsync(_selectedInterface);
                MessageBox.Show(res.Message, "Full Network Repair", MessageBoxButton.OK, MessageBoxImage.Information);
            }, "FullNetRepair");

        // ==================== MAINTENANCE, TWO-PHASE UPDATER & LOG HANDLERS ====================
        private async void BtnCleanTemp_Click(object sender, RoutedEventArgs e) =>
            await SafeRunAsync(async () =>
            {
                var (count, totalMb) = await _service.EstimateCleanableTempFilesAsync();
                if (MessageBox.Show(
                        $"Found approximately {count:N0} temp files (~{totalMb:N2} MB) older than 24 hours.\nRecent files (<24h) and locked files will be skipped safely.\n\nProceed with cleanup?",
                        "Confirm Temp Cleanup",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question) != MessageBoxResult.Yes)
                    return;

                string result = await _service.CleanTempFilesAsync(_selectedInterface);
                MessageBox.Show(result, "Temp Cleaner", MessageBoxButton.OK, MessageBoxImage.Information);
            }, "CleanTemp");

        private async void BtnDismSfc_Click(object sender, RoutedEventArgs e) =>
            await SafeRunAsync(async () =>
            {
                if (MessageBox.Show(
                        "Start DISM /RestoreHealth and SFC /scannow? This may take 10–30 minutes (you can click Stop at any time).",
                        "System Repair",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question) != MessageBoxResult.Yes)
                    return;

                var res = await _service.RunSystemRepairDismSfcAsync(_selectedInterface);
                MessageBox.Show(res.Message, "DISM & SFC Repair", MessageBoxButton.OK, res.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }, "DismSfc");

        private async void BtnWindowsUpdateReset_Click(object sender, RoutedEventArgs e) =>
            await SafeRunAsync(async () =>
            {
                if (MessageBox.Show(
                        "Stop Windows Update services, rename update cache folders, and automatically restart all services?",
                        "Windows Update Reset",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question) != MessageBoxResult.Yes)
                    return;

                var res = await _service.ResetWindowsUpdateAsync(_selectedInterface);
                MessageBox.Show(res.Message, "Windows Update Reset", MessageBoxButton.OK, res.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }, "ResetWindowsUpdate");

        private void BtnViewLog_Click(object sender, RoutedEventArgs e) =>
            TxtActionLogContent.Text = _service.ReadActionLog();

        private void BtnClearLog_Click(object sender, RoutedEventArgs e)
        {
            _service.ClearActionLog();
            TxtActionLogContent.Text = _service.ReadActionLog();
        }

        private void BtnCancelCommand_Click(object sender, RoutedEventArgs e) => _service.CancelCurrentOperation();
        private void BtnClearConsole_Click(object sender, RoutedEventArgs e) => ConsoleOutputBox.Clear();

        private void EnqueueConsoleLine(string line)
        {
            if (_isClosing) return;
            _consoleQueue.Enqueue($"[{DateTime.Now:HH:mm:ss}] {line}");
        }

        private void FlushConsoleQueue()
        {
            if (_isClosing || _consoleQueue.IsEmpty) return;

            var sb = new StringBuilder();
            int drained = 0;
            while (drained < 120 && _consoleQueue.TryDequeue(out var line))
            {
                sb.AppendLine(line);
                drained++;
            }

            if (sb.Length == 0) return;

            if (ConsoleOutputBox.Text.Length > 32000)
            {
                ConsoleOutputBox.Text = ConsoleOutputBox.Text[^18000..];
            }
            ConsoleOutputBox.AppendText(sb.ToString());
            ConsoleOutputBox.ScrollToEnd();
        }

        private async void BtnCheckUpdate_Click(object sender, RoutedEventArgs e) =>
            await SafeRunAsync(async () =>
            {
                BtnCheckUpdate.IsEnabled = false;
                bool fa = _currentLang == "fa";
                BtnCheckUpdate.Content = fa ? "در حال بررسی گیت‌هاب..." : "Checking GitHub...";

                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                    var info = await _service.CheckForUpdateInfoAsync("Yoqaizo", "AionToolPro", cts.Token);

                    if (!info.HasUpdate)
                    {
                        MessageBox.Show(info.Message, "AionToolPro Updater", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }

                    string notesPreview = string.IsNullOrWhiteSpace(info.ReleaseNotes)
                        ? "No release notes provided."
                        : (info.ReleaseNotes.Length > 500 ? info.ReleaseNotes[..500] + "..." : info.ReleaseNotes);
                    string shaStatus = string.IsNullOrWhiteSpace(info.ChecksumUrl) ? "Standard HTTPS" : "HTTPS + SHA-256 Verified";
                    string modeNote = AioNetworkService.IsInstalledCopy()
                        ? ""
                        : "\nNote: You are running a portable copy. Installing will install the new version into Program Files.";

                    var confirm = MessageBox.Show(
                        $"New version v{info.LatestVersion} is available (current: v{info.CurrentVersion}).\nVerification: {shaStatus}{modeNote}\n\nWhat's New:\n{notesPreview}\n\nDownload and install silently now?",
                        "Update Available",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (confirm != MessageBoxResult.Yes)
                        return;

                    ProgressUpdate.Value = 0;
                    ProgressUpdate.Visibility = Visibility.Visible;
                    var progress = new Progress<int>(pct =>
                    {
                        ProgressUpdate.Value = pct;
                        BtnCheckUpdate.Content = fa ? $"در حال دانلود... {pct}%" : $"Downloading... {pct}%";
                    });

                    string result = await _service.DownloadAndInstallUpdateAsync(info, progress, cts.Token);
                    if (result == "READY_TO_UPDATE")
                    {
                        _forceExit = true;
                        Close();
                        return;
                    }

                    MessageBox.Show(result, "AionToolPro Updater", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                finally
                {
                    if (!_isClosing)
                    {
                        ProgressUpdate.Visibility = Visibility.Collapsed;
                        BtnCheckUpdate.IsEnabled = true;
                        BtnCheckUpdate.Content = fa ? "🔄 بررسی بروزرسانی" : "🔄 Check for Updates";
                    }
                }
            }, "AutoUpdater");
    }
}
