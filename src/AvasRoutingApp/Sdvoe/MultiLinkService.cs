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

        private string BaseUrl
        {
            get
            {
                var cfg = _configService.Current;
                return $"http://{cfg.ControlServerIp}:{cfg.RestPort}";
            }
        }

        public async Task<IReadOnlyList<MultiLinkInfo>> QueryMultiLinkPairsAsync(CancellationToken ct = default)
        {
            var pairs = new List<MultiLinkInfo>();
            try
            {
                AppLogger.Info("MultiLink", $"Querying BlueRiver server at {BaseUrl} for AVAS-223 multi-link topology...");

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
            targetMode = targetMode.ToUpperInvariant();

            AppLogger.Info("MultiLink", $"Applying Multi-Link Mode '{targetMode}' to Primary {primaryMac} (Companion: {companionMac ?? "NONE"})...");

            try
            {
                // 1. Send set:multi_link to primary chip_0
                var p0Payload = new { op = "set:multi_link", mode = targetMode };
                bool primarySuccess;
                using (var r0 = await HttpPostAndPollAsync($"/api/device/{primaryMac}", p0Payload, ct))
                {
                    primarySuccess = IsOperationSuccessful(r0);
                }
                AppLogger.Info("MultiLink", $"Primary {primaryMac} set:multi_link result: {(primarySuccess ? "SUCCESS" : "FAILED")}");

                // 2. If target mode is DUAL and companion exists, also send set:multi_link to companion chip_1
                bool companionSuccess = true;
                bool hasComp = !string.IsNullOrWhiteSpace(companionMac) && !string.Equals(companionMac, "NONE", StringComparison.OrdinalIgnoreCase);
                if (targetMode == "DUAL" && hasComp)
                {
                    try
                    {
                        var p1Payload = new { op = "set:multi_link", mode = targetMode };
                        using var r1 = await HttpPostAndPollAsync($"/api/device/{companionMac}", p1Payload, ct);
                        companionSuccess = IsOperationSuccessful(r1);
                        AppLogger.Info("MultiLink", $"Companion {companionMac} set:multi_link result: {(companionSuccess ? "SUCCESS" : "FAILED")}");
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn("MultiLink", $"Failed setting mode on companion {companionMac}: {ex.Message}");
                        companionSuccess = false;
                    }
                }

                // 3. Issue reboots to apply hardware changes
                await RebootDeviceAsync(primaryMac, ct);
                if (hasComp)
                {
                    await RebootDeviceAsync(companionMac!, ct);
                }

                return primarySuccess && companionSuccess;
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
