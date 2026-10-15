namespace AvasRoutingApp.Sdvoe
{
    /// <summary>
    /// Represents the hardware activation and licensing status of an AVAS SDVoE device.
    /// </summary>
    public enum DeviceActivationState
    {
        Unknown,
        Activated,
        Unactivated
    }

    /// <summary>
    /// Represents hardware microcontroller (MCU) device telemetry and activation details.
    /// </summary>
    public class McuDeviceInfo
    {
        public string MacAddress { get; set; } = string.Empty;
        public DeviceActivationState ActivationState { get; set; } = DeviceActivationState.Unknown;
        public string FirmwareVersion { get; set; } = string.Empty;
        public string HardwareVersion { get; set; } = string.Empty;
        public string FactorySerialNumber { get; set; } = string.Empty;
        public string McuIpAddress { get; set; } = string.Empty;
    }
}
