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
                    config?.MulticastStartIp ?? "224.1.1.1",
                    config?.MulticastEndIp ?? "224.1.3.225",
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

                AppLogger.Debug("Telnet", $"TX >> {command}");
                await _telnetWriter.WriteLineAsync(command);
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
        /// Backward-compatible overload for discovering AVAS-223 chip_0 encoders.
        /// </summary>
        public Task<IReadOnlyList<AvasDevice>> DiscoverAvas223DevicesAsync(CancellationToken ct) 
            => DiscoverAvas223DevicesAsync(false, ct);

        /// <summary>
        /// Discovers all devices on the network and filters strictly for AVAS-223 chip_0 encoders
        /// (and optionally Advantech VEGA-1100 transceivers when enableVegaDevices is true).
        /// Fires DevicesDiscovered event on completion.
        /// </summary>
        public async Task<IReadOnlyList<AvasDevice>> DiscoverAvas223DevicesAsync(bool enableVegaDevices = false, CancellationToken ct = default)
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

            var filtered = SdvoeDiscoveryFilter.FilterTargetDevices(allDevices, enableVegaDevices);

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
            var portsToTry = new[] { _restPort, 8090, 8080, 80 }.Distinct().ToArray();
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

                // Telnet fallback if REST fails
                if (IsTelnetConnected)
                {
                    string telnetCmd = $"send {normMac} rs232 1 \"{command.Trim()}\"";
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
            }
            catch (Exception ex)
            {
                AppLogger.Warn("SdvoeClient", $"QueryMcuDeviceInfoAsync error for {chip0Mac}: {ex.Message}");
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
        /// Ensures UART 1 API mode, sends 'set rtp igmp <multicastIp>' then 'set rtp ON'.
        /// Note: The hardware streams directly to UDP port 5000 without requiring a port parameter.
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
                var setPayload = new
                {
                    op = "send:rs232",
                    port_index = 1,
                    data_string = $"set rtp igmp {multicastIp}\r\n"
                };
                using var setResp = await PostDeviceRestAsync(path, setPayload, ct);
                if (setResp == null || !setResp.IsSuccessStatusCode)
                {
                    AppLogger.Warn("SdvoeClient", $"RS-232 'set rtp igmp' HTTP request failed or timed out for {normMac}");
                    return false;
                }

                string setBody = await setResp.Content.ReadAsStringAsync(ct);
                AppLogger.Info("SdvoeClient", $"RS-232 'set rtp igmp' response: {setBody}");
                if (!IsRestResponseSuccess(setBody))
                {
                    return false;
                }

                // Step 2: Turn on RTP streaming
                var onPayload = new
                {
                    op = "send:rs232",
                    port_index = 1,
                    data_string = "set rtp ON\r\n"
                };
                using var onResp = await PostDeviceRestAsync(path, onPayload, ct);
                if (onResp == null || !onResp.IsSuccessStatusCode)
                {
                    AppLogger.Warn("SdvoeClient", $"RS-232 'set rtp ON' HTTP request failed or timed out for {normMac}");
                    return false;
                }

                string onBody = await onResp.Content.ReadAsStringAsync(ct);
                AppLogger.Info("SdvoeClient", $"RS-232 'set rtp ON' response: {onBody}");
                return IsRestResponseSuccess(onBody);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("SdvoeClient", $"StartPreviewStreamViaAvpRs232Async exception for {mac}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Stops AVAS-223 AVP thumbnail streaming via RS-232 tunneling (MCU port index 1).
        /// Sends 'set rtp OFF'.
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
                if (IsTelnetConnected)
                {
                    string telnetCmd = $"send {normMac} rs232 1 \"set rtp OFF\"";
                    string tResp = await SendTelnetCommandAsync(telnetCmd, ct);
                    AppLogger.Info("SdvoeClient", $"RS-232 'set rtp OFF' via Telnet response: {tResp}");
                    return IsCommandSuccessful(tResp);
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
        /// Starts the preview multicast stream for the given MAC address on the specified multicast IP and port.
        /// Prioritizes AVAS-223 AVP RS-232 commands with fallback to standard Semtech BlueRiver thumbnail commands.
        /// </summary>
        public async Task<bool> StartPreviewStreamAsync(
            string macAddress,
            string multicastIp,
            int port,
            CancellationToken ct = default)
        {
            // 1. Prioritize AVAS-223 native AVP RS-232 tunneling (portless multicast configuration)
            if (await StartPreviewStreamViaAvpRs232Async(macAddress, multicastIp, ct))
            {
                AppLogger.Info("SdvoeClient", $"Started AVAS-223 AVP thumbnail stream via RS-232 for {macAddress} -> {multicastIp}");
                return true;
            }

            // 2. Fall back to standard Semtech BlueRiver thumbnail stream commands
            await ConfigureThumbnailStreamAsync(macAddress, 1.0, 12345, port, ct);

            string cmd = $"start {macAddress}:thumbnail:0 {multicastIp}";
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
                // Fallback to REST
            }

            return await StartPreviewStreamViaRestAsync(macAddress, multicastIp, ct);
        }

        /// <summary>
        /// Starts preview streaming for Advantech VEGA-1100 transceivers using standard BlueRiver SDVoE thumbnail commands.
        /// Bypasses CYP MCU UART (set rtp igmp).
        /// </summary>
        public async Task<bool> StartVegaPreviewStreamAsync(
            string macAddress,
            string multicastIp,
            int port,
            CancellationToken ct = default)
        {
            try
            {
                string normMac = NormalizeMac(macAddress);
                AppLogger.Info("SdvoeClient", $"Configuring standard SDVoE thumbnail for VEGA-1100: {normMac} -> {multicastIp}:{port}");
                
                await ConfigureThumbnailStreamAsync(normMac, 20.0, 100, port, ct);

                string cmd = $"start {normMac}:thumbnail:0 {multicastIp}";
                try
                {
                    if (!_isTelnetAuthenticated) await ConnectTelnetAsync(ct);
                    string resp = await SendTelnetCommandAsync(cmd, ct);
                    if (IsCommandSuccessful(resp))
                    {
                        AppLogger.Info("SdvoeClient", $"Successfully started VEGA-1100 thumbnail stream via Telnet: {normMac}");
                        return true;
                    }
                }
                catch
                {
                    // Fallback to REST
                }

                return await StartPreviewStreamViaRestAsync(normMac, multicastIp, ct);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("SdvoeClient", $"StartVegaPreviewStreamAsync exception for {macAddress}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Stops preview streaming for Advantech VEGA-1100 transceivers using standard BlueRiver thumbnail stop commands.
        /// Bypasses CYP MCU UART.
        /// </summary>
        public async Task<bool> StopVegaPreviewStreamAsync(string macAddress, CancellationToken ct = default)
        {
            try
            {
                string normMac = NormalizeMac(macAddress);
                string cmd = $"stop {normMac}:thumbnail:0 free";
                bool success = false;

                try
                {
                    if (!_isTelnetAuthenticated) await ConnectTelnetAsync(ct);
                    string resp = await SendTelnetCommandAsync(cmd, ct);
                    if (IsCommandSuccessful(resp))
                    {
                        success = true;
                    }
                }
                catch
                {
                    success = await StopPreviewStreamViaRestAsync(normMac, true, ct);
                }

                _ipManager.ReleaseMulticastIp(normMac);
                return success;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("SdvoeClient", $"StopVegaPreviewStreamAsync exception for {macAddress}: {ex.Message}");
                return false;
            }
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

            try
            {
                if (!_isTelnetAuthenticated) await ConnectTelnetAsync(ct);
                string resp = await SendTelnetCommandAsync(cmd, ct);
                return IsCommandSuccessful(resp);
            }
            catch (Exception ex)
            {
                AppLogger.Error("SdvoeClient", $"Error executing AVP factory reset on {normMac}", ex);
                return false;
            }
        }

        /// <summary>
        /// Sends MCU factory reset command ('send <chip0Mac> rs232:1 set factory default') to restore MCU EEPROM.
        /// Strictly restricted to AVAS devices (Vendor ID 105) and always dispatched via Chip 0 MAC.
        /// </summary>
        public async Task<bool> FactoryResetMcuAsync(AvasDevice device, CancellationToken ct = default)
        {
            if (device.Family != DeviceFamily.Avas)
            {
                throw new InvalidOperationException($"Hardware diagnostics and factory resets are restricted to AVAS devices. Device family: {device.Family}");
            }

            string chip0Mac = NormalizeMac(device.MacAddress);
            string cmd = $"send {chip0Mac} rs232:1 set factory default";
            AppLogger.Info("SdvoeClient", $"Issuing MCU factory reset to {chip0Mac}");

            try
            {
                if (!_isTelnetAuthenticated) await ConnectTelnetAsync(ct);
                string resp = await SendTelnetCommandAsync(cmd, ct);
                return IsCommandSuccessful(resp);
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

            try
            {
                if (!_isTelnetAuthenticated) await ConnectTelnetAsync(ct);
                string resp = await SendTelnetCommandAsync(cmd, ct);
                return IsCommandSuccessful(resp);
            }
            catch (Exception ex)
            {
                AppLogger.Error("SdvoeClient", $"Error executing reboot on {normMac}", ex);
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
            return StartPreviewStreamAsync(macAddress, multicastIp, _ipManager.BasePort, ct);
        }

        /// <summary>
        /// Stops the preview multicast stream for the given MAC address.
        /// If free is true, appends 'free' to release the IP on the server and deallocates from the local pool.
        /// </summary>
        public async Task<bool> StopPreviewStreamAsync(string macAddress, bool free, CancellationToken ct = default)
        {
            string normMac = NormalizeMac(macAddress);

            // 1. Attempt AVAS-223 AVP RS-232 stop ('set rtp OFF')
            bool avpStopped = await StopPreviewStreamViaAvpRs232Async(normMac, ct);

            // 2. Also attempt Semtech BlueRiver thumbnail stop command
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

            return avpStopped || semtechSuccess;
        }

        /// <summary>
        /// Stops the preview stream and frees the multicast IP by default per IMulticastController.
        /// </summary>
        public Task<bool> StopPreviewStreamAsync(string macAddress, CancellationToken ct = default)
        {
            return StopPreviewStreamAsync(macAddress, free: true, ct);
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
