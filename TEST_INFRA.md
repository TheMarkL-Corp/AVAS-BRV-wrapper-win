# Test Infrastructure: AVAS Routing SW (Dual Link SDVoE Preview)

## 1. Test Philosophy & Principles

The AVAS Routing SW test harness is designed around five foundational testing principles:
1. **Opaque-Box Testing**: Tests validate requirements, interface contracts, protocol specifications, and observable system behaviors without relying on private implementation details.
2. **Progressive Testability**: Every test is self-contained and verifiable using in-process high-fidelity protocol mocks (`MockSdvoeServer` and `MockRtpStreamer`), allowing verification of networking, parsing, state transitions, and business logic before full UI assembly.
3. **Test Independence & Isolation**: Each test initializes its own isolated state, utilizes dynamic port bindings and temporary filesystem locations, executes deterministically, and cleans up resources (sockets, listeners, files) upon completion.
4. **Authoritative Output Derivation**: Test assertions are derived strictly from authoritative sources:
   - Semtech SDVoE Developers API Reference Guide (PDS-062489 Rev 3.9.0.0)
   - IETF RFC 3550 (RTP: A Transport Protocol for Real-Time Applications)
   - IETF RFC 4175 (RTP Payload Format for Uncompressed Video)
   - Advantech AVAS-223 Hardware Architecture Technical Specification
   - Empirical live probes of Semtech BlueRiver Control Server runtime (`controlserver.exe` v3.2.0.1)
5. **Systematic 4-Tier Coverage Methodology**:
   - **Tier 1 (Feature Coverage)**: Happy-path and essential functional verification for all 16 features ($\ge 5$ tests each = $\ge 80$ tests).
   - **Tier 2 (Boundary & Corner Cases)**: Boundary Value Analysis (BVA), malformed inputs, edge IP boundaries, packet drops, sequence roll-over, and fault injection ($\ge 5$ tests each = $\ge 80$ tests).
   - **Tier 3 (Cross-Feature Combinations)**: Pairwise and multi-component interaction sequences ($\ge 16$ tests).
   - **Tier 4 (Real-World Application Scenarios)**: Realistic end-to-end operational workflows and resilience testing ($\ge 8$ tests).

---

## 2. Feature Inventory Mapping

| # | Feature Code | Feature Name | Tier 1 Tests | Tier 2 Tests | Key Verification Target |
|---|--------------|--------------|--------------|--------------|-------------------------|
| 1 | R1.1 | Portable Shell & App Structure | 5 | 5 | Self-contained deployment, folder structure, asset resolution, single-instance lock |
| 2 | R1.2 | Microsoft WebView2 Embedding | 5 | 5 | Dedicated portable UserDataFolder, URL navigation, environment init, isolation |
| 3 | R1.3 | Configuration & Persistence | 5 | 5 | JSON schema serialization, atomic save, dirty tracking, default fallbacks, corruption recovery |
| 4 | R2.1 | SDVoE Control Server Client | 5 | 5 | Mandatory `require api 3.0.0.0` handshake, Telnet 6970, REST 8080/9200 endpoints, keepalive |
| 5 | R2.2 | AVAS-223 Device Filtering | 5 | 5 | VID=105, PID=81, chip_0 strictly accepted; chip_1 strictly ignored; other vendors excluded |
| 6 | R2.3 | Device Telemetry Tracking | 5 | 5 | Device Name, MAC, IP address, Model, resolution, online status updates |
| 7 | R3.1 | Dynamic Multicast IP Allocator | 5 | 5 | Pool `224.1.1.1`–`224.1.3.225`, port 6792, exclusion of `224.1.1.253/254`, conflict avoidance |
| 8 | R3.2 | SDVoE Preview Stream Control | 5 | 5 | `set <mac> thumbnail`, `start <mac>:thumbnail:0`, `stop <mac>:thumbnail:0 free`, REST equivalents |
| 9 | R4.1 | Collapsible Native Overlay Sidebar | 5 | 5 | Docked right, collapsed by default, 28px toggle strip, state transitions |
| 10 | R4.2 | Preview Cards & Telemetry UI | 5 | 5 | Card collection binding, viewport resolution, rolling FPS computation, latency tracking |
| 11 | R4.3 | Lifecycle & Teardown on Collapse | 5 | 5 | Collapse triggers multicast group drop, socket disposal, IP deallocation, server stop |
| 12 | R5.1 | UDP Multicast Socket Listener | 5 | 5 | Local NIC binding, IGMP join/drop, reuse address, async packet reception |
| 13 | R5.2 | RFC 3550 & RFC 4175 Ingestion | 5 | 5 | 20-byte header parse, marker bit frame boundary, scanline reassembly, out-of-order handling |
| 14 | R5.3 | YUV422 to RGB24 Rasterization | 5 | 5 | Q10 fixed-point conversion, 1024-byte clamp LUT, quad `[U,Y0,V,Y1]` to 2 RGB pixels, color accuracy |
| 15 | R5.4 | Zero-Leak WPF Rendering | 5 | 5 | Double-buffered WriteableBitmap update, >= 1 FPS throughput, GC pressure, zero handle leak |
| 16 | E2E.1 | Automated Test Suite & Packaging | 5 | 5 | Standalone runnability, portable package layout, config integrity, mock verification |

---

## 3. Test Harness Architecture

```
+---------------------------------------------------------------------------------------+
| E2ETests Test Harness (xUnit + .NET 8)                                               |
|                                                                                       |
| +-----------------------------+                     +-------------------------------+ |
| | MockSdvoeServer             |                     | MockRtpStreamer               | |
| | - TCP Telnet Listener (6970)|                     | - RFC 3550 RTP Generator      | |
| |   * require api 3.0.0.0     |                     | - RFC 4175 Scanline Packetizer| |
| |   * get all identity        |                     | - YUV 4:2:2 Test Patterns     | |
| |   * start / stop / free     |                     |   * Color bars, solid colors  | |
| | - HTTP REST Listener (8080) |                     | - Fault Injection Generator   | |
| |   * GET /api                |                     |   * Corrupt header, drop line | |
| |   * POST /api/device/ALL    |                     |   * Missing marker, seq jitter| |
| |   * POST /api/device/{mac}  |                     | - Real UDP Multicast / Unicast| |
| +-----------------------------+                     +-------------------------------+ |
|                │                                                    │                 |
|                ▼                                                    ▼                 |
| +-----------------------------------------------------------------------------------+ |
| | Test Suites:                                                                      | |
| | - Tier 1: Feature Coverage (80 tests, 16 features x 5)                            | |
| | - Tier 2: Boundary & Corner Cases (80 tests, 16 features x 5)                      | |
| | - Tier 3: Cross-Feature Combinations (16 pairwise / interaction tests)            | |
| | - Tier 4: Real-World Scenarios (8 end-to-end workflow tests)                      | |
| +-----------------------------------------------------------------------------------+ |
+---------------------------------------------------------------------------------------+
```

### 3.1 MockSdvoeServer
- Simulates the Semtech BlueRiver Control Server (`controlserver.exe` v3.2.0.1).
- Implements dual protocol interfaces:
  1. **Telnet TCP Server**:
     - Strict handshake enforcement: commands issued before `require api 3.0.0.0` return `{"status":"ERROR","error":{"reason":"INVALID_COMMAND"}}`.
     - Device discovery: returns configurable fleets of mock endpoints (AVAS-223 chip_0, chip_1, other vendors, receivers).
     - Streaming control: handles `set <mac> thumbnail`, `start <mac>:thumbnail:0 <multicast_ip>`, `stop <mac>:thumbnail:0 free`, and `list multicast`.
     - Validates multicast rules: rejects `224.1.1.253`, `224.1.1.254`, and out-of-range addresses (`224.1.4.1`) with `ILLEGAL_ARGUMENT`.
  2. **HTTP REST Server**:
     - Built on `System.Net.HttpListener`.
     - Implements `GET /api`, `GET /api/device`, `POST /api/device/ALL`, `POST /api/device/{mac}`, `GET /api/multicast`, and `GET /api/request/{id}`.

### 3.2 MockRtpStreamer
- Generates RFC 3550 RTP + RFC 4175 uncompressed video packet streams.
- Produces precise 20-byte packet headers:
  - Version = 2, Marker bit on frame completion scanline.
  - SequenceNumber (16-bit incrementing, supports wrap-around).
  - Timestamp (90 kHz clock rate).
  - SSRC identifier matching stream configuration.
  - RFC 4175 header: ExtendedSeqNo, Length ($2 \times \text{Width}$), LineNo ($0 \le \text{LineNo} < \text{Height}$), Offset ($0$).
- Synthesizes synthetic YUV 4:2:2 frames (SMPTE color bars, gradients, timestamps) and transmits UDP datagrams to localhost or multicast IP addresses.
- Built-in fault injection modes: corrupt version byte, truncated datagrams, dropped scanlines, out-of-order delivery, and missing marker bits.

---

## 4. Test Execution & Runner Instructions

### 4.1 Prerequisites
- .NET 8 SDK (located in `C:\Users\mark.leorna\.dotnet`).
- Windows 10/11 x64.

### 4.2 Running Tests via dotnet CLI
Execute the following in PowerShell:

```powershell
# 1. Prepend .NET SDK to PATH
$env:PATH = "C:\Users\mark.leorna\.dotnet;" + $env:PATH

# 2. Navigate to repository root
cd "d:\AVAS-SDVoE Related\Dual Link SDVoE TesterV1.16-20230824\Dual Link SDVoE TesterV1.16-20230824"

# 3. Run entire E2E test suite
dotnet test "AVAS-blueriver-wrapper-winform\tests\E2ETests\E2ETests.csproj" --verbosity normal

# 4. Filter by Tier if desired
dotnet test "AVAS-blueriver-wrapper-winform\tests\E2ETests\E2ETests.csproj" --filter "FullyQualifiedName~Tier1"
dotnet test "AVAS-blueriver-wrapper-winform\tests\E2ETests\E2ETests.csproj" --filter "FullyQualifiedName~Tier2"
dotnet test "AVAS-blueriver-wrapper-winform\tests\E2ETests\E2ETests.csproj" --filter "FullyQualifiedName~Tier3"
dotnet test "AVAS-blueriver-wrapper-winform\tests\E2ETests\E2ETests.csproj" --filter "FullyQualifiedName~Tier4"
```
