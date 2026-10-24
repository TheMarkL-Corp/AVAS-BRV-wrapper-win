# Mode-Aware Preview & Temperature Telemetry Restoration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Restore full reliability of live video preview and temperature telemetries in v1.8.0 by re-aligning the AVAS-223 preview pipeline with MCU UART streaming, making `UnifiedRtpDemuxReceiver` resilient to multi-device subnet traffic, adding multi-port REST probing to `TemperatureService`, and fixing the temperature polling lifecycle behind the Advanced maintenance tab.

**Architecture:** 
1. Re-prioritize MCU UART tunneling (`set rtp igmp` + `set rtp ON`) in `SdvoeClient.StartPreviewStreamAsync` for all AVAS hardware regardless of link mode (`SINGLE` or `DUAL`), keeping BlueRiver Scaler Stream 1 strictly as a fallback.
2. Upgrade `UnifiedRtpDemuxReceiver` to support dynamic MCU IP alias registration and multicast-aware routing, eliminating false-positive packet drops in multi-device deployments.
3. Equip `TemperatureService` with multi-port fallback probing (8090, 8080, 80) matching `SdvoeClient`, propagate the discovered port to `VOIPS_LIB.VOIPS`, and remove the destructive port 8080 forced migration in `ConfigService`.
4. Keep Temperature Telemetry securely positioned behind the PIN-locked Advanced tab as requested, while ensuring that unlocking and viewing Thermals immediately starts responsive 5s polling without freezing or timing out.

**Tech Stack:** C# .NET 8 WPF, SDVoE BlueRiver REST/Telnet API, Advantech VOIPS SDK (`VOIPS.dll`), RFC 3550 / RFC 4175 UDP Multicast, xUnit.

**Spec:** `docs/superpowers/specs/2026-10-24-v1.8.0-single-and-dual-link-preview-pipeline.md` and REA Report 16 (`SingeLink_REA_Report/16_AVAS_SDVOE_RECREATION_SPEC_INITIALIZATION_AND_PREVIEW.md`).

## Global Constraints
- Target Framework: .NET 8 (`net8.0-windows`).
- All 580 existing unit and E2E tests must pass without regressions.
- Temperature telemetries MUST remain behind the Advanced maintenance tab (per user instruction).
- Do not introduce breaking API changes to `IMulticastController` or `ITemperatureService`.

## Review Focus
1. Single-Link AVAS-223 device preview must execute MCU UART streaming (`set rtp ON`) rather than getting routed to pure HDMI scaler.
2. In multi-device setups sharing a `/24` subnet, `UnifiedRtpDemuxReceiver` must not drop packets due to subnet collisions.
3. If BlueRiver control server is on port 8090, `TemperatureService` must probe and connect successfully instead of throwing connection refused.
4. `ConfigService` must not overwrite an existing user's `RestPort = 8090` back to `8080`.
5. Toggling between Thermals and Diagnostics within the Advanced tab must cleanly start and stop the 5s timer without leaving orphan background tasks.

---

### Task 1: Revert Destructive Port 8080 Migration in `ConfigService` & Add Multi-Port Probing to `TemperatureService`

**Files:**
- Modify: `src/AvasRoutingApp/Configuration/ConfigService.cs:60-75`
- Modify: `src/AvasRoutingApp/Sdvoe/TemperatureService.cs:38-50, 480-515, 560-590`
- Test: `tests/AvasRoutingApp.Tests/ConfigTests.cs`
- Test: `tests/AvasRoutingApp.Tests/NextGenArchitectureTests.cs`

**Interfaces:**
- `ConfigService.Load()`: Preserves configured `RestPort` (does not clobber `8090`).
- `TemperatureService.QueryAllTemperaturesAsync(ct)`: Probes responsive REST ports (configured, 8090, 8080, 80) and uses resolved port for HTTP and `VOIPS_LIB.VOIPS`.

- [ ] **Step 1: Write failing tests for ConfigService port retention and TemperatureService multi-port probing**

```csharp
// In tests/AvasRoutingApp.Tests/ConfigTests.cs
[Fact]
public void Load_WhenConfigContainsRestPort8090_Preserves8090WithoutOverwritingTo8080()
{
    string tempPath = Path.Combine(Path.GetTempPath(), $"test_cfg_{Guid.NewGuid():N}.json");
    try
    {
        File.WriteAllText(tempPath, "{\"RestPort\": 8090, \"ControlServerIp\": \"127.0.0.1\"}");
        var svc = new ConfigService(tempPath);
        Assert.Equal(8090, svc.Current.RestPort);
    }
    finally
    {
        if (File.Exists(tempPath)) File.Delete(tempPath);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AvasRoutingApp.Tests/AvasRoutingApp.Tests.csproj --filter "FullyQualifiedName~Preserves8090"`
Expected: FAIL (because `ConfigService` currently converts 8090 to 8080).

- [ ] **Step 3: Modify `ConfigService.cs` to remove forced 8090 -> 8080 rewrite**

Remove lines 66-67 in `src/AvasRoutingApp/Configuration/ConfigService.cs`:
```csharp
// Delete this destructive migration:
// if (loaded.RestPort == 8090)
//     loaded.RestPort = 8080;
```

- [ ] **Step 4: Implement resilient multi-port probing in `TemperatureService.cs`**

In `src/AvasRoutingApp/Sdvoe/TemperatureService.cs`:
1. Add `private int _resolvedRestPort = 0;`
2. Update `BaseUrl`:
```csharp
private string GetBaseUrl(int port)
{
    var cfg = _configService.Current;
    string ip = string.IsNullOrWhiteSpace(cfg.ControlServerIp) ? "127.0.0.1" : cfg.ControlServerIp;
    return $"http://{ip}:{port}";
}
```
3. Update `HttpGetJsonAsync` and `HttpPostJsonAsync` to probe `new[] { _resolvedRestPort, cfg.RestPort, 8090, 8080, 80 }.Where(p => p > 0).Distinct().ToArray()` and cache `_resolvedRestPort`.
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
git commit -m "fix(temperature): remove forced 8090 overwrite and add multi-port fallback probing to TemperatureService"
```

---

### Task 2: Restore AVAS-223 MCU UART Thumbnail Preview Pipeline in `SdvoeClient.StartPreviewStreamAsync`

**Files:**
- Modify: `src/AvasRoutingApp/Sdvoe/SdvoeClient.cs:640-700`
- Test: `tests/AvasRoutingApp.Tests/SdvoeTests.cs`
- Test: `tests/E2ETests/Suites/SingleAndDualLinkPreviewE2ETests.cs`

**Interfaces:**
- `SdvoeClient.StartPreviewStreamAsync(mac, mcastIp, port, linkMode, chipIdx, ct)`:
  For AVAS-223 devices (Chip 0 or Single-Link primary MAC), execute `StartPreviewStreamViaAvpRs232Async` (`set rtp igmp <IP>` + `set rtp ON`) on port 5000. Fallback to Semtech thumbnail command (`start <MAC>:thumbnail:0`) or BlueRiver Stream 1 Scaler if MCU RS-232 returns false.

- [ ] **Step 1: Write failing unit test for Single-Link AVAS-223 preview dispatch**

```csharp
// In tests/AvasRoutingApp.Tests/SdvoeTests.cs
[Fact]
public async Task StartPreviewStreamAsync_WhenSingleLinkAndAvasMcuAvailable_PrioritizesMcuUartTunneling()
{
    // Verify that for an AVAS-223 transmitter with linkMode="SINGLE" and chipIndex=0,
    // StartPreviewStreamViaAvpRs232Async is dispatched first before any BlueRiver Scaler fallback.
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AvasRoutingApp.Tests/AvasRoutingApp.Tests.csproj --filter "FullyQualifiedName~PrioritizesMcuUartTunneling"`
Expected: FAIL.

- [ ] **Step 3: Refactor `StartPreviewStreamAsync` in `src/AvasRoutingApp/Sdvoe/SdvoeClient.cs`**

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

    // 1. For AVAS Chip 0 (regardless of whether DUAL or SINGLE link), prioritize MCU UART tunneling.
    // The Cypress MCU on AVAS-223 generates the RFC 4175 320x180 scanline RTP preview stream on UDP port 5000.
    if (chipIndex == 0)
    {
        if (await StartPreviewStreamViaAvpRs232Async(normMac, multicastIp, ct))
        {
            AppLogger.Info("SdvoeClient", $"Started AVAS-223 AVP thumbnail stream via RS-232 for {normMac} (mode: {linkMode}) -> {multicastIp}");
            return true;
        }
    }

    // 2. For Chip 1 or non-MCU fallback in SINGLE link mode, invoke native BlueRiver Stream 1 Scaler
    if (string.Equals(linkMode, "SINGLE", StringComparison.OrdinalIgnoreCase) || chipIndex > 0)
    {
        if (await StartSingleLinkStreamAsync(normMac, multicastIp, port, streamIndex: 1, ct))
        {
            AppLogger.Info("SdvoeClient", $"Started native BlueRiver Stream 1 (Scaler) for {normMac} (chip {chipIndex}) -> {multicastIp}");
            return true;
        }
    }

    // 3. Fall back to standard Semtech BlueRiver thumbnail stream commands
    await ConfigureThumbnailStreamAsync(normMac, 1.0, 12345, port, ct);
    string cmd = $"start {normMac}:thumbnail:0 {multicastIp}";
    try
    {
        if (!_isTelnetAuthenticated) await ConnectTelnetAsync(ct);
        string resp = await SendTelnetCommandAsync(cmd, ct);
        if (IsCommandSuccessful(resp)) return true;
    }
    catch { }

    if (await StartPreviewStreamViaRestAsync(normMac, multicastIp, ct))
    {
        return true;
    }

    // Final fallback to native BlueRiver Stream 1 Scaler
    return await StartSingleLinkStreamAsync(normMac, multicastIp, port, streamIndex: 1, ct);
}
```

- [ ] **Step 4: Run tests to verify pass**

Run: `dotnet test tests/AvasRoutingApp.Tests/AvasRoutingApp.Tests.csproj --filter "FullyQualifiedName~SdvoeTests"`
Expected: PASS.

- [ ] **Step 5: Commit changes**

```bash
git add src/AvasRoutingApp/Sdvoe/SdvoeClient.cs tests/AvasRoutingApp.Tests/SdvoeTests.cs
git commit -m "fix(preview): restore MCU UART preview streaming for AVAS-223 in Single-Link mode"
```

---

### Task 3: Overhaul `UnifiedRtpDemuxReceiver` Whitelist & Dynamic MCU IP Resolution

**Files:**
- Modify: `src/AvasRoutingApp/Rtp/UnifiedRtpDemuxReceiver.cs:220-275, 430-465`
- Modify: `src/AvasRoutingApp/ViewModels/MainViewModel.cs:830-865`
- Test: `tests/AvasRoutingApp.Tests/RtpTests.cs`
- Test: `tests/AvasRoutingApp.Tests/NextGenArchitectureTests.cs`

**Interfaces:**
- `UnifiedRtpDemuxReceiver.TryResolveChannel(senderIp, out channel)`:
  1. Checks direct registered unicast IPs.
  2. Checks registered aliases (including companion MAC and dynamically queried MCU IP).
  3. Checks registered Multicast IP mapping.
  4. If only 1 channel is actively listening, routes to it directly (eliminating unnecessary drops).
  5. In multi-device setups, does not reject traffic merely because devices share a `/24` subnet.

- [ ] **Step 1: Write failing unit test for multi-device `/24` subnet demuxing**

```csharp
// In tests/AvasRoutingApp.Tests/RtpTests.cs
[Fact]
public void DemuxPacket_MultipleChannelsOnSameSubnet_DoesNotDropWhenChannelHasMcastBinding()
{
    var receiver = new UnifiedRtpDemuxReceiver();
    var ch1 = receiver.GetOrCreateChannel("10.32.32.12", "225.0.0.1", new[] { "10.32.32.14" });
    var ch2 = receiver.GetOrCreateChannel("10.32.32.22", "225.0.0.2", new[] { "10.32.32.24" });
    
    // Sender from MCU 1 (10.32.32.14) should resolve ch1 even with ch2 on same subnet
    Assert.True(receiver.TryResolveChannel("10.32.32.14", out var resolved));
    Assert.Same(ch1, resolved);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AvasRoutingApp.Tests/AvasRoutingApp.Tests.csproj --filter "FullyQualifiedName~MultipleChannelsOnSameSubnet"`
Expected: FAIL.

- [ ] **Step 3: Update `UnifiedRtpDemuxReceiver.cs` and `MainViewModel.cs`**

1. In `UnifiedRtpDemuxReceiver.cs`:
   - Map multicast IP to channels: `_channelsByMcastIp[multicastIp] = channel`.
   - Update `TryResolveChannel`:
     - If direct channel matches -> return true.
     - If alias matches -> return true.
     - If `_channels.Count == 1` -> route to the single active channel.
     - Never drop packets if an alias matches an MCU management address.
2. In `MainViewModel.cs`:
   - When preparing card aliases, query baseboard MCU IP via RS-232 `get ipaddr\r\n` (via `sdvoeClient.SendMcuCommandAsync(dev.MacAddress, "get ipaddr")`).
   - If response contains `status : ipaddr <mcuIp>`, register `<mcuIp>` directly into `aliases`.

- [ ] **Step 4: Run tests and verify they pass**

Run: `dotnet test tests/AvasRoutingApp.Tests/AvasRoutingApp.Tests.csproj --filter "FullyQualifiedName~RtpTests"`
Expected: PASS.

- [ ] **Step 5: Commit changes**

```bash
git add src/AvasRoutingApp/Rtp/UnifiedRtpDemuxReceiver.cs src/AvasRoutingApp/ViewModels/MainViewModel.cs tests/AvasRoutingApp.Tests/RtpTests.cs
git commit -m "fix(demux): resolve MCU management IPs accurately and eliminate multi-device subnet packet drops"
```

---

### Task 4: Ensure Robust Temperature Telemetry Operation Behind the Advanced Tab

**Files:**
- Modify: `src/AvasRoutingApp/ViewModels/MainViewModel.cs:365-380, 410-425, 1070-1110`
- Modify: `src/AvasRoutingApp/Views/OverlaySidebarView.xaml:170-220`
- Test: `tests/AvasRoutingApp.Tests/SidebarTests.cs`
- Test: `tests/AvasRoutingApp.Tests/AdvancedSettingsLockTests.cs`

**Interfaces:**
- `MainViewModel.UpdateTemperaturePollingState()`:
  Maintains temperature tab under the PIN-locked Advanced tab as requested (`IsAdvancedUnlocked == true`).
  When Advanced tab is selected and `SelectedAdvancedSubTab == AdvancedSubTab.Thermals`, starts 5s timer immediately and invokes initial `RefreshTemperaturesAsync()` without blocking the UI thread.
  When switching sub-tabs or collapsing the sidebar, cleanly stops the timer.

- [ ] **Step 1: Write failing test verifying temperature polling starts upon selecting Advanced Thermals**

```csharp
// In tests/AvasRoutingApp.Tests/SidebarTests.cs
[Fact]
public void SelectingAdvancedThermals_WhenSidebarExpandedAndUnlocked_ActivatesTemperaturePolling()
{
    // Test that IsAdvancedUnlocked = true and SelectAdvancedThermalsCommand starts temperature timer
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AvasRoutingApp.Tests/AvasRoutingApp.Tests.csproj --filter "FullyQualifiedName~SelectingAdvancedThermals"`
Expected: FAIL.

- [ ] **Step 3: Update `MainViewModel.cs`**

Ensure that:
1. When `IsAdvancedUnlocked` is set to `true` and `SelectedTab = SidebarTab.Advanced`, `UpdateTemperaturePollingState()` is called.
2. In `UpdateTemperaturePollingState()`:
   ```csharp
   bool isThermalsActive = (IsAdvancedTabSelected && IsAdvancedThermalsSelected);
   if (isThermalsActive && IsSidebarExpanded && !_disposed)
   {
       if (_temperatureTimer != null && !_temperatureTimer.IsEnabled)
       {
           AppLogger.Debug("Temperature", "Starting 5s active-tab temperature polling timer.");
           _temperatureTimer.Start();
       }
       // Fire immediate refresh if cards are empty or stale
       if (!IsRefreshingTemperatures)
       {
           _ = RefreshTemperaturesAsync();
       }
   }
   else
   {
       if (_temperatureTimer != null && _temperatureTimer.IsEnabled)
       {
           _temperatureTimer.Stop();
       }
   }
   ```
3. Ensure `RefreshTemperaturesAsync` handles cancellation cleanly and does not deadlock with `_actionLock`.

- [ ] **Step 4: Run tests and verify they pass**

Run: `dotnet test tests/AvasRoutingApp.Tests/AvasRoutingApp.Tests.csproj --filter "FullyQualifiedName~SidebarTests"`
Expected: PASS.

- [ ] **Step 5: Commit changes**

```bash
git add src/AvasRoutingApp/ViewModels/MainViewModel.cs tests/AvasRoutingApp.Tests/SidebarTests.cs
git commit -m "fix(thermals): ensure responsive 5s polling when Advanced Thermals tab is active"
```

---

### Task 5: Full Regression Testing & E2E Validation

**Files:**
- Test: `tests/AvasRoutingApp.Tests/`
- Test: `tests/E2ETests/`

- [ ] **Step 1: Run complete unit test suite**

Run: `dotnet test tests/AvasRoutingApp.Tests/AvasRoutingApp.Tests.csproj`
Expected: ALL 389+ unit tests pass.

- [ ] **Step 2: Run complete E2E test suite**

Run: `dotnet test tests/E2ETests/E2ETests.csproj`
Expected: ALL 191+ E2E tests pass.

- [ ] **Step 3: Verify clean build of release package**

Run: `dotnet build src/AvasRoutingApp/AvasRoutingApp.csproj -c Release`
Expected: 0 errors, 0 warnings.

- [ ] **Step 4: Final commit and tag update**

```bash
git commit -m "chore: complete Mode-Aware preview and temperature restoration"
```
