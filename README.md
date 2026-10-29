<div align="center">
  <img src="AionToolPro.Desktop/AionToolp.png" width="128" height="128" alt="AionToolPro Logo">
  <h1>AionToolPro</h1>
  <p><strong>A Modern, Discord-Themed Windows Network Management & Troubleshooting Utility</strong></p>
  
  <p>
    <img src="https://img.shields.io/badge/Version-0.6-5865F2?style=for-the-badge&logo=csharp&logoColor=white" alt="Version 0.6">
    <img src="https://img.shields.io/badge/Platform-Windows-0B0E14?style=for-the-badge&logo=windows&logoColor=white" alt="Platform Windows">
    <img src="https://img.shields.io/badge/.NET-8.0-512BD4?style=for-the-badge&logo=dotnet&logoColor=white" alt=".NET 8.0">
    <img src="https://img.shields.io/badge/License-GPLv3-blue.svg?style=for-the-badge" alt="License GPLv3">
  </p>
</div>

---

## 🚀 Overview

**AionToolPro** is a professional-grade, open-source desktop application built with **C# WPF (.NET 8.0)**, designed to replace legacy script-based network utilities with a lightning-fast, modern, and aesthetic **Discord-inspired dark UI**. 

It provides advanced network diagnostics, 16+ built-in DNS providers with one-click switching, MTU optimization, live telemetry and speed monitoring, real-time multi-channel ping tracking, and Windows firewall app-blocking capabilities.

---

## ✨ Key Features

- 🎨 **Modern Discord UI**: Frameless window design featuring custom title bars, ultra-thin custom scrollbars, and a clean dark theme palette.
- 🌐 **Comprehensive DNS Catalog**: Over 16 global and anti-sanction DNS providers (including Shecan, Electro, Radar Game, Cloudflare, Google, Quad9, and AdGuard) with auto-configuration and backup/restore features.
- ⚡ **Live Multi-Channel Ping & Telemetry**: Monitor Global, Local, and Gateway latencies in real time alongside a built-in live network interface speedmeter (`Rx/Tx`).
- 🛡️ **Firewall & App Blocker**: Easily restrict or block any executable (`.exe`) from accessing the internet via Windows Advanced Firewall rules.
- 🔧 **MTU & Network Optimizer**: Scan and determine the optimal maximum transmission unit to eliminate packet loss and latency spikes during gaming.
- 🛑 **Process Control & Live Stream**: Cancel long-running CLI or system diagnostics commands instantly using the built-in `⏹ Stop` mechanism.
- 🌍 **Dual-Language Support**: Seamlessly switch between English and Persian (فارسی).
- 🛎️ **System Tray Integration**: Minimize to the Windows system tray to run network telemetry seamlessly in the background.

---

## 🛠️ Tech Stack

- **Language**: C# 12 / .NET 8.0 (Windows-specific)
- **Framework**: Windows Presentation Foundation (WPF)
- **UI Architecture**: Custom XAML styles, Dynamic Theme Manager, and Discord-styled layouts.
- **Deployment**: Portable Standalone Exe & Inno Setup professional installer script.

---

## 📦 Installation & Getting Started

### Prerequisites
- Windows 10 / 11 (64-bit)
- Administrator privileges (required for modifying network adapters, DNS, and firewall configurations).

### Method 1: Pre-built Release (Recommended)
1. Download the latest `AionToolPro_Setup.exe` from the [Releases](../../releases) page.
2. Run the installer and follow the on-screen instructions. A shortcut will be automatically created on your desktop.

### Method 2: Running from Source / Development
1. Clone the repository:
   ```bash
   git clone https://github.com/YourUsername/AionToolPro.git
   cd AionToolPro
   ```
2. Open the solution in **Visual Studio 2022** (.NET 8.0 SDK required).
3. Right-click `Run-As-Admin.bat` in the root directory to launch the application with administrator privileges, or build the solution directly.

---

## 💡 Usage Guide

- **Dashboard**: View your active network adapter health state (Connected/Disconnected/Disabled), live upload/download speed metrics, and quick diagnostic toggles.
- **DNS Switcher**: Select your preferred DNS provider from the catalog and click **Apply DNS** to route your traffic instantly.
- **Live Ping & Tracert**: Track packet response times line-by-line. Use the **Stop** button anytime to cancel the operation safely.
- **System Tray**: Closing the window will minimize AionToolPro to the tray. Right-click the tray icon to quickly switch popular DNS profiles or exit the application.

---

## 🤝 Contributing

Contributions, issues, and feature requests are welcome! Feel free to check the [issues page](../../issues).

1. Fork the Project
2. Create your Feature Branch (`git checkout -b feature/AmazingFeature`)
3. Commit your Changes (`git commit -m 'Add some AmazingFeature'`)
4. Push to the Branch (`git origin feature/AmazingFeature`)
5. Open a Pull Request

---

## 📄 License

This project is open-source software licensed under the **GNU General Public License v3.0 (GPL-3.0)**. 

See the [LICENSE](LICENSE) file for more details.

---
<div align="center">
  <p>Developed with ❤️ by <strong>TheYoqaizo</strong></p>
</div>
