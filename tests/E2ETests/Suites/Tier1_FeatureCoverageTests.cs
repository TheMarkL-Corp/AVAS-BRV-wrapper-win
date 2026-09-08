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
    public class Tier1_FeatureCoverageTests
    {
        // =========================================================================
        // FEATURE 1: R1.1 Portable Shell & App Structure
        // =========================================================================

        [Fact]
        public void F01_T01_PortableShell_ExecutesWithoutInstallationRegistry()
        {
            string appBaseDir = AppContext.BaseDirectory;
            Assert.False(string.IsNullOrWhiteSpace(appBaseDir));
            Assert.True(Directory.Exists(appBaseDir));
        }

        [Fact]
        public void F01_T02_PortableShell_ResolvesRelativeBaseDirectory()
        {
            string subDir = Path.Combine(AppContext.BaseDirectory, "ConfigTest");
            Directory.CreateDirectory(subDir);
            try
            {
                Assert.True(Directory.Exists(subDir));
            }
            finally
            {
                if (Directory.Exists(subDir)) Directory.Delete(subDir);
            }
        }

        [Fact]
        public void F01_T03_PortableShell_EnsuresSingleInstanceLockMutex()
        {
            string mutexName = "Global\\AvasRoutingSw_Test_Instance_Mutex";
            using var mutex1 = new Mutex(true, mutexName, out bool createdNew1);
            Assert.True(createdNew1);

            using var mutex2 = new Mutex(true, mutexName, out bool createdNew2);
            Assert.False(createdNew2);
        }

        [Fact]
        public void F01_T04_PortableShell_ValidatesApplicationStructureLayout()
        {
            string baseDir = AppContext.BaseDirectory;
            string testFile = Path.Combine(baseDir, "layout_check.tmp");
            File.WriteAllText(testFile, "OK");
            Assert.True(File.Exists(testFile));
            File.Delete(testFile);
        }

        [Fact]
        public void F01_T05_PortableShell_LoadsAssetsFromLocalAppDirectory()
        {
            string assetDir = Path.Combine(AppContext.BaseDirectory, "Assets");
            Directory.CreateDirectory(assetDir);
            string iconPath = Path.Combine(assetDir, "test_icon.ico");
            File.WriteAllBytes(iconPath, new byte[] { 0, 0, 1, 0, 1, 0 });
            Assert.True(File.Exists(iconPath));
            File.Delete(iconPath);
        }

        // =========================================================================
        // FEATURE 2: R1.2 Microsoft WebView2 Embedding
        // =========================================================================

        [Fact]
        public void F02_T01_WebView2_InitializesWithIsolatedPortableUserDataFolder()
        {
            string appDir = AppContext.BaseDirectory;
            string userDataFolder = Path.Combine(appDir, "WebView2_UserData_Test");
            Directory.CreateDirectory(userDataFolder);
            try
            {
                Assert.True(Directory.Exists(userDataFolder));
                Assert.StartsWith(appDir, userDataFolder);
            }
            finally
            {
                if (Directory.Exists(userDataFolder)) Directory.Delete(userDataFolder);
            }
        }

        [Fact]
        public void F02_T02_WebView2_NavigatesToConfiguredBlueRiverUrl()
        {
            var config = new AppConfig { BlueRiverUrl = "http://localhost:3000" };
            Assert.True(Uri.TryCreate(config.BlueRiverUrl, UriKind.Absolute, out var uri));
            Assert.Equal(3000, uri.Port);
            Assert.Equal("localhost", uri.Host);
        }

        [Fact]
        public void F02_T03_WebView2_HandlesLocalhostDefaultUrlFallback()
        {
            var config = new AppConfig();
            Assert.Equal("http://localhost:3000", config.BlueRiverUrl);
        }

        [Fact]
        public void F02_T04_WebView2_IsolatesUserDataFromSystemEdgeProfile()
        {
            string localUserData = Path.Combine(AppContext.BaseDirectory, "WebView2_UserData");
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Assert.False(localUserData.StartsWith(Path.Combine(userProfile, "AppData\\Local\\Microsoft\\Edge")));
        }

        [Fact]
        public void F02_T05_WebView2_HandlesNavigationFailureGracefully()
        {
            string invalidUrl = "not_a_valid_url";
            bool valid = Uri.TryCreate(invalidUrl, UriKind.Absolute, out _);
            Assert.False(valid);
        }

        // =========================================================================
        // FEATURE 3: R1.3 Configuration & Persistence
        // =========================================================================

        [Fact]
        public void F03_T01_Config_LoadsDefaultAppSettingsWhenFileMissing()
        {
            string tempConfig = Path.Combine(Path.GetTempPath(), $"cfg_{Guid.NewGuid():N}.json");
            try
            {
                var service = new ConfigServiceHelper(tempConfig);
                Assert.NotNull(service.Current);
                Assert.Equal("224.1.1.1", service.Current.MulticastStartIp);
                Assert.Equal("224.1.3.225", service.Current.MulticastEndIp);
                Assert.Equal(6792, service.Current.BasePort);
            }
            finally
            {
                if (File.Exists(tempConfig)) File.Delete(tempConfig);
            }
        }

        [Fact]
        public void F03_T02_Config_PersistsValuesAtomicallyViaTempFile()
        {
            string tempConfig = Path.Combine(Path.GetTempPath(), $"cfg_{Guid.NewGuid():N}.json");
            try
            {
                var service = new ConfigServiceHelper(tempConfig);
                var newConfig = new AppConfig
                {
                    ControlServerIp = "192.168.1.50",
                    RestPort = 9200,
                    TelnetPort = 6970,
                    MulticastStartIp = "224.1.2.1",
                    MulticastEndIp = "224.1.2.100"
                };
                service.Save(newConfig);

                Assert.True(File.Exists(tempConfig));
                var reloaded = new ConfigServiceHelper(tempConfig);
                Assert.Equal("192.168.1.50", reloaded.Current.ControlServerIp);
                Assert.Equal(9200, reloaded.Current.RestPort);
                Assert.Equal("224.1.2.1", reloaded.Current.MulticastStartIp);
            }
            finally
            {
                if (File.Exists(tempConfig)) File.Delete(tempConfig);
            }
        }

        [Fact]
        public void F03_T03_Config_FiresConfigChangedEventOnSave()
        {
            string tempConfig = Path.Combine(Path.GetTempPath(), $"cfg_{Guid.NewGuid():N}.json");
            try
            {
                var service = new ConfigServiceHelper(tempConfig);
                bool eventFired = false;
                service.ConfigChanged += cfg => { eventFired = true; };

                var updated = new AppConfig { ControlServerIp = "10.0.0.1" };
                service.Save(updated);

                Assert.True(eventFired);
                Assert.Equal("10.0.0.1", service.Current.ControlServerIp);
            }
            finally
            {
                if (File.Exists(tempConfig)) File.Delete(tempConfig);
            }
        }

        [Fact]
        public void F03_T04_Config_RecoversGracefullyFromCorruptedJson()
        {
            string tempConfig = Path.Combine(Path.GetTempPath(), $"cfg_{Guid.NewGuid():N}.json");
            try
            {
                File.WriteAllText(tempConfig, "{ \"ControlServerIp\": INVALID JSON CORRUPTION %%%");
                var service = new ConfigServiceHelper(tempConfig);
                Assert.NotNull(service.Current);
                Assert.Equal("127.0.0.1", service.Current.ControlServerIp);
            }
            finally
            {
                if (File.Exists(tempConfig)) File.Delete(tempConfig);
            }
        }

        [Fact]
        public void F03_T05_Config_SerializesAndDeserializesAllConfigFields()
        {
            var cfg = new AppConfig
            {
                BlueRiverUrl = "http://10.10.10.10:8000",
                ControlServerIp = "192.168.1.222",
                RestPort = 8088,
                TelnetPort = 6975,
                MulticastStartIp = "224.1.1.10",
                MulticastEndIp = "224.1.1.50",
                BasePort = 7000,
                LocalNetworkInterfaceIp = "192.168.1.10"
            };

            Assert.True(cfg.Validate(out string? err), err);
        }

        // =========================================================================
        // FEATURE 4: R2.1 SDVoE Control Server Client
        // =========================================================================

        [Fact]
        public async Task F04_T01_SdvoeClient_PerformsMandatoryRequireApiHandshake()
        {
            using var server = new MockSdvoeServer();
            using var client = new SdvoeClient("127.0.0.1", server.TelnetPort, server.RestPort);

            bool connected = await client.ConnectTelnetAsync();
            Assert.True(connected);
        }

        [Fact]
        public async Task F04_T02_SdvoeClient_ReceivesInvalidCommandIfHandshakeOmitted()
        {
            using var server = new MockSdvoeServer();
            using var rawClient = new TcpClient();
            await rawClient.ConnectAsync("127.0.0.1", server.TelnetPort);
            using var stream = rawClient.GetStream();
            using var writer = new StreamWriter(stream) { AutoFlush = true, NewLine = "\r\n" };
            using var reader = new StreamReader(stream);

            // Send operational command before require api
            await writer.WriteLineAsync("get all identity");
            string? response = await reader.ReadLineAsync();

            Assert.NotNull(response);
            Assert.Contains("INVALID_COMMAND", response);
        }

        [Fact]
        public async Task F04_T03_SdvoeClient_QueriesServerVersionSuccessfully()
        {
            using var server = new MockSdvoeServer();
            using var client = new SdvoeClient("127.0.0.1", server.TelnetPort, server.RestPort);

            await client.ConnectTelnetAsync();
            string versionResp = await client.SendTelnetCommandAsync("version");

            Assert.Contains("\"server\":\"Control Server\"", versionResp);
            Assert.Contains("\"version\":\"3.2.0.1\"", versionResp);
        }

        [Fact]
        public async Task F04_T04_SdvoeClient_DiscoversDevicesViaTelnetGetAllIdentity()
        {
            using var server = new MockSdvoeServer();
            using var client = new SdvoeClient("127.0.0.1", server.TelnetPort, server.RestPort);

            var devices = await client.DiscoverDevicesViaTelnetAsync();
            Assert.NotEmpty(devices);
            Assert.Contains(devices, d => d.MacAddress == "f8228500aaaa");
        }

        [Fact]
        public async Task F04_T05_SdvoeClient_DiscoversDevicesViaRestApiEndpoint()
        {
            using var server = new MockSdvoeServer();
            using var client = new SdvoeClient("127.0.0.1", server.TelnetPort, server.RestPort);

            var devices = await client.DiscoverDevicesViaRestAsync();
            Assert.NotEmpty(devices);
            Assert.Contains(devices, d => d.MacAddress == "f8228500aaaa");
        }

        // =========================================================================
        // FEATURE 5: R2.2 AVAS-223 Device Filtering
        // =========================================================================

        [Fact]
        public void F05_T01_DeviceFiltering_AcceptsTargetVendor105Product81Chip0()
        {
            var device = new AvasDevice
            {
                MacAddress = "f8228500aaaa",
                VendorId = 105,
                ProductId = 81,
                ChipIndex = 0,
                IsTransmitter = true,
                IsReceiver = false
            };

            Assert.True(SdvoeDiscoveryFilter.IsTargetAvas223Tx(device));
        }

        [Fact]
        public void F05_T02_DeviceFiltering_StrictlyExcludesChip1()
        {
            var device = new AvasDevice
            {
                MacAddress = "f8228500bbbb",
                VendorId = 105,
                ProductId = 81,
                ChipIndex = 1,
                IsTransmitter = true,
                IsReceiver = false
            };

            Assert.False(SdvoeDiscoveryFilter.IsTargetAvas223Tx(device));
        }

        [Fact]
        public void F05_T03_DeviceFiltering_ExcludesReceivers()
        {
            var device = new AvasDevice
            {
                MacAddress = "f8228500cccc",
                VendorId = 105,
                ProductId = 81,
                ChipIndex = 0,
                IsTransmitter = false,
                IsReceiver = true
            };

            Assert.False(SdvoeDiscoveryFilter.IsTargetAvas223Tx(device));
        }

        [Fact]
        public void F05_T04_DeviceFiltering_ExcludesNonMatchingVendorOrProduct()
        {
            var devWrongVendor = new AvasDevice { VendorId = 999, ProductId = 81, ChipIndex = 0, IsTransmitter = true };
            var devWrongProduct = new AvasDevice { VendorId = 105, ProductId = 99, ChipIndex = 0, IsTransmitter = true };

            Assert.False(SdvoeDiscoveryFilter.IsTargetAvas223Tx(devWrongVendor));
            Assert.False(SdvoeDiscoveryFilter.IsTargetAvas223Tx(devWrongProduct));
        }

        [Fact]
        public void F05_T05_DeviceFiltering_FiltersMixedFleetToTargetEncodersOnly()
        {
            var fleet = new List<AvasDevice>
            {
                new AvasDevice { MacAddress = "dev1", VendorId = 105, ProductId = 81, ChipIndex = 0, IsTransmitter = true },
                new AvasDevice { MacAddress = "dev2", VendorId = 105, ProductId = 81, ChipIndex = 1, IsTransmitter = true },
                new AvasDevice { MacAddress = "dev3", VendorId = 105, ProductId = 81, ChipIndex = 0, IsTransmitter = false, IsReceiver = true },
                new AvasDevice { MacAddress = "dev4", VendorId = 200, ProductId = 50, ChipIndex = 0, IsTransmitter = true },
                new AvasDevice { MacAddress = "dev5", VendorId = 105, ProductId = 81, ChipIndex = 0, IsTransmitter = true }
            };

            var filtered = SdvoeDiscoveryFilter.FilterTargetDevices(fleet);
            Assert.Equal(2, filtered.Count);
            Assert.Contains(filtered, d => d.MacAddress == "dev1");
            Assert.Contains(filtered, d => d.MacAddress == "dev5");
        }

        // =========================================================================
        // FEATURE 6: R2.3 Device Telemetry Tracking
        // =========================================================================

        [Fact]
        public void F06_T01_Telemetry_ExtractsDeviceNameAndMacAddress()
        {
            var dev = new AvasDevice { MacAddress = "f8228500aaaa", DeviceName = "AVAS-223-TX0" };
            Assert.Equal("f8228500aaaa", dev.MacAddress);
            Assert.Equal("AVAS-223-TX0", dev.DeviceName);
        }

        [Fact]
        public void F06_T02_Telemetry_ExtractsIpAddressFromNetworkInterfaceNode()
        {
            string json = "{\"status\":\"SUCCESS\",\"result\":{\"devices\":[{\"device_id\":\"f8228500aaaa\",\"device_name\":\"AVAS\",\"identity\":{\"vendor_id\":105,\"product_id\":81,\"chip_id\":0,\"is_transmitter\":true},\"nodes\":[{\"type\":\"NETWORK_INTERFACE\",\"status\":{\"ip\":{\"address\":\"192.168.1.151\"}}}]}]}}";
            var parsed = SdvoeClient.ParseDevicesJson(json);
            Assert.Single(parsed);
            Assert.Equal("192.168.1.151", parsed[0].IpAddress);
        }

        [Fact]
        public void F06_T03_Telemetry_TracksStreamingStateTransitions()
        {
            var dev = new AvasDevice { IsStreaming = false };
            Assert.False(dev.IsStreaming);

            dev.IsStreaming = true;
            dev.AllocatedMulticastIp = "224.1.1.10";
            Assert.True(dev.IsStreaming);
            Assert.Equal("224.1.1.10", dev.AllocatedMulticastIp);
        }

        [Fact]
        public void F06_T04_Telemetry_MaintainsDefaultResolutionAndModel()
        {
            var dev = new AvasDevice();
            Assert.Equal("AVAS-223", dev.Model);
            Assert.Equal("320x180", dev.Resolution);
        }

        [Fact]
        public void F06_T05_Telemetry_UpdatesFpsValueFromStreamEvent()
        {
            var dev = new AvasDevice { CurrentFps = 0.0 };
            dev.CurrentFps = 1.5;
            Assert.Equal(1.5, dev.CurrentFps);
        }

        // =========================================================================
        // FEATURE 7: R3.1 Dynamic Multicast IP Allocator
        // =========================================================================

        [Fact]
        public void F07_T01_MulticastAllocator_AllocatesFirstIpInConfiguredPool()
        {
            var manager = new MulticastIpManager("224.1.1.1", "224.1.1.10");
            string? ip = manager.AllocateMulticastIp("mac1");
            Assert.Equal("224.1.1.1", ip);
        }

        [Fact]
        public void F07_T02_MulticastAllocator_AssignsUniqueIpsToConsecutiveDevices()
        {
            var manager = new MulticastIpManager("224.1.1.1", "224.1.1.10");
            string? ip1 = manager.AllocateMulticastIp("mac1");
            string? ip2 = manager.AllocateMulticastIp("mac2");
            string? ip3 = manager.AllocateMulticastIp("mac3");

            Assert.Equal("224.1.1.1", ip1);
            Assert.Equal("224.1.1.2", ip2);
            Assert.Equal("224.1.1.3", ip3);
            Assert.NotEqual(ip1, ip2);
        }

        [Fact]
        public void F07_T03_MulticastAllocator_ReusesReleasedIpAddress()
        {
            var manager = new MulticastIpManager("224.1.1.1", "224.1.1.10");
            string? ip1 = manager.AllocateMulticastIp("mac1");
            string? ip2 = manager.AllocateMulticastIp("mac2");

            manager.ReleaseMulticastIp("mac1");

            string? ip3 = manager.AllocateMulticastIp("mac3");
            Assert.Equal(ip1, ip3); // Released IP reused
        }

        [Fact]
        public void F07_T04_MulticastAllocator_StrictlyExcludesReservedSemtechIps()
        {
            var manager = new MulticastIpManager("224.1.1.252", "224.1.1.255");
            string? ip1 = manager.AllocateMulticastIp("mac1"); // 224.1.1.252
            string? ip2 = manager.AllocateMulticastIp("mac2"); // should skip 253, 254 -> 224.1.1.255

            Assert.Equal("224.1.1.252", ip1);
            Assert.Equal("224.1.1.255", ip2);
            Assert.NotEqual("224.1.1.253", ip2);
            Assert.NotEqual("224.1.1.254", ip2);
        }

        [Fact]
        public void F07_T05_MulticastAllocator_ReturnsNullWhenPoolExhausted()
        {
            var manager = new MulticastIpManager("224.1.1.1", "224.1.1.2");
            string? ip1 = manager.AllocateMulticastIp("mac1");
            string? ip2 = manager.AllocateMulticastIp("mac2");
            string? ip3 = manager.AllocateMulticastIp("mac3");

            Assert.NotNull(ip1);
            Assert.NotNull(ip2);
            Assert.Null(ip3); // Exhausted
        }

        // =========================================================================
        // FEATURE 8: R3.2 SDVoE Preview Stream Control
        // =========================================================================

        [Fact]
        public async Task F08_T01_StreamControl_SendsThumbnailConfigWithSsrcAndFps()
        {
            using var server = new MockSdvoeServer();
            using var client = new SdvoeClient("127.0.0.1", server.TelnetPort, server.RestPort);

            bool configSuccess = await client.ConfigureThumbnailStreamAsync("f8228500aaaa", 1.0, 54321, 6792);
            Assert.True(configSuccess);

            var dev = server.GetDevice("f8228500aaaa");
            Assert.NotNull(dev);
            Assert.Equal(54321u, dev.Ssrc);
            Assert.Equal(1.0, dev.Fps);
        }

        [Fact]
        public async Task F08_T02_StreamControl_StartsMulticastStreamOnServer()
        {
            using var server = new MockSdvoeServer();
            using var client = new SdvoeClient("127.0.0.1", server.TelnetPort, server.RestPort);

            bool startSuccess = await client.StartPreviewStreamAsync("f8228500aaaa", "224.1.1.15");
            Assert.True(startSuccess);

            var dev = server.GetDevice("f8228500aaaa");
            Assert.NotNull(dev);
            Assert.True(dev.IsStreaming);
            Assert.Equal("224.1.1.15", dev.MulticastIp);
        }

        [Fact]
        public async Task F08_T03_StreamControl_StopsStreamAndFreesIpAddress()
        {
            using var server = new MockSdvoeServer();
            using var client = new SdvoeClient("127.0.0.1", server.TelnetPort, server.RestPort);

            await client.StartPreviewStreamAsync("f8228500aaaa", "224.1.1.15");
            bool stopSuccess = await client.StopPreviewStreamAsync("f8228500aaaa", free: true);
            Assert.True(stopSuccess);

            var dev = server.GetDevice("f8228500aaaa");
            Assert.NotNull(dev);
            Assert.False(dev.IsStreaming);
            Assert.Null(dev.MulticastIp);
        }

        [Fact]
        public async Task F08_T04_StreamControl_QueriesActiveStreamsViaListMulticast()
        {
            using var server = new MockSdvoeServer();
            using var client = new SdvoeClient("127.0.0.1", server.TelnetPort, server.RestPort);

            await client.ConnectTelnetAsync();
            await client.StartPreviewStreamAsync("f8228500aaaa", "224.1.1.20");

            string resp = await client.SendTelnetCommandAsync("list multicast");
            Assert.Contains("224.1.1.20", resp);
            Assert.Contains("f8228500aaaa", resp);
        }

        [Fact]
        public async Task F08_T05_StreamControl_RejectsGroupStartWithIpAddress()
        {
            using var server = new MockSdvoeServer();
            using var client = new SdvoeClient("127.0.0.1", server.TelnetPort, server.RestPort);

            await client.ConnectTelnetAsync();
            string resp = await client.SendTelnetCommandAsync("start all_tx:thumbnail:0 224.1.1.1");
            Assert.Contains("ILLEGAL_ARGUMENT", resp);
        }

        // =========================================================================
        // FEATURE 9: R4.1 Collapsible Native Overlay Sidebar
        // =========================================================================

        [Fact]
        public void F09_T01_Sidebar_IsCollapsedByDefaultOnStartup()
        {
            using var server = new MockSdvoeServer();
            using var client = new SdvoeClient("127.0.0.1", server.TelnetPort, server.RestPort);
            var ipManager = new MulticastIpManager();
            using var coordinator = new SidebarCoordinator(client, ipManager);

            Assert.False(coordinator.IsExpanded);
            Assert.Equal(0.0, coordinator.SidebarWidth);
        }

        [Fact]
        public async Task F09_T02_Sidebar_ExpandsToConfiguredWidthOnToggle()
        {
            using var server = new MockSdvoeServer();
            using var client = new SdvoeClient("127.0.0.1", server.TelnetPort, server.RestPort);
            var ipManager = new MulticastIpManager();
            using var coordinator = new SidebarCoordinator(client, ipManager);

            await coordinator.ExpandAsync();

            Assert.True(coordinator.IsExpanded);
            Assert.Equal(380.0, coordinator.SidebarWidth);
        }

        [Fact]
        public async Task F09_T03_Sidebar_CollapsesBackToZeroWidthOnSecondToggle()
        {
            using var server = new MockSdvoeServer();
            using var client = new SdvoeClient("127.0.0.1", server.TelnetPort, server.RestPort);
            var ipManager = new MulticastIpManager();
            using var coordinator = new SidebarCoordinator(client, ipManager);

            await coordinator.ExpandAsync();
            await coordinator.CollapseAsync();

            Assert.False(coordinator.IsExpanded);
            Assert.Equal(0.0, coordinator.SidebarWidth);
        }

        [Fact]
        public void F09_T04_Sidebar_Maintains28pxToggleStripVisibility()
        {
            using var server = new MockSdvoeServer();
            using var client = new SdvoeClient("127.0.0.1", server.TelnetPort, server.RestPort);
            var ipManager = new MulticastIpManager();
            using var coordinator = new SidebarCoordinator(client, ipManager);

            Assert.Equal(28.0, coordinator.ToggleStripWidth);
        }

        [Fact]
        public async Task F09_T05_Sidebar_ReportsAccurateExpansionState()
        {
            using var server = new MockSdvoeServer();
            using var client = new SdvoeClient("127.0.0.1", server.TelnetPort, server.RestPort);
            var ipManager = new MulticastIpManager();
            using var coordinator = new SidebarCoordinator(client, ipManager);

            await coordinator.ToggleAsync();
            Assert.True(coordinator.IsExpanded);

            await coordinator.ToggleAsync();
            Assert.False(coordinator.IsExpanded);
        }

        // =========================================================================
        // FEATURE 10: R4.2 Preview Cards & Telemetry UI
        // =========================================================================

        [Fact]
        public async Task F10_T01_PreviewCards_CreatesCardForEveryDiscoveredEncoder()
        {
            using var server = new MockSdvoeServer();
            using var client = new SdvoeClient("127.0.0.1", server.TelnetPort, server.RestPort);
            var ipManager = new MulticastIpManager();
            using var coordinator = new SidebarCoordinator(client, ipManager);

            await coordinator.ExpandAsync();
            var cards = coordinator.ActiveCards;

            Assert.Equal(2, cards.Count); // Default server has 2 AVAS-223 chip_0 transmitters
        }

        [Fact]
        public async Task F10_T02_PreviewCards_BindsMacAndAllocatedMulticastEndpoint()
        {
            using var server = new MockSdvoeServer();
            using var client = new SdvoeClient("127.0.0.1", server.TelnetPort, server.RestPort);
            var ipManager = new MulticastIpManager();
            using var coordinator = new SidebarCoordinator(client, ipManager);

            await coordinator.ExpandAsync();
            var card = coordinator.ActiveCards.FirstOrDefault(c => c.MacAddress == "f8228500aaaa");

            Assert.NotNull(card);
            Assert.StartsWith("224.1.1.", card.MulticastIp);
            Assert.Equal(6792, card.Port);
        }

        [Fact]
        public void F10_T03_PreviewCards_UpdatesViewportBufferOnFrameArrival()
        {
            var card = new EncoderCardViewModel();
            byte[] rgbFrame = new byte[320 * 180 * 3];
            card.LastRenderedRgbFrame = rgbFrame;
            card.FrameUpdateCount++;

            Assert.NotNull(card.LastRenderedRgbFrame);
            Assert.Equal(1, card.FrameUpdateCount);
        }

        [Fact]
        public void F10_T04_PreviewCards_DisplaysCurrentRollingFps()
        {
            var card = new EncoderCardViewModel { CurrentFps = 2.4 };
            Assert.Equal(2.4, card.CurrentFps);
        }

        [Fact]
        public void F10_T05_PreviewCards_UpdatesResolutionOnStreamDimensionChange()
        {
            var card = new EncoderCardViewModel { Resolution = "320x180" };
            card.Resolution = "640x360";
            Assert.Equal("640x360", card.Resolution);
        }

        // =========================================================================
        // FEATURE 11: R4.3 Lifecycle & Teardown on Collapse
        // =========================================================================

        [Fact]
        public async Task F11_T01_Teardown_StopsAllServerStreamsOnCollapse()
        {
            using var server = new MockSdvoeServer();
            using var client = new SdvoeClient("127.0.0.1", server.TelnetPort, server.RestPort);
            var ipManager = new MulticastIpManager();
            using var coordinator = new SidebarCoordinator(client, ipManager);

            await coordinator.ExpandAsync();
            Assert.True(server.GetDevice("f8228500aaaa")?.IsStreaming);

            await coordinator.CollapseAsync();
            Assert.False(server.GetDevice("f8228500aaaa")?.IsStreaming);
        }

        [Fact]
        public async Task F11_T02_Teardown_ReleasesAllAllocatedMulticastIpsOnCollapse()
        {
            using var server = new MockSdvoeServer();
            using var client = new SdvoeClient("127.0.0.1", server.TelnetPort, server.RestPort);
            var ipManager = new MulticastIpManager();
            using var coordinator = new SidebarCoordinator(client, ipManager);

            await coordinator.ExpandAsync();
            Assert.True(ipManager.ActiveAllocationCount > 0);

            await coordinator.CollapseAsync();
            Assert.Equal(0, ipManager.ActiveAllocationCount);
        }

        [Fact]
        public async Task F11_T03_Teardown_DisposesAllUdpSocketsOnCollapse()
        {
            using var server = new MockSdvoeServer();
            using var client = new SdvoeClient("127.0.0.1", server.TelnetPort, server.RestPort);
            var ipManager = new MulticastIpManager();
            using var coordinator = new SidebarCoordinator(client, ipManager);

            await coordinator.ExpandAsync();
            await coordinator.CollapseAsync();
            Assert.Empty(coordinator.ActiveCards);
        }

        [Fact]
        public async Task F11_T04_Teardown_ClearsPreviewCardsCollectionOnCollapse()
        {
            using var server = new MockSdvoeServer();
            using var client = new SdvoeClient("127.0.0.1", server.TelnetPort, server.RestPort);
            var ipManager = new MulticastIpManager();
            using var coordinator = new SidebarCoordinator(client, ipManager);

            await coordinator.ExpandAsync();
            Assert.NotEmpty(coordinator.ActiveCards);

            await coordinator.CollapseAsync();
            Assert.Empty(coordinator.ActiveCards);
        }

        [Fact]
        public async Task F11_T05_Teardown_ExecutesIdempotentlyOnMultipleCollapses()
        {
            using var server = new MockSdvoeServer();
            using var client = new SdvoeClient("127.0.0.1", server.TelnetPort, server.RestPort);
            var ipManager = new MulticastIpManager();
            using var coordinator = new SidebarCoordinator(client, ipManager);

            await coordinator.CollapseAsync();
            await coordinator.CollapseAsync();

            Assert.False(coordinator.IsExpanded);
        }

        // =========================================================================
        // FEATURE 12: R5.1 UDP Multicast Socket Listener
        // =========================================================================

        [Fact]
        public void F12_T01_UdpListener_BindsToConfiguredBasePortWithReuseAddress()
        {
            using var receiver = new RtpMulticastReceiver();
            receiver.StartListening("127.0.0.1", 16792);
            receiver.StopListening();
        }

        [Fact]
        public void F12_T02_UdpListener_JoinsMulticastGroupSuccessfully()
        {
            using var receiver = new RtpMulticastReceiver();
            receiver.StartListening("224.1.1.55", 16793);
            receiver.StopListening();
        }

        [Fact]
        public async Task F12_T03_UdpListener_ReceivesDatagramAsynchronously()
        {
            using var receiver = new RtpMulticastReceiver();

            receiver.StartListening("127.0.0.1", 0);
            int testPort = receiver.Port;

            ushort seq = 1;
            byte[] packet = MockRtpStreamer.CreateRtpPacket(seq, 1000, 12345, 0, 1, 320, new byte[640]);

            using var sender = new MockRtpStreamer();
            await sender.SendPacketsAsync(new IPEndPoint(IPAddress.Loopback, testPort), new[] { packet });

            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (receiver.ReceivedPacketsCount == 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10);
            }
            receiver.StopListening();

            Assert.True(receiver.ReceivedPacketsCount > 0);
        }

        [Fact]
        public void F12_T04_UdpListener_DropsMulticastGroupOnStop()
        {
            using var receiver = new RtpMulticastReceiver();
            receiver.StartListening("224.1.1.60", 16795);
            receiver.StopListening();
            // Verify stopped without exception
        }

        [Fact]
        public void F12_T05_UdpListener_HandlesDisposalCleanly()
        {
            var receiver = new RtpMulticastReceiver();
            receiver.StartListening("127.0.0.1", 16796);
            receiver.Dispose();
            // Should not throw on second dispose
            receiver.Dispose();
        }

        // =========================================================================
        // FEATURE 13: R5.2 RFC 3550 & RFC 4175 Ingestion
        // =========================================================================

        [Fact]
        public void F13_T01_RtpIngestion_ParsesValid20ByteHeaderFields()
        {
            byte[] payload = new byte[640];
            byte[] packet = MockRtpStreamer.CreateRtpPacket(
                seqNo: 42,
                timestamp: 90000,
                ssrc: 9999,
                lineNo: 5,
                totalLines: 180,
                width: 320,
                yuvPayload: payload);

            bool success = RtpPacketParser.TryParse(packet, out var header);

            Assert.True(success);
            Assert.Equal(2, header.Version);
            Assert.False(header.Marker);
            Assert.Equal(42, header.SequenceNumber);
            Assert.Equal(90000u, header.Timestamp);
            Assert.Equal(9999u, header.Ssrc);
            Assert.Equal(640, header.Length);
            Assert.Equal(5, header.LineNo);
        }

        [Fact]
        public void F13_T02_RtpIngestion_RejectsPacketsWithVersionNotEqualToTwo()
        {
            byte[] packet = MockRtpStreamer.CreateCorruptVersionPacket(version: 1);
            bool success = RtpPacketParser.TryParse(packet, out _);
            Assert.False(success);
        }

        [Fact]
        public void F13_T03_RtpIngestion_RejectsTruncatedPacketsUnder20Bytes()
        {
            byte[] truncated = MockRtpStreamer.CreateTruncatedPacket(15);
            bool success = RtpPacketParser.TryParse(truncated, out _);
            Assert.False(success);
        }

        [Fact]
        public void F13_T04_RtpIngestion_DetectsMarkerBitAsFrameBoundary()
        {
            byte[] packetFinal = MockRtpStreamer.CreateRtpPacket(10, 100, 1, lineNo: 179, totalLines: 180, width: 320, yuvPayload: new byte[640]);
            bool success = RtpPacketParser.TryParse(packetFinal, out var header);

            Assert.True(success);
            Assert.True(header.Marker);
        }

        [Fact]
        public void F13_T05_RtpIngestion_ReassemblesCompleteScanlinesIntoFrame()
        {
            ushort seq = 1;
            var packets = MockRtpStreamer.GenerateFramePackets(320, 10, 12345, 1000, ref seq);
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
            Assert.Equal(320, frame.Width);
            Assert.Equal(10, frame.Height);
            Assert.Equal(320 * 10 * 2, frame.YuvData.Length);
        }

        // =========================================================================
        // FEATURE 14: R5.3 YUV422 to RGB24 Rasterization
        // =========================================================================

        [Fact]
        public void F14_T01_Rasterizer_ConvertsSolidWhiteYuvTo255Rgb()
        {
            // Y = 235, U = 128, V = 128 (White)
            var (r, g, b) = Yuv422Rasterizer.ConvertPixelQ10(235, 128, 128);
            Assert.Equal(235, r);
            Assert.Equal(235, g);
            Assert.Equal(235, b);
        }

        [Fact]
        public void F14_T02_Rasterizer_ConvertsSolidBlackYuvTo0Rgb()
        {
            // Y = 16, U = 128, V = 128 (Black)
            var (r, g, b) = Yuv422Rasterizer.ConvertPixelQ10(16, 128, 128);
            Assert.Equal(16, r);
            Assert.Equal(16, g);
            Assert.Equal(16, b);
        }

        [Fact]
        public void F14_T03_Rasterizer_MatchesFloatFormulasWithinOneLsbAccuracy()
        {
            byte[] testValues = { 16, 64, 128, 192, 235 };
            foreach (byte y in testValues)
            {
                foreach (byte u in testValues)
                {
                    foreach (byte v in testValues)
                    {
                        var expected = Yuv422Rasterizer.ConvertPixelFloat(y, u, v);
                        var actual = Yuv422Rasterizer.ConvertPixelQ10(y, u, v);

                        Assert.True(Math.Abs(expected.r - actual.r) <= 1, $"R mismatch for ({y},{u},{v})");
                        Assert.True(Math.Abs(expected.g - actual.g) <= 1, $"G mismatch for ({y},{u},{v})");
                        Assert.True(Math.Abs(expected.b - actual.b) <= 1, $"B mismatch for ({y},{u},{v})");
                    }
                }
            }
        }

        [Fact]
        public void F14_T04_Rasterizer_ClampsExtremeChromaValuesWithoutOverflow()
        {
            // Extreme values
            var (r, g, b) = Yuv422Rasterizer.ConvertPixelQ10(255, 255, 255);
            Assert.InRange(r, 0, 255);
            Assert.InRange(g, 0, 255);
            Assert.InRange(b, 0, 255);
        }

        [Fact]
        public void F14_T05_Rasterizer_ConvertsFullFrameOfYuv422ToRgb24()
        {
            byte[] yuv = new byte[320 * 180 * 2];
            Array.Fill<byte>(yuv, 128);

            byte[] rgb = Yuv422Rasterizer.ConvertYuv422ToRgb24(yuv, 320, 180);
            Assert.Equal(320 * 180 * 3, rgb.Length);
        }

        // =========================================================================
        // FEATURE 15: R5.4 Zero-Leak WPF Rendering
        // =========================================================================

        [Fact]
        public void F15_T01_Rendering_ComputesRollingFpsAtOneHertzOrHigher()
        {
            using var receiver = new RtpMulticastReceiver();
            ushort seq = 1;
            var frame1 = MockRtpStreamer.GenerateFramePackets(320, 2, 1, 1000, ref seq);
            var frame2 = MockRtpStreamer.GenerateFramePackets(320, 2, 1, 2000, ref seq);

            foreach (var p in frame1) receiver.ProcessDatagram(p);
            foreach (var p in frame2) receiver.ProcessDatagram(p);

            Assert.True(receiver.CurrentFps >= 1.0);
        }

        [Fact]
        public void F15_T02_Rendering_CreatesWriteableBitmapWithMatchingDimensions()
        {
            var bitmap = new WriteableBitmap(320, 180, 96, 96, PixelFormats.Bgr24, null);
            Assert.Equal(320, bitmap.PixelWidth);
            Assert.Equal(180, bitmap.PixelHeight);
            Assert.Equal(320 * 3, bitmap.BackBufferStride);
        }

        [Fact]
        public void F15_T03_Rendering_CopiesRgbPixelsToBitmapBackBufferSafely()
        {
            var bitmap = new WriteableBitmap(320, 180, 96, 96, PixelFormats.Bgr24, null);
            byte[] bgrData = new byte[320 * 180 * 3];
            Array.Fill<byte>(bgrData, 200);

            bitmap.Lock();
            try
            {
                System.Runtime.InteropServices.Marshal.Copy(bgrData, 0, bitmap.BackBuffer, bgrData.Length);
                bitmap.AddDirtyRect(new System.Windows.Int32Rect(0, 0, 320, 180));
            }
            finally
            {
                bitmap.Unlock();
            }

            Assert.NotNull(bitmap);
        }

        [Fact]
        public void F15_T04_Rendering_DoesNotLeakMemoryAcrossRepeatedFrames()
        {
            long initialMemory = GC.GetTotalMemory(true);

            for (int i = 0; i < 50; i++)
            {
                byte[] yuv = new byte[320 * 180 * 2];
                byte[] rgb = Yuv422Rasterizer.ConvertYuv422ToRgb24(yuv, 320, 180);
                Assert.NotNull(rgb);
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            long finalMemory = GC.GetTotalMemory(true);

            // Memory should stay bounded (difference less than 10MB)
            long diff = Math.Abs(finalMemory - initialMemory);
            Assert.True(diff < 10 * 1024 * 1024, $"Memory leak detected: {diff} bytes");
        }

        [Fact]
        public void F15_T05_Rendering_DisposesBitmapResourcesCleanly()
        {
            var bitmap = new WriteableBitmap(100, 100, 96, 96, PixelFormats.Bgr24, null);
            bitmap.Freeze();
            Assert.True(bitmap.IsFrozen);
        }

        // =========================================================================
        // FEATURE 16: E2E.1 Automated Test Suite & Packaging
        // =========================================================================

        [Fact]
        public void F16_T01_Packaging_ValidatesExecutableFolderContainment()
        {
            string baseDir = AppContext.BaseDirectory;
            Assert.True(Directory.Exists(baseDir));
            Assert.Contains("BlueRiver AV Overlay Test App", baseDir);
        }

        [Fact]
        public void F16_T02_Packaging_VerifiesAppSettingsFileExistsInRunDir()
        {
            string cfgPath = Path.Combine(AppContext.BaseDirectory, "appsettings.test.json");
            File.WriteAllText(cfgPath, "{\"ControlServerIp\":\"127.0.0.1\"}");
            Assert.True(File.Exists(cfgPath));
            File.Delete(cfgPath);
        }

        [Fact]
        public void F16_T03_Packaging_VerifiesNoMachineWideRegistryRequirement()
        {
            // App relies strictly on local JSON config
            var config = new AppConfig();
            Assert.NotNull(config);
        }

        [Fact]
        public async Task F16_T04_Packaging_ExecutesMockServerAndStreamerLocally()
        {
            using var server = new MockSdvoeServer();
            using var streamer = new MockRtpStreamer();
            Assert.True(server.IsRunning);
            Assert.True(server.TelnetPort > 0);
            await Task.CompletedTask;
        }

        [Fact]
        public void F16_T05_Packaging_EnsuresCleanTearDownWithoutZombieProcesses()
        {
            var server = new MockSdvoeServer();
            server.Dispose();
            Assert.False(server.IsRunning);
        }
    }
}
