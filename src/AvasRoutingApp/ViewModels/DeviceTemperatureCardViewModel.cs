using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AvasRoutingApp.Sdvoe;

namespace AvasRoutingApp.ViewModels
{
    /// <summary>
    /// ViewModel representing a single device temperature card (Encoder TX or Decoder RX),
    /// combining primary Chip 0 and companion Chip 1, displaying dual-source values
    /// (SDVoE REST API vs VoIP SDK), discrepancy alerts, and bold red styling if > 70°C.
    /// </summary>
    public class DeviceTemperatureCardViewModel : ViewModelBase
    {
        private static readonly SolidColorBrush RedBrush = new(Color.FromRgb(0xEF, 0x44, 0x44));      // #EF4444 Critical (>70°C)
        private static readonly SolidColorBrush AmberBrush = new(Color.FromRgb(0xF5, 0x9E, 0x0B));    // #F59E0B Warning (61-70°C)
        private static readonly SolidColorBrush GreenBrush = new(Color.FromRgb(0x10, 0xB9, 0x81));    // #10B981 Normal / Match
        private static readonly SolidColorBrush MutedBrush = new(Color.FromRgb(0x94, 0xA3, 0xB8));    // #94A3B8 Secondary
        private static readonly SolidColorBrush TxBrush = new(Color.FromRgb(0x3B, 0x82, 0xF6));       // #3B82F6 Blue for TX
        private static readonly SolidColorBrush RxBrush = new(Color.FromRgb(0x8B, 0x5C, 0xF6));       // #8B5CF6 Purple for RX
        private static readonly SolidColorBrush CardBorderNormal = new(Color.FromRgb(0x33, 0x41, 0x55));
        private static readonly SolidColorBrush CardBorderCritical = new(Color.FromRgb(0xEF, 0x44, 0x44));

        static DeviceTemperatureCardViewModel()
        {
            RedBrush.Freeze();
            AmberBrush.Freeze();
            GreenBrush.Freeze();
            MutedBrush.Freeze();
            TxBrush.Freeze();
            RxBrush.Freeze();
            CardBorderNormal.Freeze();
            CardBorderCritical.Freeze();
        }

        private DeviceTemperatureInfo _info;
        private bool _isIdCopied;

        public DeviceTemperatureCardViewModel(DeviceTemperatureInfo info)
        {
            _info = info ?? throw new ArgumentNullException(nameof(info));
            CopyDeviceIdCommand = new RelayCommand(ExecuteCopyDeviceId);
        }

        public DeviceTemperatureInfo Model => _info;

        public string DeviceId => _info.DeviceId;
        public string DeviceName => string.IsNullOrWhiteSpace(_info.DeviceName) ? _info.DeviceId : _info.DeviceName;
        public string DeviceType => _info.DeviceType;
        public bool IsTransmitter => _info.IsTransmitter;
        public bool IsReceiver => _info.IsReceiver;
        public string LinkMode => _info.LinkMode;
        public bool IsDualLink => _info.IsDualLink;
        public string IpAddress => _info.IpAddress;
        public bool IsActive => _info.IsActive;

        public Brush DeviceTypeBrush => IsTransmitter ? TxBrush : RxBrush;

        #region Chip 0

        public string Chip0Id => _info.Chip0Id;
        public int? Chip0SdvoeTemp => _info.Chip0SdvoeTemp;
        public int? Chip0VoipSdkTemp => _info.Chip0VoipSdkTemp;

        public string Chip0SdvoeTempText => _info.Chip0SdvoeTemp.HasValue ? $"{_info.Chip0SdvoeTemp.Value} °C" : "N/A";
        public string Chip0VoipSdkTempText => _info.Chip0VoipSdkTemp.HasValue ? $"{_info.Chip0VoipSdkTemp.Value} °C" : "N/A";

        public bool Chip0HasDiscrepancy => _info.Chip0HasDiscrepancy;

        public string Chip0DiscrepancyText
        {
            get
            {
                if (!_info.Chip0SdvoeTemp.HasValue || !_info.Chip0VoipSdkTemp.HasValue) return "N/A";
                int delta = _info.Chip0SdvoeTemp.Value - _info.Chip0VoipSdkTemp.Value;
                if (delta == 0) return "Match";
                return delta > 0 ? $"Δ +{delta}°C" : $"Δ {delta}°C";
            }
        }

        public Brush Chip0DiscrepancyBrush => Chip0HasDiscrepancy ? AmberBrush : GreenBrush;

        public bool Chip0IsCritical => (_info.Chip0SdvoeTemp > 70) || (_info.Chip0VoipSdkTemp > 70);
        public bool Chip0IsWarning => !Chip0IsCritical && (
            (_info.Chip0SdvoeTemp >= 61 && _info.Chip0SdvoeTemp <= 70) ||
            (_info.Chip0VoipSdkTemp >= 61 && _info.Chip0VoipSdkTemp <= 70));

        public Brush Chip0SdvoeForeground => GetTempBrush(_info.Chip0SdvoeTemp);
        public Brush Chip0VoipForeground => GetTempBrush(_info.Chip0VoipSdkTemp);

        public FontWeight Chip0SdvoeFontWeight => (_info.Chip0SdvoeTemp > 70) ? FontWeights.Bold : FontWeights.SemiBold;
        public FontWeight Chip0VoipFontWeight => (_info.Chip0VoipSdkTemp > 70) ? FontWeights.Bold : FontWeights.SemiBold;

        #endregion

        #region Chip 1 (Companion)

        public bool HasCompanion => _info.HasCompanion;
        public string CompanionDeviceId => _info.CompanionDeviceId;
        public string CompanionName => string.IsNullOrWhiteSpace(_info.CompanionName) ? "Companion (Chip 1)" : _info.CompanionName;
        public string Chip1Id => _info.Chip1Id;
        public int? Chip1SdvoeTemp => _info.Chip1SdvoeTemp;
        public int? Chip1VoipSdkTemp => _info.Chip1VoipSdkTemp;

        public string Chip1SdvoeTempText => _info.Chip1SdvoeTemp.HasValue ? $"{_info.Chip1SdvoeTemp.Value} °C" : "N/A";
        public string Chip1VoipSdkTempText => _info.Chip1VoipSdkTemp.HasValue ? $"{_info.Chip1VoipSdkTemp.Value} °C" : "N/A";

        public bool Chip1HasDiscrepancy => _info.Chip1HasDiscrepancy;

        public string Chip1DiscrepancyText
        {
            get
            {
                if (!_info.Chip1SdvoeTemp.HasValue || !_info.Chip1VoipSdkTemp.HasValue) return "N/A";
                int delta = _info.Chip1SdvoeTemp.Value - _info.Chip1VoipSdkTemp.Value;
                if (delta == 0) return "Match";
                return delta > 0 ? $"Δ +{delta}°C" : $"Δ {delta}°C";
            }
        }

        public Brush Chip1DiscrepancyBrush => Chip1HasDiscrepancy ? AmberBrush : GreenBrush;

        public bool Chip1IsCritical => (_info.Chip1SdvoeTemp > 70) || (_info.Chip1VoipSdkTemp > 70);
        public bool Chip1IsWarning => !Chip1IsCritical && (
            (_info.Chip1SdvoeTemp >= 61 && _info.Chip1SdvoeTemp <= 70) ||
            (_info.Chip1VoipSdkTemp >= 61 && _info.Chip1VoipSdkTemp <= 70));

        public Brush Chip1SdvoeForeground => GetTempBrush(_info.Chip1SdvoeTemp);
        public Brush Chip1VoipForeground => GetTempBrush(_info.Chip1VoipSdkTemp);

        public FontWeight Chip1SdvoeFontWeight => (_info.Chip1SdvoeTemp > 70) ? FontWeights.Bold : FontWeights.SemiBold;
        public FontWeight Chip1VoipFontWeight => (_info.Chip1VoipSdkTemp > 70) ? FontWeights.Bold : FontWeights.SemiBold;

        #endregion

        #region Overall Status

        public bool IsCritical => _info.IsCritical;
        public bool IsWarning => _info.IsWarning;
        public bool HasAnyDiscrepancy => _info.HasAnyDiscrepancy;

        public Brush CardBorderBrush => IsCritical ? CardBorderCritical : CardBorderNormal;

        public string PeakTemperatureText => _info.MaxTemperature.HasValue ? $"{_info.MaxTemperature.Value} °C" : "--";

        public bool IsIdCopied
        {
            get => _isIdCopied;
            private set => SetProperty(ref _isIdCopied, value);
        }

        public ICommand CopyDeviceIdCommand { get; }

        private void ExecuteCopyDeviceId()
        {
            try
            {
                Clipboard.SetText(DeviceId);
                IsIdCopied = true;
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                timer.Tick += (s, e) =>
                {
                    IsIdCopied = false;
                    timer.Stop();
                };
                timer.Start();
            }
            catch { }
        }

        #endregion

        public void Update(DeviceTemperatureInfo newInfo)
        {
            _info = newInfo ?? throw new ArgumentNullException(nameof(newInfo));
            OnPropertyChanged(string.Empty); // Refresh all bound UI elements
        }

        private static SolidColorBrush GetTempBrush(int? temp)
        {
            if (!temp.HasValue) return MutedBrush;
            if (temp.Value > 70) return RedBrush;       // Critical: > 70°C in Bold Red
            if (temp.Value >= 61) return AmberBrush;    // Warning: 61°C - 70°C in Amber
            return GreenBrush;                          // Normal: <= 60°C in Green/Normal
        }
    }
}
