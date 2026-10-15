using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AvasRoutingApp.Sdvoe
{
    /// <summary>
    /// Service contract for discovering AVAS-223 devices from the SDVoE network.
    /// Conforms to PROJECT.md § Interface Contracts.
    /// </summary>
    public interface ISdvoeDiscoveryService
    {
        Task<IReadOnlyList<AvasDevice>> DiscoverAvas223DevicesAsync(bool enableVegaDevices = false, CancellationToken ct = default);
        Task<IReadOnlyList<AvasDevice>> DiscoverAvas223DevicesAsync(CancellationToken ct) => DiscoverAvas223DevicesAsync(false, ct);
        Task<IReadOnlyList<AvasDevice>> DiscoverAllDevicesAsync(CancellationToken ct = default) => DiscoverAvas223DevicesAsync(false, ct);
        event Action<IReadOnlyList<AvasDevice>>? DevicesDiscovered;
    }
}
