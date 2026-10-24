using System;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AvasRoutingApp.Rtp;
using AvasRoutingApp.Sdvoe;

namespace AvasRoutingApp.ViewModels
{
    /// <summary>
    /// ViewModel representing a single AVAS-223 chip_0 encoder preview card.
    /// Manages the video viewport WriteableBitmap, device telemetry, link mode badge, and color-coded FPS indicator.
    /// </summary>
    public class EncoderCardViewModel : ViewModelBase, IDisposable
    {
        private static readonly SolidColorBrush GreenBrush = new(Color.FromRgb(0x4C, 0xAF, 0x50));
        private static readonly SolidColorBrush AmberBrush = new(Color.FromRgb(0xFF, 0xA0, 0x00));
        private static readonly SolidColorBrush DualLinkBrush = new(Color.FromRgb(0x25, 0x63, 0xEB));
        private static readonly SolidColorBrush SingleLinkBrush = new(Color.FromRgb(0x47, 0x55, 0x69));
        private static readonly SolidColorBrush UnknownLinkBrush = new(Color.FromRgb(0x33, 0x41, 0x55));
        private static readonly SolidColorBrush OnlineGreenBrush = new(Color.FromRgb(0x10, 0xB9, 0x81));
        private static readonly SolidColorBrush WarningAmberBrush = new(Color.FromRgb(0xF5, 0x9E, 0x0B));
        private static readonly SolidColorBrush NeutralGrayBrush = new(Color.FromRgb(0x64, 0x74, 0x8B));

        static EncoderCardViewModel()
        {
            GreenBrush.Freeze();
            AmberBrush.Freeze();
            DualLinkBrush.Freeze();
            SingleLinkBrush.Freeze();
            UnknownLinkBrush.Freeze();
            OnlineGreenBrush.Freeze();
            WarningAmberBrush.Freeze();
            NeutralGrayBrush.Freeze();
        }

        private string _macAddress = string.Empty;
        private string _deviceName = string.Empty;
        private DeviceActivationState _activationState = DeviceActivationState.Unknown;
        private DeviceFamily _family = DeviceFamily.Avas;
        private string _firmwareVersion = string.Empty;
        private string _hardwareVersion = string.Empty;
        private string _factorySerialNumber = string.Empty;
        private string _unicastIp = string.Empty;
        private string _multicastIp = string.Empty;
        private int _port = 5000;
        private string _resolution = "320x180";
        private double _currentFps = 0.0;
        private bool _isStreaming = false;
        private bool _isWaitingForStream = true;
        private string _statusMessage = "Initializing...";
        private WriteableBitmap? _previewBitmap;
        private byte[]? _lastRenderedRgbFrame;
        private int _frameUpdateCount;
        private IRtpStreamReceiver? _receiver;
        private string _linkMode = "UNKNOWN";
        private string _companionMac = "NONE";
        private string _companionName = string.Empty;
        private bool _companionIsActive = false;
        private bool _isMacCopied;
        private bool _isMulticastCopied;
        private int _selectedChipIndex = 0;

        public int SelectedChipIndex
        {
            get => _selectedChipIndex;
            set
            {
                if (SetProperty(ref _selectedChipIndex, value))
                {
                    OnPropertyChanged(nameof(IsChip0Selected));
                    OnPropertyChanged(nameof(IsChip1Selected));
                    OnPropertyChanged(nameof(ActiveStreamingMac));
                }
            }
        }

        public bool IsChip0Selected => _selectedChipIndex == 0;
        public bool IsChip1Selected => _selectedChipIndex == 1;

        public bool CanSelectChip1 => !IsDualLink &&
                                      !string.IsNullOrEmpty(_companionMac) &&
                                      !string.Equals(_companionMac, "NONE", StringComparison.OrdinalIgnoreCase);

        public string ActiveStreamingMac => (_selectedChipIndex == 1 && CanSelectChip1)
            ? _companionMac
            : _macAddress;

        public ICommand SelectChip0Command { get; }
        public ICommand SelectChip1Command { get; }
        public Func<EncoderCardViewModel, int, Task>? OnChipSelectionChanged { get; set; }

        public bool IsMacCopied
        {
            get => _isMacCopied;
            set => SetProperty(ref _isMacCopied, value);
        }

        public bool IsMulticastCopied
        {
            get => _isMulticastCopied;
            set => SetProperty(ref _isMulticastCopied, value);
        }

        public System.Windows.Input.ICommand CopyMacCommand { get; }

        #region Deprecated UI Properties (Retained for Backwards Compatibility)

        [Obsolete("Removed from preview card UI in v1.7.0. Multicast endpoint copied from Settings.")]
        public System.Windows.Input.ICommand CopyMulticastCommand { get; }

        [Obsolete("Removed from preview card UI in v1.7.0. Diagnostics moved to Settings > Advanced.")]
        public System.Windows.Input.ICommand OpenDiagnosticsCommand { get; }

        [Obsolete("Removed from preview card UI in v1.7.0. Diagnostics moved to Settings > Advanced.")]
        public Action<EncoderCardViewModel>? OpenDiagnosticsAction { get; set; }

        [Obsolete("Removed from preview card UI in v1.7.0.")]
        public bool ShowActivationBadge => _family == DeviceFamily.Avas;

        [Obsolete("Removed from preview card UI in v1.7.0.")]
        public string BadgeText => "chip_0 (TX)";

        [Obsolete("Removed from preview card UI in v1.7.0.")]
        public bool IsMaintenanceAvailable => _family == DeviceFamily.Avas;

        [Obsolete("Removed from preview card UI in v1.7.0.")]
        public bool IsActivated => _activationState == DeviceActivationState.Activated;

        [Obsolete("Removed from preview card UI in v1.7.0.")]
        public string ActivationBadgeText => _activationState switch
        {
            DeviceActivationState.Activated => "Activated",
            DeviceActivationState.Unactivated => "Unactivated",
            _ => "Checking..."
        };

        [Obsolete("Removed from preview card UI in v1.7.0.")]
        public SolidColorBrush ActivationBadgeBrush => _activationState switch
        {
            DeviceActivationState.Activated => OnlineGreenBrush,
            DeviceActivationState.Unactivated => WarningAmberBrush,
            _ => NeutralGrayBrush
        };

        #endregion

        public EncoderCardViewModel()
        {
            CopyMacCommand = new RelayCommand(ExecuteCopyMac);
#pragma warning disable CS0618 // Type or member is obsolete
            CopyMulticastCommand = new RelayCommand(ExecuteCopyMulticast);
            OpenDiagnosticsCommand = new RelayCommand(ExecuteOpenDiagnostics, () => IsMaintenanceAvailable);
            SelectChip0Command = new RelayCommand(async () => await SetSelectedChipAsync(0));
            SelectChip1Command = new RelayCommand(async () => await SetSelectedChipAsync(1), () => CanSelectChip1);
#pragma warning restore CS0618 // Type or member is obsolete
        }

        private void ExecuteOpenDiagnostics()
        {
#pragma warning disable CS0618 // Type or member is obsolete
            if (!IsMaintenanceAvailable) return;
            OpenDiagnosticsAction?.Invoke(this);
#pragma warning restore CS0618 // Type or member is obsolete
        }

        private async Task SetSelectedChipAsync(int chipIndex)
        {
            if (chipIndex == 1 && !CanSelectChip1) return;
            if (_selectedChipIndex == chipIndex) return;
            SelectedChipIndex = chipIndex;
            if (OnChipSelectionChanged != null)
            {
                await OnChipSelectionChanged(this, chipIndex);
            }
        }

        private async void ExecuteCopyMac()
        {
            if (string.IsNullOrEmpty(_macAddress)) return;
            try
            {
                System.Windows.Clipboard.SetText(_macAddress);
                IsMacCopied = true;
                await System.Threading.Tasks.Task.Delay(1500);
                IsMacCopied = false;
            }
            catch
            {
                // Clipboard exceptions in sandbox/testing environments
            }
        }

        private async void ExecuteCopyMulticast()
        {
            if (string.IsNullOrEmpty(MulticastEndpoint)) return;
            try
            {
                System.Windows.Clipboard.SetText(MulticastEndpoint);
                IsMulticastCopied = true;
                await System.Threading.Tasks.Task.Delay(1500);
                IsMulticastCopied = false;
            }
            catch
            {
                // Clipboard exceptions in sandbox/testing environments
            }
        }

        public string MacAddress
        {
            get => _macAddress;
            set
            {
                if (SetProperty(ref _macAddress, value))
                {
                    OnPropertyChanged(nameof(ActiveStreamingMac));
                }
            }
        }

        public string DeviceName
        {
            get => _deviceName;
            set => SetProperty(ref _deviceName, value);
        }

        public string UnicastIp
        {
            get => _unicastIp;
            set => SetProperty(ref _unicastIp, value);
        }

        public string MulticastIp
        {
            get => _multicastIp;
            set
            {
                if (SetProperty(ref _multicastIp, value))
                {
                    OnPropertyChanged(nameof(MulticastEndpoint));
                }
            }
        }

        public int Port
        {
            get => _port;
            set
            {
                if (SetProperty(ref _port, value))
                {
                    OnPropertyChanged(nameof(MulticastEndpoint));
                }
            }
        }

        public string MulticastEndpoint => string.IsNullOrEmpty(_multicastIp) ? "Unallocated" : $"{_multicastIp}:{_port}";

        public string Resolution
        {
            get => _resolution;
            set => SetProperty(ref _resolution, value);
        }

        public double CurrentFps
        {
            get => _currentFps;
            set
            {
                if (SetProperty(ref _currentFps, value))
                {
                    OnPropertyChanged(nameof(FpsDisplay));
                    OnPropertyChanged(nameof(IsFpsHealthy));
                    OnPropertyChanged(nameof(FpsColorHex));
                    OnPropertyChanged(nameof(FpsBrush));
                }
            }
        }

        public string FpsDisplay => $"{_currentFps:F1} FPS";

        public bool IsFpsHealthy => _currentFps >= 1.0;

        /// <summary>
        /// Visual color-coding: Green (#4CAF50) when >= 1.0 FPS, Amber (#FFA000) when < 1.0 FPS.
        /// </summary>
        public string FpsColorHex => _currentFps >= 1.0 ? "#4CAF50" : "#FFA000";

        /// <summary>
        /// Frozen SolidColorBrush for direct binding to WPF UI elements.
        /// </summary>
        public SolidColorBrush FpsBrush => _currentFps >= 1.0 ? GreenBrush : AmberBrush;

        public bool IsStreaming
        {
            get => _isStreaming;
            set => SetProperty(ref _isStreaming, value);
        }

        public bool IsWaitingForStream
        {
            get => _isWaitingForStream;
            set => SetProperty(ref _isWaitingForStream, value);
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        public WriteableBitmap? PreviewBitmap
        {
            get => _previewBitmap;
            set => SetProperty(ref _previewBitmap, value);
        }

        public byte[]? LastRenderedRgbFrame
        {
            get => _lastRenderedRgbFrame;
            set => SetProperty(ref _lastRenderedRgbFrame, value);
        }

        public int FrameUpdateCount
        {
            get => _frameUpdateCount;
            set => SetProperty(ref _frameUpdateCount, value);
        }

        public string LinkMode
        {
            get => _linkMode;
            set
            {
                if (SetProperty(ref _linkMode, value))
                {
                    OnPropertyChanged(nameof(LinkModeBadgeText));
                    OnPropertyChanged(nameof(LinkModeBrush));
                    OnPropertyChanged(nameof(IsDualLink));
                    OnPropertyChanged(nameof(ShowLinkModeBadge));
                    OnPropertyChanged(nameof(CanSelectChip1));
                    OnPropertyChanged(nameof(ActiveStreamingMac));
                }
            }
        }

        public string LinkModeBadgeText => _linkMode.ToUpperInvariant() switch
        {
            "DUAL" => "Dual Link",
            "SINGLE" => "Single Link",
            _ => "Unknown Link"
        };

        public SolidColorBrush LinkModeBrush => _linkMode.ToUpperInvariant() switch
        {
            "DUAL" => DualLinkBrush,
            "SINGLE" => SingleLinkBrush,
            _ => UnknownLinkBrush
        };

        public bool ShowLinkModeBadge => string.Equals(_linkMode, "DUAL", StringComparison.OrdinalIgnoreCase) ||
                                         string.Equals(_linkMode, "SINGLE", StringComparison.OrdinalIgnoreCase);

        public bool IsDualLink => string.Equals(_linkMode, "DUAL", StringComparison.OrdinalIgnoreCase);

        public string CompanionMac
        {
            get => _companionMac;
            set
            {
                if (SetProperty(ref _companionMac, value))
                {
                    OnPropertyChanged(nameof(CanSelectChip1));
                    OnPropertyChanged(nameof(ActiveStreamingMac));
                }
            }
        }

        public string CompanionName
        {
            get => _companionName;
            set => SetProperty(ref _companionName, value);
        }

        public bool CompanionIsActive
        {
            get => _companionIsActive;
            set => SetProperty(ref _companionIsActive, value);
        }

        public DeviceActivationState ActivationState
        {
            get => _activationState;
            set
            {
                if (SetProperty(ref _activationState, value))
                {
#pragma warning disable CS0618 // Type or member is obsolete
                    OnPropertyChanged(nameof(IsActivated));
                    OnPropertyChanged(nameof(ActivationBadgeText));
                    OnPropertyChanged(nameof(ActivationBadgeBrush));
#pragma warning restore CS0618 // Type or member is obsolete
                    OnPropertyChanged(nameof(McuTooltipText));
                }
            }
        }

        public DeviceFamily Family
        {
            get => _family;
            set
            {
                if (SetProperty(ref _family, value))
                {
#pragma warning disable CS0618 // Type or member is obsolete
                    OnPropertyChanged(nameof(BadgeText));
                    OnPropertyChanged(nameof(IsMaintenanceAvailable));
                    OnPropertyChanged(nameof(ShowActivationBadge));
#pragma warning restore CS0618 // Type or member is obsolete
                }
            }
        }

        public string FirmwareVersion
        {
            get => _firmwareVersion;
            set
            {
                if (SetProperty(ref _firmwareVersion, value))
                {
                    OnPropertyChanged(nameof(McuTooltipText));
                }
            }
        }

        public string HardwareVersion
        {
            get => _hardwareVersion;
            set
            {
                if (SetProperty(ref _hardwareVersion, value))
                {
                    OnPropertyChanged(nameof(McuTooltipText));
                }
            }
        }

        public string FactorySerialNumber
        {
            get => _factorySerialNumber;
            set
            {
                if (SetProperty(ref _factorySerialNumber, value))
                {
                    OnPropertyChanged(nameof(McuTooltipText));
                }
            }
        }

        public string McuTooltipText
        {
            get
            {
                var sb = new StringBuilder();
#pragma warning disable CS0618 // Type or member is obsolete
                sb.AppendLine($"Activation: {ActivationBadgeText}");
#pragma warning restore CS0618 // Type or member is obsolete
                if (!string.IsNullOrWhiteSpace(_firmwareVersion)) sb.AppendLine($"MCU FW: {_firmwareVersion}");
                if (!string.IsNullOrWhiteSpace(_hardwareVersion)) sb.AppendLine($"Hardware: {_hardwareVersion}");
                if (!string.IsNullOrWhiteSpace(_factorySerialNumber)) sb.AppendLine($"Serial No: {_factorySerialNumber}");
                return sb.ToString().TrimEnd();
            }
        }

        public IRtpStreamReceiver? Receiver
        {
            get => _receiver;
            set => _receiver = value;
        }

        /// <summary>
        /// Attaches an RTP receiver to automatically update this card's bitmap, frames, and FPS telemetry.
        /// </summary>
        public void AttachReceiver(IRtpStreamReceiver receiver)
        {
            DetachReceiver();
            _receiver = receiver ?? throw new ArgumentNullException(nameof(receiver));

            _receiver.FrameReady += OnFrameReady;
            _receiver.FpsUpdated += OnFpsUpdated;
            _receiver.BitmapUpdated += OnBitmapUpdated;

            if (_receiver.Bitmap != null)
            {
                PreviewBitmap = _receiver.Bitmap;
            }

            IsStreaming = true;
            IsWaitingForStream = true;
            StatusMessage = $"Connecting to {MulticastEndpoint}...";
        }

        /// <summary>
        /// Detaches the currently attached receiver and unhooks events.
        /// </summary>
        public void DetachReceiver()
        {
            if (_receiver != null)
            {
                _receiver.FrameReady -= OnFrameReady;
                _receiver.FpsUpdated -= OnFpsUpdated;
                _receiver.BitmapUpdated -= OnBitmapUpdated;
                _receiver = null;
            }
        }

        public void UpdateFrame(byte[] rgb, int width, int height)
        {
            LastRenderedRgbFrame = rgb;
            FrameUpdateCount++;
            Resolution = $"{width}x{height}";
            IsWaitingForStream = false;
            StatusMessage = "Streaming";
        }

        public void UpdateFps(double fps)
        {
            CurrentFps = fps;
        }

        public void UpdateBitmap(WriteableBitmap bitmap)
        {
            PreviewBitmap = bitmap;
        }

        private void OnFrameReady(byte[] rgb, int width, int height)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.HasShutdownStarted && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(() => UpdateFrame(rgb, width, height)));
            }
            else
            {
                UpdateFrame(rgb, width, height);
            }
        }

        private void OnFpsUpdated(double fps)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.HasShutdownStarted && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(() => UpdateFps(fps)));
            }
            else
            {
                UpdateFps(fps);
            }
        }

        private void OnBitmapUpdated(WriteableBitmap bitmap)
        {
            UpdateBitmap(bitmap);
        }

        public void Dispose()
        {
            DetachReceiver();
            _previewBitmap = null;
            _lastRenderedRgbFrame = null;
            _isStreaming = false;
            _currentFps = 0.0;
        }
    }
}
