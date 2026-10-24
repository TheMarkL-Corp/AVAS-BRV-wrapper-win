using System;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;
using AvasRoutingApp.Logging;
using AvasRoutingApp.Sdvoe;

namespace AvasRoutingApp.ViewModels
{
    public enum TargetChipSelection
    {
        Chip0,
        Chip1,
        Both
    }

    /// <summary>
    /// ViewModel for AVAS-223 Hardware Diagnostics and Maintenance.
    /// Provides independent silicon (AVP) and MCU factory reset operations, chip targeting, and execution logging.
    /// Strictly restricted to AVAS devices (Vendor ID 105).
    /// </summary>
    public class MaintenanceViewModel : ViewModelBase
    {
        private AvasDevice _device;
        private readonly IMulticastController _multicastController;
        private readonly IReadOnlyList<AvasDevice> _availableDevices;
        private TargetChipSelection _selectedTarget = TargetChipSelection.Chip0;
        private bool _isBusy;
        private string _statusLog = string.Empty;

        public IReadOnlyList<AvasDevice> AvailableDevices => _availableDevices;

        public AvasDevice SelectedDevice
        {
            get => _device;
            set
            {
                if (value != null && !ReferenceEquals(_device, value))
                {
                    _device = value;
                    OnPropertyChanged(nameof(SelectedDevice));
                    OnPropertyChanged(nameof(DeviceName));
                    OnPropertyChanged(nameof(Chip0Mac));
                    OnPropertyChanged(nameof(Chip1Mac));
                    OnPropertyChanged(nameof(LinkMode));
                    OnPropertyChanged(nameof(IsChip1Available));
                    OnPropertyChanged(nameof(IsTransmitter));
                    OnPropertyChanged(nameof(IsReceiver));
                    OnPropertyChanged(nameof(DeviceTypeBadge));
                    SelectedTarget = TargetChipSelection.Chip0;
                    AppendLog($"Target device switched to {DeviceName} ({Chip0Mac}). Link mode: {LinkMode}.");
                }
            }
        }

        public string DeviceName => string.IsNullOrWhiteSpace(_device.DeviceName) ? _device.MacAddress : _device.DeviceName;
        public string Chip0Mac => _device.MacAddress;
        public string Chip1Mac => IsChip1Available ? _device.CompanionMac : "N/A";
        public string LinkMode => _device.LinkMode;
        public bool IsTransmitter => _device.IsTransmitter;
        public bool IsReceiver => _device.IsReceiver;
        public string DeviceTypeBadge => _device.IsTransmitter ? "TX" : "RX";
        public bool HasMultipleDevices => _availableDevices.Count > 1;

        public bool IsChip1Available => !string.IsNullOrEmpty(_device.CompanionMac) &&
                                        !string.Equals(_device.CompanionMac, "NONE", StringComparison.OrdinalIgnoreCase);

        public TargetChipSelection SelectedTarget
        {
            get => _selectedTarget;
            set
            {
                if (value == TargetChipSelection.Chip1 && !IsChip1Available)
                {
                    value = TargetChipSelection.Chip0;
                }
                if (value == TargetChipSelection.Both && !IsChip1Available)
                {
                    value = TargetChipSelection.Chip0;
                }
                SetProperty(ref _selectedTarget, value);
            }
        }

        public bool IsBusy
        {
            get => _isBusy;
            set => SetProperty(ref _isBusy, value);
        }

        public string StatusLog
        {
            get => _statusLog;
            set => SetProperty(ref _statusLog, value);
        }

        public Func<bool>? ConfirmActionCallback { get; set; }

        public ICommand FactoryResetAvpCommand { get; }
        public ICommand FactoryResetMcuCommand { get; }
        public ICommand RebootCommand { get; }
        public ICommand RebootMcuCommand { get; }

        public MaintenanceViewModel(AvasDevice device, IMulticastController multicastController, IReadOnlyList<AvasDevice>? availableDevices = null)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _multicastController = multicastController ?? throw new ArgumentNullException(nameof(multicastController));

            if (availableDevices != null && availableDevices.Count > 0)
            {
                _availableDevices = availableDevices;
            }
            else
            {
                _availableDevices = new List<AvasDevice> { device };
            }

            FactoryResetAvpCommand = new RelayCommand(async () => await ExecuteFactoryResetAvpAsync(), () => !IsBusy);
            FactoryResetMcuCommand = new RelayCommand(async () => await ExecuteFactoryResetMcuAsync(), () => !IsBusy);
            RebootCommand = new RelayCommand(async () => await ExecuteRebootAsync(), () => !IsBusy);
            RebootMcuCommand = new RelayCommand(async () => await ExecuteRebootMcuAsync(), () => !IsBusy);

            AppendLog($"Initialized Diagnostics panel for {DeviceName} ({Chip0Mac}) [{DeviceTypeBadge}]. Link mode: {LinkMode}.");
        }

        private void AppendLog(string message)
        {
            var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
            if (string.IsNullOrEmpty(_statusLog))
            {
                StatusLog = line;
            }
            else
            {
                StatusLog += Environment.NewLine + line;
            }
            AppLogger.Info("Maintenance", message);
        }

        public async Task ExecuteFactoryResetAvpAsync()
        {
            if (ConfirmActionCallback != null && !ConfirmActionCallback())
            {
                AppendLog("AVP Factory Reset aborted by user.");
                return;
            }

            IsBusy = true;
            try
            {
                AppendLog($"Starting AVP silicon factory reset (Target: {_selectedTarget})...");
                if (_selectedTarget == TargetChipSelection.Chip0 || _selectedTarget == TargetChipSelection.Both)
                {
                    AppendLog($"Resetting Chip 0 ({Chip0Mac})...");
                    bool ok0 = await _multicastController.FactoryResetAvpAsync(_device, Chip0Mac);
                    AppendLog(ok0 ? "Chip 0 AVP reset SUCCESS." : "Chip 0 AVP reset FAILED.");
                }

                if (_selectedTarget == TargetChipSelection.Chip1 || _selectedTarget == TargetChipSelection.Both)
                {
                    if (IsChip1Available)
                    {
                        AppendLog($"Resetting Chip 1 ({Chip1Mac})...");
                        bool ok1 = await _multicastController.FactoryResetAvpAsync(_device, Chip1Mac);
                        AppendLog(ok1 ? "Chip 1 AVP reset SUCCESS." : "Chip 1 AVP reset FAILED.");
                    }
                }
            }
            catch (Exception ex)
            {
                AppendLog($"AVP reset exception: {ex.Message}");
            }
            finally
            {
                IsBusy = false;
            }
        }

        public async Task ExecuteFactoryResetMcuAsync()
        {
            if (ConfirmActionCallback != null && !ConfirmActionCallback())
            {
                AppendLog("MCU Factory Reset aborted by user.");
                return;
            }

            IsBusy = true;
            try
            {
                AppendLog($"Starting MCU factory reset via Chip 0 ({Chip0Mac})...");
                bool ok = await _multicastController.FactoryResetMcuAsync(_device);
                AppendLog(ok ? "MCU factory reset SUCCESS (EEPROM restored to factory defaults)." : "MCU factory reset FAILED.");
            }
            catch (Exception ex)
            {
                AppendLog($"MCU reset exception: {ex.Message}");
            }
            finally
            {
                IsBusy = false;
            }
        }

        public async Task ExecuteRebootAsync()
        {
            if (ConfirmActionCallback != null && !ConfirmActionCallback())
            {
                AppendLog("Reboot aborted by user.");
                return;
            }

            IsBusy = true;
            try
            {
                AppendLog($"Starting device reboot (Target: {_selectedTarget})...");
                if (_selectedTarget == TargetChipSelection.Chip0 || _selectedTarget == TargetChipSelection.Both)
                {
                    AppendLog($"Rebooting Chip 0 ({Chip0Mac})...");
                    bool ok0 = await _multicastController.RebootChipAsync(_device, Chip0Mac);
                    AppendLog(ok0 ? "Chip 0 reboot SUCCESS." : "Chip 0 reboot FAILED.");
                }

                if (_selectedTarget == TargetChipSelection.Chip1 || _selectedTarget == TargetChipSelection.Both)
                {
                    if (IsChip1Available)
                    {
                        AppendLog($"Rebooting Chip 1 ({Chip1Mac})...");
                        bool ok1 = await _multicastController.RebootChipAsync(_device, Chip1Mac);
                        AppendLog(ok1 ? "Chip 1 reboot SUCCESS." : "Chip 1 reboot FAILED.");
                    }
                }
            }
            catch (Exception ex)
            {
                AppendLog($"Reboot exception: {ex.Message}");
            }
            finally
            {
                IsBusy = false;
            }
        }

        public async Task ExecuteRebootMcuAsync()
        {
            if (ConfirmActionCallback != null && !ConfirmActionCallback())
            {
                AppendLog("MCU Reboot aborted by user.");
                return;
            }

            IsBusy = true;
            try
            {
                AppendLog($"Starting MCU reboot via Chip 0 ({Chip0Mac})...");
                bool ok = await _multicastController.RebootMcuAsync(_device);
                AppendLog(ok ? "MCU reboot SUCCESS (watchdog reset initiated)." : "MCU reboot FAILED.");
            }
            catch (Exception ex)
            {
                AppendLog($"MCU reboot exception: {ex.Message}");
            }
            finally
            {
                IsBusy = false;
            }
        }
    }
}
