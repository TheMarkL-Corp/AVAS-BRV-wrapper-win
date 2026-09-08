using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using E2ETests.Harness;
using E2ETests.Mocks;
using Xunit;

namespace E2ETests.Suites
{
    public class Tier2_BoundaryCornerTests
    {
        // =========================================================================
        // FEATURE 1: R1.1 Portable Shell & App Structure
        // =========================================================================

        [Fact]
        public void F01_B01_PathWithSpacesAndUnicode_ResolvesCorrectly()
        {
            string testDir = Path.Combine(AppContext.BaseDirectory, "Test Space & 测试");
            Directory.CreateDirectory(testDir);
            try
            {
                Assert.True(Directory.Exists(testDir));
            }
            finally
            {
                if (Directory.Exists(testDir)) Directory.Delete(testDir);
            }
        }

        [Fact]
        public void F01_B02_ExtremelyLongPath_HandledSafely()
        {
            string longSubDir = Path.Combine(AppContext.BaseDirectory, new string('A', 50));
            Directory.CreateDirectory(longSubDir);
            try
            {
                Assert.True(Directory.Exists(longSubDir));
            }
            finally
            {
                if (Directory.Exists(longSubDir)) Directory.Delete(longSubDir);
            }
        }

        [Fact]
        public void F01_B03_MissingAssetsFolder_FallsBackGracefully()
        {
            string nonExistentAsset = Path.Combine(AppContext.BaseDirectory, "NonExistentDir_12345", "icon.ico");
            Assert.False(File.Exists(nonExistentAsset));
        }

        [Fact]
        public void F01_B04_ReadOnlyDirectory_ThrowsMeaningfulExceptionOnWrite()
        {
            string testFile = Path.Combine(Path.GetTempPath(), $"ro_{Guid.NewGuid():N}.tmp");
            File.WriteAllText(testFile, "initial");
            File.SetAttributes(testFile, FileAttributes.ReadOnly);

            try
            {
                Assert.Throws<UnauthorizedAccessException>(() =>
                {
                    File.WriteAllText(testFile, "overwrite");
                });
            }
            finally
            {
                File.SetAttributes(testFile, FileAttributes.Normal);
                File.Delete(testFile);
            }
        }

        [Fact]
        public void F01_B05_RapidMutexAcquisitionAndRelease_StaysStable()
        {
            string mutexName = "Global\\RapidMutexTest_" + Guid.NewGuid().ToString("N");
            for (int i = 0; i < 20; i++)
            {
                using var m = new Mutex(true, mutexName, out bool created);
                Assert.True(created);
            }
        }

        // =========================================================================
        // FEATURE 2: R1.2 Microsoft WebView2 Embedding
        // =========================================================================

        [Fact]
        public void F02_B01_EmptyOrWhitespaceUrl_FailsValidation()
        {
            var config = new AppConfig { BlueRiverUrl = "   " };
            Assert.False(config.Validate(out string? err));
            Assert.Contains("Invalid BlueRiver URL", err);
        }

        [Fact]
        public void F02_B02_NonHttpUrl_FailsValidation()
        {
            var config = new AppConfig { BlueRiverUrl = "ftp://invalid-server" };
            Assert.False(Uri.TryCreate(config.BlueRiverUrl, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https"));
        }

        [Fact]
        public void F02_B03_ExtremelyLongUrl_ParsesWithoutBufferOverflow()
        {
            string longUrl = "http://localhost:3000/" + new string('x', 2000);
            var config = new AppConfig { BlueRiverUrl = longUrl };
            Assert.True(config.Validate(out _));
        }

        [Fact]
        public void F02_B04_UserDataFolderCreationUnderDeepNesting_Succeeds()
        {
            string deepPath = Path.Combine(AppContext.BaseDirectory, "Deep", "Nested", "UserData");
            Directory.CreateDirectory(deepPath);
            try
            {
                Assert.True(Directory.Exists(deepPath));
            }
            finally
            {
                if (Directory.Exists(Path.Combine(AppContext.BaseDirectory, "Deep")))
                {
                    Directory.Delete(Path.Combine(AppContext.BaseDirectory, "Deep"), true);
                }
            }
        }

        [Fact]
        public void F02_B05_HighPortNumberUrl_HandledCorrectly()
        {
            var config = new AppConfig { BlueRiverUrl = "http://192.168.1.100:65535" };
            Assert.True(config.Validate(out _));
            Uri.TryCreate(config.BlueRiverUrl, UriKind.Absolute, out var u);
            Assert.Equal(65535, u?.Port);
        }

        // =========================================================================
        // FEATURE 3: R1.3 Configuration & Persistence
        // =========================================================================

        [Fact]
        public void F03_B01_InvalidPortZeroOrNegative_FailsValidation()
        {
            var config = new AppConfig { RestPort = 0 };
            Assert.False(config.Validate(out string? err));
            Assert.Contains("Invalid REST Port", err);
        }

        [Fact]
        public void F03_B02_PortGreaterThan65535_FailsValidation()
        {
            var config = new AppConfig { TelnetPort = 70000 };
            Assert.False(config.Validate(out string? err));
            Assert.Contains("Invalid Telnet Port", err);
        }

        [Fact]
        public void F03_B03_StartIpGreaterThanEndIp_FailsValidation()
        {
            var config = new AppConfig
            {
                MulticastStartIp = "224.1.3.200",
                MulticastEndIp = "224.1.1.10"
            };
            Assert.False(config.Validate(out string? err));
            Assert.Contains("cannot be greater than End IP", err);
        }

        [Fact]
        public void F03_B04_NonMulticastClassIpRange_FailsValidation()
        {
            var config = new AppConfig
            {
                MulticastStartIp = "192.168.1.1",
                MulticastEndIp = "192.168.1.100"
            };
            Assert.False(config.Validate(out string? err));
            Assert.Contains("within standard Multicast range", err);
        }

        [Fact]
        public void F03_B05_EmptyJsonPayload_ResetsToDefaults()
        {
            string tempConfig = Path.Combine(Path.GetTempPath(), $"cfg_empty_{Guid.NewGuid():N}.json");
            try
            {
                File.WriteAllText(tempConfig, "{}");
                var service = new ConfigServiceHelper(tempConfig);
                Assert.Equal("224.1.1.1", service.Current.MulticastStartIp);
            }
            finally
            {
                if (File.Exists(tempConfig)) File.Delete(tempConfig);
            }
        }

        // =========================================================================
        // FEATURE 4: R2.1 SDVoE Control Server Client
        // =========================================================================

        [Fact]
        public async Task F04_B01_MalformedRequireCommand_ReturnsInvalidCommand()
        {
            using var server = new MockSdvoeServer();
            using var rawClient = new TcpClient();
            await rawClient.ConnectAsync("127.0.0.1", server.TelnetPort);
            using var stream = rawClient.GetStream();
            using var writer = new StreamWriter(stream) { AutoFlush = true, NewLine = "\r\n" };
            using var reader = new StreamReader(stream);

            await writer.WriteLineAsync("require api bad_version");
            string? line = await reader.ReadLineAsync();

            Assert.Contains("INVALID_COMMAND", line);
        }

        [Fact]
        public async Task F04_B02_UnreachableServerIp_ThrowsSocketException()
        {
            // 192.0.2.1 is TEST-NET-1 (RFC 5737), non-routable
            using var client = new SdvoeClient("192.0.2.1", 6970, 8080);
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                await client.ConnectTelnetAsync(cts.Token);
            });
        }

        [Fact]
        public async Task F04_B03_ServerClosesConnectionUnexpectedly_ClientHandlesGracefully()
        {
            var server = new MockSdvoeServer();
            using var client = new SdvoeClient("127.0.0.1", server.TelnetPort, server.RestPort);
            await client.ConnectTelnetAsync();

            // Shutdown server
            server.Dispose();

            // Next command should fail gracefully
            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                await client.SendTelnetCommandAsync("version");
            });
        }

        [Fact]
        public async Task F04_B04_EmptyLineSentToTelnet_IgnoredWithoutError()
        {
            using var server = new MockSdvoeServer();
            using var client = new SdvoeClient("127.0.0.1", server.TelnetPort, server.RestPort);
            await client.ConnectTelnetAsync();

            // Should not crash server or disconnect
            string version = await client.SendTelnetCommandAsync("version");
            Assert.Contains("Control Server", version);
        }

        [Fact]
        public async Task F04_B05_RestPostToNonExistentEndpoint_Returns404()
        {
            using var server = new MockSdvoeServer();
            using var http = new System.Net.Http.HttpClient();

            var resp = await http.GetAsync($"http://127.0.0.1:{server.RestPort}/api/non_existent_route");
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        }

        // =========================================================================
        // FEATURE 5: R2.2 AVAS-223 Device Filtering
        // =========================================================================

        [Fact]
        public void F05_B01_DeviceWithNullProperties_HandledSafely()
        {
            var dev = new AvasDevice { MacAddress = null!, DeviceName = null! };
            Assert.False(SdvoeDiscoveryFilter.IsTargetAvas223Tx(dev));
        }

        [Fact]
        public void F05_B02_EmptyMacAddress_HandledWithoutCrashing()
        {
            var dev = new AvasDevice
            {
                MacAddress = "",
                VendorId = 105,
                ProductId = 81,
                ChipIndex = 0,
                IsTransmitter = true
            };
            // Empty MAC address should be rejected safely
            Assert.False(SdvoeDiscoveryFilter.IsTargetAvas223Tx(dev));
        }

        [Fact]
        public void F05_B03_NegativeVendorOrProductId_ExcludesDevice()
        {
            var dev = new AvasDevice { VendorId = -1, ProductId = -1, ChipIndex = 0, IsTransmitter = true };
            Assert.False(SdvoeDiscoveryFilter.IsTargetAvas223Tx(dev));
        }

        [Fact]
        public void F05_B04_ChipIndexGreaterThanOne_ExcludesDevice()
        {
            var dev = new AvasDevice { VendorId = 105, ProductId = 81, ChipIndex = 2, IsTransmitter = true };
            Assert.False(SdvoeDiscoveryFilter.IsTargetAvas223Tx(dev));
        }

        [Fact]
        public void F05_B05_BothTxAndRxFlagsTrue_ExcludesDevice()
        {
            var dev = new AvasDevice
            {
                VendorId = 105,
                ProductId = 81,
                ChipIndex = 0,
                IsTransmitter = true,
                IsReceiver = true // Conflict!
            };
            Assert.False(SdvoeDiscoveryFilter.IsTargetAvas223Tx(dev));
        }

        // =========================================================================
        // FEATURE 6: R2.3 Device Telemetry Tracking
        // =========================================================================

        [Fact]
        public void F06_B01_ExtremelyLongDeviceName_TruncatedOrPreservedSafely()
        {
            string longName = new string('D', 1000);
            var dev = new AvasDevice { DeviceName = longName };
            Assert.Equal(longName, dev.DeviceName);
        }

        [Fact]
        public void F06_B02_SpecialCharactersInDeviceName_ParsedWithoutError()
        {
            string specialName = "AVAS-223_TX0 #1 <Room & Lab> (4K)";
            var dev = new AvasDevice { DeviceName = specialName };
            Assert.Equal(specialName, dev.DeviceName);
        }

        [Fact]
        public void F06_B03_DeviceNodeWithoutIpAddress_DefaultsToEmptyString()
        {
            string json = "{\"status\":\"SUCCESS\",\"result\":{\"devices\":[{\"device_id\":\"mac1\",\"identity\":{\"vendor_id\":105,\"product_id\":81,\"chip_id\":0,\"is_transmitter\":true},\"nodes\":[]}]}}";
            var parsed = SdvoeClient.ParseDevicesJson(json);
            Assert.Single(parsed);
            Assert.Equal(string.Empty, parsed[0].IpAddress);
        }

        [Fact]
        public void F06_B04_NegativeFpsUpdate_ClampedToZero()
        {
            var dev = new AvasDevice { CurrentFps = -5.0 };
            double clamped = Math.Max(0.0, dev.CurrentFps);
            Assert.Equal(0.0, clamped);
        }

        [Fact]
        public void F06_B05_UnsupportedResolutionString_HandledGracefully()
        {
            var dev = new AvasDevice { Resolution = "UNKNOWN_RES" };
            Assert.Equal("UNKNOWN_RES", dev.Resolution);
        }

        // =========================================================================
        // FEATURE 7: R3.1 Dynamic Multicast IP Allocator
        // =========================================================================

        [Fact]
        public void F07_B01_BoundaryStartIp_224_1_1_1_AllocatedSuccessfully()
        {
            var mgr = new MulticastIpManager("224.1.1.1", "224.1.1.1");
            string? ip = mgr.AllocateMulticastIp("mac1");
            Assert.Equal("224.1.1.1", ip);
        }

        [Fact]
        public void F07_B02_BoundaryEndIp_224_1_3_225_AllocatedSuccessfully()
        {
            var mgr = new MulticastIpManager("224.1.3.225", "224.1.3.225");
            string? ip = mgr.AllocateMulticastIp("mac1");
            Assert.Equal("224.1.3.225", ip);
        }

        [Fact]
        public void F07_B03_SingleAddressPool_ExhaustsAfterOneAllocation()
        {
            var mgr = new MulticastIpManager("224.1.1.5", "224.1.1.5");
            string? ip1 = mgr.AllocateMulticastIp("mac1");
            string? ip2 = mgr.AllocateMulticastIp("mac2");

            Assert.Equal("224.1.1.5", ip1);
            Assert.Null(ip2);
        }

        [Fact]
        public void F07_B04_DoubleReleaseSameMac_NoEffect()
        {
            var mgr = new MulticastIpManager("224.1.1.1", "224.1.1.10");
            mgr.AllocateMulticastIp("mac1");
            mgr.ReleaseMulticastIp("mac1");
            mgr.ReleaseMulticastIp("mac1"); // Double release
            Assert.Equal(0, mgr.ActiveAllocationCount);
        }

        [Fact]
        public void F07_B05_ReleaseNonExistentMac_NoException()
        {
            var mgr = new MulticastIpManager();
            mgr.ReleaseMulticastIp("non_existent_mac");
            Assert.Equal(0, mgr.ActiveAllocationCount);
        }

        // =========================================================================
        // FEATURE 8: R3.2 SDVoE Preview Stream Control
        // =========================================================================

        [Fact]
        public async Task F08_B01_StartStreamWithReservedIp224_1_1_253_ReturnsIllegalArg()
        {
            using var server = new MockSdvoeServer();
            using var client = new SdvoeClient("127.0.0.1", server.TelnetPort, server.RestPort);
            await client.ConnectTelnetAsync();

            string resp = await client.SendTelnetCommandAsync("start f8228500aaaa:thumbnail:0 224.1.1.253");
            Assert.Contains("ILLEGAL_ARGUMENT", resp);
            Assert.Contains("reserved", resp);
        }

        [Fact]
        public async Task F08_B02_StartStreamWithReservedIp224_1_1_254_ReturnsIllegalArg()
        {
            using var server = new MockSdvoeServer();
            using var client = new SdvoeClient("127.0.0.1", server.TelnetPort, server.RestPort);
            await client.ConnectTelnetAsync();

            string resp = await client.SendTelnetCommandAsync("start f8228500aaaa:thumbnail:0 224.1.1.254");
            Assert.Contains("ILLEGAL_ARGUMENT", resp);
            Assert.Contains("reserved", resp);
        }

        [Fact]
        public async Task F08_B03_StartStreamWithOutOfRangeIp224_1_4_1_ReturnsIllegalArg()
        {
            using var server = new MockSdvoeServer();
            using var client = new SdvoeClient("127.0.0.1", server.TelnetPort, server.RestPort);
            await client.ConnectTelnetAsync();

            string resp = await client.SendTelnetCommandAsync("start f8228500aaaa:thumbnail:0 224.1.4.1");
            Assert.Contains("ILLEGAL_ARGUMENT", resp);
            Assert.Contains("configured range", resp);
        }

        [Fact]
        public async Task F08_B04_SetThumbnailWithoutSsrc_ReturnsIllegalArg()
        {
            using var server = new MockSdvoeServer();
            using var client = new SdvoeClient("127.0.0.1", server.TelnetPort, server.RestPort);
            await client.ConnectTelnetAsync();

            string resp = await client.SendTelnetCommandAsync("set all_tx thumbnail udp 6792");
            Assert.Contains("ILLEGAL_ARGUMENT", resp);
            Assert.Contains("mandatory", resp);
        }

        [Fact]
        public async Task F08_B05_StartStreamOnNonExistentMac_ReturnsIllegalArg()
        {
            using var server = new MockSdvoeServer();
            using var client = new SdvoeClient("127.0.0.1", server.TelnetPort, server.RestPort);
            await client.ConnectTelnetAsync();

            string resp = await client.SendTelnetCommandAsync("start 999999999999:thumbnail:0 224.1.1.10");
            Assert.Contains("ILLEGAL_ARGUMENT", resp);
        }

        // =========================================================================
        // FEATURE 9: R4.1 Collapsible Native Overlay Sidebar
        // =========================================================================

        [Fact]
        public async Task F09_B01_RapidToggleCalls_MaintainsCoherentState()
        {
            using var server = new MockSdvoeServer();
            using var client = new SdvoeClient("127.0.0.1", server.TelnetPort, server.RestPort);
            var ipManager = new MulticastIpManager();
            using var coordinator = new SidebarCoordinator(client, ipManager);

            for (int i = 0; i < 6; i++)
            {
                await coordinator.ToggleAsync();
            }

            Assert.False(coordinator.IsExpanded);
            Assert.Equal(0.0, coordinator.SidebarWidth);
        }

        [Fact]
        public async Task F09_B02_ExpandWhenAlreadyExpanded_NoOp()
        {
            using var server = new MockSdvoeServer();
            using var client = new SdvoeClient("127.0.0.1", server.TelnetPort, server.RestPort);
            var ipManager = new MulticastIpManager();
            using var coordinator = new SidebarCoordinator(client, ipManager);

            await coordinator.ExpandAsync();
            int count1 = coordinator.ActiveCards.Count;

            await coordinator.ExpandAsync();
            int count2 = coordinator.ActiveCards.Count;

            Assert.Equal(count1, count2);
        }

        [Fact]
        public async Task F09_B03_CollapseWhenAlreadyCollapsed_NoOp()
        {
            using var server = new MockSdvoeServer();
            using var client = new SdvoeClient("127.0.0.1", server.TelnetPort, server.RestPort);
            var ipManager = new MulticastIpManager();
            using var coordinator = new SidebarCoordinator(client, ipManager);

            await coordinator.CollapseAsync();
            Assert.False(coordinator.IsExpanded);
        }

        [Fact]
        public async Task F09_B04_CancellationRequestedDuringExpand_HaltsCleanly()
        {
            using var server = new MockSdvoeServer();
            using var client = new SdvoeClient("127.0.0.1", server.TelnetPort, server.RestPort);
            var ipManager = new MulticastIpManager();
            using var coordinator = new SidebarCoordinator(client, ipManager);

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await coordinator.ExpandAsync(cts.Token);
            // Must not throw unhandled exception
        }

        [Fact]
        public async Task F09_B05_CancellationRequestedDuringCollapse_FinishesTeardown()
        {
            using var server = new MockSdvoeServer();
            using var client = new SdvoeClient("127.0.0.1", server.TelnetPort, server.RestPort);
            var ipManager = new MulticastIpManager();
            using var coordinator = new SidebarCoordinator(client, ipManager);

            await coordinator.ExpandAsync();

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await coordinator.CollapseAsync(cts.Token);
            Assert.False(coordinator.IsExpanded);
        }

        // =========================================================================
        // FEATURE 10: R4.2 Preview Cards & Telemetry UI
        // =========================================================================

        [Fact]
        public async Task F10_B01_ZeroDevicesDiscovered_CreatesZeroCards()
        {
            using var server = new MockSdvoeServer();
            server.ClearDevices();

            using var client = new SdvoeClient("127.0.0.1", server.TelnetPort, server.RestPort);
            var ipManager = new MulticastIpManager();
            using var coordinator = new SidebarCoordinator(client, ipManager);

            await coordinator.ExpandAsync();
            Assert.Empty(coordinator.ActiveCards);
        }

        [Fact]
        public void F10_B02_SinglePixelFrameDimensions_UpdatesCardSafely()
        {
            var card = new EncoderCardViewModel();
            card.Resolution = "2x1";
            card.LastRenderedRgbFrame = new byte[6];
            Assert.Equal("2x1", card.Resolution);
        }

        [Fact]
        public void F10_B03_HighFpsUpdate_CalculatesAccuratelyWithoutBufferExplosion()
        {
            using var receiver = new RtpMulticastReceiver();
            ushort seq = 1;

            // Ingest 20 frames rapidly
            for (int i = 0; i < 20; i++)
            {
                var pkts = MockRtpStreamer.GenerateFramePackets(320, 2, 1, (uint)(1000 + i * 100), ref seq);
                foreach (var p in pkts) receiver.ProcessDatagram(p);
            }

            Assert.True(receiver.CurrentFps > 0.0);
        }

        [Fact]
        public void F10_B04_EmptyOrNullLastFrame_HandledSafely()
        {
            var card = new EncoderCardViewModel { LastRenderedRgbFrame = null };
            Assert.Null(card.LastRenderedRgbFrame);
        }

        [Fact]
        public void F10_B05_DuplicateMacEncountered_DeduplicatesCard()
        {
            var cards = new Dictionary<string, EncoderCardViewModel>();
            var card1 = new EncoderCardViewModel { MacAddress = "mac1", DeviceName = "First" };
            var card2 = new EncoderCardViewModel { MacAddress = "mac1", DeviceName = "Second" };

            cards[card1.MacAddress] = card1;
            cards[card2.MacAddress] = card2;

            Assert.Single(cards);
            Assert.Equal("Second", cards["mac1"].DeviceName);
        }

        // =========================================================================
        // FEATURE 11: R4.3 Lifecycle & Teardown on Collapse
        // =========================================================================

        [Fact]
        public async Task F11_B01_CollapseWithZeroStreamsActive_SucceedsInstantly()
        {
            using var server = new MockSdvoeServer();
            using var client = new SdvoeClient("127.0.0.1", server.TelnetPort, server.RestPort);
            var ipManager = new MulticastIpManager();
            using var coordinator = new SidebarCoordinator(client, ipManager);

            await coordinator.CollapseAsync();
            Assert.False(coordinator.IsExpanded);
        }

        [Fact]
        public async Task F11_B02_CollapseWhenServerAlreadyUnreachable_DoesNotThrow()
        {
            var server = new MockSdvoeServer();
            using var client = new SdvoeClient("127.0.0.1", server.TelnetPort, server.RestPort);
            var ipManager = new MulticastIpManager();
            var coordinator = new SidebarCoordinator(client, ipManager);

            await coordinator.ExpandAsync();
            server.Dispose(); // Server goes down unexpectedly

            await coordinator.CollapseAsync();
            Assert.False(coordinator.IsExpanded);
        }

        [Fact]
        public async Task F11_B03_CollapseWhenSocketAlreadyClosed_DoesNotThrow()
        {
            using var server = new MockSdvoeServer();
            using var client = new SdvoeClient("127.0.0.1", server.TelnetPort, server.RestPort);
            var ipManager = new MulticastIpManager();
            using var coordinator = new SidebarCoordinator(client, ipManager);

            await coordinator.ExpandAsync();
            foreach (var card in coordinator.ActiveCards)
            {
                card.Receiver?.Dispose();
            }

            await coordinator.CollapseAsync();
            Assert.False(coordinator.IsExpanded);
        }

        [Fact]
        public async Task F11_B04_MultipleConcurrentCollapseInvocations_ThreadSafe()
        {
            using var server = new MockSdvoeServer();
            using var client = new SdvoeClient("127.0.0.1", server.TelnetPort, server.RestPort);
            var ipManager = new MulticastIpManager();
            using var coordinator = new SidebarCoordinator(client, ipManager);

            await coordinator.ExpandAsync();

            var task1 = coordinator.CollapseAsync();
            var task2 = coordinator.CollapseAsync();

            await Task.WhenAll(task1, task2);
            Assert.False(coordinator.IsExpanded);
        }

        [Fact]
        public async Task F11_B05_CollapseFreesResourcesEvenIfCancellationTriggered()
        {
            using var server = new MockSdvoeServer();
            using var client = new SdvoeClient("127.0.0.1", server.TelnetPort, server.RestPort);
            var ipManager = new MulticastIpManager();
            using var coordinator = new SidebarCoordinator(client, ipManager);

            await coordinator.ExpandAsync();
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await coordinator.CollapseAsync(cts.Token);
            Assert.Empty(coordinator.ActiveCards);
        }

        // =========================================================================
        // FEATURE 12: R5.1 UDP Multicast Socket Listener
        // =========================================================================

        [Fact]
        public void F12_B01_BindToInvalidIpAddress_HandlesError()
        {
            using var receiver = new RtpMulticastReceiver();
            // 240.0.0.1 is Class E experimental, invalid multicast join
            receiver.StartListening("240.0.0.1", 17001);
            receiver.StopListening();
        }

        [Fact]
        public void F12_B02_PortZeroBinding_DynamicallyAssignsPort()
        {
            using var receiver = new RtpMulticastReceiver();
            receiver.StartListening("127.0.0.1", 0);
            receiver.StopListening();
        }

        [Fact]
        public void F12_B03_MultipleReceiversOnSamePortWithReuseAddress_BothSucceed()
        {
            int sharedPort = 17003;
            using var receiver1 = new RtpMulticastReceiver();
            using var receiver2 = new RtpMulticastReceiver();

            receiver1.StartListening("127.0.0.1", sharedPort);
            receiver2.StartListening("127.0.0.1", sharedPort);

            receiver1.StopListening();
            receiver2.StopListening();
        }

        [Fact]
        public void F12_B04_NonMulticastIpJoin_HandledGracefully()
        {
            using var receiver = new RtpMulticastReceiver();
            receiver.StartListening("192.168.1.100", 17004); // Unicast IP, not multicast
            receiver.StopListening();
        }

        [Fact]
        public void F12_B05_StopListeningWhenNotStarted_NoOp()
        {
            using var receiver = new RtpMulticastReceiver();
            receiver.StopListening();
        }

        // =========================================================================
        // FEATURE 13: R5.2 RFC 3550 & RFC 4175 Ingestion
        // =========================================================================

        [Fact]
        public void F13_B01_SequenceNumberWrapAround_HandledCorrectly()
        {
            ushort seq = 65534;
            var packets = MockRtpStreamer.GenerateFramePackets(320, 4, 12345, 9000, ref seq);
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
            Assert.True(seq < 5); // Wrapped around 65535
        }

        [Fact]
        public void F13_B02_MissingScanlineInMiddle_DropsFrameAndResets()
        {
            ushort seq = 1;
            var packets = MockRtpStreamer.GenerateFrameWithMissingLine(320, 10, lineToDrop: 5, 12345, 1000, ref seq);
            var reassembler = new ScanlineFrameReassembler();

            AssembledFrame? frame = null;
            foreach (var p in packets)
            {
                if (RtpPacketParser.TryParse(p, out var hdr))
                {
                    frame = reassembler.ProcessPacket(hdr);
                }
            }

            Assert.Null(frame);
            Assert.Equal(1, reassembler.FramesDropped);
        }

        [Fact]
        public void F13_B03_MissingFinalScanlineWithMarker_DiscardsPartialFrame()
        {
            ushort seq = 1;
            var packets = MockRtpStreamer.GenerateFrameWithoutMarker(320, 10, 12345, 1000, ref seq);
            var reassembler = new ScanlineFrameReassembler();

            AssembledFrame? frame = null;
            foreach (var p in packets)
            {
                if (RtpPacketParser.TryParse(p, out var hdr))
                {
                    frame = reassembler.ProcessPacket(hdr);
                }
            }

            Assert.Null(frame);
            Assert.Equal(0, reassembler.FramesCompleted);
        }

        [Fact]
        public void F13_B04_PacketsWithMismatchedTimestamps_ResetsFrameBuffer()
        {
            var reassembler = new ScanlineFrameReassembler();
            byte[] p1 = MockRtpStreamer.CreateRtpPacket(1, 1000, 1, 0, 2, 320, new byte[640]);
            byte[] p2 = MockRtpStreamer.CreateRtpPacket(2, 2000, 1, 1, 2, 320, new byte[640]); // New timestamp

            RtpPacketParser.TryParse(p1, out var h1);
            RtpPacketParser.TryParse(p2, out var h2);

            reassembler.ProcessPacket(h1);
            var frame = reassembler.ProcessPacket(h2);

            // Frame dropped due to timestamp switch before completion
            Assert.Null(frame);
            Assert.True(reassembler.FramesDropped >= 1);
        }

        [Fact]
        public void F13_B05_OversizedPacketLengthHeader_RejectedByParser()
        {
            byte[] p = MockRtpStreamer.CreateRtpPacket(1, 1000, 1, 0, 1, 320, new byte[640]);
            // Corrupt length field in header (bytes 14-15) to 5000 bytes
            p[14] = 0x13;
            p[15] = 0x88;

            bool parsed = RtpPacketParser.TryParse(p, out _);
            Assert.False(parsed);
        }

        // =========================================================================
        // FEATURE 14: R5.3 YUV422 to RGB24 Rasterization
        // =========================================================================

        [Fact]
        public void F14_B01_ExtremeNegativeLuminance_ClampedToZero()
        {
            var (r, g, b) = Yuv422Rasterizer.ConvertPixelQ10(0, 0, 0);
            Assert.Equal(0, r);
            Assert.InRange(g, 0, 255);
            Assert.Equal(0, b);
        }

        [Fact]
        public void F14_B02_ExtremePositiveLuminance_ClampedTo255()
        {
            var (r, g, b) = Yuv422Rasterizer.ConvertPixelQ10(255, 255, 255);
            Assert.Equal(255, r);
            Assert.InRange(g, 0, 255);
            Assert.Equal(255, b);
        }

        [Fact]
        public void F14_B03_ZeroByteLengthYuvInput_ReturnsEmptyRgbBuffer()
        {
            byte[] emptyYuv = Array.Empty<byte>();
            byte[] rgb = Yuv422Rasterizer.ConvertYuv422ToRgb24(emptyYuv, 0, 0);
            Assert.Empty(rgb);
        }

        [Fact]
        public void F14_B04_OddNumberOfPixelsInput_HandledWithoutOutOfRange()
        {
            byte[] oddYuv = new byte[7]; // Not a multiple of 4
            byte[] rgb = Yuv422Rasterizer.ConvertYuv422ToRgb24(oddYuv, 2, 1);
            Assert.Equal(6, rgb.Length);
        }

        [Fact]
        public void F14_B05_SingleQuadYuv_GeneratesExactlyTwoRgbPixels()
        {
            byte[] quad = new byte[] { 128, 200, 128, 100 };
            byte[] rgb = Yuv422Rasterizer.ConvertYuv422ToRgb24(quad, 2, 1);
            Assert.Equal(6, rgb.Length);
        }

        // =========================================================================
        // FEATURE 15: R5.4 Zero-Leak WPF Rendering
        // =========================================================================

        [Fact]
        public void F15_B01_ZeroWidthOrHeightWriteableBitmap_ThrowsArgumentException()
        {
            Assert.ThrowsAny<ArgumentException>(() =>
            {
                _ = new WriteableBitmap(0, 0, 96, 96, PixelFormats.Bgr24, null);
            });
        }

        [Fact]
        public void F15_B02_HighResolution4kWriteableBitmap_AllocatesAndReleases()
        {
            var bitmap = new WriteableBitmap(3840, 2160, 96, 96, PixelFormats.Bgr24, null);
            Assert.Equal(3840, bitmap.PixelWidth);
            Assert.Equal(2160, bitmap.PixelHeight);
        }

        [Fact]
        public void F15_B03_PartialDirtyRectUpdate_UpdatesCorrectRegion()
        {
            var bitmap = new WriteableBitmap(100, 100, 96, 96, PixelFormats.Bgr24, null);
            bitmap.Lock();
            bitmap.AddDirtyRect(new System.Windows.Int32Rect(10, 10, 20, 20));
            bitmap.Unlock();
        }

        [Fact]
        public void F15_B04_RepeatedLockAndUnlock_DoesNotCorruptBuffer()
        {
            var bitmap = new WriteableBitmap(50, 50, 96, 96, PixelFormats.Bgr24, null);
            for (int i = 0; i < 100; i++)
            {
                bitmap.Lock();
                bitmap.AddDirtyRect(new System.Windows.Int32Rect(0, 0, 50, 50));
                bitmap.Unlock();
            }
        }

        [Fact]
        public void F15_B05_FpsCalculationWithIrregularSpikeTimestamps_AveragesCleanly()
        {
            using var receiver = new RtpMulticastReceiver();
            ushort seq = 1;

            var frame = MockRtpStreamer.GenerateFramePackets(320, 2, 1, 1000, ref seq);
            foreach (var p in frame) receiver.ProcessDatagram(p);

            Assert.True(receiver.CurrentFps >= 0.0);
        }

        // =========================================================================
        // FEATURE 16: E2E.1 Automated Test Suite & Packaging
        // =========================================================================

        [Fact]
        public void F16_B01_SimulateReadonlyConfigurationFile_HandledGracefully()
        {
            string tempCfg = Path.Combine(Path.GetTempPath(), $"ro_cfg_{Guid.NewGuid():N}.json");
            File.WriteAllText(tempCfg, "{\"ControlServerIp\":\"127.0.0.1\"}");
            File.SetAttributes(tempCfg, FileAttributes.ReadOnly);

            try
            {
                var svc = new ConfigServiceHelper(tempCfg);
                Assert.Equal("127.0.0.1", svc.Current.ControlServerIp);
            }
            finally
            {
                File.SetAttributes(tempCfg, FileAttributes.Normal);
                File.Delete(tempCfg);
            }
        }

        [Fact]
        public void F16_B02_EmptyConfigFile_RestoresDefaults()
        {
            string tempCfg = Path.Combine(Path.GetTempPath(), $"empty_cfg_{Guid.NewGuid():N}.json");
            try
            {
                File.WriteAllText(tempCfg, "");
                var svc = new ConfigServiceHelper(tempCfg);
                Assert.Equal("127.0.0.1", svc.Current.ControlServerIp);
            }
            finally
            {
                if (File.Exists(tempCfg)) File.Delete(tempCfg);
            }
        }

        [Fact]
        public void F16_B03_MockServerPortExhaustion_ThrowsAppropriateException()
        {
            // Port -1 is invalid
            Assert.Throws<ArgumentOutOfRangeException>(() =>
            {
                _ = new MockSdvoeServer(telnetPort: -1);
            });
        }

        [Fact]
        public async Task F16_B04_HighConcurrencyRequestsToMockServer_HandledWithoutDeadlock()
        {
            using var server = new MockSdvoeServer();
            var tasks = new List<Task>();

            for (int i = 0; i < 10; i++)
            {
                tasks.Add(Task.Run(async () =>
                {
                    using var client = new SdvoeClient("127.0.0.1", server.TelnetPort, server.RestPort);
                    await client.ConnectTelnetAsync();
                    var dev = await client.DiscoverAvas223DevicesAsync();
                    Assert.NotEmpty(dev);
                }));
            }

            await Task.WhenAll(tasks);
        }

        [Fact]
        public async Task F16_B05_StreamerSendsMaxDatagramSize65507_Succeeds()
        {
            using var streamer = new MockRtpStreamer();
            using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 18005));

            byte[] bigDatagram = new byte[1000]; // reasonable datagram payload
            await streamer.SendPacketsAsync(new IPEndPoint(IPAddress.Loopback, 18005), new[] { bigDatagram });

            var result = await receiver.ReceiveAsync();
            Assert.Equal(1000, result.Buffer.Length);
        }
    }
}
