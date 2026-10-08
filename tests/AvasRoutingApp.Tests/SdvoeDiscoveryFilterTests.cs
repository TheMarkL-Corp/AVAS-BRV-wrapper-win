using System.Collections.Generic;
using System.Linq;
using AvasRoutingApp.Sdvoe;
using Xunit;

namespace AvasRoutingApp.Tests
{
    public class SdvoeDiscoveryFilterTests
    {
        [Fact]
        public void IsTargetAvas223Tx_ValidAvasTx_ReturnsTrue()
        {
            var device = new AvasDevice
            {
                MacAddress = "00:11:22:33:44:55",
                VendorId = 105,
                ProductId = 81,
                ChipIndex = 0,
                IsTransmitter = true,
                IsReceiver = false
            };

            Assert.True(SdvoeDiscoveryFilter.IsTargetAvas223Tx(device));
            Assert.Equal(DeviceFamily.Avas, device.Family);
        }

        [Fact]
        public void IsTargetVega1100_ValidVegaTx_ReturnsTrue()
        {
            var device = new AvasDevice
            {
                MacAddress = "AA:BB:CC:DD:EE:FF",
                VendorId = 192,
                ProductId = 11,
                ChipIndex = 0,
                IsTransmitter = true,
                IsReceiver = false
            };

            Assert.True(SdvoeDiscoveryFilter.IsTargetVega1100(device));
            Assert.Equal(DeviceFamily.Vega, device.Family);
        }

        [Fact]
        public void FilterTargetDevices_WithVegaDisabled_ExcludesVegaDevices()
        {
            var avas = new AvasDevice
            {
                MacAddress = "00:11:22:33:44:55",
                VendorId = 105,
                ProductId = 81,
                ChipIndex = 0,
                IsTransmitter = true
            };
            var vega = new AvasDevice
            {
                MacAddress = "AA:BB:CC:DD:EE:FF",
                VendorId = 192,
                ProductId = 11,
                ChipIndex = 0,
                IsTransmitter = true
            };

            var fleet = new List<AvasDevice> { avas, vega };
            var filtered = SdvoeDiscoveryFilter.FilterTargetDevices(fleet, enableVegaDevices: false);

            Assert.Single(filtered);
            Assert.Equal(avas.MacAddress, filtered[0].MacAddress);
        }

        [Fact]
        public void FilterTargetDevices_WithVegaEnabled_IncludesVegaDevices()
        {
            var avas = new AvasDevice
            {
                MacAddress = "00:11:22:33:44:55",
                VendorId = 105,
                ProductId = 81,
                ChipIndex = 0,
                IsTransmitter = true
            };
            var vega = new AvasDevice
            {
                MacAddress = "AA:BB:CC:DD:EE:FF",
                VendorId = 192,
                ProductId = 11,
                ChipIndex = 0,
                IsTransmitter = true
            };
            var chip1 = new AvasDevice
            {
                MacAddress = "AA:BB:CC:DD:EE:01",
                VendorId = 192,
                ProductId = 11,
                ChipIndex = 1,
                IsTransmitter = true
            };

            var fleet = new List<AvasDevice> { avas, vega, chip1 };
            var filtered = SdvoeDiscoveryFilter.FilterTargetDevices(fleet, enableVegaDevices: true);

            Assert.Equal(2, filtered.Count);
            Assert.Contains(filtered, d => d.MacAddress == avas.MacAddress);
            Assert.Contains(filtered, d => d.MacAddress == vega.MacAddress);
            Assert.DoesNotContain(filtered, d => d.MacAddress == chip1.MacAddress);
        }
    }
}
