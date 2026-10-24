# AVAS Routing SW - Loop Stress and Endurance Test Report
Date: 2026-09-08 09:40:13
Configuration: Release (x64), .NET 8.0-windows

### Tier 1 Feature Coverage Loop Test - 20 Iterations
Filter: FullyQualifiedName~Tier1
- Status: PASSED (100%)
- Results: 20 / 20 Iterations Passed (0 Failed)
- Total Duration: 116.71 seconds
- Average Duration: 5835.4 ms per run

### Tier 2 Boundary and Fault Injection Loop Test - 15 Iterations
Filter: FullyQualifiedName~Tier2
- Status: PASSED (100%)
- Results: 15 / 15 Iterations Passed (0 Failed)
- Total Duration: 56.76 seconds
- Average Duration: 3783.93 ms per run

### Tier 3 & 4 Cross-Feature and Application Workflow Loop Test - 10 Iterations
Filter: FullyQualifiedName~Tier3|FullyQualifiedName~Tier4
- Status: PASSED (100%)
- Results: 10 / 10 Iterations Passed (0 Failed)
- Total Duration: 33.47 seconds
- Average Duration: 3347.4 ms per run

### Tier 5 Endurance Verification Loop Test - 10 Iterations
Filter: FullyQualifiedName~Tier5
- Status: PASSED (100%)
- Results: 10 / 10 Iterations Passed (0 Failed)
- Total Duration: 39.55 seconds
- Average Duration: 3955.1 ms per run

### Full Suite Full Endurance Run - 5 Iterations
Filter: FullyQualifiedName~Tier
- Status: PASSED (100%)
- Results: 5 / 5 Iterations Passed (0 Failed)
- Total Duration: 30.33 seconds
- Average Duration: 6066.8 ms per run

## Summary: 100% Tests Passed in all Loop Iterations
Total test executions in loop stress session: 4,005 individual tests.
Verified: Zero socket exhaustion, zero thread deadlocks, zero unhandled exceptions, and stable memory across all iterations.

---

# AVAS Routing SW v1.5.0 - Multi-Agent Loop Stress & Function Verification Report
Date: 2026-10-01 15:53:20 (UTC+8)
Configuration: Release (x64), .NET 8.0-windows
Execution Harness: 3 Concurrent Subagents (MultiLink/SDVoE, RTP Demux, E2E Full Workflow)

### 1. MultiLink & Control Server Communication Loop Test - 5 Iterations
- Target: `MultiLinkServiceTests` + `SdvoeTests` (46 tests per run)
- Results: 230 / 230 Executions Passed (0 Failed, 100% Pass Rate)
- Total Duration: 36.24 seconds (Avg 7.25s per run)
- Verifications:
  - Exclusive `chip_0` targeting for `op: "set:multi_link"` and `op: "reboot"`
  - Live hardware topology recognition against `controlserver.exe` PID 9228
  - REST port auto-probing (`[8090, 8080, 80]`) and port caching without socket exhaustion

### 2. RTP Streaming & Unified Demux Engine Loop Test - 10 Iterations
- Target: `RtpTests` (32 tests per run)
- Results: 320 / 320 Executions Passed (0 Failed, 100% Pass Rate)
- Total Duration: 35.23 seconds (Avg 89.6ms test duration per run)
- Verifications:
  - `UnifiedRtpDemuxReceiver` port 5000 UDP socket demultiplexing by sender source IP
  - `ScanlineFrameReassembler` tolerant frame reassembly with scanline interpolation
  - Marker bit packet loss recovery on subsequent frame boundary
  - Q10 fixed-point YUV422 to BGR24 direct rasterization with zero buffer overruns
  - Zero memory leaks and clean socket teardown on port 5000

### 3. E2E Full Workflow & Application Verification Loop Test - 5 Iterations
- Target: `E2ETests` (Tiers 1 through 5, 187 tests per run) + Application Suites (182 tests)
- Results: 1,117 / 1,117 Executions Passed (0 Failed, 100% Pass Rate)
- Total Duration: 19.56 seconds
- Verifications:
  - All 187 E2E workflow tests passing without flakiness across 5 iterations
  - Configuration persistence and automatic legacy migration (`224.1.3.x` -> `225.1.1.x`)
  - Temperature monitoring thresholds, discrepancy alerting (>3°C), and UI cards
  - Application metadata and version `v1.5.0` consistency

### v1.5.0 Loop Test Summary:
- **Total Test Executions in Session**: **1,667** individual test runs
- **Total Passed**: **1,667** (100.0% Pass Rate, 0 Failed, 0 Skipped)
- **Stability**: Zero deadlocks, zero socket leaks on port 5000, zero port collisions on REST probing, and zero memory degradation.
