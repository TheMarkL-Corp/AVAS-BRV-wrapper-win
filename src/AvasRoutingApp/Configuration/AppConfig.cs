using System;

namespace AvasRoutingApp.Configuration
{
    /// <summary>
    /// Configuration model for AVAS Routing Software.
    /// Strictly conforms to PROJECT.md § Interface Contracts.
    /// </summary>
    public class AppConfig
    {
        public string BlueRiverUrl { get; set; } = "http://localhost:80";
        public string ControlServerIp { get; set; } = "127.0.0.1";
        public int RestPort { get; set; } = 8090;
        public int TelnetPort { get; set; } = 6970;
        public string MulticastStartIp { get; set; } = "225.1.1.1";
        public string MulticastEndIp { get; set; } = "225.1.1.254";
        public int BasePort { get; set; } = 5000;
        public string LocalNetworkInterfaceIp { get; set; } = "";
        public string Theme { get; set; } = "Light";
        public double SidebarWidth { get; set; } = 400.0;
        public string SidebarFontSize { get; set; } = "Normal";

        /// <summary>
        /// Returns the typography scale multiplier for the configured sidebar font size.
        /// </summary>
        public double GetSidebarFontScale()
        {
            if (string.Equals(SidebarFontSize, "Small", StringComparison.OrdinalIgnoreCase)) return 0.90;
            if (string.Equals(SidebarFontSize, "Large", StringComparison.OrdinalIgnoreCase)) return 1.15;
            if (string.Equals(SidebarFontSize, "ExtraLarge", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(SidebarFontSize, "Extra Large", StringComparison.OrdinalIgnoreCase)) return 1.30;
            return 1.00;
        }

        /// <summary>
        /// Returns the recommended default sidebar width in pixels for the configured font size.
        /// </summary>
        public double GetDefaultSidebarWidth()
        {
            if (string.Equals(SidebarFontSize, "Small", StringComparison.OrdinalIgnoreCase)) return 360.0;
            if (string.Equals(SidebarFontSize, "Large", StringComparison.OrdinalIgnoreCase)) return 420.0;
            if (string.Equals(SidebarFontSize, "ExtraLarge", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(SidebarFontSize, "Extra Large", StringComparison.OrdinalIgnoreCase)) return 460.0;
            return 380.0;
        }

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
                LocalNetworkInterfaceIp = this.LocalNetworkInterfaceIp,
                Theme = this.Theme,
                SidebarWidth = this.SidebarWidth,
                SidebarFontSize = this.SidebarFontSize
            };
        }
    }
}
