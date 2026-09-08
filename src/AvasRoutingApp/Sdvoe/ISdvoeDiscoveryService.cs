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
        Task<IReadOnlyList<AvasDevice>> DiscoverAvas223DevicesAsync(CancellationToken ct = default);
        event Action<IReadOnlyList<AvasDevice>>? DevicesDiscovered;
    }
}
