using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AvasRoutingApp.Configuration;
using AvasRoutingApp.Logging;
using VOIPS_LIB;

namespace AvasRoutingApp.Sdvoe
{
    /// <summary>
    /// SDVoE Control Server client providing Telnet (port 6970) and REST (port 8080/9200) communication,
    /// strict AVAS-223 device discovery and filtering, and multicast stream lifecycle control.
    /// Implements ISdvoeDiscoveryService and IMulticastController.
    /// </summary>
    public class SdvoeClient : ISdvoeDiscoveryService, IMulticastController, IDisposable
    {
        private readonly string _serverIp;
        private readonly int _telnetPort;
        private int _restPort;
        private readonly MulticastIpManager _ipManager;
        private readonly IMultiLinkService? _multiLinkService;
        private readonly HttpClient _httpClient;
        private TcpClient? _telnetClient;
        private StreamReader? _telnetReader;
        private StreamWriter? _telnetWriter;
        private readonly SemaphoreSlim _telnetLock = new(1, 1);
        private bool _isTelnetAuthenticated;
        private bool _disposed;

        public string ServerIp => _serverIp;
        public int TelnetPort => _telnetPort;
        public int RestPort => _restPort;
        public MulticastIpManager IpManager => _ipManager;
        public IMultiLinkService? MultiLinkService => _multiLinkService;
        public bool IsTelnetConnected => _telnetClient != null && _telnetClient.Connected;
        public bool IsTelnetAuthenticated => _isTelnetAuthenticated && IsTelnetConnected;

        public event Action<IReadOnlyList<AvasDevice>>? DevicesDiscovered;

        public SdvoeClient(
            string serverIp = "127.0.0.1",
            int telnetPort = 6970,
            int restPort = 8080,
            MulticastIpManager? ipManager = null,
            IMultiLinkService? multiLinkService = null)
        {
            _serverIp = string.IsNullOrWhiteSpace(serverIp) ? "127.0.0.1" : serverIp;
            _telnetPort = telnetPort > 0 ? telnetPort : 6970;
            _restPort = restPort > 0 ? restPort : 8080;
            _ipManager = ipManager ?? new MulticastIpManager();
            _multiLinkService = multiLinkService;
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        }

        public SdvoeClient(AppConfig config, MulticastIpManager? ipManager = null, IMultiLinkService? multiLinkService = null)
            : this(
                config?.ControlServerIp ?? "127.0.0.1",
                config?.TelnetPort ?? 6970,
                config?.RestPort ?? 8080,
                ipManager ?? new MulticastIpManager(
                    config?.MulticastStartIp ?? "225.0.0.1",
                    config?.MulticastEndIp ?? "225.0.1.254",
                    config?.BasePort ?? 6792),
                multiLinkService)
        {
        }

        #region Telnet Communication

        /// <summary>
        /// Connects to the SDVoE Control Server via Telnet (TCP 6970) and executes the mandatory handshake: require api 3.0.0.0.
        /// </summary>
        public async Task<bool> ConnectTelnetAsync(CancellationToken ct = default)
        {
            await _telnetLock.WaitAsync(ct);
            try
            {
                if (_telnetClient != null && _telnetClient.Connected && _isTelnetAuthenticated)
                {
                    return true;
                }

                // Cleanup existing socket if half-open
                CleanupTelnetResources();

                AppLogger.Info("Telnet", $"Connecting to SDVoE Telnet server at {_serverIp}:{_telnetPort}...");
                _telnetClient = new TcpClient();
                await _telnetClient.ConnectAsync(_serverIp, _telnetPort, ct);
                var stream = _telnetClient.GetStream();
                _telnetReader = new StreamReader(stream, Encoding.ASCII);
                _telnetWriter = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true, NewLine = "\r\n" };

                // Mandatory Semtech Handshake: require api 3.0.0.0
                AppLogger.Debug("Telnet", "TX >> require api 3.0.0.0");
                await _telnetWriter.WriteLineAsync("require api 3.0.0.0");
                string? response = await _telnetReader.ReadLineAsync(ct);
                AppLogger.Debug("Telnet", $"RX << {response}");

                if (response != null && response.Contains("\"status\":\"SUCCESS\"", StringComparison.OrdinalIgnoreCase))
                {
                    _isTelnetAuthenticated = true;
                    AppLogger.Info("Telnet", "Telnet session authenticated successfully (API 3.0.0.0).");
                    return true;
                }

                AppLogger.Warn("Telnet", $"Telnet handshake failed. Server returned: {response}");
                _isTelnetAuthenticated = false;
                return false;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("Telnet", $"Failed to connect to SDVoE Telnet ({_serverIp}:{_telnetPort}): {ex.Message}");
                _isTelnetAuthenticated = false;
                CleanupTelnetResources();
                return false;
            }
            finally
            {
                _telnetLock.Release();
            }
        }

        /// <summary>
        /// Sends a raw text command over the authenticated Telnet session and returns the single-line JSON response.
        /// </summary>
        public async Task<string> SendTelnetCommandAsync(string command, CancellationToken ct = default)
        {
            await _telnetLock.WaitAsync(ct);
            try
            {
                if (_telnetClient == null || !_telnetClient.Connected || _telnetWriter == null || _telnetReader == null)
                {
                    throw new InvalidOperationException("Telnet client is not connected.");
                }

                string cleanCmd = command.TrimEnd('\r', '\n');
                AppLogger.Debug("Telnet", $"TX >> {cleanCmd}");
                await _telnetWriter.WriteLineAsync(cleanCmd);
                string? line = await _telnetReader.ReadLineAsync(ct);
                AppLogger.Debug("Telnet", $"RX << {line}");
                return line ?? string.Empty;
            }
            catch (Exception ex)
            {
                AppLogger.Error("Telnet", $"Error sending command '{command}' over Telnet", ex);
                CleanupTelnetResources();
                throw;
            }
            finally
            {
                _telnetLock.Release();
            }
        }

        public void DisconnectTelnet()
        {
            _telnetLock.Wait();
            try
            {
                CleanupTelnetResources();
            }
            finally
            {
                _telnetLock.Release();
            }
        }

        private void CleanupTelnetResources()
        {
            _isTelnetAuthenticated = false;
            try { _telnetReader?.Dispose(); } catch { }
            try { _telnetWriter?.Dispose(); } catch { }
            try { _telnetClient?.Dispose(); } catch { }
            _telnetReader = null;
            _telnetWriter = null;
            _telnetClient = null;
        }

        #endregion

        #region Device Discovery & Filtering

        /// <summary>
        /// Discovers all devices on the network and filters strictly for AVAS-223 chip_0 encoders.
        /// Fires DevicesDiscovered event on completion.
        /// </summary>
        public async Task<IReadOnlyList<AvasDevice>> DiscoverAvas223DevicesAsync(CancellationToken ct = default)
        {
            IReadOnlyList<AvasDevice> allDevices;

            try
            {
                allDevices = await DiscoverDevicesViaTelnetAsync(ct);
            }
            catch
            {
                // Fallback to REST API if Telnet is unavailable
                allDevices = await DiscoverDevicesViaRestAsync(ct);
            }

            var filtered = SdvoeDiscoveryFilter.FilterTargetDevices(allDevices);

            // Sync currently allocated multicast IPs
            foreach (var dev in filtered)
            {
                string? allocatedIp = _ipManager.GetAllocatedIp(dev.MacAddress);
                if (!string.IsNullOrEmpty(allocatedIp))
                {
                    dev.AllocatedMulticastIp = allocatedIp;
                    dev.IsStreaming = true;
                }
            }

            // If multiLinkService is available, enrich devices with multi-link status
            if (_multiLinkService != null)
            {
                try
                {
                    var pairs = await _multiLinkService.QueryMultiLinkPairsAsync(ct);
                    var pairDict = pairs.ToDictionary(p => p.PrimaryMac, StringComparer.OrdinalIgnoreCase);
                    foreach (var dev in filtered)
                    {
                        if (pairDict.TryGetValue(dev.MacAddress, out var pair))
                        {
                            dev.LinkMode = pair.LinkMode;
                            dev.CompanionMac = pair.CompanionMac;
                            dev.CompanionName = pair.CompanionName;
                            dev.CompanionIsActive = pair.CompanionIsActive;
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Debug("SdvoeClient", $"Multi-link enrichment skipped/failed: {ex.Message}");
                }
            }

            DevicesDiscovered?.Invoke(filtered);
            return filtered;
        }

        /// <summary>
        /// Discovers all devices via Telnet command 'get all identity'.
        /// </summary>
        public async Task<IReadOnlyList<AvasDevice>> DiscoverDevicesViaTelnetAsync(CancellationToken ct = default)
        {
            if (!_isTelnetAuthenticated)
            {
                bool connected = await ConnectTelnetAsync(ct);
                if (!connected) return Array.Empty<AvasDevice>();
            }

            string response = await SendTelnetCommandAsync("get all identity", ct);
            return ParseDevicesJson(response);
        }

        /// <summary>
        /// Discovers all devices via REST API endpoint POST /api/device/ALL or GET /api/device.
        /// </summary>
        public async Task<IReadOnlyList<AvasDevice>> DiscoverDevicesViaRestAsync(CancellationToken ct = default)
        {
            string urlPost = $"http://{_serverIp}:{_restPort}/api/device/ALL";
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Post, urlPost)
                {
                    Content = new StringContent("{\"op\":\"get\",\"subset\":\"identity\"}", Encoding.UTF8, "application/json")
                };
                var resp = await _httpClient.SendAsync(request, ct);
                if (resp.IsSuccessStatusCode)
                {
                    string json = await resp.Content.ReadAsStringAsync(ct);
                    return ParseDevicesJson(json);
                }
            }
            catch
            {
                // Try fallback to GET /api/device
            }

            string urlGet = $"http://{_serverIp}:{_restPort}/api/device";
            var getResp = await _httpClient.GetAsync(urlGet, ct);
            getResp.EnsureSuccessStatusCode();
            string getJson = await getResp.Content.ReadAsStringAsync(ct);
            return ParseDevicesJson(getJson);
        }

        /// <summary>
        /// Discovers all devices (both transmitters and receivers) across the SDVoE network without filtering.
        /// </summary>
        public async Task<IReadOnlyList<AvasDevice>> DiscoverAllDevicesAsync(CancellationToken ct = default)
        {
            IReadOnlyList<AvasDevice> allDevices;
            try
            {
                allDevices = await DiscoverDevicesViaTelnetAsync(ct);
            }
            catch
            {
                allDevices = await DiscoverDevicesViaRestAsync(ct);
            }
            return allDevices;
        }

        #endregion

        #region Multicast Allocation & Stream Control (IMulticastController)

        /// <summary>
        /// Allocates a unique multicast IP from the internal pool for the given MAC address.
        /// </summary>
        public string? AllocateMulticastIp(string macAddress)
        {
            return _ipManager.AllocateMulticastIp(macAddress);
        }

        /// <summary>
        /// Releases the multicast IP allocated to the given MAC address back to the pool.
        /// </summary>
        public void ReleaseMulticastIp(string macAddress)
        {
            _ipManager.ReleaseMulticastIp(macAddress);
        }

        /// <summary>
        /// Configures the thumbnail generator on an encoder with specified FPS, SSRC, and destination UDP port.
        /// </summary>
        public async Task<bool> ConfigureThumbnailStreamAsync(
            string mac,
            double fps = 1.0,
            uint ssrc = 12345,
            int udpPort = 6792,
            CancellationToken ct = default)
        {
            string cmd = $"set {mac} thumbnail fps {fps:0.0} ssrc {ssrc} udp {udpPort}";
            try
            {
                if (!_isTelnetAuthenticated) await ConnectTelnetAsync(ct);
                string resp = await SendTelnetCommandAsync(cmd, ct);
                if (IsCommandSuccessful(resp))
                {
                    return true;
                }
            }
            catch
            {
                // Fallback to REST API
            }

            return await ConfigureThumbnailViaRestAsync(mac, fps, ssrc, udpPort, ct);
        }

        /// <summary>
        /// Starts the preview multicast stream for the given MAC address on the specified multicast IP and port.
        /// Sends 'start <mac>:thumbnail:0 <multicast_ip>' via Telnet.
        /// </summary>
        private static string NormalizeMac(string mac)
        {
            if (string.IsNullOrEmpty(mac)) return string.Empty;
            return mac.Replace(":", "").Replace("-", "").ToLowerInvariant();
        }

        /// <summary>
        /// Posts a JSON payload to a device endpoint, probing the configured RestPort, port 8090, port 8080, and port 80.
        /// Caches the active working port upon success.
        /// </summary>
        private async Task<HttpResponseMessage?> PostDeviceRestAsync(string relativePath, object payload, CancellationToken ct)
        {
            var portsToTry = new[] { _restPort, 8080, 8090, 80 }.Distinct().ToArray();
            string jsonPayload = JsonSerializer.Serialize(payload);

            foreach (int port in portsToTry)
            {
                try
                {
                    string url = $"http://{_serverIp}:{port}{relativePath}";
                    AppLogger.Debug("SdvoeClient", $"Trying REST POST on {url}: {jsonPayload}");
                    var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
                    var resp = await _httpClient.PostAsync(url, content, ct);
                    if (resp != null)
                    {
                        if (_restPort != port)
                        {
                            AppLogger.Info("SdvoeClient", $"Detected active SDVoE REST port at {port} (switched from {_restPort})");
                            _restPort = port;
                        }
                        return resp;
                    }
                }
                catch (HttpRequestException ex)
                {
                    AppLogger.Debug("SdvoeClient", $"Port {port} unreachable: {ex.Message}");
                }
                catch (SocketException ex)
                {
                    AppLogger.Debug("SdvoeClient", $"Socket error on port {port}: {ex.Message}");
                }
            }
            return null;
        }

        private static bool IsRestResponseSuccess(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return false;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("status", out var statusProp))
                {
                    string? status = statusProp.GetString();
                    return string.Equals(status, "SUCCESS", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(status, "PROCESSING", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { }

            return json.Contains("\"status\"", StringComparison.OrdinalIgnoreCase) &&
                   (json.Contains("\"SUCCESS\"", StringComparison.OrdinalIgnoreCase) ||
                    json.Contains("\"PROCESSING\"", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Ensures the device MCU UART port (stream_index 1 / port_index 1) is switched to API mode:
        /// {"stream_type":"rs232","stream_index":1,"op":"switch:api"}.
        /// Mandatory before issuing in-band MCU commands or RTP preview commands.
        /// </summary>
        public async Task<bool> EnsureUartApiModeAsync(string chip0Mac, CancellationToken ct = default)
        {
            try
            {
                string normMac = NormalizeMac(chip0Mac);
                string path = $"/api/device/{normMac}";
                var payload = new
                {
                    stream_type = "rs232",
                    stream_index = 1,
                    op = "switch:api"
                };

                using var resp = await PostDeviceRestAsync(path, payload, ct);
                if (resp != null && resp.IsSuccessStatusCode)
                {
                    string body = await resp.Content.ReadAsStringAsync(ct);
                    AppLogger.Info("SdvoeClient", $"EnsureUartApiModeAsync for {normMac} response: {body}");
                    return IsRestResponseSuccess(body);
                }

                // If REST is not connected or fails, try Telnet fallback
                try
                {
                    if (!_isTelnetAuthenticated)
                    {
                        await ConnectTelnetAsync(ct);
                    }
                }
                catch { }
                if (IsTelnetConnected)
                {
                    string telnetCmd = $"set {normMac} configuration:rs232:1:mode api";
                    string tResp = await SendTelnetCommandAsync(telnetCmd, ct);
                    AppLogger.Info("SdvoeClient", $"EnsureUartApiModeAsync via Telnet for {normMac}: {tResp}");
                    return tResp.Contains("\"status\":\"SUCCESS\"", StringComparison.OrdinalIgnoreCase);
                }

                return false;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("SdvoeClient", $"EnsureUartApiModeAsync exception for {chip0Mac}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Sends an in-band MCU command over UART 1 (RS-232 port index 1) to chip_0.
        /// Automatically ensures UART 1 is in API mode beforehand.
        /// </summary>
        public async Task<string> SendMcuCommandAsync(string chip0Mac, string command, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(command)) return string.Empty;

            try
            {
                string normMac = NormalizeMac(chip0Mac);
                await EnsureUartApiModeAsync(normMac, ct);

                string dataStr = command;
                if (!dataStr.EndsWith("\r\n"))
                {
                    dataStr = dataStr.EndsWith("\r") || dataStr.EndsWith("\n") ? dataStr.TrimEnd('\r', '\n') + "\r\n" : dataStr + "\r\n";
                }

                string path = $"/api/device/{normMac}";
                var payload = new
                {
                    op = "send:rs232",
                    port_index = 1,
                    data_string = dataStr
                };

                using var resp = await PostDeviceRestAsync(path, payload, ct);
                if (resp != null && resp.IsSuccessStatusCode)
                {
                    string body = await resp.Content.ReadAsStringAsync(ct);
                    AppLogger.Debug("SdvoeClient", $"SendMcuCommandAsync for {normMac} ('{command.Trim()}'): {body}");
                    return body;
                }

                // Telnet fallback if REST fails or is unavailable
                try
                {
                    if (!_isTelnetAuthenticated)
                    {
                        await ConnectTelnetAsync(ct);
                    }
                }
                catch { }
                if (IsTelnetConnected)
                {
                    string cleanCmd = command.Trim();
                    string telnetCmd = $"send {normMac} rs232:1 {cleanCmd}\r";
                    string tResp = await SendTelnetCommandAsync(telnetCmd, ct);
                    AppLogger.Debug("SdvoeClient", $"SendMcuCommandAsync via Telnet for {normMac}: {tResp}");
                    return tResp;
                }

                return string.Empty;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("SdvoeClient", $"SendMcuCommandAsync exception for {chip0Mac}: {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>
        /// Queries device authorization/activation state via MCU UART 1: \rget authorize\r\n.
        /// Parses:
        /// 'status : Authorize on' -> DeviceActivationState.Activated
        /// 'status : Authorize off' -> DeviceActivationState.Unactivated
        /// </summary>
        public async Task<DeviceActivationState> QueryDeviceActivationAsync(string chip0Mac, CancellationToken ct = default)
        {
            try
            {
                string normMac = NormalizeMac(chip0Mac);
                string response = await SendMcuCommandAsync(normMac, "\rget authorize\r\n", ct);
                return ParseActivationState(response);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("SdvoeClient", $"QueryDeviceActivationAsync exception for {chip0Mac}: {ex.Message}");
                return DeviceActivationState.Unknown;
            }
        }

        public static DeviceActivationState ParseActivationState(string rawResponse)
        {
            if (string.IsNullOrWhiteSpace(rawResponse))
            {
                return DeviceActivationState.Unknown;
            }

            if (System.Text.RegularExpressions.Regex.IsMatch(rawResponse, @"Authorize\s*:?\s*on\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                return DeviceActivationState.Activated;
            }

            if (System.Text.RegularExpressions.Regex.IsMatch(rawResponse, @"Authorize\s*:?\s*off\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                return DeviceActivationState.Unactivated;
            }

            return DeviceActivationState.Unknown;
        }

        /// <summary>
        /// Queries essential MCU hardware information (firmware version, hardware version, factory serial number, and activation state).
        /// </summary>
        public async Task<McuDeviceInfo> QueryMcuDeviceInfoAsync(string chip0Mac, CancellationToken ct = default)
        {
            var info = new McuDeviceInfo { MacAddress = chip0Mac };
            try
            {
                string normMac = NormalizeMac(chip0Mac);

                // 1. Primary path: Use VoIP SDK engine for asynchronous RS-232 telemetry & event resolution
                try
                {
                    var voips = new VOIPS_LIB.VOIPS();
                    voips.SET_CONTROL_SERVER(_serverIp);
                    voips.SET_HTTP_PORT(_restPort);

                    var verTask = voips.GET_MCU_VERSION(normMac);
                    var ipTask = voips.GET_MCU_IP(normMac);
                    var snTask = voips.GET_SN(normMac);

                    await Task.WhenAll(verTask, ipTask, snTask);

                    if (!string.IsNullOrWhiteSpace(verTask.Result)) info.FirmwareVersion = verTask.Result;
                    if (!string.IsNullOrWhiteSpace(ipTask.Result)) info.McuIpAddress = ipTask.Result;
                    if (!string.IsNullOrWhiteSpace(snTask.Result)) info.FactorySerialNumber = snTask.Result;

                    // Query Authorization state via in-band UART & event lookup
                    voips.SWITCH_RS232_CONTROL_SERVER(normMac, 1);
                    var latestEvents = voips.QueryEvent(0, 1000);
                    int lastId = 0;
                    if (latestEvents?.result?.events != null && latestEvents.result.events.Count > 0)
                    {
                        lastId = latestEvents.result.events[^1].event_id;
                    }
                    voips.UART_SEND(normMac, 1, "get authorize\r\n");
                    await Task.Delay(350, ct);

                    var evs = voips.QueryEvent(lastId, 10);
                    if (evs?.result?.events != null)
                    {
                        foreach (var e in evs.result.events)
                        {
                            if (string.Equals(e.event_type, "RS232_RECEIVED", StringComparison.OrdinalIgnoreCase) && e.request_id.HasValue)
                            {
                                var req = voips.GET_REQUEST((int)e.request_id.Value);
                                if (req?.result?.rs232 != null)
                                {
                                    foreach (var r in req.result.rs232)
                                    {
                                        var parsed = ParseActivationState(r.rs232_data);
                                        if (parsed != DeviceActivationState.Unknown)
                                        {
                                            info.ActivationState = parsed;
                                            break;
                                        }
                                    }
                                }
                            }
                        }
                    }

                    if (info.ActivationState != DeviceActivationState.Unknown)
                    {
                        return info;
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Debug("SdvoeClient", $"VoIP SDK query fallback: {ex.Message}");
                }

                // 2. Fallback path: In-band REST / Telnet commands
                info.ActivationState = await QueryDeviceActivationAsync(normMac, ct);

                string fwResp = await SendMcuCommandAsync(normMac, "get fw ver", ct);
                if (string.IsNullOrEmpty(info.FirmwareVersion))
                    info.FirmwareVersion = ParseMcuField(fwResp, "fw ver", string.Empty);

                string hwResp = await SendMcuCommandAsync(normMac, "get hw ver", ct);
                if (string.IsNullOrEmpty(info.HardwareVersion))
                    info.HardwareVersion = ParseMcuField(hwResp, "hw ver", string.Empty);

                string snResp = await SendMcuCommandAsync(normMac, "get fac sn", ct);
                if (string.IsNullOrEmpty(info.FactorySerialNumber))
                    info.FactorySerialNumber = ParseMcuField(snResp, "fac sn", string.Empty);

                string ipResp = await SendMcuCommandAsync(normMac, "get ipaddr", ct);
                var ipMatch = System.Text.RegularExpressions.Regex.Match(ipResp, @"ipaddr\s+([0-9]+\.[0-9]+\.[0-9]+\.[0-9]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (ipMatch.Success)
                {
                    info.McuIpAddress = ipMatch.Groups[1].Value;
                }
                else if (string.IsNullOrEmpty(info.McuIpAddress))
                {
                    info.McuIpAddress = ParseMcuField(ipResp, "ipaddr", string.Empty);
                }

                string igmpResp = await SendMcuCommandAsync(normMac, "get rtp igmp", ct);
                var igmpMatch = System.Text.RegularExpressions.Regex.Match(igmpResp, @"rtp\s*igmp\s+([0-9]+\.[0-9]+\.[0-9]+\.[0-9]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (igmpMatch.Success)
                {
                    info.MulticastIp = igmpMatch.Groups[1].Value;
                }
                else if (string.IsNullOrEmpty(info.MulticastIp))
                {
                    info.MulticastIp = ParseMcuField(igmpResp, "rtp igmp", string.Empty);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("SdvoeClient", $"QueryMcuDeviceInfoAsync error for {chip0Mac}: {ex.Message}");
            }
            return info;
        }

        public static McuDeviceInfo ParseMcuDeviceInfo(string raw)
        {
            var info = new McuDeviceInfo();
            if (string.IsNullOrWhiteSpace(raw)) return info;

            var snMatch = System.Text.RegularExpressions.Regex.Match(raw, @"status\s*:\s*fac\s*sn\s*(?<val>\S+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (snMatch.Success) info.FactorySerialNumber = snMatch.Groups["val"].Value.Trim();

            var igmpMatch = System.Text.RegularExpressions.Regex.Match(raw, @"status\s*:\s*rtp\s*igmp\s*(?<val>\d+\.\d+\.\d+\.\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (igmpMatch.Success) info.MulticastIp = igmpMatch.Groups["val"].Value.Trim();

            var ipMatch = System.Text.RegularExpressions.Regex.Match(raw, @"status\s*:\s*ipaddr\s*(?<val>\d+\.\d+\.\d+\.\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (ipMatch.Success) info.McuIpAddress = ipMatch.Groups["val"].Value.Trim();

            var verMatch = System.Text.RegularExpressions.Regex.Match(raw, @"MCU\s*VER:\s*(?<val>\S+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (verMatch.Success)
            {
                info.FirmwareVersion = verMatch.Groups["val"].Value.Trim();
            }
            else
            {
                var fwMatch = System.Text.RegularExpressions.Regex.Match(raw, @"status\s*:\s*fw\s*ver\s*(?<val>\S+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (fwMatch.Success) info.FirmwareVersion = fwMatch.Groups["val"].Value.Trim();
            }

            return info;
        }

        public static string ParseMcuField(string raw, string prefix, string fallback = "")
        {
            if (string.IsNullOrWhiteSpace(raw)) return fallback;
            string unescaped = raw.Replace("\\r\\n", "\n").Replace("\\n", "\n").Replace("\\r", "\n").Replace("\"", "");
            var lines = unescaped.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                int colonIdx = line.IndexOf(':');
                if (colonIdx >= 0 && colonIdx < line.Length - 1)
                {
                    string key = line.Substring(0, colonIdx).Trim();
                    string val = line.Substring(colonIdx + 1).Trim();

                    // Ignore command echoes
                    if (key.Equals("echo", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // Key matches prefix directly (e.g. "fw ver : V2.23")
                    if (!string.IsNullOrEmpty(prefix) && key.IndexOf(prefix, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (!val.Equals("SUCCESS", StringComparison.OrdinalIgnoreCase))
                        {
                            return val;
                        }
                    }

                    // Status line contains value (e.g. "status : ipaddr 10.32.32.14" or "status : fw ver 2.0.0")
                    if (key.Equals("status", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(prefix))
                    {
                        int pIdx = val.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
                        if (pIdx >= 0)
                        {
                            string afterPrefix = val.Substring(pIdx + prefix.Length).Trim(' ', ':');
                            if (!string.IsNullOrEmpty(afterPrefix) && !afterPrefix.Equals("SUCCESS", StringComparison.OrdinalIgnoreCase))
                            {
                                return afterPrefix;
                            }
                        }
                    }
                }
            }
            return fallback;
        }

        /// <summary>
        /// Starts AVAS-223 AVP thumbnail streaming via RS-232 tunneling (MCU port index 1).
        /// Ensures UART 1 API mode, sends 'set rtp igmp <multicastIp>\r\n' then 'set rtp ON\r\n'.
        /// Note: The hardware streams directly to UDP port 5000 without requiring a port parameter.
        /// Supports REST API with automatic Telnet fallback.
        /// </summary>
        public async Task<bool> StartPreviewStreamViaAvpRs232Async(string mac, string multicastIp, CancellationToken ct = default)
        {
            try
            {
                string normMac = NormalizeMac(mac);
                string path = $"/api/device/{normMac}";

                // Step 0: Ensure UART 1 is in API mode
                await EnsureUartApiModeAsync(normMac, ct);

                // Step 1: Set multicast IP address for RTP stream
                bool setSuccess = false;
                var setPayload = new
                {
                    op = "send:rs232",
                    port_index = 1,
                    data_string = $"set rtp igmp {multicastIp}\r\n"
                };
                using (var setResp = await PostDeviceRestAsync(path, setPayload, ct))
                {
                    if (setResp != null && setResp.IsSuccessStatusCode)
                    {
                        string setBody = await setResp.Content.ReadAsStringAsync(ct);
                        AppLogger.Info("SdvoeClient", $"RS-232 'set rtp igmp' REST response: {setBody}");
                        setSuccess = IsRestResponseSuccess(setBody);
                    }
                }

                // Telnet fallback for Step 1
                if (!setSuccess)
                {
                    try
                    {
                        if (!_isTelnetAuthenticated)
                        {
                            await ConnectTelnetAsync(ct);
                        }
                    }
                    catch { }

                    if (IsTelnetConnected)
                    {
                        string telnetSetCmd = $"send {normMac} rs232:1 set rtp igmp {multicastIp}";
                        string tSetResp = await SendTelnetCommandAsync(telnetSetCmd, ct);
                        AppLogger.Info("SdvoeClient", $"RS-232 'set rtp igmp' Telnet response: {tSetResp}");
                        setSuccess = IsCommandSuccessful(tSetResp) || IsRestResponseSuccess(tSetResp);

                        if (!setSuccess)
                        {
                            // Secondary fallback without 'set' prefix
                            string altCmd = $"send {normMac} rs232:1 rtp igmp {multicastIp}";
                            string altResp = await SendTelnetCommandAsync(altCmd, ct);
                            setSuccess = IsCommandSuccessful(altResp) || IsRestResponseSuccess(altResp);
                        }
                    }
                }

                if (!setSuccess)
                {
                    AppLogger.Warn("SdvoeClient", $"RS-232 'rtp igmp' failed on both REST and Telnet for {normMac}");
                    return false;
                }

                // Step 2: Turn on RTP streaming
                bool onSuccess = false;
                var onPayload = new
                {
                    op = "send:rs232",
                    port_index = 1,
                    data_string = "set rtp ON\r\n"
                };
                using (var onResp = await PostDeviceRestAsync(path, onPayload, ct))
                {
                    if (onResp != null && onResp.IsSuccessStatusCode)
                    {
                        string onBody = await onResp.Content.ReadAsStringAsync(ct);
                        AppLogger.Info("SdvoeClient", $"RS-232 'set rtp ON' REST response: {onBody}");
                        onSuccess = IsRestResponseSuccess(onBody);
                    }
                }

                // Telnet fallback for Step 2
                if (!onSuccess)
                {
                    try
                    {
                        if (!_isTelnetAuthenticated)
                        {
                            await ConnectTelnetAsync(ct);
                        }
                    }
                    catch { }

                    if (IsTelnetConnected)
                    {
                        string telnetOnCmd = $"send {normMac} rs232:1 set rtp ON";
                        string tOnResp = await SendTelnetCommandAsync(telnetOnCmd, ct);
                        AppLogger.Info("SdvoeClient", $"RS-232 'set rtp ON' Telnet response: {tOnResp}");
                        onSuccess = IsCommandSuccessful(tOnResp) || IsRestResponseSuccess(tOnResp);

                        if (!onSuccess)
                        {
                            // Secondary fallback without 'set' prefix
                            string altCmd = $"send {normMac} rs232:1 rtp ON";
                            string altResp = await SendTelnetCommandAsync(altCmd, ct);
                            onSuccess = IsCommandSuccessful(altResp) || IsRestResponseSuccess(altResp);
                        }
                    }
                }

                return onSuccess;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("SdvoeClient", $"StartPreviewStreamViaAvpRs232Async exception for {mac}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Stops AVAS-223 AVP thumbnail streaming via RS-232 tunneling (MCU port index 1).
        /// Sends 'set rtp OFF\r\n' with REST and Telnet fallback.
        /// </summary>
        public async Task<bool> StopPreviewStreamViaAvpRs232Async(string mac, CancellationToken ct = default)
        {
            try
            {
                string normMac = NormalizeMac(mac);
                string path = $"/api/device/{normMac}";
                var offPayload = new
                {
                    op = "send:rs232",
                    port_index = 1,
                    data_string = "set rtp OFF\r\n"
                };
                using var resp = await PostDeviceRestAsync(path, offPayload, ct);
                if (resp != null && resp.IsSuccessStatusCode)
                {
                    string body = await resp.Content.ReadAsStringAsync(ct);
                    AppLogger.Info("SdvoeClient", $"RS-232 'set rtp OFF' response: {body}");
                    if (IsRestResponseSuccess(body))
                    {
                        return true;
                    }
                }

                // Telnet fallback if REST fails or returns unacknowledged
                try
                {
                    if (!_isTelnetAuthenticated)
                    {
                        await ConnectTelnetAsync(ct);
                    }
                }
                catch { }

                if (IsTelnetConnected)
                {
                    string telnetCmd = $"send {normMac} rs232:1 set rtp OFF";
                    string tResp = await SendTelnetCommandAsync(telnetCmd, ct);
                    AppLogger.Info("SdvoeClient", $"RS-232 'set rtp OFF' via Telnet response: {tResp}");
                    if (IsCommandSuccessful(tResp) || IsRestResponseSuccess(tResp))
                    {
                        return true;
                    }

                    // Secondary fallback without 'set' prefix
                    string altCmd = $"send {normMac} rs232:1 rtp OFF";
                    string altResp = await SendTelnetCommandAsync(altCmd, ct);
                    return IsCommandSuccessful(altResp) || IsRestResponseSuccess(altResp);
                }

                return false;
            }
            catch (Exception ex)
            {
                AppLogger.Debug("SdvoeClient", $"StopPreviewStreamViaAvpRs232Async exception for {mac}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Starts the Single-Link native Semtech BlueRiver stream (Stream 1 Scaler) via Telnet/REST.
        /// Sends 'set {normMac} stream HDMI {streamIndex} enable true address {multicastIp}' and 'auto_stop true'.
        /// </summary>
        public async Task<bool> StartSingleLinkStreamAsync(string macAddress, string multicastIp, int port = 5004, int streamIndex = 1, CancellationToken ct = default)
        {
            string normMac = NormalizeMac(macAddress);
            AppLogger.Info("SdvoeClient", $"Starting Single-Link native BlueRiver Stream {streamIndex} (Scaler) for {normMac} -> {multicastIp}:{port}");

            bool telnetOk = false;
            try
            {
                if (!_isTelnetAuthenticated) await ConnectTelnetAsync(ct);
                if (IsTelnetConnected)
                {
                    string cmdEnable = $"set {normMac} stream HDMI {streamIndex} enable true address {multicastIp}";
                    string resp1 = await SendTelnetCommandAsync(cmdEnable, ct);

                    string cmdAutoStop = $"set {normMac} stream HDMI {streamIndex} auto_stop true";
                    string resp2 = await SendTelnetCommandAsync(cmdAutoStop, ct);

                    telnetOk = IsCommandSuccessful(resp1) || IsRestResponseSuccess(resp1);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("SdvoeClient", $"Telnet StartSingleLinkStreamAsync failed on {normMac}: {ex.Message}");
            }

            if (telnetOk) return true;

            // REST fallback
            try
            {
                string path = $"/api/device/{normMac}";
                var payload = new
                {
                    op = "set",
                    streams = new[]
                    {
                        new
                        {
                            type = "HDMI",
                            index = streamIndex,
                            configuration = new
                            {
                                enable = true,
                                address = multicastIp,
                                auto_stop = true
                            }
                        }
                    }
                };
                using var resp = await PostDeviceRestAsync(path, payload, ct);
                if (resp != null && resp.IsSuccessStatusCode)
                {
                    string body = await resp.Content.ReadAsStringAsync(ct);
                    return IsRestResponseSuccess(body) || IsCommandSuccessful(body);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("SdvoeClient", $"REST StartSingleLinkStreamAsync failed on {normMac}", ex);
            }

            return false;
        }

        /// <summary>
        /// Stops the Single-Link native Semtech BlueRiver stream (Stream 1 and 0) with deterministic 0.0.0.0 address nullification.
        /// </summary>
        public async Task<bool> StopSingleLinkStreamAsync(string macAddress, int streamIndex = 1, CancellationToken ct = default)
        {
            string normMac = NormalizeMac(macAddress);
            AppLogger.Info("SdvoeClient", $"Stopping Single-Link native BlueRiver Stream for {normMac} with address 0.0.0.0");

            bool telnetOk = false;
            try
            {
                if (!_isTelnetAuthenticated) await ConnectTelnetAsync(ct);
                if (IsTelnetConnected)
                {
                    string cmd1 = $"set {normMac} stream HDMI {streamIndex} enable false address 0.0.0.0";
                    await SendTelnetCommandAsync(cmd1, ct);

                    string cmd0 = $"set {normMac} stream HDMI 0 enable false address 0.0.0.0";
                    await SendTelnetCommandAsync(cmd0, ct);

                    telnetOk = true;
                }
            }
            catch { }

            // REST fallback for 0.0.0.0 nullification
            try
            {
                string path = $"/api/device/{normMac}";
                var payload = new
                {
                    op = "set",
                    streams = new[]
                    {
                        new
                        {
                            type = "HDMI",
                            index = streamIndex,
                            configuration = new
                            {
                                enable = false,
                                address = "0.0.0.0"
                            }
                        }
                    }
                };
                await PostDeviceRestAsync(path, payload, ct);
            }
            catch { }

            return telnetOk;
        }

        /// <summary>
        /// Starts the preview multicast stream for the given MAC address on the specified multicast IP and port.
        /// Prioritizes AVAS-223 native AVP RS-232 tunneling (MCU UART 1: rtp ON) for Chip 0 across both Single-Link and Dual-Link modes.
        /// Uses native BlueRiver Stream 1 Scaler for Chip 1 or as fallback if MCU RS-232 is unavailable.
        /// </summary>
        public async Task<bool> StartPreviewStreamAsync(
            string macAddress,
            string multicastIp,
            int port,
            string linkMode = "UNKNOWN",
            int chipIndex = 0,
            CancellationToken ct = default)
        {
            string normMac = NormalizeMac(macAddress);

            // 1. For Chip 0 on any AVAS device, ALWAYS prioritize native MCU UART preview stream (rtp ON)
            if (chipIndex == 0)
            {
                if (await StartPreviewStreamViaAvpRs232Async(normMac, multicastIp, ct))
                {
                    AppLogger.Info("SdvoeClient", $"Started AVAS-223 AVP thumbnail stream via RS-232 for {normMac} -> {multicastIp}");
                    return true;
                }
            }

            // 2. For Chip 1 (Dual-Link secondary chip) or as fallback for Chip 0:
            if (chipIndex == 1 || string.Equals(linkMode, "SINGLE", StringComparison.OrdinalIgnoreCase))
            {
                if (await StartSingleLinkStreamAsync(normMac, multicastIp, port, streamIndex: 1, ct))
                {
                    return true;
                }
            }

            // 3. Fall back to standard Semtech BlueRiver thumbnail stream commands
            await ConfigureThumbnailStreamAsync(normMac, 1.0, 12345, port, ct);

            string cmd = $"start {normMac}:thumbnail:0 {multicastIp}";
            try
            {
                if (!_isTelnetAuthenticated) await ConnectTelnetAsync(ct);
                string resp = await SendTelnetCommandAsync(cmd, ct);
                if (IsCommandSuccessful(resp)) return true;
            }
            catch { }

            if (await StartPreviewStreamViaRestAsync(normMac, multicastIp, ct))
            {
                return true;
            }

            // Final fallback to native BlueRiver Stream 1 Scaler
            return await StartSingleLinkStreamAsync(normMac, multicastIp, port, streamIndex: 1, ct);
        }

        public Task<bool> StartPreviewStreamAsync(string macAddress, string multicastIp, int port, CancellationToken ct)
        {
            return StartPreviewStreamAsync(macAddress, multicastIp, port, "UNKNOWN", 0, ct);
        }



        /// <summary>
        /// Sends AVP silicon factory reset command ('factory <targetMac>') for the target chip.
        /// Strictly restricted to AVAS devices (Vendor ID 105).
        /// </summary>
        public async Task<bool> FactoryResetAvpAsync(AvasDevice device, string targetMac, CancellationToken ct = default)
        {
            if (device.Family != DeviceFamily.Avas)
            {
                throw new InvalidOperationException($"Hardware diagnostics and factory resets are restricted to AVAS devices. Device family: {device.Family}");
            }

            string normMac = NormalizeMac(targetMac);
            string cmd = $"factory {normMac}";
            AppLogger.Info("SdvoeClient", $"Issuing AVP factory reset to {normMac} (parent: {device.MacAddress})");

            // 1. First try Telnet
            try
            {
                if (!_isTelnetAuthenticated) await ConnectTelnetAsync(ct);
                if (IsTelnetConnected)
                {
                    string resp = await SendTelnetCommandAsync(cmd, ct);
                    if (IsCommandSuccessful(resp))
                    {
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("SdvoeClient", $"Telnet AVP factory reset failed on {normMac}: {ex.Message}");
            }

            // 2. Fallback to REST API if Telnet is disconnected or fails
            try
            {
                string path = $"/api/device/{normMac}";
                var payload = new { op = "factory_default" };
                using var resp = await PostDeviceRestAsync(path, payload, ct);
                if (resp != null && resp.IsSuccessStatusCode)
                {
                    string body = await resp.Content.ReadAsStringAsync(ct);
                    AppLogger.Info("SdvoeClient", $"AVP factory reset via REST for {normMac}: {body}");
                    if (IsRestResponseSuccess(body) || IsCommandSuccessful(body))
                    {
                        return true;
                    }
                }

                var altPayload = new { op = "factory" };
                using var altResp = await PostDeviceRestAsync(path, altPayload, ct);
                if (altResp != null && altResp.IsSuccessStatusCode)
                {
                    string body = await altResp.Content.ReadAsStringAsync(ct);
                    AppLogger.Info("SdvoeClient", $"AVP factory reset via REST (alt) for {normMac}: {body}");
                    return IsRestResponseSuccess(body) || IsCommandSuccessful(body);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("SdvoeClient", $"Error executing AVP factory reset via REST on {normMac}", ex);
            }

            return false;
        }

        /// <summary>
        /// Sends MCU factory reset command ('send <chip0Mac> rs232:1 set factory default\r') to restore MCU EEPROM.
        /// Strictly restricted to AVAS devices (Vendor ID 105) and always dispatched via Chip 0 MAC.
        /// </summary>
        public async Task<bool> FactoryResetMcuAsync(AvasDevice device, CancellationToken ct = default)
        {
            if (device.Family != DeviceFamily.Avas)
            {
                throw new InvalidOperationException($"Hardware diagnostics and factory resets are restricted to AVAS devices. Device family: {device.Family}");
            }

            string chip0Mac = NormalizeMac(device.MacAddress);
            AppLogger.Info("SdvoeClient", $"Issuing MCU factory reset to {chip0Mac}");

            try
            {
                string resp = await SendMcuCommandAsync(chip0Mac, "set factory default\r\n", ct);
                return IsCommandSuccessful(resp) || IsRestResponseSuccess(resp);
            }
            catch (Exception ex)
            {
                AppLogger.Error("SdvoeClient", $"Error executing MCU factory reset on {chip0Mac}", ex);
                return false;
            }
        }

        /// <summary>
        /// Sends BlueRiver reboot command ('reboot <targetMac>') to power-cycle the target AVP chip.
        /// Strictly restricted to AVAS devices (Vendor ID 105).
        /// </summary>
        public async Task<bool> RebootChipAsync(AvasDevice device, string targetMac, CancellationToken ct = default)
        {
            if (device.Family != DeviceFamily.Avas)
            {
                throw new InvalidOperationException($"Hardware diagnostics and factory resets are restricted to AVAS devices. Device family: {device.Family}");
            }

            string normMac = NormalizeMac(targetMac);
            string cmd = $"reboot {normMac}";
            AppLogger.Info("SdvoeClient", $"Issuing reboot command to {normMac}");

            // 1. First try Telnet
            try
            {
                if (!_isTelnetAuthenticated) await ConnectTelnetAsync(ct);
                if (IsTelnetConnected)
                {
                    string resp = await SendTelnetCommandAsync(cmd, ct);
                    if (IsCommandSuccessful(resp))
                    {
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("SdvoeClient", $"Telnet reboot failed on {normMac}: {ex.Message}");
            }

            // 2. Fallback to REST API if Telnet is disconnected or fails
            try
            {
                string path = $"/api/device/{normMac}";
                var payload = new { op = "reboot" };
                using var resp = await PostDeviceRestAsync(path, payload, ct);
                if (resp != null && resp.IsSuccessStatusCode)
                {
                    string body = await resp.Content.ReadAsStringAsync(ct);
                    AppLogger.Info("SdvoeClient", $"Reboot via REST for {normMac}: {body}");
                    return IsRestResponseSuccess(body) || IsCommandSuccessful(body);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("SdvoeClient", $"Error executing reboot via REST on {normMac}", ex);
            }

            return false;
        }

        /// <summary>
        /// Sends MCU soft reboot command ('send <chip0Mac> rs232:1 set system reboot\r') to initiate MCU watchdog reset.
        /// Strictly restricted to AVAS devices (Vendor ID 105) and always dispatched via Chip 0 MAC.
        /// </summary>
        public async Task<bool> RebootMcuAsync(AvasDevice device, CancellationToken ct = default)
        {
            if (device.Family != DeviceFamily.Avas)
            {
                throw new InvalidOperationException($"Hardware diagnostics and factory resets are restricted to AVAS devices. Device family: {device.Family}");
            }

            string chip0Mac = NormalizeMac(device.MacAddress);
            AppLogger.Info("SdvoeClient", $"Issuing MCU reboot to {chip0Mac}");

            try
            {
                string resp = await SendMcuCommandAsync(chip0Mac, "set system reboot\r\n", ct);
                return IsCommandSuccessful(resp) || IsRestResponseSuccess(resp);
            }
            catch (Exception ex)
            {
                AppLogger.Error("SdvoeClient", $"Error executing MCU reboot on {chip0Mac}", ex);
                return false;
            }
        }

        /// <summary>
        /// Verifies whether a Semtech Telnet response status is SUCCESS/PROCESSING and contains no device-level errors.
        /// </summary>
        public static bool IsCommandSuccessful(string resp)
        {
            if (string.IsNullOrWhiteSpace(resp)) return false;

            try
            {
                using var doc = JsonDocument.Parse(resp);
                if (doc.RootElement.TryGetProperty("status", out var statusProp))
                {
                    string status = statusProp.GetString() ?? "";
                    if (!status.Equals("SUCCESS", StringComparison.OrdinalIgnoreCase) &&
                        !status.Equals("PROCESSING", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }
                else
                {
                    return false;
                }

                if (doc.RootElement.TryGetProperty("result", out var resElem) &&
                    resElem.ValueKind == JsonValueKind.Object &&
                    resElem.TryGetProperty("error", out var errElem) &&
                    errElem.ValueKind == JsonValueKind.Array &&
                    errElem.GetArrayLength() > 0)
                {
                    string errMsg = errElem[0].TryGetProperty("message", out var msgProp) ? msgProp.GetString() ?? "" : "";
                    AppLogger.Warn("SdvoeClient", $"Server rejected operation: {errMsg}");
                    return false;
                }

                return true;
            }
            catch
            {
                return resp.Contains("\"status\":\"SUCCESS\"", StringComparison.OrdinalIgnoreCase) ||
                       resp.Contains("\"status\":\"PROCESSING\"", StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// Convenience overload matching default preview port (6792).
        /// </summary>
        public Task<bool> StartPreviewStreamAsync(string macAddress, string multicastIp, CancellationToken ct = default)
        {
            return StartPreviewStreamAsync(macAddress, multicastIp, _ipManager.BasePort, "UNKNOWN", 0, ct);
        }

        /// <summary>
        /// Stops the preview multicast stream for the given MAC address.
        /// If free is true, appends 'free' to release the IP on the server and deallocates from the local pool.
        /// Performs deterministic teardown:
        /// 1. Address 0.0.0.0 nullification on Stream 1 and Stream 0
        /// 2. AVAS-223 AVP RS-232 stop ('set rtp OFF')
        /// 3. BlueRiver thumbnail stop command
        /// </summary>
        public async Task<bool> StopPreviewStreamAsync(
            string macAddress,
            bool free,
            string linkMode = "UNKNOWN",
            int chipIndex = 0,
            CancellationToken ct = default)
        {
            string normMac = NormalizeMac(macAddress);

            // 1. Issue deterministic Stream 1 & Stream 0 nullification (address 0.0.0.0)
            bool singleStopped = await StopSingleLinkStreamAsync(normMac, streamIndex: 1, ct);

            // 2. Attempt AVAS-223 AVP RS-232 stop ('set rtp OFF')
            bool avpStopped = await StopPreviewStreamViaAvpRs232Async(normMac, ct);

            // 3. Also attempt Semtech BlueRiver thumbnail stop command
            string cmd = free ? $"stop {normMac}:thumbnail:0 free" : $"stop {normMac}:thumbnail:0";
            bool semtechSuccess = false;

            try
            {
                if (!_isTelnetAuthenticated) await ConnectTelnetAsync(ct);
                string resp = await SendTelnetCommandAsync(cmd, ct);
                if (IsCommandSuccessful(resp))
                {
                    semtechSuccess = true;
                }
            }
            catch
            {
                // Fallback to REST
                semtechSuccess = await StopPreviewStreamViaRestAsync(normMac, free, ct);
            }

            if (free)
            {
                _ipManager.ReleaseMulticastIp(normMac);
            }

            return singleStopped || avpStopped || semtechSuccess;
        }

        /// <summary>
        /// Stops the preview stream and frees the multicast IP by default per IMulticastController.
        /// </summary>
        public async Task<bool> StopPreviewStreamAsync(
            string macAddress,
            string linkMode = "UNKNOWN",
            int chipIndex = 0,
            CancellationToken ct = default)
        {
            return await StopPreviewStreamAsync(macAddress, free: true, linkMode, chipIndex, ct);
        }

        public Task<bool> StopPreviewStreamAsync(string macAddress, CancellationToken ct)
        {
            return StopPreviewStreamAsync(macAddress, free: true, "UNKNOWN", 0, ct);
        }

        /// <summary>
        /// Queries active multicast stream bindings from the SDVoE server and updates the local manager to avoid conflicts.
        /// </summary>
        public async Task<IReadOnlyList<string>> QueryActiveMulticastAsync(CancellationToken ct = default)
        {
            IReadOnlyList<string> activeIps;
            try
            {
                if (!_isTelnetAuthenticated) await ConnectTelnetAsync(ct);
                string resp = await SendTelnetCommandAsync("list multicast", ct);
                activeIps = ParseMulticastListJson(resp);
            }
            catch
            {
                activeIps = await GetMulticastListViaRestAsync(ct);
            }

            foreach (var ip in activeIps)
            {
                _ipManager.RegisterInUse(ip, "active_stream");
            }

            return activeIps;
        }

        #endregion

        #region REST Endpoints

        private async Task<bool> ConfigureThumbnailViaRestAsync(string mac, double fps, uint ssrc, int udpPort, CancellationToken ct)
        {
            try
            {
                string url = $"http://{_serverIp}:{_restPort}/api/device/{mac}";
                var payload = new
                {
                    op = "set:thumbnail",
                    fps = fps,
                    ssrc = ssrc,
                    udp = udpPort
                };
                using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                using var resp = await _httpClient.PostAsync(url, content, ct);
                if (!resp.IsSuccessStatusCode) return false;

                string body = await resp.Content.ReadAsStringAsync(ct);
                return body.Contains("\"status\":\"SUCCESS\"", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private async Task<bool> StartPreviewStreamViaRestAsync(string mac, string multicastIp, CancellationToken ct)
        {
            try
            {
                string url = $"http://{_serverIp}:{_restPort}/api/device/{mac}";
                var payload = new
                {
                    op = "start",
                    stream_type = "THUMBNAIL",
                    stream_index = 0,
                    dest_address = multicastIp
                };
                using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                using var resp = await _httpClient.PostAsync(url, content, ct);
                if (!resp.IsSuccessStatusCode) return false;

                string body = await resp.Content.ReadAsStringAsync(ct);
                return body.Contains("\"status\":\"SUCCESS\"", StringComparison.OrdinalIgnoreCase) ||
                       body.Contains("\"status\":\"PROCESSING\"", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private async Task<bool> StopPreviewStreamViaRestAsync(string mac, bool free, CancellationToken ct)
        {
            try
            {
                string url = $"http://{_serverIp}:{_restPort}/api/device/{mac}";
                var payload = new
                {
                    op = "stop",
                    stream_type = "THUMBNAIL",
                    stream_index = 0,
                    free = free
                };
                using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                using var resp = await _httpClient.PostAsync(url, content, ct);
                if (!resp.IsSuccessStatusCode) return false;

                string body = await resp.Content.ReadAsStringAsync(ct);
                return body.Contains("\"status\":\"SUCCESS\"", StringComparison.OrdinalIgnoreCase) ||
                       body.Contains("\"status\":\"PROCESSING\"", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public async Task<IReadOnlyList<string>> GetMulticastListViaRestAsync(CancellationToken ct = default)
        {
            try
            {
                string url = $"http://{_serverIp}:{_restPort}/api/multicast";
                using var resp = await _httpClient.GetAsync(url, ct);
                resp.EnsureSuccessStatusCode();
                string json = await resp.Content.ReadAsStringAsync(ct);
                return ParseMulticastListJson(json);
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        #endregion

        #region JSON Parsing Helpers

        /// <summary>
        /// Parses device list from Semtech BlueRiver JSON envelope.
        /// </summary>
        public static IReadOnlyList<AvasDevice> ParseDevicesJson(string json)
        {
            var list = new List<AvasDevice>();
            if (string.IsNullOrWhiteSpace(json)) return list;

            try
            {
                using var doc = JsonDocument.Parse(json);
                JsonElement devicesElem;

                if (doc.RootElement.TryGetProperty("result", out var resultElem) &&
                    resultElem.TryGetProperty("devices", out var resDevices))
                {
                    devicesElem = resDevices;
                }
                else if (doc.RootElement.TryGetProperty("devices", out var rootDevices))
                {
                    devicesElem = rootDevices;
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
                    string mac = d.TryGetProperty("device_id", out var macProp) ? macProp.GetString() ?? "" : "";
                    string name = d.TryGetProperty("device_name", out var nameProp) ? nameProp.GetString() ?? "" : "";

                    int vid = 0, pid = 0, chip = 0;
                    bool isTx = false, isRx = false;

                    if (d.TryGetProperty("identity", out var idElem))
                    {
                        if (idElem.TryGetProperty("vendor_id", out var vidProp)) vid = vidProp.GetInt32();
                        if (idElem.TryGetProperty("product_id", out var pidProp)) pid = pidProp.GetInt32();
                        if (idElem.TryGetProperty("chip_id", out var chipProp)) chip = chipProp.GetInt32();
                        if (idElem.TryGetProperty("is_transmitter", out var txProp)) isTx = txProp.GetBoolean();
                        if (idElem.TryGetProperty("is_receiver", out var rxProp)) isRx = rxProp.GetBoolean();
                    }

                    string ip = "";
                    if (d.TryGetProperty("nodes", out var nodesElem))
                    {
                        foreach (var node in nodesElem.EnumerateArray())
                        {
                            if (node.TryGetProperty("status", out var nodeStatus) &&
                                nodeStatus.TryGetProperty("ip", out var ipObj) &&
                                ipObj.TryGetProperty("address", out var ipAddr))
                            {
                                ip = ipAddr.GetString() ?? "";
                                break;
                            }
                        }
                    }

                    list.Add(new AvasDevice
                    {
                        MacAddress = mac,
                        DeviceName = name,
                        IpAddress = ip,
                        VendorId = vid,
                        ProductId = pid,
                        ChipIndex = chip,
                        IsTransmitter = isTx,
                        IsReceiver = isRx,
                        Model = "AVAS-223",
                        Resolution = "320x180"
                    });
                }
            }
            catch
            {
                // Graceful parsing error handling
            }

            return list;
        }

        /// <summary>
        /// Parses active multicast IP addresses from 'list multicast' or GET /api/multicast JSON response.
        /// </summary>
        public static IReadOnlyList<string> ParseMulticastListJson(string json)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(json)) return list;

            try
            {
                using var doc = JsonDocument.Parse(json);
                JsonElement multicastElem;

                if (doc.RootElement.TryGetProperty("result", out var resultElem) &&
                    resultElem.TryGetProperty("multicast", out var resMulticast))
                {
                    multicastElem = resMulticast;
                }
                else if (doc.RootElement.TryGetProperty("multicast", out var rootMulticast))
                {
                    multicastElem = rootMulticast;
                }
                else
                {
                    return list;
                }

                foreach (var item in multicastElem.EnumerateArray())
                {
                    if (item.TryGetProperty("address", out var addrProp))
                    {
                        string? addr = addrProp.GetString();
                        if (!string.IsNullOrWhiteSpace(addr))
                        {
                            list.Add(addr);
                        }
                    }
                }
            }
            catch
            {
            }

            return list;
        }

        #endregion

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            DisconnectTelnet();
            _httpClient.Dispose();
            _telnetLock.Dispose();
        }
    }
}
