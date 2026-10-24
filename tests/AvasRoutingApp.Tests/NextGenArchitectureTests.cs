using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using AvasRoutingApp.Rtp;
using AvasRoutingApp.Sdvoe;
using AvasRoutingApp.Security;
using AvasRoutingApp.ViewModels;

namespace AvasRoutingApp.Tests
{
    public class NextGenArchitectureTests
    {
        #region 1. SdvoeHash Unit Tests

        [Fact]
        public void SdvoeHash_HashEncode_GeneratesValid16ByteHexKey()
        {
            string mac = "00:0b:90:11:22:33";
            string sn = "ADV123456";

            string hash = SdvoeHash.HashEncode(mac, sn);

            Assert.False(string.IsNullOrWhiteSpace(hash));
            var parts = hash.Split(',');
            Assert.Equal(16, parts.Length);
            foreach (var part in parts)
            {
                Assert.True(part.Length == 2, $"Expected 2 hex chars, got '{part}'");
                Assert.True(byte.TryParse(part, System.Globalization.NumberStyles.HexNumber, null, out _));
            }
        }

        [Fact]
        public void SdvoeHash_CheckHashKey_ValidatesCorrectly()
        {
            string mac = "00:0b:90:11:22:33";
            string sn = "ADV123456";
            string hash = SdvoeHash.HashEncode(mac, sn);

            bool isValid = SdvoeHash.CheckHashKey(mac, sn, hash);
            Assert.True(isValid);

            bool wrongMac = SdvoeHash.CheckHashKey("00:0b:90:11:22:34", sn, hash);
            Assert.False(wrongMac);

            bool corruptedHash = SdvoeHash.CheckHashKey(mac, sn, "00,00,00,00,00,00,00,00,00,00,00,00,00,00,00,00");
            Assert.False(corruptedHash);
        }

        [Theory]
        [InlineData("", "SN123")]
        [InlineData("   ", "SN123")]
        [InlineData("00:0b:90:11:22:33", "")]
        [InlineData("00:0b:90:11:22:33", "   ")]
        public void SdvoeHash_HashEncode_EmptyOrWhitespaceInputs_ThrowsArgumentException(string mac, string sn)
        {
            Assert.Throws<ArgumentException>(() => SdvoeHash.HashEncode(mac, sn));
        }

        [Fact]
        public void SdvoeHash_CheckHashKey_NullOrEmptyInputs_ReturnsFalse()
        {
            Assert.False(SdvoeHash.CheckHashKey("", "SN123", "hash"));
            Assert.False(SdvoeHash.CheckHashKey("00:0b:90:11:22:33", "", "hash"));
            Assert.False(SdvoeHash.CheckHashKey("00:0b:90:11:22:33", "SN123", ""));
        }

        [Fact]
        public void SdvoeHash_HashDecode_ReturnsDecodedComponents()
        {
            string mac = "00:0b:90:11:22:33";
            string sn = "ADV123456";
            string hash = SdvoeHash.HashEncode(mac, sn);

            string decoded = SdvoeHash.HashDecode(hash);
            Assert.False(string.IsNullOrWhiteSpace(decoded));
        }

        #endregion

        #region 2. MCU Activation & Telemetry Tests

        [Theory]
        [InlineData("status : Authorize on\r\n", DeviceActivationState.Activated)]
        [InlineData("status : Authorize on", DeviceActivationState.Activated)]
        [InlineData("Authorize: on", DeviceActivationState.Activated)]
        [InlineData("Authorize:on", DeviceActivationState.Activated)]
        [InlineData("status : Authorize:on", DeviceActivationState.Activated)]
        [InlineData("AUTHORIZE:ON", DeviceActivationState.Activated)]
        [InlineData("status : Authorize off\r\n", DeviceActivationState.Unactivated)]
        [InlineData("status : Authorize off", DeviceActivationState.Unactivated)]
        [InlineData("Authorize: off", DeviceActivationState.Unactivated)]
        [InlineData("Authorize:off", DeviceActivationState.Unactivated)]
        [InlineData("status:Authorize off", DeviceActivationState.Unactivated)]
        [InlineData("unknown response", DeviceActivationState.Unknown)]
        [InlineData("", DeviceActivationState.Unknown)]
        [InlineData(null, DeviceActivationState.Unknown)]
        public void SdvoeClient_ParseActivationState_DetectsStatusAccurately(string? raw, DeviceActivationState expected)
        {
            var actual = SdvoeClient.ParseActivationState(raw!);
            Assert.Equal(expected, actual);
        }

        [Fact]
        public void SdvoeClient_ParseMcuField_MatchesPrefixAndIgnoresUnrelatedLines()
        {
            string raw = "echo: get fw ver\r\nstatus : SUCCESS\r\nfw ver : V2.23\r\n";
            string fw = SdvoeClient.ParseMcuField(raw, "fw ver");
            Assert.Equal("V2.23", fw);

            string hw = SdvoeClient.ParseMcuField(raw, "hw ver", "REV.UNKNOWN");
            Assert.Equal("REV.UNKNOWN", hw);

            // Empty raw input returns fallback without crashing
            Assert.Equal("", SdvoeClient.ParseMcuField("", "fw ver"));
        }

        [Fact]
#pragma warning disable CS0618
        public void EncoderCardViewModel_ActivationProperties_UpdateAndNotifyCorrectly()
        {
            var vm = new EncoderCardViewModel();
            Assert.False(vm.IsActivated);
            Assert.Equal("Checking...", vm.ActivationBadgeText);

            vm.ActivationState = DeviceActivationState.Activated;
            Assert.True(vm.IsActivated);
            Assert.Equal("Activated", vm.ActivationBadgeText);
            Assert.NotNull(vm.ActivationBadgeBrush);

            vm.FirmwareVersion = "V2.23";
            vm.HardwareVersion = "REV.A";
            vm.FactorySerialNumber = "ADV901122";

            Assert.Contains("Activation: Activated", vm.McuTooltipText);
            Assert.Contains("MCU FW: V2.23", vm.McuTooltipText);
            Assert.Contains("Hardware: REV.A", vm.McuTooltipText);
            Assert.Contains("Serial No: ADV901122", vm.McuTooltipText);

            vm.ActivationState = DeviceActivationState.Unactivated;
            Assert.False(vm.IsActivated);
            Assert.Equal("Unactivated", vm.ActivationBadgeText);
            Assert.Contains("Activation: Unactivated", vm.McuTooltipText);
        }
#pragma warning restore CS0618

        [Fact]
        public void EncoderCardViewModel_TooltipText_HidesEmptyFields()
        {
            var vm = new EncoderCardViewModel();
            vm.ActivationState = DeviceActivationState.Activated;
            // FW, HW, SN are unpopulated (empty)
            Assert.Equal("Activation: Activated", vm.McuTooltipText);
            Assert.DoesNotContain("MCU FW:", vm.McuTooltipText);
            Assert.DoesNotContain("Hardware:", vm.McuTooltipText);
            Assert.DoesNotContain("Serial No:", vm.McuTooltipText);
        }

        #endregion

        #region 3. Multicast Socket Local NIC Binding & Demux Aliasing Tests

        [Fact]
        public void UnifiedRtpDemuxReceiver_EnsureMulticastGroupJoined_AcceptsExplicitLocalNic()
        {
            using var demuxer = new UnifiedRtpDemuxReceiver(5099);
            var loopbackNic = IPAddress.Loopback;

            // Explicit local NIC should be accepted without throwing exceptions
            demuxer.EnsureMulticastGroupJoined("225.1.1.10", loopbackNic);
            demuxer.EnsureMulticastGroupJoined("225.1.1.11", "127.0.0.1");

            // Calling again is idempotent
            demuxer.EnsureMulticastGroupJoined("225.1.1.10", loopbackNic);
        }

        [Fact]
        public void UnifiedRtpDemuxReceiver_RegisterAlias_RoutesPacketsToAliasedChannel()
        {
            using var demuxer = new UnifiedRtpDemuxReceiver(5098);
            string primaryIp = "192.168.1.100";
            string companionMac = "00:0b:90:88:99:aa";
            string secondarySfpIp = "10.0.0.50";

            var channel = (UnifiedRtpDemuxReceiver.DemuxChannel)demuxer.GetOrCreateChannel(
                primaryIp,
                "225.1.1.5",
                new[] { companionMac, secondarySfpIp });

            Assert.Equal(2, demuxer.AliasCount);

            // Construct mock packet
            byte[] packet = CreateMockRtpPacket(seqNo: 1, timestamp: 5000, lineNo: 0, length: 16);

            // Send packet originating from secondary SFP IP
            demuxer.DemuxPacket(secondarySfpIp, packet);
            Assert.Equal(1, channel.ReceivedPacketsCount);

            // Send packet originating from primary IP
            demuxer.DemuxPacket(primaryIp, packet);
            Assert.Equal(2, channel.ReceivedPacketsCount);
        }

        [Fact]
        public void UnifiedRtpDemuxReceiver_SubnetFallback_RoutesPacketsFromSameSubnet()
        {
            using var demuxer = new UnifiedRtpDemuxReceiver(5097);
            string primaryIp = "192.168.20.10";
            var channel = (UnifiedRtpDemuxReceiver.DemuxChannel)demuxer.GetOrCreateChannel(primaryIp, "225.1.1.6");

            byte[] packet = CreateMockRtpPacket(seqNo: 1, timestamp: 5000, lineNo: 0, length: 16);

            // Packet arrives from 192.168.20.25 (same /24 subnet, e.g. alternate interface)
            demuxer.DemuxPacket("192.168.20.25", packet);

            Assert.Equal(1, channel.ReceivedPacketsCount);
            // Verify that it auto-aliased the IP
            Assert.True(demuxer.AliasCount >= 1);
        }

        [Fact]
        public void UnifiedRtpDemuxReceiver_RemoveChannel_CleansUpAliases()
        {
            using var demuxer = new UnifiedRtpDemuxReceiver(5096);
            string primaryIp = "192.168.30.10";
            string alias = "192.168.30.11";

            demuxer.GetOrCreateChannel(primaryIp, "225.1.1.7", new[] { alias });
            Assert.Equal(1, demuxer.AliasCount);

            demuxer.RemoveChannel(primaryIp);
            Assert.Equal(0, demuxer.ChannelCount);
            Assert.Equal(0, demuxer.AliasCount);
        }

        [Fact]
        public void UnifiedRtpDemuxReceiver_SubnetFallback_MultipleChannelsOnSameSubnet_DoesNotCrossTalk()
        {
            using var demuxer = new UnifiedRtpDemuxReceiver(5095);
            string ip1 = "192.168.10.10";
            string ip2 = "192.168.10.20";

            var ch1 = (UnifiedRtpDemuxReceiver.DemuxChannel)demuxer.GetOrCreateChannel(ip1, "225.1.1.1");
            var ch2 = (UnifiedRtpDemuxReceiver.DemuxChannel)demuxer.GetOrCreateChannel(ip2, "225.1.1.2");

            byte[] packet = CreateMockRtpPacket(seqNo: 1, timestamp: 5000, lineNo: 0, length: 16);

            // An unknown IP on the same subnet arrives (e.g. 192.168.10.99)
            // It should NOT be routed to either channel since it's ambiguous
            demuxer.DemuxPacket("192.168.10.99", packet);

            Assert.Equal(0, ch1.ReceivedPacketsCount);
            Assert.Equal(0, ch2.ReceivedPacketsCount);
        }

        #endregion

        #region 4. Scanline Reassembly Line Wrap & Tolerance Tests

        [Fact]
        public void ScanlineFrameReassembler_LineWrapWithoutMarker_RecoversPreviousFrame()
        {
            var reassembler = new ScanlineFrameReassembler(allowPartialFrames: true);
            ushort lineBytes = 8;
            uint frame1Ts = 1000;

            // Frame 1: feed lines 0 to 19 without marker (simulating dropped marker)
            for (ushort line = 0; line < 20; line++)
            {
                byte[] p = CreateMockRtpPacket(line, frame1Ts + line, line, lineBytes, marker: false);
                RtpHeader.TryParse(p, out var h);
                var f = reassembler.ProcessPacket(in h);
                Assert.Null(f);
            }

            Assert.Equal(0, reassembler.FramesCompleted);

            // Frame 2 starts: line wraps backwards to line 0 with advanced timestamp (> 120 jump)
            byte[] pNext = CreateMockRtpPacket(20, frame1Ts + 500, 0, lineBytes, marker: false);
            RtpHeader.TryParse(pNext, out var hNext);
            var recoveredFrame = reassembler.ProcessPacket(in hNext);

            Assert.NotNull(recoveredFrame);
            Assert.Equal(20, recoveredFrame.Height);
            Assert.Equal(4, recoveredFrame.Width);
            Assert.Equal(1, reassembler.FramesCompleted);
        }

        [Fact]
        public void ScanlineFrameReassembler_TimestampDeltaLeap_TriggersNewFrameBoundary()
        {
            var reassembler = new ScanlineFrameReassembler(allowPartialFrames: true);
            ushort lineBytes = 8;
            uint frame1Ts = 5000;

            // Buffer lines 0 to 14
            for (ushort line = 0; line < 15; line++)
            {
                byte[] p = CreateMockRtpPacket(line, frame1Ts, line, lineBytes, marker: false);
                RtpHeader.TryParse(p, out var h);
                reassembler.ProcessPacket(in h);
            }

            // Packet arrives with timestamp delta 1500 (>= 1000 leap tolerance between frames)
            byte[] leapPacket = CreateMockRtpPacket(15, frame1Ts + 1500, 15, lineBytes, marker: false);
            RtpHeader.TryParse(leapPacket, out var leapHeader);
            var recoveredFrame = reassembler.ProcessPacket(in leapHeader);

            Assert.NotNull(recoveredFrame);
            Assert.Equal(15, recoveredFrame.Height);
            Assert.Equal(1, reassembler.FramesCompleted);
        }

        [Fact]
        public void ScanlineFrameReassembler_OutOfOrderPackets_DoesNotDropFrame()
        {
            var reassembler = new ScanlineFrameReassembler(allowPartialFrames: false);
            ushort lineBytes = 8;
            uint frameTs = 1000;

            // Order: 0, 1, 2, 6, 3, 4, 5, 7 (marker on 7)
            ushort[] order = new ushort[] { 0, 1, 2, 6, 3, 4, 5, 7 };
            AssembledFrame? finalFrame = null;
            foreach (var line in order)
            {
                bool isMarker = (line == 7);
                byte[] p = CreateMockRtpPacket(line, frameTs + line, line, lineBytes, marker: isMarker);
                RtpHeader.TryParse(p, out var h);
                var f = reassembler.ProcessPacket(in h);
                if (f != null) finalFrame = f;
            }

            Assert.NotNull(finalFrame);
            Assert.Equal(8, finalFrame.Height);
            Assert.Equal(0, reassembler.FramesDropped);
        }

        [Fact]
        public void ScanlineFrameReassembler_DuplicatePacket_DoesNotNukeFrame()
        {
            var reassembler = new ScanlineFrameReassembler(allowPartialFrames: false);
            ushort lineBytes = 8;
            uint frameTs = 2000;

            // Order: 0, 1, 2, 2 (dup), 3, 4 (marker on 4)
            ushort[] order = new ushort[] { 0, 1, 2, 2, 3, 4 };
            AssembledFrame? finalFrame = null;
            foreach (var line in order)
            {
                bool isMarker = (line == 4);
                byte[] p = CreateMockRtpPacket(line, frameTs + line, line, lineBytes, marker: isMarker);
                RtpHeader.TryParse(p, out var h);
                var f = reassembler.ProcessPacket(in h);
                if (f != null) finalFrame = f;
            }

            Assert.NotNull(finalFrame);
            Assert.Equal(5, finalFrame.Height);
            Assert.Equal(0, reassembler.FramesDropped);
        }

        #endregion

        #region Helper

        private static byte[] CreateMockRtpPacket(ushort seqNo, uint timestamp, ushort lineNo, ushort length, bool marker = false)
        {
            byte[] packet = new byte[20 + length];
            packet[0] = 0x80; // V=2
            packet[1] = (byte)((marker ? 0x80 : 0x00) | 96);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), seqNo);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4, 4), timestamp);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8, 4), 1);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(14, 2), length);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(16, 2), lineNo);

            for (int i = 0; i < length; i += 4)
            {
                packet[20 + i] = 128;
                packet[20 + i + 1] = 180;
                packet[20 + i + 2] = 128;
                packet[20 + i + 3] = 180;
            }
            return packet;
        }

        #endregion
    }
}
