using System;
using System.Collections.Generic;
using System.Linq;

namespace E2ETests.Harness
{
    public static class SdvoeDiscoveryFilter
    {
        public const int AvasVendorId = 105;
        public const int Avas223ProductId = 81;
        public const int AvasChipIndex0 = 0;

        /// <summary>
        /// Strict filtering predicate per authoritative specification:
        /// Vendor ID == 105 && Product ID == 81 && IsTransmitter == true && ChipIndex == 0
        /// Strictly ignores chip_1 and receivers or other vendors.
        /// </summary>
        public static bool IsTargetAvas223Tx(AvasDevice device)
        {
            if (device == null) return false;

            return !string.IsNullOrWhiteSpace(device.MacAddress) &&
                   device.VendorId == AvasVendorId &&
                   device.ProductId == Avas223ProductId &&
                   device.IsTransmitter &&
                   !device.IsReceiver &&
                   device.ChipIndex == AvasChipIndex0;
        }

        public static IReadOnlyList<AvasDevice> FilterTargetDevices(IEnumerable<AvasDevice> devices)
        {
            if (devices == null) return Array.Empty<AvasDevice>();
            return devices.Where(IsTargetAvas223Tx).ToList();
        }
    }
}
