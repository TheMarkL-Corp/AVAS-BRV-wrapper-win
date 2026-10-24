# AVAS Routing SW — Quickstart Guide (v1.8.0)

Welcome to **AVAS Routing SW** (`AVAS-BRV-wrapper-win`), a lightweight, high-performance native Windows desktop application designed to control and monitor SDVoE AV distribution networks.

This application provides a dual-capability management cockpit:
1. **Embedded Web Management**: Wraps and renders Semtech's **BlueRiver AV Manager** web interface directly inside a high-speed Microsoft WebView2 Chromium container.
2. **Mode-Aware Preview Overlay (v1.8.0)**: Delivers live video previews ($\ge 1$ FPS) with topology-conscious stream dispatch across Single Link and Dual Link AVAS-223 encoders:
   - **Dual Link**: Chip 0 master + MCU UART in-band tunnel on UDP port 5000 with automatic fallback to BlueRiver API.
   - **Single Link**: Direct Semtech BlueRiver Stream 1 Hardware Scaler node on UDP port 5004 with `auto_stop: true` and `remove_audio: true`.
3. **Single-Link Chip 0 / Chip 1 Toggle (v1.8.0)**: Interactive segmented pill (`[ Chip 0 | Chip 1 ]`) in the card header for dynamic link inspection with deterministic teardown (`address 0.0.0.0`) and zero orphan multicast subscriptions.
4. **Concurrent Dual-Port Demux Receiver (v1.8.0)**: Simultaneously ingests UDP 5000 and UDP 5004 with `ReuseAddress = true`, 8MB receive buffers, and strict socket-level source-IP whitelisting (REA Report 06) dropping unmonitored packets at socket entry.
5. **Multi-Link Management**: Inspect companion chip (`chip_1`) online/offline health and toggle Single/Dual link bonding modes with safe non-blocking reboot countdown.
6. **Touchscreen Ergonomics & Thermal Telemetry**: Tailored for clinical monitors and medical touch carts with 36px–44px touch targets, native direct touch flick/drag scrolling (`PanningMode="VerticalOnly"`), high-contrast touch press feedback, temperature telemetry monitoring with >70°C alert styling, and configurable sidebar typography presets (Small/Normal/Large).
7. **Zero-Internet Package Installer & Advantech White-Labeling**: Bundles offline runtime prerequisites and an automated 4-step pipeline to re-brand the local BlueRiver AV Manager interface.

---

> [!WARNING]
> **Prerequisite Notice: BlueRiver AV Manager Not Included**
> Please note that this installer and standalone application package **do NOT include** the installer for Semtech's **BlueRiver AV Manager**. In order for this application and installer to work properly, **BlueRiver AV Manager must already be installed** on the target machine (or accessible over the network). The optional Advantech white-labeling feature specifically requires a local installation of BlueRiver AV Manager (`bavm` service and local files) in order to apply branding patches.

---

## 1. Quick Start: Choosing Your Installation Method

AVAS Routing SW v1.8.0 provides two official distribution options:

### Option A: Complete Offline Installer (Recommended)
Download:
```
releases/AVAS-Routing-SW-Installer-v1.8.0.zip
```
1. Extract the zip archive and double-click `Install.bat` (or `AvasRoutingSetup.exe`).
2. **Zero-Internet Runtime Validation**: The installer automatically checks if **.NET 8 Desktop Runtime** and **Microsoft Edge WebView2 Runtime** are installed. If missing, it silently installs them from local bundled packages (`redist/`) without requiring internet access.
3. **Customization & Shortcuts**: Choose whether to create Desktop and Start Menu shortcuts.
4. **Advantech White-Labeling**: Optionally enable the 4-step Advantech branding pipeline for BlueRiver AV Manager *(requires local BlueRiver AV Manager installation)*.
5. Setup installs to `%LocalAppData%\Programs\Advantech\AVAS Routing SW` with registered Windows Programs & Features uninstaller support.

### Option B: Standalone Portable Application
Download:
```
releases/AVAS-Routing-SW-v1.8.0-win-x64.zip
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
| **SDVoE REST Port** | `8080` | REST API service port on the Control Server |
| **SDVoE Telnet Port** | `6970` | Telnet command/event port on the Control Server |
| **Multicast IP Start** | `225.0.0.1` | Starting multicast address for preview pools |
| **Multicast IP End** | `225.0.1.254` | Ending multicast address for preview pools |
| **Multicast Base Port** | `5000` | Base UDP port (Dual Link: 5000, Single Link Scaler: 5004) |
| **Local NIC IP (Optional)** | *(Auto / Empty)* | Specific NIC IP to bind for IGMP multicast joins |
| **Theme** | `Light` | UI Theme (`Light` / `Dark`) |
| **Sidebar Width** | `400` | Collapsible preview sidebar pixel width |

Click **Save Settings**. Settings are atomically persisted to `appsettings.json`.

---

## 3. Operating the Live Preview & Multi-Link Overlay

1. **Expand Sidebar**: Click the vertical **◀ PREVIEW** strip on the right border.
2. **Operator Mode (Default)**: Operators interact with two dedicated tabs:
   - **`📺 Previews`**: Minimalist cards showing 16:9 video feeds, status indicator dot, rolling FPS counter, clean `Single Link` (`#475569`) or `Dual Link` (`#2563EB`) badges, dynamic `[ Chip 0 | Chip 1 ]` selector for Single Link devices, and 2-line IP / MAC footer with 1-click clipboard copy.
   - **`🔗 Multi-Link`**: Inspect companion chip (`chip_1`) online/offline health and toggle Single/Dual link bonding modes with safe non-blocking reboot countdown.
3. **Single Link Chip Inspection**:
   - For Single Link devices with companion chips, click `[ Chip 1 ]` in the card header to switch inspection to Chip 1.
   - The application safely stops Chip 0 with `address 0.0.0.0` and seeds the Stream 1 Scaler on Chip 1 on UDP port 5004.
4. **Technician Access (Advanced Tab)**:
   - Open **⚙ Settings** and locate the **Advanced Settings (Technician Access)** section.
   - Enter technician PIN **`00000000`** (8 zeros) and click **Unlock**.
   - A dynamic **`[ ⚙ Advanced ]`** tab appears in the sidebar providing device thermals (>70°C alert monitoring) and physical hardware diagnostics (MCU watchdog reboot, MCU EEPROM factory reset, BlueRiver PHY reboot, and BlueRiver factory reset) with dual REST and Telnet fallbacks.
   - Click **🔒 Lock** inside the Advanced tab anytime to instantly return to standard operator mode.
5. **Hardware Filtering & Early Demuxing**:
   - Strictly targets **AVAS-223 chip_0** (`Vendor ID: 105`, `Product ID: 81`, `ChipIndex: 0`).
   - UDP datagrams on ports 5000 and 5004 undergo early source-IP whitelist filtering (REA Reports 01 & 06) to drop rogue packets at the socket boundary.
6. **Collapse Sidebar**: Click **▶ CLOSE** anytime. Multicast memberships and network sockets are instantly terminated to conserve bandwidth.

---

## 4. Advantech White-Labeling Pipeline

When selected during installation (or via setup maintenance), the installer performs an automated 4-step branding process for BlueRiver AV Manager:

> [!NOTE]
> BlueRiver AV Manager must be installed locally on the system for the white-labeling pipeline to locate the service and branding directories.

1. **Step 1 (Detect & Stop Service)**: Probes if the Windows Service `bavm` exists. If not found, notifies user gracefully and continues installation without failing. If found, stops the `bavm` service.
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

AVAS Routing SW v1.8.0 is validated by two automated test suites comprising **580 automated tests** passing at **100%**:

- **`AvasRoutingApp.Tests`**: **389 / 389 Passed (100%)**
  - Configuration validation, resilience, stress, and corruption recovery
  - Multi-Link chip_0 exclusivity, companion pairing, and REST auto-probing
  - Concurrent dual-port (5000/5004) UDP demultiplexer and tolerant scanline reassembly
  - Mode-aware preview stream dispatch (Single Link Scaler vs Dual Link UART)
  - Single-Link Chip 0 / Chip 1 dynamic switching and deterministic `address 0.0.0.0` teardown
  - Universal AVAS Diagnostics (TX & RX endpoints), in-dialog target switcher, and VEGA isolation
- **`E2ETests`**: **191 / 191 Passed (100%)**
  - Full Tier 1–5 functional coverage, boundary corner cases, multi-device concurrency, and endurance loops
  - Single Link and Dual Link end-to-end transmitter stream lifecycle and teardown verification

---

## 6. Official Release Packages (v1.8.0)

| Package Asset | Size | Target Environment | Contents |
|---|---|---|---|
| **[AVAS-Routing-SW-Installer-v1.8.0.zip](https://github.com/TheMarkL-Corp/AVAS-BRV-wrapper-win/releases/download/v1.8.0/AVAS-Routing-SW-Installer-v1.8.0.zip)** | ~366.3 MB | Offline / Zero-Internet PC | Self-contained `AvasRoutingSetup.exe`, `Install.bat`, bundled `.NET 8 Desktop Runtime`, bundled `WebView2 Runtime`, Advantech white-labeling assets, and full app payload |
| **[AVAS-Routing-SW-v1.8.0-win-x64.zip](https://github.com/TheMarkL-Corp/AVAS-BRV-wrapper-win/releases/download/v1.8.0/AVAS-Routing-SW-v1.8.0-win-x64.zip)** | ~1.6 MB | Pre-configured Windows x64 | Zero-install portable `AvasRoutingApp.exe`, DLLs, default config (`http://localhost:80`), and full documentation |

---

## 7. Documentation Directory

- **[User Manual](./USER_MANUAL.md)**: Exhaustive manual with UI diagrams, settings reference, and network troubleshooting.
- **[Verification Guide](./VERIFICATION_GUIDE.md)**: Deep dive into the 5-tier test harness and verification methodologies.
- **[Loop Test Results](./LOOP_TEST_RESULTS.md)**: Official 4,005-test endurance verification record.
- **[GitHub Repository](https://github.com/TheMarkL-Corp/AVAS-BRV-wrapper-win)**: Source code, issue tracker, and releases.
