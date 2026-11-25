# Changelog

All notable changes to **AionToolPro** are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.8.4] - 2026-11-25

### Changed & Improved
- **System-Grade Ping & Packet Loss Engine:** Replaced rate-limited `4.2.2.4` default with Cloudflare `1.1.1.1` (plus selectable Global Ping Server in Settings: `1.1.1.1`, `8.8.8.8`, `9.9.9.9`, `4.2.2.4`). Upgraded ICMP probes to standard Windows 32-byte payload, `TTL=128`, `2000ms` timeout, and removed hidden ICMP retries so packet loss matches real `ping.exe` measurements.
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
- Fixed `.NET 8` OEM codepage registration in `ProcessRunner` so non-English Windows `netsh` outputs decode cleanly.
- Fixed `AbandonedMutexException` handling, external `WM_CLOSE`/`SessionEnding` clean shutdown when Minimize-to-Tray is enabled, and added Win32 `MessageBoxW` fatal crash fallback with `InnerException` logging.
- Protected `%TEMP%\.net` single-file extraction cache and symbolic links (`ReparsePoint`) during Temp cleanup and elevated Hosts operations.
- Fixed `RunSpeedTestAsync` wall-clock timeout so stalled streams cancel cleanly after 10 seconds.
- Fixed multi-hostname Hosts blocklist line parsing and removed `ocsp.digicert.com` from the TeamSpeak custom blocklist.
