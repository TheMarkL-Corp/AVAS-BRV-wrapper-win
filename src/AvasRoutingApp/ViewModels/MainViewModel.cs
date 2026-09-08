using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using AvasRoutingApp.Configuration;
using AvasRoutingApp.Rtp;
using AvasRoutingApp.Sdvoe;

namespace AvasRoutingApp.ViewModels
{
    /// <summary>
    /// Root ViewModel coordinating the native overlay sidebar, SDVoE discovery,
    /// dynamic multicast IP allocation, and live preview stream receivers.
    /// </summary>
    public class MainViewModel : ViewModelBase, IAsyncDisposable, IDisposable
    {
        private readonly ISdvoeDiscoveryService _discoveryService;
        private readonly IMulticastController _multicastController;
        private readonly IConfigService _configService;
        private readonly SemaphoreSlim _actionLock = new(1, 1);

        private bool _isSidebarExpanded;
        private double _sidebarWidth;
        private string _statusText = "Ready";
        private string _serverStatusText = "SDVoE Server: Ready";
        private string _multicastPoolRangeText = "Multicast: 224.1.1.1 - 224.1.3.225";
        private int _discoveredEncoderCount;
        private int _activeStreamCount;
        private bool _isBusy;
        private bool _disposed;

        public ObservableCollection<EncoderCardViewModel> EncoderCards { get; } = new();

        public bool IsSidebarExpanded
        {
            get => _isSidebarExpanded;
            set
            {
                if (SetProperty(ref _isSidebarExpanded, value))
                {
                    OnPropertyChanged(nameof(ToggleButtonText));
                    OnPropertyChanged(nameof(ToggleTooltip));
                }
            }
        }

        public double SidebarWidth
        {
            get => _sidebarWidth;
            set => SetProperty(ref _sidebarWidth, value);
        }

        public double ToggleStripWidth { get; } = 28.0;

        public string ToggleButtonText => _isSidebarExpanded ? "▶ CLOSE" : "◀ PREVIEW";

        public string ToggleTooltip => _isSidebarExpanded ? "Collapse Live Preview Sidebar" : "Expand Live Preview Sidebar";

        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        public string ServerStatusText
        {
            get => _serverStatusText;
            set => SetProperty(ref _serverStatusText, value);
        }

        public string MulticastPoolRangeText
        {
            get => _multicastPoolRangeText;
            set => SetProperty(ref _multicastPoolRangeText, value);
        }

        public int DiscoveredEncoderCount
        {
            get => _discoveredEncoderCount;
            set => SetProperty(ref _discoveredEncoderCount, value);
        }

        public int ActiveStreamCount
        {
            get => _activeStreamCount;
            set => SetProperty(ref _activeStreamCount, value);
        }

        public bool IsBusy
        {
            get => _isBusy;
            set => SetProperty(ref _isBusy, value);
        }

        public IConfigService ConfigService => _configService;
        public ISdvoeDiscoveryService DiscoveryService => _discoveryService;
        public IMulticastController MulticastController => _multicastController;

        public ICommand ToggleSidebarCommand { get; }
        public ICommand RefreshDevicesCommand { get; }
        public ICommand StopAllStreamsCommand { get; }
        public ICommand StartAllStreamsCommand { get; }

        public MainViewModel(
            ISdvoeDiscoveryService? discoveryService = null,
            IMulticastController? multicastController = null,
            IConfigService? configService = null)
        {
            _configService = configService ?? new ConfigService();

            if (discoveryService != null && multicastController != null)
            {
                _discoveryService = discoveryService;
                _multicastController = multicastController;
            }
            else
            {
                var cfg = _configService.Current;
                var ipManager = new MulticastIpManager(cfg.MulticastStartIp, cfg.MulticastEndIp, cfg.BasePort);
                var client = new SdvoeClient(cfg.ControlServerIp, cfg.TelnetPort, cfg.RestPort, ipManager);
                _discoveryService = discoveryService ?? client;
                _multicastController = multicastController ?? client;
            }

            _isSidebarExpanded = false;
            _sidebarWidth = 0.0;

            UpdateConfigInfo(_configService.Current);
            _configService.ConfigChanged += UpdateConfigInfo;

            ToggleSidebarCommand = new RelayCommand(async () => await ToggleSidebarAsync());
            RefreshDevicesCommand = new RelayCommand(async () => await RefreshDevicesAsync());
            StopAllStreamsCommand = new RelayCommand(async () => await StopAllStreamsAsync());
            StartAllStreamsCommand = new RelayCommand(async () => await StartAllStreamsAsync());
        }

        private void UpdateConfigInfo(AppConfig config)
        {
            ServerStatusText = $"SDVoE Server: {config.ControlServerIp}:{config.RestPort}";
            MulticastPoolRangeText = $"Multicast: {config.MulticastStartIp} - {config.MulticastEndIp} (Port {config.BasePort})";
        }

        public async Task ToggleSidebarAsync(CancellationToken ct = default)
        {
            if (IsSidebarExpanded)
            {
                await CollapseSidebarAsync(ct);
            }
            else
            {
                await ExpandSidebarAsync(ct);
            }
        }

        /// <summary>
        /// Expands the preview sidebar, discovers active AVAS-223 chip_0 encoders,
        /// allocates multicast IPs, and begins video stream reception.
        /// </summary>
        public async Task<bool> ExpandSidebarAsync(CancellationToken ct = default)
        {
            if (_disposed) return false;

            try
            {
                await _actionLock.WaitAsync(ct);
            }
            catch (ObjectDisposedException)
            {
                return false;
            }

            try
            {
                if (_disposed) return false;

                IsSidebarExpanded = true;
                SidebarWidth = 380.0;
                StatusText = "Expanding sidebar | Discovering AVAS-223 encoders...";
                IsBusy = true;

                await StartAllStreamsInternalAsync(ct);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                StatusText = $"Error expanding sidebar: {ex.Message}";
                return false;
            }
            finally
            {
                IsBusy = false;
                try
                {
                    _actionLock.Release();
                }
                catch (ObjectDisposedException) { }
            }
        }

        /// <summary>
        /// Instantly collapses the sidebar and executes full resource teardown:
        /// stops preview streams on hardware, releases multicast IPs back to pool,
        /// drops multicast socket memberships, and disposes receivers.
        /// </summary>
        public async Task<bool> CollapseSidebarAsync(CancellationToken ct = default)
        {
            if (_disposed) return false;

            try
            {
                await _actionLock.WaitAsync(ct);
            }
            catch (ObjectDisposedException)
            {
                return false;
            }

            try
            {
                IsSidebarExpanded = false;
                SidebarWidth = 0.0;
                StatusText = "Sidebar collapsed | Tearing down streams...";
                IsBusy = true;

                await StopAllStreamsInternalAsync(ct);
                StatusText = "Sidebar collapsed | All streams released";
                return true;
            }
            catch (Exception ex)
            {
                StatusText = $"Error collapsing sidebar: {ex.Message}";
                return false;
            }
            finally
            {
                IsBusy = false;
                try
                {
                    _actionLock.Release();
                }
                catch (ObjectDisposedException) { }
            }
        }

        public async Task RefreshDevicesAsync(CancellationToken ct = default)
        {
            if (!IsSidebarExpanded || _disposed) return;

            try
            {
                await _actionLock.WaitAsync(ct);
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            try
            {
                IsBusy = true;
                StatusText = "Refreshing AVAS-223 devices...";
                await StopAllStreamsInternalAsync(ct);
                await StartAllStreamsInternalAsync(ct);
            }
            finally
            {
                IsBusy = false;
                try { _actionLock.Release(); } catch (ObjectDisposedException) { }
            }
        }

        public async Task StartAllStreamsAsync(CancellationToken ct = default)
        {
            if (_disposed) return;

            try
            {
                await _actionLock.WaitAsync(ct);
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            try
            {
                IsBusy = true;
                await StartAllStreamsInternalAsync(ct);
            }
            finally
            {
                IsBusy = false;
                try { _actionLock.Release(); } catch (ObjectDisposedException) { }
            }
        }

        public async Task StopAllStreamsAsync(CancellationToken ct = default)
        {
            if (_disposed) return;

            try
            {
                await _actionLock.WaitAsync(ct);
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            try
            {
                IsBusy = true;
                await StopAllStreamsInternalAsync(ct);
            }
            finally
            {
                IsBusy = false;
                try { _actionLock.Release(); } catch (ObjectDisposedException) { }
            }
        }

        private async Task StartAllStreamsInternalAsync(CancellationToken ct)
        {
            if (_disposed) return;

            IReadOnlyList<AvasDevice> devices;
            try
            {
                devices = await _discoveryService.DiscoverAvas223DevicesAsync(ct);
            }
            catch (Exception ex)
            {
                StatusText = $"Device discovery failed: {ex.Message}";
                return;
            }

            // Strictly filter for AVAS-223 chip_0 transmitters (VID 105, PID 81, chip_0)
            var filteredEncoders = devices
                .Where(SdvoeDiscoveryFilter.IsTargetAvas223Tx)
                .ToList();

            DiscoveredEncoderCount = filteredEncoders.Count;

            int startedCount = 0;
            int basePort = _configService.Current.BasePort;
            string localNic = _configService.Current.LocalNetworkInterfaceIp;

            foreach (var dev in filteredEncoders)
            {
                if (ct.IsCancellationRequested || _disposed) break;

                // Check if card already exists for this MAC
                var existingCard = EncoderCards.FirstOrDefault(c => c.MacAddress.Equals(dev.MacAddress, StringComparison.OrdinalIgnoreCase));
                if (existingCard != null) continue;

                string? mcastIp = _multicastController.AllocateMulticastIp(dev.MacAddress);
                if (string.IsNullOrEmpty(mcastIp))
                {
                    continue; // Pool exhausted
                }

                // Command SDVoE Control Server to start thumbnail streaming
                bool started = await _multicastController.StartPreviewStreamAsync(dev.MacAddress, mcastIp, basePort, ct);

                // Build Card ViewModel
                var card = new EncoderCardViewModel
                {
                    MacAddress = dev.MacAddress,
                    DeviceName = dev.DeviceName,
                    UnicastIp = dev.IpAddress,
                    MulticastIp = mcastIp,
                    Port = basePort,
                    Resolution = "320x180",
                    IsStreaming = started
                };

                // Create UDP Multicast Receiver and bind to card
                var receiver = new RtpMulticastReceiver();
                card.AttachReceiver(receiver);

                try
                {
                    receiver.StartListening(mcastIp, basePort, localNic);
                }
                catch (Exception ex)
                {
                    card.StatusMessage = $"Socket bind error: {ex.Message}";
                }

                EncoderCards.Add(card);
                startedCount++;
            }

            ActiveStreamCount = EncoderCards.Count;
            StatusText = $"Active previews: {ActiveStreamCount} / {DiscoveredEncoderCount} AVAS-223 chip_0 encoders";
        }

        private async Task StopAllStreamsInternalAsync(CancellationToken ct)
        {
            var cardsToStop = EncoderCards.ToList();
            foreach (var card in cardsToStop)
            {
                try
                {
                    // 1. Drop multicast group and dispose receiver sockets
                    if (card.Receiver != null)
                    {
                        card.Receiver.StopListening();
                        card.Receiver.Dispose();
                    }
                    card.DetachReceiver();
                }
                catch { }

                try
                {
                    // 2. Command encoder to stop streaming
                    await _multicastController.StopPreviewStreamAsync(card.MacAddress, ct);
                }
                catch { }

                try
                {
                    // 3. Release allocated multicast IP back to pool
                    _multicastController.ReleaseMulticastIp(card.MacAddress);
                }
                catch { }

                card.Dispose();
            }

            EncoderCards.Clear();
            ActiveStreamCount = 0;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _configService.ConfigChanged -= UpdateConfigInfo;
            try
            {
                var stopTask = Task.Run(async () => await StopAllStreamsInternalAsync(CancellationToken.None));
                stopTask.Wait(1500);
            }
            catch { }

            GC.SuppressFinalize(this);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;

            _configService.ConfigChanged -= UpdateConfigInfo;
            try
            {
                await StopAllStreamsInternalAsync(CancellationToken.None);
            }
            catch { }

            GC.SuppressFinalize(this);
        }
    }
}
