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
        public bool IsTelnetConnected => _telnetClient != null && _telnetClient.Connected;
        public bool IsTelnetAuthenticated => _isTelnetAuthenticated && IsTelnetConnected;

        public event Action<IReadOnlyList<AvasDevice>>? DevicesDiscovered;

        public SdvoeClient(
            string serverIp = "127.0.0.1",
            int telnetPort = 6970,
            int restPort = 8080,
            MulticastIpManager? ipManager = null)
        {
            _serverIp = string.IsNullOrWhiteSpace(serverIp) ? "127.0.0.1" : serverIp;
            _telnetPort = telnetPort > 0 ? telnetPort : 6970;
            _restPort = restPort > 0 ? restPort : 8080;
            _ipManager = ipManager ?? new MulticastIpManager();
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        }

        public SdvoeClient(AppConfig config, MulticastIpManager? ipManager = null)
            : this(
                config?.ControlServerIp ?? "127.0.0.1",
                config?.TelnetPort ?? 6970,
                config?.RestPort ?? 8080,
                ipManager ?? new MulticastIpManager(
                    config?.MulticastStartIp ?? "224.1.1.1",
                    config?.MulticastEndIp ?? "224.1.3.225",
                    config?.BasePort ?? 6792))
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
        /// Discovers all devices on the network and filters strictly for AVAS-223 chip_0 encoders
        /// (Vendor ID 105, Product ID 81, IsTransmitter == true, ChipIndex == 0).
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
        /// Starts AVAS-223 AVP thumbnail streaming via RS-232 tunneling (MCU port index 1).
        /// Sends 'set rtp igmp <multicastIp>' then 'set rtp ON'.
        /// Note: The hardware streams directly to UDP port 5000 without requiring a port parameter.
        /// </summary>
        public async Task<bool> StartPreviewStreamViaAvpRs232Async(string mac, string multicastIp, CancellationToken ct = default)
        {
            try
            {
                string normMac = NormalizeMac(mac);
                string path = $"/api/device/{normMac}";

                // Step 1: Set multicast IP address for RTP stream
                var setPayload = new
                {
                    op = "send:rs232",
                    port_index = 1,
                    data_string = $"set rtp igmp {multicastIp}\r\n"
                };
                var setResp = await PostDeviceRestAsync(path, setPayload, ct);
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
                var onResp = await PostDeviceRestAsync(path, onPayload, ct);
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
                var resp = await PostDeviceRestAsync(path, offPayload, ct);
                if (resp == null || !resp.IsSuccessStatusCode) return false;

                string body = await resp.Content.ReadAsStringAsync(ct);
                AppLogger.Info("SdvoeClient", $"RS-232 'set rtp OFF' response: {body}");
                return IsRestResponseSuccess(body);
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
            // 1. Attempt AVAS-223 AVP RS-232 stop ('set rtp OFF')
            bool avpStopped = await StopPreviewStreamViaAvpRs232Async(macAddress, ct);

            // 2. Also attempt Semtech BlueRiver thumbnail stop command
            string cmd = free ? $"stop {macAddress}:thumbnail:0 free" : $"stop {macAddress}:thumbnail:0";
            bool semtechSuccess = false;

            try
            {
                if (!_isTelnetAuthenticated) await ConnectTelnetAsync(ct);
                string resp = await SendTelnetCommandAsync(cmd, ct);
                if (resp.Contains("\"status\":\"SUCCESS\"", StringComparison.OrdinalIgnoreCase) ||
                    resp.Contains("\"status\":\"PROCESSING\"", StringComparison.OrdinalIgnoreCase))
                {
                    semtechSuccess = true;
                }
            }
            catch
            {
                // Fallback to REST
                semtechSuccess = await StopPreviewStreamViaRestAsync(macAddress, free, ct);
            }

            if (free)
            {
                _ipManager.ReleaseMulticastIp(macAddress);
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
                var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                var resp = await _httpClient.PostAsync(url, content, ct);
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
                var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                var resp = await _httpClient.PostAsync(url, content, ct);
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
                var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                var resp = await _httpClient.PostAsync(url, content, ct);
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
                var resp = await _httpClient.GetAsync(url, ct);
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
