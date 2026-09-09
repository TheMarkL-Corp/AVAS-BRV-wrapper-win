using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AvasRoutingApp.Rtp;

namespace AvasRoutingApp.ViewModels
{
    /// <summary>
    /// ViewModel representing a single AVAS-223 chip_0 encoder preview card.
    /// Manages the video viewport WriteableBitmap, device telemetry, and color-coded FPS indicator.
    /// </summary>
    public class EncoderCardViewModel : ViewModelBase, IDisposable
    {
        private static readonly SolidColorBrush GreenBrush = new(Color.FromRgb(0x4C, 0xAF, 0x50));
        private static readonly SolidColorBrush AmberBrush = new(Color.FromRgb(0xFF, 0xA0, 0x00));
        private static readonly SolidColorBrush DualLinkBrush = new(Color.FromRgb(0x25, 0x63, 0xEB));
        private static readonly SolidColorBrush SingleLinkBrush = new(Color.FromRgb(0x47, 0x55, 0x69));
        private static readonly SolidColorBrush UnknownLinkBrush = new(Color.FromRgb(0x33, 0x41, 0x55));

        static EncoderCardViewModel()
        {
            GreenBrush.Freeze();
            AmberBrush.Freeze();
            DualLinkBrush.Freeze();
            SingleLinkBrush.Freeze();
            UnknownLinkBrush.Freeze();
        }

        private string _macAddress = string.Empty;
        private string _deviceName = string.Empty;
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

        public string MacAddress
        {
            get => _macAddress;
            set => SetProperty(ref _macAddress, value);
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
                }
            }
        }

        public string LinkModeBadgeText => _linkMode.ToUpperInvariant() switch
        {
            "DUAL" => "DUAL",
            "SINGLE" => "SINGLE",
            _ => "---"
        };

        public SolidColorBrush LinkModeBrush => _linkMode.ToUpperInvariant() switch
        {
            "DUAL" => DualLinkBrush,
            "SINGLE" => SingleLinkBrush,
            _ => UnknownLinkBrush
        };

        public bool IsDualLink => string.Equals(_linkMode, "DUAL", StringComparison.OrdinalIgnoreCase);

        public string CompanionMac
        {
            get => _companionMac;
            set => SetProperty(ref _companionMac, value);
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

            if (_receiver is RtpMulticastReceiver mcastReceiver && mcastReceiver.Bitmap != null)
            {
                PreviewBitmap = mcastReceiver.Bitmap;
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
