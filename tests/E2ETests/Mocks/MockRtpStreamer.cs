using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace E2ETests.Mocks
{
    public enum TestPattern
    {
        ColorBars,
        SolidRed,
        SolidGreen,
        SolidBlue,
        SolidWhite,
        Gradient,
        Checkerboard
    }

    public class MockRtpStreamer : IDisposable
    {
        private readonly UdpClient _udpSender;
        private bool _disposed;

        public MockRtpStreamer()
        {
            _udpSender = new UdpClient();
        }

        public static byte[] CreateRtpPacket(
            ushort seqNo,
            uint timestamp,
            uint ssrc,
            ushort lineNo,
            ushort totalLines,
            ushort width,
            byte[] yuvPayload,
            bool? markerOverride = null,
            byte version = 2,
            byte payloadType = 96)
        {
            int length = yuvPayload.Length;
            byte[] packet = new byte[20 + length];

            // Byte 0: Version (2 bits), Padding (1 bit), Extension (1 bit), CSRC Count (4 bits)
            packet[0] = (byte)((version << 6) & 0xC0);

            // Byte 1: Marker (1 bit) + Payload Type (7 bits)
            bool isLastLine = (lineNo == totalLines - 1);
            bool marker = markerOverride ?? isLastLine;
            packet[1] = (byte)((marker ? 0x80 : 0x00) | (payloadType & 0x7F));

            // Bytes 2-3: Sequence Number (big-endian)
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), seqNo);

            // Bytes 4-7: Timestamp (big-endian)
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4, 4), timestamp);

            // Bytes 8-11: SSRC (big-endian)
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8, 4), ssrc);

            // RFC 4175 Payload Header (Bytes 12-19)
            // Bytes 12-13: Extended Sequence Number
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(12, 2), 0);

            // Bytes 14-15: Payload Length in bytes
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(14, 2), (ushort)length);

            // Bytes 16-17: Line Number (Field bit = 0)
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(16, 2), (ushort)(lineNo & 0x7FFF));

            // Bytes 18-19: Offset (Continuation bit = 0)
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(18, 2), 0);

            // Bytes 20..N: Payload
            Buffer.BlockCopy(yuvPayload, 0, packet, 20, length);

            return packet;
        }

        public static List<byte[]> GenerateFramePackets(
            int width,
            int height,
            uint ssrc,
            uint timestamp,
            ref ushort sequenceNumber,
            TestPattern pattern = TestPattern.ColorBars)
        {
            var packets = new List<byte[]>(height);
            int scanlineBytes = width * 2; // YUV 4:2:2 is 2 bytes per pixel

            for (ushort line = 0; line < height; line++)
            {
                byte[] scanlineData = GenerateScanlineData(width, line, height, pattern);
                byte[] packet = CreateRtpPacket(
                    seqNo: sequenceNumber++,
                    timestamp: timestamp,
                    ssrc: ssrc,
                    lineNo: line,
                    totalLines: (ushort)height,
                    width: (ushort)width,
                    yuvPayload: scanlineData);

                packets.Add(packet);
            }

            return packets;
        }

        public static byte[] GenerateScanlineData(int width, int line, int totalHeight, TestPattern pattern)
        {
            byte[] data = new byte[width * 2];

            switch (pattern)
            {
                case TestPattern.SolidRed:
                    FillSolidYuv(data, 81, 90, 240); // Standard SDVoE Red in YUV
                    break;
                case TestPattern.SolidGreen:
                    FillSolidYuv(data, 145, 54, 34); // Standard SDVoE Green in YUV
                    break;
                case TestPattern.SolidBlue:
                    FillSolidYuv(data, 41, 240, 110); // Standard SDVoE Blue in YUV
                    break;
                case TestPattern.SolidWhite:
                    FillSolidYuv(data, 235, 128, 128); // Standard 100% White in YUV
                    break;
                case TestPattern.Gradient:
                    // Horizontal intensity ramp
                    for (int x = 0; x < width; x += 2)
                    {
                        byte y = (byte)((x * 255) / width);
                        data[x * 2] = 128;     // U
                        data[x * 2 + 1] = y;   // Y0
                        data[x * 2 + 2] = 128; // V
                        data[x * 2 + 3] = y;   // Y1
                    }
                    break;
                case TestPattern.Checkerboard:
                    for (int x = 0; x < width; x += 2)
                    {
                        bool isBlack = ((x / 16) + (line / 16)) % 2 == 0;
                        byte y = isBlack ? (byte)16 : (byte)235;
                        data[x * 2] = 128;
                        data[x * 2 + 1] = y;
                        data[x * 2 + 2] = 128;
                        data[x * 2 + 3] = y;
                    }
                    break;
                case TestPattern.ColorBars:
                default:
                    // 8 vertical color bars: White, Yellow, Cyan, Green, Magenta, Red, Blue, Black
                    int barWidth = Math.Max(1, width / 8);
                    for (int x = 0; x < width; x += 2)
                    {
                        int barIndex = Math.Min(7, x / barWidth);
                        var (y, u, v) = GetColorBarYuv(barIndex);
                        data[x * 2] = u;     // U
                        data[x * 2 + 1] = y; // Y0
                        data[x * 2 + 2] = v; // V
                        data[x * 2 + 3] = y; // Y1
                    }
                    break;
            }

            return data;
        }

        private static void FillSolidYuv(byte[] data, byte y, byte u, byte v)
        {
            for (int i = 0; i < data.Length; i += 4)
            {
                data[i] = u;
                data[i + 1] = y;
                data[i + 2] = v;
                data[i + 3] = y;
            }
        }

        private static (byte y, byte u, byte v) GetColorBarYuv(int bar)
        {
            return bar switch
            {
                0 => (235, 128, 128), // White
                1 => (210, 16, 146),  // Yellow
                2 => (170, 166, 16),  // Cyan
                3 => (145, 54, 34),   // Green
                4 => (106, 202, 222), // Magenta
                5 => (81, 90, 240),   // Red
                6 => (41, 240, 110),  // Blue
                _ => (16, 128, 128)   // Black
            };
        }

        // Fault Injection Helpers
        public static byte[] CreateCorruptVersionPacket(byte version = 1)
        {
            byte[] packet = CreateRtpPacket(1, 1000, 12345, 0, 180, 320, new byte[640], version: version);
            return packet;
        }

        public static byte[] CreateTruncatedPacket(int length = 15)
        {
            byte[] truncated = new byte[length];
            truncated[0] = 0x80;
            return truncated;
        }

        public static List<byte[]> GenerateFrameWithMissingLine(
            int width, int height, int lineToDrop, uint ssrc, uint timestamp, ref ushort seqNo)
        {
            var packets = new List<byte[]>();
            for (ushort line = 0; line < height; line++)
            {
                if (line == lineToDrop)
                {
                    seqNo++; // Simulate dropped packet incrementing sequence number
                    continue;
                }
                byte[] scanline = GenerateScanlineData(width, line, height, TestPattern.ColorBars);
                packets.Add(CreateRtpPacket(seqNo++, timestamp, ssrc, line, (ushort)height, (ushort)width, scanline));
            }
            return packets;
        }

        public static List<byte[]> GenerateFrameWithoutMarker(
            int width, int height, uint ssrc, uint timestamp, ref ushort seqNo)
        {
            var packets = new List<byte[]>();
            for (ushort line = 0; line < height; line++)
            {
                byte[] scanline = GenerateScanlineData(width, line, height, TestPattern.ColorBars);
                packets.Add(CreateRtpPacket(seqNo++, timestamp, ssrc, line, (ushort)height, (ushort)width, scanline, markerOverride: false));
            }
            return packets;
        }

        public async Task SendPacketsAsync(IPEndPoint target, IEnumerable<byte[]> packets, int interPacketDelayMs = 0, CancellationToken ct = default)
        {
            foreach (var p in packets)
            {
                if (ct.IsCancellationRequested) break;
                await _udpSender.SendAsync(p, p.Length, target);
                if (interPacketDelayMs > 0)
                {
                    await Task.Delay(interPacketDelayMs, ct);
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _udpSender.Dispose();
        }
    }
}
