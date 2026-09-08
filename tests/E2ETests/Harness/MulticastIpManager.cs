using System;
using System.Collections.Generic;
using System.Net;

namespace E2ETests.Harness
{
    public interface IMulticastController
    {
        string? AllocateMulticastIp(string macAddress);
        void ReleaseMulticastIp(string macAddress);
    }

    public class MulticastIpManager : IMulticastController
    {
        private readonly uint _startIp;
        private readonly uint _endIp;
        private readonly HashSet<uint> _reservedIps = new();
        private readonly Dictionary<string, uint> _macToIp = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<uint, string> _ipToMac = new();
        private readonly object _lock = new();

        public int BasePort { get; }

        public MulticastIpManager(
            string startIpStr = "224.1.1.1",
            string endIpStr = "224.1.3.225",
            int basePort = 6792)
        {
            _startIp = IpToUint(IPAddress.Parse(startIpStr));
            _endIp = IpToUint(IPAddress.Parse(endIpStr));
            BasePort = basePort;

            // Semtech control server reserved broadcast groups
            _reservedIps.Add(IpToUint(IPAddress.Parse("224.1.1.253")));
            _reservedIps.Add(IpToUint(IPAddress.Parse("224.1.1.254")));
            _reservedIps.Add(IpToUint(IPAddress.Parse("225.225.225.225")));
        }

        public string? AllocateMulticastIp(string macAddress)
        {
            if (string.IsNullOrWhiteSpace(macAddress)) return null;

            lock (_lock)
            {
                // Idempotent: return already assigned IP if active
                if (_macToIp.TryGetValue(macAddress, out uint existingIp))
                {
                    return UintToIp(existingIp);
                }

                // Find lowest available IP in range
                for (uint current = _startIp; current <= _endIp; current++)
                {
                    if (_reservedIps.Contains(current)) continue;
                    if (_ipToMac.ContainsKey(current)) continue;

                    // Allocate
                    _macToIp[macAddress] = current;
                    _ipToMac[current] = macAddress;
                    return UintToIp(current);
                }

                return null; // Pool exhausted
            }
        }

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

        public void RegisterInUse(string ipStr, string macAddress)
        {
            if (IPAddress.TryParse(ipStr, out var ip))
            {
                uint ipNum = IpToUint(ip);
                lock (_lock)
                {
                    _macToIp[macAddress] = ipNum;
                    _ipToMac[ipNum] = macAddress;
                }
            }
        }

        public bool IsAllocated(string macAddress)
        {
            lock (_lock)
            {
                return _macToIp.ContainsKey(macAddress);
            }
        }

        public int ActiveAllocationCount
        {
            get
            {
                lock (_lock) return _macToIp.Count;
            }
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
