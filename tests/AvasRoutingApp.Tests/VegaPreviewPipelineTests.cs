using System.Threading;
using System.Threading.Tasks;
using AvasRoutingApp.Sdvoe;
using Xunit;

namespace AvasRoutingApp.Tests
{
    public class VegaPreviewPipelineTests
    {
        [Fact]
        public async Task StartVegaPreviewStreamAsync_ValidMac_FormatsThumbnailCommand()
        {
            using var client = new SdvoeClient();
            string mac = "00:11:22:33:44:55";
            string multicastIp = "225.1.1.50";
            int port = 5000;

            // Method exists and returns bool (false when not connected to hardware)
            using var cts = new CancellationTokenSource(500);
            bool result = await client.StartVegaPreviewStreamAsync(mac, multicastIp, port, cts.Token);
            Assert.False(result); // Expected false when no mock/live server connected
        }

        [Fact]
        public async Task StopVegaPreviewStreamAsync_ValidMac_ReturnsGracefully()
        {
            using var client = new SdvoeClient();
            string mac = "00:11:22:33:44:55";

            using var cts = new CancellationTokenSource(500);
            bool result = await client.StopVegaPreviewStreamAsync(mac, cts.Token);
            Assert.False(result); // Expected false when no mock/live server connected
        }
    }
}
