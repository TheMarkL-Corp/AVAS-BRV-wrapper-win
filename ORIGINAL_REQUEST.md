# Original User Request

## 2026-09-07T11:26:04Z

Develop a portable native Windows application ("AVAS Routing SW") that embeds the BlueRiver AV Manager web interface using Microsoft WebView2, equipped with an expandable right-side native overlay sidebar providing live video previews (>= 1 FPS) for all discovered AVAS-223 encoders (TX, chip_0 only: Vendor ID 105, Product ID 81). The application queries the SDVoE Control Server, assigns unique multicast IPs from a user-configured pool, starts/stops encoder preview multicasts via the SDVoE/BlueRiver APIs, and renders decoded uncompressed YUV422 RTP video streams directly onto the UI.

Working directory: d:\AVAS-SDVoE Related\Dual Link SDVoE TesterV1.16-20230824\Dual Link SDVoE TesterV1.16-20230824\BlueRiver AV Overlay Test App
Integrity mode: development

## Requirements

### R1. Native Windows Shell & Web Embedding (Portable App)
- Developed as a portable native Windows desktop application in C# .NET (WPF or Windows Forms) targeting .NET Framework or .NET 8/9 with self-contained / portable deployment.
- Integrates Microsoft WebView2 to render the BlueRiver AV Manager web UI as the primary workspace.
- Provides a configuration / settings panel (persisted to a local JSON/XML config file in the application directory) allowing the user to configure:
  - BlueRiver AV Manager URL (e.g. http://localhost:80 or user-defined IP/hostname)
  - SDVoE Control Server IP Address
  - SDVoE Control Server HTTP REST API Port (default: 8080, configurable)
  - Control Server Telnet / TCP Port (fixed: 6970)
  - Multicast IP Allocation Range: Start IP (default 224.1.1.1), End IP (default 224.1.3.225), and Base Port (default 6792).

### R2. SDVoE Device Discovery & AVAS-223 Filtering
- Connects to the SDVoE Control Server (via REST API on configured port and/or Telnet on port 6970) to discover all network endpoints.
- Filters discovered devices strictly for **AVAS-223 Encoders (TX)** by verifying:
  - Vendor ID == 105
  - Product ID == 81
  - Device Chip Index is chip_0 (strictly ignoring and excluding any chip_1 units).
- Displays device telemetry and metadata (Device Name, MAC Address, IP Address, Model, Streaming State) in the UI.

### R3. Dynamic Multicast Allocation & SDVoE Preview Control
- Implements an IP allocation manager that assigns each active chip_0 AVAS encoder a unique, non-conflicting Multicast IP and UDP Port from the configured pool (224.1.1.1–224.1.3.225, port 6792).
- Sends the proper SDVoE / BlueRiver commands to the Control Server / hardware encoder to set the multicast address/port and enable/disable thumbnail/preview streaming:
  - For BlueRiver Control Server: CLI commands set <mac> <port>, set <mac> <multicast_ip> or REST equivalent /api/devices/{mac}/thumbnail.
  - Cleanly de-allocates and disables streaming when the preview overlay is collapsed or closed.

### R4. Floating / Collapsible Native Preview Overlay
- Features a floating / collapsible native overlay panel docked to the right edge of the application window.
- On application launch, the preview overlay is minimized/collapsed by default to preserve network bandwidth and UI space.
- A prominent toggle button/handle allows the user to expand or collapse the sidebar at any time.
- **When expanded**:
  - Automatically begins UDP multicast listening for each detected chip_0 AVAS encoder.
  - Generates a scrollable grid/list of live preview cards showing the live stream for each encoder.
  - Displays encoder information (MAC, allocated Multicast IP:Port, resolution, and live FPS indicator).
- **When collapsed**:
  - Instantly drops all UDP multicast memberships, stops background worker threads, and commands encoders to stop transmitting preview streams to prevent network congestion.

### R5. RTP Multicast Stream Ingestion & Color-Space Rasterizer
- Native asynchronous UDP socket listener (System.Net.Sockets.UdpClient) bound to the local network interface and joining each encoder's assigned multicast group.
- Implements RFC 3550 RTP + RFC 4175 uncompressed video packet parsing:
  - Validates 20-byte packet headers (SequenceNumber, Timestamp, Marker, LineNo, Offset, Length).
  - Detects frame completion on packet with Marker == 1.
  - Reassembles scanlines into complete frames, gracefully handling lost packets.
- Implements optimized YUV 4:2:2 to RGB24 rasterization:
  - Quads: [U, Y0, V, Y1] to two RGB pixels using integer or SIMD arithmetic:
    R = Y + 1.4065(U - 128)
    G = Y - 0.3455(V - 128) - 0.7169(U - 128)
    B = Y + 1.7790(V - 128)
- Displays live video frames at a rate of at least 1 FPS (typically 1–5 FPS based on hardware transmission) smoothly without memory leaks or UI freezing.

## Acceptance Criteria

### Standalone Build & Portability
- [ ] The application compiles cleanly with all dependencies, SDKs, and assets placed within d:\AVAS-SDVoE Related\Dual Link SDVoE TesterV1.16-20230824\Dual Link SDVoE TesterV1.16-20230824\BlueRiver AV Overlay Test App.
- [ ] Application runs portably from its folder without requiring machine-wide installation.

### Configuration & Settings
- [ ] User can view and edit the BlueRiver AV Manager URL, SDVoE Control Server IP, HTTP REST API Port (default 8080), Telnet Port (6970), Multicast Start IP (224.1.1.1), End IP (224.1.3.225), and Base Port (6792).
- [ ] Settings persist across application restarts in a local configuration file.

### Device Discovery & Filtering
- [ ] Correctly queries the SDVoE Control Server and accurately identifies AVAS-223 units matching Vendor ID : 105 and Product ID : 81.
- [ ] Strictly filters and displays only chip_0 encoders, completely ignoring chip_1 units.

### Multicast Allocation & Streaming Control
- [ ] Assigns unique, distinct multicast IP addresses within the configured pool to each active chip_0 encoder.
- [ ] Issues proper SDVoE API / Telnet commands to activate and deactivate the thumbnail streaming channel.

### Visual Preview & Performance
- [ ] The preview sidebar is hidden/collapsed on application startup.
- [ ] Expanding the sidebar initiates multicast streaming and displays live preview cards for each encoder at >= 1 FPS.
- [ ] Collapsing the sidebar immediately ceases multicast reception and releases sockets and resources.
- [ ] No UI freezing, memory leaks, or GDI object buildup during extended streaming.
