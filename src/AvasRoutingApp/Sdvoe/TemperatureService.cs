using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AvasRoutingApp.Configuration;
using AvasRoutingApp.Logging;

namespace AvasRoutingApp.Sdvoe
{
    /// <summary>
    /// Dual-source temperature querying service.
    /// Retrieves device temperatures via SDVoE REST API and Advantech VoIP SDK (VOIPS.dll),
    /// correlates primary chip 0 and companion chip 1, and detects reading discrepancies.
    /// </summary>
    public class TemperatureService : ITemperatureService, IDisposable
    {
        private readonly IConfigService _configService;
        private readonly HttpClient _httpClient;
        private readonly bool _ownsHttpClient;
        private readonly bool _enableVoipSdk;
        private bool _disposed;

        public TemperatureService(
            IConfigService configService,
            HttpClient? httpClient = null,
            bool enableVoipSdk = true)
        {
            _configService = configService ?? throw new ArgumentNullException(nameof(configService));
            _ownsHttpClient = (httpClient == null);
            _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            _enableVoipSdk = enableVoipSdk;
        }

        private string BaseUrl
        {
            get
            {
                var cfg = _configService.Current;
                string ip = string.IsNullOrWhiteSpace(cfg.ControlServerIp) ? "127.0.0.1" : cfg.ControlServerIp;
                int port = cfg.RestPort > 0 ? cfg.RestPort : 8080;
                return $"http://{ip}:{port}";
            }
        }

        public async Task<IReadOnlyList<DeviceTemperatureInfo>> QueryAllTemperaturesAsync(CancellationToken ct = default)
        {
            var results = new List<DeviceTemperatureInfo>();
            try
            {
                AppLogger.Info("Temperature", $"Initiating dual-source temperature query to {BaseUrl} (VoIP SDK: {_enableVoipSdk})...");

                // 1. Discover all device IDs on the SDVoE network
                var deviceIds = await GetDeviceIdsAsync(ct);
                if (deviceIds.Count == 0)
                {
                    AppLogger.Warn("Temperature", "No devices returned from /api/device.");
                    return results;
                }

                AppLogger.Info("Temperature", $"Found {deviceIds.Count} device ID(s). Querying individual device telemetry...");

                // 2. Query detailed device subsets from SDVoE REST API
                var parsedDevices = new Dictionary<string, RawParsedEndpoint>(StringComparer.OrdinalIgnoreCase);

                foreach (var id in deviceIds)
                {
                    if (ct.IsCancellationRequested) break;

                    var raw = await QuerySdvoeDeviceRawAsync(id, ct);
                    if (raw != null)
                    {
                        parsedDevices[id] = raw;
                    }
                }

                // 3. For all discovered devices, optionally query VoIP SDK in parallel/batch
                var voipTemps = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
                if (_enableVoipSdk)
                {
                    foreach (var id in deviceIds)
                    {
                        if (ct.IsCancellationRequested) break;
                        int? temp = await QueryVoipSdkTemperatureAsync(id, ct);
                        voipTemps[id] = temp;
                    }
                }

                // 4. Group into Primary (chip 0) and Companion (chip 1)
                // First pass: identify chip 0 endpoints as primary card entries
                var primaries = parsedDevices.Values.Where(d => d.ChipId == 0).ToList();
                var companions = parsedDevices.Values.Where(d => d.ChipId == 1).ToDictionary(d => d.DeviceId, StringComparer.OrdinalIgnoreCase);

                // If any devices don't specify chip_id (e.g. single-chip RX), include them as primaries if not already
                foreach (var d in parsedDevices.Values)
                {
                    if (d.ChipId < 0 && !primaries.Contains(d))
                    {
                        primaries.Add(d);
                    }
                }

                foreach (var p in primaries)
                {
                    var info = new DeviceTemperatureInfo
                    {
                        DeviceId = p.DeviceId,
                        DeviceName = p.Name,
                        DeviceType = p.IsTransmitter ? "TX" : "RX",
                        LinkMode = p.LinkMode,
                        IpAddress = p.IpAddress,
                        IsActive = p.IsActive,
                        Chip0Id = p.DeviceId,
                        Chip0SdvoeTemp = p.SdvoeTemperature,
                        Chip0VoipSdkTemp = voipTemps.TryGetValue(p.DeviceId, out var c0Voip) ? c0Voip : null
                    };

                    // Check for companion
                    string companionId = p.CompanionId;
                    RawParsedEndpoint? compObj = null;

                    if (!string.IsNullOrWhiteSpace(companionId) && !string.Equals(companionId, "NONE", StringComparison.OrdinalIgnoreCase))
                    {
                        if (companions.TryGetValue(companionId, out var foundComp))
                        {
                            compObj = foundComp;
                        }
                    }

                    // Fallback pairing: if dual-link and companion not explicitly in MULTI_LINK_TRANSMITTER, check companion by base MAC rule or name
                    if (compObj == null && info.IsDualLink && companions.Count > 0)
                    {
                        // Look for matching companion with same name prefix or paired ID
                        compObj = companions.Values.FirstOrDefault(c =>
                            c.IsTransmitter == p.IsTransmitter &&
                            (c.Name.StartsWith(p.Name, StringComparison.OrdinalIgnoreCase) ||
                             p.Name.StartsWith(c.Name, StringComparison.OrdinalIgnoreCase)));
                    }

                    if (compObj != null)
                    {
                        info.HasCompanion = true;
                        info.CompanionDeviceId = compObj.DeviceId;
                        info.CompanionName = compObj.Name;
                        info.Chip1Id = compObj.DeviceId;
                        info.Chip1SdvoeTemp = compObj.SdvoeTemperature;
                        info.Chip1VoipSdkTemp = voipTemps.TryGetValue(compObj.DeviceId, out var c1Voip) ? c1Voip : null;
                    }

                    // Log any discrepancies
                    if (info.Chip0HasDiscrepancy)
                    {
                        AppLogger.Warn("Temperature",
                            $"Discrepancy on {info.DeviceName} (Chip 0 {info.Chip0Id}): SDVoE={info.Chip0SdvoeTemp}°C, VoIP SDK={info.Chip0VoipSdkTemp}°C (Delta: {info.Chip0DiscrepancyDelta}°C)");
                    }
                    if (info.Chip1HasDiscrepancy)
                    {
                        AppLogger.Warn("Temperature",
                            $"Discrepancy on {info.DeviceName} (Chip 1 {info.Chip1Id}): SDVoE={info.Chip1SdvoeTemp}°C, VoIP SDK={info.Chip1VoipSdkTemp}°C (Delta: {info.Chip1DiscrepancyDelta}°C)");
                    }

                    results.Add(info);
                }

                // If any companion was not paired to a primary, add as standalone entry so no device is hidden
                var pairedCompIds = results.Where(r => r.HasCompanion).Select(r => r.CompanionDeviceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var comp in companions.Values)
                {
                    if (!pairedCompIds.Contains(comp.DeviceId))
                    {
                        var standalone = new DeviceTemperatureInfo
                        {
                            DeviceId = comp.DeviceId,
                            DeviceName = comp.Name,
                            DeviceType = comp.IsTransmitter ? "TX" : "RX",
                            LinkMode = comp.LinkMode,
                            IpAddress = comp.IpAddress,
                            IsActive = comp.IsActive,
                            Chip0Id = comp.DeviceId,
                            Chip0SdvoeTemp = comp.SdvoeTemperature,
                            Chip0VoipSdkTemp = voipTemps.TryGetValue(comp.DeviceId, out var sVoip) ? sVoip : null,
                            HasCompanion = false
                        };
                        results.Add(standalone);
                    }
                }

                AppLogger.Info("Temperature", $"QueryAllTemperaturesAsync completed: {results.Count} combined device(s) processed.");
            }
            catch (Exception ex)
            {
                AppLogger.Error("Temperature", "Error executing QueryAllTemperaturesAsync", ex);
            }

            return results;
        }

        public async Task<DeviceTemperatureInfo?> QueryDeviceTemperatureAsync(
            string primaryMac,
            string? companionMac = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(primaryMac)) return null;

            try
            {
                var pRaw = await QuerySdvoeDeviceRawAsync(primaryMac, ct);
                if (pRaw == null) return null;

                int? pVoip = _enableVoipSdk ? await QueryVoipSdkTemperatureAsync(primaryMac, ct) : null;

                var info = new DeviceTemperatureInfo
                {
                    DeviceId = pRaw.DeviceId,
                    DeviceName = pRaw.Name,
                    DeviceType = pRaw.IsTransmitter ? "TX" : "RX",
                    LinkMode = pRaw.LinkMode,
                    IpAddress = pRaw.IpAddress,
                    IsActive = pRaw.IsActive,
                    Chip0Id = pRaw.DeviceId,
                    Chip0SdvoeTemp = pRaw.SdvoeTemperature,
                    Chip0VoipSdkTemp = pVoip
                };

                string compId = !string.IsNullOrWhiteSpace(companionMac) ? companionMac : pRaw.CompanionId;
                if (!string.IsNullOrWhiteSpace(compId) && !string.Equals(compId, "NONE", StringComparison.OrdinalIgnoreCase))
                {
                    var cRaw = await QuerySdvoeDeviceRawAsync(compId, ct);
                    int? cVoip = _enableVoipSdk ? await QueryVoipSdkTemperatureAsync(compId, ct) : null;

                    info.HasCompanion = true;
                    info.CompanionDeviceId = compId;
                    info.CompanionName = cRaw?.Name ?? "Companion";
                    info.Chip1Id = compId;
                    info.Chip1SdvoeTemp = cRaw?.SdvoeTemperature;
                    info.Chip1VoipSdkTemp = cVoip;
                }

                return info;
            }
            catch (Exception ex)
            {
                AppLogger.Error("Temperature", $"Error querying temperature for device {primaryMac}", ex);
                return null;
            }
        }

        #region SDVoE REST Engine

        private async Task<List<string>> GetDeviceIdsAsync(CancellationToken ct)
        {
            var list = new List<string>();
            try
            {
                using var resp = await HttpGetJsonAsync("/api/device", ct);
                if (resp == null) return list;

                JsonElement devicesElem;
                if (resp.RootElement.TryGetProperty("result", out var resultElem))
                {
                    if (resultElem.ValueKind == JsonValueKind.Array)
                    {
                        devicesElem = resultElem;
                    }
                    else if (resultElem.TryGetProperty("devices", out var resDevs))
                    {
                        devicesElem = resDevs;
                    }
                    else
                    {
                        devicesElem = resultElem;
                    }
                }
                else if (resp.RootElement.TryGetProperty("devices", out var rootDevs))
                {
                    devicesElem = rootDevs;
                }
                else if (resp.RootElement.ValueKind == JsonValueKind.Array)
                {
                    devicesElem = resp.RootElement;
                }
                else
                {
                    return list;
                }

                if (devicesElem.ValueKind == JsonValueKind.Array)
                {
                    foreach (var d in devicesElem.EnumerateArray())
                    {
                        if (d.ValueKind == JsonValueKind.String)
                        {
                            string? s = d.GetString();
                            if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
                        }
                        else if (d.ValueKind == JsonValueKind.Object)
                        {
                            if (d.TryGetProperty("device_id", out var idProp))
                            {
                                string? id = idProp.GetString();
                                if (!string.IsNullOrEmpty(id)) list.Add(id);
                            }
                            else if (d.TryGetProperty("id", out var idShortProp))
                            {
                                string? id = idShortProp.GetString();
                                if (!string.IsNullOrEmpty(id)) list.Add(id);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Debug("Temperature", $"Failed to parse /api/device: {ex.Message}");
            }
            return list;
        }

        private async Task<RawParsedEndpoint?> QuerySdvoeDeviceRawAsync(string deviceId, CancellationToken ct)
        {
            try
            {
                var payload = new { op = "get", subset = "device" };
                using var doc = await HttpPostAndPollAsync($"/api/device/{deviceId}", payload, ct);
                if (doc == null) return null;

                return ParseDeviceJson(doc, deviceId);
            }
            catch (Exception ex)
            {
                AppLogger.Debug("Temperature", $"QuerySdvoeDeviceRawAsync failed for {deviceId}: {ex.Message}");
                return null;
            }
        }

        internal static RawParsedEndpoint ParseDeviceJson(JsonDocument doc, string defaultId)
        {
            var parsed = new RawParsedEndpoint { DeviceId = defaultId, Name = defaultId };
            try
            {
                JsonElement dev = GetFirstDeviceElement(doc);

                // Device Name
                if (dev.TryGetProperty("device_name", out var np))
                {
                    string? s = np.GetString();
                    if (!string.IsNullOrEmpty(s)) parsed.Name = s;
                }
                else if (dev.TryGetProperty("configuration", out var cfg) &&
                         cfg.TryGetProperty("device_name", out var cnp))
                {
                    string? s = cnp.GetString();
                    if (!string.IsNullOrEmpty(s)) parsed.Name = s;
                }

                // Identity (is_transmitter, is_receiver, chip_id)
                if (dev.TryGetProperty("identity", out var idElem))
                {
                    if (idElem.TryGetProperty("is_transmitter", out var txProp))
                        parsed.IsTransmitter = txProp.GetBoolean();

                    if (idElem.TryGetProperty("is_receiver", out var rxProp))
                        parsed.IsReceiver = rxProp.GetBoolean();

                    if (idElem.TryGetProperty("chip_id", out var cp))
                        parsed.ChipId = cp.GetInt32();
                }

                // Status (active, temperature)
                if (dev.TryGetProperty("status", out var statElem))
                {
                    if (statElem.TryGetProperty("active", out var actProp))
                        parsed.IsActive = actProp.GetBoolean();

                    if (statElem.TryGetProperty("temperature", out var tempProp) &&
                        tempProp.ValueKind == JsonValueKind.Number)
                    {
                        parsed.SdvoeTemperature = tempProp.GetInt32();
                    }
                }

                // Nodes (IP address, multi-link node)
                if (dev.TryGetProperty("nodes", out var nodesElem))
                {
                    foreach (var node in nodesElem.EnumerateArray())
                    {
                        // IP Address
                        if (string.IsNullOrEmpty(parsed.IpAddress) &&
                            node.TryGetProperty("status", out var nStatus) &&
                            nStatus.TryGetProperty("ip", out var ipObj) &&
                            ipObj.TryGetProperty("address", out var addrProp))
                        {
                            parsed.IpAddress = addrProp.GetString() ?? "";
                        }

                        // Link Mode & Companion
                        if (node.TryGetProperty("type", out var typeProp))
                        {
                            string typeStr = typeProp.GetString() ?? "";
                            if (string.Equals(typeStr, "MULTI_LINK_TRANSMITTER", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(typeStr, "MULTI_LINK_RECEIVER", StringComparison.OrdinalIgnoreCase))
                            {
                                if (node.TryGetProperty("configuration", out var mcfg) &&
                                    mcfg.TryGetProperty("link_mode", out var lmProp))
                                {
                                    string lm = lmProp.GetString() ?? "";
                                    parsed.LinkMode = lm.Contains("DUAL", StringComparison.OrdinalIgnoreCase) ? "Dual Link" : "Single Link";
                                }

                                if (node.TryGetProperty("status", out var nodeStat) &&
                                    nodeStat.TryGetProperty("companions", out var compArr) &&
                                    compArr.GetArrayLength() > 0)
                                {
                                    var c0 = compArr[0];
                                    if (c0.TryGetProperty("device_id", out var cIdProp))
                                    {
                                        parsed.CompanionId = cIdProp.GetString() ?? "NONE";
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
                // Fallback safe parse
            }
            return parsed;
        }

        private static JsonElement GetFirstDeviceElement(JsonDocument doc)
        {
            if (doc.RootElement.TryGetProperty("result", out var resElem))
            {
                if (resElem.ValueKind == JsonValueKind.Array && resElem.GetArrayLength() > 0)
                {
                    return resElem[0];
                }
                if (resElem.ValueKind == JsonValueKind.Object)
                {
                    if (resElem.TryGetProperty("devices", out var devs) && devs.ValueKind == JsonValueKind.Array && devs.GetArrayLength() > 0)
                    {
                        return devs[0];
                    }
                    if (resElem.TryGetProperty("device", out var singleDev))
                    {
                        return singleDev;
                    }
                }
                return resElem;
            }

            if (doc.RootElement.TryGetProperty("devices", out var rootDevs) && rootDevs.ValueKind == JsonValueKind.Array && rootDevs.GetArrayLength() > 0)
            {
                return rootDevs[0];
            }

            if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
            {
                return doc.RootElement[0];
            }

            return doc.RootElement;
        }

        private async Task<JsonDocument?> HttpGetJsonAsync(string endpoint, CancellationToken ct)
        {
            try
            {
                using var resp = await _httpClient.GetAsync($"{BaseUrl}{endpoint}", ct);
                if (!resp.IsSuccessStatusCode) return null;
                var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                return JsonDocument.Parse(bytes);
            }
            catch
            {
                return null;
            }
        }

        private async Task<JsonDocument?> HttpPostJsonAsync(string endpoint, object payload, CancellationToken ct)
        {
            try
            {
                string json = JsonSerializer.Serialize(payload);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var resp = await _httpClient.PostAsync($"{BaseUrl}{endpoint}", content, ct);
                if (!resp.IsSuccessStatusCode) return null;
                var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                return JsonDocument.Parse(bytes);
            }
            catch
            {
                return null;
            }
        }

        private async Task<JsonDocument?> HttpPostAndPollAsync(
            string endpoint,
            object payload,
            CancellationToken ct,
            int maxPolls = 10,
            int pollIntervalMs = 300)
        {
            var initial = await HttpPostJsonAsync(endpoint, payload, ct);
            if (initial == null) return null;

            if (initial.RootElement.TryGetProperty("status", out var statusProp) &&
                string.Equals(statusProp.GetString(), "PROCESSING", StringComparison.OrdinalIgnoreCase) &&
                initial.RootElement.TryGetProperty("request_id", out var reqIdProp))
            {
                string? reqId = reqIdProp.ValueKind == JsonValueKind.Number
                    ? reqIdProp.GetInt64().ToString()
                    : reqIdProp.GetString();

                if (!string.IsNullOrEmpty(reqId))
                {
                    for (int i = 0; i < maxPolls; i++)
                    {
                        if (ct.IsCancellationRequested) break;
                        await Task.Delay(pollIntervalMs, ct);

                        var poll = await HttpGetJsonAsync($"/api/request/{reqId}", ct);
                        if (poll != null)
                        {
                            if (poll.RootElement.TryGetProperty("status", out var pollStatusProp))
                            {
                                string? status = pollStatusProp.GetString();
                                if (!string.Equals(status, "PROCESSING", StringComparison.OrdinalIgnoreCase))
                                {
                                    initial.Dispose();
                                    return poll;
                                }
                            }
                            poll.Dispose();
                        }
                    }
                }
            }

            return initial;
        }

        #endregion

        #region VoIP SDK Engine (VOIPS_LIB.VOIPS)

        /// <summary>
        /// Queries the device junction temperature using the Advantech VoIP SDK (VOIPS.dll).
        /// Reads DEVICE_STATUS.temperature directly.
        /// </summary>
        public async Task<int?> QueryVoipSdkTemperatureAsync(string deviceId, CancellationToken ct = default)
        {
            if (!_enableVoipSdk || string.IsNullOrWhiteSpace(deviceId)) return null;

            return await Task.Run<int?>(() =>
            {
                try
                {
                    var cfg = _configService.Current;
                    string host = string.IsNullOrWhiteSpace(cfg.ControlServerIp) ? "127.0.0.1" : cfg.ControlServerIp;
                    int port = cfg.RestPort > 0 ? cfg.RestPort : 8080;

                    var voips = new VOIPS_LIB.VOIPS();
                    voips.SET_CONTROL_SERVER(host);
                    voips.SET_HTTP_PORT(port);

                    // Call GET_ALL_SETTINGS (or GetSubset)
                    var brResult = voips.GET_ALL_SETTINGS(deviceId);
                    if (brResult == null) return null;

                    // If PROCESSING, poll GET_REQUEST
                    if (string.Equals(brResult.status, "PROCESSING", StringComparison.OrdinalIgnoreCase) &&
                        brResult.request_id.HasValue)
                    {
                        int reqId = brResult.request_id.Value;
                        for (int attempt = 0; attempt < 8; attempt++)
                        {
                            if (ct.IsCancellationRequested) return null;
                            Thread.Sleep(300);

                            var pollResult = voips.GET_REQUEST(reqId);
                            if (pollResult != null &&
                                !string.Equals(pollResult.status, "PROCESSING", StringComparison.OrdinalIgnoreCase))
                            {
                                brResult = pollResult;
                                break;
                            }
                        }
                    }

                    if (brResult.result?.devices != null && brResult.result.devices.Count > 0)
                    {
                        var devObj = brResult.result.devices[0];
                        if (devObj?.status != null && devObj.status.temperature.HasValue)
                        {
                            int temp = devObj.status.temperature.Value;
                            AppLogger.Debug("Temperature", $"VoIP SDK returned temperature {temp}°C for {deviceId}");
                            return temp;
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Debug("Temperature", $"VoIP SDK query failed for {deviceId}: {ex.Message}");
                }
                return null;
            }, ct);
        }

        #endregion

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_ownsHttpClient)
            {
                _httpClient.Dispose();
            }
        }
    }

    /// <summary>
    /// Internal helper struct for raw parsed endpoint properties from JSON.
    /// </summary>
    internal class RawParsedEndpoint
    {
        public string DeviceId { get; set; } = "";
        public string Name { get; set; } = "";
        public bool IsTransmitter { get; set; } = true;
        public bool IsReceiver { get; set; } = false;
        public int ChipId { get; set; } = -1;
        public string IpAddress { get; set; } = "";
        public bool IsActive { get; set; } = true;
        public int? SdvoeTemperature { get; set; }
        public string LinkMode { get; set; } = "Single Link";
        public string CompanionId { get; set; } = "NONE";
    }
}
