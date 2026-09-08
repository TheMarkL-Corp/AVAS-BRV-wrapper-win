# AVAS Routing SW — Operational Verification & Execution Guide

## Overview
**AVAS Routing SW** is a native portable Windows desktop application designed to wrap Semtech's **BlueRiver AV Manager** web interface while providing a high-performance, native collapsible preview sidebar for all connected **AVAS-223 encoders (`chip_0`)**.

All project deliverables, source code, tests, and build artifacts are self-contained within:
```
d:\AVAS-SDVoE Related\Dual Link SDVoE TesterV1.16-20230824\Dual Link SDVoE TesterV1.16-20230824\BlueRiver AV Overlay Test App
```

---

## 1. Project Deliverables Directory Structure

```
BlueRiver AV Overlay Test App/
├── AvasRoutingApp.sln                         # Solution file
├── src/
│   └── AvasRoutingApp/
│       ├── AvasRoutingApp.csproj              # .NET 8 WPF Project
│       ├── App.xaml / App.xaml.cs             # Application entrypoint
│       ├── MainWindow.xaml / .cs              # Main Window hosting WebView2 & Sidebar
│       ├── appsettings.json                   # Default runtime configuration
│       ├── Configuration/
│       │   ├── AppConfig.cs                   # Strongly-typed configuration model
│       │   └── ConfigService.cs               # Thread-safe atomic JSON persistence
│       ├── Sdvoe/
│       │   ├── AvasDevice.cs                  # Device model with telemetry
│       │   ├── SdvoeDiscoveryFilter.cs        # Strict filter: VID 105, PID 81, chip_0
│       │   ├── MulticastIpManager.cs          # Dynamic pool allocator (224.1.1.1-224.1.3.225)
│       │   ├── SdvoeClient.cs                 # Unified Telnet (6970) & REST client
│       │   ├── ISdvoeDiscoveryService.cs      # Discovery interface
│       │   └── IMulticastController.cs        # Streaming & allocation controller
│       ├── Rtp/
│       │   ├── RtpHeader.cs                   # RFC 3550 & RFC 4175 packet models
│       │   ├── RtpPacketParser.cs             # 20-byte packet header validator
│       │   ├── ScanlineFrameReassembler.cs    # Scanline reassembler with Marker frame boundary
│       │   ├── Yuv422Rasterizer.cs            # Fast Q10 fixed-point YUV422 to RGB24 rasterizer
│       │   ├── RtpMulticastReceiver.cs        # Asynchronous UDP multicast socket receiver
│       │   └── IRtpStreamReceiver.cs          # Receiver abstraction
│       ├── ViewModels/
│       │   ├── ViewModelBase.cs               # INotifyPropertyChanged & relay commands
│       │   ├── MainViewModel.cs               # Root coordinator
│       │   └── EncoderCardViewModel.cs        # Card model with rolling FPS & WriteableBitmap
│       ├── Views/
│       │   ├── OverlaySidebarView.xaml / .cs  # 380px collapsible preview drawer
│       │   ├── EncoderCardView.xaml / .cs     # Individual preview card with live video
│       │   └── SettingsDialog.xaml / .cs      # Settings modal dialog
│       └── publish/                           # Self-contained standalone binary distribution
└── tests/
    ├── E2ETests/                              # 184 Comprehensive E2E Tests (100% Passing)
    │   ├── E2ETests.csproj
    │   ├── Mocks/
    │   │   ├── MockSdvoeServer.cs             # High-fidelity Telnet & REST mock server
    │   │   └── MockRtpStreamer.cs             # Real UDP multicast RFC 4175 packet streamer
    │   └── Suites/
    │       ├── Tier1_FeatureCoverageTests.cs  # 80 tests (All 16 features x 5)
    │       ├── Tier2_BoundaryCornerTests.cs   # 80 boundary & fault injection tests
    │       ├── Tier3_CrossFeatureTests.cs     # 16 interaction & multi-device tests
    │       └── Tier4_ApplicationScenarioTests.cs # 8 end-to-end operational workflow tests
```

---

## 2. Verification Evidence

### Automated Test Suite Execution
The complete 184-test suite was executed against the production codebase:
```powershell
dotnet test "BlueRiver AV Overlay Test App\tests\E2ETests\E2ETests.csproj"
```
**Results:**
- Total Tests: **184**
- Passed: **184 (100%)**
- Failed: **0**
- Skipped: **0**
- Duration: **~1.1s**

### Feature Coverage Checklist
| Requirement | Verification Item | Status | Evidence |
| :--- | :--- | :---: | :--- |
| **R1** | Portable Windows App & WebView2 | **PASSED** | Compiled in `src\AvasRoutingApp\publish\AvasRoutingApp.exe`. Uses local `.\WebView2_UserData` for 100% portable isolation. |
| **R1** | Settings Panel & Persistence | **PASSED** | `SettingsDialog.xaml` provides editing of BlueRiver URL, Control Server IP, REST port (8080), Telnet port (6970), Multicast pool (`224.1.1.1`–`224.1.3.225`), and base port (`6792`). Persisted atomically to `appsettings.json`. |
| **R2** | Strict AVAS-223 `chip_0` Filter | **PASSED** | Verified in `SdvoeDiscoveryFilter.cs` and 10+ unit/E2E tests. Matches `Vendor ID: 105`, `Product ID: 81`, `IsTransmitter: true`, and `ChipIndex: 0`. Strictly ignores `chip_1`, receivers, and third-party endpoints. |
| **R3** | Unique Multicast IP Allocator | **PASSED** | `MulticastIpManager.cs` dynamically allocates unique IPs per active encoder from the configured range, strictly excluding Semtech reserved addresses (`224.1.1.253`, `224.1.1.254`). |
| **R4** | Collapsible Right-Side Overlay | **PASSED** | `MainWindow.xaml` features a 28px vertical toggle strip. Minimized/collapsed on startup. Expanding triggers discovery and multicast subscriptions; collapsing drops all multicast memberships, releases IPs, and stops streams to conserve bandwidth. |
| **R5** | RTP Multicast Ingestion & Rasterizer | **PASSED** | `RtpMulticastReceiver.cs` joins IGMP groups, parses RFC 3550 + RFC 4175 scanlines, uses Q10 fixed-point integer math (`Yuv422Rasterizer.cs`) with a 1024-byte clamp LUT, and renders to a WPF `WriteableBitmap` with a live rolling FPS counter ($\ge 1$ FPS). |

---

## 3. How to Launch and Use the Application

### Option A: Launch Pre-Built Portable Executable
Run the pre-published standalone application directly:
```powershell
& "d:\AVAS-SDVoE Related\Dual Link SDVoE TesterV1.16-20230824\Dual Link SDVoE TesterV1.16-20230824\BlueRiver AV Overlay Test App\src\AvasRoutingApp\publish\AvasRoutingApp.exe"
```

### Option B: Build and Run from Source
```powershell
cd "d:\AVAS-SDVoE Related\Dual Link SDVoE TesterV1.16-20230824\Dual Link SDVoE TesterV1.16-20230824\BlueRiver AV Overlay Test App\src\AvasRoutingApp"
C:\Users\mark.leorna\.dotnet\dotnet.exe run
```

### Operational Instructions:
1. **Startup**:
   - The application opens centered at 1440x850.
   - The embedded Microsoft WebView2 loads the BlueRiver AV Manager web interface (default `http://localhost:3000`). If the web server is offline, an offline banner appears with a direct retry button.
   - The preview overlay on the right is **collapsed** by default.
2. **Settings**:
   - Click the **⚙ Settings** button in the top toolbar.
   - Adjust the BlueRiver AV Manager URL, SDVoE Control Server IP, REST Port, Telnet Port, or Multicast IP Range as required, then click **Save Settings**.
3. **Open Live Preview**:
   - Click the vertical **◀ PREVIEW** strip on the right edge of the window.
   - The sidebar expands to 380px, queries the SDVoE Control Server for all AVAS-223 `chip_0` encoders, assigns each a unique multicast IP, and begins live preview streaming ($\ge 1$ FPS) with real-time rolling FPS metrics.
4. **Close Live Preview**:
   - Click the **▶ CLOSE** button or the toggle strip.
   - The sidebar collapses to 0 width, immediately dropping all IGMP multicast memberships and shutting down background receiver threads to ensure zero network bandwidth consumption while hidden.
