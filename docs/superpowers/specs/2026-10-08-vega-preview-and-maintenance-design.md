# Design Document: VEGA-1100 Preview Support & AVAS Advanced Diagnostics

**Date:** 2026-10-08  
**Target Repository:** `AVAS-BRV-wrapper-win`  
**Status:** Approved  

---

## 1. Overview & Problem Statement

The next bump-up version of `AvasRoutingApp` addresses two key system capabilities:

1. **Experimental VEGA-1100 Discovery & Preview**:
   - Advantech VEGA-1100 is an SDVoE transceiver based on the **Semtech BlueRiver AVP2000T** silicon (Vendor ID: 192, Product ID: 11).
   - Because it is manufactured by Advantech (not CYP), it cannot use the proprietary CYP VOIPS MCU UART commands (`set rtp igmp` on UART 1).
   - The app must support discovering VEGA-1100 (gated behind an **"Enable VEGA Devices"** setting) and streaming its preview using standard BlueRiver SDVoE thumbnail commands (`set <mac> thumbnail ssrc 100 fps 20 udp <ip>:<port>`), while rendering a distinct `[VEGA-1100 (Test)]` badge.

2. **AVAS Advanced Diagnostics & Hardware Commanding**:
   - Reverse-engineered from `Dual Link SDVoE Tester V1.16`:
     - **AVP Factory Reset**: Issues native BlueRiver `factory <mac>\r` directly to the Semtech AVP silicon.
     - **MCU Factory Reset**: Issues `send <mac> rs232:1 set factory default\r` via BlueRiver UART tunnel 1 to the onboard MCU.
     - **Hardware Reboot**: Issues `reboot <mac>\r`.
   - **Independent Dual-Chip Commanding**: Allows selecting **Chip 0 (AVP0)**, **Chip 1 (AVP1)**, or **Both Chips** independently on dual-link AVAS devices. MCU commands are strictly locked to route through Chip 0 (where the physical UART trace is wired).
   - **Strict AVAS Guardrail**: This maintenance panel is **exclusive to AVAS devices** (Vendor ID 105). VEGA-1100 and 3rd party hardware are strictly prevented from executing or accessing these commands.

---

## 2. Architecture & Data Model

### 2.1 Configuration
File: `src/AvasRoutingApp/Configuration/AppConfig.cs`
- Add property:
  ```csharp
  public bool EnableVegaDevices { get; set; } = false;
  ```
- Persisted in `appsettings.json`.

### 2.2 Device Identification & Family
File: `src/AvasRoutingApp/Sdvoe/AvasDevice.cs`
- Add enum:
  ```csharp
  public enum DeviceFamily
  {
      Avas,
      Vega,
      GenericSdvoe
  }
  ```
- Add property:
  ```csharp
  public DeviceFamily Family => VendorId switch
  {
      105 => DeviceFamily.Avas,
      192 when ProductId == 11 => DeviceFamily.Vega,
      _ => DeviceFamily.GenericSdvoe
  };
  ```

### 2.3 Discovery Filtering
File: `src/AvasRoutingApp/Sdvoe/SdvoeDiscoveryFilter.cs`
- Predicates:
  - `IsTargetAvas223Tx(AvasDevice device)`: `VendorId == 105 && ProductId == 81 && IsTransmitter && !IsReceiver && ChipIndex == 0`
  - `IsTargetVega1100(AvasDevice device)`: `VendorId == 192 && ProductId == 11 && IsTransmitter && ChipIndex == 0`
- Method:
  ```csharp
  public static IReadOnlyList<AvasDevice> FilterTargetDevices(IEnumerable<AvasDevice> devices, bool enableVegaDevices)
  {
      if (devices == null) return Array.Empty<AvasDevice>();
      return devices.Where(d => IsTargetAvas223Tx(d) || (enableVegaDevices && IsTargetVega1100(d))).ToList();
  }
  ```

---

## 3. Communication & Preview Pipeline

### 3.1 VEGA Preview Streaming
File: `src/AvasRoutingApp/Sdvoe/SdvoeClient.cs`
- Add pure SDVoE preview method:
  ```csharp
  public async Task<bool> StartVegaPreviewStreamAsync(string mac, string multicastIp, int port, CancellationToken ct = default);
  public async Task<bool> StopVegaPreviewStreamAsync(string mac, CancellationToken ct = default);
  ```
- Workflow:
  1. Issues BlueRiver API command: `set <mac> thumbnail ssrc 100 fps 20 udp <multicastIp>:<port>` (via Telnet or REST).
  2. Bypasses CYP MCU UART (no `set rtp igmp` sent).
  3. RTP reception and YUV422 rasterization in `Rtp/UnifiedRtpDemuxReceiver` process the stream identically.

### 3.2 Maintenance Hardware Commands
File: `src/AvasRoutingApp/Sdvoe/SdvoeClient.cs`
- Guardrail: Throws `InvalidOperationException` if called on non-AVAS device (`VendorId != 105`).
- Methods:
  ```csharp
  public async Task<bool> FactoryResetAvpAsync(string targetMac, CancellationToken ct = default);
  public async Task<bool> FactoryResetMcuAsync(string chip0Mac, CancellationToken ct = default);
  public async Task<bool> RebootChipAsync(string targetMac, CancellationToken ct = default);
  ```

---

## 4. UI Components & Presentation

### 4.1 Settings Dialog (`SettingsDialog.xaml`)
- Toggle switch / checkbox for **"Enable VEGA Devices"**.
- Triggers immediate device rediscovery upon saving.

### 4.2 Device Card (`EncoderCardView.xaml`)
- Header badge:
  - If `DeviceFamily == Vega`: shows badge `[VEGA-1100 (Test)]`.
  - If `DeviceFamily == Avas`: shows badge `AVAS-223`.
- Diagnostics button:
  - Visible **only** for `DeviceFamily == Avas`.

### 4.3 Advanced Diagnostics Maintenance Dialog (`MaintenanceDialog.xaml`)
- Exclusively available for AVAS devices.
- Target selector:
  - 🔘 `Chip 0 (AVP0)` [MAC]
  - 🔘 `Chip 1 (AVP1)` [MAC] (Enabled only if dual-link companion exists)
  - 🔘 `Both Chips (Dual Link)`
- Action buttons:
  - `Factory Reset (AVP)`
  - `MCU Factory Reset` (hint: routed through Chip 0)
  - `Reboot Device`
- Safety confirmation modal before executing any factory reset action.
- Status log output window for command responses.

---

## 5. Verification Plan

### Automated Tests
- `SdvoeDiscoveryFilterTests`:
  - Verify AVAS-223 Tx always included.
  - Verify VEGA-1100 included when `EnableVegaDevices == true`.
  - Verify VEGA-1100 excluded when `EnableVegaDevices == false`.
  - Verify secondary chips (ChipIndex == 1) and receivers are excluded from primary discovery.
- `SdvoeClientCommandTests`:
  - Verify `FactoryResetAvpAsync` sends `factory <mac>\r`.
  - Verify `FactoryResetMcuAsync` sends `send <chip0Mac> rs232:1 set factory default\r`.
  - Verify `StartVegaPreviewStreamAsync` sends native BlueRiver thumbnail command.
  - Verify non-AVAS device guardrail prevents maintenance command execution.

### Manual Verification
- Toggle "Enable VEGA Devices" in Settings and verify discovery refresh.
- Check badge rendering on simulated or physical VEGA device.
- Open Diagnostics Dialog on AVAS-223, test selecting Chip 0 vs Chip 1, and verify confirmation modal.
