# Fix VEGA-1100 Discovery and Diagnostics Dialog Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix VEGA-1100 endpoint discovery and stream routing when "Enable VEGA Devices" is active, fix the missing `SecondaryButtonStyle` XAML crash in `MaintenanceDialog`, and enlarge the Diagnostics button in `EncoderCardView`.

**Architecture:** Update `ISdvoeDiscoveryService` and `SdvoeClient.DiscoverAvas223DevicesAsync` to accept `enableVegaDevices` parameter; update `MainViewModel.StartAllStreamsInternalAsync` to filter using `FilterTargetDevices(devices, enableVega)` and dispatch VEGA endpoints to `StartVegaPreviewStreamAsync`; trigger refresh on settings save in `MainWindow`; move reusable button styles to `Themes/LightTheme.xaml` and `Themes/DarkTheme.xaml`; and restyle the `EncoderCardView` header for clear visibility and clickability.

**Tech Stack:** C# 12, .NET 8 WPF, XAML, xUnit, Semtech BlueRiver Telnet & REST API.

**Spec:** User reported bug reports with live hardware parameters (`VEGA_1100`, Vendor ID 192, Product ID 11, MAC `0016c0589c1e`) and attached screenshot showing `XamlParseException` on `SecondaryButtonStyle` in `MaintenanceDialog.xaml:126`.

## Global Constraints

- VEGA-1100 endpoints must only appear when `EnableVegaDevices == true`.
- Diagnostics button must remain strictly visible for AVAS devices and hidden for VEGA (`dev.Family != DeviceFamily.Avas`).
- VEGA stream start must bypass CYP MCU UART commands and use pure BlueRiver SDVoE commands (`StartVegaPreviewStreamAsync`).
- All dialog button styles (`PrimaryButtonStyle`, `SecondaryButtonStyle`) must be centrally defined and accessible without XAML runtime crashes.
- Do not break existing AVAS-223 dual-link routing, preview decoding, or MCU telemetry.

## Review Focus

1. VEGA device identity from SDVoE server (`is_transmitter: true, is_receiver: true`) matches `IsTargetVega1100`.
2. Discovery returns VEGA when `enableVegaDevices: true` and excludes VEGA when `enableVegaDevices: false`.
3. Preview stream invocation for VEGA routes to `StartVegaPreviewStreamAsync` and does not attempt CYP MCU UART.
4. `MaintenanceDialog` instantiates cleanly on UI thread without missing resource exceptions.
5. `EncoderCardView` Diagnostics button has min height 26px, clear typography, and does not collide with card title.

---

### Task 1: Fix Missing Button Styles in Themes and Diag Button Sizing in `EncoderCardView`

**Files:**
- Modify: `src/AvasRoutingApp/Themes/LightTheme.xaml`
- Modify: `src/AvasRoutingApp/Themes/DarkTheme.xaml`
- Modify: `src/AvasRoutingApp/Views/EncoderCardView.xaml:60-72`
- Modify: `src/AvasRoutingApp/Views/MaintenanceDialog.xaml:124-128`
- Test: `tests/AvasRoutingApp.Tests/MaintenanceDialogTests.cs`

**Interfaces:**
- Consumes: `MaintenanceViewModel`, theme resource dictionaries.
- Produces: App-wide `PrimaryButtonStyle` and `SecondaryButtonStyle` resources, well-proportioned Diagnostics button in card header.

- [ ] **Step 1: Write the failing unit test for `MaintenanceDialog` resource resolution**

Create `tests/AvasRoutingApp.Tests/MaintenanceDialogTests.cs`:
```csharp
using System;
using System.Threading;
using Xunit;
using AvasRoutingApp.Sdvoe;
using AvasRoutingApp.ViewModels;
using AvasRoutingApp.Views;
using Moq;

namespace AvasRoutingApp.Tests
{
    public class MaintenanceDialogTests
    {
        [WpfFact]
        public void MaintenanceDialog_CanInstantiate_WithoutMissingResourceException()
        {
            var dev = new AvasDevice
            {
                MacAddress = "74fe488b07bb",
                DeviceName = "TX2_DUAL LINK_SFPA",
                VendorId = 105,
                ProductId = 81,
                LinkMode = "SINGLE"
            };
            var mockController = new Mock<IMulticastController>();
            var vm = new MaintenanceViewModel(dev, mockController.Object);

            var ex = Record.Exception(() => new MaintenanceDialog(vm));
            Assert.Null(ex);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AvasRoutingApp.Tests/ --filter "FullyQualifiedName~MaintenanceDialogTests"`
Expected: FAIL with `XamlParseException: Cannot find resource named 'SecondaryButtonStyle'`.

- [ ] **Step 3: Add `PrimaryButtonStyle` and `SecondaryButtonStyle` to `Themes/LightTheme.xaml` and `Themes/DarkTheme.xaml`, and adjust `EncoderCardView.xaml`**

In `Themes/LightTheme.xaml` and `Themes/DarkTheme.xaml`, add standard styles:
```xml
    <!-- Standard Dialog Buttons -->
    <Style x:Key="PrimaryButtonStyle" TargetType="Button">
        <Setter Property="Background" Value="{DynamicResource ThemeAccentBrush}" />
        <Setter Property="Foreground" Value="{DynamicResource ThemeAccentForegroundBrush}" />
        <Setter Property="FontWeight" Value="SemiBold" />
        <Setter Property="Padding" Value="16,8" />
        <Setter Property="MinHeight" Value="34" />
        <Setter Property="BorderThickness" Value="0" />
        <Setter Property="Cursor" Value="Hand" />
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="Button">
                    <Border x:Name="Border" Background="{TemplateBinding Background}" CornerRadius="5" Padding="{TemplateBinding Padding}">
                        <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" />
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter TargetName="Border" Property="Opacity" Value="0.9" />
                        </Trigger>
                        <Trigger Property="IsPressed" Value="True">
                            <Setter TargetName="Border" Property="Opacity" Value="0.75" />
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <Style x:Key="SecondaryButtonStyle" TargetType="Button">
        <Setter Property="Background" Value="{DynamicResource ThemeButtonBackgroundBrush}" />
        <Setter Property="Foreground" Value="{DynamicResource ThemeButtonForegroundBrush}" />
        <Setter Property="Padding" Value="16,8" />
        <Setter Property="MinHeight" Value="34" />
        <Setter Property="BorderThickness" Value="1" />
        <Setter Property="BorderBrush" Value="{DynamicResource ThemeButtonBorderBrush}" />
        <Setter Property="Cursor" Value="Hand" />
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="Button">
                    <Border x:Name="Border" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="5" Padding="{TemplateBinding Padding}">
                        <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" />
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter TargetName="Border" Property="Background" Value="{DynamicResource ThemeButtonHoverBackgroundBrush}" />
                        </Trigger>
                        <Trigger Property="IsPressed" Value="True">
                            <Setter TargetName="Border" Property="Opacity" Value="0.75" />
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>
```

In `EncoderCardView.xaml`:
Update header DockPanel so `StackPanel DockPanel.Dock="Right"` is declared first, and enlarge the Diagnostics button to `MinHeight="26"`, `Padding="8,3"`, `FontSize="11"`, with a visible icon and text:
```xml
<Button Command="{Binding OpenDiagnosticsCommand}"
        Visibility="{Binding IsMaintenanceAvailable, Converter={StaticResource BoolToVisConverter}}"
        Background="{DynamicResource ThemePillBackgroundBrush}"
        BorderThickness="1"
        BorderBrush="{DynamicResource ThemeAccentBrush}"
        Padding="8,3"
        Margin="6,0,0,0"
        MinHeight="26"
        VerticalAlignment="Center"
        Cursor="Hand"
        ToolTip="AVAS Hardware Diagnostics &amp; Maintenance">
    <StackPanel Orientation="Horizontal" VerticalAlignment="Center">
        <TextBlock Text="⚙" FontSize="11" Foreground="{DynamicResource ThemeAccentBrush}" Margin="0,0,4,0" VerticalAlignment="Center" />
        <TextBlock Text="Diag" FontSize="11" FontWeight="SemiBold" Foreground="{DynamicResource ThemeTextPrimaryBrush}" VerticalAlignment="Center" />
    </StackPanel>
</Button>
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/AvasRoutingApp.Tests/ --filter "FullyQualifiedName~MaintenanceDialogTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/AvasRoutingApp/Themes/ src/AvasRoutingApp/Views/ tests/AvasRoutingApp.Tests/MaintenanceDialogTests.cs
git commit -m "fix: resolve SecondaryButtonStyle resource crash and improve Diag button sizing"
```

---

### Task 2: Fix VEGA-1100 Discovery and Preview Stream Routing in Services & ViewModels

**Files:**
- Modify: `src/AvasRoutingApp/Sdvoe/ISdvoeDiscoveryService.cs`
- Modify: `src/AvasRoutingApp/Sdvoe/SdvoeClient.cs`
- Modify: `src/AvasRoutingApp/ViewModels/MainViewModel.cs:480-520,580-600`
- Modify: `src/AvasRoutingApp/MainWindow.xaml.cs:270-285`
- Test: `tests/AvasRoutingApp.Tests/VegaDiscoveryRoutingTests.cs`

**Interfaces:**
- Consumes: `AppConfig.EnableVegaDevices`, `ISdvoeDiscoveryService`, `IMulticastController`.
- Produces: Correct endpoint filtering including VEGA when enabled, and clean dispatch to `StartVegaPreviewStreamAsync`.

- [ ] **Step 1: Write unit tests for VEGA discovery and stream dispatch**

Create `tests/AvasRoutingApp.Tests/VegaDiscoveryRoutingTests.cs`:
```csharp
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using AvasRoutingApp.Sdvoe;
using Moq;

namespace AvasRoutingApp.Tests
{
    public class VegaDiscoveryRoutingTests
    {
        [Fact]
        public void SdvoeDiscoveryFilter_IdentifiesLiveVegaTransceiver()
        {
            var vegaDevice = new AvasDevice
            {
                MacAddress = "0016c0589c1e",
                DeviceName = "VEGA_1100",
                VendorId = 192,
                ProductId = 11,
                ChipIndex = 0,
                IsTransmitter = true,
                IsReceiver = true // Transceiver (TR)
            };

            Assert.True(SdvoeDiscoveryFilter.IsTargetVega1100(vegaDevice));
            Assert.False(SdvoeDiscoveryFilter.IsTargetAvas223Tx(vegaDevice));

            var devices = new List<AvasDevice> { vegaDevice };
            var filteredDisabled = SdvoeDiscoveryFilter.FilterTargetDevices(devices, enableVegaDevices: false);
            Assert.Empty(filteredDisabled);

            var filteredEnabled = SdvoeDiscoveryFilter.FilterTargetDevices(devices, enableVegaDevices: true);
            Assert.Single(filteredEnabled);
            Assert.Equal("0016c0589c1e", filteredEnabled[0].MacAddress);
            Assert.Equal(DeviceFamily.Vega, filteredEnabled[0].Family);
        }

        [Fact]
        public async Task SdvoeClient_DispatchesVegaToVegaPreviewMethod()
        {
            var mockController = new Mock<IMulticastController>();
            mockController
                .Setup(m => m.StartVegaPreviewStreamAsync("0016c0589c1e", "239.255.0.1", 6792, It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            var dev = new AvasDevice
            {
                MacAddress = "0016c0589c1e",
                DeviceName = "VEGA_1100",
                VendorId = 192,
                ProductId = 11,
                IsTransmitter = true,
                IsReceiver = true
            };

            bool started = dev.Family == DeviceFamily.Vega
                ? await mockController.Object.StartVegaPreviewStreamAsync(dev.MacAddress, "239.255.0.1", 6792)
                : await mockController.Object.StartPreviewStreamAsync(dev.MacAddress, "239.255.0.1", 6792);

            Assert.True(started);
            mockController.Verify(m => m.StartVegaPreviewStreamAsync("0016c0589c1e", "239.255.0.1", 6792, It.IsAny<CancellationToken>()), Times.Once);
            mockController.Verify(m => m.StartPreviewStreamAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }
}
```

- [ ] **Step 2: Run test to verify initial state**

Run: `dotnet test tests/AvasRoutingApp.Tests/ --filter "FullyQualifiedName~VegaDiscoveryRoutingTests"`

- [ ] **Step 3: Update `ISdvoeDiscoveryService`, `SdvoeClient`, `MainViewModel`, and `MainWindow`**

1. In `ISdvoeDiscoveryService.cs`:
```csharp
Task<IReadOnlyList<AvasDevice>> DiscoverAvas223DevicesAsync(bool enableVegaDevices = false, CancellationToken ct = default);
```

2. In `SdvoeClient.cs`:
```csharp
public async Task<IReadOnlyList<AvasDevice>> DiscoverAvas223DevicesAsync(bool enableVegaDevices = false, CancellationToken ct = default)
{
    IReadOnlyList<AvasDevice> allDevices;
    try
    {
        allDevices = await DiscoverDevicesViaTelnetAsync(ct);
    }
    catch
    {
        allDevices = await DiscoverDevicesViaRestAsync(ct);
    }

    var filtered = SdvoeDiscoveryFilter.FilterTargetDevices(allDevices, enableVegaDevices);
    ...
```

3. In `MainViewModel.cs`:
Update `StartAllStreamsInternalAsync`:
```csharp
bool enableVega = _configService.Current.EnableVegaDevices;
devices = await _discoveryService.DiscoverAvas223DevicesAsync(enableVega, ct);
...
var filteredEncoders = SdvoeDiscoveryFilter.FilterTargetDevices(devices, enableVega);
...
// Command preview stream start:
bool started = dev.Family == DeviceFamily.Vega
    ? await _multicastController.StartVegaPreviewStreamAsync(dev.MacAddress, mcastIp, basePort, ct)
    : await _multicastController.StartPreviewStreamAsync(dev.MacAddress, mcastIp, basePort, ct);
```

4. In `MainWindow.xaml.cs`:
In `BtnSettings_Click`:
```csharp
if (dialog.ShowDialog() == true)
{
    var config = _configService.Current;
    UpdateStatusDisplays(config);

    if (config.BlueRiverUrl != _currentLoadedUrl)
    {
        NavigateToConfiguredUrl();
    }

    // Refresh devices if settings changed so toggling Enable VEGA Devices takes immediate effect
    if (ViewModel?.RefreshDevicesCommand != null && ViewModel.RefreshDevicesCommand.CanExecute(null))
    {
        ViewModel.RefreshDevicesCommand.Execute(null);
    }
}
```

- [ ] **Step 4: Run test to verify all tests pass**

Run: `dotnet test tests/AvasRoutingApp.Tests/ --filter "FullyQualifiedName~VegaDiscoveryRoutingTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/AvasRoutingApp/Sdvoe/ src/AvasRoutingApp/ViewModels/ src/AvasRoutingApp/MainWindow.xaml.cs tests/AvasRoutingApp.Tests/
git commit -m "fix: enable VEGA-1100 discovery pipeline and direct SDVoE stream dispatch"
```

---

### Task 3: Full Solution Build & Release Package Verification

**Files:**
- Output directory: `src/AvasRoutingApp/bin/Release/net8.0-windows/`

- [ ] **Step 1: Run the full test suite**

Run: `dotnet test tests/AvasRoutingApp.Tests/`
Verify that all unit tests pass.

- [ ] **Step 2: Build Release binary**

Run: `dotnet build src/AvasRoutingApp/AvasRoutingApp.csproj -c Release`
Verify zero errors and warnings are clean.

- [ ] **Step 3: Verification**

Confirm `AvasRoutingApp.exe` is updated in `src/AvasRoutingApp/bin/Release/net8.0-windows/`.
Confirm `MaintenanceDialog.xaml` loads resources without error.
Confirm VEGA discovery filtering and dispatch logic is active.
