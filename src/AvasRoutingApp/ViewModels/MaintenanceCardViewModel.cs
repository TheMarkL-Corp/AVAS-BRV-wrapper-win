using System;
using System.Windows.Input;
using System.Windows.Media;
using AvasRoutingApp.Sdvoe;

namespace AvasRoutingApp.ViewModels
{
    /// <summary>
    /// ViewModel representing an AVAS TX or RX endpoint in the dedicated Diagnostics/Maintenance panel.
    /// Exposes device identifiers, link mode, chip telemetry, and triggers hardware diagnostics.
    /// Strictly restricted to AVAS devices (Vendor ID 105).
    /// </summary>
    public class MaintenanceCardViewModel : ViewModelBase
    {
        private static readonly SolidColorBrush TxBrush = new(Color.FromRgb(0x3B, 0x82, 0xF6)); // #3B82F6 Blue for TX
        private static readonly SolidColorBrush RxBrush = new(Color.FromRgb(0x8B, 0x5C, 0xF6)); // #8B5CF6 Purple for RX
        private static readonly SolidColorBrush DualLinkBrush = new(Color.FromRgb(0x25, 0x63, 0xEB));
        private static readonly SolidColorBrush SingleLinkBrush = new(Color.FromRgb(0x47, 0x55, 0x69));

        static MaintenanceCardViewModel()
        {
            TxBrush.Freeze();
            RxBrush.Freeze();
            DualLinkBrush.Freeze();
            SingleLinkBrush.Freeze();
        }

        private readonly AvasDevice _device;
        private string _firmwareVersion = string.Empty;
        private string _hardwareVersion = string.Empty;
        private string _mcuIpAddress = string.Empty;
        private bool _isMacCopied;

        public MaintenanceCardViewModel(AvasDevice device)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            OpenDiagnosticsCommand = new RelayCommand(ExecuteOpenDiagnostics, () => IsMaintenanceAvailable);
            CopyMacCommand = new RelayCommand(ExecuteCopyMac);
        }

        public AvasDevice Device => _device;

        public string DeviceName => string.IsNullOrWhiteSpace(_device.DeviceName) ? _device.MacAddress : _device.DeviceName;
        public string MacAddress => _device.MacAddress;
        public string IpAddress => _device.IpAddress;
        public bool IsTransmitter => _device.IsTransmitter;
        public bool IsReceiver => _device.IsReceiver;
        public DeviceFamily Family => _device.Family;

        public string DeviceTypeBadge => _device.IsTransmitter ? "TX" : "RX";
        public SolidColorBrush DeviceTypeBrush => _device.IsTransmitter ? TxBrush : RxBrush;

        public string LinkMode => string.IsNullOrWhiteSpace(_device.LinkMode) ? "SINGLE" : _device.LinkMode.ToUpperInvariant();
        public bool IsDualLink => string.Equals(LinkMode, "DUAL", StringComparison.OrdinalIgnoreCase);
        public SolidColorBrush LinkModeBrush => IsDualLink ? DualLinkBrush : SingleLinkBrush;

        public string CompanionMac
        {
            get => !string.IsNullOrEmpty(_device.CompanionMac) && !string.Equals(_device.CompanionMac, "NONE", StringComparison.OrdinalIgnoreCase)
                ? _device.CompanionMac
                : "N/A";
            set
            {
                if (_device.CompanionMac != value)
                {
                    _device.CompanionMac = value ?? "NONE";
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasCompanion));
                }
            }
        }

        public bool HasCompanion => !string.IsNullOrEmpty(_device.CompanionMac) && !string.Equals(_device.CompanionMac, "NONE", StringComparison.OrdinalIgnoreCase);

        public bool IsMaintenanceAvailable => _device.Family == DeviceFamily.Avas;

        public string FirmwareVersion
        {
            get => _firmwareVersion;
            set => SetProperty(ref _firmwareVersion, value);
        }

        public string HardwareVersion
        {
            get => _hardwareVersion;
            set => SetProperty(ref _hardwareVersion, value);
        }

        public string McuIpAddress
        {
            get => _mcuIpAddress;
            set => SetProperty(ref _mcuIpAddress, value);
        }

        public bool IsMacCopied
        {
            get => _isMacCopied;
            set => SetProperty(ref _isMacCopied, value);
        }

        public ICommand OpenDiagnosticsCommand { get; }
        public ICommand CopyMacCommand { get; }

        public Action<MaintenanceCardViewModel>? OpenDiagnosticsAction { get; set; }

        private void ExecuteOpenDiagnostics()
        {
            if (!IsMaintenanceAvailable) return;
            OpenDiagnosticsAction?.Invoke(this);
        }

        private async void ExecuteCopyMac()
        {
            if (string.IsNullOrEmpty(_device.MacAddress)) return;

            try
            {
                System.Windows.Clipboard.SetText(_device.MacAddress);
                IsMacCopied = true;
                await System.Threading.Tasks.Task.Delay(2000);
                IsMacCopied = false;
            }
            catch { }
        }
    }
}
