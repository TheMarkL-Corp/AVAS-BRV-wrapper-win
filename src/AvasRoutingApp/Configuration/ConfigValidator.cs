using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace AvasRoutingApp.Configuration
{
    /// <summary>
    /// Validates AppConfig values against network and port constraints.
    /// </summary>
    public static class ConfigValidator
    {
        public static (bool IsValid, List<string> Errors) Validate(AppConfig config)
        {
            var errors = new List<string>();

            if (config == null)
            {
                errors.Add("Configuration cannot be null.");
                return (false, errors);
            }

            // BlueRiver AV Manager URL validation
            if (string.IsNullOrWhiteSpace(config.BlueRiverUrl))
            {
                errors.Add("BlueRiver AV Manager URL cannot be empty.");
            }
            else if (!Uri.TryCreate(config.BlueRiverUrl, UriKind.Absolute, out var uri) ||
                     (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                errors.Add("BlueRiver AV Manager URL must be a valid HTTP or HTTPS absolute URL (e.g., http://localhost:3000).");
            }

            // Control Server IP validation
            if (string.IsNullOrWhiteSpace(config.ControlServerIp))
            {
                errors.Add("SDVoE Control Server IP cannot be empty.");
            }
            else if (!IsValidIPv4Address(config.ControlServerIp))
            {
                errors.Add($"SDVoE Control Server IP '{config.ControlServerIp}' is not a valid IPv4 address.");
            }

            // REST Port validation
            if (!IsValidPort(config.RestPort))
            {
                errors.Add($"REST Port ({config.RestPort}) must be between 1 and 65535.");
            }

            // Telnet Port validation
            if (!IsValidPort(config.TelnetPort))
            {
                errors.Add($"Telnet Port ({config.TelnetPort}) must be between 1 and 65535.");
            }

            // Multicast Base Port validation
            if (!IsValidPort(config.BasePort))
            {
                errors.Add($"Multicast Base Port ({config.BasePort}) must be between 1 and 65535.");
            }

            // Multicast Start IP validation
            bool startIpValid = false;
            uint startIpNum = 0;
            if (string.IsNullOrWhiteSpace(config.MulticastStartIp))
            {
                errors.Add("Multicast Start IP cannot be empty.");
            }
            else if (!IsValidMulticastIp(config.MulticastStartIp, out startIpNum))
            {
                errors.Add($"Multicast Start IP '{config.MulticastStartIp}' is not a valid Class D multicast address (224.0.0.0 - 239.255.255.255).");
            }
            else
            {
                startIpValid = true;
            }

            // Multicast End IP validation
            bool endIpValid = false;
            uint endIpNum = 0;
            if (string.IsNullOrWhiteSpace(config.MulticastEndIp))
            {
                errors.Add("Multicast End IP cannot be empty.");
            }
            else if (!IsValidMulticastIp(config.MulticastEndIp, out endIpNum))
            {
                errors.Add($"Multicast End IP '{config.MulticastEndIp}' is not a valid Class D multicast address (224.0.0.0 - 239.255.255.255).");
            }
            else
            {
                endIpValid = true;
            }

            // Range validation: Start IP <= End IP
            if (startIpValid && endIpValid && startIpNum > endIpNum)
            {
                errors.Add($"Multicast Start IP ({config.MulticastStartIp}) cannot be greater than End IP ({config.MulticastEndIp}).");
            }

            // Local Network Interface IP (optional, but if specified must be valid IPv4)
            if (!string.IsNullOrWhiteSpace(config.LocalNetworkInterfaceIp))
            {
                if (!IsValidIPv4Address(config.LocalNetworkInterfaceIp))
                {
                    errors.Add($"Local Network Interface IP '{config.LocalNetworkInterfaceIp}' is not a valid IPv4 address.");
                }
            }

            return (errors.Count == 0, errors);
        }

        public static bool IsValidIPv4Address(string ip)
        {
            if (string.IsNullOrWhiteSpace(ip)) return false;
            string trimmed = ip.Trim();
            var parts = trimmed.Split('.');
            if (parts.Length != 4) return false;
            foreach (var part in parts)
            {
                if (!byte.TryParse(part, out _)) return false;
            }
            if (!IPAddress.TryParse(trimmed, out var address)) return false;
            return address.AddressFamily == AddressFamily.InterNetwork;
        }

        public static bool IsValidMulticastIp(string ip, out uint numericValue)
        {
            numericValue = 0;
            if (!IsValidIPv4Address(ip)) return false;
            if (!IPAddress.TryParse(ip.Trim(), out var address)) return false;
            if (address.AddressFamily != AddressFamily.InterNetwork) return false;

            byte[] bytes = address.GetAddressBytes();
            // Class D multicast addresses: 224.0.0.0 - 239.255.255.255 (first octet between 224 and 239)
            if (bytes[0] < 224 || bytes[0] > 239) return false;

            numericValue = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
            return true;
        }

        public static bool IsValidPort(int port)
        {
            return port >= 1 && port <= 65535;
        }
    }
}
