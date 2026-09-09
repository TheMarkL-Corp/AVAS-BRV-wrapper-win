# Project: AVAS Routing SW (AVAS-blueriver-wrapper-winform)

> **PRIMARY DEVELOPMENT DIRECTORY**: `AVAS-blueriver-wrapper-winform`  
> All source code, build scripts, test suites, documentation, and release packages reside and must be developed within this directory.  
> **GitHub Repository**: [https://github.com/TheMarkL-Corp/AVAS-BRV-wrapper-win](https://github.com/TheMarkL-Corp/AVAS-BRV-wrapper-win)

## Architecture
AVAS Routing SW is a high-performance portable native Windows desktop application built on C# .NET 8 (`net8.0-windows` WPF) that embeds the BlueRiver AV Manager web interface using Microsoft WebView2, equipped with an expandable right-side native overlay sidebar providing live video previews (>= 1 FPS) for all discovered AVAS-223 encoders (TX, chip_0 only: Vendor ID 105, Product ID 81).

```
+---------------------------------------------------------------------------------------+
| AVAS Routing SW Window                                                                |
| +---------------------------------------------------------+ +----+ +----------------+ |
| | Microsoft WebView2 Browser                             | |    | | Native Overlay | |
| | (Embeds BlueRiver AV Manager Web UI)                    | | T  | | Sidebar Panel  | |
| | Isolated portable UserDataFolder: .\WebView2_UserData   | | O  | | (Collapsed on  | |
| | Configurable URL: e.g. http://localhost:3000            | | G  | |  Startup)      | |
| |                                                         | | G  | |                | |
| |                                                         | | L  | | [Preview Card] | |
| |                                                         | | E  | | Live Video     | |
| |                                                         | |    | | MAC, IP, FPS   | |
| |                                                         | | S  | |                | |
| |                                                         | | T  | | [Preview Card] | |
| |                                                         | | R  | | Live Video     | |
| |                                                         | | I  | | MAC, IP, FPS   | |
| |                                                         | | P  | |                | |
| +---------------------------------------------------------+ +----+ +----------------+ |
+---------------------------------------------------------------------------------------+
                                  │                                   │
                                  ▼                                   ▼
                   +-----------------------------+     +-------------------------------+
                   | SDVoE Discovery & Client    |     | Multicast RTP Ingestion Engine|
                   | - Telnet Client (Port 6970) |     | - UDP Multicast Sockets       |
                   |   require api 3.0.0.0       |     | - RFC 3550 + RFC 4175 Parser  |
                   | - REST Client (Port 8080)   |     | - Direct Slotting Reassembler |
                   | - Strict AVAS-223 Filter    |     | - Q10 Fixed-Point Rasterizer  |
                   |   (VID:105, PID:81, chip_0) |     | - Zero-Leak WriteableBitmap   |
                   +-----------------------------+     +-------------------------------+
                                  │                                   │
                                  ▼                                   ▼
                   +-------------------------------------------------------------------+
                   | Multicast Allocation Manager                                      |
                   | - Pool: 224.1.1.1 - 224.1.3.225, Port 6792                       |
                   | - Excludes: 224.1.1.253, 224.1.1.254, active streams              |
                   +-------------------------------------------------------------------+
```

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

## Interface Contracts

### Configuration Model (`IConfigService`)
```csharp
public class AppConfig
{
    public string BlueRiverUrl { get; set; } = "http://localhost:3000";
    public string ControlServerIp { get; set; } = "127.0.0.1";
    public int RestPort { get; set; } = 8080;
    public int TelnetPort { get; set; } = 6970;
    public string MulticastStartIp { get; set; } = "224.1.1.1";
    public string MulticastEndIp { get; set; } = "224.1.3.225";
    public int BasePort { get; set; } = 6792;
    public string LocalNetworkInterfaceIp { get; set; } = "";
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
    public string AllocatedMulticastIp { get; set; } = "";
    public int AllocatedPort { get; set; } = 6792;
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
    Task<bool> StartPreviewStreamAsync(string macAddress, string multicastIp, int port, CancellationToken ct = default);
    Task<bool> StopPreviewStreamAsync(string macAddress, CancellationToken ct = default);
}
```

### RTP Stream Ingestion & Rasterization (`IRtpStreamReceiver`)
```csharp
public interface IRtpStreamReceiver : IDisposable
{
    void StartListening(string multicastIp, int port, string localInterfaceIp);
    void StopListening();
    event Action<IntPtr, int, int, int>? FrameReady; // bufferPtr, width, height, stride
    event Action<double>? FpsUpdated;
}
```

## Code Layout
Target Application Root: `d:\AVAS-SDVoE Related\Dual Link SDVoE TesterV1.16-20230824\Dual Link SDVoE TesterV1.16-20230824\BlueRiver AV Overlay Test App`

```
BlueRiver AV Overlay Test App/
├── AvasRoutingApp.sln
├── src/
│   └── AvasRoutingApp/
│       ├── AvasRoutingApp.csproj
│       ├── App.xaml / App.xaml.cs
│       ├── MainWindow.xaml / MainWindow.xaml.cs
│       ├── Configuration/
│       │   ├── AppConfig.cs
│       │   ├── ConfigService.cs
│       │   └── appsettings.json
│       ├── Sdvoe/
│       │   ├── SdvoeClient.cs
│       │   ├── SdvoeTelnetClient.cs
│       │   ├── SdvoeRestClient.cs
│       │   ├── AvasDevice.cs
│       │   └── MulticastIpManager.cs
│       ├── Rtp/
│       │   ├── RtpHeader.cs
│       │   ├── RtpPacketParser.cs
│       │   ├── ScanlineFrameReassembler.cs
│       │   ├── Yuv422Rasterizer.cs
│       │   └── RtpMulticastReceiver.cs
│       ├── ViewModels/
│       │   ├── MainViewModel.cs
│       │   ├── EncoderCardViewModel.cs
│       │   └── SettingsViewModel.cs
│       ├── Views/
│       │   ├── OverlaySidebarView.xaml / .cs
│       │   ├── EncoderCardView.xaml / .cs
│       │   └── SettingsDialog.xaml / .cs
│       └── Assets/
│           └── icon.ico
└── tests/
    ├── AvasRoutingApp.Tests/
    │   ├── AvasRoutingApp.Tests.csproj
    │   ├── ConfigTests.cs
    │   ├── DeviceFilteringTests.cs
    │   ├── MulticastAllocationTests.cs
    │   ├── RtpParserTests.cs
    │   └── YuvRasterizerTests.cs
    └── E2ETests/
        ├── E2ETests.csproj
        ├── MockSdvoeServer.cs
        ├── MockRtpStreamer.cs
        ├── Tier1_FeatureCoverageTests.cs
        ├── Tier2_BoundaryCornerTests.cs
        ├── Tier3_CrossFeatureTests.cs
        └── Tier4_ApplicationScenarioTests.cs
```
