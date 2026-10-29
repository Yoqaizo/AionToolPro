<div align="center">

  <h1>🚀 AionToolPro v0.7</h1>
  <p><strong>A professional-grade, modern-UI network management, DNS switcher, latency monitor, and system optimization suite built for Windows.</strong></p>

  <p>
    <img src="https://img.shields.io/badge/Version-0.7-5865F2?style=for-the-badge&logo=dotnet&logoColor=white" alt="Version 0.7">
    <img src="https://img.shields.io/badge/Platform-Windows%2010%2F11-0B0E14?style=for-the-badge&logo=windows&logoColor=white" alt="Platform">
    <img src="https://img.shields.io/badge/Framework-.NET%208.0%20%7C%20WPF-38BDF8?style=for-the-badge&logo=csharp&logoColor=white" alt=".NET 8">
    <img src="https://img.shields.io/badge/License-MIT-10B981?style=for-the-badge" alt="License">
  </p>

</div>

---

## 📖 Overview

**AionToolPro** is a modern, high-performance C# WPF desktop application engineered to replace legacy script-based network utilities. Designed with a sleek, Discord-inspired dark mode aesthetic and fluid interactions, it empowers power users, developers, and gamers to diagnose connection health, bypass regional restrictions, benchmark DNS providers, optimize network performance, and maintain system stability—all within a single, lightweight portable or installable package.

---

## ✨ Key Features

### 🌐 Network Intelligence & Telemetry
* **Live Multi-Channel Ping Monitoring**: Continuous tracking across three independent channels (Global Edge, Local Intranet/Gateway, and Custom Targets) with automated Jitter and Packet Loss calculations.
* **Real-Time Speedometer**: Live download and upload throughput meters integrated into the top app header.
* **🚀 Real-Time Speed Test**: Instant bandwidth diagnostics via high-speed content delivery networks.
* **🌍 NAT Type Analyzer**: Advanced socket inspection to detect Open, Moderate, or Strict (CGNAT) network configurations.

### ⚡ Advanced DNS Studio
* **22+ Integrated DNS Providers**: Pre-configured catalogs for anti-sanction, gaming, and security resolvers (Shecan, Electro, Radar Game, 403 Online, Cloudflare, Google, Quad9, AdGuard, etc.).
* **Parallel Latency Benchmarking**: Automated testing of all providers simultaneously, highlighting the lowest-latency winner instantly.
* **DNS-over-HTTPS (DoH)**: Modern secure DNS resolver configurations.
* **Instant Apply & Flush**: Seamless network adapter binding with automatic DNS cache clearing.

### 🎮 Gaming & System Performance
* **🎮 Game Booster Mode**: Purges working set RAM across active processes, suspends background telemetry tasks, and terminates resource-heavy non-essential applications.
* **🔧 MTU Optimizer**: Automated packet fragmentation discovery to eliminate lag and connection drops in online games.
* **🛡️ Application Firewall Blocker**: Instantly restrict or permit internet access for specific `.exe` binaries using native Windows Firewall rules.

### 🧰 Diagnostics & Maintenance
* **Wi-Fi Credential Vault**: Extract and review saved wireless profiles and plaintext security keys.
* **System Repair Toolkit**: One-click execution of `DISM`, `SFC` system file verification, Windows Update cache purging, and temporary file cleanup.
* **Network Reset Utilities**: Quick execution of `ipconfig /flushdns`, `netsh winsock reset`, and IP lease renewals.

### 🎨 Modern UI / UX Architecture
* **Discord-Themed Dark Mode**: Crafted with deep dark surfaces (`#0B0E14`, `#171C28`), sleek borders, and custom ultra-thin scrollbars.
* **10 Dynamic Color Themes**: Switch instantly between Discord Blurple, Cyberpunk Sky Blue, Matrix Emerald, Crimson Tactical Red, Solar Amber, and more (including a clean daylight mode).
* **System Tray Integration**: Minimize to tray with quick-access context menus for fast DNS switching without opening the main window.
* **Dual-Language Support**: Full out-of-the-box localization for English and Persian (Farsi).

---

## 🛠️ Tech Stack

* **Language**: C# 12
* **Framework**: .NET 8.0 (Windows Presentation Foundation - WPF)
* **Interop**: Native Windows API / Win32 Interop (`user32.dll`, `psapi.dll`, `iphlpapi.dll`)
* **Deployment**: Standalone self-contained publishing & Inno Setup installer packaging.

---

## 🚀 Installation & Getting Started

### Prerequisites
* **Windows 10 or Windows 11 (64-bit)**
* Administrator privileges (required for network adapter modifications, firewall rules, and system repair utilities).

### Method 1: Quick Development Launch (Requires .NET 8 SDK)
1. Clone or extract the repository.
2. Right-click **`Run-As-Admin.bat`** in the root directory and select **Run as administrator**.

### Method 2: Building a Portable Standalone Executable
To bundle the .NET 8 runtime directly into a single standalone portable `.exe`:
1. Execute **`Publish-Standalone-Exe.bat`**.
2. Locate the generated standalone executable inside the publish output folder.

### Method 3: Creating a Professional Setup Installer
1. Run `Publish-Standalone-Exe.bat` to generate the release binary.
2. Open **`AionToolPro_Installer.iss`** using **Inno Setup Compiler**.
3. Press **F9** (or click Build > Compile) to package the professional installer into the `Installer/` directory.

---

## 📁 Project Structure

```text
AionToolPro/
│
├── AionToolPro.Desktop/
│   ├── Models/            # Network adapters, DNS providers, blocklists, telemetry data models
│   ├── Services/          # Core network utilities, ping threads, process executors, tray service
│   ├── Themes/            # Dynamic color theme definitions and resource dictionaries
│   ├── App.xaml           # Global styles, Discord scrollbars, button templates
│   ├── MainWindow.xaml    # Main layout, tabs, dashboards, and console view
│   └── MainWindow.xaml.cs # UI event handlers and service bindings
│
├── Run-As-Admin.bat       # Developer elevation script
├── Publish-Standalone.bat # Standalone self-contained publishing script
└── AionToolPro_Installer.iss # Inno Setup configuration script
```

---

## 👤 Author & Credits

* **Created by**: **Yoqaizo**

---

## 📄 License

This project is licensed under the terms of the **MIT License**. See the [LICENSE](LICENSE) file for details.
