using System;
using System.Collections.Generic;
using System.Net;

namespace AvasRoutingApp.Sdvoe
{
    /// <summary>
    /// Contract for dynamic multicast IP address allocation.
    /// </summary>
    public interface IMulticastIpAllocator
    {
        string? AllocateMulticastIp(string macAddress);
        void ReleaseMulticastIp(string macAddress);
        void Reset();
    }

    /// <summary>
    /// Thread-safe multicast IP manager that allocates unique multicast IPs within a configured pool.
    /// Excludes Semtech reserved addresses (224.1.1.253, 224.1.1.254, 225.225.225.225) and currently active streams.
    /// </summary>
    public class MulticastIpManager : IMulticastIpAllocator
    {
        private readonly uint _startIp;
        private readonly uint _endIp;
        private readonly HashSet<uint> _reservedIps = new();
        private readonly Dictionary<string, uint> _macToIp = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<uint, string> _ipToMac = new();
        private readonly object _lock = new();

        public int BasePort { get; }
        public string StartIp { get; }
        public string EndIp { get; }

        public MulticastIpManager(
            string startIpStr = "224.1.1.1",
            string endIpStr = "224.1.3.225",
            int basePort = 6792)
        {
            if (!IPAddress.TryParse(startIpStr, out var startAddr))
                throw new ArgumentException($"Invalid Start IP address: {startIpStr}", nameof(startIpStr));
            if (!IPAddress.TryParse(endIpStr, out var endAddr))
                throw new ArgumentException($"Invalid End IP address: {endIpStr}", nameof(endIpStr));

            _startIp = IpToUint(startAddr);
            _endIp = IpToUint(endAddr);
            if (_startIp > _endIp)
                throw new ArgumentException("Multicast start IP cannot be greater than end IP.");

            StartIp = startIpStr;
            EndIp = endIpStr;
            BasePort = basePort;

            // Semtech control server reserved broadcast groups
            _reservedIps.Add(IpToUint(IPAddress.Parse("224.1.1.253"))); // All TX control
            _reservedIps.Add(IpToUint(IPAddress.Parse("224.1.1.254"))); // All RX control
            _reservedIps.Add(IpToUint(IPAddress.Parse("225.225.225.225"))); // Semtech internal reserved
        }

        /// <summary>
        /// Allocates a unique multicast IP from the configured pool for the specified MAC address.
        /// Idempotent: returns existing allocation if MAC already has an active IP.
        /// Returns null if the pool is exhausted or macAddress is empty.
        /// </summary>
        public string? AllocateMulticastIp(string macAddress)
        {
            if (string.IsNullOrWhiteSpace(macAddress)) return null;

            lock (_lock)
            {
                if (_macToIp.TryGetValue(macAddress, out uint existingIp))
                {
                    return UintToIp(existingIp);
                }

                for (uint current = _startIp; current <= _endIp; current++)
                {
                    if (_reservedIps.Contains(current)) continue;
                    if (_ipToMac.ContainsKey(current)) continue;

                    _macToIp[macAddress] = current;
                    _ipToMac[current] = macAddress;
                    return UintToIp(current);
                }

                return null;
            }
        }

        /// <summary>
        /// Releases any multicast IP allocated to the specified MAC address.
        /// </summary>
        public void ReleaseMulticastIp(string macAddress)
        {
            if (string.IsNullOrWhiteSpace(macAddress)) return;

            lock (_lock)
            {
                if (_macToIp.TryGetValue(macAddress, out uint ip))
                {
                    _macToIp.Remove(macAddress);
                    _ipToMac.Remove(ip);
                }
            }
        }

        /// <summary>
        /// Resets all allocations in the manager, returning all addresses to the pool.
        /// </summary>
        public void Reset()
        {
            lock (_lock)
            {
                _macToIp.Clear();
                _ipToMac.Clear();
            }
        }

        /// <summary>
        /// Registers an external IP address as already in-use (e.g. from server list multicast).
        /// </summary>
        public void RegisterInUse(string ipStr, string macAddress)
        {
            if (IPAddress.TryParse(ipStr, out var ip))
            {
                uint ipNum = IpToUint(ip);
                lock (_lock)
                {
                    string effectiveKey = string.Equals(macAddress, "active_stream", StringComparison.OrdinalIgnoreCase)
                        ? $"active_stream_{ipNum:X8}"
                        : macAddress;
                    _macToIp[effectiveKey] = ipNum;
                    _ipToMac[ipNum] = effectiveKey;
                }
            }
        }

        /// <summary>
        /// Checks whether a MAC address currently has an allocated multicast IP.
        /// </summary>
        public bool IsAllocated(string macAddress)
        {
            if (string.IsNullOrWhiteSpace(macAddress)) return false;
            lock (_lock)
            {
                return _macToIp.ContainsKey(macAddress);
            }
        }

        /// <summary>
        /// Gets the allocated multicast IP for the given MAC address, or null if not allocated.
        /// </summary>
        public string? GetAllocatedIp(string macAddress)
        {
            if (string.IsNullOrWhiteSpace(macAddress)) return null;
            lock (_lock)
            {
                return _macToIp.TryGetValue(macAddress, out uint ip) ? UintToIp(ip) : null;
            }
        }

        /// <summary>
        /// Gets the number of currently active allocations.
        /// </summary>
        public int ActiveAllocationCount
        {
            get
            {
                lock (_lock) return _macToIp.Count;
            }
        }

        /// <summary>
        /// Checks if an IP is reserved.
        /// </summary>
        public bool IsReserved(string ipStr)
        {
            if (IPAddress.TryParse(ipStr, out var ip))
            {
                uint num = IpToUint(ip);
                lock (_lock)
                {
                    return _reservedIps.Contains(num);
                }
            }
            return false;
        }

        public static uint IpToUint(IPAddress ip)
        {
            byte[] b = ip.GetAddressBytes();
            return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        }

        public static string UintToIp(uint value)
        {
            return $"{(value >> 24) & 0xFF}.{(value >> 16) & 0xFF}.{(value >> 8) & 0xFF}.{value & 0xFF}";
        }
    }
}
