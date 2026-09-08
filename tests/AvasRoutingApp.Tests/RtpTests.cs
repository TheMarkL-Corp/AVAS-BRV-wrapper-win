using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using AvasRoutingApp.Rtp;

namespace AvasRoutingApp.Tests
{
    /// <summary>
    /// Comprehensive unit test suite for Milestone M5: RTP Ingestion Engine,
    /// 20-byte combined header parsing, direct-slotting scanline frame reassembly,
    /// Q10 fixed-point color space conversion, and RtpMulticastReceiver pipeline.
    /// </summary>
    public class RtpTests
    {
        #region Test Packet Helpers

        private static byte[] CreateRtpVideoPacket(
            ushort seqNo,
            uint timestamp,
            uint ssrc,
            ushort lineNo,
            ushort offset,
            ushort length,
            bool marker,
            byte payloadType = 96,
            byte version = 2,
            byte[]? customPayload = null)
        {
            byte[] packet = new byte[20 + length];

            // Byte 0: Version (2 bits), Padding (1 bit), Extension (1 bit), CC (4 bits)
            packet[0] = (byte)((version << 6) & 0xC0);

            // Byte 1: Marker (1 bit), PayloadType (7 bits)
            packet[1] = (byte)((marker ? 0x80 : 0x00) | (payloadType & 0x7F));

            // Bytes 2-3: Sequence Number
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), seqNo);

            // Bytes 4-7: Timestamp
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4, 4), timestamp);

            // Bytes 8-11: SSRC
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8, 4), ssrc);

            // Bytes 12-13: Extended Sequence Number
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(12, 2), 0);

            // Bytes 14-15: Length
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(14, 2), length);

            // Bytes 16-17: Line Number
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(16, 2), lineNo);

            // Bytes 18-19: Offset
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(18, 2), offset);

            // Bytes 20+: Payload
            if (customPayload != null)
            {
                int copyLen = Math.Min(customPayload.Length, length);
                Buffer.BlockCopy(customPayload, 0, packet, 20, copyLen);
            }
            else
            {
                // Default test payload: alternating pattern [U=128, Y0=180, V=128, Y1=180]
                for (int i = 0; i < length; i += 4)
                {
                    packet[20 + i] = 128;     // U
                    packet[20 + i + 1] = 180; // Y0
                    packet[20 + i + 2] = 128; // V
                    packet[20 + i + 3] = 180; // Y1
                }
            }

            return packet;
        }

        #endregion

        #region 1. 20-Byte Combined Header Parsing Tests

        [Fact]
        public void RtpHeader_TryParse_ValidPacket_ExtractsAllFieldsCorrectly()
        {
            ushort seqNo = 4321;
            uint timestamp = 987654;
            uint ssrc = 0x12345678;
            ushort lineNo = 42;
            ushort offset = 0;
            ushort length = 640; // 320 pixels * 2 bytes
            bool marker = false;

            byte[] packet = CreateRtpVideoPacket(seqNo, timestamp, ssrc, lineNo, offset, length, marker);

            bool success = RtpHeader.TryParse(packet, out var header);

            Assert.True(success);
            Assert.Equal(2, header.Version);
            Assert.False(header.Marker);
            Assert.Equal(96, header.PayloadType);
            Assert.Equal(seqNo, header.SequenceNumber);
            Assert.Equal(timestamp, header.Timestamp);
            Assert.Equal(ssrc, header.Ssrc);
            Assert.Equal(length, header.Length);
            Assert.Equal(lineNo, header.LineNo);
            Assert.Equal(offset, header.Offset);
            Assert.Equal(length, header.Payload.Length);
        }

        [Fact]
        public void RtpHeader_TryParse_MarkerBitSet_DetectedAccurately()
        {
            byte[] packetWithMarker = CreateRtpVideoPacket(100, 1000, 1, 179, 0, 640, marker: true);
            byte[] packetWithoutMarker = CreateRtpVideoPacket(101, 1000, 1, 178, 0, 640, marker: false);

            Assert.True(RtpHeader.TryParse(packetWithMarker, out var h1));
            Assert.True(h1.Marker);

            Assert.True(RtpHeader.TryParse(packetWithoutMarker, out var h2));
            Assert.False(h2.Marker);
        }

        [Fact]
        public void RtpPacketParser_TryParse_DelegatesToRtpHeader()
        {
            byte[] packet = CreateRtpVideoPacket(123, 456, 789, 0, 0, 64, marker: true);

            bool success = RtpPacketParser.TryParse(packet, out var header);

            Assert.True(success);
            Assert.Equal(123, header.SequenceNumber);
            Assert.Equal(456u, header.Timestamp);
            Assert.True(header.Marker);
        }

        #endregion

        #region 2. Corrupted & Edge-Case Packet Validation

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(10)]
        [InlineData(19)]
        public void RtpHeader_TryParse_TruncatedBufferSmallerThan20Bytes_ReturnsFalse(int length)
        {
            byte[] truncated = new byte[length];
            bool success = RtpHeader.TryParse(truncated, out _);
            Assert.False(success);
        }

        [Theory]
        [InlineData(0)] // Version 0
        [InlineData(1)] // Version 1
        [InlineData(3)] // Version 3
        public void RtpHeader_TryParse_InvalidRtpVersion_ReturnsFalse(byte invalidVersion)
        {
            byte[] packet = CreateRtpVideoPacket(1, 100, 1, 0, 0, 64, false, version: invalidVersion);
            bool success = RtpHeader.TryParse(packet, out _);
            Assert.False(success);
        }

        [Fact]
        public void RtpHeader_TryParse_PayloadShorterThanDeclaredLength_ReturnsFalse()
        {
            // Declares length = 640, but total buffer is only 20 + 300 = 320 bytes
            byte[] packet = CreateRtpVideoPacket(1, 100, 1, 0, 0, 640, false);
            byte[] truncatedPayload = new byte[320];
            Buffer.BlockCopy(packet, 0, truncatedPayload, 0, 320);

            bool success = RtpHeader.TryParse(truncatedPayload, out _);
            Assert.False(success);
        }

        #endregion

        #region 3. Frame Reassembly & Direct Slotting Tests

        [Fact]
        public void Reassembler_InOrderPackets_AssemblesFullFrameOnMarker()
        {
            var reassembler = new ScanlineFrameReassembler();
            int height = 180;
            int width = 320;
            ushort lineBytes = (ushort)(width * 2);
            uint timestamp = 55000;

            AssembledFrame? resultFrame = null;

            for (ushort line = 0; line < height; line++)
            {
                bool isMarker = (line == height - 1);
                byte[] linePayload = new byte[lineBytes];
                Array.Fill(linePayload, (byte)(line % 256));

                byte[] packet = CreateRtpVideoPacket(line, timestamp, 100, line, 0, lineBytes, isMarker, customPayload: linePayload);
                Assert.True(RtpHeader.TryParse(packet, out var header));

                var frame = reassembler.ProcessPacket(in header);
                if (isMarker)
                {
                    resultFrame = frame;
                }
                else
                {
                    Assert.Null(frame);
                }
            }

            Assert.NotNull(resultFrame);
            Assert.Equal(width, resultFrame.Width);
            Assert.Equal(height, resultFrame.Height);
            Assert.Equal(timestamp, resultFrame.Timestamp);
            Assert.Equal(width * height * 2, resultFrame.YuvData.Length);
            Assert.Equal(1, reassembler.FramesCompleted);
            Assert.Equal(0, reassembler.FramesDropped);

            // Verify first and last line content
            Assert.Equal(0, resultFrame.YuvData[0]);
            Assert.Equal((byte)((height - 1) % 256), resultFrame.YuvData[(height - 1) * lineBytes]);
        }

        [Fact]
        public void Reassembler_OutOfOrderPackets_DirectSlottingReassemblesFrameCorrectly()
        {
            var reassembler = new ScanlineFrameReassembler();
            int height = 4;
            int width = 4;
            ushort lineBytes = (ushort)(width * 2);
            uint timestamp = 99999;

            // Generate packets
            var packets = new List<byte[]>();
            for (ushort line = 0; line < height; line++)
            {
                byte[] linePayload = new byte[lineBytes];
                Array.Fill(linePayload, (byte)(line + 10)); // line 0 -> 10, line 1 -> 11, etc.
                bool isMarker = (line == height - 1);
                packets.Add(CreateRtpVideoPacket(line, timestamp, 1, line, 0, lineBytes, isMarker, customPayload: linePayload));
            }

            // Feed packets in scrambled order: Line 2, Line 0, Line 1, Line 3 (Marker)
            ushort[] order = { 2, 0, 1, 3 };
            AssembledFrame? finalFrame = null;

            foreach (var idx in order)
            {
                Assert.True(RtpHeader.TryParse(packets[idx], out var header));
                var f = reassembler.ProcessPacket(in header);
                if (header.Marker)
                {
                    finalFrame = f;
                }
            }

            Assert.NotNull(finalFrame);
            Assert.Equal(width, finalFrame.Width);
            Assert.Equal(height, finalFrame.Height);
            Assert.Equal(1, reassembler.FramesCompleted);

            // Verify scanlines are slotted into exact vertical order
            for (int line = 0; line < height; line++)
            {
                byte expectedVal = (byte)(line + 10);
                int offset = line * lineBytes;
                Assert.Equal(expectedVal, finalFrame.YuvData[offset]);
                Assert.Equal(expectedVal, finalFrame.YuvData[offset + lineBytes - 1]);
            }
        }

        [Fact]
        public void Reassembler_MissingScanline_DropsFrameOnMarker()
        {
            var reassembler = new ScanlineFrameReassembler();
            ushort lineBytes = 8;
            uint timestamp = 8888;

            // Feed lines 0, 1, then line 3 (Marker), skipping line 2
            byte[] p0 = CreateRtpVideoPacket(0, timestamp, 1, 0, 0, lineBytes, false);
            byte[] p1 = CreateRtpVideoPacket(1, timestamp, 1, 1, 0, lineBytes, false);
            byte[] p3 = CreateRtpVideoPacket(3, timestamp, 1, 3, 0, lineBytes, true); // Marker!

            RtpHeader.TryParse(p0, out var h0);
            RtpHeader.TryParse(p1, out var h1);
            RtpHeader.TryParse(p3, out var h3);

            Assert.Null(reassembler.ProcessPacket(in h0));
            Assert.Null(reassembler.ProcessPacket(in h1));
            var frame = reassembler.ProcessPacket(in h3);

            Assert.Null(frame); // Dropped due to missing line 2
            Assert.Equal(0, reassembler.FramesCompleted);
            Assert.Equal(1, reassembler.FramesDropped);
        }

        [Fact]
        public void Reassembler_TimestampChangeBeforeMarker_IncrementsDroppedFrames()
        {
            var reassembler = new ScanlineFrameReassembler();
            ushort lineBytes = 8;

            // Frame 1: Line 0 with timestamp 1000
            byte[] p0 = CreateRtpVideoPacket(0, 1000, 1, 0, 0, lineBytes, false);
            RtpHeader.TryParse(p0, out var h0);
            reassembler.ProcessPacket(in h0);

            // New timestamp 2000 arrives before Frame 1 finished
            byte[] p1 = CreateRtpVideoPacket(1, 2000, 1, 0, 0, lineBytes, false);
            RtpHeader.TryParse(p1, out var h1);
            reassembler.ProcessPacket(in h1);

            Assert.Equal(1, reassembler.FramesDropped);
            Assert.Equal(0, reassembler.FramesCompleted);
        }

        [Fact]
        public void Reassembler_Reset_ClearsInternalState()
        {
            var reassembler = new ScanlineFrameReassembler();
            byte[] p0 = CreateRtpVideoPacket(0, 1000, 1, 0, 0, 8, false);
            RtpHeader.TryParse(p0, out var h0);
            reassembler.ProcessPacket(in h0);

            reassembler.Reset();

            // Sending new frame with new timestamp does not drop previous frame because state was reset
            byte[] p1 = CreateRtpVideoPacket(1, 2000, 1, 0, 0, 8, true);
            RtpHeader.TryParse(p1, out var h1);
            var frame = reassembler.ProcessPacket(in h1);

            Assert.NotNull(frame);
            Assert.Equal(1, reassembler.FramesCompleted);
            Assert.Equal(0, reassembler.FramesDropped);
        }

        #endregion

        #region 4. YUV 4:2:2 Q10 Fixed-Point Color Space Conversion Tests

        [Fact]
        public void YuvRasterizer_NeutralGray_ConvertsToExactRgb()
        {
            // Y=128, U=128, V=128 should produce R=128, G=128, B=128
            var (r, g, b) = Yuv422Rasterizer.ConvertPixelQ10(128, 128, 128);

            Assert.Equal(128, r);
            Assert.Equal(128, g);
            Assert.Equal(128, b);
        }

        [Fact]
        public void YuvRasterizer_PureBlack_ConvertsToZeroRgb()
        {
            var (r, g, b) = Yuv422Rasterizer.ConvertPixelQ10(0, 128, 128);

            Assert.Equal(0, r);
            Assert.Equal(0, g);
            Assert.Equal(0, b);
        }

        [Fact]
        public void YuvRasterizer_PureWhite_ConvertsTo255Rgb()
        {
            var (r, g, b) = Yuv422Rasterizer.ConvertPixelQ10(255, 128, 128);

            Assert.Equal(255, r);
            Assert.Equal(255, g);
            Assert.Equal(255, b);
        }

        [Fact]
        public void YuvRasterizer_Q10MatchesTheoreticalDefinitionWithinOneLsb()
        {
            // Test grid across standard broadcast and full swing values
            byte[] testValues = { 0, 16, 64, 128, 192, 235, 255 };

            foreach (byte y in testValues)
            {
                foreach (byte u in testValues)
                {
                    foreach (byte v in testValues)
                    {
                        var expected = Yuv422Rasterizer.ConvertPixelFloat(y, u, v);
                        var actual = Yuv422Rasterizer.ConvertPixelQ10(y, u, v);

                        Assert.True(Math.Abs(expected.r - actual.r) <= 1, $"R mismatch for Y={y}, U={u}, V={v}: exp={expected.r}, act={actual.r}");
                        Assert.True(Math.Abs(expected.g - actual.g) <= 1, $"G mismatch for Y={y}, U={u}, V={v}: exp={expected.g}, act={actual.g}");
                        Assert.True(Math.Abs(expected.b - actual.b) <= 1, $"B mismatch for Y={y}, U={u}, V={v}: exp={expected.b}, act={actual.b}");
                    }
                }
            }
        }

        [Fact]
        public void YuvRasterizer_ClampLut_HandlesExtremeValuesCorrectly()
        {
            // Verify 1024-byte clamp LUT bounds
            Assert.Equal(0, Yuv422Rasterizer.Clamp(-1000));
            Assert.Equal(0, Yuv422Rasterizer.Clamp(-384));
            Assert.Equal(0, Yuv422Rasterizer.Clamp(-1));
            Assert.Equal(0, Yuv422Rasterizer.Clamp(0));
            Assert.Equal(128, Yuv422Rasterizer.Clamp(128));
            Assert.Equal(255, Yuv422Rasterizer.Clamp(255));
            Assert.Equal(255, Yuv422Rasterizer.Clamp(256));
            Assert.Equal(255, Yuv422Rasterizer.Clamp(639));
            Assert.Equal(255, Yuv422Rasterizer.Clamp(2000));
        }

        [Fact]
        public void YuvRasterizer_ConvertYuv422ToRgb24_ProducesCorrectMemoryLayout()
        {
            // Quad [U, Y0, V, Y1]: [128, 200, 128, 100]
            // Pixel 0: Y=200, U=128, V=128 -> (200, 200, 200)
            // Pixel 1: Y=100, U=128, V=128 -> (100, 100, 100)
            byte[] yuv = { 128, 200, 128, 100 };
            byte[] rgb = Yuv422Rasterizer.ConvertYuv422ToRgb24(yuv, 2, 1);

            Assert.Equal(6, rgb.Length);
            // Pixel 0: RGB order
            Assert.Equal(200, rgb[0]); // R
            Assert.Equal(200, rgb[1]); // G
            Assert.Equal(200, rgb[2]); // B

            // Pixel 1: RGB order
            Assert.Equal(100, rgb[3]); // R
            Assert.Equal(100, rgb[4]); // G
            Assert.Equal(100, rgb[5]); // B
        }

        [Fact]
        public void YuvRasterizer_ConvertYuv422ToBgr24_ProducesCorrectWindowsDIBOrder()
        {
            // Quad [U, Y0, V, Y1]: [128, 200, 128, 100]
            byte[] yuv = { 128, 200, 128, 100 };
            byte[] bgr = Yuv422Rasterizer.ConvertYuv422ToBgr24(yuv, 2, 1);

            Assert.Equal(6, bgr.Length);
            // Pixel 0: BGR order
            Assert.Equal(200, bgr[0]); // B
            Assert.Equal(200, bgr[1]); // G
            Assert.Equal(200, bgr[2]); // R

            // Pixel 1: BGR order
            Assert.Equal(100, bgr[3]); // B
            Assert.Equal(100, bgr[4]); // G
            Assert.Equal(100, bgr[5]); // R
        }

        [Fact]
        public void YuvRasterizer_ZeroAllocationSpan_WritesDirectlyToDestination()
        {
            byte[] yuv = { 128, 150, 128, 150 };
            Span<byte> destination = stackalloc byte[6];

            Yuv422Rasterizer.ConvertYuv422ToBgr24(yuv, destination, 2, 1);

            Assert.Equal(150, destination[0]);
            Assert.Equal(150, destination[1]);
            Assert.Equal(150, destination[2]);
            Assert.Equal(150, destination[3]);
            Assert.Equal(150, destination[4]);
            Assert.Equal(150, destination[5]);
        }

        #endregion

        #region 5. RtpMulticastReceiver Pipeline & Telemetry Tests

        [Fact]
        public void RtpMulticastReceiver_ProcessDatagram_ReassemblesAndTriggersEvents()
        {
            using var receiver = new RtpMulticastReceiver();
            byte[]? receivedRgb = null;
            int receivedWidth = 0;
            int receivedHeight = 0;
            double lastFps = 0.0;

            receiver.FrameReady += (rgb, w, h) =>
            {
                receivedRgb = rgb;
                receivedWidth = w;
                receivedHeight = h;
            };
            receiver.FpsUpdated += fps => lastFps = fps;

            // Generate 2 scanlines for a 2x2 frame
            ushort lineBytes = 4; // 2 pixels * 2 bytes
            uint timestamp = 123456;

            byte[] p0 = CreateRtpVideoPacket(0, timestamp, 1, 0, 0, lineBytes, marker: false);
            byte[] p1 = CreateRtpVideoPacket(1, timestamp, 1, 1, 0, lineBytes, marker: true);

            receiver.ProcessDatagram(p0);
            Assert.Null(receivedRgb);
            Assert.Equal(1, receiver.ReceivedPacketsCount);
            Assert.Equal(0, receiver.ReceivedFramesCount);

            receiver.ProcessDatagram(p1);
            Assert.NotNull(receivedRgb);
            Assert.Equal(2, receiver.ReceivedPacketsCount);
            Assert.Equal(1, receiver.ReceivedFramesCount);
            Assert.Equal(2, receivedWidth);
            Assert.Equal(2, receivedHeight);
            Assert.Equal(2 * 2 * 3, receivedRgb.Length);
            Assert.True(lastFps >= 1.0);
        }

        [Fact]
        public void RtpMulticastReceiver_StartAndStop_ManagesListeningStateCleanly()
        {
            using var receiver = new RtpMulticastReceiver();
            Assert.False(receiver.IsListening);

            // Start listening on ephemeral port
            int port = 49152 + (Random.Shared.Next(1000, 5000));
            receiver.StartListening("224.1.2.3", port);
            Assert.True(receiver.IsListening);
            Assert.Equal("224.1.2.3", receiver.MulticastIp);
            Assert.Equal(port, receiver.Port);

            // Stop listening
            receiver.StopListening();
            Assert.False(receiver.IsListening);
            Assert.Equal(0.0, receiver.CurrentFps);
        }

        #endregion
    }
}
