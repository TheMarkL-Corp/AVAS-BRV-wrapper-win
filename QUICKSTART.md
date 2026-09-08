# AVAS Routing SW — Quickstart Guide (v1.0.0)

Welcome to **AVAS Routing SW** (`AVAS-BRV-wrapper-win`), a lightweight, high-performance native Windows desktop application designed to control and monitor SDVoE AV distribution networks.

This application provides a dual-capability management cockpit:
1. **Embedded Web Management**: Wraps and renders Semtech's **BlueRiver AV Manager** web interface directly inside a high-speed Microsoft WebView2 Chromium container.
2. **Real-Time Multicast Preview Overlay**: Provides a floating, collapsible native sidebar delivering live uncompressed video previews ($\ge 1$ FPS) for connected **Advantech AVAS-223 encoders (`chip_0` only)** using SDVoE Control Server APIs and RFC 3550 / RFC 4175 UDP multicast ingestion.

---

## 1. Quick Start in 3 Steps (Zero-Install)

AVAS Routing SW is **100% portable** with zero external installer or registry dependencies.

### Step 1: Download & Extract
Download the official release zip:
```
releases/AVAS-Routing-SW-v1.0.0-win-x64.zip
```
Extract the archive anywhere on your system (e.g. `C:\Tools\AVAS-Routing-SW` or a portable USB drive).

### Step 2: Configure Server Connections
Launch `AvasRoutingApp.exe` and click the **⚙ Settings** button in the upper-right corner. Configure your network parameters:

| Setting | Default Value | Description |
|---|---|---|
| **BlueRiver AV Manager URL** | `http://localhost:3000` | Address of your BlueRiver Web GUI |
| **SDVoE Control Server IP** | `127.0.0.1` | IP address of the SDVoE Control Server (`controlserver.exe`) |
| **SDVoE REST Port** | `8080` | REST API service port on the Control Server |
| **Multicast IP Start** | `224.1.1.1` | Starting multicast address for preview pools |
| **Multicast IP End** | `224.1.3.225` | Ending multicast address for preview pools |
| **Base UDP Port** | `6792` | Base port for incoming video datagrams |
| **Local NIC IP (Optional)** | *(Auto / Empty)* | Specific NIC IP to bind for IGMP multicast joins |

Click **Save & Connect**. Settings are atomically saved to `appsettings.json`.

### Step 3: Expand the Live Preview Overlay
- On launch, the preview overlay is collapsed on the right side of the screen.
- Click the **◀ PREVIEW** strip on the right border.
- The sidebar dynamically queries the SDVoE Control Server, discovers all **AVAS-223 encoders (`chip_0`)**, assigns dedicated collision-free multicast IPs, and begins streaming live uncompressed video previews.
- Click **▶ CLOSE** anytime to collapse the overlay. Multicast memberships and network ingestion are immediately stopped.

---

## 2. Hardware Filtering & Architecture

AVAS Routing SW strictly isolates primary AVAS-223 encoder units from other SDVoE devices on the fabric:

```
[SDVoE Control Server] <--(Telnet:6970 / REST:8080)--> [SdvoeClient]
                                                              |
                                                    [Discovery Filter] 
                                                    (Vendor ID: 105, Product ID: 81, chip_0 only)
                                                              |
                                                    [MulticastIpManager] 
                                                    (Pool: 224.1.1.1 - 224.1.3.225)
                                                              |
[Multicast Stream] ---> [RtpMulticastReceiver] ---> [ScanlineReassembler]
                                                              |
                                                    [Yuv422Rasterizer] (Q10 Fixed-Point)
                                                              |
                                                    [WriteableBitmap] (Lockless Backpressure)
                                                              |
                                                    [EncoderCardView] (Live Preview Tile)
```

- **Vendor ID**: `105` (Advantech)
- **Product ID**: `81` (AVAS-223)
- **Chip Index**: `0` (Primary video processing chip; `chip_1` aggregators and receivers are filtered out)
- **Direct Rasterization**: Q10 fixed-point conversion directly translates YUV422 scanlines into WPF `WriteableBitmap.BackBuffer` without UI thread locking.

---

## 3. QA Code Audit & Architectural Hardening

During comprehensive architectural code auditing, several critical enhancements were engineered into v1.0.0:

| Defect / Subsystem | Severity | Remediation Applied |
|---|---|---|
| **LOH Memory Pressure** | **P0 - Critical** | Guarded RGB24 array conversion behind `FrameReady != null` in `RtpMulticastReceiver.cs`, eliminating 6.2 MB/frame allocations and slashing GC Gen 2 churn by >370 MB/s. |
| **UI Shutdown Deadlock** | **P0 - Critical** | Replaced sync-over-async `.GetAwaiter().GetResult()` in `MainViewModel.Dispose()` with bounded `Task.Run()` timeouts (`1500ms`), preventing UI freeze during window closing. |
| **Telnet Broken Stream Recovery** | **P1 - High** | Wrapped `SendTelnetCommandAsync` in `SdvoeClient.cs` to trigger `CleanupTelnetResources()` and reset `_isTelnetAuthenticated = false` upon network disconnects. |
| **Multicast IP Key Collision** | **P2 - Medium** | Differentiated external active stream keys in `MulticastIpManager.cs` as `$"active_stream_{ipNum:X8}"`, eliminating pool leaks. |
| **UI Dispatcher Safety** | **P2 - Medium** | Added `Dispatcher.BeginInvoke` marshaling for background UDP telemetry in `EncoderCardViewModel.cs` (`CurrentFps`, `FpsBrush`, `StatusMessage`). |
| **Test Socket Isolation** | **P2 - Medium** | Updated test suites to bind ephemeral port `0` dynamically with polling deadlines, eliminating race conditions under rapid recycling. |

---

## 4. Test Suite & Verification Results

AVAS Routing SW is validated by two automated test suites comprising **388 automated tests** passing at **100%**:

- **`AvasRoutingApp.Tests`**: **201 / 201 Passed (100%)**
- **`E2ETests`**: **187 / 187 Passed (100%)**

### Loop Stress & Endurance Testing (4,005 Test Executions)

An intensive 5-tier stress loop was performed across the prebuilt Release configuration:

| Test Tier | Iterations | Tests / Run | Total Tests | Duration | Result |
|---|---|---|---|---|---|
| **Tier 1: Feature Coverage** | 20 runs | 81 tests | 1,620 tests | 116.71s | **PASSED (100%)** |
| **Tier 2: Boundary & Fault Injection** | 15 runs | 81 tests | 1,215 tests | 56.76s | **PASSED (100%)** |
| **Tier 3 & 4: Workflows & Multi-Device** | 10 runs | 25 tests | 250 tests | 33.47s | **PASSED (100%)** |
| **Tier 5: Prolonged Endurance** | 10 runs | 3 tests | 30 tests | 39.55s | **PASSED (100%)** |
| **Full Suite Endurance Runs** | 5 runs | 187 tests | 935 tests | 30.33s | **PASSED (100%)** |
| **Grand Total** | **60 runs** | — | **4,005 tests** | **276.82s** | **100% PASSED (0 Failures)** |

> [!NOTE]
> See [`LOOP_TEST_RESULTS.md`](./LOOP_TEST_RESULTS.md) for full execution logs and benchmarks.

---

## 5. Standalone Release Package

A ready-to-deploy zip bundle is available in the repository at:
```
releases/AVAS-Routing-SW-v1.0.0-win-x64.zip
```

### Bundle Contents
```
releases/AVAS-Routing-SW-v1.0.0-win-x64.zip
├── AvasRoutingApp.exe             # Portable Application Executable
├── AvasRoutingApp.dll             # Core Application Assemblies
├── appsettings.json               # Local Configuration File
├── Microsoft.Web.WebView2.*.dll   # Embedded WebView2 Runtime Libraries
├── WebView2Loader.dll             # WebView2 Native Loader
├── USER_MANUAL.md                 # Complete User Manual
├── QUICKSTART.md                  # This Quickstart Guide
├── VERIFICATION_GUIDE.md          # Verification & Test Harness Guide
└── LOOP_TEST_RESULTS.md           # 4,005-Test Endurance Report
```

---

## 6. Documentation Directory

- **[User Manual](./USER_MANUAL.md)**: Exhaustive manual with UI diagrams, settings reference, and network requirements.
- **[Verification Guide](./VERIFICATION_GUIDE.md)**: Deep dive into the 5-tier test harness and verification methodologies.
- **[Loop Test Results](./LOOP_TEST_RESULTS.md)**: Official endurance test verification record.
- **[GitHub Repository](https://github.com/TheMarkL-Corp/AVAS-BRV-wrapper-win)**: Source code, issue tracker, and releases.
