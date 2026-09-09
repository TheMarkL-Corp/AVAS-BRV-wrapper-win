using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AvasRoutingApp.Sdvoe
{
    /// <summary>
    /// Service contract for discovering AVAS-223 multi-link topologies,
    /// querying companion health, changing multi-link mode, and commanding reboots.
    /// </summary>
    public interface IMultiLinkService
    {
        Task<IReadOnlyList<MultiLinkInfo>> QueryMultiLinkPairsAsync(CancellationToken ct = default);
        Task<bool> SetMultiLinkModeAsync(string primaryMac, string? companionMac, string targetMode, CancellationToken ct = default);
        Task<bool> RebootDeviceAsync(string mac, CancellationToken ct = default);
    }
}
