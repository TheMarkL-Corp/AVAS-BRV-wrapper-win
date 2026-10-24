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
        public void FilterTargetDevices_FiltersOnlyTargetAvas223Tx()
        {
            var avasTx = new AvasDevice
            {
                MacAddress = "00:11:22:33:44:55",
                VendorId = 105,
                ProductId = 81,
                ChipIndex = 0,
                IsTransmitter = true,
                IsReceiver = false
            };
            var avasChip1 = new AvasDevice
            {
                MacAddress = "00:11:22:33:44:56",
                VendorId = 105,
                ProductId = 81,
                ChipIndex = 1,
                IsTransmitter = true
            };
            var avasRx = new AvasDevice
            {
                MacAddress = "00:11:22:33:44:57",
                VendorId = 105,
                ProductId = 81,
                ChipIndex = 0,
                IsTransmitter = false,
                IsReceiver = true
            };
            var nonAvas = new AvasDevice
            {
                MacAddress = "AA:BB:CC:DD:EE:FF",
                VendorId = 192,
                ProductId = 11,
                ChipIndex = 0,
                IsTransmitter = true
            };

            var fleet = new List<AvasDevice> { avasTx, avasChip1, avasRx, nonAvas };
            var filtered = SdvoeDiscoveryFilter.FilterTargetDevices(fleet);

            Assert.Single(filtered);
            Assert.Equal(avasTx.MacAddress, filtered[0].MacAddress);
        }
    }
}
