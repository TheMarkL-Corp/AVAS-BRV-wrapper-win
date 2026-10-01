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
    /// Service for querying and configuring AVAS-223 multi-link topologies,
    /// tracking companion online/offline status, and executing synchronized mode switches.
    /// </summary>
    public class MultiLinkService : IMultiLinkService, IDisposable
    {
        private readonly IConfigService _configService;
        private readonly HttpClient _httpClient;
        private readonly bool _ownsHttpClient;
        private bool _disposed;

        public MultiLinkService(IConfigService configService, HttpClient? httpClient = null)
        {
            _configService = configService ?? throw new ArgumentNullException(nameof(configService));
            _ownsHttpClient = (httpClient == null);
            _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        }

        private int _resolvedRestPort = 0;

        private IEnumerable<int> GetPortsToTry()
        {
            var cfg = _configService.Current;
            int configured = cfg.RestPort > 0 ? cfg.RestPort : 8090;
            int resolved = _resolvedRestPort > 0 ? _resolvedRestPort : configured;
            return new[] { resolved, configured, 8090, 8080, 80 }.Distinct();
        }

        private static string NormalizeMac(string mac)
        {
            if (string.IsNullOrWhiteSpace(mac)) return string.Empty;
            return mac.Replace(":", "").Replace("-", "").Trim().ToLowerInvariant();
        }

        private static bool IsConsecutiveMac(string macA, string macB)
        {
            if (string.IsNullOrWhiteSpace(macA) || string.IsNullOrWhiteSpace(macB)) return false;
            macA = NormalizeMac(macA);
            macB = NormalizeMac(macB);
            if (macA.Length != 12 || macB.Length != 12) return false;
            if (long.TryParse(macA, System.Globalization.NumberStyles.HexNumber, null, out long valA) &&
                long.TryParse(macB, System.Globalization.NumberStyles.HexNumber, null, out long valB))
            {
                return Math.Abs(valA - valB) == 1;
            }
            return false;
        }

        private static bool ArePairedTransmitterNames(string nameA, string nameB)
        {
            if (string.IsNullOrWhiteSpace(nameA) || string.IsNullOrWhiteSpace(nameB)) return false;
            string cleanA = nameA.Replace("SFPA", "", StringComparison.OrdinalIgnoreCase).Replace("SFPB", "", StringComparison.OrdinalIgnoreCase).Trim();
            string cleanB = nameB.Replace("SFPA", "", StringComparison.OrdinalIgnoreCase).Replace("SFPB", "", StringComparison.OrdinalIgnoreCase).Trim();
            return string.Equals(cleanA, cleanB, StringComparison.OrdinalIgnoreCase);
        }

        public async Task<IReadOnlyList<MultiLinkInfo>> QueryMultiLinkPairsAsync(CancellationToken ct = default)
        {
            var pairs = new List<MultiLinkInfo>();
            try
            {
                var cfg = _configService.Current;
                AppLogger.Info("MultiLink", $"Querying BlueRiver server at {cfg.ControlServerIp} for AVAS-223 multi-link topology...");

                // 1. Get all device IDs
                List<string> deviceIds;
                using (var devListResp = await HttpGetJsonAsync("/api/device", ct))
                {
                    if (devListResp == null)
                    {
                        AppLogger.Warn("MultiLink", "Failed to get device list from /api/device");
                        return pairs;
                    }

                    deviceIds = ExtractDeviceIds(devListResp);
                }
                AppLogger.Info("MultiLink", $"Found {deviceIds.Count} total device(s) on network.");

                // 2. Query identity for each device to find transmitters
                var txCandidates = new List<(string Id, string Name, int ChipId, string Ip)>();
                foreach (var devId in deviceIds)
                {
                    if (ct.IsCancellationRequested) break;

                    try
                    {
                        var idPayload = new { op = "get", subset = "identity" };
                        using var idResp = await HttpPostJsonAsync($"/api/device/{devId}", idPayload, ct);
                        if (idResp != null)
                        {
                            var info = ParseIdentity(idResp, devId);
                            if (info != null && info.Value.IsTx)
                            {
                                txCandidates.Add((devId, info.Value.Name, info.Value.ChipId, info.Value.Ip));
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Debug("MultiLink", $"Identity query failed for {devId}: {ex.Message}");
                    }
                }

                AppLogger.Info("MultiLink", $"Identified {txCandidates.Count} transmitter candidate(s). Querying full node details...");

                // 3. Fetch detailed device subsets to read MULTI_LINK_TRANSMITTER node
                var allTxDetails = new Dictionary<string, (string Name, int ChipId, string Ip, bool IsActive, string LinkMode, string CompanionId, string Caps)>(StringComparer.OrdinalIgnoreCase);

                foreach (var cand in txCandidates)
                {
                    if (ct.IsCancellationRequested) break;

                    try
                    {
                        var subsetPayload = new { op = "get", subset = "device" };
                        using var fullResp = await HttpPostAndPollAsync($"/api/device/{cand.Id}", subsetPayload, ct);
                        if (fullResp != null)
                        {
                            var details = ParseDeviceSubset(fullResp, cand.Id, cand.Name, cand.ChipId, cand.Ip);
                            allTxDetails[cand.Id] = details;
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Debug("MultiLink", $"Device subset query failed for {cand.Id}: {ex.Message}");
                    }
                }

                // 4. Match chip_0 primaries with chip_1 companions
                foreach (var kvp in allTxDetails)
                {
                    var (name, chipId, ip, isActive, linkMode, companionId, caps) = kvp.Value;
                    if (chipId == 0)
                    {
                        var pair = new MultiLinkInfo
                        {
                            PrimaryMac = kvp.Key,
                            PrimaryName = name,
                            PrimaryIp = ip,
                            LinkMode = linkMode,
                            CompanionMac = companionId,
                            Capabilities = caps
                        };

                        if (pair.HasCompanion)
                        {
                            if (allTxDetails.TryGetValue(pair.CompanionMac, out var compDetails))
                            {
                                pair.CompanionName = compDetails.Name;
                                pair.CompanionIsActive = compDetails.IsActive;
                                AppLogger.Info("MultiLink", $"Matched Primary {pair.PrimaryMac} with Companion {pair.CompanionMac} (Active: {pair.CompanionIsActive})");
                            }
                            else
                            {
                                // Direct fallback query for companion if not found in initial transmitter list
                                try
                                {
                                    AppLogger.Debug("MultiLink", $"Querying companion {pair.CompanionMac} directly...");
                                    using var compResp = await HttpPostAndPollAsync($"/api/device/{pair.CompanionMac}", new { op = "get", subset = "device" }, ct);
                                    if (compResp != null)
                                    {
                                        var compParsed = ParseDeviceSubset(compResp, pair.CompanionMac, "chip_1", 1, "");
                                        pair.CompanionName = compParsed.Name;
                                        pair.CompanionIsActive = compParsed.IsActive;
                                        AppLogger.Info("MultiLink", $"Direct query resolved Companion {pair.CompanionMac} (Active: {pair.CompanionIsActive})");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    AppLogger.Warn("MultiLink", $"Failed direct query for companion {pair.CompanionMac}: {ex.Message}");
                                    pair.CompanionIsActive = false;
                                }
                            }
                        }
                        else
                        {
                            // If companion not explicitly listed in MULTI_LINK_TRANSMITTER (e.g. device is currently in SINGLE mode),
                            // correlate companion chip_1 from discovered transmitters by consecutive MAC or name prefix:
                            var compCand = allTxDetails.FirstOrDefault(c =>
                                c.Value.ChipId == 1 &&
                                (IsConsecutiveMac(pair.PrimaryMac, c.Key) ||
                                 ArePairedTransmitterNames(pair.PrimaryName, c.Value.Name)));

                            if (!string.IsNullOrEmpty(compCand.Key))
                            {
                                pair.CompanionMac = compCand.Key;
                                pair.CompanionName = compCand.Value.Name;
                                pair.CompanionIsActive = compCand.Value.IsActive;
                                AppLogger.Info("MultiLink", $"Correlated Companion {pair.CompanionMac} ({pair.CompanionName}) for Primary {pair.PrimaryMac} in {pair.LinkMode} mode (Active: {pair.CompanionIsActive})");
                            }
                        }

                        pairs.Add(pair);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("MultiLink", "Error querying multi-link pairs", ex);
            }

            return pairs;
        }

        public async Task<bool> SetMultiLinkModeAsync(string primaryMac, string? companionMac, string targetMode, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(primaryMac)) return false;
            string normPrimaryMac = NormalizeMac(primaryMac);
            targetMode = targetMode.ToUpperInvariant();

            AppLogger.Info("MultiLink", $"Applying Multi-Link Mode '{targetMode}' to Primary {normPrimaryMac} (Adv_VOIPS_Sample chip_0 alignment)...");

            try
            {
                // 1. Send set:multi_link EXCLUSIVELY to primary chip_0 (per Adv_VOIPS_Sample TX_SET_MULTI_LINK_MODE)
                var p0Payload = new { op = "set:multi_link", mode = targetMode };
                bool primarySuccess;
                using (var r0 = await HttpPostAndPollAsync($"/api/device/{normPrimaryMac}", p0Payload, ct))
                {
                    primarySuccess = IsOperationSuccessful(r0);
                }
                AppLogger.Info("MultiLink", $"Primary {normPrimaryMac} set:multi_link result: {(primarySuccess ? "SUCCESS" : "FAILED")}");

                if (!primarySuccess)
                {
                    return false;
                }

                // Settle delay before hardware reboot
                await Task.Delay(300, ct);

                // 2. Issue reboot only to primary chip_0; BlueRiver control server coordinates link synchronization
                bool rebootSuccess = await RebootDeviceAsync(normPrimaryMac, ct);
                AppLogger.Info("MultiLink", $"Primary {normPrimaryMac} reboot result: {(rebootSuccess ? "SUCCESS" : "FAILED")}");

                return primarySuccess;
            }
            catch (Exception ex)
            {
                AppLogger.Error("MultiLink", $"Exception setting multi-link mode for {primaryMac}", ex);
                return false;
            }
        }

        public async Task<bool> RebootDeviceAsync(string mac, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(mac)) return false;
            mac = NormalizeMac(mac);
            try
            {
                AppLogger.Info("MultiLink", $"Issuing reboot command to device {mac}...");
                var payload = new { op = "reboot" };
                using var resp = await HttpPostJsonAsync($"/api/device/{mac}", payload, ct);
                return resp != null;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("MultiLink", $"Reboot command failed for {mac}: {ex.Message}");
                return false;
            }
        }

        #region HTTP & JSON Helpers

        private async Task<JsonDocument?> HttpGetJsonAsync(string endpoint, CancellationToken ct)
        {
            var cfg = _configService.Current;
            string host = cfg.ControlServerIp;
            if (string.IsNullOrWhiteSpace(host)) host = "127.0.0.1";

            foreach (int port in GetPortsToTry())
            {
                try
                {
                    string url = $"http://{host}:{port}{endpoint}";
                    using var resp = await _httpClient.GetAsync(url, ct);
                    if (resp.IsSuccessStatusCode)
                    {
                        _resolvedRestPort = port;
                        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                        return JsonDocument.Parse(bytes);
                    }
                }
                catch
                {
                    // Try next candidate port
                }
            }
            return null;
        }

        private async Task<JsonDocument?> HttpPostJsonAsync(string endpoint, object payload, CancellationToken ct)
        {
            var cfg = _configService.Current;
            string host = cfg.ControlServerIp;
            if (string.IsNullOrWhiteSpace(host)) host = "127.0.0.1";

            string json = JsonSerializer.Serialize(payload);
            foreach (int port in GetPortsToTry())
            {
                try
                {
                    string url = $"http://{host}:{port}{endpoint}";
                    using var content = new StringContent(json, Encoding.UTF8, "application/json");
                    using var resp = await _httpClient.PostAsync(url, content, ct);
                    if (resp.IsSuccessStatusCode)
                    {
                        _resolvedRestPort = port;
                        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                        return JsonDocument.Parse(bytes);
                    }
                }
                catch
                {
                    // Try next candidate port
                }
            }
            return null;
        }

        private async Task<JsonDocument?> HttpPostAndPollAsync(string endpoint, object payload, CancellationToken ct, int maxPolls = 12, int pollIntervalMs = 500)
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

        private static List<string> ExtractDeviceIds(JsonDocument doc)
        {
            var list = new List<string>();
            try
            {
                JsonElement devicesElem;
                if (doc.RootElement.TryGetProperty("result", out var resultElem) &&
                    resultElem.TryGetProperty("devices", out var resDevs))
                {
                    devicesElem = resDevs;
                }
                else if (doc.RootElement.TryGetProperty("devices", out var rootDevs))
                {
                    devicesElem = rootDevs;
                }
                else if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    devicesElem = doc.RootElement;
                }
                else
                {
                    return list;
                }

                foreach (var d in devicesElem.EnumerateArray())
                {
                    if (d.TryGetProperty("device_id", out var idProp))
                    {
                        string? id = idProp.GetString();
                        if (!string.IsNullOrEmpty(id)) list.Add(id);
                    }
                }
            }
            catch { }
            return list;
        }

        private static (bool IsTx, string Name, int ChipId, string Ip)? ParseIdentity(JsonDocument doc, string defaultId)
        {
            try
            {
                JsonElement devElem = GetFirstDeviceElement(doc);
                string name = devElem.TryGetProperty("device_name", out var np) ? np.GetString() ?? defaultId : defaultId;

                bool isTx = false;
                int chipId = 0;
                if (devElem.TryGetProperty("identity", out var idElem))
                {
                    if (idElem.TryGetProperty("is_transmitter", out var txProp)) isTx = txProp.GetBoolean();
                    if (idElem.TryGetProperty("chip_id", out var chipProp)) chipId = chipProp.GetInt32();
                }

                string ip = "";
                if (devElem.TryGetProperty("nodes", out var nodesElem))
                {
                    foreach (var node in nodesElem.EnumerateArray())
                    {
                        if (node.TryGetProperty("status", out var nStatus) &&
                            nStatus.TryGetProperty("ip", out var ipObj) &&
                            ipObj.TryGetProperty("address", out var addrProp))
                        {
                            ip = addrProp.GetString() ?? "";
                            break;
                        }
                    }
                }

                return (isTx, name, chipId, ip);
            }
            catch
            {
                return null;
            }
        }

        private static (string Name, int ChipId, string Ip, bool IsActive, string LinkMode, string CompanionId, string Caps)
            ParseDeviceSubset(JsonDocument doc, string defaultId, string defaultName, int defaultChipId, string defaultIp)
        {
            string name = defaultName;
            int chipId = defaultChipId;
            string ip = defaultIp;
            bool isActive = false;
            string linkMode = "UNKNOWN";
            string companionId = "NONE";
            string caps = "SINGLE,DUAL";

            try
            {
                JsonElement dev = GetFirstDeviceElement(doc);
                if (dev.TryGetProperty("device_name", out var np))
                {
                    string? s = np.GetString();
                    if (!string.IsNullOrEmpty(s)) name = s;
                }

                if (dev.TryGetProperty("identity", out var idElem) && idElem.TryGetProperty("chip_id", out var cp))
                {
                    chipId = cp.GetInt32();
                }

                if (dev.TryGetProperty("status", out var statElem) && statElem.TryGetProperty("active", out var actProp))
                {
                    isActive = actProp.GetBoolean();
                }

                if (dev.TryGetProperty("nodes", out var nodesElem))
                {
                    foreach (var node in nodesElem.EnumerateArray())
                    {
                        if (node.TryGetProperty("type", out var typeProp) &&
                            string.Equals(typeProp.GetString(), "MULTI_LINK_TRANSMITTER", StringComparison.OrdinalIgnoreCase))
                        {
                            if (node.TryGetProperty("configuration", out var cfgElem) &&
                                cfgElem.TryGetProperty("link_mode", out var lmProp))
                            {
                                linkMode = (lmProp.GetString() ?? "UNKNOWN").ToUpperInvariant();
                            }

                            if (node.TryGetProperty("status", out var nodeStat))
                            {
                                if (nodeStat.TryGetProperty("companions", out var compArr) && compArr.GetArrayLength() > 0)
                                {
                                    var c0 = compArr[0];
                                    if (c0.TryGetProperty("device_id", out var cIdProp))
                                    {
                                        companionId = cIdProp.GetString() ?? "NONE";
                                    }
                                }

                                if (nodeStat.TryGetProperty("link_capabilities", out var capsArr))
                                {
                                    var capList = new List<string>();
                                    foreach (var c in capsArr.EnumerateArray())
                                    {
                                        if (c.TryGetProperty("mode", out var mProp))
                                        {
                                            string? m = mProp.GetString();
                                            if (!string.IsNullOrEmpty(m)) capList.Add(m);
                                        }
                                    }
                                    if (capList.Count > 0) caps = string.Join(",", capList);
                                }
                            }
                        }

                        if (string.IsNullOrEmpty(ip) &&
                            node.TryGetProperty("status", out var ns) &&
                            ns.TryGetProperty("ip", out var ipObj) &&
                            ipObj.TryGetProperty("address", out var addrProp))
                        {
                            ip = addrProp.GetString() ?? "";
                        }
                    }
                }
            }
            catch { }

            return (name, chipId, ip, isActive, linkMode, companionId, caps);
        }

        private static JsonElement GetFirstDeviceElement(JsonDocument doc)
        {
            if (doc.RootElement.TryGetProperty("result", out var resElem) &&
                resElem.TryGetProperty("devices", out var devs) &&
                devs.GetArrayLength() > 0)
            {
                return devs[0];
            }

            if (doc.RootElement.TryGetProperty("devices", out var rootDevs) && rootDevs.GetArrayLength() > 0)
            {
                return rootDevs[0];
            }

            return doc.RootElement;
        }

        private static bool IsOperationSuccessful(JsonDocument? doc)
        {
            if (doc == null) return false;
            try
            {
                if (doc.RootElement.TryGetProperty("status", out var statusProp))
                {
                    string? status = statusProp.GetString();
                    return string.Equals(status, "SUCCESS", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(status, "PROCESSING", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { }
            return false;
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
}
