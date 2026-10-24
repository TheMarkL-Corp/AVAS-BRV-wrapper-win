# AVAS Routing SW — User Manual (v1.8.0)

## 1. Product Introduction
**AVAS Routing SW** is a high-performance native Windows desktop application designed to control and monitor SDVoE AV distribution networks. It serves as a comprehensive management environment:
1. **Embedded Web Management**: Wraps and renders Semtech's **BlueRiver AV Manager** web interface directly inside a high-speed Microsoft WebView2 Chromium container.
2. **Mode-Aware Real-Time Multicast Preview Overlay (v1.8.0)**: Provides a floating, collapsible native sidebar delivering live uncompressed video previews ($\ge 1$ FPS) for connected **Advantech AVAS-223 encoders** using topology-aware stream dispatch across Single Link and Dual Link configurations.
3. **Single Link Chip 0 / Chip 1 Selection (v1.8.0)**: Clean segmented pill (`[ Chip 0 | Chip 1 ]`) in Single Link card headers to inspect either silicon chip on the fly with deterministic teardown and zero orphan multicast streams.
4. **Multi-Link Management**: Dedicated interface to inspect and configure **Single-Link vs Dual-Link** operation on AVAS-223 TX units, track companion chip (`chip_1`) online/offline health in real time, and execute safe synchronized mode switches alongside live video streams.
5. **Touchscreen Ergonomics**: Full touch optimization with 36px–44px targets, native direct touch flick/drag scrolling (`PanningMode="VerticalOnly"`), tactile pressed states (`IsPressed`), and a 14px thumb-resizable splitter handle.

---

## 2. System Requirements & Prerequisites
- **Operating System**: Windows 10 (Build 19041+) or Windows 11 (64-bit).
- **Network Hardware**: 10GbE / 1GbE network interface card (NIC) connected to the SDVoE switch fabric.
- **Runtime Dependencies**:
  - Microsoft .NET 8 Desktop Runtime (x64) installed or bundled.
  - Microsoft Edge WebView2 Evergreen Runtime (preinstalled on modern Windows 10/11 systems).
- **Server Infrastructure**:
  - BlueRiver Control Server (`controlserver.exe` v3.2+) running on the network.
  - BlueRiver AV Manager Web Server (typically running on port 8080, 3000, or 80).

---

## 3. Installation & Portability
AVAS Routing SW is designed for **100% portable zero-install operation**:
- No registry keys or machine-wide administrative permissions are required.
- All configuration settings are maintained in the local `appsettings.json` file.
- All WebView2 browser cache, cookies, and local storage are isolated in the local subfolder:
  ```
  .\WebView2_UserData
  ```
To run, simply double-click:
```
AvasRoutingApp.exe
```

---

## 4. User Interface Tour

```
+-----------------------------------------------------------------------------------------------+
| AVAS ROUTING SW [v1.8.0] | SDVoE Preview Controller              URL: http://localhost:80 [🔄] [⚙] |
+---------------------------------------------------------------------------------------+-------+
|                                                                                       | ◀     |
|                                                                                       | P     |
|                                                                                       | R     |
|                             Embedded BlueRiver AV Manager                             | E     |
|                                     (Web Canvas)                                      | V     |
|                                                                                       | I     |
|                                                                                       | E     |
|                                                                                       | W     |
+---------------------------------------------------------------------------------------+-------+
| Ready | Portable WebView2 UserData: .\WebView2_UserData            SDVoE Server: 127.0.0.1:8080|
+-----------------------------------------------------------------------------------------------+
```

### 4.1 Top Navigation Bar
- **App Title & Version Badge**: Displays the active application version (`v1.8.0`).
- **URL Indicator**: Displays the currently loaded BlueRiver AV Manager web address.
- **🔄 Reload Button**: Refreshes the embedded WebView2 browser canvas.
- **⚙ Settings Button**: Opens the Configuration & Network Settings modal dialog.

### 4.2 Main Embedded Web Canvas
- Renders the complete BlueRiver AV Manager interface.
- If the configured web URL is unreachable, an **Offline Fallback Banner** automatically appears at the top, offering instant "Retry" and "Settings..." options without crashing or freezing.

### 4.3 Collapsible Right-Side Sidebar
- **Toggle Strip**: A 28px vertical bar docked on the right side of the screen labeled `◀ PREVIEW`.
- **Minimized by Default**: On startup, the sidebar is completely collapsed to avoid occluding the web canvas and prevent unwanted network multicast traffic.
- **Top Segmented Tab Navigation**:
  - **`📺 Previews`**: Real-time video preview cards with live rolling FPS telemetry, IP/MAC footer, mode badges (`[SINGLE]` or `[DUAL]`), and the dynamic `[ Chip 0 | Chip 1 ]` selector.
  - **`🔗 Multi-Link`**: Dedicated multi-link management cards showing primary MAC, companion chip (`chip_1`) MAC, online/offline status pill (`ONLINE` green / `OFFLINE` red), and Single/Dual link mode toggle controls.
- **Collapsing the Sidebar**: Clicking `▶ CLOSE` immediately issues deterministic hardware stream stops (`address 0.0.0.0`), drops all IGMP multicast memberships, and halts background socket processing, freeing 100% of network preview bandwidth.

---

## 5. Mode-Aware Single Link vs. Dual Link Operation

In AVAS-223 / SDVoE deployments, transmitter endpoints operate in two fundamentally different hardware modes:

### 5.1 Dual Link Mode (`DUAL`, 10GBY2)
- **Hardware Architecture**: Two 10G optical links (Chip 0 / Major MAC and Chip 1 / Minor MAC) bond together to transport uncompressed 4K60 4:4:4 HDR video (~18 Gbps). The Cypress MCU auxiliary processor is physically hardwired to Chip 0's serial bus.
- **Preview Acquisition**: The application commands Chip 0 via an in-band MCU UART tunnel (`set rtp igmp <MCAST_IP>\r\n` + `set rtp ON\r\n`).
- **UDP Port**: Multicast RTP preview streams stream on **UDP port 5000**.
- **Automatic Fallback**: If the MCU UART is unacknowledged within the timeout window, the application automatically falls back to Semtech BlueRiver Stream 0 / Stream 1 commands.

### 5.2 Single Link Mode (`SINGLE`, Autonomous 10G)
- **Hardware Architecture**: Each AVP2000 chip operates as an independent 10G network node. Chip 1 has no physical UART bus to the MCU.
- **Preview Acquisition**: Bypasses MCU UART completely (preventing serial timeouts) and directly commands the Semtech BlueRiver Stream 1 Hardware Scaler node (`set <MAC> stream HDMI 1 enable true address <MCAST_IP>`, `auto_stop: true`, `remove_audio: true`).
- **UDP Port**: Multicast RTP preview streams stream on **UDP port 5004**.

### 5.3 Using the `[ Chip 0 | Chip 1 ]` Selector
For Single Link encoders equipped with a companion chip:
1. Preview cards display an interactive segmented selector pill: `[ Chip 0 | Chip 1 ]` in the card header.
2. **Default Selection**: `Chip 0` (Primary MAC).
3. **Switching to Chip 1**:
   - Click `Chip 1`.
   - The application instantly executes deterministic teardown on Chip 0 (`address 0.0.0.0`).
   - The application commands Chip 1 (`CompanionMac`) to seed its Stream 1 Scaler.
   - The card's video surface binds seamlessly to the new stream on UDP port 5004.
4. **Switching back to Chip 0**:
   - Click `Chip 0`.
   - Chip 1 is safely stopped and Chip 0 resumes streaming.
   - Prevents desynchronized RTP timestamps and duplicate multicast subscriptions.

---

## 6. Concurrent Dual-Port Demux Receiver & Deterministic Teardown

### 6.1 Concurrent Dual-Port Ingestion (`UnifiedRtpDemuxReceiver`)
- Automatically binds to both **UDP port 5000** (Dual Link) and **UDP port 5004** (Single Link) simultaneously upon sidebar expansion.
- Sockets are configured with `ReuseAddress = true` and 8MB receive buffers (`ReceiveBufferSize = 8 * 1024 * 1024`).
- **Strict Source-IP Whitelisting (REA Report 06)**: Every incoming UDP packet is verified against the table of active registered transmitter source IPs. Rogue or unmonitored network traffic is dropped immediately at the socket boundary before buffer allocations or RFC 4175 parsing occurs.
- **Multi-Adapter Multicast Joins**: IGMP multicast memberships are joined across the designated local NIC adapter first, with automatic sweep across all active network adapters to guarantee packet reception in complex multi-NIC topologies.

### 6.2 Deterministic Double-Action Teardown
When switching chips, changing modes, or collapsing the sidebar:
1. **Control Plane Nullification**: Issues `set <MAC> stream HDMI 1 enable false address 0.0.0.0` and `set <MAC> stream HDMI 0 enable false address 0.0.0.0`, along with `send <MAC> rs232:1 set rtp OFF` and `stop <MAC>:thumbnail:0 free`.
   > Specifying `address 0.0.0.0` commands the AVP packetizer FIFO to immediately halt emitting datagrams, preventing lingering multicast flood on the network switch.
2. **Media Plane Cleanup**: Drops IGMP multicast group subscriptions on the host NIC, flushes scanline reassembly buffers, and closes active demux channels.

---

## 7. Configuration & Network Settings

To open the configuration dialog, click the **⚙ Settings** button in the top toolbar:

| Setting Field | Default Value | Description |
| :--- | :--- | :--- |
| **Theme** | `Light Mode` | User interface visual theme (Light or Dark). |
| **BlueRiver URL** | `http://localhost:80` | The web URL where BlueRiver AV Manager is hosted. |
| **Control Server Host** | `127.0.0.1` | The IP address or hostname of the SDVoE Control Server. |
| **REST API Port** | `8080` | The HTTP REST API port configured in `controlserver.conf` (default 8080). |
| **Telnet TCP Port** | `6970` | Fixed standard SDVoE CLI command port (default `6970`). |
| **Start Multicast IP** | `225.0.0.1` | Starting boundary of the dynamic multicast allocation pool. |
| **End Multicast IP** | `225.0.1.254` | Ending boundary of the dynamic multicast allocation pool. |
| **Multicast Base Port** | `5000` | Base UDP port assigned to preview multicast datagrams. |
| **Local Interface IP** | *(Empty)* | IP address of the local network adapter attached to the SDVoE network (leave empty for auto-detection). |

*Note: All settings are validated before saving. If any parameter is invalid (e.g. invalid IP address format or start IP > end IP), a red warning banner identifies the correction needed.*

---

## 8. Device Discovery & Strict Filtering
The software communicates with the SDVoE Control Server and enforces an authoritative hardware filter:
- **Vendor ID**: `105` (Advantech)
- **Product ID**: `81` (AVAS-223 series)
- **Direction**: `IsTransmitter == true`
- **Chip Index**: `chip_0` strictly accepted.
- **Excluded Devices**: Secondary link aggregation chips (`chip_1`), receivers (`RX`), and third-party endpoints are automatically omitted from the preview sidebar to prevent channel duplication and bandwidth waste.

---

## 9. Preview Video Processing & Performance
- **Protocol**: IETF RFC 3550 (RTP) + RFC 4175 (Uncompressed Video Payload).
- **Pixel Color-Space**: YUV 4:2:2 rasterization converted via high-speed Q10 fixed-point arithmetic into 24-bit RGB.
- **Rendering Architecture**: Double-buffered WPF `WriteableBitmap` utilizing direct back-buffer memory locking with zero GDI object leakage.
- **Live Frame Rate Telemetry**: Each preview card features a real-time rolling FPS counter with color-coded health indicators:
  - 🟢 **Green ($\ge 1.0$ FPS)**: Normal live streaming.
  - 🟠 **Amber ($< 1.0$ FPS)**: Low frame rate or stream negotiation in progress.

---

## 10. Troubleshooting & FAQ

**Q1: The web view shows an offline warning.**
- *Solution*: Verify that the BlueRiver AV Manager server is running. Open **⚙ Settings** and confirm the URL (e.g. `http://localhost:80` or `http://192.168.1.50:80`). Click **Retry** once the server is accessible.

**Q2: No preview cards appear when I expand the preview sidebar.**
- *Solution*: Check that the SDVoE Control Server IP and REST port in Settings match your `controlserver.conf` file. Click **🔄 Discover** in the preview drawer to trigger a fresh scan.

**Q3: Preview frames are black or connecting indefinitely.**
- *Solution*: Ensure your PC's network adapter is physically connected to the SDVoE multicast VLAN and that IGMP snooping is enabled on the network switch. If your PC has multiple network cards, specify the exact SDVoE NIC IP in **Local Network Interface** in Settings.

**Q4: In Single Link mode, Chip 1 does not stream.**
- *Solution*: Confirm that the companion chip is connected to the 10G switch fabric. Click `[ Chip 1 ]` in the preview card header. Single Link mode will command the Stream 1 Scaler directly on port 5004 without requiring MCU UART.
