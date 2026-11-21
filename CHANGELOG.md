# Changelog

All notable changes to **AionToolPro** are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
