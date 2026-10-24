using System;
using System.Threading.Tasks;
using Xunit;
using E2ETests.Harness;

namespace E2ETests.Suites
{
    public class SingleAndDualLinkPreviewE2ETests
    {
        [Fact]
        public async Task E2E_SingleLinkTransmitter_StartsStreamViaScaler_AndTearsDownWithZeroAddress()
        {
            var mockTelnet = new MockTelnetServer();
            int port = mockTelnet.Start();
            var client = new AvasRoutingApp.Sdvoe.SdvoeClient("127.0.0.1", telnetPort: port, restPort: 8080);

            // Start stream in Single Link mode
            bool started = await client.StartPreviewStreamAsync("74fe4887fdf4", "225.0.0.10", 5004, linkMode: "SINGLE", chipIndex: 0);
            Assert.True(started);
            Assert.Contains(mockTelnet.ReceivedCommands, c => c.Contains("stream HDMI 1 enable true address 225.0.0.10"));
            Assert.Contains(mockTelnet.ReceivedCommands, c => c.Contains("auto_stop true"));

            // Teardown
            bool stopped = await client.StopPreviewStreamAsync("74fe4887fdf4", linkMode: "SINGLE", chipIndex: 0);
            Assert.True(stopped);
            Assert.Contains(mockTelnet.ReceivedCommands, c => c.Contains("stream HDMI 1 enable false address 0.0.0.0"));
            Assert.Contains(mockTelnet.ReceivedCommands, c => c.Contains("stop 74fe4887fdf4:thumbnail:0 free"));

            client.Dispose();
            mockTelnet.Stop();
        }

        [Fact]
        public async Task E2E_DualLinkTransmitter_UsesUartPath()
        {
            var mockTelnet = new MockTelnetServer();
            int port = mockTelnet.Start();
            var client = new AvasRoutingApp.Sdvoe.SdvoeClient("127.0.0.1", telnetPort: port, restPort: 8080);

            bool started = await client.StartPreviewStreamAsync("74fe4887fdf4", "225.0.0.11", 5000, linkMode: "DUAL", chipIndex: 0);
            Assert.True(started);

            // Verify it attempted or executed the UART RS-232 path
            Assert.Contains(mockTelnet.ReceivedCommands, c => c.Contains("configuration:rs232:1:mode api") || c.Contains("rs232:1 set rtp igmp 225.0.0.11"));

            client.Dispose();
            mockTelnet.Stop();
        }

        [Fact]
        public async Task E2E_SingleLink_Chip1Switching_TearsDownChip0_AndStartsChip1()
        {
            var mockTelnet = new MockTelnetServer();
            int port = mockTelnet.Start();
            var client = new AvasRoutingApp.Sdvoe.SdvoeClient("127.0.0.1", telnetPort: port, restPort: 8080);

            // 1. Initial state: Chip 0 is streaming
            bool chip0Started = await client.StartPreviewStreamAsync("74fe4887fdf4", "225.0.0.10", 5004, linkMode: "SINGLE", chipIndex: 0);
            Assert.True(chip0Started);

            // 2. Simulate switching from Chip 0 to Chip 1 in single-link mode:
            // Stops Chip 0 with 0.0.0.0 address
            bool chip0Stopped = await client.StopPreviewStreamAsync("74fe4887fdf4", linkMode: "SINGLE", chipIndex: 0);
            Assert.True(chip0Stopped);

            // Starts Chip 1 ("74fe4887fdf5") on stream HDMI 1
            bool chip1Started = await client.StartPreviewStreamAsync("74fe4887fdf5", "225.0.0.10", 5004, linkMode: "SINGLE", chipIndex: 1);
            Assert.True(chip1Started);

            // 3. Verifies both command sequences in the received commands ledger
            Assert.Contains(mockTelnet.ReceivedCommands, c => c.Contains("74fe4887fdf4") && c.Contains("stream HDMI 1 enable false address 0.0.0.0"));
            Assert.Contains(mockTelnet.ReceivedCommands, c => c.Contains("74fe4887fdf5") && c.Contains("stream HDMI 1 enable true address 225.0.0.10"));

            client.Dispose();
            mockTelnet.Stop();
        }

        [Fact]
        public async Task Harness_SingleLinkTransmitter_StartsAndStopsStream()
        {
            var mockTelnet = new MockTelnetServer();
            int port = mockTelnet.Start();
            var harnessClient = new E2ETests.Harness.SdvoeClient("127.0.0.1", telnetPort: port, restPort: 8080);

            bool started = await harnessClient.StartPreviewStreamAsync("74fe4887fdf4", "225.0.0.10", 5004, linkMode: "SINGLE", chipIndex: 0);
            Assert.True(started);
            Assert.Contains(mockTelnet.ReceivedCommands, c => c.Contains("stream HDMI 1 enable true address 225.0.0.10"));

            bool stopped = await harnessClient.StopPreviewStreamAsync("74fe4887fdf4", linkMode: "SINGLE", chipIndex: 0);
            Assert.True(stopped);
            Assert.Contains(mockTelnet.ReceivedCommands, c => c.Contains("stream HDMI 1 enable false address 0.0.0.0"));

            harnessClient.Dispose();
            mockTelnet.Stop();
        }
    }
}
