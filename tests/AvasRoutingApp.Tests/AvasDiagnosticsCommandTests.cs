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
        public async Task FactoryResetAvpAsync_WhenDeviceIsVega_ThrowsInvalidOperationException()
        {
            using var client = new SdvoeClient();
            var vegaDevice = new AvasDevice
            {
                MacAddress = "00:11:22:33:44:55",
                VendorId = 192,
                ProductId = 11
            };

            Assert.Equal(DeviceFamily.Vega, vegaDevice.Family);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                client.FactoryResetAvpAsync(vegaDevice, vegaDevice.MacAddress));
        }

        [Fact]
        public async Task FactoryResetMcuAsync_WhenDeviceIsVega_ThrowsInvalidOperationException()
        {
            using var client = new SdvoeClient();
            var vegaDevice = new AvasDevice
            {
                MacAddress = "00:11:22:33:44:55",
                VendorId = 192,
                ProductId = 11
            };

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                client.FactoryResetMcuAsync(vegaDevice));
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
                using var client = await listener.AcceptTcpClientAsync();
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII);
                using var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true, NewLine = "\r\n" };

                while (!reader.EndOfStream)
                {
                    var line = await reader.ReadLineAsync();
                    if (line == null) break;
                    receivedCommands.AppendLine(line);
                    if (line.StartsWith("require api", StringComparison.OrdinalIgnoreCase))
                    {
                        await writer.WriteLineAsync("{\"status\":\"SUCCESS\"}");
                    }
                    else
                    {
                        await writer.WriteLineAsync("{\"status\":\"SUCCESS\"}");
                    }
                }
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
                using var client = await listener.AcceptTcpClientAsync();
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII);
                using var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true, NewLine = "\r\n" };

                while (!reader.EndOfStream)
                {
                    var line = await reader.ReadLineAsync();
                    if (line == null) break;
                    receivedCommands.AppendLine(line);
                    if (line.StartsWith("require api", StringComparison.OrdinalIgnoreCase))
                    {
                        await writer.WriteLineAsync("{\"status\":\"SUCCESS\"}");
                    }
                    else
                    {
                        await writer.WriteLineAsync("{\"status\":\"SUCCESS\"}");
                    }
                }
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
                using var client = await listener.AcceptTcpClientAsync();
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII);
                using var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true, NewLine = "\r\n" };

                while (!reader.EndOfStream)
                {
                    var line = await reader.ReadLineAsync();
                    if (line == null) break;
                    receivedCommands.AppendLine(line);
                    if (line.StartsWith("require api", StringComparison.OrdinalIgnoreCase))
                    {
                        await writer.WriteLineAsync("{\"status\":\"SUCCESS\"}");
                    }
                    else
                    {
                        await writer.WriteLineAsync("{\"status\":\"SUCCESS\"}");
                    }
                }
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
    }
}
