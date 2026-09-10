# AVAS-BRV-wrapper-win (AVAS Routing SW)

[![Platform](https://img.shields.io/badge/platform-Windows%20x64-blue.svg)](https://microsoft.com)
[![Framework](https://img.shields.io/badge/.NET-8.0--windows-purple.svg)](https://dotnet.microsoft.com)
[![Tests](https://img.shields.io/badge/tests-434%20passing%20(100%25)-brightgreen.svg)](#test-suite--qa-verification)
[![Loop Stress](https://img.shields.io/badge/loop%20stress-4%2C005%2F4%2C005%20passed-success.svg)](./LOOP_TEST_RESULTS.md)
[![Version](https://img.shields.io/badge/version-v1.2.0-informational.svg)](#)

A high-performance, portable native Windows desktop application that wraps the **Semtech BlueRiver AV Manager** web interface within an isolated **Microsoft WebView2** browser container, and provides a collapsible real-time overlay sidebar displaying live multicast video previews ($\ge 1$ FPS) of **Advantech AVAS-223 Encoders (`chip_0` only)** using SDVoE Control Server APIs and RFC 3550 / RFC 4175 UDP multicast ingestion.

---

## Key Features

- **Embedded BlueRiver AV Manager**: Direct embedded web canvas wrapping BlueRiver AV Manager with zero external browser dependencies.
- **Collapsible Live Multicast Preview**: Right-side collapsible sidebar (collapsed by default) with real-time video feeds for discovered AVAS-223 TX encoders.
- **AVAS-223 Multi-Link Management**: Monitor Single/Dual link status, check companion chip (`chip_1`) online/offline health, and seamlessly toggle link modes with non-blocking reboots.
- **Strict Hardware Filtering**: Automatically targets **AVAS-223 primary chip** (`Vendor ID: 105`, `Product ID: 81`, `ChipIndex: 0`, `IsTransmitter: true`), rejecting `chip_1` aggregators, receivers, and third-party SDVoE endpoints.
- **Dynamic Multicast Pool Management**: Collision-free allocation of multicast IPs from configurable pool (`224.1.3.1` – `224.1.3.225`) and base UDP port (`5000`). Drops multicast memberships when the sidebar is collapsed or the app closes.
- **High-Performance Direct Rasterization**: Q10 fixed-point SIMD-friendly YUV422 $\to$ BGR24 conversion directly into WPF `WriteableBitmap.BackBuffer` with backpressure drop gates to eliminate UI freezes and GC churn.
- **100% Portable**: Zero-install operation; portable settings stored in `appsettings.json`, and WebView2 profile isolated in `.\WebView2_UserData`.

---

## System Architecture

```
+-----------------------------------------------------------------------------------------------+
| AVAS ROUTING SW [v1.1.0]                                            URL: http://localhost:80 [⚙] |
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
| Ready | Portable UserData: .\WebView2_UserData                     SDVoE Server: 127.0.0.1:8090|
+-----------------------------------------------------------------------------------------------+
```

```
[SDVoE Control Server] <--(Telnet:6970 / REST:8090)--> [SdvoeClient]
                                                              |
                                                    [Discovery Filter] (VID 105, PID 81, chip_0)
                                                              |
                                                    [MulticastIpManager] (224.1.3.1 - 224.1.3.225)
                                                              |
[Multicast Stream] ---> [RtpMulticastReceiver] ---> [ScanlineReassembler]
                                                              |
                                                    [Yuv422Rasterizer] (Q10 Fixed-Point)
                                                              |
                                                    [WriteableBitmap] (Lockless Backpressure)
                                                              |
                                                    [EncoderCardView] (Live Preview Tile)
```

---

## Quick Start & Releases

- **[Quickstart Guide](./QUICKSTART.md)**: 3-step zero-install run guide, QA audit findings, and troubleshooting.
- **[Download v1.1.0 Release Zip](./releases/AVAS-Routing-SW-v1.1.0-win-x64.zip)**: Standalone portable application bundle (886 KB).

---

## Documentation

- **[Quickstart Guide](./QUICKSTART.md)**: Operator onboarding, QA audit breakdown, and quickstart steps.
- **[User Manual](./USER_MANUAL.md)**: Full operator guide, UI diagrams, configuration reference, and troubleshooting.
- **[Verification Guide](./VERIFICATION_GUIDE.md)**: Test harness architecture, end-to-end verification steps, and empirical benchmarks.
- **[Loop Test Results](./LOOP_TEST_RESULTS.md)**: Comprehensive report of 4,005 test executions across all stress tiers.

---

## Test Suite & QA Verification

The repository contains two full test suites covering 388 automated tests:

1. **AvasRoutingApp.Tests (201 tests)**: Unit, integration, ViewModel, color conversion, discovery filtering, and WebView2 contract tests.
2. **E2ETests (187 tests)**: Multi-device workflows, network failure recovery, socket lifecycle, and 5-tier endurance tests.

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
