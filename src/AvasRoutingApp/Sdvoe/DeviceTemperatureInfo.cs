using System;
using System.Collections.Generic;
using System.Linq;

namespace AvasRoutingApp.Sdvoe
{
    /// <summary>
    /// Represents thermal telemetry and discrepancy status for an AVAS TX (Encoder) or RX (Decoder) endpoint.
    /// Combines primary Chip 0 and optional companion Chip 1 into a unified device model.
    /// Supports comparative analysis between SDVoE REST API and Advantech VoIP SDK (DEVICE_STATUS).
    /// </summary>
    public class DeviceTemperatureInfo
    {
        public const int WarningThresholdDegC = 61;
        public const int CriticalThresholdDegC = 71; // > 70 deg C is Critical

        /// <summary>
        /// Primary device identifier / MAC address.
        /// </summary>
        public string DeviceId { get; set; } = "";

        /// <summary>
        /// User-friendly or network-assigned device name.
        /// </summary>
        public string DeviceName { get; set; } = "";

        /// <summary>
        /// Device type category: "TX" (Transmitter/Encoder) or "RX" (Receiver/Decoder).
        /// </summary>
        public string DeviceType { get; set; } = "TX";

        public bool IsTransmitter => string.Equals(DeviceType, "TX", StringComparison.OrdinalIgnoreCase);
        public bool IsReceiver => string.Equals(DeviceType, "RX", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Network mode: "Single Link" or "Dual Link".
        /// </summary>
        public string LinkMode { get; set; } = "Single Link";

        public bool IsDualLink => LinkMode.Contains("Dual", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// IP address of primary chip.
        /// </summary>
        public string IpAddress { get; set; } = "";

        /// <summary>
        /// Connection/heartbeat active status.
        /// </summary>
        public bool IsActive { get; set; } = true;

        #region Chip 0 Telemetry

        /// <summary>
        /// Device ID of Chip 0 (Primary).
        /// </summary>
        public string Chip0Id { get; set; } = "";

        /// <summary>
        /// Junction temperature queried via SDVoE REST API (in degrees Celsius).
        /// </summary>
        public int? Chip0SdvoeTemp { get; set; }

        /// <summary>
        /// Junction temperature queried via Advantech VoIP SDK DEVICE_STATUS (in degrees Celsius).
        /// </summary>
        public int? Chip0VoipSdkTemp { get; set; }

        /// <summary>
        /// True if both sources returned a value and they differ.
        /// </summary>
        public bool Chip0HasDiscrepancy =>
            Chip0SdvoeTemp.HasValue && Chip0VoipSdkTemp.HasValue &&
            Chip0SdvoeTemp.Value != Chip0VoipSdkTemp.Value;

        /// <summary>
        /// Delta between SDVoE API and VoIP SDK (SDVoE - VoIP).
        /// </summary>
        public int? Chip0DiscrepancyDelta =>
            (Chip0SdvoeTemp.HasValue && Chip0VoipSdkTemp.HasValue)
                ? (Chip0SdvoeTemp.Value - Chip0VoipSdkTemp.Value)
                : null;

        #endregion

        #region Chip 1 (Companion) Telemetry

        private bool? _hasCompanion;

        /// <summary>
        /// Indicates whether this device has a companion chip (e.g. dual-link companion AVP1).
        /// </summary>
        public bool HasCompanion
        {
            get => _hasCompanion ?? !string.IsNullOrWhiteSpace(CompanionDeviceId);
            set => _hasCompanion = value;
        }

        /// <summary>
        /// Companion device identifier / MAC address.
        /// </summary>
        public string CompanionDeviceId { get; set; } = "";

        /// <summary>
        /// Alias for CompanionDeviceId (Chip 1 ID).
        /// </summary>
        public string Chip1Id
        {
            get => CompanionDeviceId;
            set => CompanionDeviceId = value;
        }

        /// <summary>
        /// Companion device name.
        /// </summary>
        public string CompanionName { get; set; } = "";

        /// <summary>
        /// Junction temperature for Chip 1 queried via SDVoE REST API (in degrees Celsius).
        /// </summary>
        public int? Chip1SdvoeTemp { get; set; }

        /// <summary>
        /// Junction temperature for Chip 1 queried via Advantech VoIP SDK DEVICE_STATUS (in degrees Celsius).
        /// </summary>
        public int? Chip1VoipSdkTemp { get; set; }

        /// <summary>
        /// True if both sources returned a value for Chip 1 and they differ.
        /// </summary>
        public bool Chip1HasDiscrepancy =>
            Chip1SdvoeTemp.HasValue && Chip1VoipSdkTemp.HasValue &&
            Chip1SdvoeTemp.Value != Chip1VoipSdkTemp.Value;

        /// <summary>
        /// Delta between SDVoE API and VoIP SDK for Chip 1.
        /// </summary>
        public int? Chip1DiscrepancyDelta =>
            (Chip1SdvoeTemp.HasValue && Chip1VoipSdkTemp.HasValue)
                ? (Chip1SdvoeTemp.Value - Chip1VoipSdkTemp.Value)
                : null;

        #endregion

        /// <summary>
        /// Returns the peak recorded temperature across all valid readings for this device.
        /// </summary>
        public int? MaxTemperature
        {
            get
            {
                var temps = new List<int>();
                if (Chip0SdvoeTemp.HasValue) temps.Add(Chip0SdvoeTemp.Value);
                if (Chip0VoipSdkTemp.HasValue) temps.Add(Chip0VoipSdkTemp.Value);
                if (HasCompanion)
                {
                    if (Chip1SdvoeTemp.HasValue) temps.Add(Chip1SdvoeTemp.Value);
                    if (Chip1VoipSdkTemp.HasValue) temps.Add(Chip1VoipSdkTemp.Value);
                }
                return temps.Count > 0 ? temps.Max() : null;
            }
        }

        /// <summary>
        /// Critical Alert condition: strictly greater than 70 degrees Celsius per specification.
        /// </summary>
        public bool IsCritical
        {
            get
            {
                if (Chip0SdvoeTemp > 70 || Chip0VoipSdkTemp > 70) return true;
                if (HasCompanion && (Chip1SdvoeTemp > 70 || Chip1VoipSdkTemp > 70)) return true;
                return false;
            }
        }

        /// <summary>
        /// Warning condition: between 61°C and 70°C inclusive.
        /// </summary>
        public bool IsWarning
        {
            get
            {
                if (IsCritical) return false;
                var max = MaxTemperature;
                return max.HasValue && max.Value >= WarningThresholdDegC && max.Value <= 70;
            }
        }

        /// <summary>
        /// True if either chip reports a discrepancy between SDVoE API and VoIP SDK.
        /// </summary>
        public bool HasAnyDiscrepancy => Chip0HasDiscrepancy || (HasCompanion && Chip1HasDiscrepancy);
    }
}
