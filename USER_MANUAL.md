# AVAS Routing SW — User Manual (v1.0.0)

## 1. Product Introduction
**AVAS Routing SW** is a high-performance native Windows desktop application designed to control and monitor SDVoE AV distribution networks. It serves as a dual-capability environment:
1. **Embedded Web Management**: Wraps and renders Semtech's **BlueRiver AV Manager** web interface directly inside a high-speed Microsoft WebView2 Chromium container.
2. **Real-Time Multicast Preview Overlay**: Provides a floating, collapsible native sidebar delivering live uncompressed video previews ($\ge 1$ FPS) for connected **Advantech AVAS-223 encoders (`chip_0`)**.

---

## 2. System Requirements & Prerequisites
- **Operating System**: Windows 10 (Build 19041+) or Windows 11 (64-bit).
- **Network Hardware**: 10GbE / 1GbE network interface card (NIC) connected to the SDVoE switch fabric.
- **Runtime Dependencies**:
  - Microsoft .NET 8 Desktop Runtime (x64) installed or bundled.
  - Microsoft Edge WebView2 Evergreen Runtime (preinstalled on modern Windows 10/11 systems).
- **Server Infrastructure**:
  - BlueRiver Control Server (`controlserver.exe` v3.2+) running on the network.
  - BlueRiver AV Manager Web Server (typically running on port 3000 or 80).

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
| AVAS ROUTING SW [v1.0.0] | SDVoE Preview Controller            URL: http://localhost:3000 [🔄] [⚙] |
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
- **App Title & Version Badge**: Displays the active application version (`v1.0.0`).
- **URL Indicator**: Displays the currently loaded BlueRiver AV Manager web address.
- **🔄 Reload Button**: Refreshes the embedded WebView2 browser canvas.
- **⚙ Settings Button**: Opens the Configuration & Network Settings modal dialog.

### 4.2 Main Embedded Web Canvas
- Renders the complete BlueRiver AV Manager interface.
- If the configured web URL is unreachable, an **Offline Fallback Banner** automatically appears at the top, offering instant "Retry" and "Settings..." options without crashing or freezing.

### 4.3 Collapsible Right-Side Preview Drawer
- **Toggle Strip**: A 28px vertical bar docked on the right side of the screen labeled `◀ PREVIEW`.
- **Minimized by Default**: On startup, the preview drawer is completely collapsed to avoid occluding the web canvas and prevent unwanted network multicast traffic.
- **Expanding the Drawer**: Clicking the toggle strip slides out a 380px preview pane, queries the SDVoE Control Server, allocates unique multicast channels, and initiates live video streams.
- **Collapsing the Drawer**: Clicking `▶ CLOSE` immediately drops all IGMP multicast memberships and halts background stream processing, freeing 100% of network preview bandwidth.

---

## 5. Configuration & Network Settings

To open the configuration dialog, click the **⚙ Settings** button in the top toolbar:

| Setting Field | Default Value | Description |
| :--- | :--- | :--- |
| **BlueRiver URL** | `http://localhost:3000` | The web URL where BlueRiver AV Manager is hosted. |
| **Control Server Host** | `127.0.0.1` | The IP address or hostname of the SDVoE Control Server. |
| **REST API Port** | `8080` | The HTTP REST API port configured in `controlserver.conf` (e.g. 8080, 9200, 80). |
| **Telnet TCP Port** | `6970` | Fixed standard SDVoE CLI command port (default `6970`). |
| **Start Multicast IP** | `224.1.1.1` | Starting boundary of the dynamic multicast allocation pool. |
| **End Multicast IP** | `224.1.3.225` | Ending boundary of the dynamic multicast allocation pool. |
| **Multicast Base Port** | `6792` | Base UDP port assigned to preview multicast datagrams. |
| **Local Interface IP** | *(Empty)* | IP address of the local network adapter attached to the SDVoE network (leave empty for auto-detection). |

*Note: All settings are validated before saving. If any parameter is invalid (e.g. invalid IP address format or start IP > end IP), a red warning banner identifies the correction needed.*

---

## 6. Device Discovery & Strict Filtering
The software communicates with the SDVoE Control Server and enforces an authoritative hardware filter:
- **Vendor ID**: `105` (Advantech)
- **Product ID**: `81` (AVAS-223 series)
- **Direction**: `IsTransmitter == true`
- **Chip Index**: `chip_0` strictly accepted.
- **Excluded Devices**: Secondary link aggregation chips (`chip_1`), receivers (`RX`), and third-party endpoints are automatically omitted from the preview sidebar to prevent channel duplication and bandwidth waste.

---

## 7. Preview Video Processing & Performance
- **Protocol**: IETF RFC 3550 (RTP) + RFC 4175 (Uncompressed Video Payload).
- **Pixel Color-Space**: YUV 4:2:2 rasterization converted via high-speed Q10 fixed-point arithmetic into 24-bit RGB.
- **Rendering Architecture**: Double-buffered WPF `WriteableBitmap` utilizing direct back-buffer memory locking with zero GDI object leakage.
- **Live Frame Rate Telemetry**: Each preview card features a real-time rolling FPS counter with color-coded health indicators:
  - 🟢 **Green ($\ge 1.0$ FPS)**: Normal live streaming.
  - 🟠 **Amber ($< 1.0$ FPS)**: Low frame rate or stream negotiation in progress.

---

## 8. Troubleshooting & FAQ

**Q1: The web view shows an offline warning.**
- *Solution*: Verify that the BlueRiver AV Manager server is running. Open **⚙ Settings** and confirm the URL (e.g. `http://localhost:3000` or `http://192.168.1.50:80`). Click **Retry** once the server is accessible.

**Q2: No preview cards appear when I expand the preview sidebar.**
- *Solution*: Check that the SDVoE Control Server IP and REST port in Settings match your `controlserver.conf` file. Click **🔄 Discover** in the preview drawer to trigger a fresh scan.

**Q3: Preview frames are black or connecting indefinitely.**
- *Solution*: Ensure your PC's network adapter is physically connected to the SDVoE multicast VLAN and that IGMP snooping is enabled on the network switch. If your PC has multiple network cards, specify the exact SDVoE NIC IP in **Local Network Interface** in Settings.
