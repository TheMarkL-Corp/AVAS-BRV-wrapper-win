# Project: AVAS Routing SW (AVAS-blueriver-wrapper-winform) — v1.8.0

> **PRIMARY DEVELOPMENT DIRECTORY**: `AVAS-blueriver-wrapper-winform`  
> All source code, build scripts, test suites, documentation, and release packages reside and must be developed within this directory.  
> **GitHub Repository**: [https://github.com/TheMarkL-Corp/AVAS-BRV-wrapper-win](https://github.com/TheMarkL-Corp/AVAS-BRV-wrapper-win)

## Architecture
AVAS Routing SW is a high-performance portable native Windows desktop application built on C# .NET 8 (`net8.0-windows` WPF) that embeds the BlueRiver AV Manager web interface using Microsoft WebView2, equipped with an expandable right-side native overlay sidebar providing live video previews (>= 1 FPS) for all discovered AVAS-223 encoders (TX, chip_0 only: Vendor ID 105, Product ID 81), supporting mode-aware Dual Link and Single Link preview streaming pipelines.

```
+---------------------------------------------------------------------------------------+
| AVAS Routing SW Window                                                                |
| +---------------------------------------------------------+ +----+ +----------------+ |
| | Microsoft WebView2 Browser                             | |    | | Native Overlay | |
| | (Embeds BlueRiver AV Manager Web UI)                    | | T  | | Sidebar Panel  | |
| | Isolated portable UserDataFolder: .\WebView2_UserData   | | O  | | (Collapsed on  | |
| | Configurable URL: e.g. http://localhost:80              | | G  | |  Startup)      | |
| |                                                         | | G  | |                | |
| |                                                         | | L  | | [Preview Card] | |
| |                                                         | | E  | | Live Video     | |
| |                                                         | |    | | [Chip 0|Chip 1]| |
| |                                                         | | S  | | MAC, IP, FPS   | |
| |                                                         | | T  | |                | |
| |                                                         | | R  | | [Preview Card] | |
| |                                                         | | I  | | Live Video     | |
| |                                                         | | P  | | Dual Link      | |
| +---------------------------------------------------------+ +----+ +----------------+ |
+---------------------------------------------------------------------------------------+
                                  │                                   │
                                  ▼                                   ▼
                   +-----------------------------+     +-------------------------------+
                   | SDVoE Discovery & Client    |     | Unified RTP Demux Receiver    |
                   | - Telnet Client (Port 6970) |     | - Dual Ports: UDP 5000 & 5004 |
                   |   require api 3.0.0.0       |     |   ReuseAddress=true, 8MB Buf  |
                   | - REST Client (Port 8080)   |     | - Strict Source-IP Whitelist  |
                   | - Strict AVAS-223 Filter    |     | - RFC 3550 + RFC 4175 Parser  |
                   |   (VID:105, PID:81, chip_0) |     | - Direct Slotting Reassembler |
                   | - Mode-Aware Preview Stream |     | - Q10 Fixed-Point Rasterizer  |
                   |   Dual: UART 5000 / Single: |     | - Zero-Leak WriteableBitmap   |
                   |   Stream 1 Scaler 5004      |     +-------------------------------+
                   +-----------------------------+                    │
                                  │                                   │
                                  ▼                                   ▼
                   +-------------------------------------------------------------------+
                   | Multicast Allocation Manager                                      |
                   | - Pool: 225.0.0.1 - 225.0.1.254 (510 Class D Multicast IPs)       |
                   | - Ports: 5000 (Dual Link), 5004 (Single Link Stream 1 Scaler)     |
                   | - Deterministic Teardown via address 0.0.0.0 & IGMP Multicast Drop|
                   +-------------------------------------------------------------------+
```

### Mode-Aware Dual-Path Preview Architecture (v1.8.0)
Based on reverse-engineered blueprints from `SingleModeSDVoETester` (`SingeLink_REA_Report`) and `Dual Link SDVoE Tester` (`REA_Reports`), v1.8.0 implements topology-conscious streaming:

1. **Dual Link Preview Pipeline (`LinkMode == "DUAL"`, 10GBY2)**:
   - Primary 10G link (Chip 0 / Major MAC) and secondary 10G link (Chip 1 / Minor MAC) bond to transmit uncompressed 4K60 4:4:4 HDR video (~18 Gbps).
   - Cypress MCU auxiliary processor is physically hardwired to Chip 0's serial bus.
   - Stream Acquisition: Dispatched via Chip 0 master + MCU UART in-band tunnel (`send <MAC> rs232:1 set rtp igmp <MCAST_IP>\r\n` + `send <MAC> rs232:1 set rtp ON\r\n`) on UDP port 5000.
   - Automatic Fallback: If MCU UART is unresponsive or unacknowledged within the timeout window, automatically falls back to Semtech BlueRiver Stream API (`set <MAC> stream HDMI 0 enable true address <MCAST_IP>`).

2. **Single Link Preview Pipeline (`LinkMode == "SINGLE"`, Autonomous 10G)**:
   - Each AVP2000 chip operates as an independent 10G endpoint. Chip 1 has no physical UART bus to the MCU; commanding MCU UART on Chip 1 causes silent timeouts.
   - Stream Acquisition: Commands Semtech BlueRiver Stream 1 Hardware Scaler node directly on the selected chip (`set <MAC> stream HDMI 1 enable true address <MCAST_IP>`, `auto_stop: true`, `remove_audio: true`) on UDP port 5004.
   - Bypasses MCU UART completely, eliminating UART serial latency and timeouts.

3. **Deterministic Double-Action Teardown**:
   - Control Plane Nullification: Explicitly issues `set <MAC> stream HDMI 1 enable false address 0.0.0.0` and `set <MAC> stream HDMI 0 enable false address 0.0.0.0`, followed by `send <MAC> rs232:1 set rtp OFF` and `stop <MAC>:thumbnail:0 free`. Nullifying the target address to `0.0.0.0` with `enable false` commands the AVP packetizer FIFO to immediately halt emitting datagrams.
   - Media Plane Disposal: Drops host IGMP multicast group membership on the NIC, flushes `DemuxChannel` packet queues, and closes active listening channels.

4. **Concurrent Dual-Port Ingestion (`UnifiedRtpDemuxReceiver`)**:
   - Listens simultaneously on **both UDP port 5000 and UDP port 5004** without operator port reconfiguration.
   - Sockets are bound with `ExclusiveAddressUse = false`, `SocketOptionName.ReuseAddress = true`, and 8MB receive buffers (`ReceiveBufferSize = 8 * 1024 * 1024`).
   - Strict Source-IP Whitelist Filtering (REA Report 06): Incoming packets on either port are matched against active registered transmitter IPs; unmonitored or rogue traffic is discarded at socket entry before memory allocation or RFC 4175 parsing.
   - Multi-adapter IGMP sweeps ensure multicast joins succeed even in complex multi-NIC topologies.

5. **Single-Link Chip 0 / Chip 1 UI Selection**:
   - Single Link preview cards with a valid companion chip display an interactive segmented toggle `[ Chip 0 | Chip 1 ]` in the card header.
   - Seamlessly switches preview between Chip 0 and Chip 1 by deterministically tearing down the old stream (`address 0.0.0.0`) before starting the new stream, preventing cross-stream packet collisions or orphan multicast subscriptions.

## Feature Inventory
| # | Feature | Description | Milestone | Source |
|---|---------|-------------|-----------|--------|
| 1 | R1.1 Portable Shell & App Structure | .NET 8 WPF application shell, self-contained/portable layout in target directory | M1 | ORIGINAL_REQUEST §R1 |
| 2 | R1.2 Microsoft WebView2 Embedding | Embeds BlueRiver AV Manager Web UI with isolated portable `WebView2_UserData` | M1 | ORIGINAL_REQUEST §R1 |
| 3 | R1.3 Configuration & Persistence | Strongly-typed JSON configuration (`appsettings.json`) with settings dialog & atomic persistence | M1 | ORIGINAL_REQUEST §R1 |
| 4 | R2.1 SDVoE Control Server Client | Telnet (6970, `require api 3.0.0.0`) & REST API (8080/9200) client | M2 | ORIGINAL_REQUEST §R2 |
| 5 | R2.2 AVAS-223 Device Filtering | Filter endpoints for Vendor 105, Product 81, chip_0 only (strictly exclude chip_1) | M2 | ORIGINAL_REQUEST §R2 |
| 6 | R2.3 Device Telemetry Tracking | Extract & maintain Device Name, MAC, IP, Model, Streaming State | M2 | ORIGINAL_REQUEST §R2 |
| 7 | R3.1 Dynamic Multicast IP Allocator | Unique allocation from 224.1.1.1–224.1.3.225 (port 6792), excluding reserved 224.1.1.253/254 | M3 | ORIGINAL_REQUEST §R3 |
| 8 | R3.2 SDVoE Preview Stream Control | Start/stop thumbnail generator (`set <mac> thumbnail...`, `start ...`, `stop ... free`) | M3 | ORIGINAL_REQUEST §R3 |
| 9 | R4.1 Collapsible Native Overlay Sidebar | Right-docked sidebar, collapsed by default, 28px toggle strip, zero airspace occlusion | M4 | ORIGINAL_REQUEST §R4 |
| 10 | R4.2 Preview Cards & Telemetry UI | Scrollable card list with video viewport, MAC, IP:port, resolution, live rolling FPS | M4 | ORIGINAL_REQUEST §R4 |
| 11 | R4.3 Lifecycle & Teardown on Collapse | Instant teardown on collapse: drop multicast groups, dispose sockets, free IPs, halt streams | M4 | ORIGINAL_REQUEST §R4 |
| 12 | R5.1 UDP Multicast Socket Listener | Asynchronous UDP socket bound to local network interface, joining assigned multicast groups | M5 | ORIGINAL_REQUEST §R5 |
| 13 | R5.2 RFC 3550 & RFC 4175 Ingestion | 20-byte header validation, marker detection, direct-slotting scanline reassembly | M5 | ORIGINAL_REQUEST §R5 |
| 14 | R5.3 YUV422 to RGB24 Rasterization | Q10 fixed-point fast conversion for `[U, Y0, V, Y1]` to RGB24 with 1024-byte clamp LUT | M5 | ORIGINAL_REQUEST §R5 |
| 15 | R5.4 Zero-Leak WPF Rendering | Direct `WriteableBitmap.BackBuffer` updating at >= 1 FPS without GDI or memory leaks | M5 | ORIGINAL_REQUEST §R5 |
| 16 | E2E.1 Automated Test Suite & Packaging | Full end-to-end testing (Tiers 1-5) and standalone portable package verification | M6 | ORIGINAL_REQUEST Acceptance |
| 17 | R6.1 AVAS-223 Multi-Link Management | Check Single/Dual-link mode, companion online/offline status pill, mode toggle with non-blocking 20s countdown, automatic stream pause/reconnect | M7 | V1.1.0_GOAL |
| 18 | R7.1 Device Temperature & Telemetry Monitoring | TX & RX temperature cards, dual-source SDVoE vs VoIP SDK discrepancy engine, companion chip_0 & chip_1 combining, >70°C bold red alerts, active-tab auto-polling | M8 | V1.4.0_GOAL |
| 19 | R8.1 Sidebar Font Size Adjustment & Scaling | Discrete preset selector (Small, Normal, Large, ExtraLarge), live preview in Settings dialog, layout transform scaling, dynamic width adaptation | M9 | V1.4.0_GOAL |
| 20 | R9.1 Unified Multicast Demuxer & Multi-Link Reliability | Adv_VOIPS_Sample aligned single port-5000 UDP socket with source IP demuxing, tolerant scanline reassembly with packet loss interpolation, chip_0 exclusive multi-link targeting with REST auto-probing, shifted 225.1.1.x multicast pool | M10 | V1.5.0_GOAL |
| 21 | R10.1 Universal AVAS Diagnostics (v1.6.0) | Dedicated sidebar tab for AVAS Diagnostics (TX & RX), in-dialog target switcher, graceful multicast stream teardown on exit | M11 | V1.6.0_GOAL |
| 22 | R11.1 Modernized Preview Pipeline & Source-IP Filtering (v1.7.0) | Early source-IP whitelist filtering on UDP 5000 (REA Reports 01 & 06) dropping unmonitored packets before buffer allocation; dual-path acquisition (UART `set rtp igmp` vs BlueRiver thumbnail API) | M12 | REA_Reports §01,06 |
| 23 | R11.2 Minimalist Single/Dual Link Preview Cards (v1.7.0) | Stripped "Diag" button, "chip_0", and activation badges; explicit Single Link (`#475569`) or Dual Link (`#2563EB`) badges; clean 2-line footer (IP, MAC + copy), live FPS pill | M12 | USER_REQUEST |
| 24 | R11.3 Technician PIN-Protected Advanced Settings (v1.7.0) | Default 2-tab operator mode (`Previews`, `Multi-Link`); Thermals & Diagnostics secured behind 8-digit PIN `00000000`, revealed in dynamic `[ ⚙ Advanced ]` tab with session relock | M12 | USER_REQUEST |
| 25 | R11.4 Bifurcated Hardware Reset & Reboot Subsystems (v1.7.0) | Dedicated MCU soft reboot (`set system reboot\r\n`) and factory reset (`set factory default\r\n`) with trailing `\r\n` and REST fallback; hardened AVP PHY reboot and factory default (REA Report 03) | M12 | REA_Reports §03 |
| 26 | R11.5 Purged VEGA-1100 Support (v1.7.0) | Complete removal of VEGA-1100 discovery, preview streaming, config options, and tests; 100% focused on AVAS-223 encoders | M12 | USER_REQUEST |
| 27 | R11.6 Modern Default Network Configuration (v1.7.0+) | Default multicast pool updated to `225.0.0.1` - `225.0.1.254` (510 addresses, Class D validated) and default REST port updated to `8080`, with automatic legacy migration in `ConfigService` | M12 | USER_REQUEST |
| 28 | R12.1 Mode-Aware Dual-Path Preview Pipeline (v1.8.0) | Dual Link (MCU UART on port 5000 with BlueRiver fallback) vs Single Link (Stream 1 Scaler on port 5004 with `auto_stop: true`), concurrent dual-port demux receiver (5000 & 5004) with `ReuseAddress = true` and 8MB buffers, deterministic `0.0.0.0` teardown, dynamic `[ Chip 0 \| Chip 1 ]` card toggle | M13 | REA_Reports §01,06 & SingeLink_REA_Report |

## Milestones
| # | Name | Scope | Dependencies | Status |
|---|------|-------|-------------|--------|
| E2E | E2E Testing Track | Test harness, mock SDVoE server, RTP packet generator, Tiers 1-5 test suite | none | DONE |
| M1 | Shell, WebView2 & Config | WPF shell, portable WebView2 embedding, JSON configuration model & dialog | none | DONE |
| M2 | SDVoE Client & AVAS Filter | Telnet/REST discovery client, AVAS-223 chip_0 filter, telemetry models | M1 | DONE |
| M3 | Multicast Allocator & Control | Multicast pool manager (224.1.1.1-224.1.3.225), preview start/stop commands | M2 | DONE |
| M4 | Native Overlay Sidebar UI | Expandable sidebar, collapsed default, preview cards list, teardown lifecycle | M1, M3 | DONE |
| M5 | RTP Ingestion & Rasterizer | Async UDP multicast listener, RFC 3550/4175 parser, Q10 YUV422 rasterizer, WriteableBitmap | M4 | DONE |
| M6 | Final Verification & Release | Run 388 tests (100% pass), 4,005 loop stress runs, GitHub release v1.0.0 | E2E, M5 | DONE (v1.0.0 Released) |
| M7 | Multi-Link Management (v1.1.0) | Top segmented switcher tab, companion telemetry, mode toggle, safe reboot & preview co-existence | M4, M6 | DONE (v1.1.0 Ready) |
| M8 | Device Temperature Monitoring (v1.4.0) | Sidebar Thermals tab, TX/RX grouping, chip_0 & chip_1 companion pairing, SDVoE vs VoIP SDK discrepancy testing, >70°C alert styling | M4, M7 | DONE (v1.4.0 Released) |
| M9 | Sidebar Font Size Adjustment (v1.4.0) | Settings typography presets, live preview, layout transform scaling, dynamic width adaptation | M8 | DONE (v1.4.0 Released) |
| M10 | Unified Multicast Demux & Multi-Link Fixes (v1.5.0) | Port 5000 source-IP demuxer, tolerant scanline interpolation, chip_0 exclusive multi-link targeting, auto-probing REST ports, 225.1.1.x pool, 462 tests passed | M7, M9 | DONE (v1.5.0 Released) |
| M11 | Universal Diagnostics & VEGA-1100 Support (v1.6.0) | Dedicated Diagnostics sidebar tab (TX/RX), in-dialog target device switcher, VEGA-1100 direct SDVoE streaming, OneWay binding hardening, 544 tests passed | M10 | DONE (v1.6.0 Released) |
| M12 | Modernized Pipeline, Advanced PIN Lock & Defaults (v1.7.0) | Early port-5000 source-IP filtering, minimalist preview cards, PIN `00000000` Advanced tab, hardened MCU/PHY reset, purged VEGA, 225.0.0.1-225.0.1.254 & 8080 defaults, 565 tests passed | M11 | DONE (v1.7.0 Released) |
| M13 | Mode-Aware Single & Dual Link Preview Pipeline (v1.8.0) | Dual-path stream dispatch (port 5000 UART / 5004 Scaler), concurrent dual-port demuxer, deterministic `address 0.0.0.0` teardown, dynamic `[ Chip 0 \| Chip 1 ]` toggle pill, 580 tests passed | M12 | DONE (v1.8.0 Ready) |

## Interface Contracts

### Configuration Model (`IConfigService`)
```csharp
public class AppConfig
{
    public string BlueRiverUrl { get; set; } = "http://localhost:80";
    public string ControlServerIp { get; set; } = "127.0.0.1";
    public int RestPort { get; set; } = 8080;
    public int TelnetPort { get; set; } = 6970;
    public string MulticastStartIp { get; set; } = "225.0.0.1";
    public string MulticastEndIp { get; set; } = "225.0.1.254";
    public int BasePort { get; set; } = 5000;
    public string LocalNetworkInterfaceIp { get; set; } = "";
    public string Theme { get; set; } = "Light";
    public double SidebarWidth { get; set; } = 400.0;
    public string SidebarFontSize { get; set; } = "Normal";
}

public interface IConfigService
{
    AppConfig Current { get; }
    void Save(AppConfig config);
    event Action<AppConfig>? ConfigChanged;
}
```

### Device Discovery & Filtering (`ISdvoeDiscoveryService`)
```csharp
public class AvasDevice
{
    public string MacAddress { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public string Model { get; set; } = "AVAS-223";
    public int VendorId { get; set; } = 105;
    public int ProductId { get; set; } = 81;
    public int ChipIndex { get; set; } = 0;
    public bool IsTransmitter { get; set; } = true;
    public string LinkMode { get; set; } = "UNKNOWN"; // "SINGLE" or "DUAL"
    public string CompanionMac { get; set; } = "";
    public int SelectedPreviewChipIndex { get; set; } = 0;
    public string AllocatedMulticastIp { get; set; } = "";
    public int AllocatedPort { get; set; } = 5000;
    public bool IsStreaming { get; set; } = false;
    public double CurrentFps { get; set; } = 0.0;
    public string Resolution { get; set; } = "320x180";
}

public interface ISdvoeDiscoveryService
{
    Task<IReadOnlyList<AvasDevice>> DiscoverAvas223DevicesAsync(CancellationToken ct = default);
    event Action<IReadOnlyList<AvasDevice>>? DevicesDiscovered;
}
```

### Multicast Allocation & Stream Control (`IMulticastController`)
```csharp
public interface IMulticastController
{
    string? AllocateMulticastIp(string macAddress);
    void ReleaseMulticastIp(string macAddress);
    
    // Mode-aware stream dispatch & deterministic teardown
    Task<bool> StartPreviewStreamAsync(
        string macAddress, 
        string multicastIp, 
        int port, 
        string linkMode = "UNKNOWN", 
        int chipIndex = 0, 
        CancellationToken ct = default);

    Task<bool> StopPreviewStreamAsync(
        string macAddress, 
        bool free = true, 
        string linkMode = "UNKNOWN", 
        int chipIndex = 0, 
        CancellationToken ct = default);

    // Hardware maintenance & diagnostics contracts
    Task<bool> RebootChipAsync(AvasDevice device, string targetMac, CancellationToken ct = default);
    Task<bool> RebootMcuAsync(AvasDevice device, CancellationToken ct = default);
    Task<bool> FactoryResetAvpAsync(AvasDevice device, string targetMac, CancellationToken ct = default);
    Task<bool> FactoryResetMcuAsync(AvasDevice device, CancellationToken ct = default);
}
```

### RTP Stream Ingestion & Rasterization (`IRtpStreamReceiver` & `UnifiedRtpDemuxReceiver`)
```csharp
public interface IRtpStreamReceiver : IDisposable
{
    void StartListening(string multicastIp, int port, string localInterfaceIp);
    void StopListening();
    event Action<IntPtr, int, int, int>? FrameReady; // bufferPtr, width, height, stride
    event Action<double>? FpsUpdated;
}

public class UnifiedRtpDemuxReceiver : IDisposable
{
    public static readonly int[] DefaultPorts = new[] { 5000, 5004 };
    public IReadOnlyList<int> ListeningPorts { get; }
    public void EnsureStarted(IEnumerable<int>? ports = null);
    public void RegisterChannel(string sourceIp, string multicastIp, int port, Action<IntPtr, int, int, int> frameCallback, Action<double>? fpsCallback = null);
    public void UnregisterChannel(string sourceIp);
    public void StopAll();
}
```

## Code Layout
Target Application Root: `c:\Users\POC-615\Documents\GitHub\AVAS-BRV-wrapper-win`

```
AVAS-BRV-wrapper-win/
├── AvasRoutingApp.sln
├── src/
│   ├── AvasRoutingApp/
│   │   ├── AvasRoutingApp.csproj
│   │   ├── App.xaml / App.xaml.cs
│   │   ├── MainWindow.xaml / MainWindow.xaml.cs
│   │   ├── Configuration/
│   │   │   ├── AppConfig.cs
│   │   │   ├── ConfigService.cs
│   │   │   └── appsettings.json
│   │   ├── Sdvoe/
│   │   │   ├── SdvoeClient.cs
│   │   │   ├── SdvoeTelnetClient.cs
│   │   │   ├── SdvoeRestClient.cs
│   │   │   ├── AvasDevice.cs
│   │   │   ├── MulticastIpManager.cs
│   │   │   ├── MultiLinkService.cs
│   │   │   └── DeviceTemperatureInfo.cs
│   │   ├── Rtp/
│   │   │   ├── RtpHeader.cs
│   │   │   ├── RtpPacketParser.cs
│   │   │   ├── ScanlineFrameReassembler.cs
│   │   │   ├── Yuv422Rasterizer.cs
│   │   │   ├── UnifiedRtpDemuxReceiver.cs
│   │   │   └── RtpMulticastReceiver.cs
│   │   ├── ViewModels/
│   │   │   ├── MainViewModel.cs
│   │   │   ├── EncoderCardViewModel.cs
│   │   │   ├── MultiLinkViewModel.cs
│   │   │   ├── MaintenanceViewModel.cs
│   │   │   └── SettingsViewModel.cs
│   │   ├── Views/
│   │   │   ├── OverlaySidebarView.xaml / .cs
│   │   │   ├── EncoderCardView.xaml / .cs
│   │   │   ├── MaintenanceDialog.xaml / .cs
│   │   │   └── SettingsDialog.xaml / .cs
│   │   └── Assets/
│   │       └── icon.ico
│   └── AvasRoutingSetup/
│       ├── AvasRoutingSetup.csproj
│       └── ...
└── tests/
    ├── AvasRoutingApp.Tests/
    │   ├── AvasRoutingApp.Tests.csproj
    │   ├── AdvancedSettingsLockTests.cs
    │   ├── AvasDiagnosticsCommandTests.cs
    │   ├── ConfigTests.cs
    │   ├── DeviceFilteringTests.cs
    │   ├── EncoderCardViewModelTests.cs
    │   ├── MulticastAllocationTests.cs
    │   ├── MultiLinkViewModelTests.cs
    │   ├── NextGenArchitectureTests.cs
    │   ├── RtpParserTests.cs
    │   ├── SidebarTests.cs
    │   └── YuvRasterizerTests.cs
    └── E2ETests/
        ├── E2ETests.csproj
        ├── Harness/
        ├── Mocks/
        │   ├── MockSdvoeServer.cs
        │   └── MockRtpStreamer.cs
        └── Suites/
            ├── SingleAndDualLinkPreviewE2ETests.cs
            ├── Tier1_FeatureCoverageTests.cs
            ├── Tier2_BoundaryCornerTests.cs
            ├── Tier3_CrossFeatureTests.cs
            └── Tier4_ApplicationScenarioTests.cs
```
