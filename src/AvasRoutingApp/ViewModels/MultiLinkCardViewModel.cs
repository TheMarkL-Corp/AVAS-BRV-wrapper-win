using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AvasRoutingApp.Sdvoe;

namespace AvasRoutingApp.ViewModels
{
    /// <summary>
    /// ViewModel representing an AVAS-223 TX pair for multi-link management,
    /// tracking companion online/offline status, executing mode switches,
    /// and running non-blocking 20-second hardware reboot countdowns.
    /// </summary>
    public class MultiLinkCardViewModel : ViewModelBase
    {
        private static readonly SolidColorBrush GreenBrush = new(Color.FromRgb(0x10, 0xB9, 0x81)); // Emerald-500
        private static readonly SolidColorBrush RedBrush = new(Color.FromRgb(0xEF, 0x44, 0x44));   // Red-500
        private static readonly SolidColorBrush AmberBrush = new(Color.FromRgb(0xF5, 0x9E, 0x0B)); // Amber-500
        private static readonly SolidColorBrush DualLinkBrush = new(Color.FromRgb(0x25, 0x63, 0xEB)); // Blue-600
        private static readonly SolidColorBrush SingleLinkBrush = new(Color.FromRgb(0x47, 0x55, 0x69)); // Slate-600
        private static readonly SolidColorBrush UnknownLinkBrush = new(Color.FromRgb(0x33, 0x41, 0x55)); // Slate-700
        private static readonly SolidColorBrush GrayBrush = new(Color.FromRgb(0x64, 0x74, 0x8B)); // Slate-500

        static MultiLinkCardViewModel()
        {
            GreenBrush.Freeze();
            RedBrush.Freeze();
            AmberBrush.Freeze();
            DualLinkBrush.Freeze();
            SingleLinkBrush.Freeze();
            UnknownLinkBrush.Freeze();
            GrayBrush.Freeze();
        }

        private string _primaryMac = string.Empty;
        private string _primaryName = string.Empty;
        private string _primaryIp = string.Empty;
        private string _linkMode = "UNKNOWN";
        private string _companionMac = "NONE";
        private string _companionName = string.Empty;
        private bool _companionIsActive = false;
        private bool _isRebooting = false;
        private int _rebootSecondsRemaining = 0;
        private double _rebootProgress = 0.0;
        private string _rebootStatusMessage = string.Empty;
        private bool _isBusy = false;

        private readonly IMultiLinkService _multiLinkService;
        private DispatcherTimer? _countdownTimer;
        private DateTime _rebootStartTime;
        private const double RebootDurationSeconds = 20.0;

        /// <summary>
        /// Event raised when a mode switch begins, allowing parent to coordinate preview pauses.
        /// </summary>
        public event Func<MultiLinkCardViewModel, string, Task>? ModeSwitchStarted;

        /// <summary>
        /// Event raised when the 20-second reboot completes, allowing parent to refresh streams.
        /// </summary>
        public event Func<MultiLinkCardViewModel, Task>? ModeSwitchCompleted;

        public string PrimaryMac
        {
            get => _primaryMac;
            set => SetProperty(ref _primaryMac, value);
        }

        public string PrimaryName
        {
            get => _primaryName;
            set => SetProperty(ref _primaryName, value);
        }

        public string PrimaryIp
        {
            get => _primaryIp;
            set => SetProperty(ref _primaryIp, value);
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
                    OnPropertyChanged(nameof(IsDualLinkSelected));
                    OnPropertyChanged(nameof(IsSingleLinkSelected));
                }
            }
        }

        public string LinkModeBadgeText => _linkMode.ToUpperInvariant() switch
        {
            "DUAL" => "DUAL-LINK (20G)",
            "SINGLE" => "SINGLE-LINK (10G)",
            _ => "UNKNOWN"
        };

        public SolidColorBrush LinkModeBrush => _linkMode.ToUpperInvariant() switch
        {
            "DUAL" => DualLinkBrush,
            "SINGLE" => SingleLinkBrush,
            _ => UnknownLinkBrush
        };

        public bool IsDualLinkSelected => string.Equals(_linkMode, "DUAL", StringComparison.OrdinalIgnoreCase);
        public bool IsSingleLinkSelected => string.Equals(_linkMode, "SINGLE", StringComparison.OrdinalIgnoreCase);

        public string CompanionMac
        {
            get => _companionMac;
            set
            {
                if (SetProperty(ref _companionMac, value))
                {
                    OnPropertyChanged(nameof(HasCompanion));
                    OnPropertyChanged(nameof(CompanionDisplayTitle));
                }
            }
        }

        public string CompanionName
        {
            get => _companionName;
            set
            {
                if (SetProperty(ref _companionName, value))
                {
                    OnPropertyChanged(nameof(CompanionDisplayTitle));
                }
            }
        }

        public bool CompanionIsActive
        {
            get => _companionIsActive;
            set
            {
                if (SetProperty(ref _companionIsActive, value))
                {
                    OnPropertyChanged(nameof(CompanionStatusText));
                    OnPropertyChanged(nameof(CompanionStatusBrush));
                }
            }
        }

        public bool HasCompanion => !string.IsNullOrWhiteSpace(_companionMac) &&
                                    !string.Equals(_companionMac, "NONE", StringComparison.OrdinalIgnoreCase);

        public string CompanionDisplayTitle
        {
            get
            {
                if (!HasCompanion) return "Companion: None Reported";
                return string.IsNullOrWhiteSpace(_companionName) ? _companionMac : $"{_companionName} ({_companionMac})";
            }
        }

        public string CompanionStatusText
        {
            get
            {
                if (!HasCompanion) return "NONE";
                return _companionIsActive ? "ONLINE" : "OFFLINE";
            }
        }

        public SolidColorBrush CompanionStatusBrush
        {
            get
            {
                if (!HasCompanion) return GrayBrush;
                return _companionIsActive ? GreenBrush : RedBrush;
            }
        }

        public bool IsRebooting
        {
            get => _isRebooting;
            set => SetProperty(ref _isRebooting, value);
        }

        public int RebootSecondsRemaining
        {
            get => _rebootSecondsRemaining;
            set => SetProperty(ref _rebootSecondsRemaining, value);
        }

        public double RebootProgress
        {
            get => _rebootProgress;
            set => SetProperty(ref _rebootProgress, value);
        }

        public string RebootStatusMessage
        {
            get => _rebootStatusMessage;
            set => SetProperty(ref _rebootStatusMessage, value);
        }

        public bool IsBusy
        {
            get => _isBusy;
            set
            {
                if (SetProperty(ref _isBusy, value))
                {
                    OnPropertyChanged(nameof(CanSwitchMode));
                }
            }
        }

        public bool CanSwitchMode => !_isBusy && !_isRebooting;

        public ICommand SetSingleModeCommand { get; }
        public ICommand SetDualModeCommand { get; }

        public MultiLinkCardViewModel(IMultiLinkService multiLinkService)
        {
            _multiLinkService = multiLinkService ?? throw new ArgumentNullException(nameof(multiLinkService));

            SetSingleModeCommand = new RelayCommand(async () => await ExecuteModeSwitchAsync("SINGLE"), () => CanSwitchMode);
            SetDualModeCommand = new RelayCommand(async () => await ExecuteModeSwitchAsync("DUAL"), () => CanSwitchMode);
        }

        /// <summary>
        /// Confirmation delegate for switching to DUAL mode while companion is offline.
        /// Defaults to MessageBox.Show, but can be overridden in tests or headless runners.
        /// </summary>
        public Func<string, string, bool> ConfirmOfflineCompanionSwitch { get; set; } = (msg, title) =>
        {
            return MessageBox.Show(msg, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
        };

        public async Task ExecuteModeSwitchAsync(string targetMode)
        {
            targetMode = targetMode.ToUpperInvariant();
            if (string.Equals(_linkMode, targetMode, StringComparison.OrdinalIgnoreCase))
            {
                return; // Already in target mode
            }

            if (!CanSwitchMode) return;

            // Warning Confirmation Dialog if switching to DUAL while companion is offline
            if (targetMode == "DUAL")
            {
                if (!HasCompanion || !CompanionIsActive)
                {
                    var msg = $"WARNING: The companion chip (chip_1) for {PrimaryName} ({PrimaryMac}) is currently OFFLINE or not detected.\n\n" +
                              "Dual-link mode requires both SFP+ A and SFP+ B cables connected and active.\n\n" +
                              "Do you want to proceed with switching to DUAL mode anyway?";

                    bool proceed = ConfirmOfflineCompanionSwitch?.Invoke(msg, "Companion Status Warning") ?? true;
                    if (!proceed)
                    {
                        return;
                    }
                }
            }

            IsBusy = true;
            IsRebooting = true;
            RebootStatusMessage = $"Applying {targetMode} mode... Sending commands to BlueRiver server.";

            // Notify parent to pause preview stream cleanly
            if (ModeSwitchStarted != null)
            {
                try
                {
                    await ModeSwitchStarted.Invoke(this, targetMode);
                }
                catch { }
            }

            // Execute commands in background
            bool success = false;
            try
            {
                success = await Task.Run(async () =>
                {
                    return await _multiLinkService.SetMultiLinkModeAsync(PrimaryMac, CompanionMac, targetMode);
                });
            }
            catch (Exception ex)
            {
                RebootStatusMessage = $"Error: {ex.Message}";
            }

            if (success)
            {
                LinkMode = targetMode;
                // Start 20-second non-blocking countdown
                StartRebootCountdown(targetMode);
            }
            else
            {
                IsBusy = false;
                IsRebooting = false;
                if (string.IsNullOrEmpty(RebootStatusMessage) || !RebootStatusMessage.StartsWith("Error"))
                {
                    RebootStatusMessage = $"Mode switch to {targetMode} failed.";
                }

                // If preview stream was paused, notify completion to resume/restore state
                if (ModeSwitchCompleted != null)
                {
                    try
                    {
                        await ModeSwitchCompleted.Invoke(this);
                    }
                    catch { }
                }
            }
        }

        private void StartRebootCountdown(string targetMode)
        {
            _rebootStartTime = DateTime.UtcNow;
            RebootSecondsRemaining = (int)RebootDurationSeconds;
            RebootProgress = 0.0;
            RebootStatusMessage = $"Rebooting encoder hardware in {targetMode} mode... ({RebootSecondsRemaining}s)";

            _countdownTimer?.Stop();
            _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _countdownTimer.Tick += CountdownTimer_Tick;
            _countdownTimer.Start();
        }

        private async void CountdownTimer_Tick(object? sender, EventArgs e)
        {
            var elapsed = (DateTime.UtcNow - _rebootStartTime).TotalSeconds;
            var remaining = Math.Max(0.0, RebootDurationSeconds - elapsed);

            RebootSecondsRemaining = (int)Math.Ceiling(remaining);
            RebootProgress = Math.Min(RebootDurationSeconds, elapsed);
            RebootStatusMessage = $"Hardware rebooting... {RebootSecondsRemaining}s remaining";

            if (remaining <= 0.0)
            {
                _countdownTimer?.Stop();
                _countdownTimer = null;
                IsRebooting = false;
                IsBusy = false;
                RebootStatusMessage = "Reboot complete. Re-connecting streams...";

                if (ModeSwitchCompleted != null)
                {
                    try
                    {
                        await ModeSwitchCompleted.Invoke(this);
                    }
                    catch { }
                }
            }
        }

        public void CancelCountdown()
        {
            _countdownTimer?.Stop();
            _countdownTimer = null;
            IsRebooting = false;
            IsBusy = false;
        }
    }
}
