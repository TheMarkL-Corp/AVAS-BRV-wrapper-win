# [Feature] Universal AVAS Diagnostics (TX & RX) & Activation Badge Resolution

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Allow users to execute AVAS Hardware Diagnostics & Maintenance (silicon factory reset, MCU factory reset, chip reboot) on **both AVAS Transmitters (TX) and Receivers (RX)**, while fixing the misleading "Activated" badge on VEGA and the permanent "Checking..." status on AVAS TX cards.

**Architecture:** 
1. **Dedicated Diagnostics / Maintenance Tab in Sidebar:** Add a 4th top-level segmented tab (`[ 📺 Previews ] [ 🔗 Multi-Link ] [ 🌡️ Thermals ] [ ⚙️ Diagnostics ]`) in `OverlaySidebarView` that displays all discovered AVAS endpoints grouped into TX Encoders and RX Decoders. Each card provides device telemetry, IP, link mode, chip targeting, and a prominent `[ ⚙ Diag ]` button strictly enabled for AVAS devices (VID 105).
2. **Contextual Diag Buttons:** Retain `[ ⚙ Diag ]` on TX preview cards and add `[ ⚙ Diag ]` to `DeviceTemperatureCardView` so users in the Thermals tab can jump directly into diagnostics for any overheating or misbehaving TX or RX.
3. **MaintenanceDialog Device Selector:** Add a target device dropdown in `MaintenanceDialog` allowing on-the-fly switching between any discovered AVAS TX or RX endpoint without reopening the dialog.
4. **Activation Badge Fix:** Disassociate VEGA from AVAS activation (hide activation badge or display "N/A" for VEGA); resolve AVAS activation accurately by properly capturing MCU serial telemetry.

**Tech Stack:** WPF (.NET 8.0), MVVM, XAML, C#, Semtech BlueRiver SDVoE API, Advantech VoIP SDK (VOIPS.dll).

**Spec References:**
- `src/AvasRoutingApp/Views/OverlaySidebarView.xaml`
- `src/AvasRoutingApp/Views/DeviceTemperatureCardView.xaml`
- `src/AvasRoutingApp/Views/MaintenanceDialog.xaml`
- `src/AvasRoutingApp/ViewModels/MainViewModel.cs`
- `src/AvasRoutingApp/ViewModels/MaintenanceViewModel.cs`
- `src/AvasRoutingApp/ViewModels/EncoderCardViewModel.cs`
- `src/AvasRoutingApp/ViewModels/DeviceTemperatureCardViewModel.cs`

---

## Global Constraints

- Hardware diagnostics and factory resets are strictly restricted to **AVAS devices (Vendor ID 105)**; VEGA endpoints must have diagnostics disabled.
- The dual-link architecture and preview stream decoding must remain completely undisturbed.
- All newly added UI elements must respect theme switching (`ThemeBackgroundBrush`, `ThemeAccentBrush`, etc.) and the user's custom `SidebarFontScale`.
- Zero regressions on the 534 existing unit, integration, and E2E tests.

---

## Review Focus

1. **RX Device Discovery & Representation:** RX devices have no thumbnail video stream, so they must be populated from the SDVoE device inventory without attempting to bind RTP video channels.
2. **Chip 0 vs Chip 1 Addressing on RX:** Multi-link RX decoders also have companion chips; ensuring factory reset and reboot commands correctly address RX Chip 0 vs RX Chip 1.
3. **Theme and Scaling Consistency:** 4 segmented tabs in the sidebar header must not truncate or wrap awkwardly on standard 380px sidebar width.
4. **False Positive Activation on VEGA:** VEGA cards must never show "Activated" or "Unactivated", as VEGA lacks CYP authorization.
5. **Thread Safety in Maintenance Actions:** Dialog actions must handle async execution and display status logs cleanly without locking the UI.

---

## Tasks

### Task 1: Activation Badge Discrepancy & VEGA Badge Normalization

**Files:**
- Modify: `src/AvasRoutingApp/ViewModels/MainViewModel.cs`
- Modify: `src/AvasRoutingApp/ViewModels/EncoderCardViewModel.cs`
- Modify: `src/AvasRoutingApp/Views/EncoderCardView.xaml`
- Modify: `tests/AvasRoutingApp.Tests/EncoderCardViewModelTests.cs`
- Modify: `tests/AvasRoutingApp.Tests/VegaPreviewPipelineTests.cs`

- [ ] **Step 1.1: Write failing test for VEGA activation badge suppression**
  In `EncoderCardViewModelTests.cs`, assert that when `Family == DeviceFamily.Vega`, `ActivationBadgeText` is empty or "N/A", and `ShowActivationBadge` is `false`.
- [ ] **Step 1.2: Update `EncoderCardViewModel`**
  Add property `public bool ShowActivationBadge => Family == DeviceFamily.Avas;`. When `Family != DeviceFamily.Avas`, suppress the activation badge in `EncoderCardView.xaml`.
- [ ] **Step 1.3: Fix `MainViewModel.cs:613` initial activation assignment**
  Change line 613 from hardcoded `dev.Family == DeviceFamily.Vega ? DeviceActivationState.Activated : DeviceActivationState.Unknown` to `DeviceActivationState.Unknown`.
- [ ] **Step 1.4: Resolve AVAS MCU Telemetry & Activation Query**
  In `MainViewModel.cs`, integrate `VOIPS.dll`'s tested `voips.GET_MCU_VERSION(mac)` and `voips.GET_MCU_IP(mac)` as a robust fast-path for MCU telemetry, ensuring AVAS TX devices populate valid firmware/hardware versions and activation state rather than hanging on "Checking...".
- [ ] **Step 1.5: Run tests and verify**
  Verify with `dotnet test --filter "FullyQualifiedName~EncoderCard"` and commit.

---

### Task 2: Diagnostics Model & Data Layer for RX and TX Endpoints

**Files:**
- Create/Modify: `src/AvasRoutingApp/ViewModels/MaintenanceCardViewModel.cs`
- Modify: `src/AvasRoutingApp/ViewModels/MainViewModel.cs`
- Modify: `src/AvasRoutingApp/ViewModels/DeviceTemperatureCardViewModel.cs`
- Modify: `tests/AvasRoutingApp.Tests/MaintenanceViewModelTests.cs`

- [ ] **Step 2.1: Create `MaintenanceCardViewModel`**
  A dedicated lightweight ViewModel representing an AVAS TX or RX endpoint in the Diagnostics panel:
  - Properties: `DeviceName`, `MacAddress`, `IpAddress`, `IsTransmitter`, `IsReceiver`, `DeviceTypeBadge` ("TX" / "RX"), `LinkMode` ("SINGLE" / "DUAL"), `CompanionMac`, `FirmwareVersion`, `HardwareVersion`, `IsMaintenanceAvailable` (`Family == DeviceFamily.Avas`).
  - Command: `OpenDiagnosticsCommand` which invokes `MaintenanceViewModel` with the underlying `AvasDevice`.
- [ ] **Step 2.2: Add ObservableCollection of Diagnostics Cards in `MainViewModel`**
  In `MainViewModel`, maintain `ObservableCollection<MaintenanceCardViewModel> MaintenanceCards` and filtered lists `FilteredTxMaintenanceCards` and `FilteredRxMaintenanceCards`.
  Populate both TX and RX AVAS devices discovered from SDVoE Control Server.
- [ ] **Step 2.3: Wire Diag Action on `DeviceTemperatureCardViewModel`**
  Add `OpenDiagnosticsCommand` and `IsMaintenanceAvailable` to `DeviceTemperatureCardViewModel` so RX and TX cards in the Thermals tab can also trigger the Maintenance dialog directly.
- [ ] **Step 2.4: Write unit tests for TX & RX MaintenanceCard creation**
  Assert that both TX (chip_0) and RX (chip_0) AVAS devices populate `MaintenanceCards` and correctly configure the maintenance action callback.
- [ ] **Step 2.5: Run tests and commit**

---

### Task 3: Dedicated Diagnostics Tab in Sidebar UI (`OverlaySidebarView.xaml`)

**Files:**
- Modify: `src/AvasRoutingApp/Views/OverlaySidebarView.xaml`
- Modify: `src/AvasRoutingApp/Views/OverlaySidebarView.xaml.cs`
- Modify: `src/AvasRoutingApp/ViewModels/MainViewModel.cs`
- Modify: `src/AvasRoutingApp/Views/DeviceTemperatureCardView.xaml`

- [ ] **Step 3.1: Add 4th Segmented Tab in Sidebar Header**
  In `OverlaySidebarView.xaml`, expand the tab grid to 4 columns:
  - Tab 1: `Previews` (`IconGeometryPreviews`)
  - Tab 2: `Multi-Link` (`IconGeometryMultiLink`)
  - Tab 3: `Thermals` (`IconGeometryThermals`)
  - Tab 4: `Diagnostics` (Gear icon `IconGeometrySettings` or wrench icon)
  Style with crisp SVG icons, tooltips, active/inactive highlight states, and responsive labels.
- [ ] **Step 3.2: Implement `IsDiagnosticsTabSelected` in `MainViewModel`**
  Add `SelectDiagnosticsTabCommand` to switch view mode, updating header status text and active tab indicators.
- [ ] **Step 3.3: Implement Diagnostics Tab Body View**
  Create scrollable categorized sections for:
  - **AVAS Transmitters (TX)** — Cards with Name, MAC, IP, Link status, and `[ ⚙ Diag ]` button.
  - **AVAS Receivers (RX)** — Cards with Name, MAC, IP, Link status, and `[ ⚙ Diag ]` button.
  - Sub-filter pills: `[ All ]`, `[ TX ]`, `[ RX ]` matching the clean style from the Thermals tab.
- [ ] **Step 3.4: Add `[ ⚙ Diag ]` Button to `DeviceTemperatureCardView.xaml`**
  Place a clean secondary `[ ⚙ Diag ]` button on each AVAS temperature card header, giving immediate maintenance access from the Thermals screen.
- [ ] **Step 3.5: Run tests and verify visual styling**
  Run `dotnet test` to confirm XAML bindings and commands compile cleanly without errors.

---

### Task 4: MaintenanceDialog Device Switcher & RX Validation

**Files:**
- Modify: `src/AvasRoutingApp/ViewModels/MaintenanceViewModel.cs`
- Modify: `src/AvasRoutingApp/Views/MaintenanceDialog.xaml`
- Modify: `tests/AvasRoutingApp.Tests/MaintenanceDialogTests.cs`

- [ ] **Step 4.1: Add Device Picker to `MaintenanceViewModel`**
  Support passing a list of available AVAS devices to `MaintenanceViewModel` so a ComboBox in `MaintenanceDialog` can switch the active target device (e.g. from TX2_DUAL to RX1_DUAL) without dismissing and reopening the dialog.
- [ ] **Step 4.2: Update `MaintenanceDialog.xaml` UI**
  Add target device header with device type indicator (`[TX]` blue badge, `[RX]` violet badge), MAC address, and device selector.
- [ ] **Step 4.3: Write automated tests in `MaintenanceDialogTests.cs`**
  Test initializing with an RX device, verifying Chip 0 / Chip 1 MAC resolution, and executing diagnostic commands on RX targets.
- [ ] **Step 4.4: Run full test suite**
  Execute `dotnet test` to ensure all 534+ tests pass.

---

### Task 5: End-to-End Build, Verification & Release Packaging

**Files:**
- Build & Publish: `src/AvasRoutingApp/bin/Release/net8.0-windows/`
- Release Package: `releases/AVAS-Routing-SW-v1.5.0-win-x64/`

- [ ] **Step 5.1: Run full test suite across solution**
  Confirm 100% green tests across `E2ETests` and `AvasRoutingApp.Tests`.
- [ ] **Step 5.2: Build in Release configuration**
  `dotnet build src/AvasRoutingApp/AvasRoutingApp.csproj -c Release`
- [ ] **Step 5.3: Publish standalone distribution**
  `dotnet publish src/AvasRoutingApp/AvasRoutingApp.csproj -c Release -r win-x64 --no-self-contained -o "releases/AVAS-Routing-SW-v1.5.0-win-x64"`
- [ ] **Step 5.4: Live verification on physical hardware**
  Verify that AVAS TX and RX devices are both listed in the Diagnostics tab, the Diag dialog operates seamlessly on both, and the VEGA card displays cleanly without the false "Activated" badge.
