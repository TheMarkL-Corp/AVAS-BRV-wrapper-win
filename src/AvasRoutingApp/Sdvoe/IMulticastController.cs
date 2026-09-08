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
        Task<bool> StartPreviewStreamAsync(string macAddress, string multicastIp, int port, CancellationToken ct = default);
        Task<bool> StopPreviewStreamAsync(string macAddress, CancellationToken ct = default);
    }
}
