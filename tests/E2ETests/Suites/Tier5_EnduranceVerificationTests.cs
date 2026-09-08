using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using E2ETests.Harness;
using E2ETests.Mocks;
using Xunit;
using Xunit.Abstractions;

namespace E2ETests.Suites
{
    public class Tier5_EnduranceVerificationTests
    {
        private readonly ITestOutputHelper _output;

        public Tier5_EnduranceVerificationTests(ITestOutputHelper output)
        {
            _output = output;
        }

        // =========================================================================
        // ENDURANCE TEST 1: 100-Iteration Loop of Tier 1 Feature Operations
        // =========================================================================
        [Fact]
        public void Endurance_Tier1_100Iterations_FeatureCoverage_GcAndMemoryStability()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long initialMemory = GC.GetTotalMemory(true);
            long initialAllocated = GC.GetTotalAllocatedBytes();
            int initialGen0 = GC.CollectionCount(0);
            int initialGen1 = GC.CollectionCount(1);
            int initialGen2 = GC.CollectionCount(2);

            var process = Process.GetCurrentProcess();
            long initialWs = process.WorkingSet64;

            ushort seq = 1;
            var reassembler = new ScanlineFrameReassembler();
            var writeableBmp = new WriteableBitmap(320, 180, 96, 96, PixelFormats.Bgr24, null);

            var iterationSnapshots = new List<(int Iteration, long Memory, long WorkingSet, int Gen0, int Gen1, int Gen2)>();

            for (int i = 1; i <= 100; i++)
            {
                // 1. Config Service Lifecycle
                string cfgPath = Path.Combine(Path.GetTempPath(), $"avas_endurance_cfg_{Guid.NewGuid():N}.json");
                var configService = new ConfigServiceHelper(cfgPath);
                var config = new AppConfig
                {
                    ControlServerIp = "127.0.0.1",
                    TelnetPort = 6970,
                    RestPort = 8080,
                    MulticastStartIp = "224.1.1.1",
                    MulticastEndIp = "224.1.1.10",
                    BasePort = 6792
                };
                configService.Save(config);
                var loaded = configService.Current;
                Assert.Equal("127.0.0.1", loaded.ControlServerIp);
                if (File.Exists(cfgPath)) File.Delete(cfgPath);

                // 2. Multicast IP Allocation & Deallocation
                var ipManager = new MulticastIpManager("224.1.1.1", "224.1.1.10", 6792);
                var allocatedIps = new List<string>();
                for (int m = 0; m < 5; m++)
                {
                    string mac = $"f8228500000{m:X}";
                    string? ip = ipManager.AllocateMulticastIp(mac);
                    Assert.NotNull(ip);
                    allocatedIps.Add(mac);
                }
                foreach (var mac in allocatedIps)
                {
                    ipManager.ReleaseMulticastIp(mac);
                }
                Assert.Equal(0, ipManager.ActiveAllocationCount);

                // 3. RTP Generation, Parsing & Reassembly
                var packets = MockRtpStreamer.GenerateFramePackets(320, 180, 12345, (uint)(i * 1000), ref seq, TestPattern.ColorBars);
                AssembledFrame? frame = null;
                foreach (var pkt in packets)
                {
                    if (RtpPacketParser.TryParse(pkt, out var parsed))
                    {
                        var res = reassembler.ProcessPacket(parsed);
                        if (res != null) frame = res;
                    }
                }
                Assert.NotNull(frame);

                // 4. YUV422 to BGR24 Rasterization & WriteableBitmap Lock
                byte[] bgr = Yuv422Rasterizer.ConvertYuv422ToBgr24(frame.YuvData, 320, 180);
                writeableBmp.Lock();
                try
                {
                    System.Runtime.InteropServices.Marshal.Copy(bgr, 0, writeableBmp.BackBuffer, bgr.Length);
                    writeableBmp.AddDirtyRect(new System.Windows.Int32Rect(0, 0, 320, 180));
                }
                finally
                {
                    writeableBmp.Unlock();
                }

                if (i == 1 || i % 20 == 0)
                {
                    process.Refresh();
                    iterationSnapshots.Add((
                        i,
                        GC.GetTotalMemory(false),
                        process.WorkingSet64,
                        GC.CollectionCount(0) - initialGen0,
                        GC.CollectionCount(1) - initialGen1,
                        GC.CollectionCount(2) - initialGen2
                    ));
                }
            }

            Assert.Equal(100, reassembler.FramesCompleted);
            Assert.Equal(0, reassembler.FramesDropped);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long finalMemory = GC.GetTotalMemory(true);
            long totalAllocated = GC.GetTotalAllocatedBytes() - initialAllocated;
            process.Refresh();
            long finalWs = process.WorkingSet64;

            long memoryDelta = finalMemory - initialMemory;
            _output.WriteLine($"=== Tier 1 Endurance (100 Iterations) Results ===");
            _output.WriteLine($"Initial Heap: {initialMemory / 1024.0 / 1024.0:F2} MB | Final Heap: {finalMemory / 1024.0 / 1024.0:F2} MB | Heap Delta: {memoryDelta / 1024.0 / 1024.0:F2} MB");
            _output.WriteLine($"Total Bytes Allocated: {totalAllocated / 1024.0 / 1024.0:F2} MB");
            _output.WriteLine($"Initial WorkingSet: {initialWs / 1024.0 / 1024.0:F2} MB | Final WorkingSet: {finalWs / 1024.0 / 1024.0:F2} MB");
            _output.WriteLine($"GC Collections: Gen0: {GC.CollectionCount(0) - initialGen0}, Gen1: {GC.CollectionCount(1) - initialGen1}, Gen2: {GC.CollectionCount(2) - initialGen2}");

            foreach (var s in iterationSnapshots)
            {
                _output.WriteLine($"  Iteration {s.Iteration,3}: Heap={s.Memory / 1024.0 / 1024.0:F2} MB, WS={s.WorkingSet / 1024.0 / 1024.0:F2} MB, Gen0={s.Gen0}, Gen1={s.Gen1}, Gen2={s.Gen2}");
            }

            // Verify heap delta is strictly bounded (< 5 MB across 100 complete iterations)
            Assert.True(Math.Abs(memoryDelta) < 5 * 1024 * 1024, $"Heap memory grew unboundedly: {memoryDelta} bytes");
        }

        // =========================================================================
        // ENDURANCE TEST 2: 50-Iteration Loop of Tier 2 Fault Injection & Recovery
        // =========================================================================
        [Fact]
        public void Endurance_Tier2_50Iterations_FaultInjection_MemoryStability()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long initialMemory = GC.GetTotalMemory(true);
            int initialGen0 = GC.CollectionCount(0);
            int initialGen1 = GC.CollectionCount(1);
            int initialGen2 = GC.CollectionCount(2);

            var process = Process.GetCurrentProcess();
            long initialWs = process.WorkingSet64;

            var reassembler = new ScanlineFrameReassembler();
            ushort seq = 100;
            var rand = new Random(12345);

            for (int i = 1; i <= 50; i++)
            {
                // 1. Corrupt RTP packet injection
                byte[] corruptHeader = MockRtpStreamer.CreateCorruptVersionPacket(version: 1);
                Assert.False(RtpPacketParser.TryParse(corruptHeader, out _));

                byte[] randomNoise = new byte[1200];
                rand.NextBytes(randomNoise);
                Assert.False(RtpPacketParser.TryParse(randomNoise, out _));

                // 2. Missing scanlines fault injection & frame drop
                var framePackets = MockRtpStreamer.GenerateFramePackets(320, 180, 54321, (uint)(i * 2000), ref seq);
                // Drop packet at index 2 (simulating network loss)
                var droppedPackets = framePackets.Where((p, idx) => idx != 2).ToList();
                foreach (var p in droppedPackets)
                {
                    if (RtpPacketParser.TryParse(p, out var h))
                    {
                        reassembler.ProcessPacket(h);
                    }
                }

                // 3. Multicast pool exhaustion and boundary recovery
                var ipMgr = new MulticastIpManager("224.1.1.1", "224.1.1.3");
                string? ip1 = ipMgr.AllocateMulticastIp("mac1");
                string? ip2 = ipMgr.AllocateMulticastIp("mac2");
                string? ip3 = ipMgr.AllocateMulticastIp("mac3");
                string? ip4 = ipMgr.AllocateMulticastIp("mac4"); // Exhaustion!
                Assert.NotNull(ip1);
                Assert.NotNull(ip2);
                Assert.NotNull(ip3);
                Assert.Null(ip4);

                // Recover by releasing mac2
                ipMgr.ReleaseMulticastIp("mac2");
                string? ipRecovered = ipMgr.AllocateMulticastIp("mac5");
                Assert.NotNull(ipRecovered);
                Assert.Equal("224.1.1.2", ipRecovered);
                ipMgr.ReleaseMulticastIp("mac1");
                ipMgr.ReleaseMulticastIp("mac3");
                ipMgr.ReleaseMulticastIp("mac5");
                Assert.Equal(0, ipMgr.ActiveAllocationCount);

                // 4. Send valid frame after fault injection to confirm clean recovery
                var recoveryPackets = MockRtpStreamer.GenerateFramePackets(320, 180, 54321, (uint)(i * 2000 + 100), ref seq);
                AssembledFrame? recoveredFrame = null;
                foreach (var p in recoveryPackets)
                {
                    if (RtpPacketParser.TryParse(p, out var h))
                    {
                        var res = reassembler.ProcessPacket(h);
                        if (res != null) recoveredFrame = res;
                    }
                }
                Assert.NotNull(recoveredFrame);
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long finalMemory = GC.GetTotalMemory(true);
            process.Refresh();
            long finalWs = process.WorkingSet64;
            long memoryDelta = finalMemory - initialMemory;

            _output.WriteLine($"=== Tier 2 Fault Injection Endurance (50 Iterations) ===");
            _output.WriteLine($"Initial Heap: {initialMemory / 1024.0 / 1024.0:F2} MB | Final Heap: {finalMemory / 1024.0 / 1024.0:F2} MB | Delta: {memoryDelta / 1024.0 / 1024.0:F2} MB");
            _output.WriteLine($"Initial WS: {initialWs / 1024.0 / 1024.0:F2} MB | Final WS: {finalWs / 1024.0 / 1024.0:F2} MB");
            _output.WriteLine($"GC Collections: Gen0: {GC.CollectionCount(0) - initialGen0}, Gen1: {GC.CollectionCount(1) - initialGen1}, Gen2: {GC.CollectionCount(2) - initialGen2}");
            _output.WriteLine($"Total Dropped Frames Handled: {reassembler.FramesDropped} | Completed: {reassembler.FramesCompleted}");

            Assert.Equal(50, reassembler.FramesDropped);
            Assert.Equal(50, reassembler.FramesCompleted);
            Assert.True(Math.Abs(memoryDelta) < 5 * 1024 * 1024, $"Memory leak in fault recovery: {memoryDelta} bytes");
        }

        // =========================================================================
        // ENDURANCE TEST 3: 20-Iteration Loop of Tier 3 & Tier 4 E2E Workflows
        // =========================================================================
        [Fact]
        public async Task Endurance_Tier3_Tier4_20Iterations_MultiDevice_RapidExpandCollapse_SocketReuse()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long initialMemory = GC.GetTotalMemory(true);
            int initialGen0 = GC.CollectionCount(0);
            int initialGen1 = GC.CollectionCount(1);
            int initialGen2 = GC.CollectionCount(2);

            var process = Process.GetCurrentProcess();
            long initialWs = process.WorkingSet64;

            ushort seq = 1;

            for (int iteration = 1; iteration <= 20; iteration++)
            {
                // 1. Spin up isolated MockSdvoeServer
                using var server = new MockSdvoeServer();
                Assert.True(server.IsRunning);

                // 2. Initialize Client and Multicast Allocator
                using var client = new SdvoeClient("127.0.0.1", server.TelnetPort, server.RestPort);
                var ipManager = new MulticastIpManager("224.1.1.1", "224.1.1.10", 6792);

                // 3. Initialize SidebarCoordinator
                using var coordinator = new SidebarCoordinator(client, ipManager);
                Assert.False(coordinator.IsExpanded);

                // 4. Expand: Triggers discovery, multicast allocation, server stream start, UDP receiver bind
                bool expanded = await coordinator.ExpandAsync();
                Assert.True(expanded);
                Assert.True(coordinator.IsExpanded);
                Assert.Equal(2, coordinator.ActiveCards.Count);
                Assert.Equal(2, ipManager.ActiveAllocationCount);

                // 5. Ingest simulated RTP multicast traffic for active cards
                foreach (var card in coordinator.ActiveCards)
                {
                    var packets = MockRtpStreamer.GenerateFramePackets(320, 180, 12345, (uint)(iteration * 5000), ref seq);
                    foreach (var pkt in packets)
                    {
                        card.Receiver?.ProcessDatagram(pkt);
                    }
                    Assert.True(card.FrameUpdateCount > 0);
                    Assert.NotNull(card.LastRenderedRgbFrame);
                }

                // 6. Rapid Toggle Cycling (Expand -> Collapse -> Expand -> Collapse)
                await coordinator.CollapseAsync();
                Assert.False(coordinator.IsExpanded);
                Assert.Equal(0, ipManager.ActiveAllocationCount);
                Assert.Empty(coordinator.ActiveCards);

                await coordinator.ExpandAsync();
                Assert.True(coordinator.IsExpanded);
                Assert.Equal(2, coordinator.ActiveCards.Count);

                // Final collapse for this iteration
                await coordinator.CollapseAsync();
                Assert.False(coordinator.IsExpanded);
                Assert.Equal(0, ipManager.ActiveAllocationCount);
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long finalMemory = GC.GetTotalMemory(true);
            process.Refresh();
            long finalWs = process.WorkingSet64;
            long memoryDelta = finalMemory - initialMemory;

            _output.WriteLine($"=== Tier 3/4 E2E Workflows Endurance (20 Iterations) ===");
            _output.WriteLine($"Initial Heap: {initialMemory / 1024.0 / 1024.0:F2} MB | Final Heap: {finalMemory / 1024.0 / 1024.0:F2} MB | Delta: {memoryDelta / 1024.0 / 1024.0:F2} MB");
            _output.WriteLine($"Initial WS: {initialWs / 1024.0 / 1024.0:F2} MB | Final WS: {finalWs / 1024.0 / 1024.0:F2} MB");
            _output.WriteLine($"GC Collections: Gen0: {GC.CollectionCount(0) - initialGen0}, Gen1: {GC.CollectionCount(1) - initialGen1}, Gen2: {GC.CollectionCount(2) - initialGen2}");

            // Verify heap delta is tightly bounded (< 6 MB across 20 full server/client/receiver lifecycles)
            Assert.True(Math.Abs(memoryDelta) < 6 * 1024 * 1024, $"E2E socket/heap leak detected: {memoryDelta} bytes");
        }
    }
}
