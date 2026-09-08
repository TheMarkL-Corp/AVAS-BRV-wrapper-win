using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace E2ETests.Harness
{
    public class EncoderCardViewModel
    {
        public string MacAddress { get; set; } = "";
        public string DeviceName { get; set; } = "";
        public string MulticastIp { get; set; } = "";
        public int Port { get; set; } = 6792;
        public string Resolution { get; set; } = "320x180";
        public double CurrentFps { get; set; } = 0.0;
        public byte[]? LastRenderedRgbFrame { get; set; }
        public int FrameUpdateCount { get; set; }
        public IRtpStreamReceiver? Receiver { get; set; }
    }

    public class SidebarCoordinator : IAsyncDisposable, IDisposable
    {
        private readonly SdvoeClient _sdvoeClient;
        private readonly MulticastIpManager _ipManager;
        private readonly ConcurrentDictionary<string, EncoderCardViewModel> _cards = new();
        private readonly object _stateLock = new();

        public bool IsExpanded { get; private set; }
        public double SidebarWidth { get; private set; }
        public double ToggleStripWidth { get; } = 28.0;

        public IReadOnlyCollection<EncoderCardViewModel> ActiveCards => _cards.Values.ToList();

        public SidebarCoordinator(SdvoeClient sdvoeClient, MulticastIpManager ipManager)
        {
            _sdvoeClient = sdvoeClient;
            _ipManager = ipManager;
            IsExpanded = false;
            SidebarWidth = 0.0;
        }

        public async Task<bool> ExpandAsync(CancellationToken ct = default)
        {
            lock (_stateLock)
            {
                if (IsExpanded) return true;
                IsExpanded = true;
                SidebarWidth = 380.0;
            }

            try
            {
                // Discover and start streaming on discovered AVAS-223 chip_0 devices
                var devices = await _sdvoeClient.DiscoverAvas223DevicesAsync(ct);

                foreach (var dev in devices)
                {
                    if (ct.IsCancellationRequested) break;

                    string? mcastIp = _ipManager.AllocateMulticastIp(dev.MacAddress);
                    if (mcastIp == null) continue; // Pool exhausted or unavailable

                    // Configure and start on SDVoE server
                    await _sdvoeClient.ConfigureThumbnailStreamAsync(dev.MacAddress, 1.0, 12345, _ipManager.BasePort, ct);
                    await _sdvoeClient.StartPreviewStreamAsync(dev.MacAddress, mcastIp, ct);

                    // Setup card & receiver
                    var card = new EncoderCardViewModel
                    {
                        MacAddress = dev.MacAddress,
                        DeviceName = dev.DeviceName,
                        MulticastIp = mcastIp,
                        Port = _ipManager.BasePort,
                        Resolution = "320x180"
                    };

                    var receiver = new RtpMulticastReceiver();
                    receiver.FrameReady += (rgb, w, h) =>
                    {
                        card.LastRenderedRgbFrame = rgb;
                        card.FrameUpdateCount++;
                        card.Resolution = $"{w}x{h}";
                    };
                    receiver.FpsUpdated += fps =>
                    {
                        card.CurrentFps = fps;
                    };

                    receiver.StartListening(mcastIp, _ipManager.BasePort);
                    card.Receiver = receiver;

                    _cards[dev.MacAddress] = card;
                }

                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        public async Task<bool> CollapseAsync(CancellationToken ct = default)
        {
            lock (_stateLock)
            {
                if (!IsExpanded) return true;
                IsExpanded = false;
                SidebarWidth = 0.0;
            }

            // Instant teardown per R4.3:
            // 1. Halt server streams & free IPs
            // 2. Drop sockets & multicast memberships
            // 3. Deallocate local IP pool
            foreach (var kvp in _cards)
            {
                string mac = kvp.Key;
                var card = kvp.Value;

                try
                {
                    card.Receiver?.StopListening();
                    card.Receiver?.Dispose();
                }
                catch { }

                try
                {
                    await _sdvoeClient.StopPreviewStreamAsync(mac, free: true, ct);
                }
                catch { }

                _ipManager.ReleaseMulticastIp(mac);
            }

            _cards.Clear();
            return true;
        }

        public async Task ToggleAsync(CancellationToken ct = default)
        {
            if (IsExpanded)
            {
                await CollapseAsync(ct);
            }
            else
            {
                await ExpandAsync(ct);
            }
        }

        public void Dispose()
        {
            CollapseAsync().GetAwaiter().GetResult();
        }

        public async ValueTask DisposeAsync()
        {
            await CollapseAsync();
        }
    }
}
