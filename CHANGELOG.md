# Changelog

All notable changes to **AionToolPro** are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.9.4] - 2026-11-26

### Hardened Smart Agent, Report Accuracy, DoH Precision & Performance
- **Strict Smart Agent Bypass Guard & Full HTTPS + HTTP (Port 80) Support:**
  - Enforced `summary.FoundWorkingDns` so geo-restricted (`HTTP 403/451`) DNS servers are never auto-applied as false successes.
  - Enabled strict TLS certificate validation (`SslPolicyErrors.None`) to catch and reject MitM/fake-certificate ISP block pages (`✗ TLS Cert Invalid / MitM`), plus automatic `GET /` fallback when servers return `HTTP 405` on `HEAD /`.
  - Added full **HTTP (Port 80)** site support (automatic fallback when Port 443 is unavailable, or direct priority when entering `http://...`) with ISP block-page signature filtering (`10.10.34.x`, `peyvandha.ir`, `internet.ir`).
  - Bound Agent UDP/53 queries & TCP probes to the selected adapter (testing up to 2 resolved IPs per DNS), added clean `OperationCanceledException` (`WasCancelled`) handling when clicking Stop, color-coded the `Bypass Status` column (Green/Amber/Red), and wired `Enter` key submission in the target input box.
  - Upgraded **Auto-Failover Guard** to check live reachability of the actual target site (not just `1.1.1.1`) and skip pointless scans whenever the network adapter is physically disconnected.
- **Full-Session Graphical DNS Report (`.txt`) Fixes:** Widened the Unicode box layout from 68 to 86 columns so `Min / Avg / Max / Jitter / Loss %` never truncate, and upgraded `DnsSessionTracker` with true cumulative full-session ping statistics across Global, Intranet, and Router channels (not just the last 40 samples).
- **Sinkhole-Free DNS Benchmark & Precise Windows 11 DoH:** Rejected ISP sinkhole IPs inside `BenchmarkAllDnsAsync` so hijacked resolvers can never win "Fastest DNS". Reordered Windows 11 `DohInterfaceSettings` registry writes ahead of `netsh set dnsservers` and added explicit `DohFlags = 0` deactivation when toggling DoH OFF.
- **Final Reliability Polish:** Cleared stale per-interface DoH registry entries during `ResetDnsToDhcpAsync`, routed `Apply Fastest DNS` through `ApplyProviderDnsAsync` (honoring Windows DoH), isolated per-provider exceptions inside parallel Agent scans while preserving clean cancellation, forced DWM backdrop refresh after Settings Import, and reset per-channel cumulative report counters when switching ping targets.
- **~70% Fewer OS NIC Enumerations & One-Time Tray Balloon:** Introduced shared `AdapterProbeContext` across parallel pings (reducing `GetAllNetworkInterfaces()` calls from ~6 per tick to 1) and added a one-time localized Win32 System Tray balloon tip (`NIF_INFO`) when hiding to Tray.

## [0.9.3] - 2026-11-26

### Added & Replaced
- **Smart Anti-Sanction DNS Agent (Replaces Game Mode):** Completely removed Game Booster and replaced it with a live **Smart Agent** (`🤖 Smart Agent` toggle + expandable live Dashboard panel):
  - **Parallel Multi-DNS Anti-Sanction Scanner (`AioNetworkService.Agent.cs`):** Sends native RFC 1035 UDP/53 queries (with full label-compression A-record parsing) to all DNS servers simultaneously for any website or URL entered by the user (plus 8 one-click quick chips: Discord, Steam, Riot, Epic, ChatGPT, Spotify, EA, GitHub).
  - **ISP Sinkhole Detection & Real TLS SNI + HTTP Bypass Verification:** Automatically filters out ISP censorship sinkhole IPs (`10.10.34.x`, `0.0.0.0`, `127.0.0.1`), verifies real TCP + TLS 1.2/1.3 SNI handshake & HTTP status (`200/3xx` vs `403/451` geo-blocks) directly against each resolved IP, ranks all working resolvers, and automatically applies the best working DNS to Windows (honoring Windows DoH).
  - **Live Auto-Failover Guard:** While Agent is active, continuously monitors live ping and packet loss and automatically triggers a parallel re-scan and switch if packet loss spikes (`>=25%`) or 3 consecutive drops occur.
- **Graphical DNS Session Telemetry Report (`.txt`):** Added a live `DnsSessionTracker` and **📊 DNS Report** button on the Dashboard that tracks exact Rx/Tx downloaded data during the active DNS session, peak speeds, Global/Intranet/Router ping Min/Avg/Max/Jitter/Loss, latency sparkline (`▁▂▃▄▅▆▇█`), Agent history, and overall Network Quality Grade (`S+` to `D`), exporting a cleanly framed Unicode box-drawing `.txt` report (UTF-8 BOM for crisp Notepad rendering).

### Removed
- Removed `iran.ir` from Intranet ping targets, tooltips, and documentation (retaining `www.arvancloud.ir`, `www.salatingames.ir`, and `parsvds.com`).
- Removed Game Booster / Game Mode (`OptimizeForGamingAsync`, `RestoreNormalModeAsync`, `gamebooster_state.json`) cleanly in favor of the Smart Anti-Sanction DNS Agent.

## [0.9.2] - 2026-11-25

### Backend, DoH, Custom Color Studio & Updater Reliability
- **Clean Physical Adapter Filter & Startup Active-Route Auto-Connect:** Filtered out virtual, VPN TUN, hypervisor, Bluetooth, Wi-Fi Direct, Npcap, and junk interfaces so only real physical adapters appear. On startup, Win32 `GetBestInterface` (`iphlpapi.dll`) detects the active internet adapter automatically and connects to it cleanly (never overridden by a stale disconnected adapter in saved settings).
- **Strict Per-Adapter Ping & Telemetry Binding:** Upgraded ping probing (`ProbeHostOnAdapterAsync`) to bind ICMP via Win32 `IcmpSendEcho2Ex` (`SourceAddress`) and TCP fallback sockets strictly to the selected adapter's IPv4 (with transparent WAN routing fallback when a VPN TUN is active), and isolated gateway/DNS/link-speed/Rx-Tx readings strictly to the chosen adapter.
- **Hardened Auto-Updater (`Check for Updates` Fix):** Added a dedicated `UpdateHttp` client with `Timeout.InfiniteTimeSpan` (fixing 60-second premature download cancellations on large ~75MB installers), automatic GitHub web-redirect fallback when `api.github.com` returns HTTP 403/429 rate limits, accurate distinction between user Cancel vs Timeout, partial-download truncation validation, and guarded UI exception handling so the Update Center modal never stalls.
- **Full-Spectrum Custom Color Theme Studio:** Added **Custom Color** as the last option in Accent Theme with an expandable HSL Spectrum Studio (360° rainbow Hue, Saturation, Lightness sliders, direct `#HEX` input, Primary/Secondary gradient editor, and 16 quick color dots) persisted across settings & exports.
- **Native Windows 11 DNS-over-HTTPS (DoH) Toggle & Tiny Card Badges:** Added **Windows DoH (Encrypted DNS)** toggle in Settings and crisp miniature `DoH` badges on all 10 DoH-capable DNS cards (Cloudflare, Google, OpenDNS, ControlD, Mullvad, AliDNS, Quad9, AdGuard, CleanBrowsing, dns0.eu). When enabled, connecting to any DoH-capable DNS automatically registers global `netsh dns` rules (handling pre-existing Windows 11 templates cleanly) and applies per-interface `Dnscache\InterfaceSpecificParameters\{guid}\DohInterfaceSettings` registry flags.
- **Diagnostic Bug Logs & One-Click Bug Report Export:** Added **Diagnostic Bug Logs** toggle (`diagnostics.log`) and a **Send Bug Report** button that packages sanitized system/adapter info and logs into a ready-to-attach ZIP bundle, copies a summary to the clipboard, and opens the GitHub issue page.
- **System Tray Enabled by Default:** Changed `MinimizeToTray` default to `true` for seamless background operation out of the box.

## [0.9.1] - 2026-11-25

### Visual Hotfix & UI Polish
- **Fixed Duplicate Windows 11 Caption Buttons (`— □ ×`):** Permanently suppressed native DWM caption buttons via `VisualBackdropService.HideNativeCaptionButtons` (clears `WS_SYSMENU` on startup, state change, and window activation while keeping native rounded corners, Aero Snap, Acrylic Glass, and `Alt+F4`). Added dedicated crimson hover styling (`WindowChromeCloseBtnStyle`) for the custom Close button.
- **Fixed Glass Corner Alignment (Win11 Rounded Frame):** Synchronized `CornerRadius` across `RootWindowBorder`, `TitleBarBorder`, `SidebarBorder`, `ConsoleBorder`, and `UpdateModalOverlay` (with full LTR/RTL awareness and crisp square reset when Maximized).
- **Fixed Glass-Mode Popup, Dropdown & Modal Readability:** Introduced opaque theme-calibrated `PopupSurfaceBrush` for `ComboBox` dropdowns, System Tray `ContextMenu`, and `UpdateModalCard` so menus and modals stay razor-sharp even at `35%` Glass transparency.
- **Fixed DataGrid Selection, Tray Menu & Sidebar Footer Layout:** Added themed `DataGridRow`, `DataGridCell`, `ContextMenu`, and `MenuItem` styles (removing legacy Windows blue row highlights and white tray menus), pinned the Sidebar version footer cleanly at `560px` minimum window height, improved `MutedTextBrush` readability at low Brightness, and replaced blocky emoji glyphs with crisp `↻` icons.
- **Typography, Categorized DNS Grid & Alignment Polish:** Unified `Segoe UI Variable Text` / `Cascadia Mono` typography, fixed `Button` `TemplateBinding` content alignment, reorganized all 22 built-in DNS servers + Custom profiles into 4 clean responsive `UniformGrid` categories (Iran/Gaming, Global Public including `114DNS`, Security & Ad-Blocking, Custom), and standardized all headers and telemetry cards with 2-column `Grid` layouts.

## [0.9.0] - 2026-11-25

### Major UI/UX & Graphics Overhaul
- **Streamlined 4-Button Dashboard & Single-Button Game Booster Toggle:** Simplified the main Dashboard action bar to 4 focused actions: a single **Game Booster** toggle button (Green when idle, turns Red with an active badge when enabled, click again to restore), **Fastest DNS**, **Reset DNS** (resets to automatic DHCP + flushes DNS cache), and **Reset Adaptor**.
- **Graphical Software Update Center Modal:** Replaced plain `MessageBox` dialogs with a sleek, animated glassmorphic Update Center modal showing live download percentage, transfer speed (`MB/s`), downloaded/total MB, SHA-256 verification status, release notes preview, and dedicated visual states for Up-to-Date, Cancelled, and Error with one-click Cancel and Retry.
- **Native Windows Glass Mode (Acrylic / Mica Backdrop):** Added `VisualBackdropService` with true Windows 11 DWM Acrylic/Mica blur (`DWMWA_SYSTEMBACKDROP_TYPE`), WPF `CompositionTarget` transparent swapchain & `WindowChrome.GlassFrameThickness` synchronization, native rounded corners (`DWMWA_WINDOW_CORNER_PREFERENCE`), Windows 10 smooth `ACCENT_ENABLE_BLURBEHIND` fallback (zero drag lag), specular `GlassEdgeBrush` borders, plus new **Frost Cyber Glass**, **Aurora Prism Glass**, and **Frosted Acrylic Glass** presets.
- **Display Calibration Sliders (Transparency, Brightness & Contrast):** Added live custom Studio-Neon sliders in Settings for **Transparency (35%–100%)** (controls frost alpha in Glass Mode and native hardware-accelerated `WS_EX_LAYERED` opacity in Solid Mode), **Brightness (75%–130%)**, and **Contrast (80%–135%)** with a one-click **Reset Visuals** button and full persistence/export.
- **Silky GPU-Accelerated Transitions & Hover Polish:** Smooth `QuinticEase` page transitions (Fade + Slide + Scale), hover card lifts, and debounced zero-lag display sliders.
- **Responsive Auto-Reflow Window Engine:** Lowered minimum window bounds cleanly (`840x560`) with startup WorkArea clamping for 1366x768 & 150% DPI laptops, native `SingleBorderWindow` + `WindowChrome` (`CaptionHeight=44`) Aero Snap / Windows 11 Snap Layouts / drag-from-maximize / Auto-Hide Taskbar support, DPI-calibrated maximize frame insets, `WM_DPICHANGED` live resync, and `TaskbarCreated` Tray auto-recovery with automatic adaptive layout on resize — Telemetry Strip switches seamlessly between 4-column and 2x2 grid, Sidebar compacts gracefully, Header pills auto-hide before crowding chrome buttons, Help grid reflows `2 -> 1` column, and Console height adapts on shorter screens.
- **Modern Categorized Sidebar & Crisp High-DPI Rendering:** Organized sidebar navigation into clean bilingual categories (**Overview**, **Network & DNS**, **System & Security**, **Preferences**) with `ClearType` display text formatting, `UseLayoutRounding`, `HighQuality` bitmap scaling, and custom glowing sliders.

## [0.8.5] - 2026-11-25

### Added & Improved
- **Interactive Click-to-Switch Live Ping Cards:** Click directly on the top **Global Ping** card to cycle between `1.1.1.1` (Cloudflare), `8.8.8.8` (Google), `9.9.9.9` (Quad9), and `4.2.2.4` (Level3), or click the **Intranet Ping** card to cycle between `www.arvancloud.ir`, `www.salatingames.ir`, `parsvds.com`, and `iran.ir` — with instant forced re-probe and automatic persistence in settings & export/import bundles.
- **Redesigned Compact Settings UI:** Rebuilt Settings into a streamlined 2-card layout, removed duplicate Theme/Surface/Ping dropdowns, and added live dynamic resource-bound border highlights on active theme and background swatches.
- **Full Bilingual Help (English LTR + Persian RTL):** Translated all 6 visual Help guide cards, titles, and descriptions into complete Persian with proper RTL layout switching.

### Fixed
- **Instant Ping Re-Probe on Target Switch:** Added `_pingRescanRequested` queueing in `RefreshPingTelemetryAsync` so switching ping servers while a probe is in flight immediately runs a fresh measurement without waiting for the next timer tick, clearing stale jitter/min/avg/max readouts immediately.
- **Dynamic Theme Brush Binding:** Switched runtime DNS card foregrounds and active swatch borders from static `FindResource` lookups to `SetResourceReference` so Light/Dark mode and theme changes always stay synchronized.
- **Cold-Start Intranet DNS Resolve Timeout:** Increased `ResolveHostCachedAsync` timeout from `1200ms` to `2000ms` to eliminate first-probe false timeouts when switching intranet domains.

## [0.8.4] - 2026-11-25

### Changed & Improved
- **System-Grade Ping & Packet Loss Engine:** Upgraded ICMP probes to standard Windows 32-byte payload, `TTL=128`, `2000ms` timeout, and removed hidden ICMP retries so packet loss matches real `ping.exe` measurements.
- **Honest 40-Sample Sliding-Window Loss & RFC 3550 Jitter:** Removed false first-drop masking in `PingChannelTracker`, replaced instant two-sample jitter with RFC 3550 smoothed jitter, and replaced misleading header `Loss In / Out` (`Math.Min`) with real Global `Loss %` and live NIC hardware/driver drops (`Errors + Discards`).
- **Clean ICMP vs. TCP Fallback Separation:** Detects ICMP-blocked networks (e.g., VPN TUN mode or strict router firewalls) cleanly with a `TCP ONLY` badge instead of false `OFFLINE` alerts, and treats active router `TCP RST` (`ConnectionRefused`) responses as alive with accurate LAN RTT.
- **Decoupled 1s NIC Telemetry & Background Tray Updates:** Separated the 1-second NIC speedometer/stats timer from the 1.5-second ping loop so download/upload speeds never freeze during ping timeouts, merged adapter + gateway polling into one unified snapshot, and added lightweight Tray tooltip ping updates when minimized.

### Fixed
- **Zero Disk I/O in Telemetry Loop:** Added thread-safe in-memory caching for custom DNS profiles (`custom_dns.json`) so telemetry ticks never read disk synchronously.
- **Clean Reset on Adapter Switch:** Switching network adapters now resets all 3 ping trackers, byte deltas, and NIC drop baselines to prevent stale statistics and false speed spikes.
- **Native Win32 OEM Console Decoder:** Added a zero-dependency `kernel32.dll` (`GetOEMCP` + `MultiByteToWideChar`) fallback in `ProcessRunner` so non-English Windows console output decodes accurately in self-contained single-file `.NET 8` builds.

## [0.8.3] - 2026-11-21

### Added
- **Hardened Self-Contained Single-File Packaging:** Enabled `IncludeNativeLibrariesForSelfExtract=true` and Inno Setup 6/7 compatibility (`#if VER >= 0x06030000`, `shellexec` post-install launch, hidden `PrepareToInstall` taskkill, and full uninstall cleanup).
- **Hardened Auto-Updater:** Added TLS 1.2/1.3 enforcement, pre- and post-redirect GitHub host validation, NTFS ACL-locked `%ProgramData%\AionToolPro\Updates` staging via `icacls`, Windows `MZ` PE header validation, `SHA-256` verification against `checksums.txt`, and live download progress bar.
- **Native System Tray & Auto-Start:** Added zero-WinForms Win32 System Tray (`TrayIconManager`) with live ping tooltip, quick DNS switcher menu, multi-monitor taskbar-safe Maximize (`WM_GETMINMAXINFO`), and elevated Start-with-Windows via Task Scheduler.
- **True UDP/53 DNS Benchmark & Custom Profiles:** Native RFC 1035 DNS A-record benchmarking across 22 built-in providers plus persistent user-defined custom DNS profiles (`custom_dns.json`) and instant dashboard search/filter.
- **RFC 5389 STUN NAT Analyzer & Game Server Pings:** Dual-server UDP STUN test for Open/Cone/Symmetric NAT and RFC 6598 CGNAT detection, plus parallel latency checks for Steam, Riot/Valorant EU, Discord, Epic, Cloudflare, Google, and ArvanCloud.
- **IPv4 + IPv6 Per-App Connection Monitor:** Upgraded `GetExtendedTcpTable` to support both `AF_INET` and `AF_INET6` with search filtering, protected Windows process guard, and SHA-256 path-hashed firewall rules with persistent blocked list management.
- **Settings Export/Import & Full Persian RTL:** Added JSON settings/profile export and import, custom CheckBox styling, dynamic assembly version display, and full RTL layout support when Persian is selected.

### Fixed
- Fixed IPv6 DNS sticking/leaking when switching from dual-stack resolvers (e.g., Cloudflare/Google) to IPv4-only anti-sanction resolvers (e.g., Shecan/Electro).
- Fixed `AbandonedMutexException` handling, external `WM_CLOSE`/`SessionEnding` clean shutdown when Minimize-to-Tray is enabled, and added Win32 `MessageBoxW` fatal crash fallback with `InnerException` logging.
- Protected `%TEMP%\.net` single-file extraction cache and symbolic links (`ReparsePoint`) during Temp cleanup and elevated Hosts operations.
- Fixed `RunSpeedTestAsync` wall-clock timeout so stalled streams cancel cleanly after 10 seconds.
- Fixed multi-hostname Hosts blocklist line parsing and removed `ocsp.digicert.com` from the TeamSpeak custom blocklist.
