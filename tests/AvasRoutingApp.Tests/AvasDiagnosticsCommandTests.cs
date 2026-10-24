using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AvasRoutingApp.Sdvoe;
using Xunit;

namespace AvasRoutingApp.Tests
{
    public class AvasDiagnosticsCommandTests
    {
        [Fact]
        public async Task FactoryResetAvpAsync_WhenDeviceIsNotAvas_ThrowsInvalidOperationException()
        {
            using var client = new SdvoeClient();
            var nonAvasDevice = new AvasDevice
            {
                MacAddress = "00:11:22:33:44:55",
                VendorId = 999,
                ProductId = 11
            };

            Assert.Equal(DeviceFamily.GenericSdvoe, nonAvasDevice.Family);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                client.FactoryResetAvpAsync(nonAvasDevice, nonAvasDevice.MacAddress));
        }

        [Fact]
        public async Task FactoryResetMcuAsync_WhenDeviceIsNotAvas_ThrowsInvalidOperationException()
        {
            using var client = new SdvoeClient();
            var nonAvasDevice = new AvasDevice
            {
                MacAddress = "00:11:22:33:44:55",
                VendorId = 999,
                ProductId = 11
            };

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                client.FactoryResetMcuAsync(nonAvasDevice));
        }

        [Fact]
        public async Task FactoryResetAvpAsync_WhenDeviceIsAvas_SendsFactoryCommandToTargetMac()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            var receivedCommands = new StringBuilder();
            var serverTask = Task.Run(async () =>
            {
                try
                {
                    using var client = await listener.AcceptTcpClientAsync();
                    using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII);
                    using var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true, NewLine = "\r\n" };

                    string? line;
                    while ((line = await reader.ReadLineAsync()) != null)
                    {
                        receivedCommands.AppendLine(line);
                        await writer.WriteLineAsync("{\"status\":\"SUCCESS\"}");
                    }
                }
                catch (IOException) { }
                catch (SocketException) { }
            });

            try
            {
                using var sdvoeClient = new SdvoeClient("127.0.0.1", port);

                var avasDevice = new AvasDevice
                {
                    MacAddress = "00:14:95:AA:BB:CC",
                    CompanionMac = "00:14:95:AA:BB:CD",
                    VendorId = 105,
                    ProductId = 3
                };

                bool ok = await sdvoeClient.FactoryResetAvpAsync(avasDevice, "00:14:95:AA:BB:CD");
                Assert.True(ok);

                sdvoeClient.DisconnectTelnet();
                await serverTask;

                string log = receivedCommands.ToString();
                Assert.Contains("factory 001495aabbcd", log);
            }
            finally
            {
                listener.Stop();
            }
        }

        [Fact]
        public async Task FactoryResetMcuAsync_WhenDeviceIsAvas_SendsUartCommandViaChip0Mac()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            var receivedCommands = new StringBuilder();
            var serverTask = Task.Run(async () =>
            {
                try
                {
                    using var client = await listener.AcceptTcpClientAsync();
                    using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII);
                    using var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true, NewLine = "\r\n" };

                    string? line;
                    while ((line = await reader.ReadLineAsync()) != null)
                    {
                        receivedCommands.AppendLine(line);
                        await writer.WriteLineAsync("{\"status\":\"SUCCESS\"}");
                    }
                }
                catch (IOException) { }
                catch (SocketException) { }
            });

            try
            {
                using var sdvoeClient = new SdvoeClient("127.0.0.1", port);

                var avasDevice = new AvasDevice
                {
                    MacAddress = "00:14:95:AA:BB:CC",
                    CompanionMac = "00:14:95:AA:BB:CD",
                    VendorId = 105,
                    ProductId = 3
                };

                bool ok = await sdvoeClient.FactoryResetMcuAsync(avasDevice);
                Assert.True(ok);

                sdvoeClient.DisconnectTelnet();
                await serverTask;

                string log = receivedCommands.ToString();
                // Dual Link SDVoE Tester reverse-engineering showed: send <chip0Mac> rs232:1 set factory default\r
                Assert.Contains("send 001495aabbcc rs232:1 set factory default", log);
            }
            finally
            {
                listener.Stop();
            }
        }

        [Fact]
        public async Task RebootChipAsync_WhenDeviceIsAvas_SendsRebootCommand()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            var receivedCommands = new StringBuilder();
            var serverTask = Task.Run(async () =>
            {
                try
                {
                    using var client = await listener.AcceptTcpClientAsync();
                    using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII);
                    using var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true, NewLine = "\r\n" };

                    string? line;
                    while ((line = await reader.ReadLineAsync()) != null)
                    {
                        receivedCommands.AppendLine(line);
                        await writer.WriteLineAsync("{\"status\":\"SUCCESS\"}");
                    }
                }
                catch (IOException) { }
                catch (SocketException) { }
            });

            try
            {
                using var sdvoeClient = new SdvoeClient("127.0.0.1", port);

                var avasDevice = new AvasDevice
                {
                    MacAddress = "00:14:95:AA:BB:CC",
                    CompanionMac = "00:14:95:AA:BB:CD",
                    VendorId = 105,
                    ProductId = 3
                };

                bool ok = await sdvoeClient.RebootChipAsync(avasDevice, "00:14:95:AA:BB:CC");
                Assert.True(ok);

                sdvoeClient.DisconnectTelnet();
                await serverTask;

                string log = receivedCommands.ToString();
                Assert.Contains("reboot 001495aabbcc", log);
            }
            finally
            {
                listener.Stop();
            }
        }
    [Fact]
        public async Task RebootMcuAsync_WhenDeviceIsNotAvas_ThrowsInvalidOperationException()
        {
            using var client = new SdvoeClient();
            var nonAvasDevice = new AvasDevice
            {
                MacAddress = "00:11:22:33:44:55",
                VendorId = 999,
                ProductId = 11
            };

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                client.RebootMcuAsync(nonAvasDevice));
        }

        [Fact]
        public async Task RebootChipAsync_WhenDeviceIsNotAvas_ThrowsInvalidOperationException()
        {
            using var client = new SdvoeClient();
            var nonAvasDevice = new AvasDevice
            {
                MacAddress = "00:11:22:33:44:55",
                VendorId = 999,
                ProductId = 11
            };

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                client.RebootChipAsync(nonAvasDevice, nonAvasDevice.MacAddress));
        }

        [Fact]
        public async Task RebootMcuAsync_WhenDeviceIsAvas_SendsUartCommandViaChip0Mac()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            var receivedCommands = new StringBuilder();
            var serverTask = Task.Run(async () =>
            {
                try
                {
                    using var client = await listener.AcceptTcpClientAsync();
                    using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII);
                    using var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true, NewLine = "\r\n" };

                    string? line;
                    while ((line = await reader.ReadLineAsync()) != null)
                    {
                        receivedCommands.AppendLine(line);
                        await writer.WriteLineAsync("{\"status\":\"SUCCESS\"}");
                    }
                }
                catch (IOException) { }
                catch (SocketException) { }
            });

            try
            {
                using var sdvoeClient = new SdvoeClient("127.0.0.1", port);

                var avasDevice = new AvasDevice
                {
                    MacAddress = "00:14:95:AA:BB:CC",
                    CompanionMac = "00:14:95:AA:BB:CD",
                    VendorId = 105,
                    ProductId = 3
                };

                bool ok = await sdvoeClient.RebootMcuAsync(avasDevice);
                Assert.True(ok);

                sdvoeClient.DisconnectTelnet();
                await serverTask;

                string log = receivedCommands.ToString();
                Assert.Contains("send 001495aabbcc rs232:1 set system reboot", log);
            }
            finally
            {
                listener.Stop();
            }
        }

        [Fact]
        public async Task RebootChipAsync_WhenTelnetFails_FallsBackToRestApi()
        {
            int httpPort = GetRandomUnusedPort();
            var httpListener = new HttpListener();
            httpListener.Prefixes.Add($"http://127.0.0.1:{httpPort}/");
            httpListener.Start();

            string receivedBody = string.Empty;
            string requestedUrl = string.Empty;
            var serverTask = Task.Run(async () =>
            {
                var context = await httpListener.GetContextAsync();
                requestedUrl = context.Request.RawUrl ?? string.Empty;
                using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                receivedBody = await reader.ReadToEndAsync();
                byte[] responseBytes = Encoding.UTF8.GetBytes("{\"status\":\"SUCCESS\"}");
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = responseBytes.Length;
                await context.Response.OutputStream.WriteAsync(responseBytes, 0, responseBytes.Length);
                context.Response.Close();
            });

            try
            {
                int deadTelnetPort = GetRandomUnusedPort();
                using var sdvoeClient = new SdvoeClient("127.0.0.1", deadTelnetPort, httpPort);

                var avasDevice = new AvasDevice
                {
                    MacAddress = "00:14:95:AA:BB:CC",
                    VendorId = 105,
                    ProductId = 3
                };

                bool ok = await sdvoeClient.RebootChipAsync(avasDevice, "00:14:95:AA:BB:CC");
                Assert.True(ok);

                await serverTask;
                Assert.Contains("/api/device/001495aabbcc", requestedUrl);
                Assert.Contains("\"op\":\"reboot\"", receivedBody);
            }
            finally
            {
                httpListener.Stop();
            }
        }

        [Fact]
        public async Task FactoryResetAvpAsync_WhenTelnetFails_FallsBackToRestApi()
        {
            int httpPort = GetRandomUnusedPort();
            var httpListener = new HttpListener();
            httpListener.Prefixes.Add($"http://127.0.0.1:{httpPort}/");
            httpListener.Start();

            string receivedBody = string.Empty;
            string requestedUrl = string.Empty;
            var serverTask = Task.Run(async () =>
            {
                var context = await httpListener.GetContextAsync();
                requestedUrl = context.Request.RawUrl ?? string.Empty;
                using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                receivedBody = await reader.ReadToEndAsync();
                byte[] responseBytes = Encoding.UTF8.GetBytes("{\"status\":\"SUCCESS\"}");
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = responseBytes.Length;
                await context.Response.OutputStream.WriteAsync(responseBytes, 0, responseBytes.Length);
                context.Response.Close();
            });

            try
            {
                int deadTelnetPort = GetRandomUnusedPort();
                using var sdvoeClient = new SdvoeClient("127.0.0.1", deadTelnetPort, httpPort);

                var avasDevice = new AvasDevice
                {
                    MacAddress = "00:14:95:AA:BB:CC",
                    VendorId = 105,
                    ProductId = 3
                };

                bool ok = await sdvoeClient.FactoryResetAvpAsync(avasDevice, "00:14:95:AA:BB:CC");
                Assert.True(ok);

                await serverTask;
                Assert.Contains("/api/device/001495aabbcc", requestedUrl);
                Assert.Contains("\"op\":\"factory_default\"", receivedBody);
            }
            finally
            {
                httpListener.Stop();
            }
        }

        [Fact]
        public async Task SendMcuCommandAsync_ViaRest_AppendsCrLfProperly()
        {
            int httpPort = GetRandomUnusedPort();
            var httpListener = new HttpListener();
            httpListener.Prefixes.Add($"http://127.0.0.1:{httpPort}/");
            httpListener.Start();

            var receivedBodies = new System.Collections.Generic.List<string>();
            var serverTask = Task.Run(async () =>
            {
                for (int i = 0; i < 2; i++)
                {
                    var context = await httpListener.GetContextAsync();
                    using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                    string b = await reader.ReadToEndAsync();
                    lock (receivedBodies) { receivedBodies.Add(b); }
                    byte[] responseBytes = Encoding.UTF8.GetBytes("{\"status\":\"SUCCESS\"}");
                    context.Response.ContentType = "application/json";
                    context.Response.ContentLength64 = responseBytes.Length;
                    await context.Response.OutputStream.WriteAsync(responseBytes, 0, responseBytes.Length);
                    context.Response.Close();
                }
            });

            try
            {
                int deadTelnetPort = GetRandomUnusedPort();
                using var sdvoeClient = new SdvoeClient("127.0.0.1", deadTelnetPort, httpPort);

                // First call without trailing \r\n
                await sdvoeClient.SendMcuCommandAsync("00:14:95:AA:BB:CC", "set system reboot");

                await serverTask;

                Assert.Equal(2, receivedBodies.Count); // EnsureUartApiMode + SendMcuCommand
                string sendCmdPayload = receivedBodies[1];
                // JSON serialized data_string with \r\n contains "\\r\\n"
                Assert.Contains("\"data_string\":\"set system reboot\\r\\n\"", sendCmdPayload);
            }
            finally
            {
                httpListener.Stop();
            }
        }

        [Fact]
        public async Task StartPreviewStreamViaAvpRs232Async_ViaRest_SendsRtpIgmpAndRtpOnWithCrLf()
        {
            int httpPort = GetRandomUnusedPort();
            var httpListener = new HttpListener();
            httpListener.Prefixes.Add($"http://127.0.0.1:{httpPort}/");
            httpListener.Start();

            var receivedBodies = new System.Collections.Generic.List<string>();
            var serverTask = Task.Run(async () =>
            {
                // EnsureUartApiMode + set rtp igmp + set rtp ON (3 requests)
                for (int i = 0; i < 3; i++)
                {
                    var context = await httpListener.GetContextAsync();
                    using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                    string b = await reader.ReadToEndAsync();
                    lock (receivedBodies) { receivedBodies.Add(b); }
                    byte[] responseBytes = Encoding.UTF8.GetBytes("{\"status\":\"SUCCESS\"}");
                    context.Response.ContentType = "application/json";
                    context.Response.ContentLength64 = responseBytes.Length;
                    await context.Response.OutputStream.WriteAsync(responseBytes, 0, responseBytes.Length);
                    context.Response.Close();
                }
            });

            try
            {
                int deadTelnetPort = GetRandomUnusedPort();
                using var sdvoeClient = new SdvoeClient("127.0.0.1", deadTelnetPort, httpPort);

                bool success = await sdvoeClient.StartPreviewStreamViaAvpRs232Async("00:14:95:AA:BB:CC", "225.1.1.5");
                Assert.True(success);

                await serverTask;

                Assert.Equal(3, receivedBodies.Count);
                Assert.Contains("\"data_string\":\"set rtp igmp 225.1.1.5\\r\\n\"", receivedBodies[1]);
                Assert.Contains("\"data_string\":\"set rtp ON\\r\\n\"", receivedBodies[2]);
            }
            finally
            {
                httpListener.Stop();
            }
        }

        [Fact]
        public async Task StartPreviewStreamViaAvpRs232Async_WhenRestFails_FallsBackToTelnet()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int telnetPort = ((IPEndPoint)listener.LocalEndpoint).Port;

            var receivedCommands = new StringBuilder();
            var serverTask = Task.Run(async () =>
            {
                try
                {
                    using var client = await listener.AcceptTcpClientAsync();
                    using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII);
                    using var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true, NewLine = "\r\n" };

                    string? line;
                    while ((line = await reader.ReadLineAsync()) != null)
                    {
                        receivedCommands.AppendLine(line);
                        await writer.WriteLineAsync("{\"status\":\"SUCCESS\"}");
                    }
                }
                catch (IOException) { }
                catch (SocketException) { }
            });

            try
            {
                int deadHttpPort = GetRandomUnusedPort();
                using var sdvoeClient = new SdvoeClient("127.0.0.1", telnetPort, deadHttpPort);

                bool success = await sdvoeClient.StartPreviewStreamViaAvpRs232Async("00:14:95:AA:BB:CC", "225.1.1.8");
                Assert.True(success);

                sdvoeClient.DisconnectTelnet();
                await serverTask;

                string log = receivedCommands.ToString();
                Assert.Contains("send 001495aabbcc rs232:1 set rtp igmp 225.1.1.8", log);
                Assert.Contains("send 001495aabbcc rs232:1 set rtp ON", log);
            }
            finally
            {
                listener.Stop();
            }
        }

        [Fact]
        public async Task StopPreviewStreamViaAvpRs232Async_SendsSetRtpOffWithCrLfAndTelnetFallback()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int telnetPort = ((IPEndPoint)listener.LocalEndpoint).Port;

            var receivedCommands = new StringBuilder();
            var serverTask = Task.Run(async () =>
            {
                try
                {
                    using var client = await listener.AcceptTcpClientAsync();
                    using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII);
                    using var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true, NewLine = "\r\n" };

                    string? line;
                    while ((line = await reader.ReadLineAsync()) != null)
                    {
                        receivedCommands.AppendLine(line);
                        await writer.WriteLineAsync("{\"status\":\"SUCCESS\"}");
                    }
                }
                catch (IOException) { }
                catch (SocketException) { }
            });

            try
            {
                int deadHttpPort = GetRandomUnusedPort();
                using var sdvoeClient = new SdvoeClient("127.0.0.1", telnetPort, deadHttpPort);

                bool success = await sdvoeClient.StopPreviewStreamViaAvpRs232Async("00:14:95:AA:BB:CC");
                Assert.True(success);

                sdvoeClient.DisconnectTelnet();
                await serverTask;

                string log = receivedCommands.ToString();
                Assert.Contains("send 001495aabbcc rs232:1 set rtp OFF", log);
            }
            finally
            {
                listener.Stop();
            }
        }

        private static int GetRandomUnusedPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }
}