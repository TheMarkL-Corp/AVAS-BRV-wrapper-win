# VEGA-1100 Preview & AVAS Advanced Diagnostics Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement experimental discovery and preview streaming for Advantech VEGA-1100 devices and an AVAS-only Advanced Diagnostics Maintenance panel with AVP/MCU factory reset and independent dual-chip commanding.

**Architecture:** Extend device filtering with an "Enable VEGA Devices" config flag and add pure SDVoE thumbnail streaming for VEGA-1100 without CYP MCU dependencies. Reverse-engineered BlueRiver native commands (`factory <mac>`, `reboot <mac>`) and MCU UART tunnel commands (`send <mac> rs232:1 set factory default`) are implemented with strict AVAS-only guardrails and independent Chip 0 / Chip 1 targeting in a dedicated maintenance dialog.

**Tech Stack:** .NET 8, C#, WPF, MVVM, Semtech BlueRiver API (Telnet 6970 / REST), xUnit.

**Spec:** `docs/superpowers/specs/2026-10-08-vega-preview-and-maintenance-design.md`

## Global Constraints
- Target Framework: .NET 8.0-windows
- Strict AVAS guardrail: Maintenance actions (Factory Reset / MCU Reset / Reboot) MUST ONLY be executable on AVAS devices (`VendorId == 105`). Non-AVAS devices throw `InvalidOperationException` and have UI buttons hidden.
- VEGA Preview: MUST bypass CYP MCU UART (`set rtp igmp`) and use native BlueRiver SDVoE thumbnail commands.
- MCU UART Routing: MCU commands on dual-link AVAS devices MUST strictly route via Chip 0 (`device.MacAddress`).

## Review Focus
1. VEGA-1100 devices must be completely excluded from discovery when `EnableVegaDevices` is false.
2. Dual-link secondary chips (`ChipIndex == 1`) must never appear as duplicate standalone devices in the primary card view.
3. Factory Reset and MCU Factory Reset buttons must never appear or be accessible on VEGA-1100 cards.
4. Chip 1 selection in the Maintenance Dialog must be disabled if no companion chip exists (`CompanionMac == "NONE"`).
5. Cancellation tokens must be honored in all newly added asynchronous network operations.

---

### Task 1: Device Model, Configuration & Discovery Filtering

**Files:**
- Modify: `src/AvasRoutingApp/Configuration/AppConfig.cs`
- Modify: `src/AvasRoutingApp/Sdvoe/AvasDevice.cs`
- Modify: `src/AvasRoutingApp/Sdvoe/SdvoeDiscoveryFilter.cs`
- Test: `tests/AvasRoutingApp.Tests/SdvoeDiscoveryFilterTests.cs`

**Interfaces:**
- Produces:
  - `AppConfig.EnableVegaDevices`: `bool`
  - `DeviceFamily`: `enum { Avas, Vega, GenericSdvoe }`
  - `AvasDevice.Family`: `DeviceFamily`
  - `SdvoeDiscoveryFilter.IsTargetVega1100(AvasDevice)`: `bool`
  - `SdvoeDiscoveryFilter.FilterTargetDevices(IEnumerable<AvasDevice>, bool)`: `IReadOnlyList<AvasDevice>`

- [x] **Step 1: Write failing unit tests for discovery filtering**
Create tests in `tests/AvasRoutingApp.Tests/SdvoeDiscoveryFilterTests.cs` verifying:
1. AVAS-223 Tx is always included.
2. VEGA-1100 (VID 192, PID 11) is included when `enableVegaDevices = true`.
3. VEGA-1100 is excluded when `enableVegaDevices = false`.
4. Chip 1 secondary devices are excluded regardless of flag.

- [x] **Step 2: Run test to verify failure**
Run: `dotnet test tests/AvasRoutingApp.Tests/ --filter FullyQualifiedName~SdvoeDiscoveryFilterTests`
Expected: FAIL with compilation error (missing members / signatures).

- [x] **Step 3: Implement minimal changes in AppConfig, AvasDevice, and SdvoeDiscoveryFilter**
1. Add `public bool EnableVegaDevices { get; set; } = false;` to `AppConfig.cs`.
2. Add `DeviceFamily` enum and `Family` getter property to `AvasDevice.cs`.
3. Update `SdvoeDiscoveryFilter.cs` to add `IsTargetVega1100` and overload `FilterTargetDevices(devices, enableVega)`.

- [x] **Step 4: Run test to verify it passes**
Run: `dotnet test tests/AvasRoutingApp.Tests/ --filter FullyQualifiedName~SdvoeDiscoveryFilterTests`
Expected: PASS.

- [x] **Step 5: Commit**
```bash
git add src/AvasRoutingApp/Configuration/AppConfig.cs src/AvasRoutingApp/Sdvoe/AvasDevice.cs src/AvasRoutingApp/Sdvoe/SdvoeDiscoveryFilter.cs tests/AvasRoutingApp.Tests/SdvoeDiscoveryFilterTests.cs
git commit -m "feat: add VEGA-1100 device model and discovery filter"
```

---

### Task 2: Pure SDVoE VEGA-1100 Preview Pipeline in `SdvoeClient`

**Files:**
- Modify: `src/AvasRoutingApp/Sdvoe/SdvoeClient.cs`
- Modify: `src/AvasRoutingApp/Sdvoe/ISdvoeDiscoveryService.cs`
- Test: `tests/AvasRoutingApp.Tests/VegaPreviewPipelineTests.cs`

**Interfaces:**
- Consumes: `SdvoeClient.SendTelnetCommandAsync(string, CancellationToken)`
- Produces:
  - `SdvoeClient.StartVegaPreviewStreamAsync(string mac, string multicastIp, int port, CancellationToken)`: `Task<bool>`
  - `SdvoeClient.StopVegaPreviewStreamAsync(string mac, CancellationToken)`: `Task<bool>`

- [x] **Step 1: Write failing unit test for VEGA preview commands**
Create `tests/AvasRoutingApp.Tests/VegaPreviewPipelineTests.cs` validating:
1. `StartVegaPreviewStreamAsync` sends `set <mac> thumbnail ssrc 100 fps 20 udp <ip>:<port>`.
2. `StopVegaPreviewStreamAsync` sends `stop <mac>:thumbnail:0 free` or equivalent BlueRiver stop command.
3. No UART RS232 `set rtp igmp` packets are sent for VEGA devices.

- [x] **Step 2: Run test to verify failure**
Run: `dotnet test tests/AvasRoutingApp.Tests/ --filter FullyQualifiedName~VegaPreviewPipelineTests`
Expected: FAIL.

- [x] **Step 3: Implement VEGA preview methods in `SdvoeClient.cs`**
Add `StartVegaPreviewStreamAsync` and `StopVegaPreviewStreamAsync`. In `StartPreviewStreamAsync`, route automatically:
```csharp
if (device.Family == DeviceFamily.Vega)
{
    return await StartVegaPreviewStreamAsync(device.MacAddress, multicastIp, port, ct);
}
return await StartPreviewStreamViaAvpRs232Async(device.MacAddress, multicastIp, ct);
```

- [x] **Step 4: Run test to verify it passes**
Run: `dotnet test tests/AvasRoutingApp.Tests/ --filter FullyQualifiedName~VegaPreviewPipelineTests`
Expected: PASS.

- [x] **Step 5: Commit**
```bash
git add src/AvasRoutingApp/Sdvoe/SdvoeClient.cs src/AvasRoutingApp/Sdvoe/ISdvoeDiscoveryService.cs tests/AvasRoutingApp.Tests/VegaPreviewPipelineTests.cs
git commit -m "feat: implement pure SDVoE preview streaming for VEGA-1100"
```

---

### Task 3: AVAS Hardware Diagnostics & Factory Reset Commands

**Files:**
- Modify: `src/AvasRoutingApp/Sdvoe/SdvoeClient.cs`
- Modify: `src/AvasRoutingApp/Sdvoe/IMulticastController.cs`
- Test: `tests/AvasRoutingApp.Tests/AvasDiagnosticsCommandTests.cs`

**Interfaces:**
- Produces:
  - `SdvoeClient.FactoryResetAvpAsync(AvasDevice device, string targetMac, CancellationToken)`: `Task<bool>`
  - `SdvoeClient.FactoryResetMcuAsync(AvasDevice device, CancellationToken)`: `Task<bool>`
  - `SdvoeClient.RebootChipAsync(AvasDevice device, string targetMac, CancellationToken)`: `Task<bool>`

- [x] **Step 1: Write failing unit test for AVAS diagnostics commands**
Create `tests/AvasRoutingApp.Tests/AvasDiagnosticsCommandTests.cs` validating:
1. `FactoryResetAvpAsync` sends `factory <targetMac>\r`.
2. `FactoryResetMcuAsync` sends `send <chip0Mac> rs232:1 set factory default\r`.
3. Calling these on a VEGA or non-AVAS device throws `InvalidOperationException` and does not transmit commands.

- [x] **Step 2: Run test to verify failure**
Run: `dotnet test tests/AvasRoutingApp.Tests/ --filter FullyQualifiedName~AvasDiagnosticsCommandTests`
Expected: FAIL.

- [x] **Step 3: Implement maintenance methods in `SdvoeClient.cs`**
Enforce `if (device.Family != DeviceFamily.Avas) throw new InvalidOperationException("Maintenance commands are restricted to AVAS devices.");` and dispatch Telnet commands.

- [x] **Step 4: Run test to verify it passes**
Run: `dotnet test tests/AvasRoutingApp.Tests/ --filter FullyQualifiedName~AvasDiagnosticsCommandTests`
Expected: PASS.

- [x] **Step 5: Commit**
```bash
git add src/AvasRoutingApp/Sdvoe/SdvoeClient.cs src/AvasRoutingApp/Sdvoe/IMulticastController.cs tests/AvasRoutingApp.Tests/AvasDiagnosticsCommandTests.cs
git commit -m "feat: implement AVAS-only AVP and MCU factory reset commands"
```

---

### Task 4: UI Updates - Settings Dialog & Encoder Card Badge

**Files:**
- Modify: `src/AvasRoutingApp/Views/SettingsDialog.xaml`
- Modify: `src/AvasRoutingApp/Views/SettingsDialog.xaml.cs`
- Modify: `src/AvasRoutingApp/ViewModels/EncoderCardViewModel.cs`
- Modify: `src/AvasRoutingApp/Views/EncoderCardView.xaml`
- Modify: `src/AvasRoutingApp/ViewModels/MainViewModel.cs`
- Test: `tests/AvasRoutingApp.Tests/EncoderCardViewModelTests.cs`

**Interfaces:**
- Consumes: `AppConfig.EnableVegaDevices`, `AvasDevice.Family`
- Produces:
  - `EncoderCardViewModel.IsVegaDevice`: `bool`
  - `EncoderCardViewModel.BadgeText`: `string`
  - `EncoderCardViewModel.IsMaintenanceAvailable`: `bool`

- [x] **Step 1: Write unit tests for EncoderCardViewModel properties**
Verify `IsVegaDevice == true`, `BadgeText == "[VEGA-1100 (Test)]"`, and `IsMaintenanceAvailable == false` for VEGA devices, and `IsMaintenanceAvailable == true` for AVAS devices.

- [x] **Step 2: Implement ViewModel properties and XAML badge binding**
1. Update `EncoderCardViewModel.cs` with `BadgeText`, `IsVegaDevice`, and `IsMaintenanceAvailable`.
2. In `EncoderCardView.xaml`, bind header badge to `BadgeText` with styled pill background for VEGA devices.
3. In `SettingsDialog.xaml`, add "Enable VEGA Devices" toggle bound to config.

- [x] **Step 3: Verify tests pass**
Run: `dotnet test tests/AvasRoutingApp.Tests/ --filter FullyQualifiedName~EncoderCardViewModelTests`
Expected: PASS.

- [x] **Step 4: Commit**
```bash
git add src/AvasRoutingApp/Views/SettingsDialog.xaml src/AvasRoutingApp/Views/SettingsDialog.xaml.cs src/AvasRoutingApp/ViewModels/EncoderCardViewModel.cs src/AvasRoutingApp/Views/EncoderCardView.xaml src/AvasRoutingApp/ViewModels/MainViewModel.cs tests/AvasRoutingApp.Tests/EncoderCardViewModelTests.cs
git commit -m "feat: add Enable VEGA Devices setting toggle and VEGA-1100 UI badge"
```

---

### Task 5: Advanced Diagnostics Maintenance Dialog (Dual-Chip Targeting)

**Files:**
- Create: `src/AvasRoutingApp/ViewModels/MaintenanceViewModel.cs`
- Create: `src/AvasRoutingApp/Views/MaintenanceDialog.xaml`
- Create: `src/AvasRoutingApp/Views/MaintenanceDialog.xaml.cs`
- Modify: `src/AvasRoutingApp/ViewModels/EncoderCardViewModel.cs`
- Test: `tests/AvasRoutingApp.Tests/MaintenanceViewModelTests.cs`

**Interfaces:**
- Consumes: `AvasDevice.MacAddress`, `AvasDevice.CompanionMac`, `SdvoeClient` maintenance methods
- Produces:
  - `MaintenanceViewModel.TargetChipSelection`: `enum { Chip0, Chip1, Both }`
  - `MaintenanceViewModel.FactoryResetCommand`: `ICommand`
  - `MaintenanceViewModel.McuResetCommand`: `ICommand`
  - `MaintenanceViewModel.RebootCommand`: `ICommand`

- [x] **Step 1: Write unit tests for MaintenanceViewModel**
Verify:
1. Target selection defaults to `Chip0`.
2. Selecting `Chip1` is disabled if `CompanionMac == "NONE"`.
3. Executing Factory Reset on `Both` calls `FactoryResetAvpAsync` on Chip 0 then Chip 1.
4. MCU reset always passes Chip 0 MAC.

- [x] **Step 2: Create MaintenanceViewModel and MaintenanceDialog XAML**
1. Implement `MaintenanceViewModel.cs` with commands, status log output, and confirmation callbacks.
2. Build `MaintenanceDialog.xaml` with styled chip selector radio buttons, action buttons, and live log console.
3. Wire "Diagnostics" button in `EncoderCardView.xaml` to open `MaintenanceDialog`.

- [x] **Step 3: Run tests to verify**
Run: `dotnet test tests/AvasRoutingApp.Tests/ --filter FullyQualifiedName~MaintenanceViewModelTests`
Expected: PASS.

- [x] **Step 4: Commit**
```bash
git add src/AvasRoutingApp/ViewModels/MaintenanceViewModel.cs src/AvasRoutingApp/Views/MaintenanceDialog.xaml src/AvasRoutingApp/Views/MaintenanceDialog.xaml.cs src/AvasRoutingApp/ViewModels/EncoderCardViewModel.cs tests/AvasRoutingApp.Tests/MaintenanceViewModelTests.cs
git commit -m "feat: create AVAS advanced diagnostics dialog with dual-chip selection"
```

---

### Task 6: Comprehensive Regression & End-to-End Verification

**Files:**
- Test: `tests/AvasRoutingApp.Tests/` (all test suites)

- [x] **Step 1: Run full automated test suite**
Run: `dotnet test tests/AvasRoutingApp.Tests/ -v normal`
Expected: All tests pass with zero regressions.

- [x] **Step 2: Build complete solution**
Run: `dotnet build src/AvasRoutingApp/AvasRoutingApp.csproj -c Release`
Expected: Build succeeds with 0 errors.

- [x] **Step 3: Commit final integration touches**
```bash
git commit -am "chore: verify full test suite and clean release build for bump-up version"
```
