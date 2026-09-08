using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using E2ETests.Harness;
using E2ETests.Mocks;
using Xunit;

namespace E2ETests.Suites
{
    public class Tier4_ApplicationScenarioTests
    {
        // =========================================================================
        // SCENARIO 1: Cold Launch -> Discover -> Expand -> Stream Preview -> Teardown
        // =========================================================================
        [Fact]
        public async Task Scenario01_ColdLaunch_Discover_Expand_Preview_Teardown()
        {
            // 1. Setup Environment & Configuration
            string configPath = Path.Combine(Path.GetTempPath(), $"avas_cfg_{Guid.NewGuid():N}.json");
            using var server = new MockSdvoeServer();

            try
            {
                var configService = new ConfigServiceHelper(configPath);
                var config = new AppConfig
                {
                    ControlServerIp = "127.0.0.1",
                    TelnetPort = server.TelnetPort,
                    RestPort = server.RestPort,
                    MulticastStartIp = "224.1.1.1",
                    MulticastEndIp = "224.1.1.10",
                    BasePort = 6792
                };
                configService.Save(config);

                // 2. Initialize Client and Multicast Allocator
                using var client = new SdvoeClient(config.ControlServerIp, config.TelnetPort, config.RestPort);
                var ipManager = new MulticastIpManager(config.MulticastStartIp, config.MulticastEndIp, config.BasePort);
                using var coordinator = new SidebarCoordinator(client, ipManager);

                // 3. Verify Initial State (Collapsed by default per R4.1)
                Assert.False(coordinator.IsExpanded);
                Assert.Equal(0.0, coordinator.SidebarWidth);
                Assert.Equal(28.0, coordinator.ToggleStripWidth);
                Assert.Equal(0, ipManager.ActiveAllocationCount);

                // 4. User Expands Sidebar
                await coordinator.ExpandAsync();
                Assert.True(coordinator.IsExpanded);
                Assert.Equal(380.0, coordinator.SidebarWidth);
                Assert.Equal(2, coordinator.ActiveCards.Count);

                // 5. Ingest Live Video Frames
                var card = coordinator.ActiveCards.First();
                ushort seq = 1;
                var framePackets = MockRtpStreamer.GenerateFramePackets(320, 10, 12345, 1000, ref seq, TestPattern.ColorBars);

                foreach (var p in framePackets)
                {
                    card.Receiver?.ProcessDatagram(p);
                }

                Assert.NotNull(card.LastRenderedRgbFrame);
                Assert.True(card.FrameUpdateCount > 0);
                Assert.Equal("320x10", card.Resolution);

                // 6. User Collapses Sidebar -> Instant Teardown
                await coordinator.CollapseAsync();
                Assert.False(coordinator.IsExpanded);
                Assert.Equal(0.0, coordinator.SidebarWidth);
                Assert.Empty(coordinator.ActiveCards);
                Assert.Equal(0, ipManager.ActiveAllocationCount);
            }
            finally
            {
                if (File.Exists(configPath)) File.Delete(configPath);
            }
        }

        // =========================================================================
        // SCENARIO 2: Mixed-Fleet Dense Network with Strict Chip 0 Isolation
        // =========================================================================
        [Fact]
        public async Task Scenario02_MixedFleetDenseNetwork_StrictChip0Isolation()
        {
            using var server = new MockSdvoeServer();
            server.ClearDevices();

            // Populate dense network:
            // 4x AVAS-223 Chip 0
            for (int i = 0; i < 4; i++)
            {
                server.AddDevice(new MockDevice
                {
                    MacAddress = $"f8228500000{i}",
                    DeviceName = $"AVAS-223-TX0-Unit{i}",
                    VendorId = 105,
                    ProductId = 81,
                    ChipIndex = 0,
                    IsTransmitter = true
                });
            }

            // 4x AVAS-223 Chip 1 (Secondary link aggregation processors)
            for (int i = 0; i < 4; i++)
            {
                server.AddDevice(new MockDevice
                {
                    MacAddress = $"f8228500001{i}",
                    DeviceName = $"AVAS-223-TX1-Unit{i}",
                    VendorId = 105,
                    ProductId = 81,
                    ChipIndex = 1,
                    IsTransmitter = true
                });
            }

            // 2x AVAS-223 Receivers
            for (int i = 0; i < 2; i++)
            {
                server.AddDevice(new MockDevice
                {
                    MacAddress = $"f8228500002{i}",
                    DeviceName = $"AVAS-223-RX-Unit{i}",
                    VendorId = 105,
                    ProductId = 81,
                    ChipIndex = 0,
                    IsTransmitter = false,
                    IsReceiver = true
                });
            }

            // 2x Third-Party Transmitters
            for (int i = 0; i < 2; i++)
            {
                server.AddDevice(new MockDevice
                {
                    MacAddress = $"00112200000{i}",
                    DeviceName = $"Generic-TX-Unit{i}",
                    VendorId = 999,
                    ProductId = 50,
                    ChipIndex = 0,
                    IsTransmitter = true
                });
            }

            Assert.Equal(12, server.GetAllDevices().Count);

            using var client = new SdvoeClient("127.0.0.1", server.TelnetPort, server.RestPort);
            var ipManager = new MulticastIpManager("224.1.1.1", "224.1.1.50");
            using var coordinator = new SidebarCoordinator(client, ipManager);

            await coordinator.ExpandAsync();

            // Strictly only the 4 Chip 0 transmitters are previewed
            Assert.Equal(4, coordinator.ActiveCards.Count);
            Assert.All(coordinator.ActiveCards, c => Assert.StartsWith("f8228500000", c.MacAddress));

            // Verify unique IPs assigned
            var assignedIps = coordinator.ActiveCards.Select(c => c.MulticastIp).Distinct().ToList();
            Assert.Equal(4, assignedIps.Count);

            await coordinator.CollapseAsync();
        }

        // =========================================================================
        // SCENARIO 3: Network Interruption During Active Stream & Recovery
        // =========================================================================
        [Fact]
        public async Task Scenario03_NetworkInterruptionDuringActiveStream_RecoversGracefully()
        {
            var server = new MockSdvoeServer();
            int serverPort = server.TelnetPort;

            using var client = new SdvoeClient("127.0.0.1", serverPort, server.RestPort);
            var ipManager = new MulticastIpManager();
            var coordinator = new SidebarCoordinator(client, ipManager);

            await coordinator.ExpandAsync();
            Assert.Equal(2, coordinator.ActiveCards.Count);

            // Simulate server network crash
            server.Dispose();

            // Client detects connection loss
            client.DisconnectTelnet();

            // Server restarts on same port
            using var recoveredServer = new MockSdvoeServer(telnetPort: serverPort);

            // Reconnect and rediscover
            bool reconnected = await client.ConnectTelnetAsync();
            Assert.True(reconnected);

            var recoveredDevices = await client.DiscoverAvas223DevicesAsync();
            Assert.NotEmpty(recoveredDevices);

            await coordinator.CollapseAsync();
        }

        // =========================================================================
        // SCENARIO 4: Multicast Pool Depletion Under High Density & Graceful Degradation
        // =========================================================================
        [Fact]
        public async Task Scenario04_MulticastPoolDepletionUnderHighDensity_GracefulDegradation()
        {
            using var server = new MockSdvoeServer();
            // Tiny pool: only 1 IP available
            var ipManager = new MulticastIpManager("224.1.1.1", "224.1.1.1");
            using var client = new SdvoeClient("127.0.0.1", server.TelnetPort, server.RestPort);
            using var coordinator = new SidebarCoordinator(client, ipManager);

            await coordinator.ExpandAsync();

            // First encoder gets the 1 available IP, second is omitted gracefully
            Assert.Single(coordinator.ActiveCards);
            Assert.Equal(1, ipManager.ActiveAllocationCount);

            // Collapsing frees the IP
            await coordinator.CollapseAsync();
            Assert.Equal(0, ipManager.ActiveAllocationCount);
        }

        // =========================================================================
        // SCENARIO 5: Dynamic Settings Reconfiguration While Running
        // =========================================================================
        [Fact]
        public async Task Scenario05_DynamicSettingsReconfigurationWhileRunning()
        {
            string cfgPath = Path.Combine(Path.GetTempPath(), $"dyn_cfg_{Guid.NewGuid():N}.json");
            using var server1 = new MockSdvoeServer();
            using var server2 = new MockSdvoeServer();

            try
            {
                var cfgService = new ConfigServiceHelper(cfgPath);
                var config = new AppConfig
                {
                    ControlServerIp = "127.0.0.1",
                    TelnetPort = server1.TelnetPort,
                    MulticastStartIp = "224.1.1.1",
                    MulticastEndIp = "224.1.1.10"
                };
                cfgService.Save(config);

                // Expand on server 1
                var ipMgr1 = new MulticastIpManager(config.MulticastStartIp, config.MulticastEndIp);
                using var client1 = new SdvoeClient("127.0.0.1", server1.TelnetPort, server1.RestPort);
                var coord1 = new SidebarCoordinator(client1, ipMgr1);
                await coord1.ExpandAsync();
                Assert.True(server1.GetDevice("f8228500aaaa")?.IsStreaming);

                // User reconfigures to server 2 and new pool 224.1.2.1-224.1.2.10
                await coord1.CollapseAsync();
                config.TelnetPort = server2.TelnetPort;
                config.RestPort = server2.RestPort;
                config.MulticastStartIp = "224.1.2.1";
                config.MulticastEndIp = "224.1.2.10";
                cfgService.Save(config);

                var ipMgr2 = new MulticastIpManager(config.MulticastStartIp, config.MulticastEndIp);
                using var client2 = new SdvoeClient("127.0.0.1", server2.TelnetPort, server2.RestPort);
                var coord2 = new SidebarCoordinator(client2, ipMgr2);
                await coord2.ExpandAsync();

                Assert.True(server2.GetDevice("f8228500aaaa")?.IsStreaming);
                Assert.StartsWith("224.1.2.", server2.GetDevice("f8228500aaaa")?.MulticastIp);

                await coord2.CollapseAsync();
            }
            finally
            {
                if (File.Exists(cfgPath)) File.Delete(cfgPath);
            }
        }

        // =========================================================================
        // SCENARIO 6: Extended Streaming Endurance (60 Frames) & Memory Stability
        // =========================================================================
        [Fact]
        public void Scenario06_ExtendedStreamingEndurance_60Frames_MemoryStability()
        {
            using var receiver = new RtpMulticastReceiver();
            var reassembler = new ScanlineFrameReassembler();
            var bitmap = new WriteableBitmap(320, 180, 96, 96, PixelFormats.Bgr24, null);

            ushort seq = 1;
            long memStart = GC.GetTotalMemory(true);

            for (int f = 0; f < 60; f++)
            {
                var packets = MockRtpStreamer.GenerateFramePackets(320, 180, 12345, (uint)(f * 1000), ref seq, TestPattern.ColorBars);
                AssembledFrame? assembled = null;

                foreach (var p in packets)
                {
                    if (RtpPacketParser.TryParse(p, out var h))
                    {
                        assembled = reassembler.ProcessPacket(h);
                    }
                }

                Assert.NotNull(assembled);
                byte[] bgr = Yuv422Rasterizer.ConvertYuv422ToBgr24(assembled.YuvData, 320, 180);

                bitmap.Lock();
                try
                {
                    System.Runtime.InteropServices.Marshal.Copy(bgr, 0, bitmap.BackBuffer, bgr.Length);
                    bitmap.AddDirtyRect(new System.Windows.Int32Rect(0, 0, 320, 180));
                }
                finally
                {
                    bitmap.Unlock();
                }
            }

            Assert.Equal(60, reassembler.FramesCompleted);
            Assert.Equal(0, reassembler.FramesDropped);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            long memEnd = GC.GetTotalMemory(true);

            // Memory increase must be bounded (< 8 MB across 60 frames)
            long delta = Math.Max(0, memEnd - memStart);
            Assert.True(delta < 8 * 1024 * 1024, $"Memory delta {delta} exceeds 8MB limit");
        }

        // =========================================================================
        // SCENARIO 7: Malformed RTP Noise Flood & Recovery
        // =========================================================================
        [Fact]
        public void Scenario07_MalformedRtpNoiseFlood_DropsNoise_CleanlyRendersSubsequentValidFrame()
        {
            using var receiver = new RtpMulticastReceiver();
            var reassembler = new ScanlineFrameReassembler();

            // 1. Send noise packets
            byte[] truncated = new byte[10];
            byte[] badVersion = MockRtpStreamer.CreateCorruptVersionPacket(version: 0);
            byte[] randomNoise = new byte[500];
            new Random(42).NextBytes(randomNoise);

            Assert.False(RtpPacketParser.TryParse(truncated, out _));
            Assert.False(RtpPacketParser.TryParse(badVersion, out _));
            Assert.False(RtpPacketParser.TryParse(randomNoise, out _));

            // 2. Immediately send a valid frame
            ushort seq = 1;
            var validFrame = MockRtpStreamer.GenerateFramePackets(320, 10, 12345, 9999, ref seq);
            AssembledFrame? frame = null;

            foreach (var p in validFrame)
            {
                if (RtpPacketParser.TryParse(p, out var h))
                {
                    frame = reassembler.ProcessPacket(h);
                }
            }

            Assert.NotNull(frame);
            Assert.Equal(320, frame.Width);
            Assert.Equal(10, frame.Height);
        }

        // =========================================================================
        // SCENARIO 8: Rapid User Toggle Spamming & Stress Resilience
        // =========================================================================
        [Fact]
        public async Task Scenario08_RapidUserToggleSpamming_StressResilience()
        {
            using var server = new MockSdvoeServer();
            using var client = new SdvoeClient("127.0.0.1", server.TelnetPort, server.RestPort);
            var ipManager = new MulticastIpManager();
            using var coordinator = new SidebarCoordinator(client, ipManager);

            // Rapid toggle 10 times
            for (int i = 0; i < 10; i++)
            {
                await coordinator.ToggleAsync();
            }

            // After an even number of toggles, sidebar must be collapsed with 0 leaked resources
            Assert.False(coordinator.IsExpanded);
            Assert.Equal(0.0, coordinator.SidebarWidth);
            Assert.Empty(coordinator.ActiveCards);
            Assert.Equal(0, ipManager.ActiveAllocationCount);
        }
    }
}
