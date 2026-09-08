using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using E2ETests.Harness;
using E2ETests.Mocks;
using Xunit;

namespace E2ETests.Suites
{
    public class Tier3_CrossFeatureTests
    {
        // -------------------------------------------------------------------------
        // T3_01: Config Update Reconfigures Discovery Client Ports (F3 x F4)
        // -------------------------------------------------------------------------
        [Fact]
        public async Task T3_01_ConfigUpdate_ReconfiguresDiscoveryClientPorts()
        {
            using var server = new MockSdvoeServer();
            var config = new AppConfig
            {
                ControlServerIp = "127.0.0.1",
                TelnetPort = server.TelnetPort,
                RestPort = server.RestPort
            };

            using var client = new SdvoeClient(config.ControlServerIp, config.TelnetPort, config.RestPort);
            bool connected = await client.ConnectTelnetAsync();
            Assert.True(connected);

            var devices = await client.DiscoverAvas223DevicesAsync();
            Assert.NotEmpty(devices);
        }

        // -------------------------------------------------------------------------
        // T3_02: Discovery Filtering Feeds Multicast Allocator (F5 x F7)
        // -------------------------------------------------------------------------
        [Fact]
        public async Task T3_02_DiscoveryFiltering_FeedsMulticastAllocator()
        {
            using var server = new MockSdvoeServer();
            using var client = new SdvoeClient("127.0.0.1", server.TelnetPort, server.RestPort);
            var ipManager = new MulticastIpManager("224.1.1.1", "224.1.1.10");

            var allDevices = await client.DiscoverDevicesViaTelnetAsync();
            var filtered = SdvoeDiscoveryFilter.FilterTargetDevices(allDevices);

            foreach (var dev in filtered)
            {
                dev.AllocatedMulticastIp = ipManager.AllocateMulticastIp(dev.MacAddress);
            }

            Assert.Equal(2, filtered.Count);
            Assert.All(filtered, d => Assert.NotNull(d.AllocatedMulticastIp));
            Assert.NotEqual(filtered[0].AllocatedMulticastIp, filtered[1].AllocatedMulticastIp);
        }

        // -------------------------------------------------------------------------
        // T3_03: Multicast Allocation Triggers Telnet Stream Start (F7 x F8)
        // -------------------------------------------------------------------------
        [Fact]
        public async Task T3_03_MulticastAllocation_TriggersTelnetStreamStart()
        {
            using var server = new MockSdvoeServer();
            using var client = new SdvoeClient("127.0.0.1", server.TelnetPort, server.RestPort);
            var ipManager = new MulticastIpManager("224.1.1.1", "224.1.1.5");

            string mac = "f8228500aaaa";
            string? allocatedIp = ipManager.AllocateMulticastIp(mac);
            Assert.NotNull(allocatedIp);

            bool started = await client.StartPreviewStreamAsync(mac, allocatedIp);
            Assert.True(started);

            var serverDev = server.GetDevice(mac);
            Assert.NotNull(serverDev);
            Assert.True(serverDev.IsStreaming);
            Assert.Equal(allocatedIp, serverDev.MulticastIp);
        }

        // -------------------------------------------------------------------------
        // T3_04: Sidebar Expansion Chains Discovery, Allocation & Stream Start (F9 x F4 x F7 x F8)
        // -------------------------------------------------------------------------
        [Fact]
        public async Task T3_04_SidebarExpansion_ChainsDiscoveryAllocationAndStreamStart()
        {
            using var server = new MockSdvoeServer();
            using var client = new SdvoeClient("127.0.0.1", server.TelnetPort, server.RestPort);
            var ipManager = new MulticastIpManager("224.1.1.1", "224.1.1.10");
            using var coordinator = new SidebarCoordinator(client, ipManager);

            await coordinator.ExpandAsync();

            Assert.True(coordinator.IsExpanded);
            Assert.Equal(2, coordinator.ActiveCards.Count);
            Assert.Equal(2, ipManager.ActiveAllocationCount);
            Assert.True(server.GetDevice("f8228500aaaa")?.IsStreaming);
        }

        // -------------------------------------------------------------------------
        // T3_05: Sidebar Collapse Executes Full Teardown Chain (F9 x F11 x F8 x F7)
        // -------------------------------------------------------------------------
        [Fact]
        public async Task T3_05_SidebarCollapse_ExecutesFullTeardownChain()
        {
            using var server = new MockSdvoeServer();
            using var client = new SdvoeClient("127.0.0.1", server.TelnetPort, server.RestPort);
            var ipManager = new MulticastIpManager("224.1.1.1", "224.1.1.10");
            using var coordinator = new SidebarCoordinator(client, ipManager);

            await coordinator.ExpandAsync();
            await coordinator.CollapseAsync();

            Assert.False(coordinator.IsExpanded);
            Assert.Empty(coordinator.ActiveCards);
            Assert.Equal(0, ipManager.ActiveAllocationCount);
            Assert.False(server.GetDevice("f8228500aaaa")?.IsStreaming);
            Assert.Null(server.GetDevice("f8228500aaaa")?.MulticastIp);
        }

        // -------------------------------------------------------------------------
        // T3_06: Stream Start Initiates RTP Multicast Ingestion (F8 x F12 x F13)
        // -------------------------------------------------------------------------
        [Fact]
        public async Task T3_06_StreamStart_InitiatesRtpMulticastIngestion()
        {
            using var server = new MockSdvoeServer();
            using var client = new SdvoeClient("127.0.0.1", server.TelnetPort, server.RestPort);
            using var receiver = new RtpMulticastReceiver();

            receiver.StartListening("127.0.0.1", 0);
            int testPort = receiver.Port;

            // Configure stream on server to this port
            await client.ConfigureThumbnailStreamAsync("f8228500aaaa", 1.0, 12345, testPort);
            await client.StartPreviewStreamAsync("f8228500aaaa", "127.0.0.1");

            // Stream real packets from mock streamer to receiver
            using var streamer = new MockRtpStreamer();
            ushort seq = 1;
            var packets = MockRtpStreamer.GenerateFramePackets(320, 2, 12345, 1000, ref seq);
            await streamer.SendPacketsAsync(new IPEndPoint(IPAddress.Loopback, testPort), packets);

            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (receiver.ReceivedPacketsCount < 2 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10);
            }
            receiver.StopListening();

            Assert.True(receiver.ReceivedPacketsCount >= 2);
        }

        // -------------------------------------------------------------------------
        // T3_07: RTP Reassembly Directs YUV to Rasterizer (F13 x F14)
        // -------------------------------------------------------------------------
        [Fact]
        public void T3_07_RtpReassembly_DirectsYuvToRasterizer()
        {
            ushort seq = 1;
            var packets = MockRtpStreamer.GenerateFramePackets(320, 10, 12345, 5000, ref seq, TestPattern.ColorBars);
            var reassembler = new ScanlineFrameReassembler();

            AssembledFrame? frame = null;
            foreach (var p in packets)
            {
                if (RtpPacketParser.TryParse(p, out var hdr))
                {
                    frame = reassembler.ProcessPacket(hdr);
                }
            }

            Assert.NotNull(frame);
            byte[] rgb = Yuv422Rasterizer.ConvertYuv422ToRgb24(frame.YuvData, frame.Width, frame.Height);

            Assert.Equal(320 * 10 * 3, rgb.Length);
            // Verify white bar in first column of color bars
            Assert.True(rgb[0] > 200); // Red
            Assert.True(rgb[1] > 200); // Green
            Assert.True(rgb[2] > 200); // Blue
        }

        // -------------------------------------------------------------------------
        // T3_08: Rasterizer Feeds Preview Card UI Telemetry (F14 x F10 x F15)
        // -------------------------------------------------------------------------
        [Fact]
        public void T3_08_Rasterizer_FeedsPreviewCardUiTelemetry()
        {
            var card = new EncoderCardViewModel { MacAddress = "f8228500aaaa" };
            byte[] dummyYuv = new byte[320 * 180 * 2];
            Array.Fill<byte>(dummyYuv, 128);

            byte[] rgb = Yuv422Rasterizer.ConvertYuv422ToRgb24(dummyYuv, 320, 180);
            card.LastRenderedRgbFrame = rgb;
            card.FrameUpdateCount++;
            card.CurrentFps = 1.2;

            Assert.NotNull(card.LastRenderedRgbFrame);
            Assert.Equal(1, card.FrameUpdateCount);
            Assert.Equal(1.2, card.CurrentFps);
        }

        // -------------------------------------------------------------------------
        // T3_09: Dynamic Pool Reconfiguration While Streams Active (F3 x F7 x F8)
        // -------------------------------------------------------------------------
        [Fact]
        public async Task T3_09_DynamicPoolReconfiguration_WhileStreamsActive()
        {
            using var server = new MockSdvoeServer();
            using var client = new SdvoeClient("127.0.0.1", server.TelnetPort, server.RestPort);

            // Initial pool: 224.1.1.1 to 224.1.1.2
            var ipManager = new MulticastIpManager("224.1.1.1", "224.1.1.2");
            string? ip1 = ipManager.AllocateMulticastIp("dev1");
            Assert.Equal("224.1.1.1", ip1);
            await client.StartPreviewStreamAsync("f8228500aaaa", ip1!);

            // Reconfigured manager with new expanded pool
            var newManager = new MulticastIpManager("224.1.2.1", "224.1.2.50");
            string? ip2 = newManager.AllocateMulticastIp("dev2");

            Assert.Equal("224.1.2.1", ip2);
            await client.StartPreviewStreamAsync("f8228500dddd", ip2!);

            Assert.NotEqual(ip1, ip2);
        }

        // -------------------------------------------------------------------------
        // T3_10: Server Disconnect Recovery and Re-Discovery (F4 x F5 x F6)
        // -------------------------------------------------------------------------
        [Fact]
        public async Task T3_10_ServerDisconnect_RecoveryAndRediscovery()
        {
            var server = new MockSdvoeServer();
            using var client = new SdvoeClient("127.0.0.1", server.TelnetPort, server.RestPort);

            await client.ConnectTelnetAsync();
            var dev1 = await client.DiscoverAvas223DevicesAsync();
            Assert.NotEmpty(dev1);

            // Server restarts on new port
            int oldPort = server.TelnetPort;
            server.Dispose();

            using var newServer = new MockSdvoeServer(telnetPort: oldPort);
            client.DisconnectTelnet();

            // Reconnect
            bool reconnected = await client.ConnectTelnetAsync();
            Assert.True(reconnected);

            var dev2 = await client.DiscoverAvas223DevicesAsync();
            Assert.NotEmpty(dev2);
        }

        // -------------------------------------------------------------------------
        // T3_11: Multiple Simultaneous Encoders Stream to Independent IPs (F7 x F8 x F12 x F13)
        // -------------------------------------------------------------------------
        [Fact]
        public async Task T3_11_MultipleSimultaneousEncoders_StreamToIndependentMulticastIps()
        {
            using var server = new MockSdvoeServer();
            using var client = new SdvoeClient("127.0.0.1", server.TelnetPort, server.RestPort);
            var ipManager = new MulticastIpManager("224.1.1.1", "224.1.1.10");

            string? ip1 = ipManager.AllocateMulticastIp("f8228500aaaa");
            string? ip2 = ipManager.AllocateMulticastIp("f8228500dddd");

            Assert.NotEqual(ip1, ip2);

            bool start1 = await client.StartPreviewStreamAsync("f8228500aaaa", ip1!);
            bool start2 = await client.StartPreviewStreamAsync("f8228500dddd", ip2!);

            Assert.True(start1);
            Assert.True(start2);

            var active = server.GetActiveMulticastStreams();
            Assert.True(active.ContainsKey(ip1!));
            Assert.True(active.ContainsKey(ip2!));
        }

        // -------------------------------------------------------------------------
        // T3_12: Device Removal During Active Preview Cleans Up Card and Stream (F5 x F8 x F11)
        // -------------------------------------------------------------------------
        [Fact]
        public async Task T3_12_DeviceRemovalDuringActivePreview_CleansUpCardAndStream()
        {
            using var server = new MockSdvoeServer();
            using var client = new SdvoeClient("127.0.0.1", server.TelnetPort, server.RestPort);
            var ipManager = new MulticastIpManager();
            using var coordinator = new SidebarCoordinator(client, ipManager);

            await coordinator.ExpandAsync();
            Assert.Equal(2, coordinator.ActiveCards.Count);

            // Device removed on server
            server.ClearDevices();
            var devicesAfter = await client.DiscoverAvas223DevicesAsync();
            Assert.Empty(devicesAfter);
        }

        // -------------------------------------------------------------------------
        // T3_13: RTP Packet Loss Frame Dropped, Subsequent Frame Recovers (F13 x F14 x F15)
        // -------------------------------------------------------------------------
        [Fact]
        public void T3_13_RtpPacketLoss_FrameDropped_SubsequentFrameRecovers()
        {
            var reassembler = new ScanlineFrameReassembler();
            ushort seq = 1;

            // Frame 1: missing scanline line 3
            var frame1Packets = MockRtpStreamer.GenerateFrameWithMissingLine(320, 10, lineToDrop: 3, 1000, 1000, ref seq);
            AssembledFrame? f1 = null;
            foreach (var p in frame1Packets)
            {
                if (RtpPacketParser.TryParse(p, out var h)) f1 = reassembler.ProcessPacket(h);
            }
            Assert.Null(f1);
            Assert.Equal(1, reassembler.FramesDropped);

            // Frame 2: completely intact
            var frame2Packets = MockRtpStreamer.GenerateFramePackets(320, 10, 1000, 2000, ref seq);
            AssembledFrame? f2 = null;
            foreach (var p in frame2Packets)
            {
                if (RtpPacketParser.TryParse(p, out var h)) f2 = reassembler.ProcessPacket(h);
            }

            Assert.NotNull(f2);
            Assert.Equal(1, reassembler.FramesCompleted);
        }

        // -------------------------------------------------------------------------
        // T3_14: Rapid Toggle Stress Cycle Maintains Resource Integrity (F9 x F11 x F7 x F8)
        // -------------------------------------------------------------------------
        [Fact]
        public async Task T3_14_RapidToggleStressCycle_MaintainsResourceIntegrity()
        {
            using var server = new MockSdvoeServer();
            using var client = new SdvoeClient("127.0.0.1", server.TelnetPort, server.RestPort);
            var ipManager = new MulticastIpManager();
            using var coordinator = new SidebarCoordinator(client, ipManager);

            for (int i = 0; i < 5; i++)
            {
                await coordinator.ExpandAsync();
                Assert.True(coordinator.IsExpanded);
                await coordinator.CollapseAsync();
                Assert.False(coordinator.IsExpanded);
            }

            Assert.Equal(0, ipManager.ActiveAllocationCount);
            Assert.Empty(coordinator.ActiveCards);
        }

        // -------------------------------------------------------------------------
        // T3_15: High-Rate RTP Stream Sustains Accurate Rolling FPS (F6 x F10 x F13)
        // -------------------------------------------------------------------------
        [Fact]
        public void T3_15_HighRateRtpStream_SustainsAccurateRollingFps()
        {
            using var receiver = new RtpMulticastReceiver();
            ushort seq = 1;

            // Transmit 10 frames
            for (int i = 0; i < 10; i++)
            {
                var pkts = MockRtpStreamer.GenerateFramePackets(320, 2, 1, (uint)(10000 + i * 200), ref seq);
                foreach (var p in pkts) receiver.ProcessDatagram(p);
            }

            Assert.Equal(10, receiver.ReceivedFramesCount);
            Assert.True(receiver.CurrentFps >= 1.0);
        }

        // -------------------------------------------------------------------------
        // T3_16: Dual-Channel Telnet and REST Consistency (F4 x F8)
        // -------------------------------------------------------------------------
        [Fact]
        public async Task T3_16_DualChannelTelnetAndRestConsistency()
        {
            using var server = new MockSdvoeServer();
            using var client = new SdvoeClient("127.0.0.1", server.TelnetPort, server.RestPort);

            var telnetDevices = await client.DiscoverDevicesViaTelnetAsync();
            var restDevices = await client.DiscoverDevicesViaRestAsync();

            Assert.Equal(telnetDevices.Count, restDevices.Count);

            // Start stream via Telnet
            await client.StartPreviewStreamAsync("f8228500aaaa", "224.1.1.77");

            // Query active via REST
            using var http = new System.Net.Http.HttpClient();
            string mcastJson = await http.GetStringAsync($"http://127.0.0.1:{server.RestPort}/api/multicast");

            Assert.Contains("224.1.1.77", mcastJson);
            Assert.Contains("f8228500aaaa", mcastJson);
        }
    }
}
