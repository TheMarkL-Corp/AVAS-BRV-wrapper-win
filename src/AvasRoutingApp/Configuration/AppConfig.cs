using System;

namespace AvasRoutingApp.Configuration
{
    /// <summary>
    /// Configuration model for AVAS Routing SW.
    /// Strictly conforms to PROJECT.md § Interface Contracts.
    /// </summary>
    public class AppConfig
    {
        public string BlueRiverUrl { get; set; } = "http://localhost:3000";
        public string ControlServerIp { get; set; } = "127.0.0.1";
        public int RestPort { get; set; } = 8080;
        public int TelnetPort { get; set; } = 6970;
        public string MulticastStartIp { get; set; } = "224.1.1.1";
        public string MulticastEndIp { get; set; } = "224.1.3.225";
        public int BasePort { get; set; } = 6792;
        public string LocalNetworkInterfaceIp { get; set; } = "";

        /// <summary>
        /// Creates a deep copy of the configuration instance.
        /// </summary>
        public AppConfig Clone()
        {
            return new AppConfig
            {
                BlueRiverUrl = this.BlueRiverUrl,
                ControlServerIp = this.ControlServerIp,
                RestPort = this.RestPort,
                TelnetPort = this.TelnetPort,
                MulticastStartIp = this.MulticastStartIp,
                MulticastEndIp = this.MulticastEndIp,
                BasePort = this.BasePort,
                LocalNetworkInterfaceIp = this.LocalNetworkInterfaceIp
            };
        }
    }
}
