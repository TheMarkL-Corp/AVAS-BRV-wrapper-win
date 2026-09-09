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
    }

    public class MockMulticastController : IMulticastController
    {
        public List<string> AllocatedMacs { get; } = new();
        public List<string> ReleasedMacs { get; } = new();
        public List<(string mac, string mcastIp, int port)> StartedStreams { get; } = new();
        public List<string> StoppedStreams { get; } = new();
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

        public Task<bool> StartPreviewStreamAsync(string macAddress, string multicastIp, int port, CancellationToken ct = default)
        {
            StartedStreams.Add((macAddress, multicastIp, port));
            return Task.FromResult(true);
        }

        public Task<bool> StopPreviewStreamAsync(string macAddress, CancellationToken ct = default)
        {
            StoppedStreams.Add(macAddress);
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
            Exception? caughtEx = null;
            var t = new Thread(() =>
            {
                try
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
                }
                catch (Exception ex)
                {
                    caughtEx = ex;
                }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            Assert.Null(caughtEx);
        }
    }
}
