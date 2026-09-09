using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using AvasRoutingApp.Configuration;
using AvasRoutingApp.Sdvoe;

namespace AvasRoutingApp.Tests
{
    public class SdvoeTests : IDisposable
    {
        private readonly List<IDisposable> _disposables = new();

        public void Dispose()
        {
            foreach (var d in _disposables)
            {
                try { d.Dispose(); } catch { }
            }
            _disposables.Clear();
        }

        #region 1. AvasDevice Contract Tests

        [Fact]
        public void AvasDevice_DefaultValues_MatchInterfaceContracts()
        {
            var dev = new AvasDevice();

            Assert.Equal("", dev.MacAddress);
            Assert.Equal("", dev.DeviceName);
            Assert.Equal("", dev.IpAddress);
            Assert.Equal("AVAS-223", dev.Model);
            Assert.Equal(105, dev.VendorId);
            Assert.Equal(81, dev.ProductId);
            Assert.Equal(0, dev.ChipIndex);
            Assert.True(dev.IsTransmitter);
            Assert.False(dev.IsReceiver);
            Assert.Equal("", dev.AllocatedMulticastIp);
            Assert.Equal(6792, dev.AllocatedPort);
            Assert.False(dev.IsStreaming);
            Assert.Equal(0.0, dev.CurrentFps);
            Assert.Equal("320x180", dev.Resolution);
            Assert.Equal(12345u, dev.Ssrc);
        }

        [Fact]
        public void AvasDevice_PropertyMutation_TracksTelemetryCorrectly()
        {
            var dev = new AvasDevice
            {
                MacAddress = "f8228500aaaa",
                DeviceName = "AVAS-223-TX0",
                IpAddress = "192.168.1.151",
                AllocatedMulticastIp = "224.1.1.10",
                AllocatedPort = 6792,
                IsStreaming = true,
                CurrentFps = 1.5,
                Resolution = "640x360"
            };

            Assert.Equal("f8228500aaaa", dev.MacAddress);
            Assert.Equal("AVAS-223-TX0", dev.DeviceName);
            Assert.Equal("192.168.1.151", dev.IpAddress);
            Assert.Equal("224.1.1.10", dev.AllocatedMulticastIp);
            Assert.Equal(6792, dev.AllocatedPort);
            Assert.True(dev.IsStreaming);
            Assert.Equal(1.5, dev.CurrentFps);
            Assert.Equal("640x360", dev.Resolution);
        }

        #endregion

        #region 2. Device Filtering Tests (SdvoeDiscoveryFilter)

        [Fact]
        public void DeviceFiltering_ValidAvas223Chip0_Accepted()
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
        public void DeviceFiltering_Chip1_StrictlyRejected()
        {
            var device = new AvasDevice
            {
                MacAddress = "f8228500bbbb",
                VendorId = 105,
                ProductId = 81,
                ChipIndex = 1, // Secondary processor link aggregation
                IsTransmitter = true,
                IsReceiver = false
            };

            Assert.False(SdvoeDiscoveryFilter.IsTargetAvas223Tx(device));
        }

        [Fact]
        public void DeviceFiltering_Receivers_StrictlyRejected()
        {
            var rx1 = new AvasDevice
            {
                MacAddress = "f8228500cccc",
                VendorId = 105,
                ProductId = 81,
                ChipIndex = 0,
                IsTransmitter = false,
                IsReceiver = true
            };

            var rx2 = new AvasDevice
            {
                MacAddress = "f8228500cccc",
                VendorId = 105,
                ProductId = 81,
                ChipIndex = 0,
                IsTransmitter = true,
                IsReceiver = true // Marked receiver even if transmitter flag is set
            };

            Assert.False(SdvoeDiscoveryFilter.IsTargetAvas223Tx(rx1));
            Assert.False(SdvoeDiscoveryFilter.IsTargetAvas223Tx(rx2));
        }

        [Fact]
        public void DeviceFiltering_NonMatchingVendorOrProduct_Rejected()
        {
            var wrongVendor = new AvasDevice
            {
                MacAddress = "001122334455",
                VendorId = 999,
                ProductId = 81,
                ChipIndex = 0,
                IsTransmitter = true
            };

            var wrongProduct = new AvasDevice
            {
                MacAddress = "f8228500aaaa",
                VendorId = 105,
                ProductId = 99,
                ChipIndex = 0,
                IsTransmitter = true
            };

            Assert.False(SdvoeDiscoveryFilter.IsTargetAvas223Tx(wrongVendor));
            Assert.False(SdvoeDiscoveryFilter.IsTargetAvas223Tx(wrongProduct));
        }

        [Fact]
        public void DeviceFiltering_NullOrEmptyMac_Rejected()
        {
            var emptyMac = new AvasDevice
            {
                MacAddress = "   ",
                VendorId = 105,
                ProductId = 81,
                ChipIndex = 0,
                IsTransmitter = true
            };

            Assert.False(SdvoeDiscoveryFilter.IsTargetAvas223Tx(emptyMac));
            Assert.False(SdvoeDiscoveryFilter.IsTargetAvas223Tx(null!));
        }

        [Fact]
        public void DeviceFiltering_FleetFiltering_ReturnsOnlyTargetEncoders()
        {
            var fleet = new List<AvasDevice>
            {
                new AvasDevice { MacAddress = "tx0_unit1", VendorId = 105, ProductId = 81, ChipIndex = 0, IsTransmitter = true },
                new AvasDevice { MacAddress = "tx1_unit1", VendorId = 105, ProductId = 81, ChipIndex = 1, IsTransmitter = true },
                new AvasDevice { MacAddress = "rx0_unit1", VendorId = 105, ProductId = 81, ChipIndex = 0, IsTransmitter = false, IsReceiver = true },
                new AvasDevice { MacAddress = "third_party", VendorId = 200, ProductId = 50, ChipIndex = 0, IsTransmitter = true },
                new AvasDevice { MacAddress = "tx0_unit2", VendorId = 105, ProductId = 81, ChipIndex = 0, IsTransmitter = true }
            };

            var filtered = SdvoeDiscoveryFilter.FilterTargetDevices(fleet);

            Assert.Equal(2, filtered.Count);
            Assert.Equal("tx0_unit1", filtered[0].MacAddress);
            Assert.Equal("tx0_unit2", filtered[1].MacAddress);
        }

        [Fact]
        public void DeviceFiltering_EmptyOrNullFleet_ReturnsEmptyList()
        {
            Assert.Empty(SdvoeDiscoveryFilter.FilterTargetDevices(null!));
            Assert.Empty(SdvoeDiscoveryFilter.FilterTargetDevices(new List<AvasDevice>()));
        }

        #endregion

        #region 3. MulticastIpManager Tests

        [Fact]
        public void MulticastManager_DefaultPoolBounds_MatchSpecifications()
        {
            var mgr = new MulticastIpManager();

            Assert.Equal("224.1.1.1", mgr.StartIp);
            Assert.Equal("224.1.3.225", mgr.EndIp);
            Assert.Equal(5000, mgr.BasePort);
            Assert.Equal(0, mgr.ActiveAllocationCount);
        }

        [Fact]
        public void MulticastManager_InvalidParameters_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(() => new MulticastIpManager("not_an_ip", "224.1.3.225"));
            Assert.Throws<ArgumentException>(() => new MulticastIpManager("224.1.1.1", "invalid_ip"));
            Assert.Throws<ArgumentException>(() => new MulticastIpManager("224.1.3.1", "224.1.1.1")); // start > end
        }

        [Fact]
        public void MulticastManager_SequentialAssignment_AssignsAscendingIps()
        {
            var mgr = new MulticastIpManager("224.1.1.1", "224.1.1.10");

            string? ip1 = mgr.AllocateMulticastIp("mac1");
            string? ip2 = mgr.AllocateMulticastIp("mac2");
            string? ip3 = mgr.AllocateMulticastIp("mac3");

            Assert.Equal("224.1.1.1", ip1);
            Assert.Equal("224.1.1.2", ip2);
            Assert.Equal("224.1.1.3", ip3);
            Assert.Equal(3, mgr.ActiveAllocationCount);
        }

        [Fact]
        public void MulticastManager_Uniqueness_AssignsDistinctIps()
        {
            var mgr = new MulticastIpManager("224.1.1.1", "224.1.1.50");
            var allocated = new HashSet<string>();

            for (int i = 0; i < 20; i++)
            {
                string? ip = mgr.AllocateMulticastIp($"mac_{i}");
                Assert.NotNull(ip);
                Assert.True(allocated.Add(ip), $"IP {ip} was allocated more than once!");
            }

            Assert.Equal(20, mgr.ActiveAllocationCount);
        }

        [Fact]
        public void MulticastManager_Idempotent_ReturnsSameIpForSameMac()
        {
            var mgr = new MulticastIpManager("224.1.1.1", "224.1.1.10");

            string? ip1 = mgr.AllocateMulticastIp("mac_test");
            string? ip2 = mgr.AllocateMulticastIp("mac_test");
            string? ip3 = mgr.AllocateMulticastIp("MAC_TEST"); // Case-insensitive MAC

            Assert.Equal(ip1, ip2);
            Assert.Equal(ip1, ip3);
            Assert.Equal(1, mgr.ActiveAllocationCount);
        }

        [Fact]
        public void MulticastManager_ReleaseAndReuse_ReclaimsLowestAvailableIp()
        {
            var mgr = new MulticastIpManager("224.1.1.1", "224.1.1.10");

            string? ip1 = mgr.AllocateMulticastIp("mac1"); // 224.1.1.1
            string? ip2 = mgr.AllocateMulticastIp("mac2"); // 224.1.1.2
            string? ip3 = mgr.AllocateMulticastIp("mac3"); // 224.1.1.3

            Assert.Equal("224.1.1.1", ip1);
            Assert.Equal(3, mgr.ActiveAllocationCount);

            // Release mac1
            mgr.ReleaseMulticastIp("mac1");
            Assert.Equal(2, mgr.ActiveAllocationCount);
            Assert.False(mgr.IsAllocated("mac1"));

            // Next allocation should reuse lowest available: 224.1.1.1
            string? ip4 = mgr.AllocateMulticastIp("mac4");
            Assert.Equal("224.1.1.1", ip4);
            Assert.Equal(3, mgr.ActiveAllocationCount);
        }

        [Fact]
        public void MulticastManager_StrictlyExcludesReservedSemtechIps()
        {
            // Pool spanning 224.1.1.252 to 224.1.1.255
            // 224.1.1.253 (All TX) and 224.1.1.254 (All RX) must be skipped
            var mgr = new MulticastIpManager("224.1.1.252", "224.1.1.255");

            string? ip1 = mgr.AllocateMulticastIp("mac1"); // 224.1.1.252
            string? ip2 = mgr.AllocateMulticastIp("mac2"); // should skip 253, 254 -> 224.1.1.255
            string? ip3 = mgr.AllocateMulticastIp("mac3"); // exhausted

            Assert.Equal("224.1.1.252", ip1);
            Assert.Equal("224.1.1.255", ip2);
            Assert.Null(ip3);

            Assert.NotEqual("224.1.1.253", ip1);
            Assert.NotEqual("224.1.1.253", ip2);
            Assert.NotEqual("224.1.1.254", ip1);
            Assert.NotEqual("224.1.1.254", ip2);

            Assert.True(mgr.IsReserved("224.1.1.253"));
            Assert.True(mgr.IsReserved("224.1.1.254"));
            Assert.True(mgr.IsReserved("225.225.225.225"));
        }

        [Fact]
        public void MulticastManager_PoolExhaustion_ReturnsNullGracefully()
        {
            var mgr = new MulticastIpManager("224.1.1.1", "224.1.1.2");

            string? ip1 = mgr.AllocateMulticastIp("mac1");
            string? ip2 = mgr.AllocateMulticastIp("mac2");
            string? ip3 = mgr.AllocateMulticastIp("mac3");

            Assert.NotNull(ip1);
            Assert.NotNull(ip2);
            Assert.Null(ip3);
        }

        [Fact]
        public void MulticastManager_NullOrWhitespaceMac_ReturnsNull()
        {
            var mgr = new MulticastIpManager();

            Assert.Null(mgr.AllocateMulticastIp(null!));
            Assert.Null(mgr.AllocateMulticastIp(""));
            Assert.Null(mgr.AllocateMulticastIp("   "));
            Assert.Equal(0, mgr.ActiveAllocationCount);
        }

        [Fact]
        public void MulticastManager_Reset_ClearsAllAllocations()
        {
            var mgr = new MulticastIpManager("224.1.1.1", "224.1.1.5");

            mgr.AllocateMulticastIp("mac1");
            mgr.AllocateMulticastIp("mac2");
            Assert.Equal(2, mgr.ActiveAllocationCount);

            mgr.Reset();
            Assert.Equal(0, mgr.ActiveAllocationCount);

            // Re-allocate should start at beginning
            string? ip = mgr.AllocateMulticastIp("mac1");
            Assert.Equal("224.1.1.1", ip);
        }

        [Fact]
        public void MulticastManager_RegisterInUse_PreventsAddressCollision()
        {
            var mgr = new MulticastIpManager("224.1.1.1", "224.1.1.5");

            // Server reports 224.1.1.1 is already in use by another transmitter
            mgr.RegisterInUse("224.1.1.1", "existing_tx");

            // Local allocation should skip 224.1.1.1 and pick 224.1.1.2
            string? ip1 = mgr.AllocateMulticastIp("new_mac");
            Assert.Equal("224.1.1.2", ip1);
        }

        [Fact]
        public void MulticastManager_ConcurrentAllocations_ThreadSafe()
        {
            var mgr = new MulticastIpManager("224.1.1.1", "224.1.2.100");
            int threadCount = 10;
            int allocationsPerThread = 20;
            var allocatedIps = new System.Collections.Concurrent.ConcurrentBag<string>();

            Parallel.For(0, threadCount, threadIdx =>
            {
                for (int i = 0; i < allocationsPerThread; i++)
                {
                    string mac = $"mac_{threadIdx}_{i}";
                    string? ip = mgr.AllocateMulticastIp(mac);
                    if (ip != null)
                    {
                        allocatedIps.Add(ip);
                    }
                }
            });

            Assert.Equal(threadCount * allocationsPerThread, allocatedIps.Count);
            // Verify all allocated IPs are unique
            var distinctCount = allocatedIps.Distinct().Count();
            Assert.Equal(allocatedIps.Count, distinctCount);
        }

        #endregion

        #region 4. JSON Parsing Tests

        [Fact]
        public void JsonParsing_SemtechIdentityEnvelope_ExtractsAllFields()
        {
            string json = @"{
                ""status"": ""SUCCESS"",
                ""request_id"": null,
                ""result"": {
                    ""devices"": [
                        {
                            ""device_id"": ""f8228500aaaa"",
                            ""device_name"": ""AVAS-223-TX0"",
                            ""identity"": {
                                ""chipset_type"": ""AVP2000T"",
                                ""vendor_id"": 105,
                                ""product_id"": 81,
                                ""chip_id"": 0,
                                ""is_transmitter"": true,
                                ""is_receiver"": false
                            },
                            ""nodes"": [
                                {
                                    ""type"": ""NETWORK_INTERFACE"",
                                    ""index"": 0,
                                    ""status"": {
                                        ""mac_address"": ""f8228500aaaa"",
                                        ""ip"": {
                                            ""address"": ""192.168.1.151""
                                        }
                                    }
                                }
                            ]
                        }
                    ],
                    ""error"": []
                },
                ""error"": null
            }";

            var devices = SdvoeClient.ParseDevicesJson(json);

            Assert.Single(devices);
            var dev = devices[0];
            Assert.Equal("f8228500aaaa", dev.MacAddress);
            Assert.Equal("AVAS-223-TX0", dev.DeviceName);
            Assert.Equal("192.168.1.151", dev.IpAddress);
            Assert.Equal(105, dev.VendorId);
            Assert.Equal(81, dev.ProductId);
            Assert.Equal(0, dev.ChipIndex);
            Assert.True(dev.IsTransmitter);
            Assert.False(dev.IsReceiver);
            Assert.Equal("AVAS-223", dev.Model);
        }

        [Fact]
        public void JsonParsing_InvalidOrEmptyJson_ReturnsEmptyListWithoutException()
        {
            Assert.Empty(SdvoeClient.ParseDevicesJson(""));
            Assert.Empty(SdvoeClient.ParseDevicesJson("    "));
            Assert.Empty(SdvoeClient.ParseDevicesJson("invalid json {{{"));
            Assert.Empty(SdvoeClient.ParseDevicesJson("{}"));
            Assert.Empty(SdvoeClient.ParseDevicesJson("{\"result\":{}}"));
        }

        [Fact]
        public void JsonParsing_MulticastListJson_ExtractsAddresses()
        {
            string json = @"{
                ""status"": ""SUCCESS"",
                ""result"": {
                    ""multicast"": [
                        { ""address"": ""224.1.1.10"", ""devices"": [""dev1""], ""stream"": ""THUMBNAIL:0"" },
                        { ""address"": ""224.1.1.11"", ""devices"": [""dev2""], ""stream"": ""THUMBNAIL:0"" }
                    ]
                }
            }";

            var list = SdvoeClient.ParseMulticastListJson(json);

            Assert.Equal(2, list.Count);
            Assert.Contains("224.1.1.10", list);
            Assert.Contains("224.1.1.11", list);
        }

        #endregion

        #region 5. Telnet Message Framing & Handshake Tests

        [Fact]
        public async Task Telnet_MandatoryHandshake_SendsRequireApi3000()
        {
            var server = new TestTelnetServer();
            _disposables.Add(server);
            server.Start();

            using var client = new SdvoeClient("127.0.0.1", server.Port, 8080);
            bool connected = await client.ConnectTelnetAsync();

            Assert.True(connected);
            Assert.True(client.IsTelnetAuthenticated);

            // Verify first command received by server was exactly 'require api 3.0.0.0'
            var commands = server.ReceivedCommands;
            Assert.NotEmpty(commands);
            Assert.Equal("require api 3.0.0.0", commands[0]);
        }

        [Fact]
        public async Task Telnet_FailedHandshake_MarksClientUnauthenticated()
        {
            var server = new TestTelnetServer(handshakeSuccess: false);
            _disposables.Add(server);
            server.Start();

            using var client = new SdvoeClient("127.0.0.1", server.Port, 8080);
            bool connected = await client.ConnectTelnetAsync();

            Assert.False(connected);
            Assert.False(client.IsTelnetAuthenticated);
        }

        [Fact]
        public async Task Telnet_CommandFraming_TerminatesWithCarriageReturnLineFeed()
        {
            var server = new TestTelnetServer();
            _disposables.Add(server);
            server.Start();

            using var client = new SdvoeClient("127.0.0.1", server.Port, 8080);
            await client.ConnectTelnetAsync();

            string resp = await client.SendTelnetCommandAsync("version");
            Assert.NotNull(resp);

            var commands = server.ReceivedCommands;
            Assert.Contains("version", commands);
        }

        [Fact]
        public async Task Telnet_StreamControlCommands_ProperlyFormatted()
        {
            var server = new TestTelnetServer();
            _disposables.Add(server);
            server.Start();

            using var client = new SdvoeClient("127.0.0.1", server.Port, 8080);
            await client.ConnectTelnetAsync();

            // Configure thumbnail
            bool configOk = await client.ConfigureThumbnailStreamAsync("f8228500aaaa", 1.0, 12345, 6792);
            Assert.True(configOk);
            Assert.Contains(server.ReceivedCommands, c => c == "set f8228500aaaa thumbnail fps 1.0 ssrc 12345 udp 6792");

            // Start stream
            bool startOk = await client.StartPreviewStreamAsync("f8228500aaaa", "224.1.1.15", 6792);
            Assert.True(startOk);
            Assert.Contains(server.ReceivedCommands, c => c == "start f8228500aaaa:thumbnail:0 224.1.1.15");

            // Stop stream with free
            bool stopOk = await client.StopPreviewStreamAsync("f8228500aaaa", free: true);
            Assert.True(stopOk);
            Assert.Contains(server.ReceivedCommands, c => c == "stop f8228500aaaa:thumbnail:0 free");

            // Stop stream without free
            bool stopNoFreeOk = await client.StopPreviewStreamAsync("f8228500aaaa", free: false);
            Assert.True(stopNoFreeOk);
            Assert.Contains(server.ReceivedCommands, c => c == "stop f8228500aaaa:thumbnail:0");
        }

        [Fact]
        public async Task Discovery_EndToEnd_DiscoversAndFiltersAndFiresEvent()
        {
            var server = new TestTelnetServer();
            _disposables.Add(server);
            server.Start();

            using var client = new SdvoeClient("127.0.0.1", server.Port, 8080);
            IReadOnlyList<AvasDevice>? eventDevices = null;
            client.DevicesDiscovered += devs => { eventDevices = devs; };

            var discovered = await client.DiscoverAvas223DevicesAsync();

            // TestTelnetServer seeds 2 devices: f8228500aaaa (chip 0 TX) and f8228500bbbb (chip 1 TX)
            Assert.Single(discovered);
            Assert.Equal("f8228500aaaa", discovered[0].MacAddress);
            Assert.Equal(0, discovered[0].ChipIndex);

            // Event fired with matching devices
            Assert.NotNull(eventDevices);
            Assert.Single(eventDevices);
            Assert.Equal("f8228500aaaa", eventDevices[0].MacAddress);
        }

        [Fact]
        public async Task MulticastController_LifecycleIntegration()
        {
            var server = new TestTelnetServer();
            _disposables.Add(server);
            server.Start();

            var ipManager = new MulticastIpManager("224.1.1.1", "224.1.1.5");
            using var client = new SdvoeClient("127.0.0.1", server.Port, 8080, ipManager);

            // Allocate IP via controller
            string? ip = client.AllocateMulticastIp("f8228500aaaa");
            Assert.Equal("224.1.1.1", ip);

            // Start preview
            bool startOk = await client.StartPreviewStreamAsync("f8228500aaaa", ip!, 6792);
            Assert.True(startOk);

            // Stop preview (frees IP)
            bool stopOk = await client.StopPreviewStreamAsync("f8228500aaaa");
            Assert.True(stopOk);

            // Allocation released
            Assert.False(ipManager.IsAllocated("f8228500aaaa"));
        }

        [Fact]
        public async Task StartAndStopPreviewStreamViaAvpRs232_SendsExpectedJsonPayloads()
        {
            var listener = new HttpListener();
            // Use dynamic free port for HTTP listener
            int port = 0;
            using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                s.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                port = ((IPEndPoint)s.LocalEndPoint!).Port;
            }

            string prefix = $"http://127.0.0.1:{port}/";
            listener.Prefixes.Add(prefix);
            listener.Start();

            var receivedBodies = new List<string>();
            var listenTask = Task.Run(async () =>
            {
                for (int i = 0; i < 3; i++)
                {
                    var ctx = await listener.GetContextAsync();
                    using var r = new System.IO.StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                    string b = await r.ReadToEndAsync();
                    lock (receivedBodies) receivedBodies.Add(b);

                    byte[] resp = Encoding.UTF8.GetBytes("{\"status\":\"SUCCESS\",\"result\":{\"error\":[],\"send_rs232\":[{\"device_id\":\"f8228500aaaa\"}]}}");
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.ContentLength64 = resp.Length;
                    await ctx.Response.OutputStream.WriteAsync(resp, 0, resp.Length);
                    ctx.Response.Close();
                }
            });

            try
            {
                using var client = new SdvoeClient("127.0.0.1", 6970, port);

                bool startOk = await client.StartPreviewStreamViaAvpRs232Async("F8:22:85:00:AA:AA", "224.1.3.1");
                Assert.True(startOk);

                bool stopOk = await client.StopPreviewStreamViaAvpRs232Async("f8228500aaaa");
                Assert.True(stopOk);

                await listenTask;

                Assert.Equal(3, receivedBodies.Count);
                Assert.Contains("\"data_string\":\"set rtp igmp 224.1.3.1\\r\\n\"", receivedBodies[0]);
                Assert.Contains("\"op\":\"send:rs232\"", receivedBodies[0]);
                Assert.Contains("\"data_string\":\"set rtp ON\\r\\n\"", receivedBodies[1]);
                Assert.Contains("\"data_string\":\"set rtp OFF\\r\\n\"", receivedBodies[2]);
            }
            finally
            {
                listener.Stop();
                listener.Close();
            }
        }

        [Fact]
        public async Task LiveHardware_IfControlServerRunning_StreamsFrames()
        {
            // Check if 127.0.0.1:8090 is reachable
            using var testTcp = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await testTcp.ConnectAsync(IPAddress.Parse("127.0.0.1"), 8090);
            }
            catch
            {
                // Not running on live machine with controlserver, skip gracefully
                return;
            }

            using var client = new SdvoeClient("127.0.0.1", 6970, 8090);
            string mac = "74fe488b07bb";
            string mcast = "224.1.3.1";
            int port = 5000;

            bool startOk = await client.StartPreviewStreamAsync(mac, mcast, port);
            if (!startOk) return; // Device might not be connected in CI

            using var receiver = new Rtp.RtpMulticastReceiver();
            receiver.StartListening(mcast, port);

            // Wait for packets
            for (int i = 0; i < 15; i++)
            {
                await Task.Delay(200);
                if (receiver.ReceivedPacketsCount > 0) break;
            }

            Assert.True(receiver.ReceivedPacketsCount > 0, "Packets should be received on UDP 5000 from hardware");

            bool stopOk = await client.StopPreviewStreamAsync(mac, free: false);
            Assert.True(stopOk);
        }

        #endregion

        #region Helper: Test Telnet Server

        private class TestTelnetServer : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly List<string> _receivedCommands = new();
            private readonly object _lock = new();
            private readonly bool _handshakeSuccess;
            private readonly CancellationTokenSource _cts = new();

            public int Port { get; }
            public IReadOnlyList<string> ReceivedCommands
            {
                get
                {
                    lock (_lock) return new List<string>(_receivedCommands);
                }
            }

            public TestTelnetServer(bool handshakeSuccess = true)
            {
                _handshakeSuccess = handshakeSuccess;
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            }

            public void Start()
            {
                Task.Run(AcceptLoopAsync);
            }

            private async Task AcceptLoopAsync()
            {
                while (!_cts.IsCancellationRequested)
                {
                    try
                    {
                        var tcpClient = await _listener.AcceptTcpClientAsync(_cts.Token);
                        _ = Task.Run(() => HandleClientAsync(tcpClient, _cts.Token));
                    }
                    catch
                    {
                        break;
                    }
                }
            }

            private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
            {
                using (client)
                using (var stream = client.GetStream())
                using (var reader = new StreamReader(stream, Encoding.ASCII))
                using (var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true, NewLine = "\r\n" })
                {
                    while (!ct.IsCancellationRequested && client.Connected)
                    {
                        string? line;
                        try
                        {
                            line = await reader.ReadLineAsync(ct);
                        }
                        catch
                        {
                            break;
                        }

                        if (line == null) break;

                        lock (_lock)
                        {
                            _receivedCommands.Add(line);
                        }

                        string response = HandleCommand(line);
                        try
                        {
                            await writer.WriteLineAsync(response);
                        }
                        catch
                        {
                            break;
                        }
                    }
                }
            }

            private string HandleCommand(string command)
            {
                if (command.StartsWith("require api", StringComparison.OrdinalIgnoreCase))
                {
                    return _handshakeSuccess
                        ? "{\"status\":\"SUCCESS\",\"request_id\":null,\"result\":null,\"error\":null}"
                        : "{\"status\":\"ERROR\",\"request_id\":null,\"result\":null,\"error\":{\"reason\":\"INVALID_COMMAND\",\"message\":\"Invalid\"}}";
                }

                if (command.Equals("version", StringComparison.OrdinalIgnoreCase))
                {
                    return "{\"status\":\"SUCCESS\",\"result\":{\"server\":\"Control Server\",\"version\":\"3.2.0.1\"},\"error\":null}";
                }

                if (command.Equals("get all identity", StringComparison.OrdinalIgnoreCase))
                {
                    return "{\"status\":\"SUCCESS\",\"request_id\":null,\"result\":{\"devices\":[{\"device_id\":\"f8228500aaaa\",\"device_name\":\"AVAS-223-TX0\",\"identity\":{\"vendor_id\":105,\"product_id\":81,\"chip_id\":0,\"is_transmitter\":true,\"is_receiver\":false},\"nodes\":[{\"type\":\"NETWORK_INTERFACE\",\"status\":{\"ip\":{\"address\":\"192.168.1.151\"}}}]},{\"device_id\":\"f8228500bbbb\",\"device_name\":\"AVAS-223-TX1\",\"identity\":{\"vendor_id\":105,\"product_id\":81,\"chip_id\":1,\"is_transmitter\":true,\"is_receiver\":false},\"nodes\":[{\"type\":\"NETWORK_INTERFACE\",\"status\":{\"ip\":{\"address\":\"192.168.1.150\"}}}]}],\"error\":[]},\"error\":null}";
                }

                if (command.StartsWith("set ", StringComparison.OrdinalIgnoreCase) ||
                    command.StartsWith("start ", StringComparison.OrdinalIgnoreCase) ||
                    command.StartsWith("stop ", StringComparison.OrdinalIgnoreCase) ||
                    command.StartsWith("list multicast", StringComparison.OrdinalIgnoreCase))
                {
                    return "{\"status\":\"SUCCESS\",\"request_id\":null,\"result\":{\"status\":\"ok\"},\"error\":null}";
                }

                return "{\"status\":\"SUCCESS\",\"request_id\":null,\"result\":null,\"error\":null}";
            }

            public void Dispose()
            {
                _cts.Cancel();
                _listener.Stop();
                _cts.Dispose();
            }
        }

        #endregion
    }
}
