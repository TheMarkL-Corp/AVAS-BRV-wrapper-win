using System;

namespace AvasRoutingApp.Sdvoe
{
    /// <summary>
    /// Represents the multi-link configuration and paired companion telemetry for an AVAS-223 transmitter.
    /// </summary>
    public class MultiLinkInfo
    {
        public string PrimaryMac { get; set; } = "";
        public string PrimaryName { get; set; } = "";
        public string PrimaryIp { get; set; } = "";
        public string LinkMode { get; set; } = "UNKNOWN"; // "SINGLE", "DUAL", or "UNKNOWN"
        public string CompanionMac { get; set; } = "NONE";
        public string CompanionName { get; set; } = "";
        public bool CompanionIsActive { get; set; } = false;
        public string Capabilities { get; set; } = "SINGLE,DUAL";

        public bool HasCompanion => !string.IsNullOrEmpty(CompanionMac) &&
                                    !string.Equals(CompanionMac, "NONE", StringComparison.OrdinalIgnoreCase);
    }
}
