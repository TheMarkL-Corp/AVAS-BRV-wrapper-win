using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using AvasRoutingApp.Configuration;
using AvasRoutingApp.Rtp;
using AvasRoutingApp.Sdvoe;
using AvasRoutingApp.ViewModels;
using AvasRoutingApp.Views;
using System.Windows;

namespace AvasRoutingApp.Tests
{
    public class MockDiscoveryService : ISdvoeDiscoveryService
    {
        public List<AvasDevice> DevicesToReturn { get; } = new();
        public event Action<IReadOnlyList<AvasDevice>>? DevicesDiscovered;

        public Task<IReadOnlyList<AvasDevice>> DiscoverAvas223DevicesAsync(CancellationToken ct = default)
        {
            IReadOnlyList<AvasDevice> list = DevicesToReturn.ToList();
            DevicesDiscovered?.Invoke(list);
            return Task.FromResult(list);
        }

        public Task<IReadOnlyList<AvasDevice>> DiscoverAllDevicesAsync(CancellationToken ct = default)
        {
            return Task.FromResult<IReadOnlyList<AvasDevice>>(DevicesToReturn.ToList());
        }
    }

    public class MockMulticastController : IMulticastController
    {
        public List<string> AllocatedMacs { get; } = new();
        public List<string> ReleasedMacs { get; } = new();
        public List<(string mac, string mcastIp, int port)> StartedStreams { get; } = new();
        public List<string> StoppedStreams { get; } = new();
        public List<(string Mac, string MulticastIp, int Port, string LinkMode, int ChipIndex)> StartCalls { get; } = new();
        public List<(string Mac, string LinkMode, int ChipIndex)> StopCalls { get; } = new();
        public int NextIpSuffix = 1;
        public bool SimulatePoolExhaustion = false;

        public string? AllocateMulticastIp(string macAddress)
        {
            if (SimulatePoolExhaustion) return null;
            AllocatedMacs.Add(macAddress);
            return $"224.1.1.{NextIpSuffix++}";
        }

        public void ReleaseMulticastIp(string macAddress)
        {
            ReleasedMacs.Add(macAddress);
        }

        public Task<bool> StartPreviewStreamAsync(string macAddress, string multicastIp, int port, string linkMode = "UNKNOWN", int chipIndex = 0, CancellationToken ct = default)
        {
            StartedStreams.Add((macAddress, multicastIp, port));
            StartCalls.Add((macAddress, multicastIp, port, linkMode, chipIndex));
            return Task.FromResult(true);
        }

        public Task<bool> StopPreviewStreamAsync(string macAddress, string linkMode = "UNKNOWN", int chipIndex = 0, CancellationToken ct = default)
        {
            StoppedStreams.Add(macAddress);
            StopCalls.Add((macAddress, linkMode, chipIndex));
            return Task.FromResult(true);
        }



        public Task<bool> FactoryResetAvpAsync(AvasDevice device, string targetMac, CancellationToken ct = default)
        {
            return Task.FromResult(true);
        }

        public Task<bool> FactoryResetMcuAsync(AvasDevice device, CancellationToken ct = default)
        {
            return Task.FromResult(true);
        }

        public Task<bool> RebootChipAsync(AvasDevice device, string targetMac, CancellationToken ct = default)
        {
            return Task.FromResult(true);
        }

        public Task<bool> RebootMcuAsync(AvasDevice device, CancellationToken ct = default)
        {
            return Task.FromResult(true);
        }
    }

    /// <summary>
    /// Unit test suite for Milestone M4: Native Overlay Sidebar UI,
    /// MVVM lifecycle, device filtering, card telemetry, and instant teardown on collapse.
    /// </summary>
    public class SidebarTests
    {
        private static AvasDevice CreateDevice(
            string mac,
            string name,
            string ip,
            int vendorId = 105,
            int productId = 81,
            int chipIndex = 0,
            bool isTx = true)
        {
            return new AvasDevice
            {
                MacAddress = mac,
                DeviceName = name,
                IpAddress = ip,
                VendorId = vendorId,
                ProductId = productId,
                ChipIndex = chipIndex,
                IsTransmitter = isTx,
                IsReceiver = !isTx
            };
        }

        [Fact]
        public void Sidebar_InitialState_IsCollapsedWithZeroWidth()
        {
            var mockDiscovery = new MockDiscoveryService();
            var mockController = new MockMulticastController();
            using var vm = new MainViewModel(mockDiscovery, mockController);

            Assert.False(vm.IsSidebarExpanded);
            Assert.Equal(0.0, vm.SidebarWidth);
            Assert.Empty(vm.EncoderCards);
            Assert.Equal("◀ PREVIEW", vm.ToggleButtonText);
            Assert.Equal(28.0, vm.ToggleStripWidth);
            Assert.Equal(0, vm.ActiveStreamCount);
        }

        [Fact]
        public async Task Sidebar_Expand_StrictlyFiltersForAvas223Chip0Transmitters()
        {
            var mockDiscovery = new MockDiscoveryService();
            var mockController = new MockMulticastController();

            // Setup mixed network endpoints
            mockDiscovery.DevicesToReturn.AddRange(new[]
            {
                CreateDevice("00:0B:AB:01:01:01", "AVAS-223 TX 1 (chip_0)", "192.168.1.101", vendorId: 105, productId: 81, chipIndex: 0, isTx: true),
                CreateDevice("00:0B:AB:01:01:02", "AVAS-223 TX 1 (chip_1)", "192.168.1.102", vendorId: 105, productId: 81, chipIndex: 1, isTx: true), // Exclude: chip_1
                CreateDevice("00:0B:AB:02:02:01", "Third-Party TX", "192.168.1.201", vendorId: 999, productId: 81, chipIndex: 0, isTx: true),        // Exclude: wrong VID
                CreateDevice("00:0B:AB:03:03:01", "AVAS-223 RX", "192.168.1.202", vendorId: 105, productId: 81, chipIndex: 0, isTx: false),         // Exclude: RX
                CreateDevice("00:0B:AB:04:04:01", "AVAS-223 TX 2 (chip_0)", "192.168.1.104", vendorId: 105, productId: 81, chipIndex: 0, isTx: true)
            });

            using var vm = new MainViewModel(mockDiscovery, mockController);

            bool success = await vm.ExpandSidebarAsync();

            Assert.True(success);
            Assert.True(vm.IsSidebarExpanded);
            Assert.Equal(380.0, vm.SidebarWidth);
            Assert.Equal("▶ CLOSE", vm.ToggleButtonText);

            // Exactly 2 valid chip_0 TX devices should be admitted
            Assert.Equal(2, vm.EncoderCards.Count);
            Assert.Equal(2, vm.DiscoveredEncoderCount);
            Assert.Equal(2, vm.ActiveStreamCount);

            Assert.Contains(vm.EncoderCards, c => c.MacAddress == "00:0B:AB:01:01:01");
            Assert.Contains(vm.EncoderCards, c => c.MacAddress == "00:0B:AB:04:04:01");
            Assert.DoesNotContain(vm.EncoderCards, c => c.MacAddress == "00:0B:AB:01:01:02"); // chip_1 strictly excluded
            Assert.DoesNotContain(vm.EncoderCards, c => c.MacAddress == "00:0B:AB:02:02:01"); // non-105 vendor strictly excluded
            Assert.DoesNotContain(vm.EncoderCards, c => c.MacAddress == "00:0B:AB:03:03:01"); // RX strictly excluded
        }

        [Fact]
        public async Task Sidebar_Expand_AllocatesMulticastIpsAndStartsStreams()
        {
            var mockDiscovery = new MockDiscoveryService();
            var mockController = new MockMulticastController();

            mockDiscovery.DevicesToReturn.Add(CreateDevice("00:0B:AB:AA:BB:CC", "AVAS-TX-A", "192.168.1.50"));

            using var vm = new MainViewModel(mockDiscovery, mockController);

            await vm.ExpandSidebarAsync();

            Assert.Single(mockController.AllocatedMacs);
            Assert.Equal("00:0B:AB:AA:BB:CC", mockController.AllocatedMacs[0]);

            Assert.Single(mockController.StartedStreams);
            Assert.Equal("00:0B:AB:AA:BB:CC", mockController.StartedStreams[0].mac);
            Assert.Equal("224.1.1.1", mockController.StartedStreams[0].mcastIp);
            Assert.Equal(5000, mockController.StartedStreams[0].port);

            var card = vm.EncoderCards[0];
            Assert.Equal("00:0B:AB:AA:BB:CC", card.MacAddress);
            Assert.Equal("224.1.1.1", card.MulticastIp);
            Assert.Equal(5000, card.Port);
            Assert.Equal("224.1.1.1:5000", card.MulticastEndpoint);
            Assert.Equal("320x180", card.Resolution);
            Assert.NotNull(card.Receiver);
            Assert.True(card.Receiver.IsListening);
        }

        [Fact]
        public async Task Sidebar_Collapse_InstantTeardown_FreesIpsStopsStreamsAndDisposesSockets()
        {
            var mockDiscovery = new MockDiscoveryService();
            var mockController = new MockMulticastController();

            mockDiscovery.DevicesToReturn.AddRange(new[]
            {
                CreateDevice("00:0B:AB:11:11:11", "TX 1", "192.168.1.10"),
                CreateDevice("00:0B:AB:22:22:22", "TX 2", "192.168.1.20")
            });

            using var vm = new MainViewModel(mockDiscovery, mockController);

            // 1. Expand and verify streams are active
            await vm.ExpandSidebarAsync();
            Assert.Equal(2, vm.EncoderCards.Count);
            var card1Receiver = vm.EncoderCards[0].Receiver;
            var card2Receiver = vm.EncoderCards[1].Receiver;
            Assert.NotNull(card1Receiver);
            Assert.NotNull(card2Receiver);
            Assert.True(card1Receiver.IsListening);
            Assert.True(card2Receiver.IsListening);

            // 2. Collapse and verify instant teardown
            bool success = await vm.CollapseSidebarAsync();

            Assert.True(success);
            Assert.False(vm.IsSidebarExpanded);
            Assert.Equal(0.0, vm.SidebarWidth);
            Assert.Equal("◀ PREVIEW", vm.ToggleButtonText);
            Assert.Empty(vm.EncoderCards);
            Assert.Equal(0, vm.ActiveStreamCount);

            // Verification of instant teardown actions:
            // Sockets closed and stopped listening
            Assert.False(card1Receiver.IsListening);
            Assert.False(card2Receiver.IsListening);

            // Streams stopped on hardware
            Assert.Equal(2, mockController.StoppedStreams.Count);
            Assert.Contains("00:0B:AB:11:11:11", mockController.StoppedStreams);
            Assert.Contains("00:0B:AB:22:22:22", mockController.StoppedStreams);

            // Multicast IPs returned to pool
            Assert.Equal(2, mockController.ReleasedMacs.Count);
            Assert.Contains("00:0B:AB:11:11:11", mockController.ReleasedMacs);
            Assert.Contains("00:0B:AB:22:22:22", mockController.ReleasedMacs);
        }

        [Fact]
        public async Task Sidebar_Toggle_CyclesBetweenExpandedAndCollapsed()
        {
            var mockDiscovery = new MockDiscoveryService();
            var mockController = new MockMulticastController();
            mockDiscovery.DevicesToReturn.Add(CreateDevice("00:0B:AB:AA:11:22", "TX Toggle", "192.168.1.15"));

            using var vm = new MainViewModel(mockDiscovery, mockController);

            // 1st toggle: Expand
            await vm.ToggleSidebarAsync();
            Assert.True(vm.IsSidebarExpanded);
            Assert.Equal(380.0, vm.SidebarWidth);
            Assert.Single(vm.EncoderCards);

            // 2nd toggle: Collapse
            await vm.ToggleSidebarAsync();
            Assert.False(vm.IsSidebarExpanded);
            Assert.Equal(0.0, vm.SidebarWidth);
            Assert.Empty(vm.EncoderCards);

            // 3rd toggle: Expand again
            await vm.ToggleSidebarAsync();
            Assert.True(vm.IsSidebarExpanded);
            Assert.Equal(380.0, vm.SidebarWidth);
            Assert.Single(vm.EncoderCards);
        }

        [Fact]
        public async Task Sidebar_RefreshDevices_StopsExistingStreams_BeforeStartingNewStreams()
        {
            var mockDiscovery = new MockDiscoveryService();
            var mockController = new MockMulticastController();
            mockDiscovery.DevicesToReturn.AddRange(new[]
            {
                CreateDevice("00:0B:AB:11:11:11", "TX 1", "192.168.1.10"),
                CreateDevice("00:0B:AB:22:22:22", "TX 2", "192.168.1.20")
            });

            using var vm = new MainViewModel(mockDiscovery, mockController);

            // 1. Expand sidebar - 2 streams active
            await vm.ExpandSidebarAsync();
            Assert.Equal(2, vm.ActiveStreamCount);
            Assert.Equal(2, mockController.StartedStreams.Count);
            Assert.Empty(mockController.StoppedStreams);

            // 2. Click Discover / RefreshDevices
            await vm.RefreshDevicesAsync();

            // Verify: Previous streams were stopped before new ones were started
            Assert.Equal(2, mockController.StoppedStreams.Count);
            Assert.Contains("00:0B:AB:11:11:11", mockController.StoppedStreams);
            Assert.Contains("00:0B:AB:22:22:22", mockController.StoppedStreams);
            Assert.Equal(2, mockController.ReleasedMacs.Count);

            // And refreshed streams were restarted
            Assert.Equal(4, mockController.StartedStreams.Count); // 2 initial + 2 after refresh
            Assert.Equal(2, vm.ActiveStreamCount);
        }

        [Fact]
        public async Task Sidebar_Dispose_StopsAllStreamsAndFreesIps()
        {
            var mockDiscovery = new MockDiscoveryService();
            var mockController = new MockMulticastController();
            mockDiscovery.DevicesToReturn.Add(CreateDevice("00:0B:AB:33:33:33", "TX 3", "192.168.1.30"));

            var vm = new MainViewModel(mockDiscovery, mockController);
            await vm.ExpandSidebarAsync();
            Assert.Equal(1, vm.ActiveStreamCount);

            // Dispose ViewModel (as happens when closing application)
            vm.Dispose();

            // Verify hardware stop and IP release was triggered
            Assert.Single(mockController.StoppedStreams);
            Assert.Equal("00:0B:AB:33:33:33", mockController.StoppedStreams[0]);
            Assert.Single(mockController.ReleasedMacs);
            Assert.Equal("00:0B:AB:33:33:33", mockController.ReleasedMacs[0]);
        }

        [Fact]
        public void EncoderCardViewModel_TelemetryAndColorCodedFps_ReflectsThresholds()
        {
            using var card = new EncoderCardViewModel
            {
                MacAddress = "00:0B:AB:99:88:77",
                MulticastIp = "224.1.1.5",
                Port = 6792
            };

            // Initial state: 0.0 FPS -> Amber (< 1.0)
            Assert.Equal(0.0, card.CurrentFps);
            Assert.Equal("0.0 FPS", card.FpsDisplay);
            Assert.False(card.IsFpsHealthy);
            Assert.Equal("#FFA000", card.FpsColorHex);

            // Low FPS state: 0.9 FPS -> Amber (< 1.0)
            card.UpdateFps(0.9);
            Assert.Equal(0.9, card.CurrentFps);
            Assert.Equal("0.9 FPS", card.FpsDisplay);
            Assert.False(card.IsFpsHealthy);
            Assert.Equal("#FFA000", card.FpsColorHex);

            // Healthy boundary: 1.0 FPS -> Green (>= 1.0)
            card.UpdateFps(1.0);
            Assert.Equal(1.0, card.CurrentFps);
            Assert.Equal("1.0 FPS", card.FpsDisplay);
            Assert.True(card.IsFpsHealthy);
            Assert.Equal("#4CAF50", card.FpsColorHex);

            // Normal active stream: 3.5 FPS -> Green (>= 1.0)
            card.UpdateFps(3.5);
            Assert.Equal(3.5, card.CurrentFps);
            Assert.Equal("3.5 FPS", card.FpsDisplay);
            Assert.True(card.IsFpsHealthy);
            Assert.Equal("#4CAF50", card.FpsColorHex);
        }

        [Fact]
        public void EncoderCardViewModel_UpdateFrame_UpdatesResolutionAndFrameCount()
        {
            using var card = new EncoderCardViewModel();
            Assert.Equal("320x180", card.Resolution);
            Assert.Equal(0, card.FrameUpdateCount);
            Assert.True(card.IsWaitingForStream);

            byte[] dummyRgb = new byte[640 * 360 * 3];
            card.UpdateFrame(dummyRgb, 640, 360);

            Assert.Equal("640x360", card.Resolution);
            Assert.Equal(1, card.FrameUpdateCount);
            Assert.False(card.IsWaitingForStream);
            Assert.Equal("Streaming", card.StatusMessage);
            Assert.Same(dummyRgb, card.LastRenderedRgbFrame);
        }

        [Fact]
        public async Task Sidebar_MulticastPoolExhaustion_HandlesGracefullyWithoutCrash()
        {
            var mockDiscovery = new MockDiscoveryService();
            var mockController = new MockMulticastController
            {
                SimulatePoolExhaustion = true // Always returns null for AllocateMulticastIp
            };

            mockDiscovery.DevicesToReturn.Add(CreateDevice("00:0B:AB:EE:FF:00", "TX Overloaded", "192.168.1.99"));

            using var vm = new MainViewModel(mockDiscovery, mockController);

            bool success = await vm.ExpandSidebarAsync();

            Assert.True(success);
            Assert.True(vm.IsSidebarExpanded);
            // No card added because IP could not be allocated
            Assert.Empty(vm.EncoderCards);
            Assert.Equal(0, vm.ActiveStreamCount);
            Assert.Empty(mockController.StartedStreams);
        }


        [Fact]
        public void Sidebar_InstantiateCardView_OnStaThread()
        {
            WpfTestHelper.Run(() =>
            {
                var card = new EncoderCardViewModel
                {
                    DeviceName = "Test Card",
                    MacAddress = "00:11:22:33:44:55",
                    UnicastIp = "10.0.0.1",
                    MulticastIp = "224.1.1.1",
                    Port = 6792
                };
                var view = new EncoderCardView
                {
                    DataContext = card
                };
                view.Measure(new System.Windows.Size(350, 260));
                view.Arrange(new System.Windows.Rect(0, 0, 350, 260));
                view.UpdateLayout();
            });
        }

        [Fact]
        public void Sidebar_FontScale_UpdatesFromConfigChange()
        {
            var mockDiscovery = new MockDiscoveryService();
            var mockController = new MockMulticastController();

            string tempDir = Path.Combine(Path.GetTempPath(), "avas_sidebar_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var configService = new ConfigService(Path.Combine(tempDir, "appsettings.json"));
                using var vm = new MainViewModel(mockDiscovery, mockController, configService);

                // Initial scale: Normal (1.0)
                Assert.Equal(1.0, vm.SidebarFontScale);

                // Update config to ExtraLarge
                var config = configService.Current;
                config.SidebarFontSize = "ExtraLarge";
                configService.Save(config);

                Assert.Equal(1.30, vm.SidebarFontScale);

                // Update config to Small
                config.SidebarFontSize = "Small";
                configService.Save(config);

                Assert.Equal(0.90, vm.SidebarFontScale);
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        [Fact]
        public void SettingsDialog_SidebarFontSize_LoadsAndSaves()
        {
            WpfTestHelper.Run(() =>
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "avas_settings_test_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                try
                {
                    var configService = new ConfigService(Path.Combine(tempDir, "appsettings.json"));
                    var dialog = new SettingsDialog(configService);

                    var cmbFont = (System.Windows.Controls.ComboBox)dialog.FindName("CmbSidebarFontSize");
                    Assert.NotNull(cmbFont);
                    Assert.Equal("Normal / Default (100% — Standard)", ((System.Windows.Controls.ComboBoxItem)cmbFont.SelectedItem).Content.ToString());

                    // Select "Large (115%)" (index 2: 0=Small, 1=Normal, 2=Large, 3=ExtraLarge)
                    cmbFont.SelectedIndex = 2;

                    var btnSave = (System.Windows.Controls.Button)dialog.FindName("BtnSave");
                    Assert.NotNull(btnSave);
                    btnSave.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

                    // Verify saved config
                    Assert.Equal("Large", configService.Current.SidebarFontSize);
                    Assert.Equal(420.0, configService.Current.SidebarWidth);
                }
                finally
                {
                    if (Directory.Exists(tempDir))
                    {
                        Directory.Delete(tempDir, true);
                    }
                }
            });
        }

        [Fact]
        public void SelectDiagnosticsTabCommand_SetsSelectedTabAndDiagnosticsFlag()
        {
            var vm = new MainViewModel(new MockDiscoveryService(), new MockMulticastController());

            Assert.Equal(SidebarTab.Previews, vm.SelectedTab);
            Assert.True(vm.IsPreviewsTabSelected);
            Assert.False(vm.IsDiagnosticsTabSelected);

            vm.SelectDiagnosticsTabCommand.Execute(null);

            Assert.Equal(SidebarTab.Diagnostics, vm.SelectedTab);
            Assert.True(vm.IsDiagnosticsTabSelected);
            Assert.False(vm.IsPreviewsTabSelected);
            Assert.False(vm.IsMultiLinkTabSelected);
            Assert.False(vm.IsTemperatureTabSelected);
        }

        [Fact]
        public void DiagnosticsFiltering_FiltersTxAndRxSections()
        {
            var vm = new MainViewModel(new MockDiscoveryService(), new MockMulticastController());

            // Add dummy TX and RX maintenance cards
            var devTx = new AvasDevice { MacAddress = "00:14:95:11:11:11", VendorId = 105, IsTransmitter = true, IsReceiver = false };
            var devRx = new AvasDevice { MacAddress = "00:14:95:22:22:22", VendorId = 105, IsTransmitter = false, IsReceiver = true };

            vm.MaintenanceCards.Add(new MaintenanceCardViewModel(devTx));
            vm.MaintenanceCards.Add(new MaintenanceCardViewModel(devRx));
            vm.UpdateFilteredMaintenanceCards();

            // Default ALL
            Assert.True(vm.IsFilterAllDiagnosticsSelected);
            Assert.True(vm.ShowTxDiagnosticsSection);
            Assert.True(vm.ShowRxDiagnosticsSection);
            Assert.Single(vm.FilteredTxMaintenanceCards);
            Assert.Single(vm.FilteredRxMaintenanceCards);

            // Filter TX only
            vm.SetFilterTxDiagnosticsCommand.Execute(null);
            Assert.True(vm.IsFilterTxDiagnosticsSelected);
            Assert.True(vm.ShowTxDiagnosticsSection);
            Assert.False(vm.ShowRxDiagnosticsSection);
            Assert.Single(vm.FilteredTxMaintenanceCards);
            Assert.Empty(vm.FilteredRxMaintenanceCards);

            // Filter RX only
            vm.SetFilterRxDiagnosticsCommand.Execute(null);
            Assert.True(vm.IsFilterRxDiagnosticsSelected);
            Assert.False(vm.ShowTxDiagnosticsSection);
            Assert.True(vm.ShowRxDiagnosticsSection);
            Assert.Empty(vm.FilteredTxMaintenanceCards);
            Assert.Single(vm.FilteredRxMaintenanceCards);
        }

        [Fact]
        public async Task StartAllStreams_PopulatesMaintenanceCardsWithTxAndRx()
        {
            var discovery = new MockDiscoveryService();
            var devTx = new AvasDevice { MacAddress = "00:14:95:11:11:11", VendorId = 105, ChipIndex = 0, IsTransmitter = true, IsReceiver = false };
            var devRx = new AvasDevice { MacAddress = "00:14:95:22:22:22", VendorId = 105, ChipIndex = 0, IsTransmitter = false, IsReceiver = true };
            discovery.DevicesToReturn.Add(devTx);
            discovery.DevicesToReturn.Add(devRx);

            var vm = new MainViewModel(discovery, new MockMulticastController());
            await vm.ExpandSidebarAsync();

            Assert.Equal(2, vm.MaintenanceCards.Count);
            Assert.Single(vm.MaintenanceCards, c => c.IsTransmitter && c.DeviceTypeBadge == "TX");
            Assert.Single(vm.MaintenanceCards, c => c.IsReceiver && c.DeviceTypeBadge == "RX");
        }

        [Fact]
        public async Task SwitchingChipOnSingleLinkCard_RestartsStreamOnCompanionMac()
        {
            var mockDiscovery = new MockDiscoveryService();
            var mockController = new MockMulticastController();
            var configService = new ConfigService();
            using var vm = new MainViewModel(mockDiscovery, mockController, configService);

            var card = new EncoderCardViewModel
            {
                MacAddress = "74fe4887fdf4",
                UnicastIp = "192.168.1.151",
                LinkMode = "SINGLE",
                CompanionMac = "74fe4887fdf5",
                MulticastIp = "225.0.0.10",
                Port = 5004
            };
            vm.Cards.Add(card);
            vm.WireUpCardChipSelection(card);

            // Switch to Chip 1
            card.SelectChip1Command.Execute(null);

            // Wait for async execution
            for (int i = 0; i < 20 && mockController.StartCalls.Count == 0; i++)
            {
                await Task.Delay(50);
            }

            // Assert that Stop was called on Chip 0 and Start was called on Chip 1
            Assert.Contains(mockController.StopCalls, c => c.Mac == "74fe4887fdf4" && c.ChipIndex == 0);
            Assert.Contains(mockController.StartCalls, c => c.Mac == "74fe4887fdf5" && c.ChipIndex == 1);
        }
    }
}
