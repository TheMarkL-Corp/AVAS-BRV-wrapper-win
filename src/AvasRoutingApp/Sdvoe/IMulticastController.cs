using System.Threading;
using System.Threading.Tasks;

namespace AvasRoutingApp.Sdvoe
{
    /// <summary>
    /// Service contract for multicast allocation and stream control.
    /// Conforms to PROJECT.md § Interface Contracts.
    /// </summary>
    public interface IMulticastController
    {
        string? AllocateMulticastIp(string macAddress);
        void ReleaseMulticastIp(string macAddress);
        Task<bool> StartPreviewStreamAsync(string macAddress, string multicastIp, int port, string linkMode = "UNKNOWN", int chipIndex = 0, CancellationToken ct = default);
        Task<bool> StartPreviewStreamAsync(string macAddress, string multicastIp, int port, CancellationToken ct) =>
            StartPreviewStreamAsync(macAddress, multicastIp, port, "UNKNOWN", 0, ct);
        Task<bool> StopPreviewStreamAsync(string macAddress, string linkMode = "UNKNOWN", int chipIndex = 0, CancellationToken ct = default);
        Task<bool> StopPreviewStreamAsync(string macAddress, CancellationToken ct) =>
            StopPreviewStreamAsync(macAddress, "UNKNOWN", 0, ct);
        Task<bool> FactoryResetAvpAsync(AvasDevice device, string targetMac, CancellationToken ct = default);
        Task<bool> FactoryResetMcuAsync(AvasDevice device, CancellationToken ct = default);
        Task<bool> RebootChipAsync(AvasDevice device, string targetMac, CancellationToken ct = default);
        Task<bool> RebootMcuAsync(AvasDevice device, CancellationToken ct = default);
    }
}
