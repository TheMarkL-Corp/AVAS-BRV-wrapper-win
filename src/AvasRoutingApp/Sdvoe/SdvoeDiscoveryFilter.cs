using System;
using System.Collections.Generic;
using System.Linq;

namespace AvasRoutingApp.Sdvoe
{
    /// <summary>
    /// Strict device filtering utility for AVAS-223 encoders.
    /// Filters endpoints strictly for Vendor ID 105, Product ID 81, IsTransmitter == true, and ChipIndex == 0 (chip_0).
    /// Strictly excludes chip_1 secondary link aggregators, receivers, and other vendors.
    /// </summary>
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

        /// <summary>
        /// Filters an enumeration of devices, returning only those that match the strict AVAS-223 chip_0 criteria.
        /// </summary>
        public static IReadOnlyList<AvasDevice> FilterTargetDevices(IEnumerable<AvasDevice> devices)
        {
            if (devices == null) return Array.Empty<AvasDevice>();
            return devices.Where(IsTargetAvas223Tx).ToList();
        }
    }
}
