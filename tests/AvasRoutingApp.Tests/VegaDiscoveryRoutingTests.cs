using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using AvasRoutingApp.Configuration;
using AvasRoutingApp.Sdvoe;
using AvasRoutingApp.ViewModels;

namespace AvasRoutingApp.Tests
{
    public class VegaDiscoveryRoutingTests
    {
        [Fact]
        public void IsTargetVega1100_WithValidVegaTransceiver_ReturnsTrue()
        {
            var vegaDev = new AvasDevice
            {
                MacAddress = "0016c0589c1e",
                DeviceName = "VEGA_1100",
                VendorId = 192,
                ProductId = 11,
                ChipIndex = 0,
                IsTransmitter = true,
                IsReceiver = true // Transceiver (TR) is both transmitter and receiver
            };

            Assert.True(SdvoeDiscoveryFilter.IsTargetVega1100(vegaDev));
            Assert.Equal(DeviceFamily.Vega, vegaDev.Family);
        }

        [Theory]
        [InlineData(192, 11, 0, false, true, false)] // Not transmitter
        [InlineData(192, 11, 1, true, true, false)]  // Chip index 1
        [InlineData(105, 11, 0, true, true, false)]  // Wrong vendor ID
        [InlineData(192, 12, 0, true, true, false)]  // Wrong product ID
        [InlineData(192, 11, 0, true, true, true)]   // Valid matching
        public void IsTargetVega1100_EdgeCases_ValidatedProperly(
            int vendorId, int productId, int chipIndex, bool isTx, bool isRx, bool expected)
        {
            var dev = new AvasDevice
            {
                MacAddress = "0016c0589c1e",
                VendorId = vendorId,
                ProductId = productId,
                ChipIndex = chipIndex,
                IsTransmitter = isTx,
                IsReceiver = isRx
            };

            Assert.Equal(expected, SdvoeDiscoveryFilter.IsTargetVega1100(dev));
        }

        [Fact]
        public void FilterTargetDevices_FleetFiltering_IncludesVegaOnlyWhenEnabled()
        {
            var fleet = new List<AvasDevice>
            {
                new AvasDevice { MacAddress = "avas_tx0", VendorId = 105, ProductId = 81, ChipIndex = 0, IsTransmitter = true, IsReceiver = false },
                new AvasDevice { MacAddress = "avas_tx1", VendorId = 105, ProductId = 81, ChipIndex = 1, IsTransmitter = true, IsReceiver = false },
                new AvasDevice { MacAddress = "avas_rx0", VendorId = 105, ProductId = 81, ChipIndex = 0, IsTransmitter = false, IsReceiver = true },
                new AvasDevice { MacAddress = "vega_tr0", VendorId = 192, ProductId = 11, ChipIndex = 0, IsTransmitter = true, IsReceiver = true },
                new AvasDevice { MacAddress = "vega_tr1", VendorId = 192, ProductId = 11, ChipIndex = 1, IsTransmitter = true, IsReceiver = true },
                new AvasDevice { MacAddress = "third_party", VendorId = 999, ProductId = 1, ChipIndex = 0, IsTransmitter = true }
            };

            // When enableVegaDevices == false
            var defaultFiltered = SdvoeDiscoveryFilter.FilterTargetDevices(fleet, enableVegaDevices: false);
            Assert.Single(defaultFiltered);
            Assert.Equal("avas_tx0", defaultFiltered[0].MacAddress);

            // When enableVegaDevices == true
            var vegaFiltered = SdvoeDiscoveryFilter.FilterTargetDevices(fleet, enableVegaDevices: true);
            Assert.Equal(2, vegaFiltered.Count);
            Assert.Contains(vegaFiltered, d => d.MacAddress == "avas_tx0");
            Assert.Contains(vegaFiltered, d => d.MacAddress == "vega_tr0");
        }

        [Fact]
        public async Task MainViewModel_StartAllStreams_RoutesVegaToVegaStreamAndAvasToAvasStream()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "vega_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var configFile = Path.Combine(tempDir, "appsettings.json");
                var configService = new ConfigService(configFile);
                var config = configService.Current;
                config.EnableVegaDevices = true;
                configService.Save(config);

                var avasDev = new AvasDevice
                {
                    MacAddress = "f82285001122",
                    DeviceName = "AVAS_TX",
                    IpAddress = "192.168.1.100",
                    VendorId = 105,
                    ProductId = 81,
                    ChipIndex = 0,
                    IsTransmitter = true,
                    IsReceiver = false
                };

                var vegaDev = new AvasDevice
                {
                    MacAddress = "0016c0589c1e",
                    DeviceName = "VEGA_1100",
                    IpAddress = "192.168.1.150",
                    VendorId = 192,
                    ProductId = 11,
                    ChipIndex = 0,
                    IsTransmitter = true,
                    IsReceiver = true
                };

                var mockDiscovery = new TestDiscoveryService();
                mockDiscovery.AllDevices.Add(avasDev);
                mockDiscovery.AllDevices.Add(vegaDev);

                var mockController = new TrackingMulticastController();

                using var vm = new MainViewModel(
                    discoveryService: mockDiscovery,
                    multicastController: mockController,
                    configService: configService);

                await vm.StartAllStreamsAsync();

                // Check discovery query received enableVega = true
                Assert.True(mockDiscovery.LastEnableVegaRequested);

                // Both devices should have cards
                Assert.Equal(2, vm.EncoderCards.Count);

                // AVAS stream should be started via StartPreviewStreamAsync
                Assert.Contains("f82285001122", mockController.StartedAvasStreams.Select(s => s.mac));
                Assert.DoesNotContain("f82285001122", mockController.StartedVegaStreams.Select(s => s.mac));

                // VEGA stream should be started via StartVegaPreviewStreamAsync
                Assert.Contains("0016c0589c1e", mockController.StartedVegaStreams.Select(s => s.mac));
                Assert.DoesNotContain("0016c0589c1e", mockController.StartedAvasStreams.Select(s => s.mac));

                // Stop all streams and verify proper stop routing
                await vm.StopAllStreamsAsync();
                Assert.Contains("f82285001122", mockController.StoppedAvasStreams);
                Assert.Contains("0016c0589c1e", mockController.StoppedVegaStreams);
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        #region Helpers

        private class TestDiscoveryService : ISdvoeDiscoveryService
        {
            public List<AvasDevice> AllDevices { get; } = new();
            public bool LastEnableVegaRequested { get; private set; }
            public event Action<IReadOnlyList<AvasDevice>>? DevicesDiscovered;

            public Task<IReadOnlyList<AvasDevice>> DiscoverAvas223DevicesAsync(CancellationToken ct = default)
            {
                return DiscoverAvas223DevicesAsync(false, ct);
            }

            public Task<IReadOnlyList<AvasDevice>> DiscoverAvas223DevicesAsync(bool enableVegaDevices = false, CancellationToken ct = default)
            {
                LastEnableVegaRequested = enableVegaDevices;
                var filtered = SdvoeDiscoveryFilter.FilterTargetDevices(AllDevices, enableVegaDevices);
                DevicesDiscovered?.Invoke(filtered);
                return Task.FromResult(filtered);
            }

            public Task<IReadOnlyList<AvasDevice>> DiscoverAllDevicesAsync(CancellationToken ct = default)
            {
                return Task.FromResult<IReadOnlyList<AvasDevice>>(AllDevices.ToList());
            }
        }

        private class TrackingMulticastController : IMulticastController
        {
            public List<(string mac, string mcastIp, int port)> StartedAvasStreams { get; } = new();
            public List<(string mac, string mcastIp, int port)> StartedVegaStreams { get; } = new();
            public List<string> StoppedAvasStreams { get; } = new();
            public List<string> StoppedVegaStreams { get; } = new();
            public List<string> ReleasedMacs { get; } = new();
            private int _ipCounter = 1;

            public string? AllocateMulticastIp(string macAddress)
            {
                return $"224.1.1.{_ipCounter++}";
            }

            public void ReleaseMulticastIp(string macAddress)
            {
                ReleasedMacs.Add(macAddress);
            }

            public Task<bool> StartPreviewStreamAsync(string macAddress, string multicastIp, int port, CancellationToken ct = default)
            {
                StartedAvasStreams.Add((macAddress, multicastIp, port));
                return Task.FromResult(true);
            }

            public Task<bool> StopPreviewStreamAsync(string macAddress, CancellationToken ct = default)
            {
                StoppedAvasStreams.Add(macAddress);
                return Task.FromResult(true);
            }

            public Task<bool> StartVegaPreviewStreamAsync(string macAddress, string multicastIp, int port, CancellationToken ct = default)
            {
                StartedVegaStreams.Add((macAddress, multicastIp, port));
                return Task.FromResult(true);
            }

            public Task<bool> StopVegaPreviewStreamAsync(string macAddress, CancellationToken ct = default)
            {
                StoppedVegaStreams.Add(macAddress);
                return Task.FromResult(true);
            }

            public Task<bool> FactoryResetAvpAsync(AvasDevice device, string targetMac, CancellationToken ct = default) => Task.FromResult(true);
            public Task<bool> FactoryResetMcuAsync(AvasDevice device, CancellationToken ct = default) => Task.FromResult(true);
            public Task<bool> RebootChipAsync(AvasDevice device, string targetMac, CancellationToken ct = default) => Task.FromResult(true);
        }

        #endregion
    }
}
