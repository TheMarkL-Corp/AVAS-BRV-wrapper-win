using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using AvasRoutingApp.Configuration;
using AvasRoutingApp.Logging;
using AvasRoutingApp.Rtp;
using AvasRoutingApp.Sdvoe;

namespace AvasRoutingApp.ViewModels
{
    public enum SidebarTab
    {
        Previews,
        MultiLink
    }

    /// <summary>
    /// Root ViewModel coordinating the native overlay sidebar, SDVoE discovery,
    /// dynamic multicast IP allocation, live preview stream receivers, and multi-link topology management.
    /// </summary>
    public class MainViewModel : ViewModelBase, IAsyncDisposable, IDisposable
    {
        private readonly ISdvoeDiscoveryService _discoveryService;
        private readonly IMulticastController _multicastController;
        private readonly IConfigService _configService;
        private readonly IMultiLinkService _multiLinkService;
        private readonly SemaphoreSlim _actionLock = new(1, 1);

        private SidebarTab _selectedTab = SidebarTab.Previews;
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
        public ObservableCollection<MultiLinkCardViewModel> MultiLinkCards { get; } = new();

        public SidebarTab SelectedTab
        {
            get => _selectedTab;
            set
            {
                if (SetProperty(ref _selectedTab, value))
                {
                    OnPropertyChanged(nameof(IsPreviewsTabSelected));
                    OnPropertyChanged(nameof(IsMultiLinkTabSelected));
                }
            }
        }

        public bool IsPreviewsTabSelected => _selectedTab == SidebarTab.Previews;
        public bool IsMultiLinkTabSelected => _selectedTab == SidebarTab.MultiLink;

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
        public IMultiLinkService MultiLinkService => _multiLinkService;

        public ICommand ToggleSidebarCommand { get; }
        public ICommand RefreshDevicesCommand { get; }
        public ICommand StopAllStreamsCommand { get; }
        public ICommand StartAllStreamsCommand { get; }
        public ICommand SelectPreviewsTabCommand { get; }
        public ICommand SelectMultiLinkTabCommand { get; }

        public MainViewModel(
            ISdvoeDiscoveryService? discoveryService = null,
            IMulticastController? multicastController = null,
            IConfigService? configService = null,
            IMultiLinkService? multiLinkService = null)
        {
            _configService = configService ?? new ConfigService();
            _multiLinkService = multiLinkService ?? (_discoveryService as SdvoeClient)?.MultiLinkService ?? new MultiLinkService(_configService);

            if (discoveryService != null && multicastController != null)
            {
                _discoveryService = discoveryService;
                _multicastController = multicastController;
            }
            else
            {
                var cfg = _configService.Current;
                var ipManager = new MulticastIpManager(cfg.MulticastStartIp, cfg.MulticastEndIp, cfg.BasePort);
                var client = new SdvoeClient(cfg.ControlServerIp, cfg.TelnetPort, cfg.RestPort, ipManager, _multiLinkService);
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
            SelectPreviewsTabCommand = new RelayCommand(() => SelectedTab = SidebarTab.Previews);
            SelectMultiLinkTabCommand = new RelayCommand(() => SelectedTab = SidebarTab.MultiLink);
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

                AppLogger.Info("Sidebar", "Expanding preview sidebar — initiating discovery and stream acquisition...");
                await StartAllStreamsInternalAsync(ct);
                AppLogger.Info("Sidebar", $"Sidebar expansion complete | Active streams: {ActiveStreamCount} / {DiscoveredEncoderCount}");
                return true;
            }
            catch (OperationCanceledException)
            {
                AppLogger.Warn("Sidebar", "Sidebar expansion was cancelled.");
                return false;
            }
            catch (Exception ex)
            {
                AppLogger.Error("Sidebar", "Error during sidebar expansion", ex);
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

                AppLogger.Info("Sidebar", "Collapsing preview sidebar — tearing down streams and releasing resources...");
                await StopAllStreamsInternalAsync(ct);
                StatusText = "Sidebar collapsed | All streams released";
                AppLogger.Info("Sidebar", "All preview streams and multicast resources released.");
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Error("Sidebar", "Error during sidebar collapse", ex);
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
                AppLogger.Info("Discovery", "Querying SDVoE Control Server for connected endpoints...");
                devices = await _discoveryService.DiscoverAvas223DevicesAsync(ct);
                AppLogger.Info("Discovery", $"Discovered {devices.Count} total endpoints from SDVoE server.");
            }
            catch (Exception ex)
            {
                AppLogger.Error("Discovery", "Device discovery failed", ex);
                StatusText = $"Device discovery failed: {ex.Message}";
                return;
            }

            // Strictly filter for AVAS-223 chip_0 transmitters (VID 105, PID 81, chip_0)
            var filteredEncoders = devices
                .Where(SdvoeDiscoveryFilter.IsTargetAvas223Tx)
                .ToList();

            DiscoveredEncoderCount = filteredEncoders.Count;
            AppLogger.Info("Discovery", $"Filtered for AVAS-223 chip_0 TX encoders: {DiscoveredEncoderCount} matching device(s).");

            // Query Multi-Link topology to populate MultiLinkCards and enrich preview cards
            IReadOnlyList<MultiLinkInfo> multiLinkPairs = Array.Empty<MultiLinkInfo>();
            try
            {
                multiLinkPairs = await _multiLinkService.QueryMultiLinkPairsAsync(ct);
                AppLogger.Info("Discovery", $"Discovered {multiLinkPairs.Count} multi-link pair(s).");
            }
            catch (Exception ex)
            {
                AppLogger.Warn("Discovery", $"Multi-link topology query failed: {ex.Message}");
            }

            var pairLookup = multiLinkPairs.ToDictionary(p => p.PrimaryMac, StringComparer.OrdinalIgnoreCase);

            // Synchronize MultiLinkCards
            foreach (var pair in multiLinkPairs)
            {
                var existingMlCard = MultiLinkCards.FirstOrDefault(c => c.PrimaryMac.Equals(pair.PrimaryMac, StringComparison.OrdinalIgnoreCase));
                if (existingMlCard == null)
                {
                    var mlCard = new MultiLinkCardViewModel(_multiLinkService)
                    {
                        PrimaryMac = pair.PrimaryMac,
                        PrimaryName = pair.PrimaryName,
                        PrimaryIp = pair.PrimaryIp,
                        LinkMode = pair.LinkMode,
                        CompanionMac = pair.CompanionMac,
                        CompanionName = pair.CompanionName,
                        CompanionIsActive = pair.CompanionIsActive
                    };
                    mlCard.ModeSwitchStarted += OnCardModeSwitchStartedAsync;
                    mlCard.ModeSwitchCompleted += OnCardModeSwitchCompletedAsync;
                    MultiLinkCards.Add(mlCard);
                }
                else if (!existingMlCard.IsRebooting)
                {
                    existingMlCard.PrimaryName = pair.PrimaryName;
                    existingMlCard.PrimaryIp = pair.PrimaryIp;
                    existingMlCard.LinkMode = pair.LinkMode;
                    existingMlCard.CompanionMac = pair.CompanionMac;
                    existingMlCard.CompanionName = pair.CompanionName;
                    existingMlCard.CompanionIsActive = pair.CompanionIsActive;
                }
            }

            int startedCount = 0;
            int basePort = _configService.Current.BasePort;
            string localNic = _configService.Current.LocalNetworkInterfaceIp;

            foreach (var dev in filteredEncoders)
            {
                if (ct.IsCancellationRequested || _disposed) break;

                pairLookup.TryGetValue(dev.MacAddress, out var matchedPair);
                string linkMode = !string.IsNullOrEmpty(dev.LinkMode) && dev.LinkMode != "UNKNOWN"
                    ? dev.LinkMode
                    : (matchedPair?.LinkMode ?? "UNKNOWN");
                string companionMac = !string.IsNullOrEmpty(dev.CompanionMac) && dev.CompanionMac != "NONE"
                    ? dev.CompanionMac
                    : (matchedPair?.CompanionMac ?? "NONE");
                bool compActive = dev.CompanionIsActive || (matchedPair?.CompanionIsActive ?? false);

                // Check if card already exists for this MAC
                var existingCard = EncoderCards.FirstOrDefault(c => c.MacAddress.Equals(dev.MacAddress, StringComparison.OrdinalIgnoreCase));
                if (existingCard != null)
                {
                    existingCard.LinkMode = linkMode;
                    existingCard.CompanionMac = companionMac;
                    existingCard.CompanionIsActive = compActive;
                    continue;
                }

                string? mcastIp = _multicastController.AllocateMulticastIp(dev.MacAddress);
                if (string.IsNullOrEmpty(mcastIp))
                {
                    AppLogger.Warn("Multicast", $"Multicast pool exhausted! Cannot allocate address for MAC: {dev.MacAddress}");
                    continue; // Pool exhausted
                }

                AppLogger.Info("StreamControl", $"Allocated Multicast IP {mcastIp}:{basePort} for MAC {dev.MacAddress} ({dev.DeviceName})");

                // Command SDVoE Control Server to start thumbnail streaming
                bool started = await _multicastController.StartPreviewStreamAsync(dev.MacAddress, mcastIp, basePort, ct);
                AppLogger.Info("StreamControl", $"Start preview stream response for {dev.MacAddress}: {(started ? "SUCCESS" : "FAILED / UNSUPPORTED")}");

                // Build Card ViewModel
                var card = new EncoderCardViewModel
                {
                    MacAddress = dev.MacAddress,
                    DeviceName = dev.DeviceName,
                    UnicastIp = dev.IpAddress,
                    MulticastIp = mcastIp,
                    Port = basePort,
                    Resolution = "320x180",
                    IsStreaming = started,
                    StatusMessage = started ? "Streaming" : "Stream start request unacknowledged",
                    LinkMode = linkMode,
                    CompanionMac = companionMac,
                    CompanionIsActive = compActive
                };

                // Create UDP Multicast Receiver and bind to card
                var receiver = new RtpMulticastReceiver();
                card.AttachReceiver(receiver);

                try
                {
                    AppLogger.Info("Multicast", $"Binding UDP listener on {mcastIp}:{basePort} (Local NIC: '{(string.IsNullOrEmpty(localNic) ? "ALL" : localNic)}')");
                    receiver.StartListening(mcastIp, basePort, localNic);
                }
                catch (Exception ex)
                {
                    AppLogger.Error("Multicast", $"Socket bind error for {mcastIp}:{basePort}", ex);
                    card.StatusMessage = $"Socket bind error: {ex.Message}";
                }

                EncoderCards.Add(card);
                startedCount++;
            }

            ActiveStreamCount = EncoderCards.Count;
            StatusText = $"Active previews: {ActiveStreamCount} / {DiscoveredEncoderCount} AVAS-223 chip_0 encoders";
            AppLogger.Info("Sidebar", $"Sidebar update finished: {ActiveStreamCount} card(s) active.");
        }

        private async Task OnCardModeSwitchStartedAsync(MultiLinkCardViewModel mlCard, string targetMode)
        {
            AppLogger.Info("MainViewModel", $"Mode switch initiated for {mlCard.PrimaryMac} -> {targetMode}. Gracefully pausing preview stream...");

            var previewCard = EncoderCards.FirstOrDefault(c => c.MacAddress.Equals(mlCard.PrimaryMac, StringComparison.OrdinalIgnoreCase));
            if (previewCard != null)
            {
                try
                {
                    if (previewCard.Receiver != null)
                    {
                        previewCard.Receiver.StopListening();
                        previewCard.Receiver.Dispose();
                        previewCard.DetachReceiver();
                    }

                    await _multicastController.StopPreviewStreamAsync(previewCard.MacAddress, CancellationToken.None);
                    _multicastController.ReleaseMulticastIp(previewCard.MacAddress);

                    previewCard.IsStreaming = false;
                    previewCard.IsWaitingForStream = true;
                    previewCard.StatusMessage = $"Hardware rebooting ({targetMode})...";
                    previewCard.CurrentFps = 0.0;
                    previewCard.LinkMode = targetMode;
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("MainViewModel", $"Error pausing stream for {mlCard.PrimaryMac}: {ex.Message}");
                }
            }
        }

        private async Task OnCardModeSwitchCompletedAsync(MultiLinkCardViewModel mlCard)
        {
            AppLogger.Info("MainViewModel", $"Hardware reboot completed for {mlCard.PrimaryMac}. Resuming preview stream...");

            try
            {
                var pairs = await _multiLinkService.QueryMultiLinkPairsAsync(CancellationToken.None);
                var match = pairs.FirstOrDefault(p => p.PrimaryMac.Equals(mlCard.PrimaryMac, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    mlCard.LinkMode = match.LinkMode;
                    mlCard.CompanionMac = match.CompanionMac;
                    mlCard.CompanionName = match.CompanionName;
                    mlCard.CompanionIsActive = match.CompanionIsActive;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("MainViewModel", $"Failed refreshing multi-link info post-reboot: {ex.Message}");
            }

            var previewCard = EncoderCards.FirstOrDefault(c => c.MacAddress.Equals(mlCard.PrimaryMac, StringComparison.OrdinalIgnoreCase));
            if (previewCard != null)
            {
                previewCard.LinkMode = mlCard.LinkMode;
                previewCard.CompanionMac = mlCard.CompanionMac;
                previewCard.CompanionIsActive = mlCard.CompanionIsActive;

                int basePort = _configService.Current.BasePort;
                string localNic = _configService.Current.LocalNetworkInterfaceIp;

                string? mcastIp = _multicastController.AllocateMulticastIp(previewCard.MacAddress);
                if (!string.IsNullOrEmpty(mcastIp))
                {
                    previewCard.MulticastIp = mcastIp;
                    previewCard.Port = basePort;

                    bool started = await _multicastController.StartPreviewStreamAsync(previewCard.MacAddress, mcastIp, basePort, CancellationToken.None);
                    previewCard.IsStreaming = started;
                    previewCard.StatusMessage = started ? "Streaming" : "Stream restart pending";

                    var receiver = new RtpMulticastReceiver();
                    previewCard.AttachReceiver(receiver);
                    try
                    {
                        receiver.StartListening(mcastIp, basePort, localNic);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Error("Multicast", $"Failed to re-bind socket for {mcastIp}:{basePort}", ex);
                        previewCard.StatusMessage = $"Socket bind error: {ex.Message}";
                    }
                }
            }
        }

        private async Task StopAllStreamsInternalAsync(CancellationToken ct)
        {
            foreach (var mlCard in MultiLinkCards.ToList())
            {
                mlCard.CancelCountdown();
                mlCard.ModeSwitchStarted -= OnCardModeSwitchStartedAsync;
                mlCard.ModeSwitchCompleted -= OnCardModeSwitchCompletedAsync;
            }
            MultiLinkCards.Clear();

            var cardsToStop = EncoderCards.ToList();
            AppLogger.Info("Sidebar", $"Stopping {cardsToStop.Count} active stream cards...");

            foreach (var card in cardsToStop)
            {
                try
                {
                    // 1. Drop multicast group and dispose receiver sockets
                    if (card.Receiver != null)
                    {
                        AppLogger.Debug("Multicast", $"Dropping multicast group for MAC {card.MacAddress} ({card.MulticastIp})");
                        card.Receiver.StopListening();
                        card.Receiver.Dispose();
                    }
                    card.DetachReceiver();
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("Multicast", $"Error stopping receiver for {card.MacAddress}", ex);
                }

                try
                {
                    // 2. Command encoder to stop streaming
                    AppLogger.Debug("StreamControl", $"Stopping preview stream on hardware for {card.MacAddress}");
                    await _multicastController.StopPreviewStreamAsync(card.MacAddress, ct);
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("StreamControl", $"Error commanding stream stop for {card.MacAddress}", ex);
                }

                try
                {
                    // 3. Release allocated multicast IP back to pool
                    AppLogger.Debug("Multicast", $"Releasing multicast IP for MAC {card.MacAddress}");
                    _multicastController.ReleaseMulticastIp(card.MacAddress);
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("Multicast", $"Error releasing multicast IP for {card.MacAddress}", ex);
                }

                card.Dispose();
            }

            EncoderCards.Clear();
            ActiveStreamCount = 0;
            AppLogger.Info("Sidebar", "All cards cleared and stopped.");
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
