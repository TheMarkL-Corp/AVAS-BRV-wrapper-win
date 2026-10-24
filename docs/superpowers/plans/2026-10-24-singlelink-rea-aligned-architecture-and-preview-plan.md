# Comprehensive Architecture & Preview Restoration Plan (Aligned with SingleLink REA Reports)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Overhaul the AVAS Routing Software (v1.8.0) to fully align with the authoritative reverse engineering blueprints in `SingleLink REA Reports` (Reports 06, 14, 15, 16, 17), resolving the root causes of Preview failure and Temperature telemetry failure while hardening Handshake, Device Initialization, Device Info Extraction, MCU Communication, and Real-Time Video Preview.

**Architecture:**
1. **Three-Plane Architecture**: Strictly decouple Control Plane (TCP Port 6970 JSON-RPC / Telnet), Device Management Plane (UART:1 RS-232 pass-through tunnel), and Media Plane (UDP Multicast RFC 4175 with `ReuseAddress = true` on Local 10G NIC).
2. **Deterministic MCU IP Resolution**: Query `get ipaddr\r\n` dynamically upon device initialization/selection to extract the true Baseboard MCU Management IP (e.g. `10.32.32.14`), eliminating heuristic `+2` offset guessing.
3. **Resilient Unified RTP Demuxing**: In `UnifiedRtpDemuxReceiver`, demux packets by: (a) exact Sender IP, (b) verified MCU IP alias, (c) Destination Multicast Group, and (d) single active channel fallback. Remove the multi-device `/24` subnet false-positive drop.
4. **Universal AVAS Preview Pipeline**: For AVAS-223 hardware (Vendor ID 105), execute MCU UART preview (`rtp igmp <IP>` + `rtp ON`) on UDP port 5000 as the primary preview engine across both `SINGLE` and `DUAL` link modes. BlueRiver Stream 1 (`HDMI:1` Scaler) remains as a secondary fallback.
5. **Deterministic Double-Action Teardown**: Ensure stream teardown drops IGMP multicast locally (`DropMulticastGroup`), halts MCU packetization (`rtp OFF\r\n`), flushes AVP FIFOs (`address 0.0.0.0`), and stops Semtech thumbnails (`stop <MAC>:thumbnail:0 free`).
6. **Multi-Port Resilient Telemetry**: Revert destructive port 8080 overwrites in `ConfigService`, implement multi-port fallback probing (8090, 8080, 80) in `TemperatureService`, pass the resolved port to `VOIPS_LIB.VOIPS`, and maintain temperature telemetries securely behind the PIN-locked Advanced tab with responsive 5s polling when active.

**Tech Stack:** C# .NET 8 / WPF, Semtech BlueRiver JSON-RPC (TCP 6970), RFC 3550 / RFC 4175 RTP UDP Multicast, Advantech VoIP SDK (`VOIPS.dll`), ITU-R BT.601 integer rasterizer, xUnit.

**Specs & Sources:**
- REA Report 17: `17_AI_DEVELOPER_HANDOVER_AND_IMPLEMENTATION_GUIDE.md`
- REA Report 16: `16_AVAS_SDVOE_RECREATION_SPEC_INITIALIZATION_AND_PREVIEW.md`
- REA Report 14: `14_PREVIEW_LIFECYCLE_AND_ROBUSTNESS.md`
- REA Report 06: `06_FEATURE_DEEP_DIVE_PREVIEW.md`

## Global Constraints
- Target Framework: .NET 8 (`net8.0-windows`).
- All 580 existing unit and E2E tests must pass without regressions.
- Temperature telemetries MUST remain behind the Advanced maintenance tab (per user directive).
- Socket binding MUST use `SocketOptionName.ReuseAddress = true` to prevent `WSAEADDRINUSE 10048`.

## Review Focus
1. **Single-Link AVAS-223 Preview**: In Single-Link mode, AVAS-223 transmitters must start preview via MCU UART (`rtp ON`) instead of getting hijacked by BlueRiver HDMI Scaler Stream 1.
2. **Subnet Sharing**: Multiple AVAS devices located on the same `/24` subnet must not trigger false-positive packet discards in `UnifiedRtpDemuxReceiver`.
3. **MCU IP Resolution**: The baseboard MCU IP must be learned via `get ipaddr\r\n` and registered as an alias, rather than relying on `ipBytes[3] + 2`.
4. **Port 8090 Resilience**: `TemperatureService` and `ConfigService` must work seamlessly with SDVoE servers running on port 8090 without forcing an 8080 overwrite.
5. **Double-Action Teardown**: Stopping preview must issue both local IGMP group drop and hardware `rtp OFF` + `address 0.0.0.0` to prevent switch multicast storms.

---

### Task 1: Revert Port Overwrite in `ConfigService` & Implement Multi-Port Probing in `TemperatureService`

**Files:**
- Modify: `src/AvasRoutingApp/Configuration/ConfigService.cs:60-72`
- Modify: `src/AvasRoutingApp/Sdvoe/TemperatureService.cs:38-52, 475-520, 560-595`
- Test: `tests/AvasRoutingApp.Tests/ConfigTests.cs`
- Test: `tests/AvasRoutingApp.Tests/NextGenArchitectureTests.cs`

**Interfaces:**
- `ConfigService.Load()`: Loads existing `appsettings.json` without converting `RestPort = 8090` to `8080`.
- `TemperatureService.QueryAllTemperaturesAsync(ct)`: Probes responsive ports (`_resolvedRestPort`, `cfg.RestPort`, `8090`, `8080`, `80`), caches responsive port, and propagates it to `VOIPS_LIB.VOIPS.SET_HTTP_PORT(port)`.

- [ ] **Step 1: Write failing unit test for port 8090 config preservation**

```csharp
// In tests/AvasRoutingApp.Tests/ConfigTests.cs
[Fact]
public void Load_WhenConfigContainsRestPort8090_Preserves8090WithoutOverwritingTo8080()
{
    string tempFile = Path.Combine(Path.GetTempPath(), $"cfg_test_{Guid.NewGuid():N}.json");
    try
    {
        File.WriteAllText(tempFile, "{\"RestPort\": 8090, \"ControlServerIp\": \"127.0.0.1\"}");
        var service = new ConfigService(tempFile);
        Assert.Equal(8090, service.Current.RestPort);
    }
    finally
    {
        if (File.Exists(tempFile)) File.Delete(tempFile);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AvasRoutingApp.Tests/AvasRoutingApp.Tests.csproj --filter "FullyQualifiedName~Preserves8090"`
Expected: FAIL (because `ConfigService` lines 66-67 forcibly rewrite 8090 to 8080).

- [ ] **Step 3: Remove destructive port migration in `ConfigService.cs`**

Delete lines 66-67 in `src/AvasRoutingApp/Configuration/ConfigService.cs`:
```csharp
// REMOVE:
// if (loaded.RestPort == 8090)
//     loaded.RestPort = 8080;
```

- [ ] **Step 4: Implement multi-port probing in `TemperatureService.cs`**

In `src/AvasRoutingApp/Sdvoe/TemperatureService.cs`:
1. Add `private int _resolvedRestPort = 0;`
2. Update HTTP helpers to try candidate ports:
```csharp
private IEnumerable<int> GetCandidatePorts()
{
    var cfg = _configService.Current;
    var list = new List<int>();
    if (_resolvedRestPort > 0) list.Add(_resolvedRestPort);
    if (cfg.RestPort > 0) list.Add(cfg.RestPort);
    list.Add(8090);
    list.Add(8080);
    list.Add(80);
    return list.Distinct();
}
```
3. In `HttpGetJsonAsync` and `HttpPostJsonAsync`, iterate through `GetCandidatePorts()`. Upon first successful HTTP response, cache `_resolvedRestPort = port;`.
4. In `QueryVoipSdkTemperatureAsync`:
```csharp
int port = _resolvedRestPort > 0 ? _resolvedRestPort : (cfg.RestPort > 0 ? cfg.RestPort : 8090);
voips.SET_CONTROL_SERVER(host);
voips.SET_HTTP_PORT(port);
```

- [ ] **Step 5: Run tests and verify they pass**

Run: `dotnet test tests/AvasRoutingApp.Tests/AvasRoutingApp.Tests.csproj --filter "FullyQualifiedName~ConfigTests"`
Expected: PASS.

- [ ] **Step 6: Commit changes**

```bash
git add src/AvasRoutingApp/Configuration/ConfigService.cs src/AvasRoutingApp/Sdvoe/TemperatureService.cs tests/AvasRoutingApp.Tests/ConfigTests.cs
git commit -m "fix(temperature): preserve port 8090 in config and add multi-port fallback probing to TemperatureService"
```

---

### Task 2: Handshake & Device Initialization Hardening in `SdvoeClient`

**Files:**
- Modify: `src/AvasRoutingApp/Sdvoe/SdvoeClient.cs:220-310, 480-550`
- Modify: `src/AvasRoutingApp/Sdvoe/AvasDevice.cs:20-50`
- Test: `tests/AvasRoutingApp.Tests/SdvoeTests.cs`

**Interfaces:**
- `SdvoeClient.ConnectTelnetAsync(ct)`:
  Reads unsolicited server welcome frame (`result.server`, `result.version`, `result.interfaces`) before issuing `require api 3.0.0.0`. Stores detected host interfaces.
- `AvasDevice.McuIpAddress`:
  String property storing the baseboard MCU's out-of-band management IP.
- `SdvoeClient.QueryMcuDeviceInfoAsync(macAddress, ct)`:
  Executes the REA Report 16 query cascade via RS-232 UART 1:
  - `get fac sn\r\n` -> extracts factory serial number (`status : fac sn <SN>`)
  - `get rtp igmp\r\n` -> extracts current multicast IP (`status : rtp igmp <IP>`)
  - `get ipaddr\r\n` -> extracts MCU management IP (`status : ipaddr <IP>`)
  - `GET MCU VER\r\n` -> extracts MCU firmware version (`MCU VER: <VER>`)

- [ ] **Step 1: Write failing unit test for MCU device info cascade**

```csharp
// In tests/AvasRoutingApp.Tests/SdvoeTests.cs
[Fact]
public async Task QueryMcuDeviceInfoAsync_ParsesExactReaAsciiResponses()
{
    // Test that parsing:
    // "status : fac sn GSD7467312\r\nstatus : rtp igmp 225.0.0.1\r\nstatus : ipaddr 10.32.32.14\r\nMCU VER: V1.2.3\r\n"
    // correctly populates SerialNumber="GSD7467312", MulticastIp="225.0.0.1", McuIp="10.32.32.14", McuVersion="V1.2.3"
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AvasRoutingApp.Tests/AvasRoutingApp.Tests.csproj --filter "FullyQualifiedName~QueryMcuDeviceInfoAsync"`
Expected: FAIL.

- [ ] **Step 3: Update `AvasDevice.cs` and `SdvoeClient.cs`**

1. In `src/AvasRoutingApp/Sdvoe/AvasDevice.cs`, add:
```csharp
public string McuIpAddress { get; set; } = string.Empty;
public string FactorySerialNumber { get; set; } = string.Empty;
public string McuFirmwareVersion { get; set; } = string.Empty;
public string CurrentMulticastIp { get; set; } = string.Empty;
```

2. In `src/AvasRoutingApp/Sdvoe/SdvoeClient.cs`, implement `QueryMcuDeviceInfoAsync`:
```csharp
public async Task<McuDeviceInfo> QueryMcuDeviceInfoAsync(string macAddress, CancellationToken ct = default)
{
    string normMac = NormalizeMac(macAddress);
    var info = new McuDeviceInfo();

    // 1. Ensure UART 1 pass-through mode
    await EnsureUartApiModeAsync(normMac, ct);

    // 2. Query factory serial number: "get fac sn\r\n"
    string snResp = await SendMcuCommandAsync(normMac, "get fac sn\r\n", ct);
    var snMatch = Regex.Match(snResp, @"status\s*:\s*fac\s*sn\s*(?<val>\S+)", RegexOptions.IgnoreCase);
    if (snMatch.Success) info.SerialNumber = snMatch.Groups["val"].Value.Trim();

    // 3. Query current RTP IGMP multicast: "get rtp igmp\r\n"
    string igmpResp = await SendMcuCommandAsync(normMac, "get rtp igmp\r\n", ct);
    var igmpMatch = Regex.Match(igmpResp, @"status\s*:\s*rtp\s*igmp\s*(?<val>\d+\.\d+\.\d+\.\d+)", RegexOptions.IgnoreCase);
    if (igmpMatch.Success) info.MulticastIp = igmpMatch.Groups["val"].Value.Trim();

    // 4. Query MCU management IP: "get ipaddr\r\n"
    string ipResp = await SendMcuCommandAsync(normMac, "get ipaddr\r\n", ct);
    var ipMatch = Regex.Match(ipResp, @"status\s*:\s*ipaddr\s*(?<val>\d+\.\d+\.\d+\.\d+)", RegexOptions.IgnoreCase);
    if (ipMatch.Success) info.McuIpAddress = ipMatch.Groups["val"].Value.Trim();

    // 5. Query MCU version: "GET MCU VER\r\n"
    string verResp = await SendMcuCommandAsync(normMac, "GET MCU VER\r\n", ct);
    var verMatch = Regex.Match(verResp, @"MCU\s*VER:\s*(?<val>\S+)", RegexOptions.IgnoreCase);
    if (verMatch.Success) info.FirmwareVersion = verMatch.Groups["val"].Value.Trim();

    return info;
}
```

- [ ] **Step 4: Run tests and verify they pass**

Run: `dotnet test tests/AvasRoutingApp.Tests/AvasRoutingApp.Tests.csproj --filter "FullyQualifiedName~SdvoeTests"`
Expected: PASS.

- [ ] **Step 5: Commit changes**

```bash
git add src/AvasRoutingApp/Sdvoe/AvasDevice.cs src/AvasRoutingApp/Sdvoe/SdvoeClient.cs tests/AvasRoutingApp.Tests/SdvoeTests.cs
git commit -m "feat(sdvoe): add REA-aligned MCU device info query cascade and McuIpAddress property"
```

---

### Task 3: Restore AVAS-223 Preview Pipeline Across Single & Dual Link Modes

**Files:**
- Modify: `src/AvasRoutingApp/Sdvoe/SdvoeClient.cs:640-720, 770-830`
- Test: `tests/AvasRoutingApp.Tests/SdvoeTests.cs`
- Test: `tests/E2ETests/Suites/SingleAndDualLinkPreviewE2ETests.cs`

**Interfaces:**
- `SdvoeClient.StartPreviewStreamAsync(mac, mcastIp, port, linkMode, chipIdx, ct)`:
  - For Chip 0 on any AVAS device (`chipIdx == 0`), dispatches MCU UART commands (`rtp igmp <IP>\r\n` and `rtp ON\r\n` with `set rtp` fallback) to stream scanline RTP on UDP port 5000.
  - Does NOT divert to BlueRiver Scaler Stream 1 for Chip 0 merely because `linkMode == "SINGLE"`.
  - For Chip 1 or fallback: dispatches BlueRiver Stream 1 Scaler.
- `SdvoeClient.StopPreviewStreamAsync(mac, free, linkMode, chipIdx, ct)`:
  - Executes Deterministic Double-Action Teardown:
    1. Sends `rtp OFF\r\n` (and `set rtp OFF\r\n`) via RS-232 UART 1.
    2. Sends `set <MAC> stream HDMI 1 enable false address 0.0.0.0` (FIFO flush).
    3. Sends `stop <MAC>:thumbnail:0 free` via Telnet/REST.

- [ ] **Step 1: Write failing unit test verifying Single-Link AVAS-223 uses MCU UART**

```csharp
// In tests/AvasRoutingApp.Tests/SdvoeTests.cs
[Fact]
public async Task StartPreviewStreamAsync_SingleLinkAvasChip0_SendsMcuUartCommands()
{
    // Verify that when linkMode == "SINGLE" and chipIndex == 0,
    // StartPreviewStreamViaAvpRs232Async is called and rtp ON is dispatched.
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AvasRoutingApp.Tests/AvasRoutingApp.Tests.csproj --filter "FullyQualifiedName~SingleLinkAvasChip0"`
Expected: FAIL.

- [ ] **Step 3: Update `StartPreviewStreamAsync` and `StopPreviewStreamAsync` in `SdvoeClient.cs`**

1. In `StartPreviewStreamAsync`:
```csharp
public async Task<bool> StartPreviewStreamAsync(
    string macAddress,
    string multicastIp,
    int port,
    string linkMode = "UNKNOWN",
    int chipIndex = 0,
    CancellationToken ct = default)
{
    string normMac = NormalizeMac(macAddress);

    // 1. For AVAS-223 Chip 0 (both DUAL and SINGLE link), prioritize MCU UART tunneling.
    // As proven in REA Report 16 §6, the Cypress MCU outputs 320x180 RFC 4175 YUV422 scanlines on UDP port 5000.
    if (chipIndex == 0)
    {
        if (await StartPreviewStreamViaAvpRs232Async(normMac, multicastIp, ct))
        {
            AppLogger.Info("SdvoeClient", $"Started AVAS-223 AVP thumbnail stream via RS-232 for {normMac} (mode: {linkMode}) -> {multicastIp}");
            return true;
        }
    }

    // 2. For Chip 1 (which lacks an MCU UART bus) or pure SDVoE Scaler endpoints:
    if (string.Equals(linkMode, "SINGLE", StringComparison.OrdinalIgnoreCase) || chipIndex > 0)
    {
        if (await StartSingleLinkStreamAsync(normMac, multicastIp, port, streamIndex: 1, ct))
        {
            AppLogger.Info("SdvoeClient", $"Started native BlueRiver Stream 1 (Scaler) for {normMac} (chip {chipIndex}) -> {multicastIp}");
            return true;
        }
    }

    // 3. Fallback to Semtech thumbnail stream commands
    await ConfigureThumbnailStreamAsync(normMac, 1.0, 12345, port, ct);
    string cmd = $"start {normMac}:thumbnail:0 {multicastIp}";
    try
    {
        if (!_isTelnetAuthenticated) await ConnectTelnetAsync(ct);
        string resp = await SendTelnetCommandAsync(cmd, ct);
        if (IsCommandSuccessful(resp)) return true;
    }
    catch { }

    if (await StartPreviewStreamViaRestAsync(normMac, multicastIp, ct)) return true;

    return await StartSingleLinkStreamAsync(normMac, multicastIp, port, streamIndex: 1, ct);
}
```

2. In `StartPreviewStreamViaAvpRs232Async`:
   - Send both `rtp igmp <IP>\r\n` (REA native) and `set rtp igmp <IP>\r\n` (legacy format).
   - Send both `rtp ON\r\n` (REA native) and `set rtp ON\r\n`.

3. In `StopPreviewStreamAsync`:
   - Send both `rtp OFF\r\n` and `set rtp OFF\r\n`.
   - Send `set <MAC> stream HDMI 1 enable false address 0.0.0.0`.
   - Send `stop <MAC>:thumbnail:0 free`.

- [ ] **Step 4: Run tests and verify they pass**

Run: `dotnet test tests/AvasRoutingApp.Tests/AvasRoutingApp.Tests.csproj --filter "FullyQualifiedName~SdvoeTests"`
Expected: PASS.

- [ ] **Step 5: Commit changes**

```bash
git add src/AvasRoutingApp/Sdvoe/SdvoeClient.cs tests/AvasRoutingApp.Tests/SdvoeTests.cs
git commit -m "fix(preview): restore MCU UART preview streaming for AVAS-223 in Single-Link mode and apply REA command formats"
```

---

### Task 4: Upgrade `UnifiedRtpDemuxReceiver` with Dynamic MCU Aliasing & Subnet Resilience

**Files:**
- Modify: `src/AvasRoutingApp/Rtp/UnifiedRtpDemuxReceiver.cs:220-275, 430-475`
- Modify: `src/AvasRoutingApp/ViewModels/MainViewModel.cs:830-865`
- Test: `tests/AvasRoutingApp.Tests/RtpTests.cs`
- Test: `tests/AvasRoutingApp.Tests/NextGenArchitectureTests.cs`

**Interfaces:**
- `UnifiedRtpDemuxReceiver.TryResolveChannel(senderIp, out channel)`:
  - Direct channel lookup by sender IP.
  - Direct alias lookup (including dynamically queried `AvasDevice.McuIpAddress`).
  - Single active channel fallback (if only 1 channel is listening, accepts all valid RFC 4175 packets).
  - Removes the flawed `matchCount == 1` `/24` subnet drop that silently discarded traffic in multi-device setups.
- `MainViewModel.StartAllStreamsInternalAsync`:
  - Enriches card aliases with the dynamically queried `McuIpAddress` from Task 2.

- [ ] **Step 1: Write failing unit test for multi-device `/24` subnet scenario**

```csharp
// In tests/AvasRoutingApp.Tests/RtpTests.cs
[Fact]
public void DemuxPacket_MultipleDevicesOnSameSubnet_ResolvesByMcuIpAliasWithoutDropping()
{
    var receiver = new UnifiedRtpDemuxReceiver();
    var ch1 = receiver.GetOrCreateChannel("10.32.32.12", "225.0.0.1", new[] { "10.32.32.14" });
    var ch2 = receiver.GetOrCreateChannel("10.32.32.22", "225.0.0.2", new[] { "10.32.32.24" });

    // Packets arriving from MCU 1 (10.32.32.14) must resolve ch1 even though ch1 and ch2 share 10.32.32.x
    Assert.True(receiver.TryResolveChannel("10.32.32.14", out var resolved));
    Assert.Same(ch1, resolved);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AvasRoutingApp.Tests/AvasRoutingApp.Tests.csproj --filter "FullyQualifiedName~MultipleDevicesOnSameSubnet"`
Expected: FAIL.

- [ ] **Step 3: Update `UnifiedRtpDemuxReceiver.cs` and `MainViewModel.cs`**

1. In `src/AvasRoutingApp/Rtp/UnifiedRtpDemuxReceiver.cs`:
```csharp
public bool TryResolveChannel(string senderIp, [NotNullWhen(true)] out DemuxChannel? channel)
{
    if (string.IsNullOrWhiteSpace(senderIp))
    {
        channel = null;
        return false;
    }

    string cleanIp = senderIp.Trim();

    // 1. Direct channel lookup (O(1))
    if (_channels.TryGetValue(cleanIp, out channel)) return true;

    // 2. Direct alias lookup (O(1))
    if (_aliases.TryGetValue(cleanIp, out channel)) return true;

    // 3. Single active channel fallback: If only one channel is currently registered/listening,
    // all incoming multicast datagrams on this port belong to this channel.
    if (_channels.Count == 1)
    {
        channel = _channels.Values.First();
        return true;
    }

    // 4. Subnet /24 fallback: Match candidate on same subnet
    if (IPAddress.TryParse(cleanIp, out var senderAddr) && senderAddr.AddressFamily == AddressFamily.InterNetwork)
    {
        string senderPrefix = GetSubnet24Prefix(cleanIp);
        DemuxChannel? matchCandidate = null;

        foreach (var kvp in _channels)
        {
            if (GetSubnet24Prefix(kvp.Key) == senderPrefix)
            {
                matchCandidate = kvp.Value;
                break; // Select first matching channel on subnet instead of dropping
            }
        }

        if (matchCandidate != null)
        {
            channel = matchCandidate;
            return true;
        }
    }

    channel = null;
    return false;
}
```

2. In `src/AvasRoutingApp/ViewModels/MainViewModel.cs`:
   - When constructing aliases for `dev.IpAddress`, if `dev.McuIpAddress` is populated (or queried via `sdvoeClient.QueryMcuDeviceInfoAsync`), add `aliases.Add(dev.McuIpAddress)`.

- [ ] **Step 4: Run tests and verify they pass**

Run: `dotnet test tests/AvasRoutingApp.Tests/AvasRoutingApp.Tests.csproj --filter "FullyQualifiedName~RtpTests"`
Expected: PASS.

- [ ] **Step 5: Commit changes**

```bash
git add src/AvasRoutingApp/Rtp/UnifiedRtpDemuxReceiver.cs src/AvasRoutingApp/ViewModels/MainViewModel.cs tests/AvasRoutingApp.Tests/RtpTests.cs
git commit -m "fix(demux): harden channel resolution with dynamic MCU IP aliases and eliminate multi-device subnet packet drops"
```

---

### Task 5: Robust Temperature Polling Lifecycle Behind the Advanced Tab

**Files:**
- Modify: `src/AvasRoutingApp/ViewModels/MainViewModel.cs:365-380, 410-425, 1070-1110`
- Modify: `src/AvasRoutingApp/Views/OverlaySidebarView.xaml:170-220`
- Test: `tests/AvasRoutingApp.Tests/SidebarTests.cs`
- Test: `tests/AvasRoutingApp.Tests/AdvancedSettingsLockTests.cs`

**Interfaces:**
- `MainViewModel.UpdateTemperaturePollingState()`:
  - Maintains temperature monitoring securely behind the PIN-locked Advanced tab (as instructed: `IsAdvancedUnlocked == true`).
  - When the user selects Advanced $\rightarrow$ Thermals, starts the 5s polling timer immediately and triggers an initial `RefreshTemperaturesAsync()` without blocking the UI thread.
  - When switching tabs away from Thermals, stops the timer cleanly to eliminate idle CPU and network consumption.

- [ ] **Step 1: Write failing unit test for Advanced Thermals polling activation**

```csharp
// In tests/AvasRoutingApp.Tests/SidebarTests.cs
[Fact]
public void SelectingAdvancedThermals_WhenUnlockedAndSidebarOpen_ImmediatelyActivatesPolling()
{
    // Verify that setting IsAdvancedUnlocked = true and invoking SelectAdvancedThermalsCommand
    // starts the temperature timer and triggers RefreshTemperaturesAsync.
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AvasRoutingApp.Tests/AvasRoutingApp.Tests.csproj --filter "FullyQualifiedName~SelectingAdvancedThermals"`
Expected: FAIL.

- [ ] **Step 3: Update `MainViewModel.cs`**

```csharp
private void UpdateTemperaturePollingState()
{
    bool isThermalsActive = (IsAdvancedTabSelected && IsAdvancedThermalsSelected);
    if (isThermalsActive && IsSidebarExpanded && !_disposed)
    {
        if (_temperatureTimer != null && !_temperatureTimer.IsEnabled)
        {
            AppLogger.Debug("Temperature", "Starting 5s active-tab temperature polling timer.");
            _temperatureTimer.Start();
        }

        if (!IsRefreshingTemperatures)
        {
            _ = RefreshTemperaturesAsync();
        }
    }
    else
    {
        if (_temperatureTimer != null && _temperatureTimer.IsEnabled)
        {
            AppLogger.Debug("Temperature", "Stopping active-tab temperature polling timer.");
            _temperatureTimer.Stop();
        }
    }
}
```

- [ ] **Step 4: Run tests and verify they pass**

Run: `dotnet test tests/AvasRoutingApp.Tests/AvasRoutingApp.Tests.csproj --filter "FullyQualifiedName~SidebarTests"`
Expected: PASS.

- [ ] **Step 5: Commit changes**

```bash
git add src/AvasRoutingApp/ViewModels/MainViewModel.cs tests/AvasRoutingApp.Tests/SidebarTests.cs
git commit -m "fix(thermals): ensure responsive 5s polling when Advanced Thermals tab is active and unlocked"
```

---

### Task 6: Full Regression Verification & Release Build

**Files:**
- Test: `tests/AvasRoutingApp.Tests/`
- Test: `tests/E2ETests/`

- [ ] **Step 1: Run complete unit test suite**

Run: `dotnet test tests/AvasRoutingApp.Tests/AvasRoutingApp.Tests.csproj`
Expected: ALL 389+ unit tests pass.

- [ ] **Step 2: Run complete E2E test suite**

Run: `dotnet test tests/E2ETests/E2ETests.csproj`
Expected: ALL 191+ E2E tests pass.

- [ ] **Step 3: Build release binary package**

Run: `dotnet build src/AvasRoutingApp/AvasRoutingApp.csproj -c Release`
Expected: Build SUCCESS with 0 errors.

- [ ] **Step 4: Commit release verification artifacts**

```bash
git commit -m "chore(release): complete comprehensive REA-aligned preview and temperature overhaul"
```
