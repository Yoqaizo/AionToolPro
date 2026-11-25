<p align="center">
  <img src="AionToolPro.Desktop/AionToolp.png" alt="AionToolPro Logo" width="128" height="128" />
</p>

<h1 align="center">AionToolPro</h1>

<p align="center">
  <strong>Modern Windows DNS, Network Telemetry, Diagnostics &amp; System Optimization Suite</strong>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/Version-0.9.2-5865F2?style=for-the-badge" alt="Version 0.9.2" />
  <img src="https://img.shields.io/badge/.NET-8.0%20WPF-512BD4?style=for-the-badge&logo=dotnet&logoColor=white" alt=".NET 8.0 WPF" />
  <img src="https://img.shields.io/badge/Platform-Windows%2010%2F11%20x64-0078D6?style=for-the-badge&logo=windows&logoColor=white" alt="Windows 10/11 x64" />
  <img src="https://img.shields.io/badge/License-GPL--3.0-10B981?style=for-the-badge" alt="GPL-3.0 License" />
</p>

<p align="center">
  <a href="https://github.com/Yoqaizo/AionToolPro/releases/latest">Download Latest Release</a> &bull;
  <a href="CHANGELOG.md">Changelog</a> &bull;
  <a href="https://github.com/Yoqaizo/AionToolPro/issues">Report an Issue</a>
</p>

---

## Overview

**AionToolPro** is a native Windows desktop utility built with C# and .NET 8 WPF designed for gamers, network engineers, and power users. It combines real-time multi-target latency telemetry, native RFC 1035 UDP/53 DNS benchmarking, Windows 11 DNS-over-HTTPS (DoH) management, IPv4/IPv6 per-application socket inspection, atomic Hosts blocklist management, and safe Windows maintenance tools in a sharp, high-DPI PerMonitorV2 interface with English and Persian (RTL) localization.

## Features

| Module | Capabilities |
| :--- | :--- |
| **Live Telemetry Strip** | Clickable live Global (`1.1.1.1` / `8.8.8.8` / `9.9.9.9` / `4.2.2.4`), clickable Intranet (`arvancloud.ir` / `salatingames.ir` / `parsvds.com` / `iran.ir`), and auto-detected Local Router gateway latency, RFC 3550 jitter, 40-packet loss, and independent 1s speedometers. |
| **DNS Studio &amp; Custom Profiles** | 22 built-in Iranian anti-sanction, gaming, privacy, and global DNS presets plus persistent custom DNS profiles, automatic IPv6 leak cleanup, non-destructive JSON backups, and one-click DHCP reset. |
| **True UDP/53 DNS Benchmark** | Sends native RFC 1035 DNS A-record queries over UDP port 53 across all resolvers concurrently with zero external NuGet dependencies. |
| **Encrypted DoH (Windows 11)** | Enables native Windows 11 DNS-over-HTTPS (`udpfallback=yes`) for Cloudflare, Google, and Quad9 with scoped removal that preserves user-defined rules. |
| **Diagnostics &amp; STUN NAT** | RFC 5389 dual-server UDP STUN NAT analyzer (Open / Cone / Symmetric + RFC 6598 CGNAT detection), 2-stream Cloudflare speed test, Game Server Ping matrix, Traceroute, Ping Monitor, and TCP Port Scanner. |
| **Active Connections &amp; Firewall** | Native Win32 `GetExtendedTcpTable` (`AF_INET` &amp; `AF_INET6`) monitor with search filter, protected system process guard, and SHA-256 path-hashed Windows Firewall blocking with persistent unblock management. |
| **Wi-Fi &amp; Atomic Hosts Manager** | Language-independent Wi-Fi profile extraction (masked by default, securely wiped temp XML) and atomic UTF-8 (no BOM) Hosts blocklist management with multi-host parsing and automatic backups. |
| **Safe Game Booster &amp; Maintenance** | Suspends only currently running non-essential Windows services with exact state restoration, 24-hour guarded Temp cleanup (protecting `%TEMP%\.net`), DISM/SFC repair, and safe Windows Update cache reset. |
| **Tray, Themes &amp; Auto-Updater** | Native Win32 System Tray with live ping tooltip and quick DNS menu, elevated Start-with-Windows via Task Scheduler, 13 themes (including Glass presets + Full-Spectrum Custom Color Studio) + 9 surface palettes + native Windows 10/11 DWM Acrylic Glass Mode, live Transparency (35%–100%), Brightness & Contrast calibration, and smooth GPU-accelerated animations, settings Export/Import, and SHA-256 verified GitHub Releases auto-updater. |

## Security Architecture

- **Strict System32 Execution:** All OS utilities (`netsh`, `ipconfig`, `ping`, `tracert`, `nslookup`, `DISM`, `sfc`, `schtasks`, `icacls`, `taskkill`) are resolved strictly from `System32` and invoked via `ProcessStartInfo.ArgumentList` to prevent Path Hijacking and Argument Injection.
- **Hardened Auto-Updater:** Enforces TLS 1.2/1.3, validates trusted GitHub HTTPS hosts both before and after redirects, stages installers inside an NTFS ACL-locked `%ProgramData%\AionToolPro\Updates` directory (`SYSTEM` and `Administrators` only), verifies the Windows `MZ` PE header, and checks `SHA-256` against `checksums.txt`.
- **Safe Elevated File I/O:** Checks `FileAttributes.ReparsePoint` before elevated writes or cleanups, uses atomic `File.Replace` for Hosts and settings, and preserves `%TEMP%\.net` extraction caches.

> **Why Administrator Privileges Are Required:** AionToolPro requests `<requestedExecutionLevel level="requireAdministrator" />` to configure network adapters (`netsh`), manage Windows Defender Firewall rules, update `C:\Windows\System32\drivers\etc\hosts`, configure Windows 11 DoH, and manage Windows services.

## Installation

1. Download the latest `AionToolPro_Setup_v0.9.2.exe` and `checksums.txt` from the [GitHub Releases](https://github.com/Yoqaizo/AionToolPro/releases/latest) page.
2. *(Optional)* Verify the installer integrity in PowerShell:
   ```powershell
   Get-FileHash .\AionToolPro_Setup_v0.9.2.exe -Algorithm SHA256
   ```
3. Run the installer and follow the setup wizard.

## Building from Source

### Prerequisites
- Windows 10 or Windows 11 (x64)
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

### Build Commands
```powershell
# Compile Release build
dotnet build AionToolPro.Desktop\AionToolPro.Desktop.csproj -c Release --no-incremental

# Publish self-contained single-file x64 executable
dotnet publish AionToolPro.Desktop\AionToolPro.Desktop.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true /p:PublishReadyToRun=true /p:EnableCompressionInSingleFile=true /p:DebugType=None /p:DebugSymbols=false
```

## License

This project is licensed under the **GNU General Public License v3.0** — see the [LICENSE](LICENSE) file for details.
