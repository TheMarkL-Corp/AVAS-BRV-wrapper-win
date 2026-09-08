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
        private readonly int _restPort;
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

                _telnetClient = new TcpClient();
                await _telnetClient.ConnectAsync(_serverIp, _telnetPort, ct);
                var stream = _telnetClient.GetStream();
                _telnetReader = new StreamReader(stream, Encoding.ASCII);
                _telnetWriter = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true, NewLine = "\r\n" };

                // Mandatory Semtech Handshake: require api 3.0.0.0
                await _telnetWriter.WriteLineAsync("require api 3.0.0.0");
                string? response = await _telnetReader.ReadLineAsync(ct);

                if (response != null && response.Contains("\"status\":\"SUCCESS\"", StringComparison.OrdinalIgnoreCase))
                {
                    _isTelnetAuthenticated = true;
                    return true;
                }

                _isTelnetAuthenticated = false;
                return false;
            }
            catch
            {
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

                await _telnetWriter.WriteLineAsync(command);
                string? line = await _telnetReader.ReadLineAsync(ct);
                return line ?? string.Empty;
            }
            catch (Exception)
            {
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
                if (resp.Contains("\"status\":\"SUCCESS\"", StringComparison.OrdinalIgnoreCase))
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
        public async Task<bool> StartPreviewStreamAsync(
            string macAddress,
            string multicastIp,
            int port,
            CancellationToken ct = default)
        {
            // Configure thumbnail stream parameters first
            await ConfigureThumbnailStreamAsync(macAddress, 1.0, 12345, port, ct);

            string cmd = $"start {macAddress}:thumbnail:0 {multicastIp}";
            try
            {
                if (!_isTelnetAuthenticated) await ConnectTelnetAsync(ct);
                string resp = await SendTelnetCommandAsync(cmd, ct);
                if (resp.Contains("\"status\":\"SUCCESS\"", StringComparison.OrdinalIgnoreCase) ||
                    resp.Contains("\"status\":\"PROCESSING\"", StringComparison.OrdinalIgnoreCase))
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
            string cmd = free ? $"stop {macAddress}:thumbnail:0 free" : $"stop {macAddress}:thumbnail:0";
            bool success = false;

            try
            {
                if (!_isTelnetAuthenticated) await ConnectTelnetAsync(ct);
                string resp = await SendTelnetCommandAsync(cmd, ct);
                if (resp.Contains("\"status\":\"SUCCESS\"", StringComparison.OrdinalIgnoreCase) ||
                    resp.Contains("\"status\":\"PROCESSING\"", StringComparison.OrdinalIgnoreCase))
                {
                    success = true;
                }
            }
            catch
            {
                // Fallback to REST
                success = await StopPreviewStreamViaRestAsync(macAddress, free, ct);
            }

            if (free)
            {
                _ipManager.ReleaseMulticastIp(macAddress);
            }

            return success;
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
