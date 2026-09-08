using System;
using System.Net;

namespace E2ETests.Harness
{
    public class AppConfig
    {
        public string BlueRiverUrl { get; set; } = "http://localhost:3000";
        public string ControlServerIp { get; set; } = "127.0.0.1";
        public int RestPort { get; set; } = 8080;
        public int TelnetPort { get; set; } = 6970;
        public string MulticastStartIp { get; set; } = "224.1.1.1";
        public string MulticastEndIp { get; set; } = "224.1.3.225";
        public int BasePort { get; set; } = 6792;
        public string LocalNetworkInterfaceIp { get; set; } = "";

        public bool Validate(out string? errorMessage)
        {
            if (string.IsNullOrWhiteSpace(BlueRiverUrl) || !Uri.TryCreate(BlueRiverUrl, UriKind.Absolute, out _))
            {
                errorMessage = "Invalid BlueRiver URL";
                return false;
            }

            if (!IPAddress.TryParse(ControlServerIp, out _))
            {
                errorMessage = "Invalid Control Server IP";
                return false;
            }

            if (RestPort < 1 || RestPort > 65535)
            {
                errorMessage = "Invalid REST Port";
                return false;
            }

            if (TelnetPort < 1 || TelnetPort > 65535)
            {
                errorMessage = "Invalid Telnet Port";
                return false;
            }

            if (BasePort < 1024 || BasePort > 65535)
            {
                errorMessage = "Invalid Base UDP Port";
                return false;
            }

            if (!IPAddress.TryParse(MulticastStartIp, out var startIp) ||
                !IPAddress.TryParse(MulticastEndIp, out var endIp))
            {
                errorMessage = "Invalid Multicast IP range format";
                return false;
            }

            uint startVal = IpToUint(startIp);
            uint endVal = IpToUint(endIp);

            if (startVal > endVal)
            {
                errorMessage = "Multicast Start IP cannot be greater than End IP";
                return false;
            }

            // Must be in multicast class D: 224.0.0.0 to 239.255.255.255
            uint minMulticast = 0xE0000000;
            uint maxMulticast = 0xEFFFFFFF;

            if (startVal < minMulticast || endVal > maxMulticast)
            {
                errorMessage = "IP range must be within standard Multicast range (224.0.0.0 - 239.255.255.255)";
                return false;
            }

            errorMessage = null;
            return true;
        }

        private static uint IpToUint(IPAddress ip)
        {
            byte[] bytes = ip.GetAddressBytes();
            return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        }
    }

    public class AvasDevice
    {
        public string MacAddress { get; set; } = "";
        public string DeviceName { get; set; } = "";
        public string IpAddress { get; set; } = "";
        public string Model { get; set; } = "AVAS-223";
        public int VendorId { get; set; } = 105;
        public int ProductId { get; set; } = 81;
        public int ChipIndex { get; set; } = 0;
        public bool IsTransmitter { get; set; } = true;
        public bool IsReceiver { get; set; } = false;
        public string? AllocatedMulticastIp { get; set; }
        public int AllocatedPort { get; set; } = 6792;
        public bool IsStreaming { get; set; } = false;
        public double CurrentFps { get; set; } = 0.0;
        public string Resolution { get; set; } = "320x180";
        public uint Ssrc { get; set; } = 12345;
    }

    public readonly ref struct RtpVideoHeader
    {
        public readonly byte Version;
        public readonly bool Marker;
        public readonly byte PayloadType;
        public readonly ushort SequenceNumber;
        public readonly uint Timestamp;
        public readonly uint Ssrc;
        public readonly ushort Length;
        public readonly ushort LineNo;
        public readonly ushort Offset;
        public readonly ReadOnlySpan<byte> Payload;

        public RtpVideoHeader(
            byte version,
            bool marker,
            byte payloadType,
            ushort seqNo,
            uint timestamp,
            uint ssrc,
            ushort length,
            ushort lineNo,
            ushort offset,
            ReadOnlySpan<byte> payload)
        {
            Version = version;
            Marker = marker;
            PayloadType = payloadType;
            SequenceNumber = seqNo;
            Timestamp = timestamp;
            Ssrc = ssrc;
            Length = length;
            LineNo = lineNo;
            Offset = offset;
            Payload = payload;
        }
    }
}
