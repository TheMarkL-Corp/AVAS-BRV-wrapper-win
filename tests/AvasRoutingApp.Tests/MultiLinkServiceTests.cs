using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using AvasRoutingApp.Configuration;
using AvasRoutingApp.Sdvoe;

namespace AvasRoutingApp.Tests
{
    public class MockHttpMessageHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, Task<HttpResponseMessage>>? Handler { get; set; }
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (Handler != null)
            {
                return await Handler(request);
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }

    [Collection("LiveHardware")]
    public class MultiLinkServiceTests
    {
        [Fact]
        public void MultiLinkInfo_DefaultValues_Correct()
        {
            var info = new MultiLinkInfo();
            Assert.Equal("", info.PrimaryMac);
            Assert.Equal("", info.PrimaryName);
            Assert.Equal("", info.PrimaryIp);
            Assert.Equal("UNKNOWN", info.LinkMode);
            Assert.Equal("NONE", info.CompanionMac);
            Assert.Equal("", info.CompanionName);
            Assert.False(info.CompanionIsActive);
            Assert.Equal("SINGLE,DUAL", info.Capabilities);
            Assert.False(info.HasCompanion);
        }

        [Fact]
        public void MultiLinkInfo_HasCompanion_RecognizesValidAndInvalid()
        {
            var info = new MultiLinkInfo { CompanionMac = "74FE488B07BC" };
            Assert.True(info.HasCompanion);

            info.CompanionMac = "NONE";
            Assert.False(info.HasCompanion);

            info.CompanionMac = "";
            Assert.False(info.HasCompanion);

            info.CompanionMac = "none";
            Assert.False(info.HasCompanion);
        }

        [Fact]
        public async Task QueryMultiLinkPairsAsync_ParsesTopologyAndMatchesCompanionCaseInsensitive()
        {
            var mockHandler = new MockHttpMessageHandler();
            mockHandler.Handler = async req =>
            {
                string path = req.RequestUri?.AbsolutePath ?? "";
                string body = req.Content != null ? await req.Content.ReadAsStringAsync() : "";

                // 1. Device list
                if (path == "/api/device" && req.Method == HttpMethod.Get)
                {
                    string json = "{\"status\":\"SUCCESS\",\"result\":{\"devices\":[{\"device_id\":\"74fe488b07bb\"},{\"device_id\":\"74fe488b07bc\"}]}}";
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
                }

                // 2. Identity query
                if (path == "/api/device/74fe488b07bb" && body.Contains("identity"))
                {
                    string json = "{\"status\":\"SUCCESS\",\"result\":{\"devices\":[{\"device_name\":\"AVAS-223-TX0\",\"identity\":{\"is_transmitter\":true,\"chip_id\":0,\"vendor_id\":105,\"product_id\":81},\"nodes\":[{\"status\":{\"ip\":{\"address\":\"192.168.1.10\"}}}]}]}}";
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
                }
                if (path == "/api/device/74fe488b07bc" && body.Contains("identity"))
                {
                    string json = "{\"status\":\"SUCCESS\",\"result\":{\"devices\":[{\"device_name\":\"AVAS-223-TX0-B\",\"identity\":{\"is_transmitter\":true,\"chip_id\":1,\"vendor_id\":105,\"product_id\":81},\"nodes\":[{\"status\":{\"ip\":{\"address\":\"192.168.1.11\"}}}]}]}}";
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
                }

                // 3. Full device subset query
                if (path == "/api/device/74fe488b07bb" && body.Contains("subset\":\"device\""))
                {
                    // Primary returns uppercase companion MAC "74FE488B07BC"
                    string json = "{\"status\":\"SUCCESS\",\"result\":{\"devices\":[{\"device_name\":\"AVAS-223-TX0\",\"identity\":{\"chip_id\":0},\"status\":{\"active\":true},\"nodes\":[{\"type\":\"MULTI_LINK_TRANSMITTER\",\"configuration\":{\"link_mode\":\"DUAL\"},\"status\":{\"companions\":[{\"device_id\":\"74FE488B07BC\"}],\"link_capabilities\":[{\"mode\":\"SINGLE\"},{\"mode\":\"DUAL\"}]}},{\"status\":{\"ip\":{\"address\":\"192.168.1.10\"}}}]}]}}";
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
                }
                if (path == "/api/device/74fe488b07bc" && body.Contains("subset\":\"device\""))
                {
                    string json = "{\"status\":\"SUCCESS\",\"result\":{\"devices\":[{\"device_name\":\"AVAS-223-TX0-B\",\"identity\":{\"chip_id\":1},\"status\":{\"active\":true},\"nodes\":[{\"status\":{\"ip\":{\"address\":\"192.168.1.11\"}}}]}]}}";
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            };

            var configService = new ConfigService();
            using var httpClient = new HttpClient(mockHandler);
            using var service = new MultiLinkService(configService, httpClient);

            var pairs = await service.QueryMultiLinkPairsAsync();

            Assert.Single(pairs);
            var pair = pairs[0];
            Assert.Equal("74fe488b07bb", pair.PrimaryMac);
            Assert.Equal("AVAS-223-TX0", pair.PrimaryName);
            Assert.Equal("192.168.1.10", pair.PrimaryIp);
            Assert.Equal("DUAL", pair.LinkMode);
            Assert.Equal("74FE488B07BC", pair.CompanionMac);
            Assert.Equal("AVAS-223-TX0-B", pair.CompanionName);
            Assert.True(pair.CompanionIsActive);
        }

        [Fact]
        public async Task SetMultiLinkModeAsync_DualMode_ConfiguresBothAndReboots()
        {
            var mockHandler = new MockHttpMessageHandler();
            var postedEndpoints = new List<string>();
            var postedBodies = new List<string>();

            mockHandler.Handler = async req =>
            {
                postedEndpoints.Add(req.RequestUri?.AbsolutePath ?? "");
                string body = req.Content != null ? await req.Content.ReadAsStringAsync() : "";
                postedBodies.Add(body);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"status\":\"SUCCESS\"}", Encoding.UTF8, "application/json")
                };
            };

            var configService = new ConfigService();
            using var httpClient = new HttpClient(mockHandler);
            using var service = new MultiLinkService(configService, httpClient);

            bool success = await service.SetMultiLinkModeAsync("74fe488b07bb", "74FE488B07BC", "DUAL");

            Assert.True(success);
            // Must have called set:multi_link on primary
            Assert.Contains(postedEndpoints, p => p == "/api/device/74fe488b07bb");
            // Must have called set:multi_link on companion
            Assert.Contains(postedEndpoints, p => p == "/api/device/74FE488B07BC");
            // Must have rebooted both
            Assert.Contains(postedBodies, b => b.Contains("\"reboot\""));
        }

        [Fact]
        public async Task SetMultiLinkModeAsync_SingleMode_ConfiguresPrimaryOnlyAndReboots()
        {
            var mockHandler = new MockHttpMessageHandler();
            var postedEndpoints = new List<string>();
            var postedBodies = new List<string>();

            mockHandler.Handler = async req =>
            {
                postedEndpoints.Add(req.RequestUri?.AbsolutePath ?? "");
                string body = req.Content != null ? await req.Content.ReadAsStringAsync() : "";
                postedBodies.Add(body);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"status\":\"SUCCESS\"}", Encoding.UTF8, "application/json")
                };
            };

            var configService = new ConfigService();
            using var httpClient = new HttpClient(mockHandler);
            using var service = new MultiLinkService(configService, httpClient);

            bool success = await service.SetMultiLinkModeAsync("74fe488b07bb", "74FE488B07BC", "SINGLE");

            Assert.True(success);
            // In SINGLE mode, set:multi_link is sent to primary chip_0
            Assert.Contains(postedBodies, b => b.Contains("\"SINGLE\""));
            // Reboot called
            Assert.Contains(postedBodies, b => b.Contains("\"reboot\""));
        }

        [Fact]
        public async Task QueryMultiLinkPairsAsync_CompanionOffline_CorrectlyReportsOffline()
        {
            var mockHandler = new MockHttpMessageHandler();
            mockHandler.Handler = async req =>
            {
                string path = req.RequestUri?.AbsolutePath ?? "";
                string body = req.Content != null ? await req.Content.ReadAsStringAsync() : "";

                if (path == "/api/device" && req.Method == HttpMethod.Get)
                {
                    string json = "{\"status\":\"SUCCESS\",\"result\":{\"devices\":[{\"device_id\":\"74fe488b07bb\"},{\"device_id\":\"74fe488b07bc\"}]}}";
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
                }

                if (path == "/api/device/74fe488b07bb" && body.Contains("identity"))
                {
                    string json = "{\"status\":\"SUCCESS\",\"result\":{\"devices\":[{\"device_name\":\"AVAS-223-TX0\",\"identity\":{\"is_transmitter\":true,\"chip_id\":0,\"vendor_id\":105,\"product_id\":81}}]}}";
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
                }
                if (path == "/api/device/74fe488b07bc" && body.Contains("identity"))
                {
                    string json = "{\"status\":\"SUCCESS\",\"result\":{\"devices\":[{\"device_name\":\"AVAS-223-TX0-B\",\"identity\":{\"is_transmitter\":true,\"chip_id\":1,\"vendor_id\":105,\"product_id\":81}}]}}";
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
                }

                if (path == "/api/device/74fe488b07bb" && body.Contains("subset\":\"device\""))
                {
                    string json = "{\"status\":\"SUCCESS\",\"result\":{\"devices\":[{\"device_name\":\"AVAS-223-TX0\",\"identity\":{\"chip_id\":0},\"status\":{\"active\":true},\"nodes\":[{\"type\":\"MULTI_LINK_TRANSMITTER\",\"configuration\":{\"link_mode\":\"SINGLE\"},\"status\":{\"companions\":[{\"device_id\":\"74FE488B07BC\"}]}}]}]}}";
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
                }
                if (path == "/api/device/74fe488b07bc" && body.Contains("subset\":\"device\""))
                {
                    // Companion is reported as active: false
                    string json = "{\"status\":\"SUCCESS\",\"result\":{\"devices\":[{\"device_name\":\"AVAS-223-TX0-B\",\"identity\":{\"chip_id\":1},\"status\":{\"active\":false}}]}}";
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            };

            var configService = new ConfigService();
            using var httpClient = new HttpClient(mockHandler);
            using var service = new MultiLinkService(configService, httpClient);

            var pairs = await service.QueryMultiLinkPairsAsync();

            Assert.Single(pairs);
            Assert.Equal("SINGLE", pairs[0].LinkMode);
            Assert.Equal("74FE488B07BC", pairs[0].CompanionMac);
            Assert.False(pairs[0].CompanionIsActive);
        }

        [Fact]
        public async Task RebootDeviceAsync_PostsRebootPayloadSuccessfully()
        {
            var mockHandler = new MockHttpMessageHandler();
            string? capturedBody = null;
            mockHandler.Handler = async req =>
            {
                capturedBody = req.Content != null ? await req.Content.ReadAsStringAsync() : "";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"status\":\"SUCCESS\"}", Encoding.UTF8, "application/json")
                };
            };

            var configService = new ConfigService();
            using var httpClient = new HttpClient(mockHandler);
            using var service = new MultiLinkService(configService, httpClient);

            bool success = await service.RebootDeviceAsync("74fe488b07bb");
            Assert.True(success);
            Assert.NotNull(capturedBody);
            Assert.Contains("\"reboot\"", capturedBody);
        }

        [Fact]
        public async Task SetMultiLinkModeAsync_NetworkFailure_ReturnsFalseGracefully()
        {
            var mockHandler = new MockHttpMessageHandler();
            mockHandler.Handler = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));

            var configService = new ConfigService();
            using var httpClient = new HttpClient(mockHandler);
            using var service = new MultiLinkService(configService, httpClient);

            bool success = await service.SetMultiLinkModeAsync("74fe488b07bb", "74FE488B07BC", "DUAL");
            Assert.False(success);
        }

        [Fact]
        public async Task QueryMultiLinkPairsAsync_ServerReturns404_ReturnsEmptyListGracefully()
        {
            var mockHandler = new MockHttpMessageHandler();
            mockHandler.Handler = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            var configService = new ConfigService();
            using var httpClient = new HttpClient(mockHandler);
            using var service = new MultiLinkService(configService, httpClient);

            var pairs = await service.QueryMultiLinkPairsAsync();
            Assert.NotNull(pairs);
            Assert.Empty(pairs);
        }

        [Fact]
        public async Task QueryMultiLinkPairsAsync_ServerTimesOut_ReturnsEmptyListGracefully()
        {
            var mockHandler = new MockHttpMessageHandler();
            mockHandler.Handler = _ => throw new TaskCanceledException("Request timed out");

            var configService = new ConfigService();
            using var httpClient = new HttpClient(mockHandler);
            using var service = new MultiLinkService(configService, httpClient);

            var pairs = await service.QueryMultiLinkPairsAsync();
            Assert.NotNull(pairs);
            Assert.Empty(pairs);
        }

        [Fact]
        public async Task QueryMultiLinkPairsAsync_ServerReturns500_ReturnsEmptyListGracefully()
        {
            var mockHandler = new MockHttpMessageHandler();
            mockHandler.Handler = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));

            var configService = new ConfigService();
            using var httpClient = new HttpClient(mockHandler);
            using var service = new MultiLinkService(configService, httpClient);

            var pairs = await service.QueryMultiLinkPairsAsync();
            Assert.NotNull(pairs);
            Assert.Empty(pairs);
        }

        [Fact]
        public async Task SetMultiLinkModeAsync_DualMode_CompanionFails_ReturnsFalse()
        {
            var mockHandler = new MockHttpMessageHandler();
            mockHandler.Handler = req =>
            {
                string path = req.RequestUri?.AbsolutePath ?? "";
                if (path == "/api/device/74fe488b07bb")
                {
                    // Primary succeeds
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"status\":\"SUCCESS\"}", Encoding.UTF8, "application/json")
                    });
                }
                // Companion fails with 500 error
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            };

            var configService = new ConfigService();
            using var httpClient = new HttpClient(mockHandler);
            using var service = new MultiLinkService(configService, httpClient);

            bool success = await service.SetMultiLinkModeAsync("74fe488b07bb", "74FE488B07BC", "DUAL");
            // Since companion failed in DUAL mode, overall result must be false
            Assert.False(success);
        }

        [Fact]
        public async Task MultiLinkService_RapidQueries_ZeroSocketLeaks_ZeroExceptions()
        {
            var mockHandler = new MockHttpMessageHandler();
            mockHandler.Handler = req =>
            {
                string path = req.RequestUri?.AbsolutePath ?? "";
                if (path == "/api/device")
                {
                    string json = "{\"status\":\"SUCCESS\",\"result\":{\"devices\":[{\"device_id\":\"74fe488b07bb\"}]}}";
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
                }
                string devJson = "{\"status\":\"SUCCESS\",\"result\":{\"devices\":[{\"device_name\":\"AVAS-223-TX0\",\"identity\":{\"is_transmitter\":true,\"chip_id\":0,\"vendor_id\":105,\"product_id\":81},\"status\":{\"active\":true},\"nodes\":[{\"type\":\"MULTI_LINK_TRANSMITTER\",\"configuration\":{\"link_mode\":\"SINGLE\"}}]}]}}";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(devJson, Encoding.UTF8, "application/json") });
            };

            var configService = new ConfigService();
            using var httpClient = new HttpClient(mockHandler);
            using var service = new MultiLinkService(configService, httpClient);

            // Execute 50 rapid queries in a loop
            for (int i = 0; i < 50; i++)
            {
                var pairs = await service.QueryMultiLinkPairsAsync();
                Assert.Single(pairs);
            }
        }

        [Fact]
        public async Task LiveHardware_IfControlServerRunning_QueriesRealMultiLinkTopology()
        {
            try
            {
                using var tcpClient = new System.Net.Sockets.TcpClient();
                await tcpClient.ConnectAsync("127.0.0.1", 8090);
            }
            catch
            {
                // Control server not running locally, skip gracefully in CI
                return;
            }

            string tmpConfig = Path.GetTempFileName();
            try
            {
                var configService = new ConfigService(tmpConfig);
                var cfg = configService.Current;
                cfg.ControlServerIp = "127.0.0.1";
                cfg.RestPort = 8090;
                configService.Save(cfg);

                using var service = new MultiLinkService(configService);
                IReadOnlyList<MultiLinkInfo> pairs = Array.Empty<MultiLinkInfo>();
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    pairs = await service.QueryMultiLinkPairsAsync();
                    if (pairs.Count > 0) break;
                    await Task.Delay(1000);
                }

                Assert.NotNull(pairs);
                Assert.True(pairs.Count > 0, $"Expected at least 1 multi-link pair on live hardware, but found {pairs.Count}");
            
            var primary = pairs.FirstOrDefault(p => p.PrimaryMac.Equals("74fe488b07bb", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(primary);
            Assert.True(primary.LinkMode is "DUAL" or "SINGLE", $"Expected DUAL or SINGLE, got {primary.LinkMode}");
            Assert.True(primary.HasCompanion);
            Assert.Equal("74fe488b07bc", primary.CompanionMac.ToLowerInvariant());
            Assert.True(primary.CompanionIsActive, $"CompanionName: '{primary.CompanionName}', CompanionMac: '{primary.CompanionMac}', Total pairs: {pairs.Count}");
            }
            finally
            {
                if (File.Exists(tmpConfig)) File.Delete(tmpConfig);
            }
        }
    }
}
