# AVAS-BRV-wrapper-win (AVAS Routing SW)

[![Platform](https://img.shields.io/badge/platform-Windows%20x64-blue.svg)](https://microsoft.com)
[![Framework](https://img.shields.io/badge/.NET-8.0--windows-purple.svg)](https://dotnet.microsoft.com)
[![Tests](https://img.shields.io/badge/tests-580%20passing%20(100%25)-brightgreen.svg)](#test-suite--qa-verification)
[![Loop Stress](https://img.shields.io/badge/loop%20stress-4%2C005%2F4%2C005%20passed-success.svg)](./LOOP_TEST_RESULTS.md)
[![Version](https://img.shields.io/badge/version-v1.8.0-informational.svg)](#)

A high-performance, portable native Windows desktop application that wraps the **Semtech BlueRiver AV Manager** web interface within an isolated **Microsoft WebView2** browser container, and provides a collapsible real-time overlay sidebar displaying live multicast video previews ($\ge 1$ FPS) of **Advantech AVAS-223 Encoders (`chip_0` only)** using SDVoE Control Server APIs and RFC 3550 / RFC 4175 UDP multicast ingestion.

---

## Key Features

- **Embedded BlueRiver AV Manager**: Direct embedded web canvas wrapping BlueRiver AV Manager with zero external browser dependencies.
- **Mode-Aware Dual-Path Preview Architecture (v1.8.0)**:
  - **Dual Link Mode (10GBY2)**: Primary 10G link (Chip 0) and secondary 10G link (Chip 1) bond for uncompressed 4K60 video. Commands preview via Cypress MCU UART in-band tunnel (`set rtp igmp <IP>` + `set rtp ON`) on UDP port 5000, with automatic fallback to Semtech BlueRiver Stream API if UART is unacknowledged.
  - **Single Link Mode (Autonomous 10G)**: Bypasses MCU UART entirely to eliminate serial timeouts. Commands Semtech BlueRiver Stream 1 Scaler directly (`set <MAC> stream HDMI 1 enable true address <MCAST_IP>`, `auto_stop: true`, `remove_audio: true`) on UDP port 5004.
- **Single-Link Chip 0 / Chip 1 Toggle (v1.8.0)**: Clean segmented pill (`[ Chip 0 | Chip 1 ]`) displayed in the card header on Single Link devices with valid companion chips, enabling dynamic link inspection without leaving orphan multicast subscriptions.
- **Concurrent Dual-Port Demux Receiver (v1.8.0)**: Listens concurrently on both UDP ports 5000 and 5004 with `ReuseAddress = true`, 8MB receive buffers, multi-adapter IGMP sweeps, and strict socket-level source-IP whitelisting (REA Report 06) to drop rogue datagrams before memory allocation.
- **Deterministic Hardware Teardown (v1.8.0)**: Issues explicit `enable false address 0.0.0.0` commands across Stream 1 and Stream 0 to command the AVP packetizer FIFO to immediately halt emitting datagrams, paired with IGMP multicast drops and socket cleanup.
- **Minimalist Single/Dual Link Preview Cards (v1.7.0+)**: Clean operator viewport showing strictly `Single Link` (`#475569`) or `Dual Link` (`#2563EB`) badges, 2-line footer (IP, MAC with quick clipboard copy), live color-coded FPS pill, with all maintenance clutter and technical multicast rows removed.
- **PIN-Locked Technician Mode (v1.7.0+)**: Standard operators only see `Previews` and `Multi-Link` tabs; Thermals and Hardware Diagnostics are hidden behind an 8-digit technician PIN (`00000000`), revealing the dynamic `[ ⚙ Advanced ]` tab with an instant session relock action.
- **Hardened Bifurcated Hardware Reset & Reboot (v1.7.0+)**: Resolved non-functional MCU reset/reboot by enforcing trailing `\r\n` line terminators and REST fallbacks (REA Report 03); provides separated MCU watchdog reboot (`set system reboot\r\n`), MCU factory reset (`set factory default\r\n`), AVP PHY reboot (`reboot <MAC>`), and AVP factory reset (`factory <MAC>`).
- **Purged VEGA-1100 Support (v1.7.0+)**: Stripped all VEGA-1100 references, discovery filters, streaming paths, and config flags to ensure 100% focused reliability on AVAS-223 encoders.
- **Modern Default Network & Port Configurations (v1.7.0+)**: Default multicast pool configured to `225.0.0.1` - `225.0.1.254` (510 discrete stream addresses, Class D validated) and default REST port set to `8080`, with automated legacy configuration migration in `ConfigService`.
- **Tolerant Scanline Reassembly**: Renders live preview smoothly across packet loss via scanline interpolation without UI stalling or discarding full frames.
- **AVAS-223 Multi-Link Management**: Robust chip_0 exclusive targeting with companion correlation and auto-probing REST port resolution (`[8080, 8090, 80]`).
- **Strict Hardware Filtering**: Automatically targets **AVAS-223 primary chip** (`Vendor ID: 105`, `Product ID: 81`, `ChipIndex: 0`, `IsTransmitter: true`), rejecting `chip_1` aggregators, receivers, and third-party SDVoE endpoints.
- **High-Performance Direct Rasterization**: Q10 fixed-point SIMD-friendly YUV422 $\to$ BGR24 conversion directly into WPF `WriteableBitmap.BackBuffer` with backpressure drop gates to eliminate UI freezes and GC churn.
- **Touchscreen Optimized**: Designed for medical carts and surgical touchscreens with 36px–44px touch targets, native direct touch flick/drag scrolling (`PanningMode="VerticalOnly"`), high-contrast pressed states (`IsPressed`), and 14px draggable splitter grip.
- **100% Portable**: Zero-install operation; portable settings stored in `appsettings.json`, and WebView2 profile isolated in `.\WebView2_UserData`.

---

## System Architecture

```
+-----------------------------------------------------------------------------------------------+
| AVAS ROUTING SW [v1.8.0]                                            URL: http://localhost:80 [⚙] |
+---------------------------------------------------------------------------------------+-------+
|                                                                                       | ◀     |
|                                                                                       | P     |
|                                                                                       | R     |
|                             Embedded BlueRiver AV Manager                             | E     |
|                              (Microsoft WebView2 Canvas)                              | V     |
|                                                                                       | I     |
|                                                                                       | E     |
|                                                                                       | W     |
+---------------------------------------------------------------------------------------+-------+
| Ready | Portable UserData: .\WebView2_UserData                     SDVoE Server: 127.0.0.1:8080|
+-----------------------------------------------------------------------------------------------+
```

```
[SDVoE Control Server] <--(Telnet:6970 / REST:8080)--> [SdvoeClient]
                                                               │
                                                     [Discovery Filter] (VID 105, PID 81, chip_0)
                                                               │
                                                     [MulticastIpManager] (225.0.0.1 - 225.0.1.254)
                                                               │
[Dual Link: UDP 5000 / Single Link: UDP 5004] ---> [UnifiedRtpDemuxReceiver] (Dual-Port Concurrent)
                                                               │
                                                     [Strict Source-IP Whitelist Filter]
                                                               │
                                                     [ScanlineFrameReassembler]
                                                               │
                                                     [Yuv422Rasterizer] (Q10 Fixed-Point)
                                                               │
                                                     [WriteableBitmap] (Lockless Backpressure)
                                                               │
                                                     [EncoderCardView] ([Chip 0 | Chip 1] Toggle)
```

---

## Quick Start & Releases

- **[Quickstart Guide](./QUICKSTART.md)**: 3-step zero-install run guide, QA audit findings, and troubleshooting.
- **[Download v1.4.0 Release Zip](./releases/AVAS-Routing-SW-v1.4.0-win-x64.zip)**: Standalone portable application bundle.

---

## Documentation

- **[Quickstart Guide](./QUICKSTART.md)**: Operator onboarding, QA audit breakdown, and quickstart steps.
- **[User Manual](./USER_MANUAL.md)**: Full operator guide, UI diagrams, configuration reference, and troubleshooting.
- **[Verification Guide](./VERIFICATION_GUIDE.md)**: Test harness architecture, end-to-end verification steps, and empirical benchmarks.
- **[Loop Test Results](./LOOP_TEST_RESULTS.md)**: Comprehensive report of 4,005 test executions across all stress tiers.

---

## Test Suite & QA Verification

The repository contains two full test suites covering 580 automated tests:

1. **AvasRoutingApp.Tests (389 tests)**: Unit, integration, ViewModel, PIN lock security, hardware diagnostics, color conversion, discovery filtering, Single/Dual link mode switching, and WebView2 contract tests.
2. **E2ETests (191 tests)**: Multi-device workflows, network failure recovery, socket lifecycle, Single/Dual link preview pipelines, and 5-tier endurance tests.

### Loop Stress Testing Summary (100% Pass Rate)

| Test Tier | Iterations | Tests Executed | Result |
|---|---|---|---|
| **Tier 1: Feature Coverage** | 20 runs | 1,620 tests | **PASSED (100%)** |
| **Tier 2: Boundary & Fault Injection** | 15 runs | 1,215 tests | **PASSED (100%)** |
| **Tier 3 & 4: Workflows & Multi-Device** | 10 runs | 250 tests | **PASSED (100%)** |
| **Tier 5: Endurance Verification** | 10 runs | 30 tests | **PASSED (100%)** |
| **Full Suite Endurance Run** | 5 runs | 935 tests | **PASSED (100%)** |
| **Total** | **60 runs** | **4,005 tests** | **PASSED (100%)** |

---

## Building and Running

### Prerequisites
- Windows 10 / 11 (64-bit)
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

### Build Solution
```powershell
dotnet build AvasRoutingApp.sln -c Release
```

### Run Tests
```powershell
dotnet test tests/AvasRoutingApp.Tests/AvasRoutingApp.Tests.csproj -c Release
dotnet test tests/E2ETests/E2ETests.csproj -c Release
```

### Publish Standalone Release
```powershell
dotnet publish src/AvasRoutingApp/AvasRoutingApp.csproj -c Release -r win-x64 --self-contained false -o publish
```

---

## License
Proprietary Advantech SDVoE Application Component.
