using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AvasRoutingApp.Sdvoe
{
    /// <summary>
    /// Service contract for querying device junction temperatures from SDVoE REST API
    /// and Advantech VoIP SDK (DEVICE_STATUS), pairing companions, and computing discrepancies.
    /// </summary>
    public interface ITemperatureService
    {
        /// <summary>
        /// Queries all discovered Encoders (TX) and Decoders (RX) for temperature readings across both sources.
        /// </summary>
        Task<IReadOnlyList<DeviceTemperatureInfo>> QueryAllTemperaturesAsync(CancellationToken ct = default);

        /// <summary>
        /// Queries an individual device (and optional companion) for dual-source temperatures.
        /// </summary>
        Task<DeviceTemperatureInfo?> QueryDeviceTemperatureAsync(string primaryMac, string? companionMac = null, CancellationToken ct = default);
    }
}
