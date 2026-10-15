using System;

namespace AvasRoutingApp.Sdvoe
{
    public enum DeviceFamily
    {
        Avas,
        Vega,
        GenericSdvoe
    }

    /// <summary>
    /// Represents an AVAS or supported SDVoE device discovered on the network.
    /// Conforms to PROJECT.md § Interface Contracts.
    /// </summary>
    public class AvasDevice
    {
        public DeviceFamily Family => VendorId switch
        {
            105 => DeviceFamily.Avas,
            192 when ProductId == 11 => DeviceFamily.Vega,
            _ => DeviceFamily.GenericSdvoe
        };
        public string MacAddress { get; set; } = "";
        public string DeviceName { get; set; } = "";
        public string IpAddress { get; set; } = "";
        public string Model { get; set; } = "AVAS-223";
        public int VendorId { get; set; } = 105;
        public int ProductId { get; set; } = 81;
        public int ChipIndex { get; set; } = 0;
        public bool IsTransmitter { get; set; } = true;
        public bool IsReceiver { get; set; } = false;
        public string? AllocatedMulticastIp { get; set; } = "";
        public int AllocatedPort { get; set; } = 6792;
        public bool IsStreaming { get; set; } = false;
        public double CurrentFps { get; set; } = 0.0;
        public string Resolution { get; set; } = "320x180";
        public uint Ssrc { get; set; } = 12345;
        public string LinkMode { get; set; } = "UNKNOWN";
        public string CompanionMac { get; set; } = "NONE";
        public string CompanionName { get; set; } = "";
        public bool CompanionIsActive { get; set; } = false;
        public string McuIpAddress { get; set; } = string.Empty;
    }
}
