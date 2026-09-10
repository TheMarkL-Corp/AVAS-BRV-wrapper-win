# AVAS Routing SW — Quickstart Guide (v1.2.0)

Welcome to **AVAS Routing SW** (`AVAS-BRV-wrapper-win`), a lightweight, high-performance native Windows desktop application designed to control and monitor SDVoE AV distribution networks.

This application provides a dual-capability management cockpit:
1. **Embedded Web Management**: Wraps and renders Semtech's **BlueRiver AV Manager** web interface directly inside a high-speed Microsoft WebView2 Chromium container.
2. **Real-Time Multicast Preview Overlay & Multi-Link Manager**: Provides a floating, collapsible native sidebar delivering live uncompressed video previews ($\ge 1$ FPS) and Multi-Link configuration for connected **Advantech AVAS-223 encoders** using SDVoE Control Server APIs and RFC 3550 / RFC 4175 UDP multicast ingestion.
3. **Zero-Internet Package Installer & Advantech White-Labeling**: Bundles offline runtime prerequisites and an automated 4-step pipeline to re-brand the local BlueRiver AV Manager interface.

---

## 1. Quick Start: Choosing Your Installation Method

AVAS Routing SW v1.2.0 provides two official distribution options:

### Option A: Complete Offline Installer (Recommended)
Download:
```
releases/AVAS-Routing-SW-Installer-v1.2.0.zip
```
1. Extract the zip archive and double-click `Install.bat` (or `AvasRoutingSetup.exe`).
2. **Zero-Internet Runtime Validation**: The installer automatically checks if **.NET 8 Desktop Runtime** and **Microsoft Edge WebView2 Runtime** are installed. If missing, it silently installs them from local bundled packages (`redist/`) without requiring internet access.
3. **Customization & Shortcuts**: Choose whether to create Desktop and Start Menu shortcuts.
4. **Advantech White-Labeling**: Optionally enable the 4-step Advantech branding pipeline for BlueRiver AV Manager.
5. Setup installs to `%LocalAppData%\Programs\Advantech\AVAS Routing SW` with registered Windows Programs & Features uninstaller support.

### Option B: Standalone Portable Application
Download:
```
releases/AVAS-Routing-SW-v1.2.0-win-x64.zip
```
1. Extract the archive anywhere on your system (e.g. `C:\Tools\AVAS-Routing-SW` or a portable USB drive).
2. Launch `AvasRoutingApp.exe`. No installation or administrative privileges required.

---

## 2. Server Connections & Default Settings

Launch `AvasRoutingApp.exe` and click the **⚙ Settings** button in the top toolbar to configure your network parameters:

| Setting | Default Value | Description |
|---|---|---|
| **BlueRiver AV Manager URL** | `http://localhost:80` | Address of your BlueRiver Web GUI (default port 80) |
| **SDVoE Control Server IP** | `127.0.0.1` | IP address of the SDVoE Control Server (`controlserver.exe`) |
| **SDVoE REST Port** | `8090` | REST API service port on the Control Server |
| **SDVoE Telnet Port** | `6970` | Telnet command/event port on the Control Server |
| **Multicast IP Start** | `224.1.3.1` | Starting multicast address for preview pools |
| **Multicast IP End** | `224.1.3.225` | Ending multicast address for preview pools |
| **Base UDP Port** | `5000` | Base port for incoming video datagrams |
| **Local NIC IP (Optional)** | *(Auto / Empty)* | Specific NIC IP to bind for IGMP multicast joins |
| **Theme** | `Light` | UI Theme (`Light` / `Dark`) |
| **Sidebar Width** | `400` | Collapsible preview sidebar pixel width |

Click **Save Settings**. Settings are atomically persisted to `appsettings.json`.

---

## 3. Operating the Live Preview & Multi-Link Overlay

1. **Expand Sidebar**: Click the vertical **◀ PREVIEW** strip on the right border.
2. **Hardware Filtering**: The client connects to the SDVoE Control Server and filters devices strictly for Advantech AVAS-223:
   - **Vendor ID**: `105` (Advantech)
   - **Product ID**: `81` (AVAS-223)
   - **Chip Index**: `0` (Primary video processing chip; `chip_1` secondary aggregators are handled under Multi-Link)
3. **Live Streaming**: Dynamic collision-free multicast IPs are allocated from the pool, starting RFC 3550 RTP / RFC 4175 raw video ingestion with real-time rolling FPS metrics ($\ge 1$ FPS).
4. **Collapse Sidebar**: Click **▶ CLOSE** anytime. Multicast memberships and network sockets are instantly terminated to conserve bandwidth.

---

## 4. Advantech White-Labeling Pipeline

When selected during installation (or via setup maintenance), the installer performs an automated 4-step branding process for BlueRiver AV Manager:

1. **Step 1 (Detect & Stop Service)**: Probes if the Windows Service `bavm` exists. If not found, notifies user gracefully and continues installation. If found, stops the `bavm` service.
2. **Step 2 (Deploy Logo)**: Backs up original `logo.svg` to `logo.svg.bak` and deploys Advantech's corporate `logo.svg` into `%APPDATA%\Semtech\BlueRiver AV Manager\app\front\images\`.
3. **Step 3 (Theme & Header Configuration)**: Backs up original `index.js` to `index.js.bak` and injects Advantech branding into `%APPDATA%\Semtech\BlueRiver AV Manager\app\src\config\index.js`:
   ```javascript
   APP_TITLE: 'AV Manager',
   APP_HEADER: 'AV Manager',
   THEME_PRIMARY_COLOR: '#0055afff'
   ```
4. **Step 4 (Service Restart)**: Starts the `bavm` Windows service and confirms operational status.

---

## 5. Automated Test Suite & Verification

AVAS Routing SW v1.2.0 is validated by two automated test suites comprising **434 automated tests** passing at **100%**:

- **`AvasRoutingApp.Tests`**: **247 / 247 Passed (100%)**
  - Configuration validation, resilience, stress, and corruption recovery
  - Installer dependency detection, silent deployment, and white-labeling pipeline
  - RTP scanline reassembly and Q10 fixed-point rasterization
- **`E2ETests`**: **187 / 187 Passed (100%)**
  - Full Tier 1–5 functional coverage, boundary corner cases, multi-device concurrency, and endurance loops

---

## 6. Official Release Packages (v1.2.0)

| Package Asset | Size | Target Environment | Contents |
|---|---|---|---|
| **[AVAS-Routing-SW-Installer-v1.2.0.zip](https://github.com/TheMarkL-Corp/AVAS-BRV-wrapper-win/releases/download/v1.2.0/AVAS-Routing-SW-Installer-v1.2.0.zip)** | ~383.2 MB | Offline / Zero-Internet PC | Self-contained `AvasRoutingSetup.exe`, `Install.bat`, bundled `.NET 8 Desktop Runtime`, bundled `WebView2 Runtime`, Advantech white-labeling assets, and full app payload |
| **[AVAS-Routing-SW-v1.2.0-win-x64.zip](https://github.com/TheMarkL-Corp/AVAS-BRV-wrapper-win/releases/download/v1.2.0/AVAS-Routing-SW-v1.2.0-win-x64.zip)** | ~748 KB | Pre-configured Windows x64 | Zero-install portable `AvasRoutingApp.exe`, DLLs, default config (`http://localhost:80`), and full documentation |

---

## 7. Documentation Directory

- **[User Manual](./USER_MANUAL.md)**: Exhaustive manual with UI diagrams, settings reference, and network troubleshooting.
- **[Verification Guide](./VERIFICATION_GUIDE.md)**: Deep dive into the 5-tier test harness and verification methodologies.
- **[Loop Test Results](./LOOP_TEST_RESULTS.md)**: Official 4,005-test endurance verification record.
- **[GitHub Repository](https://github.com/TheMarkL-Corp/AVAS-BRV-wrapper-win)**: Source code, issue tracker, and releases.